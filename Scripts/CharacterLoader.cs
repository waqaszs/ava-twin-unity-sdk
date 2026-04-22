using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Serialization;


namespace AvaTwin
{

[AddComponentMenu("")]
[DisallowMultipleComponent]
public sealed class CharacterLoader : MonoBehaviour
{
    // ── Mesh / object name constants ──
    private const string MeshNameBody = "Body";
    private const string MeshNameHead = "head";
    private const string MeshNameAvatarHead = "avatar_head";

    // ── Protocol constants ──
    private const string CustomizerCancelPayload = "__ava_twin_cancelled__";

    // ── Cache directory names ──
    private const string CacheRootFolder = "AvaTwinCache";
    private const string CacheCharactersFolder = "Characters";

    // ── Required ─────────────────────────────────────────────────────
    [Header("Required")]
    [SerializeField] private Credentials credentials;

    // ── Avatar ───────────────────────────────────────────────────────
    [Header("Avatar")]
    [Tooltip("Optional. If set, loads this specific avatar instead of opening the customizer. Leave empty to use the customizer flow.")]
    [SerializeField] private string initialAvatarId = "";

    // ── Caching ──────────────────────────────────────────────────────
    [Header("Caching")]
    [SerializeField] private bool useCache = true;
    [Tooltip("How long a cached character stays valid (minutes).")]
    [SerializeField] private float cacheTtlMinutes = 60f;
    [Tooltip("If true, using the cache refreshes its TTL (sliding expiration).")]
    [SerializeField] private bool refreshTtlOnUse = true;
    [Tooltip("Signed URLs often change tokens. If true, cache key ignores the query string.")]
    [SerializeField] private bool cacheIgnoreQueryString = true;

    // ── Advanced — usually leave defaults ────────────────────────────
    [Header("Advanced — usually leave defaults")]
    [Tooltip("GLB model URL. Set automatically by the Customizer flow — not usually set manually.")]
    [SerializeField] private string glbUrl = string.Empty;
    [Tooltip("Skin tone tint applied to body/head materials. Set automatically from head variant selection.")]
    [SerializeField] private string skinToneHex = "#FFDFC4";
    [FormerlySerializedAs("webCustomizerUrl")]
    [Tooltip("Base URL for the Ava-Twin customizer. Change only if self-hosting.")]
    [SerializeField] private string customizerUrl = "https://customizer.ava-twin.me/customize";
#pragma warning disable CS0414 // Used in UNITY_WEBGL builds only
    [FormerlySerializedAs("webCustomizerAllowedOrigin")]
    [Tooltip("Security check for iframe postMessage origin. Change only if self-hosting.")]
    [SerializeField] private string customizerAllowedOrigin = "https://customizer.ava-twin.me";
    [Tooltip("Debug: adds a button in WebGL iframe overlay that sends a dummy URL back to Unity.")]
    [SerializeField] private bool enableDummyIframeReturnButton = false;
#pragma warning restore CS0414
    [Tooltip("Optional parent for instantiated mobile customizer UI.")]
    [SerializeField] private Transform mobileCustomizerParent;

    /// <summary>
    /// Builds the avatar-resolve endpoint from the customizer base URL (no hardcoded Supabase URLs).
    /// </summary>
    private string GetBaseUrl()
    {
        var config = Resources.Load<AvaTwinConfig>("AvaTwinConfig");
        if (config != null && !string.IsNullOrWhiteSpace(config.baseApiUrl))
            return config.baseApiUrl.TrimEnd('/');
        return customizerUrl.TrimEnd('/').Replace("/customize", "");
    }

    /// <summary>Fired after a character is successfully loaded and instantiated.</summary>
    public event Action<GameObject> CharacterLoaded;
    /// <summary>Fired after any successful visual character load (preview or final).</summary>
    public event Action CharacterVisualLoaded;

    /// <summary>
    /// Invoked when character loading fails. The parameter is the error message.
    /// </summary>
    public event Action<string> CharacterLoadFailed;

    /// <summary>Fires with status text during loading stages.</summary>
    public event Action<string> LoadingStatusChanged;

    /// <summary>Returns true if the customizer iframe overlay is currently open.</summary>
    public bool IsCustomizerOpen { get; private set; }

    private CancellationTokenSource _cts;
    private RuntimeAnimatorController _cachedAnimatorController;
    private GameObject _instanceRoot;
    private bool _isLoading;

    // Runtime credential overrides (take precedence over ScriptableObject)
    private string _runtimeAppId;
    private string _runtimeApiKey;

    private Avatar avatar;
    private AvaTwinMobileCustomizer _mobileCustomizerInstance;

    public string GlbUrl
    {
        get => glbUrl;
        set => glbUrl = value;
    }

    public string SkinToneHex => skinToneHex;

    /// <summary>The opaque avatar ID from the last customizer save. Use for network sync.</summary>
    public string LastAvatarId { get; private set; }

    /// <summary>Sets credentials at runtime (overrides the ScriptableObject asset).</summary>
    public void SetCredentials(string appId, string apiKey)
    {
        _runtimeAppId = appId;
        _runtimeApiKey = apiKey;
    }

    /// <summary>Programmatically closes the customizer iframe overlay.</summary>
    public void CloseCustomizer()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        AvaTwin_CloseFullscreenIframe();
#endif
        if (_mobileCustomizerInstance != null)
            _mobileCustomizerInstance.gameObject.SetActive(false);
        IsCustomizerOpen = false;
    }

    /// <summary>Returns the loaded character GameObject, or null if not yet loaded.</summary>
    public GameObject GetLoadedCharacter() => _instanceRoot;
    public Avatar GetLoadedCharacterAvatar() => avatar;

    /// <summary>
    /// Updates the current GLB URL and starts loading immediately.
    /// Useful for native/mobile customizer flows that do not use the WebGL iframe callback.
    /// </summary>
    public void LoadCharacterFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            var msg = "Cannot load character — no URL provided.";
            Debug.LogError("[Ava-Twin] Failed to load character: missing URL.");
            CharacterLoadFailed?.Invoke(msg);
            return;
        }

        GlbUrl = url;
        LoadCharacter(emitLoadedEvent: true);
    }

    /// <summary>
    /// Loads a character URL as visual preview only.
    /// This intentionally does not emit CharacterLoaded, so gameplay/controller setup is not triggered.
    /// </summary>
    public void LoadPreviewCharacterFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            var msg = "Cannot preview character — no URL provided.";
            Debug.LogError("[Ava-Twin] Failed to preview character: missing URL.");
            CharacterLoadFailed?.Invoke(msg);
            return;
        }

        GlbUrl = url;
        LoadCharacter(emitLoadedEvent: false);
    }

    public void SetSkinToneHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;

        skinToneHex = hex;
        // Re-apply skin tone to the already-loaded avatar via AvatarPipeline
        if (_instanceRoot != null)
            AvatarPipeline.ApplyMaterials(_instanceRoot, skinToneHex);
    }

    private async void LoadCharacter(bool emitLoadedEvent = true)
    {
        try
        {
            var character = await LoadAndInstantiateAsync();
            if (character != null)
            {
                CharacterVisualLoaded?.Invoke();
            }
            if (character != null && emitLoadedEvent)
            {
                CharacterLoaded?.Invoke(character);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[Ava-Twin] Failed to load character.");
            CharacterLoadFailed?.Invoke(ex.Message);
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// Loads a GLB from the current GlbUrl, instantiates it, applies materials and humanoid config.
    /// Delegates download, instantiation, materials, and humanoid setup to AvatarPipeline.
    /// Retains disk caching and legacy animation handling unique to CharacterLoader.
    /// </summary>
    public async Task<GameObject> LoadAndInstantiateAsync()
    {
        if (string.IsNullOrWhiteSpace(glbUrl))
        {
            var msg = "GLB URL is empty — cannot load character.";
            Debug.LogError("[Ava-Twin] Cannot load character: GLB URL is empty.");
            CharacterLoadFailed?.Invoke(msg);
            return null;
        }

        CancelLoad();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Try disk cache first
        byte[] glbBytes = null;
        if (useCache)
        {
            glbBytes = TryLoadCachedBytes(glbUrl);
        }

        if (glbBytes == null)
        {
            try
            {
                glbBytes = await AvatarPipeline.DownloadGlbAsync(glbUrl, token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                var msg = $"Download failed: {ex.Message}";
                Debug.LogError("[Ava-Twin] Failed to download avatar.");
                CharacterLoadFailed?.Invoke(msg);
                return null;
            }

            if (useCache)
            {
                TrySaveCachedBytes(glbUrl, glbBytes);
            }
        }

        LoadingStatusChanged?.Invoke("Building character...");

        // Instantiate GLB via AvatarPipeline
        GameObject pipelineRoot;
        try
        {
            pipelineRoot = await AvatarPipeline.InstantiateGlbAsync(glbBytes, token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            var msg = $"GLB load/instantiation failed: {ex.Message}";
            Debug.LogError("[Ava-Twin] Failed to load/instantiate GLB data.");
            CharacterLoadFailed?.Invoke(msg);
            return null;
        }

        // Destroy old instance
        if (_instanceRoot != null)
        {
            Destroy(_instanceRoot);
            _instanceRoot = null;
        }

        // Rename to match CharacterLoader convention
        pipelineRoot.name = "Character";
        _instanceRoot = pipelineRoot;

        // Configure humanoid bones via AvatarPipeline
        LoadingStatusChanged?.Invoke("Configuring humanoid...");
        avatar = AvatarPipeline.ConfigureHumanoid(_instanceRoot);

        // If humanoid configured, remove any legacy Animation component that glTFast may have added
        if (avatar != null)
        {
            var legacyAnim = _instanceRoot.GetComponentInChildren<Animation>(true);
            if (legacyAnim != null)
                Destroy(legacyAnim);
        }

        // Apply materials (skin mask extraction + flat materials + skin tone) via AvatarPipeline
        AvatarPipeline.ApplyMaterials(_instanceRoot, skinToneHex);

        _instanceRoot.transform.position = Vector3.zero;
        _instanceRoot.transform.rotation = Quaternion.identity;

        return _instanceRoot;
    }

    public void CancelLoad()
    {
        if (_cts == null) return;
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    /// <summary>
    /// Checks all 30 finger HumanBodyBones via animator.GetBoneTransform() and logs how many are mapped.
    /// Call after avatar assignment to verify finger bone coverage.
    /// </summary>
    public static void VerifyFingerMapping(Animator animator)
    {
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            Debug.LogWarning("[Ava-Twin] VerifyFingerMapping: Animator has no valid humanoid avatar.");
            return;
        }

        var fingerBones = new HumanBodyBones[]
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal,

            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal
        };

        int mapped = 0;
        for (int i = 0; i < fingerBones.Length; i++)
        {
            var t = animator.GetBoneTransform(fingerBones[i]);
            if (t != null) mapped++;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("[Ava-Twin] Finger bone mapping check complete.");
#endif
        if (mapped < 30)
        {
            for (int i = 0; i < fingerBones.Length; i++)
            {
                var t = animator.GetBoneTransform(fingerBones[i]);
                if (t == null)
                    Debug.LogWarning("[Ava-Twin] Missing finger bone detected.");
            }
        }
    }
#endif

    private byte[] TryLoadCachedBytes(string url)
    {
        var ttlSeconds = Mathf.Max(0f, cacheTtlMinutes) * 60f;
        if (ttlSeconds <= 0f) return null;

        var path = GetCacheFilePath(url);
        if (!File.Exists(path)) return null;

        try
        {
            var ageSeconds = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            if (ageSeconds > ttlSeconds)
            {
                File.Delete(path);
                return null;
            }

            var bytes = File.ReadAllBytes(path);

            if (refreshTtlOnUse)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }

            return bytes;
        }
        catch
        {
            // Corrupt/locked cache file: treat as miss.
            return null;
        }
    }

    private void TrySaveCachedBytes(string url, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return;

        var dir = GetCacheDirectory();
        var path = GetCacheFilePath(url);

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // Cache is best-effort. Ignore failures.
        }
    }

    private string GetCacheDirectory()
    {
        return Path.Combine(Application.persistentDataPath, CacheRootFolder, CacheCharactersFolder);
    }

    private string GetCacheFilePath(string url)
    {
        var dir = GetCacheDirectory();
        var key = ComputeCacheKey(url);
        return Path.Combine(dir, $"{key}.glb");
    }

    private string ComputeCacheKey(string url)
    {
        var keySource = url ?? string.Empty;

        if (cacheIgnoreQueryString)
        {
            try
            {
                var uri = new Uri(keySource);
                keySource = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
            }
            catch
            {
                // Ignore parsing issues; fall back to full string.
            }
        }

        using (var sha = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(keySource);
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }


    [System.Obsolete("Use OpenCustomizer() instead.")]
    public void InitializeWebCustomizer() => OpenCustomizer();

    public void OpenCustomizer()
    {
        if (_isLoading) return;
        _isLoading = true;

        // If an initial avatar ID is set, load it directly instead of opening customizer
        if (!string.IsNullOrWhiteSpace(initialAvatarId))
        {
            ResolveAndLoadAvatarDirect(initialAvatarId);
            return;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // WebGL build: iframe customizer
        if (string.IsNullOrWhiteSpace(customizerUrl))
        {
            Debug.LogWarning("[Ava-Twin] Customizer URL is empty.");
            _isLoading = false;
            return;
        }

        var finalCustomizerUrl = BuildCustomizerUrl();
        IsCustomizerOpen = true;
        AvaTwin_SetIframeMessageTarget(gameObject.name, nameof(OnCustomizerUrlReceived), customizerAllowedOrigin);
        AvaTwin_SetIframeDebugDummy(enableDummyIframeReturnButton, glbUrl);
        AvaTwin_OpenFullscreenIframe(finalCustomizerUrl);
#elif UNITY_ANDROID || UNITY_IOS
        // Mobile (editor or device): native customizer UI
        OpenMobileCustomizer();
#elif UNITY_EDITOR
        // Editor with WebGL target: random load for quick testing
        EditorLoadRandomAvatar();
#else
        // Desktop standalone: not supported yet
        Debug.LogWarning("[Ava-Twin] Desktop standalone is not supported yet. Use WebGL or mobile builds.");
        CharacterLoadFailed?.Invoke("Desktop standalone is not supported yet.");
        _isLoading = false;
#endif
    }

    /// <summary>
    /// Picks a random head/top/bottom/shoes combination, saves it via player-avatar-save
    /// to get a combo ID, then resolves and loads the resulting GLB.
    /// Useful for quick testing in the Editor without the customizer iframe.
    /// </summary>
    public async void EditorLoadRandomAvatar()
    {
        try
        {
            LoadingStatusChanged?.Invoke("Connecting...");

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[Ava-Twin] Loading random avatar in editor mode...");
#endif

            // Mint a session token for the library fetch and save request
            var creds = GetCredentials();
            var appId  = !string.IsNullOrWhiteSpace(_runtimeAppId)  ? _runtimeAppId  : creds?.AppId;
            var apiKey = !string.IsNullOrWhiteSpace(_runtimeApiKey) ? _runtimeApiKey : creds?.ApiKey;

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(apiKey))
            {
                Debug.LogError("[Ava-Twin] Cannot save editor avatar — missing app credentials.");
                CharacterLoadFailed?.Invoke("Missing app credentials for editor avatar save.");
                return;
            }

            var baseUrl = GetBaseUrl();
            string sessionToken;
            try
            {
                sessionToken = await AvatarPipeline.MintTokenAsync(appId, apiKey, baseUrl);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Ava-Twin] Network error while minting session token for editor avatar: {ex.Message}");
                CharacterLoadFailed?.Invoke(ex.Message);
                return;
            }

            // Fetch avatar library to get public_ids per category
            LoadingStatusChanged?.Invoke("Fetching avatar library...");
            var libraryUrl = baseUrl.TrimEnd('/') + "/api/avatar-library";
            using var libReq = new UnityWebRequest(libraryUrl, "GET");
            libReq.downloadHandler = new DownloadHandlerBuffer();
            libReq.SetRequestHeader("Authorization", $"Bearer {sessionToken}");

            var libOp = libReq.SendWebRequest();
            while (!libOp.isDone)
                await Task.Yield();

            if (libReq.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Ava-Twin] Failed to fetch avatar library: {libReq.error}");
                CharacterLoadFailed?.Invoke("Failed to fetch avatar library.");
                return;
            }

            // Parse library — response is { library: { generic: { head: [...], top: [...], ... } } }
            // JsonUtility can't handle nested dicts, so parse the "generic" gender categories directly
            var libraryJson = libReq.downloadHandler.text;
            var libraryWrapper = JsonUtility.FromJson<AvatarLibraryResponse>(libraryJson);

            var headIds = new List<string>();
            var topIds = new List<string>();
            var bottomIds = new List<string>();
            var shoesIds = new List<string>();
            var headSkinTones = new Dictionary<string, string>();

            if (libraryWrapper?.library?.generic != null)
            {
                var g = libraryWrapper.library.generic;
                if (g.head != null) foreach (var item in g.head) { headIds.Add(item.variation_id); if (!string.IsNullOrEmpty(item.default_skin_tone)) headSkinTones[item.variation_id] = item.default_skin_tone; }
                if (g.top != null) foreach (var item in g.top) topIds.Add(item.variation_id);
                if (g.bottom != null) foreach (var item in g.bottom) bottomIds.Add(item.variation_id);
                if (g.shoes != null) foreach (var item in g.shoes) shoesIds.Add(item.variation_id);
            }

            if (headIds.Count == 0 || topIds.Count == 0 || bottomIds.Count == 0 || shoesIds.Count == 0)
            {
                Debug.LogError("[Ava-Twin] Avatar library has no items for some categories.");
                CharacterLoadFailed?.Invoke("Avatar library incomplete.");
                return;
            }

            var rng = new System.Random();
            var head = headIds[rng.Next(headIds.Count)];
            var top = topIds[rng.Next(topIds.Count)];
            var bottom = bottomIds[rng.Next(bottomIds.Count)];
            var shoe = shoesIds[rng.Next(shoesIds.Count)];

            // Find the selected head's default skin tone from the library
            skinToneHex = "#FFDFC4"; // fallback
            if (headSkinTones.TryGetValue(head, out var headTone))
            {
                skinToneHex = headTone;
            }

            // Call sdk-avatar-save to get the combo ID
            LoadingStatusChanged?.Invoke("Saving avatar...");
            var saveUrl = baseUrl.TrimEnd('/') + "/api/sdk/avatar-save";
            var saveBody = JsonUtility.ToJson(new PlayerAvatarSaveRequest
            {
                variation_selections = new VariationSelections
                {
                    gender = "generic",
                    head = head,
                    top = top,
                    bottom = bottom,
                    shoes = shoe,
                    skin_tone = skinToneHex
                },
                // Use the real player_id if the host has initialized AvaTwinPlayer,
                // otherwise fall back to "editor_test" so the Editor avatar-save
                // still works without requiring explicit identity setup.
                player_id = AvaTwinPlayer.PlayerId ?? "editor_test"
            });

            using var saveReq = new UnityWebRequest(saveUrl, "POST");
            saveReq.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(saveBody));
            saveReq.downloadHandler = new DownloadHandlerBuffer();
            saveReq.SetRequestHeader("Content-Type", "application/json");
            saveReq.SetRequestHeader("Authorization", $"Bearer {sessionToken}");

            var op = saveReq.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (saveReq.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Ava-Twin] Failed to save editor avatar: {saveReq.error}");
                CharacterLoadFailed?.Invoke("Failed to create avatar for editor testing.");
                return;
            }

            var saveResponse = JsonUtility.FromJson<PlayerAvatarSaveResponse>(saveReq.downloadHandler.text);
            var avatarId = saveResponse.avatar_id;
            LastAvatarId = avatarId;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[Ava-Twin] Editor avatar saved with ID: {avatarId}");
#endif

            // ResolveAndLoadAvatar mints its own token internally — acceptable extra round-trip for editor
            await ResolveAndLoadAvatar(avatarId);
        }
        catch (Exception ex)
        {
            Debug.LogError("[Ava-Twin] Failed to load random avatar in editor.");
            CharacterLoadFailed?.Invoke(ex.Message);
        }
    }

    [Serializable]
    private class VariationSelections
    {
        public string gender;
        public string head;
        public string top;
        public string bottom;
        public string shoes;
        public string skin_tone;
    }

    [Serializable]
    private class PlayerAvatarSaveRequest
    {
        public VariationSelections variation_selections;
        public string player_id;
    }

    [Serializable]
    private class PlayerAvatarSaveResponse
    {
        public string avatar_id;
        public string status;
    }

    [Serializable]
    private class AvatarLibraryResponse
    {
        public AvatarLibraryGenders library;
    }

    [Serializable]
    private class AvatarLibraryGenders
    {
        public AvatarLibraryCategories generic;
    }

    [Serializable]
    private class AvatarLibraryCategories
    {
        public AvatarLibraryItem[] head;
        public AvatarLibraryItem[] top;
        public AvatarLibraryItem[] bottom;
        public AvatarLibraryItem[] shoes;
    }

    [Serializable]
    private class AvatarLibraryItem
    {
        public string variation_id;
        public string display_name;
        public string default_skin_tone;
    }

    /// <summary>
    /// Resolves an explicit avatar_id via the avatar-resolve endpoint and loads the GLB.
    /// </summary>
    public async void ResolveAndLoadAvatarDirect(string avatarId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(avatarId))
            {
                Debug.LogError("[Ava-Twin] Cannot resolve avatar: missing avatar identifier.");
                CharacterLoadFailed?.Invoke("avatarId is null or empty.");
                return;
            }

            LastAvatarId = avatarId;

            // Derive skin tone from head component if present
            skinToneHex = AvatarPipeline.GetDefaultSkinTone(avatarId);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[Ava-Twin] Resolving avatar directly...");
#endif

            await ResolveAndLoadAvatar(avatarId);
        }
        catch (Exception ex)
        {
            Debug.LogError("[Ava-Twin] Failed to resolve and load avatar.");
            CharacterLoadFailed?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Full pipeline: mint token, resolve avatar, download GLB, instantiate, apply materials, configure humanoid.
    /// Delegates all steps to AvatarPipeline, but retains CharacterLoader-specific state management
    /// (GlbUrl, events, disk cache, loading status).
    /// </summary>
    private async Task ResolveAndLoadAvatar(string avatarId)
    {
        var creds = GetCredentials();
        var appId  = !string.IsNullOrWhiteSpace(_runtimeAppId)  ? _runtimeAppId  : creds?.AppId;
        var apiKey = !string.IsNullOrWhiteSpace(_runtimeApiKey) ? _runtimeApiKey : creds?.ApiKey;

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(apiKey))
        {
            Debug.LogError("[Ava-Twin] Cannot resolve avatar — missing app credentials. Assign Credentials or call SetCredentials() first.");
            CharacterLoadFailed?.Invoke("Missing app credentials for avatar resolve.");
            return;
        }

        var baseUrl = GetBaseUrl();

        // Step 1: Mint a session token (delegated to AvatarPipeline)
        LoadingStatusChanged?.Invoke("Connecting...");
        string sessionToken;
        try
        {
            sessionToken = await AvatarPipeline.MintTokenAsync(appId, apiKey, baseUrl);
        }
        catch (Exception ex)
        {
            Debug.LogError("[Ava-Twin] Network error while minting session token.");
            CharacterLoadFailed?.Invoke(ex.Message);
            return;
        }

        // Step 2: Resolve avatar (delegated to AvatarPipeline)
        LoadingStatusChanged?.Invoke("Resolving avatar...");
        AvatarPipeline.ResolveResult resolveResult;
        try
        {
            resolveResult = await AvatarPipeline.ResolveAvatarAsync(avatarId, sessionToken, baseUrl);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Ava-Twin] Network error while resolving avatar: {ex.Message}");
            CharacterLoadFailed?.Invoke(ex.Message);
            return;
        }

        // Update skin tone from server if provided
        if (!string.IsNullOrEmpty(resolveResult.SkinTone))
            skinToneHex = resolveResult.SkinTone;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("[Ava-Twin] Avatar resolved successfully.");
#endif

        // Step 3: Set GlbUrl (Atlanta reads this for Photon) and load the GLB
        GlbUrl = resolveResult.GlbUrl;
        LoadingStatusChanged?.Invoke("Downloading model...");
        LoadCharacter();
    }

    private void OpenMobileCustomizer()
    {
        if (_mobileCustomizerInstance == null)
        {
            var prefab = Resources.Load<AvaTwinMobileCustomizer>("AvaTwinMobileCustomizer");
            if (prefab == null)
            {
                Debug.LogError("[Ava-Twin] Mobile customizer prefab not found at Resources/AvaTwinMobileCustomizer. Ensure the SDK Resources are imported.");
                CharacterLoadFailed?.Invoke("Mobile customizer prefab missing.");
                _isLoading = false;
                return;
            }

            _mobileCustomizerInstance = mobileCustomizerParent != null
                ? Instantiate(prefab, mobileCustomizerParent)
                : Instantiate(prefab);
        }

        _mobileCustomizerInstance.Configure(null, this);
        _mobileCustomizerInstance.OnAvatarUrlReady -= OnMobileCustomizerAvatarUrlReady;
        _mobileCustomizerInstance.OnError -= OnMobileCustomizerError;
        _mobileCustomizerInstance.OnAvatarUrlReady += OnMobileCustomizerAvatarUrlReady;
        _mobileCustomizerInstance.OnError += OnMobileCustomizerError;

        // Hide the demo scene's WebGL customizer UI (named "AvaTwin Canvas")
        // while the mobile customizer is active. No-op in host projects that
        // don't have it.
        var webglDemoCanvas = GameObject.Find("AvaTwin Canvas");
        if (webglDemoCanvas != null) webglDemoCanvas.SetActive(false);

        EnsureEventSystemExists();

        _mobileCustomizerInstance.gameObject.SetActive(true);
        IsCustomizerOpen = true;
        _mobileCustomizerInstance.Initialize();
    }

    /// <summary>
    /// The customizer UI is a uGUI Canvas; it needs a scene EventSystem to
    /// receive pointer / touch input. Host projects often don't have one
    /// (Atlanta did, marcus didn't). Auto-create one if missing so buttons
    /// like Save &amp; Continue are always clickable.
    /// </summary>
    private static void EnsureEventSystemExists()
    {
        if (UnityEngine.EventSystems.EventSystem.current != null) return;
        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() != null) return;

        var esGo = new GameObject("EventSystem");
        esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        // Project is on the new Input System only.
        esGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        // Legacy input or both — StandaloneInputModule covers mouse / touch.
        esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
#endif
    }

    private void OnMobileCustomizerAvatarUrlReady(string url)
    {
        // Read the avatar_id from the mobile customizer controller
        // (set before this event fires) so multiplayer sync (e.g. Photon) works.
        if (_mobileCustomizerInstance != null)
        {
            LastAvatarId = _mobileCustomizerInstance.LastAvatarId;
            _mobileCustomizerInstance.gameObject.SetActive(false);
        }

        OnCustomizerUrlReceived(url);
    }

    private void OnMobileCustomizerError(string error)
    {
        Debug.LogWarning("[Ava-Twin] Mobile customizer encountered an error.");
        if (string.Equals(error, "Customizer closed without saving.", StringComparison.Ordinal))
            IsCustomizerOpen = false;
    }

    private string BuildCustomizerUrl()
    {
        var creds = GetCredentials();
        if (creds == null)
        {
            Debug.LogWarning("[Ava-Twin] Customizer credentials not found. Assign a Credentials asset to the CharacterLoader component.");
            return customizerUrl;
        }

        var appId = !string.IsNullOrWhiteSpace(_runtimeAppId) ? _runtimeAppId : creds.AppId;
        var apiKey = !string.IsNullOrWhiteSpace(_runtimeApiKey) ? _runtimeApiKey : creds.ApiKey;

        if (string.IsNullOrWhiteSpace(appId) && string.IsNullOrWhiteSpace(apiKey))
            return customizerUrl;

        // Pass credentials in the hash fragment (NOT query string) so they are never
        // sent to the server or logged in access logs. The customizer reads from the hash.
        var sb = new StringBuilder(customizerUrl);
        sb.Append("#");
        var separator = "";

        if (!string.IsNullOrWhiteSpace(appId))
        {
            sb.Append(separator);
            sb.Append("appId=");
            sb.Append(Uri.EscapeDataString(appId));
            separator = "&";
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            sb.Append(separator);
            sb.Append("apiKey=");
            sb.Append(Uri.EscapeDataString(apiKey));
        }

        return sb.ToString();
    }

    /// <summary>Returns the Credentials ScriptableObject assigned to this loader (or null).</summary>
    public Credentials GetCredentials()
    {
        if (credentials == null)
            credentials = Resources.Load<Credentials>("Credentials");
        return credentials;
    }

    public void OnCustomizerUrlReceived(string payload)
    {
        IsCustomizerOpen = false;

        // Handle cancel — player closed customizer without saving
        if (payload == CustomizerCancelPayload)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[Ava-Twin] Customizer was closed without saving.");
#endif
            CharacterLoadFailed?.Invoke("Customizer closed without saving.");
            return;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("[Ava-Twin] Received avatar selection from customizer.");
#endif

        // The jslib extracts glb_url from structured messages, so payload should be a URL.
        // But handle the case where it might be a JSON string (fallback).
        var url = payload;
        if (payload != null && payload.StartsWith("{"))
        {
            try
            {
                var json = JsonUtility.FromJson<AvatarSavedMessage>(payload);
                if (!string.IsNullOrEmpty(json.glb_url))
                {
                    url = json.glb_url;
                }
                if (!string.IsNullOrEmpty(json.avatar_id))
                    LastAvatarId = json.avatar_id;
                if (!string.IsNullOrEmpty(json.skin_tone))
                {
                    SetSkinToneHex(json.skin_tone);
                }
            }
            catch (System.Exception ex)
            {
                // Non-JSON payload — treat as raw URL (no log in production)
            }
        }

        if (!string.IsNullOrEmpty(url))
        {
            GlbUrl = url;
        }
        LoadCharacter();
    }

    [Serializable]
    private class AvatarSavedMessage
    {
        public string type;
        public string avatar_id;
        public string glb_url;
        public string skin_tone;
    }

    private void OnDestroy()
    {
        CancelLoad();
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void AvaTwin_SetIframeMessageTarget(string gameObjectName, string methodName, string allowedOrigin);

    [DllImport("__Internal")]
    private static extern void AvaTwin_SetIframeDebugDummy(bool enabled, string dummyUrl);

    [DllImport("__Internal")]
    private static extern void AvaTwin_OpenFullscreenIframe(string url);

    [DllImport("__Internal")]
    private static extern void AvaTwin_CloseFullscreenIframe();
#endif
}

} // namespace AvaTwin
