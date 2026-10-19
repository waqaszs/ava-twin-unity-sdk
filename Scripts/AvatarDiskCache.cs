using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// Persistent on-device GLB cache, shared by SDK.LoadAvatar and CharacterLoader.
    ///
    /// Entries are keyed by the Ava-Twin storage object the server resolved
    /// (scheme + host + path, without the signed-URL token). Published storage
    /// objects are never overwritten, so an entry never goes stale. The server is
    /// still asked to resolve every load, so authentication, allowlists, caps and
    /// plan fallback keep applying; only the GLB download is saved.
    ///
    /// Each entry is a GLB file plus a JSON sidecar (format version, asset key,
    /// byte length, SHA-256). The sidecar is written last and acts as the commit
    /// marker, so a partial write is never read back. Entries are validated on
    /// every read; an invalid entry is deleted and treated as a miss. The cache is
    /// best-effort: failures are logged as warnings and never break a load.
    /// </summary>
    internal static class AvatarDiskCache
    {
        // ── Layout constants ────────────────────────────────────────────
        private const string CacheRootFolder = "AvaTwinCache";
        private const string CacheAvatarsFolder = "Avatars";
        private const string GlbExtension = ".glb";
        private const string SidecarExtension = ".json";
        private const string TempExtension = ".tmp";
        private const string TrashSuffix = ".deleting-";
        private const int FormatVersion = 1;

        // ── Cacheable storage objects (immutable once published) ────────
        private const string SignedObjectPrefix = "/storage/v1/object/sign/";
        private const string PublicObjectPrefix = "/storage/v1/object/public/";
        private const string ModelsBucketPrefix = "models/";

        // ── GLB header constants ────────────────────────────────────────
        private const uint GlbMagic = 0x46546C67; // "glTF", little-endian
        private const int GlbHeaderLength = 12;

        private static readonly object DiskLock = new();
        private static int _generation;

        /// <summary>Overrides the cache directory (tests only). Null uses persistentDataPath.</summary>
        internal static string DirectoryOverride;

        [Serializable]
        private class Sidecar
        {
            public int v;
            public string key;
            public long length;
            public string sha256;
        }

        /// <summary>Claims of a Supabase Storage signed-URL token.</summary>
        [Serializable]
        private class SignedUrlClaims
        {
            public string url;
            public long exp;
        }

        /// <summary>Changes on every <see cref="Clear"/>; work started earlier must not repopulate the cache.</summary>
        internal static int Generation => System.Threading.Volatile.Read(ref _generation);

        // ── Keys ────────────────────────────────────────────────────────

        /// <summary>True for an Ava-Twin model storage object (immutable once published).</summary>
        internal static bool IsAvaTwinStorageUrl(string url)
        {
            return TryParseStorageUrl(url, out _, out _, out _);
        }

        /// <summary>
        /// Cache key for a GLB URL, or null when the persistent cache must not be used:
        /// the URL is not an Ava-Twin model object, or it is a signed URL whose token has
        /// expired or does not name this object. An unexpired token proves the server
        /// authorized this exact object recently, so reusing the cached bytes is safe.
        /// </summary>
        internal static string AssetKeyFromUrl(string url)
        {
            if (!TryParseStorageUrl(url, out var uri, out var objectPath, out var signed)) return null;
            if (signed && !HasFreshToken(uri, objectPath)) return null;
            return $"{uri.Scheme}://{uri.Authority.ToLowerInvariant()}{uri.AbsolutePath}";
        }

        private static bool TryParseStorageUrl(string url, out Uri uri, out string objectPath, out bool signed)
        {
            objectPath = null;
            signed = false;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                uri = null;
                return false;
            }
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;

            var path = uri.AbsolutePath;
            if (path.StartsWith(SignedObjectPrefix, StringComparison.Ordinal))
            {
                signed = true;
                objectPath = Uri.UnescapeDataString(path.Substring(SignedObjectPrefix.Length));
            }
            else if (path.StartsWith(PublicObjectPrefix, StringComparison.Ordinal))
            {
                objectPath = Uri.UnescapeDataString(path.Substring(PublicObjectPrefix.Length));
            }
            else
            {
                return false;
            }
            return objectPath.StartsWith(ModelsBucketPrefix, StringComparison.Ordinal);
        }

        private static bool HasFreshToken(Uri uri, string objectPath)
        {
            try
            {
                var token = GetQueryParameter(uri.Query, "token");
                if (string.IsNullOrEmpty(token)) return false;
                var parts = token.Split('.');
                if (parts.Length != 3) return false;

                var claims = JsonUtility.FromJson<SignedUrlClaims>(
                    Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
                if (claims == null || !string.Equals(claims.url, objectPath, StringComparison.Ordinal))
                    return false;
                return claims.exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            catch
            {
                return false;
            }
        }

        private static string GetQueryParameter(string query, string name)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name)
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        private static byte[] Base64UrlDecode(string value)
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
        }

        /// <summary>Must be called on the main thread (reads Application.persistentDataPath).</summary>
        internal static string GetDirectory()
        {
            return DirectoryOverride
                ?? Path.Combine(Application.persistentDataPath, CacheRootFolder, CacheAvatarsFolder);
        }

        internal static void GetEntryPaths(string dir, string assetKey, out string glbPath, out string sidecarPath)
        {
            var name = Sha256Hex(Encoding.UTF8.GetBytes($"v{FormatVersion}:{assetKey}"));
            glbPath = Path.Combine(dir, name + GlbExtension);
            sidecarPath = Path.Combine(dir, name + SidecarExtension);
        }

        // ── Async entry points (call from the main thread) ──────────────
        // Off the main thread everywhere except WebGL, which has no threads.

        /// <summary>Validated bytes for <paramref name="assetKey"/>, or null on a miss.</summary>
        internal static Task<byte[]> TryLoadAsync(string assetKey)
        {
            if (string.IsNullOrEmpty(assetKey)) return Task.FromResult<byte[]>(null);
            var dir = GetDirectory();
#if UNITY_WEBGL && !UNITY_EDITOR
            var bytes = TryLoad(dir, assetKey);
            if (bytes != null) SyncPersistentStorage(); // keep LRU recency across reloads
            return Task.FromResult(bytes);
#else
            return Task.Run(() => TryLoad(dir, assetKey));
#endif
        }

        /// <summary>
        /// Persist a GLB that has already imported successfully, then evict
        /// least-recently-used entries above <paramref name="maxBytes"/> (0 or less = no cap).
        /// </summary>
        internal static Task SaveAsync(string assetKey, byte[] bytes, long maxBytes, int generation)
        {
            if (string.IsNullOrEmpty(assetKey)) return Task.CompletedTask;
            var dir = GetDirectory();
#if UNITY_WEBGL && !UNITY_EDITOR
            Save(dir, assetKey, bytes, maxBytes, generation);
            SyncPersistentStorage();
            return Task.CompletedTask;
#else
            return Task.Run(() => Save(dir, assetKey, bytes, maxBytes, generation));
#endif
        }

        /// <summary>
        /// Delete every persisted avatar. The folder is renamed at once and deleted in
        /// the background, so the caller never waits on a recursive delete.
        /// </summary>
        internal static void Clear()
        {
            var dir = GetDirectory();
            string trash = null;
            lock (DiskLock)
            {
                System.Threading.Interlocked.Increment(ref _generation);
                try
                {
                    if (Directory.Exists(dir))
                    {
                        trash = dir + TrashSuffix + Guid.NewGuid().ToString("N");
                        Directory.Move(dir, trash);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Ava-Twin] Could not clear the avatar cache: {ex.Message}");
                }
            }

            if (trash != null)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                TryDeleteDirectory(trash);
#else
                Task.Run(() => TryDeleteDirectory(trash));
#endif
            }

            SyncPersistentStorage();
        }

        /// <summary>Total bytes of persisted GLB files.</summary>
        internal static long GetSizeBytes()
        {
            var dir = GetDirectory();
            lock (DiskLock)
            {
                if (!Directory.Exists(dir)) return 0;
                long total = 0;
                foreach (var file in new DirectoryInfo(dir).GetFiles("*" + GlbExtension))
                    total += file.Length;
                return total;
            }
        }

        // ── Synchronous core (any thread) ───────────────────────────────

        internal static byte[] TryLoad(string dir, string assetKey)
        {
            GetEntryPaths(dir, assetKey, out var glbPath, out var sidecarPath);

            lock (DiskLock)
            {
                var glbExists = File.Exists(glbPath);
                var sidecarExists = File.Exists(sidecarPath);
                if (!glbExists && !sidecarExists) return null;

                byte[] data;
                try
                {
                    if (!glbExists || !sidecarExists)
                    {
                        // Interrupted write: one half of the entry is missing.
                        DeleteEntry(glbPath, sidecarPath);
                        return null;
                    }

                    var sidecar = JsonUtility.FromJson<Sidecar>(File.ReadAllText(sidecarPath));
                    data = File.ReadAllBytes(glbPath);

                    if (sidecar == null
                        || sidecar.v != FormatVersion
                        || !string.Equals(sidecar.key, assetKey, StringComparison.Ordinal)
                        || data.LongLength != sidecar.length
                        || !IsGlb(data)
                        || !string.Equals(Sha256Hex(data), sidecar.sha256, StringComparison.Ordinal))
                    {
                        Debug.LogWarning("[Ava-Twin] Discarding an invalid cached avatar. It will be downloaded again.");
                        DeleteEntry(glbPath, sidecarPath);
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Ava-Twin] Could not read a cached avatar: {ex.Message}. It will be downloaded again.");
                    DeleteEntry(glbPath, sidecarPath);
                    return null;
                }

                try
                {
                    // Least-recently-used bookkeeping for eviction.
                    File.SetLastWriteTimeUtc(glbPath, DateTime.UtcNow);
                }
                catch
                {
                    // Recency is best-effort; the entry itself is valid.
                }

                return data;
            }
        }

        internal static void Save(string dir, string assetKey, byte[] bytes, long maxBytes, int generation)
        {
            if (string.IsNullOrEmpty(assetKey) || bytes == null || !IsGlb(bytes)) return;
            if (maxBytes > 0 && bytes.LongLength > maxBytes) return;

            GetEntryPaths(dir, assetKey, out var glbPath, out var sidecarPath);

            lock (DiskLock)
            {
                // The cache was cleared after this load started.
                if (generation != _generation) return;

                try
                {
                    Directory.CreateDirectory(dir);

                    // The sidecar is the commit marker: remove it first, write it last.
                    if (File.Exists(sidecarPath)) File.Delete(sidecarPath);

                    var glbTemp = glbPath + TempExtension;
                    File.WriteAllBytes(glbTemp, bytes);
                    if (File.Exists(glbPath)) File.Delete(glbPath);
                    File.Move(glbTemp, glbPath);

                    var sidecar = new Sidecar
                    {
                        v = FormatVersion,
                        key = assetKey,
                        length = bytes.LongLength,
                        sha256 = Sha256Hex(bytes),
                    };
                    var sidecarTemp = sidecarPath + TempExtension;
                    File.WriteAllText(sidecarTemp, JsonUtility.ToJson(sidecar));
                    File.Move(sidecarTemp, sidecarPath);

                    Evict(dir, maxBytes, glbPath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Ava-Twin] Could not cache an avatar on disk: {ex.Message}");
                    DeleteEntry(glbPath, sidecarPath);
                }
            }
        }

        internal static bool IsGlb(byte[] bytes)
        {
            if (bytes == null || bytes.Length < GlbHeaderLength) return false;
            if (BitConverter.ToUInt32(bytes, 0) != GlbMagic) return false;
            return BitConverter.ToUInt32(bytes, 8) == (uint)bytes.Length;
        }

        private static void Evict(string dirPath, long maxBytes, string keepGlbPath)
        {
            var dir = new DirectoryInfo(dirPath);

            // Leftovers from an interrupted write or an interrupted clear.
            foreach (var stray in dir.GetFiles("*" + TempExtension))
                TryDelete(stray.FullName);
            if (dir.Parent != null)
            {
                foreach (var trash in dir.Parent.GetDirectories(dir.Name + TrashSuffix + "*"))
                    TryDeleteDirectory(trash.FullName);
            }

            if (maxBytes <= 0) return;

            var entries = dir.GetFiles("*" + GlbExtension);
            long total = 0;
            foreach (var entry in entries) total += entry.Length;
            if (total <= maxBytes) return;

            Array.Sort(entries, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            var keepFullPath = Path.GetFullPath(keepGlbPath);
            foreach (var entry in entries)
            {
                if (total <= maxBytes) break;
                if (string.Equals(entry.FullName, keepFullPath, StringComparison.Ordinal)) continue;
                total -= entry.Length;
                DeleteEntry(entry.FullName, Path.ChangeExtension(entry.FullName, SidecarExtension));
            }
        }

        private static void DeleteEntry(string glbPath, string sidecarPath)
        {
            // Sidecar first, so a half-deleted entry is never read as valid.
            TryDelete(sidecarPath);
            TryDelete(glbPath);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ── WebGL: flush persistentDataPath (IDBFS) to IndexedDB ────────
        private static void SyncPersistentStorage()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { AvaTwin_SyncPersistentStorage(); }
            catch (Exception ex) { Debug.LogWarning($"[Ava-Twin] Avatar cache sync failed: {ex.Message}"); }
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void AvaTwin_SyncPersistentStorage();
#endif
    }
}
