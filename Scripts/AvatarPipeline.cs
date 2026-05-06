using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;
using UnityEngine.Networking;

namespace AvaTwin
{
    /// <summary>
    /// Stateless avatar loading pipeline. Each method is independent —
    /// safe to call concurrently for multiple avatars.
    /// </summary>
    public static class AvatarPipeline
    {
        // ── Shader property name constants ──────────────────────────────
        private const string PropMetallic = "_Metallic";
        private const string PropSmoothness = "_Smoothness";
        private const string PropGlossiness = "_Glossiness";
        private const string PropGlossMapScale = "_GlossMapScale";
        private const string PropGlossyReflections = "_GlossyReflections";
        private const string PropOcclusionStrength = "_OcclusionStrength";
        private const string PropBumpScale = "_BumpScale";
        private const string PropMetallicGlossMap = "_MetallicGlossMap";
        private const string PropSpecGlossMap = "_SpecGlossMap";
        private const string PropBumpMap = "_BumpMap";
        private const string PropOcclusionMap = "_OcclusionMap";
        private const string PropParallaxMap = "_ParallaxMap";
        private const string PropDetailAlbedoMap = "_DetailAlbedoMap";
        private const string PropDetailNormalMap = "_DetailNormalMap";
        private const string PropDetailMask = "_DetailMask";
        private const string PropEmissionMap = "_EmissionMap";
        private const string PropSpecColor = "_SpecColor";
        private const string PropEmissionColor = "_EmissionColor";
        private const string PropEnvironmentReflections = "_EnvironmentReflections";
        private const string PropSpecularHighlights = "_SpecularHighlights";
        private const string PropReceiveShadows = "_ReceiveShadows";
        private const string PropSurface = "_Surface";
        private const string PropWorkflowMode = "_WorkflowMode";
        private const string PropMode = "_Mode";
        private const string PropBaseColor = "_BaseColor";
        private const string PropColor = "_Color";
        private const string PropBaseMap = "_BaseMap";
        private const string PropMainTex = "_MainTex";

        // ── Skin mask constants ─────────────────────────────────────────
        private const string PropSkinColor = "_SkinColor";
        private const string PropSkinMask = "_SkinMask";
        private const string PropHasSkinMask = "_HasSkinMask";

        // ── Skin tone mapping (head variant -> hex color) ───────────────
        private static readonly Dictionary<string, string> SkinToneMap = new()
        {
            { "h1", "#FFDFC4" },
            { "h2", "#D4A574" },
            { "h3", "#8D5524" },
            { "h4", "#C68642" },
        };

        /// <summary>Get the default skin tone for a head variant in an avatar_id.</summary>
        public static string GetDefaultSkinTone(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId)) return "#FFDFC4";
            foreach (var kvp in SkinToneMap)
            {
                if (avatarId.Contains($"_{kvp.Key}_") || avatarId.EndsWith($"_{kvp.Key}"))
                    return kvp.Value;
            }
            return "#FFDFC4";
        }

        // ── In-memory GLB cache ─────────────────────────────────────────
        private static readonly Dictionary<string, byte[]> GlbCache = new();
        private static readonly object CacheLock = new();

        private static byte[] GetCachedGlb(string avatarId)
        {
            lock (CacheLock)
            {
                return GlbCache.TryGetValue(avatarId, out var bytes) ? bytes : null;
            }
        }

        private static void CacheGlb(string avatarId, byte[] bytes)
        {
            lock (CacheLock)
            {
                if (!GlbCache.ContainsKey(avatarId) && bytes != null && bytes.Length > 0)
                    GlbCache[avatarId] = bytes;
            }
        }

        // ── ResolveResult ───────────────────────────────────────────────
        /// <summary>Result from resolving an avatar ID to a GLB URL.</summary>
        public struct ResolveResult
        {
            public string GlbUrl;
            /// <summary>Skin tone hex from server, or null if not provided.</summary>
            public string SkinTone;
        }

        // ── Resources paths ─────────────────────────────────────────────
        private const string AnimatorControllerResourcePath = "AvaTwinAnimator";
        private const string AvatarTposePath = "TPose/AvaTwinTPose";

        // ── JSON response types ─────────────────────────────────────────
        [Serializable]
        private class MintTokenResponse
        {
            public string token;
            // Server populates this only when mode=editor. Null/default-zeroed
            // for embed/playground mints. The shared EditorQuotaLogger gates
            // its output on plan != "" so it is safe to pass even an unset
            // (default-initialized) struct here.
            public EditorQuota editor_quota;
        }

        /// <summary>
        /// Shape of the JSON body returned by /api/token-mint on failure.
        /// `code` is a stable machine-readable identifier (e.g.
        /// ORIGIN_NOT_REGISTERED, BUNDLE_NOT_ALLOWED); `error` is a
        /// developer-readable message (often references the Console).
        /// </summary>
        [Serializable]
        private class MintTokenErrorResponse
        {
            public string code;
            public string error;
        }

        [Serializable]
        private class AvatarResolveResponse
        {
            public string avatar_id;
            public string url;
            public string skin_tone;
        }

        // ─────────────────────────────────────────────────────────────────
        // LoadAsync — full pipeline orchestrator
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Load a complete avatar: resolve (api_key direct) -> download -> instantiate -> shader -> humanoid.
        /// No customizer-token-mint on this path — game-runtime LoadAvatar does NOT count a session.
        /// </summary>
        public static async Task<AvatarResult> LoadAsync(
            string avatarId,
            Credentials credentials,
            string baseUrl,
            string skinToneHex,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                throw new ArgumentException("avatarId is null or empty.", nameof(avatarId));
            if (credentials == null)
                throw new ArgumentNullException(nameof(credentials));
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl is null or empty.", nameof(baseUrl));

            var resolvedSkinTone = skinToneHex;

            // Check GLB cache first
            var cachedBytes = GetCachedGlb(avatarId);
            byte[] glbBytes;

            if (cachedBytes != null)
            {
                glbBytes = cachedBytes;
            }
            else
            {
                // Game-runtime LoadAvatar path: resolve via api_key direct (x-api-key + x-app-id),
                // bypassing customizer-token-mint so this rendering path does NOT count a session.
                // Each customizer-open still mints (see OpenCustomizerAsync); only LoadAvatar paths
                // are token-less.
                var resolveResult = await ResolveAvatarByApiKeyAsync(
                    avatarId, credentials.AppId, credentials.ApiKey, baseUrl, ct);
                if (!string.IsNullOrEmpty(resolveResult.SkinTone))
                    resolvedSkinTone = resolveResult.SkinTone;
                glbBytes = await DownloadGlbAsync(resolveResult.GlbUrl, ct);
                CacheGlb(avatarId, glbBytes);
            }

            var root = await InstantiateGlbAsync(glbBytes, ct);
            ApplyMaterials(root, resolvedSkinTone);
            ConfigureHumanoid(root);
            return new AvatarResult(root, avatarId, resolvedSkinTone);
        }

        /// <summary>Load a complete avatar using explicit string credentials.</summary>
        public static async Task<AvatarResult> LoadAsync(
            string avatarId,
            string appId,
            string apiKey,
            string baseUrl,
            string skinToneHex,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                throw new ArgumentException("avatarId is null or empty.", nameof(avatarId));
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentException("appId is null or empty.", nameof(appId));
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl is null or empty.", nameof(baseUrl));

            var resolvedSkinTone = skinToneHex;

            // Check GLB cache first
            var cachedBytes = GetCachedGlb(avatarId);
            byte[] glbBytes;

            if (cachedBytes != null)
            {
                glbBytes = cachedBytes;
            }
            else
            {
                // Game-runtime LoadAvatar path: resolve via api_key direct (x-api-key + x-app-id),
                // bypassing customizer-token-mint so this rendering path does NOT count a session.
                var resolveResult = await ResolveAvatarByApiKeyAsync(
                    avatarId, appId, apiKey, baseUrl, ct);
                if (!string.IsNullOrEmpty(resolveResult.SkinTone))
                    resolvedSkinTone = resolveResult.SkinTone;
                glbBytes = await DownloadGlbAsync(resolveResult.GlbUrl, ct);
                CacheGlb(avatarId, glbBytes);
            }

            var root = await InstantiateGlbAsync(glbBytes, ct);
            ApplyMaterials(root, resolvedSkinTone);
            ConfigureHumanoid(root);
            return new AvatarResult(root, avatarId, resolvedSkinTone);
        }

        // ─────────────────────────────────────────────────────────────────
        // MintTokenAsync
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Mint a session token from app credentials.</summary>
        public static async Task<string> MintTokenAsync(
            string appId,
            string apiKey,
            string baseUrl,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentException("appId is null or empty.", nameof(appId));
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("apiKey is null or empty.", nameof(apiKey));

            var mintUrl = $"{baseUrl.TrimEnd('/')}/api/token-mint";
            // v4 contract: include bundle_id (Application.identifier) for apps cap enforcement
            // and platform (Application.platform) for analytics. Both are optional server-side.
            var bundleId = EscapeJson(Application.identifier ?? string.Empty);
            var platformStr = EscapeJson(Application.platform.ToString());
            // mode signals editor flows to the server (no session count, relaxed platform check).
            // Compile-stripped from prod builds via preprocessor — the "editor" literal cannot
            // reach a shipped binary, preventing devs from hijacking platform reporting.
#if UNITY_EDITOR
            const string mintMode = "editor";
#else
            const string mintMode = "embed";
#endif
            var mintJson = $"{{\"appId\":\"{appId}\",\"apiKey\":\"{apiKey}\",\"mode\":\"{mintMode}\",\"bundle_id\":\"{bundleId}\",\"platform\":\"{platformStr}\"}}";
            var mintBytes = Encoding.UTF8.GetBytes(mintJson);

            using (var req = new UnityWebRequest(mintUrl, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(mintBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    // Surface a developer-friendly message for the per-platform
                    // identifier registration errors introduced by the server's
                    // allowlist enforcement. The general error path stays
                    // unchanged — this just adds extra clarity (and a docs link)
                    // for the registration-related codes so devs know where to
                    // fix the mismatch.
                    LogRegistrationErrorIfApplicable(req.downloadHandler?.text);

                    throw new Exception(
                        $"Token mint failed: {req.error} — {req.downloadHandler?.text}");
                }

                var resp = JsonUtility.FromJson<MintTokenResponse>(req.downloadHandler.text);
                if (resp == null || string.IsNullOrWhiteSpace(resp.token))
                {
                    throw new Exception(
                        $"Token mint returned no token. Response: {req.downloadHandler.text}");
                }

                // Surface plan-aware editor-mode quota via the shared helper —
                // identical log shape across every token-mint caller. The helper
                // body is wrapped in #if UNITY_EDITOR so the string formatting is
                // compile-stripped from shipped builds.
                EditorQuotaLogger.LogIfPresent(resp.editor_quota);

                return resp.token;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // ResolveAvatarAsync
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Resolve an avatar_id to a signed GLB URL (and optional skin tone).</summary>
        public static async Task<ResolveResult> ResolveAvatarAsync(
            string avatarId,
            string sessionToken,
            string baseUrl,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                throw new ArgumentException("avatarId is null or empty.", nameof(avatarId));
            if (string.IsNullOrWhiteSpace(sessionToken))
                throw new ArgumentException("sessionToken is null or empty.", nameof(sessionToken));

            var resolveUrl = $"{baseUrl.TrimEnd('/')}/api/avatar-resolve";
            // v4 contract: include bundle_id (Application.identifier) and platform
            // (Application.platform) so the server can enforce per-app
            // platform_identifiers allowlists. JWT-bearer path already authenticates
            // the caller — these fields add defense-in-depth and keep the SDK
            // consistent with the api_key path below.
            var (bundleId, platformStr) = PlatformInfo.Get();
            var jsonBody = $"{{\"avatar_id\":\"{EscapeJson(avatarId)}\",\"bundle_id\":\"{EscapeJson(bundleId)}\",\"platform\":\"{EscapeJson(platformStr)}\"}}";
            var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

            using (var req = new UnityWebRequest(resolveUrl, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", $"Bearer {sessionToken}");

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    throw new Exception(
                        $"Avatar resolve failed: {req.error} — {req.downloadHandler?.text}");
                }

                var resp = JsonUtility.FromJson<AvatarResolveResponse>(req.downloadHandler.text);
                if (resp == null || string.IsNullOrWhiteSpace(resp.url))
                {
                    throw new Exception(
                        $"Avatar resolve returned no URL. Response: {req.downloadHandler.text}");
                }

                return new ResolveResult
                {
                    GlbUrl = resp.url,
                    SkinTone = string.IsNullOrEmpty(resp.skin_tone) ? null : resp.skin_tone
                };
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // ResolveAvatarByApiKeyAsync
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve an avatar_id to a signed GLB URL using direct app-key authentication
        /// (x-api-key + x-app-id headers). Skips customizer-token-mint entirely so this
        /// path does NOT count a customizer session — intended for game-runtime LoadAvatar
        /// flows (multiplayer rendering, remote-player loads). Each customizer-open should
        /// continue to use MintTokenAsync, since opens legitimately count as sessions.
        /// </summary>
        public static async Task<ResolveResult> ResolveAvatarByApiKeyAsync(
            string avatarId,
            string appId,
            string apiKey,
            string baseUrl,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
                throw new ArgumentException("avatarId is null or empty.", nameof(avatarId));
            if (string.IsNullOrWhiteSpace(appId))
                throw new ArgumentException("appId is null or empty.", nameof(appId));
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("apiKey is null or empty.", nameof(apiKey));
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl is null or empty.", nameof(baseUrl));

            var resolveUrl = $"{baseUrl.TrimEnd('/')}/api/avatar-resolve";
            // v4 contract: include bundle_id (Application.identifier) and platform
            // (Application.platform) so the server can enforce per-app
            // platform_identifiers allowlists for the api_key direct path.
            var (bundleId, platformStr) = PlatformInfo.Get();
            var jsonBody = $"{{\"avatar_id\":\"{EscapeJson(avatarId)}\",\"bundle_id\":\"{EscapeJson(bundleId)}\",\"platform\":\"{EscapeJson(platformStr)}\"}}";
            var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

            using (var req = new UnityWebRequest(resolveUrl, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("x-api-key", apiKey);
                req.SetRequestHeader("x-app-id", appId);

                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    throw new Exception(
                        $"Avatar resolve (api_key) failed: {req.error} — {req.downloadHandler?.text}");
                }

                var resp = JsonUtility.FromJson<AvatarResolveResponse>(req.downloadHandler.text);
                if (resp == null || string.IsNullOrWhiteSpace(resp.url))
                {
                    throw new Exception(
                        $"Avatar resolve (api_key) returned no URL. Response: {req.downloadHandler.text}");
                }

                return new ResolveResult
                {
                    GlbUrl = resp.url,
                    SkinTone = string.IsNullOrEmpty(resp.skin_tone) ? null : resp.skin_tone
                };
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // DownloadGlbAsync
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Download GLB bytes from a URL.</summary>
        public static async Task<byte[]> DownloadGlbAsync(
            string url,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("url is null or empty.", nameof(url));

            using (var req = UnityWebRequest.Get(url))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                var op = req.SendWebRequest();

                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested)
                    {
                        req.Abort();
                        ct.ThrowIfCancellationRequested();
                    }
                    await Task.Yield();
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    throw new Exception($"GLB download failed: {req.error}");
                }

                return req.downloadHandler.data;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // InstantiateGlbAsync
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Instantiate a GLB from bytes using glTFast.</summary>
        public static async Task<GameObject> InstantiateGlbAsync(
            byte[] glbBytes,
            CancellationToken ct = default)
        {
            if (glbBytes == null || glbBytes.Length == 0)
                throw new ArgumentException("glbBytes is null or empty.", nameof(glbBytes));

            // Enforce minimum skin weights for correct mesh rendering
            if (QualitySettings.skinWeights < SkinWeights.FourBones)
            {
                QualitySettings.skinWeights = SkinWeights.FourBones;
            }

            var logger = new ConsoleLogger();
            var import = new GltfImport(materialGenerator: new FlatMaterialGenerator(), logger: logger);

            Uri sourceUri;
            try
            {
                sourceUri = new Uri("https://ava-twin.me/avatar.glb");
            }
            catch
            {
                sourceUri = null;
            }

            bool loaded;
            try
            {
                loaded = await import.LoadGltfBinary(glbBytes, uri: sourceUri, cancellationToken: ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new Exception($"GLB load failed: {ex.Message}", ex);
            }

            if (!loaded)
            {
                throw new Exception("GLB import failed — glTFast returned false.");
            }

            var root = new GameObject("AvatarPipeline_Root");
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            var instantiator = new GameObjectInstantiator(import, root.transform);

            bool instantiated;
            try
            {
                instantiated = await import.InstantiateMainSceneAsync(instantiator);
            }
            catch (OperationCanceledException)
            {
                UnityEngine.Object.Destroy(root);
                throw;
            }
            catch (Exception ex)
            {
                UnityEngine.Object.Destroy(root);
                throw new Exception($"GLB instantiation failed: {ex.Message}", ex);
            }

            if (!instantiated)
            {
                UnityEngine.Object.Destroy(root);
                throw new Exception("GLB scene instantiation failed.");
            }

            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;

            return root;
        }

        // ─────────────────────────────────────────────────────────────────
        // ApplyMaterials
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply Ava-Twin shader, extract skin masks, apply skin tone.
        /// Must be called on the main thread.
        /// </summary>
        public static void ApplyMaterials(GameObject root, string skinToneHex)
        {
            if (root == null) return;

            // Extract skin masks from emissive slot BEFORE ForceFlatMaterials clears them
            ExtractSkinMasks(root);

            // Force flat/matte on ALL materials — ensures no PBR reflections survive
            ForceFlatMaterials(root);

            // Apply skin tone tint
            ApplySkinToneToAvatar(root, skinToneHex);
        }

        // ─────────────────────────────────────────────────────────────────
        // ConfigureHumanoid
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Configure humanoid bones and build Unity Avatar.
        /// Uses the pre-built Ava-Twin TPose Avatar from Resources.
        /// Returns the Avatar, or null if configuration failed.
        /// </summary>
        public static Avatar ConfigureHumanoid(GameObject root)
        {
            if (root == null) return null;

            var avatar = GetAvaTwinAvatar();
            if (avatar == null)
            {
                Debug.LogError("[Ava-Twin] Humanoid avatar asset not found. " +
                               "Ensure the TPose resource is included in your project.");
                return null;
            }

            var animator = root.GetComponent<Animator>();
            if (animator == null)
                animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = false;

            var controller = Resources.Load<RuntimeAnimatorController>(AnimatorControllerResourcePath);
            if (controller != null)
            {
                animator.runtimeAnimatorController = controller;
                animator.Rebind();
                animator.SetBool("Grounded", true);
                animator.Update(0f);
            }
            else
            {
                Debug.LogWarning("[Ava-Twin] Animator controller not found in Resources. " +
                                 "Avatar assigned but no controller.");
            }

            return avatar;
        }

        // ─────────────────────────────────────────────────────────────────
        // Internal: GetAvaTwinAvatar
        // ─────────────────────────────────────────────────────────────────

        private static Avatar _cachedAvatar;

        /// <summary>
        /// If the token-mint failure body contains one of the per-platform
        /// identifier registration codes (ORIGIN_NOT_REGISTERED,
        /// ORIGIN_NOT_ALLOWED, BUNDLE_NOT_REGISTERED, BUNDLE_NOT_ALLOWED),
        /// emit a Debug.LogError that includes the server's message and a
        /// link to the registration docs. Other failures fall through to the
        /// generic exception thrown by the caller.
        /// </summary>
        private static void LogRegistrationErrorIfApplicable(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return;

            MintTokenErrorResponse err;
            try
            {
                err = JsonUtility.FromJson<MintTokenErrorResponse>(responseBody);
            }
            catch
            {
                return;
            }

            if (err == null || string.IsNullOrEmpty(err.code)) return;

            if (err.code == "ORIGIN_NOT_REGISTERED" ||
                err.code == "ORIGIN_NOT_ALLOWED" ||
                err.code == "BUNDLE_NOT_REGISTERED" ||
                err.code == "BUNDLE_NOT_ALLOWED")
            {
                var serverMessage = string.IsNullOrEmpty(err.error) ? "(no message)" : err.error;
                Debug.LogError(
                    $"[Ava-Twin] {err.code}: {serverMessage}\n" +
                    "Docs: https://ava-twin.me/docs/setup#registering-identifiers");
            }
        }

        /// <summary>
        /// Minimal JSON string-escape for use in hand-built JSON payloads.
        /// Application.identifier on Android is a reverse-DNS bundle id (no
        /// special chars in practice), but we defensively escape anyway since
        /// the host project controls this value.
        /// </summary>
        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns the pre-built Ava-Twin Avatar from the TPose FBX in Resources.
        /// </summary>
        private static Avatar GetAvaTwinAvatar()
        {
            if (_cachedAvatar != null) return _cachedAvatar;

            var allAssets = Resources.LoadAll(AvatarTposePath);
            if (allAssets != null)
            {
                foreach (var asset in allAssets)
                {
                    if (asset is Avatar av && av.isHuman)
                    {
                        _cachedAvatar = av;
                        return _cachedAvatar;
                    }
                }
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // Internal: ExtractSkinMasks
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extract skin mask textures from the emissive slot of GLB materials.
        /// Must run BEFORE ForceFlatMaterials which clears emission.
        /// </summary>
        private static void ExtractSkinMasks(GameObject root)
        {
            if (root == null) return;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;

                    if (!mat.HasProperty(PropEmissionMap)) continue;
                    var emissiveTex = mat.GetTexture(PropEmissionMap) as Texture2D;
                    if (emissiveTex == null) continue;

                    // This emissive texture is actually our skin mask — extract it
                    if (mat.HasProperty(PropSkinMask))
                    {
                        mat.SetTexture(PropSkinMask, emissiveTex);
                        mat.SetFloat(PropHasSkinMask, 1f);

                        // Point filtering — no interpolation (matches Three.js NearestFilter)
                        emissiveTex.filterMode = FilterMode.Point;
                    }

                    // Clear emissive so it doesn't glow
                    mat.SetTexture(PropEmissionMap, null);
                    mat.SetColor(PropEmissionColor, Color.black);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Internal: ForceFlatMaterials
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Force all materials to metallic=0, smoothness=0, no reflections, no specular.
        /// Guarantees stylized textures render flat regardless of glTFast importer settings.
        /// </summary>
        private static void ForceFlatMaterials(GameObject root)
        {
            if (root == null) return;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = renderer.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;

                    // Zero all PBR value properties (covers both Standard + URP)
                    if (mat.HasProperty(PropMetallic)) mat.SetFloat(PropMetallic, 0f);
                    if (mat.HasProperty(PropSmoothness)) mat.SetFloat(PropSmoothness, 0f);
                    if (mat.HasProperty(PropGlossiness)) mat.SetFloat(PropGlossiness, 0f);
                    if (mat.HasProperty(PropGlossMapScale)) mat.SetFloat(PropGlossMapScale, 0f);
                    if (mat.HasProperty(PropGlossyReflections)) mat.SetFloat(PropGlossyReflections, 0f);
                    if (mat.HasProperty(PropOcclusionStrength)) mat.SetFloat(PropOcclusionStrength, 0f);
                    if (mat.HasProperty(PropBumpScale)) mat.SetFloat(PropBumpScale, 0f);

                    // Remove ALL secondary maps so shaders can't read smoothness from alpha
                    if (mat.HasProperty(PropMetallicGlossMap)) mat.SetTexture(PropMetallicGlossMap, null);
                    if (mat.HasProperty(PropSpecGlossMap)) mat.SetTexture(PropSpecGlossMap, null);
                    if (mat.HasProperty(PropBumpMap)) mat.SetTexture(PropBumpMap, null);
                    if (mat.HasProperty(PropOcclusionMap)) mat.SetTexture(PropOcclusionMap, null);
                    if (mat.HasProperty(PropParallaxMap)) mat.SetTexture(PropParallaxMap, null);
                    if (mat.HasProperty(PropDetailAlbedoMap)) mat.SetTexture(PropDetailAlbedoMap, null);
                    if (mat.HasProperty(PropDetailNormalMap)) mat.SetTexture(PropDetailNormalMap, null);
                    if (mat.HasProperty(PropDetailMask)) mat.SetTexture(PropDetailMask, null);
                    if (mat.HasProperty(PropEmissionMap)) mat.SetTexture(PropEmissionMap, null);

                    // Zero specular color
                    if (mat.HasProperty(PropSpecColor)) mat.SetColor(PropSpecColor, Color.black);
                    if (mat.HasProperty(PropEmissionColor)) mat.SetColor(PropEmissionColor, Color.black);

                    // Disable feature flags (0 = off) — covers both Standard + URP
                    if (mat.HasProperty(PropEnvironmentReflections)) mat.SetFloat(PropEnvironmentReflections, 0f);
                    if (mat.HasProperty(PropSpecularHighlights)) mat.SetFloat(PropSpecularHighlights, 0f);
                    if (mat.HasProperty(PropReceiveShadows)) mat.SetFloat(PropReceiveShadows, 1f);

                    // Fix UV seam lines: disable mipmaps on albedo texture.
                    // Mipmaps blend texels with black padding outside UV islands,
                    // creating visible dark lines at seams when viewed from distance.
                    if (mat.HasProperty(PropBaseMap))
                    {
                        var baseTex = mat.GetTexture(PropBaseMap) as Texture2D;
                        if (baseTex != null)
                        {
                            baseTex.filterMode = FilterMode.Bilinear;
                            // Request mipmaps off — requires re-upload
                            // Note: Texture2D from glTFast may be read-only, so we catch
                            try
                            {
                                if (baseTex.mipmapCount > 1)
                                {
                                    baseTex.requestedMipmapLevel = 0;
                                }
                            }
                            catch { /* read-only texture, skip */ }
                        }
                    }

                    // Shader keywords: disable ALL map-based inputs
                    mat.DisableKeyword("_METALLICSPECGLOSSMAP");
                    mat.DisableKeyword("_METALLICGLOSSMAP");
                    mat.DisableKeyword("_SPECGLOSSMAP");
                    mat.DisableKeyword("_NORMALMAP");
                    mat.DisableKeyword("_OCCLUSIONMAP");
                    mat.DisableKeyword("_PARALLAXMAP");
                    mat.DisableKeyword("_DETAIL_MULX2");
                    mat.DisableKeyword("_EMISSION");

                    // Standard shader: disable specular highlights + reflections
                    mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                    mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                    mat.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
                    mat.EnableKeyword("_GLOSSYREFLECTIONS_OFF");

                    // URP: disable reflections + specular (different keyword names)
                    mat.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                    mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

                    // Force surface/workflow settings
                    if (mat.HasProperty(PropSurface)) mat.SetFloat(PropSurface, 0f);
                    if (mat.HasProperty(PropWorkflowMode)) mat.SetFloat(PropWorkflowMode, 1f);
                    if (mat.HasProperty(PropMode)) mat.SetFloat(PropMode, 0f);

                    mat.SetShaderPassEnabled("SHADOWCASTER", true);
                    mat.renderQueue = -1;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Internal: ApplySkinToneToAvatar
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply skin tone tint to avatar materials.
        /// Uses _SkinColor property on Ava-Twin/Stylized shader (mask-aware).
        /// </summary>
        private static void ApplySkinToneToAvatar(GameObject avatarRoot, string skinToneHex)
        {
            if (avatarRoot == null || string.IsNullOrWhiteSpace(skinToneHex))
                return;
            if (!ColorUtility.TryParseHtmlString(skinToneHex, out var tint))
                return;

            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                var mats = renderer.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;

                    // Only apply skin color if material has a mask — no mask means no tinting
                    if (mat.HasProperty(PropHasSkinMask) && mat.GetFloat(PropHasSkinMask) > 0.5f
                        && mat.HasProperty(PropSkinColor))
                    {
                        mat.SetColor(PropSkinColor, tint);
                    }
                }
                renderer.materials = mats;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // FlatMaterialGenerator — inner class for glTFast material creation
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Custom glTFast material generator that creates flat/matte materials
        /// using the Ava-Twin/Stylized shader. Extracts skin masks from the
        /// emissive texture slot during import.
        /// </summary>
        private sealed class FlatMaterialGenerator : IMaterialGenerator
        {
            private ICodeLogger _logger;

            public void SetLogger(ICodeLogger logger)
            {
                _logger = logger;
            }

            public Material GetDefaultMaterial(bool pointsSupport = false)
            {
                var mat = CreateFlatMaterial();
                if (pointsSupport) mat.enableInstancing = true;
                return mat;
            }

            public Material GenerateMaterial(
                GLTFast.Schema.Material gltfMaterial,
                IGltfReadable gltf,
                bool pointsSupport = false)
            {
                var mat = CreateFlatMaterial();
                if (pointsSupport) mat.enableInstancing = true;

                var pbr = gltfMaterial?.pbrMetallicRoughness;
                if (pbr?.baseColorFactor != null && pbr.baseColorFactor.Length >= 4)
                {
                    var color = new Color(
                        pbr.baseColorFactor[0],
                        pbr.baseColorFactor[1],
                        pbr.baseColorFactor[2],
                        pbr.baseColorFactor[3]
                    );
                    if (mat.HasProperty(PropBaseColor)) mat.SetColor(PropBaseColor, color);
                    if (mat.HasProperty(PropColor)) mat.SetColor(PropColor, color);
                }

                // Apply base color texture if available
                if (pbr?.baseColorTexture != null)
                {
                    var texInfo = pbr.baseColorTexture;
                    var tex = gltf.GetTexture(texInfo.index);
                    if (tex != null)
                    {
                        if (mat.HasProperty(PropBaseMap)) mat.SetTexture(PropBaseMap, tex);
                        if (mat.HasProperty(PropMainTex)) mat.SetTexture(PropMainTex, tex);
                    }
                }

                // Apply emissive texture (skin mask smuggled via emissive slot)
                if (gltfMaterial?.emissiveTexture != null)
                {
                    var emissiveTex = gltf.GetTexture(gltfMaterial.emissiveTexture.index);
                    if (emissiveTex != null)
                    {
                        // This is actually our skin mask — assign directly to _SkinMask
                        if (mat.HasProperty(PropSkinMask))
                        {
                            emissiveTex.filterMode = FilterMode.Point;
                            mat.SetTexture(PropSkinMask, emissiveTex);
                            mat.SetFloat(PropHasSkinMask, 1f);
                        }
                        // Also set on _EmissionMap so ExtractSkinMasks can find it as fallback
                        if (mat.HasProperty(PropEmissionMap))
                        {
                            mat.SetTexture(PropEmissionMap, emissiveTex);
                        }
                    }
                }

                // Clear emission color/strength so mask doesn't glow
                if (mat.HasProperty(PropEmissionColor)) mat.SetColor(PropEmissionColor, Color.black);

                return mat;
            }

            private static Material CreateFlatMaterial()
            {
                var shader = Shader.Find("Ava-Twin/Stylized");
                if (shader == null) shader = Shader.Find("Ava-Twin/Stylized Builtin");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Unlit/Color");

                if (shader == null)
                {
                    throw new InvalidOperationException(
                        "No fallback runtime shader found for GLB materials.");
                }

                var mat = new Material(shader);

                // Subtle PBR response — soft specular sheen, gentle environment fill.
                // Matches Three.js MeshStandardMaterial at roughness ~0.85.
                if (mat.HasProperty(PropMetallic)) mat.SetFloat(PropMetallic, 0f);
                if (mat.HasProperty(PropSmoothness)) mat.SetFloat(PropSmoothness, 0.3f);
                if (mat.HasProperty(PropGlossMapScale)) mat.SetFloat(PropGlossMapScale, 0.3f);
                if (mat.HasProperty(PropSpecColor))
                    mat.SetColor(PropSpecColor, new Color(0.04f, 0.04f, 0.04f, 1f));
                if (mat.HasProperty(PropSpecGlossMap)) mat.SetTexture(PropSpecGlossMap, null);
                if (mat.HasProperty(PropEnvironmentReflections))
                    mat.SetFloat(PropEnvironmentReflections, 0.3f);
                if (mat.HasProperty(PropSpecularHighlights))
                    mat.SetFloat(PropSpecularHighlights, 1f);

                return mat;
            }
        }
    }
}
