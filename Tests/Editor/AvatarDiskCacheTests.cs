using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AvaTwin.Tests
{
    /// <summary>
    /// Persistent avatar cache. The resolve and download steps are fakes that
    /// count calls, so no test touches the network.
    /// </summary>
    public class AvatarDiskCacheTests
    {
        private const string AvatarId = "test_g1_h2_t1_b1_s1";
        private const string Context = "app-1|https://customizer.example.invalid";
        private const string Host = "https://api.example.invalid";
        private const string SignedPrefix = "/storage/v1/object/sign/";
        private const string AvatarPath = "/storage/v1/object/sign/models/avatars/combos/abc123.glb";
        private const string FallbackPath = "/storage/v1/object/sign/models/avatars/combos/default01.glb";

        private string _dir;
        private int _resolveCalls;
        private int _downloadCalls;
        private string _resolvedPath;
        private bool _planFallback;
        private byte[] _served;
        private TaskCompletionSource<byte[]> _heldDownload;
        private SynchronizationContext _savedContext;
        private Func<string, Task<long?>> _savedVerifier;
        private int _verifyCalls;

        [SetUp]
        public void SetUp()
        {
            // The Editor runs tests on its main thread under UnitySynchronizationContext.
            // These tests block on tasks, so continuations must not need that thread.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _savedVerifier = AvatarPipeline.VerifyStorageUrlAsync;
            _verifyCalls = 0;

            _dir = Path.Combine(Path.GetTempPath(), "AvaTwinCacheTests-" + Guid.NewGuid().ToString("N"));
            AvatarDiskCache.DirectoryOverride = _dir;
            AvatarPipeline.PersistentCacheEnabled = true;
            AvatarPipeline.PersistentCacheMaxBytes = 100L * 1024 * 1024;
            AvatarPipeline.ClearMemoryCache();
            _resolveCalls = 0;
            _downloadCalls = 0;
            _resolvedPath = AvatarPath;
            _planFallback = false;
            _served = MakeGlb(4096, 1);
            _heldDownload = null;
        }

        [TearDown]
        public void TearDown()
        {
            AvatarPipeline.ClearMemoryCache();
            AvatarDiskCache.DirectoryOverride = null;
            AvatarPipeline.PersistentCacheEnabled = true;
            AvatarPipeline.PersistentCacheMaxBytes = 100L * 1024 * 1024;
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            AvatarPipeline.VerifyStorageUrlAsync = _savedVerifier;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void FakeStorage(long? verifiedLength)
        {
            AvatarPipeline.VerifyStorageUrlAsync = url =>
            {
                _verifyCalls++;
                return Task.FromResult(verifiedLength);
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static string B64Url(string json)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>A signed URL shaped like Supabase Storage's: token claims {url, exp}.</summary>
        private static string SignedUrl(string path, int expiresInSeconds = 300, string claimedObject = null, string host = Host)
        {
            var objectPath = claimedObject ?? path.Substring(SignedPrefix.Length);
            var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresInSeconds;
            var token = B64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." +
                        B64Url($"{{\"url\":\"{objectPath}\",\"scope\":\"download\",\"iat\":0,\"exp\":{exp}}}") + "." +
                        B64Url(Guid.NewGuid().ToString("N")); // signature is not checked client-side
            return $"{host}{path}?token={token}";
        }

        private static byte[] MakeGlb(int length, byte seed)
        {
            var bytes = new byte[length];
            for (var i = 12; i < length; i++) bytes[i] = (byte)(i * 31 + seed);
            BitConverter.GetBytes(0x46546C67u).CopyTo(bytes, 0); // "glTF"
            BitConverter.GetBytes(2u).CopyTo(bytes, 4);
            BitConverter.GetBytes((uint)length).CopyTo(bytes, 8);
            return bytes;
        }

        private Task<AvatarPipeline.GlbAcquisition> Acquire(string avatarId = AvatarId, string context = Context)
        {
            return AvatarPipeline.AcquireGlbAsync(
                avatarId,
                context,
                () =>
                {
                    _resolveCalls++;
                    // A fresh signed URL on every resolve, as the server does.
                    return Task.FromResult(new AvatarPipeline.ResolveResult
                    {
                        GlbUrl = SignedUrl(_resolvedPath),
                        PlanFallback = _planFallback,
                    });
                },
                url =>
                {
                    _downloadCalls++;
                    return _heldDownload != null ? _heldDownload.Task : Task.FromResult(_served);
                });
        }

        private static T Wait<T>(Task<T> task)
        {
            if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                Assert.Fail("Timed out waiting for a cache task.");
            return task.GetAwaiter().GetResult();
        }

        private static void Wait(Task task)
        {
            if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                Assert.Fail("Timed out waiting for a cache task.");
            task.GetAwaiter().GetResult();
        }

        /// <summary>Acquire, then commit as a successful import would.</summary>
        private byte[] Load(string avatarId = AvatarId)
        {
            var glb = Wait(Acquire(avatarId));
            Wait(AvatarPipeline.CommitGlbAsync(glb));
            return glb.Bytes;
        }

        /// <summary>Drops the in-memory cache, as an app restart would.</summary>
        private static void SimulateRestart() => AvatarPipeline.ClearMemoryCache();

        private string GlbPath(string path = AvatarPath)
        {
            AvatarDiskCache.GetEntryPaths(_dir, AvatarDiskCache.AssetKeyFromUrl(SignedUrl(path)), out var glb, out _);
            return glb;
        }

        private string SidecarPath(string path = AvatarPath)
        {
            AvatarDiskCache.GetEntryPaths(_dir, AvatarDiskCache.AssetKeyFromUrl(SignedUrl(path)), out _, out var sidecar);
            return sidecar;
        }

        private static string Key(string name) =>
            AvatarDiskCache.AssetKeyFromUrl(SignedUrl($"{SignedPrefix}models/avatars/combos/{name}.glb"));

        private static void Save(string dir, string key, byte[] bytes, long cap) =>
            AvatarDiskCache.Save(dir, key, bytes, cap, AvatarDiskCache.Generation);

        // ── Acceptance: restart reuses the GLB, server still consulted ──

        [Test]
        public void AfterRestart_GlbComesFromDisk_ResolveStillRuns()
        {
            var first = Load();
            Assert.AreEqual(1, _resolveCalls);
            Assert.AreEqual(1, _downloadCalls);

            SimulateRestart();
            var second = Load();

            Assert.AreEqual(2, _resolveCalls, "the server must authorize every load");
            Assert.AreEqual(1, _downloadCalls, "a disk hit must not download the GLB");
            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void SameSession_SecondLoad_UsesMemoryCache()
        {
            Load();
            Load();
            Assert.AreEqual(1, _resolveCalls);
            Assert.AreEqual(1, _downloadCalls);
        }

        [Test]
        public void SignedUrlToken_DoesNotAffectTheKey()
        {
            var one = AvatarDiskCache.AssetKeyFromUrl(SignedUrl(AvatarPath));
            Assert.IsNotNull(one);
            Assert.AreEqual(one, AvatarDiskCache.AssetKeyFromUrl(SignedUrl(AvatarPath)));
        }

        // ── Provenance: a cached file is reused only for a fresh, matching token ──

        [Test]
        public void ExpiredSignedUrl_IsNotCacheable()
        {
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl(SignedUrl(AvatarPath, expiresInSeconds: -1)));
        }

        [Test]
        public void TokenForAnotherObject_IsNotCacheable()
        {
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl(
                SignedUrl(AvatarPath, claimedObject: "models/avatars/combos/someone-else.glb")));
        }

        [Test]
        public void MissingOrGarbageToken_IsNotCacheable()
        {
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl(Host + AvatarPath));
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl(Host + AvatarPath + "?token=not-a-jwt"));
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl(Host + AvatarPath + "?token=a.%%%.c"));
        }

        [Test]
        public void ExpiredUrl_PassedToCharacterLoader_IsStillAnAvaTwinObject()
        {
            // Such URLs must skip both caches, not fall back to the TTL cache.
            Assert.IsTrue(AvatarDiskCache.IsAvaTwinStorageUrl(SignedUrl(AvatarPath, expiresInSeconds: -1)));
            Assert.IsFalse(AvatarDiskCache.IsAvaTwinStorageUrl("https://cdn.example.com/my-avatar.glb"));
        }

        [Test]
        public void NonDefaultPort_IsPartOfTheKey()
        {
            Assert.AreNotEqual(
                AvatarDiskCache.AssetKeyFromUrl(SignedUrl(AvatarPath, host: "https://self-hosted.example.invalid")),
                AvatarDiskCache.AssetKeyFromUrl(SignedUrl(AvatarPath, host: "https://self-hosted.example.invalid:8443")));
        }

        // ── Plan and access changes ─────────────────────────────────────

        [Test]
        public void PlanFallback_ThenUpgrade_LoadsTheRealAvatar()
        {
            _planFallback = true;
            _resolvedPath = FallbackPath;
            var fallback = MakeGlb(4096, 9);
            _served = fallback;
            CollectionAssert.AreEqual(fallback, Load());

            // The plan is upgraded: the server now resolves the real avatar.
            SimulateRestart();
            _planFallback = false;
            _resolvedPath = AvatarPath;
            _served = MakeGlb(4096, 1);

            CollectionAssert.AreEqual(_served, Load());
            Assert.AreEqual(2, _downloadCalls, "the cached fallback must never stand in for the real avatar");
        }

        [Test]
        public void ResolveFailure_IsNotMaskedByTheDiskCache()
        {
            Load();
            SimulateRestart();

            var denied = AvatarPipeline.AcquireGlbAsync(
                AvatarId,
                Context,
                () => Task.FromException<AvatarPipeline.ResolveResult>(new Exception("403 BUNDLE_NOT_ALLOWED")),
                url => Task.FromResult(_served));

            Assert.Throws<Exception>(() => Wait(denied));
        }

        // ── Acceptance: corruption and partial writes ───────────────────

        [Test]
        public void CorruptedEntry_IsRecoveredByExactlyOneDownload()
        {
            Load();
            var corrupt = File.ReadAllBytes(GlbPath());
            corrupt[corrupt.Length / 2] ^= 0xFF; // header intact, body damaged
            File.WriteAllBytes(GlbPath(), corrupt);

            SimulateRestart();
            CollectionAssert.AreEqual(_served, Load());
            Assert.AreEqual(2, _downloadCalls);

            SimulateRestart();
            Load();
            Assert.AreEqual(2, _downloadCalls, "the re-downloaded entry must be valid again");
        }

        [Test]
        public void TruncatedGlb_IsNeverServed()
        {
            Load();
            var full = File.ReadAllBytes(GlbPath());
            var truncated = new byte[full.Length / 2];
            Array.Copy(full, truncated, truncated.Length);
            File.WriteAllBytes(GlbPath(), truncated);

            SimulateRestart();
            CollectionAssert.AreEqual(_served, Load());
            Assert.AreEqual(2, _downloadCalls);
        }

        [Test]
        public void InterruptedWrite_MissingSidecar_IsAMissAndIsCleanedUp()
        {
            Load();
            File.Delete(SidecarPath()); // crash after the GLB, before the commit marker

            Assert.IsNull(AvatarDiskCache.TryLoad(_dir, AvatarDiskCache.AssetKeyFromUrl(SignedUrl(AvatarPath))));
            Assert.IsFalse(File.Exists(GlbPath()), "an orphan GLB must be removed");
        }

        [Test]
        public void GarbageSidecar_IsAMiss()
        {
            Load();
            File.WriteAllText(SidecarPath(), "{not json");

            SimulateRestart();
            Load();
            Assert.AreEqual(2, _downloadCalls);
        }

        [Test]
        public void StrayTempFiles_AreRemovedOnNextSave()
        {
            Directory.CreateDirectory(_dir);
            var stray = Path.Combine(_dir, "leftover.glb.tmp");
            File.WriteAllBytes(stray, new byte[10]);

            Load();
            Assert.IsFalse(File.Exists(stray));
        }

        // ── What must not be persisted ──────────────────────────────────

        [Test]
        public void UncommittedAcquisition_IsNotPersisted()
        {
            // Acquired but never committed, as when the GLB fails to import.
            Wait(Acquire());
            Assert.IsFalse(File.Exists(GlbPath()));

            SimulateRestart();
            Load();
            Assert.AreEqual(2, _downloadCalls);
        }

        [Test]
        public void NonGlbResponse_IsNotPersisted()
        {
            _served = System.Text.Encoding.UTF8.GetBytes("<html>error</html>");
            Load();
            Assert.IsFalse(File.Exists(GlbPath()));
        }

        [Test]
        public void NonAvaTwinUrls_AreNotCacheable()
        {
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl("https://cdn.example.com/my-avatar.glb"));
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl(Host + "/storage/v1/object/sign/other-bucket/a.glb"));
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl("file:///tmp/storage/v1/object/sign/models/a.glb"));
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl("not a url"));
            Assert.IsNotNull(AvatarDiskCache.AssetKeyFromUrl(Host + "/storage/v1/object/public/models/a.glb"));
            Assert.IsNull(AvatarDiskCache.AssetKeyFromUrl(SignedUrl("/storage/v1/object/sign/other-bucket/a.glb")));
        }

        [Test]
        public void OptOut_WritesNothing()
        {
            AvatarPipeline.PersistentCacheEnabled = false;
            Load();
            SimulateRestart();
            Load();

            Assert.AreEqual(2, _downloadCalls);
            Assert.IsFalse(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
        }

        // ── Concurrency ─────────────────────────────────────────────────

        [Test]
        public void ConcurrentLoadsOfOneAvatar_ShareOneResolveAndDownload()
        {
            _heldDownload = new TaskCompletionSource<byte[]>();
            var a = Acquire();
            var b = Acquire();
            Assert.IsFalse(a.IsCompleted);

            _heldDownload.SetResult(_served);
            var glbA = Wait(a);
            var glbB = Wait(b);
            Wait(AvatarPipeline.CommitGlbAsync(glbA));
            Wait(AvatarPipeline.CommitGlbAsync(glbB));

            Assert.AreEqual(1, _resolveCalls);
            Assert.AreEqual(1, _downloadCalls);
            Assert.IsTrue(File.Exists(GlbPath()));
        }

        // ── URLs from outside the SDK (CharacterLoader) need Storage's verdict ──

        [Test]
        public void ExternalUrl_DiskHit_UsedOnlyWhenStorageAccepts()
        {
            Load();
            FakeStorage(_served.LongLength);
            CollectionAssert.AreEqual(_served, Wait(AvatarPipeline.TryLoadVerifiedAsync(SignedUrl(AvatarPath))));
            Assert.AreEqual(1, _verifyCalls);
        }

        [Test]
        public void ExternalUrl_RejectedByStorage_IsNotServedFromDisk()
        {
            Load();
            FakeStorage(null); // forged signature, revoked or expired: Storage says no
            Assert.IsNull(Wait(AvatarPipeline.TryLoadVerifiedAsync(SignedUrl(AvatarPath))));
        }

        [Test]
        public void ExternalUrl_LengthMismatch_IsNotServedFromDisk()
        {
            Load();
            FakeStorage(_served.LongLength + 1);
            Assert.IsNull(Wait(AvatarPipeline.TryLoadVerifiedAsync(SignedUrl(AvatarPath))));
        }

        [Test]
        public void ExternalUrl_NoCachedEntry_DoesNotCallStorage()
        {
            FakeStorage(4096);
            Assert.IsNull(Wait(AvatarPipeline.TryLoadVerifiedAsync(SignedUrl(AvatarPath))));
            Assert.AreEqual(0, _verifyCalls);
        }

        [Test]
        public void ClearRacingACommit_NeverLeavesStaleBytes()
        {
            for (var i = 0; i < 50; i++)
            {
                AvatarPipeline.ClearAvatarCache();
                var glb = Wait(Acquire());
                var commit = Task.Run(() => AvatarPipeline.CommitGlbAsync(glb));
                var clear = Task.Run(() => AvatarPipeline.ClearAvatarCache());
                Wait(Task.WhenAll(commit, clear));

                // Whichever ran first, nothing from before the clear survives it.
                Assert.AreEqual(0, AvatarDiskCache.GetSizeBytes(), $"disk, iteration {i}");
                Assert.AreNotEqual(AvatarPipeline.GlbSource.Memory, Wait(Acquire()).Source, $"memory, iteration {i}");
            }
        }

        [Test]
        public void DifferentApps_DoNotShareAnInFlightLoad()
        {
            _heldDownload = new TaskCompletionSource<byte[]>();
            var appA = Acquire(context: "app-a|https://customizer.example.invalid");
            var appB = Acquire(context: "app-b|https://customizer.example.invalid");
            _heldDownload.SetResult(_served);
            Wait(appA);
            Wait(appB);

            Assert.AreEqual(2, _resolveCalls, "each app must be authorized by its own resolve");
        }

        [Test]
        public void ClearDuringLoad_DoesNotRepopulateTheCache()
        {
            _heldDownload = new TaskCompletionSource<byte[]>();
            var pending = Acquire();
            AvatarPipeline.ClearAvatarCache();
            _heldDownload.SetResult(_served);
            Wait(AvatarPipeline.CommitGlbAsync(Wait(pending)));

            Assert.AreEqual(0, AvatarDiskCache.GetSizeBytes());
            _heldDownload = null;
            Load();
            Assert.AreEqual(2, _downloadCalls, "neither memory nor disk may hold the pre-clear load");
        }

        [Test]
        public void FailedLoad_DoesNotPoisonTheNextAttempt()
        {
            var failed = AvatarPipeline.AcquireGlbAsync(
                AvatarId,
                Context,
                () => Task.FromException<AvatarPipeline.ResolveResult>(new Exception("network down")),
                url => Task.FromResult(_served));
            Assert.Throws<Exception>(() => Wait(failed));

            CollectionAssert.AreEqual(_served, Load());
        }

        // ── Size cap ────────────────────────────────────────────────────

        [Test]
        public void SizeCap_EvictsLeastRecentlyUsed()
        {
            const long cap = 4096 * 2 + 100;
            Save(_dir, Key("a"), MakeGlb(4096, 1), cap);
            Save(_dir, Key("b"), MakeGlb(4096, 2), cap);
            AvatarDiskCache.GetEntryPaths(_dir, Key("a"), out var aPath, out _);
            AvatarDiskCache.GetEntryPaths(_dir, Key("b"), out var bPath, out _);
            File.SetLastWriteTimeUtc(aPath, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(bPath, DateTime.UtcNow.AddMinutes(-5));

            // Reading "a" makes it the most recently used; "b" becomes the oldest.
            Assert.IsNotNull(AvatarDiskCache.TryLoad(_dir, Key("a")));

            Save(_dir, Key("c"), MakeGlb(4096, 3), cap);

            Assert.IsNotNull(AvatarDiskCache.TryLoad(_dir, Key("a")));
            Assert.IsNull(AvatarDiskCache.TryLoad(_dir, Key("b")), "least recently used entry must be evicted");
            Assert.IsNotNull(AvatarDiskCache.TryLoad(_dir, Key("c")));
            Assert.LessOrEqual(AvatarDiskCache.GetSizeBytes(), cap);
        }

        [Test]
        public void EntryLargerThanCap_IsNotPersisted()
        {
            Save(_dir, Key("big"), MakeGlb(8192, 1), 4096);
            Assert.IsNull(AvatarDiskCache.TryLoad(_dir, Key("big")));
        }

        // ── Misc ────────────────────────────────────────────────────────

        [Test]
        public void ClearAvatarCache_RemovesEverything()
        {
            Load();
            AvatarPipeline.ClearAvatarCache();

            Assert.AreEqual(0, AvatarDiskCache.GetSizeBytes());
            Load();
            Assert.AreEqual(2, _downloadCalls);
        }

        [Test]
        public void DifferentObjects_DoNotCollide()
        {
            Save(_dir, Key("one"), MakeGlb(4096, 1), 0);
            Save(_dir, Key("two"), MakeGlb(4096, 2), 0);

            CollectionAssert.AreNotEqual(
                AvatarDiskCache.TryLoad(_dir, Key("one")),
                AvatarDiskCache.TryLoad(_dir, Key("two")));
        }
    }
}
