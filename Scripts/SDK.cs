using System.Threading.Tasks;
using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// Ava-Twin SDK public API.
    ///
    /// Two methods:
    ///   SDK.OpenCustomizerAsync()  — local player picks avatar via UI
    ///   SDK.LoadAvatar()           — stateless load, concurrent-safe for multiplayer
    /// </summary>
    public static class SDK
    {
        private static bool CheckPlatformSupport()
        {
#if UNITY_WEBGL || UNITY_ANDROID || UNITY_IOS || UNITY_EDITOR
            return true;
#else
            Debug.LogWarning("[Ava-Twin] Desktop standalone is not supported yet. Use WebGL or mobile builds.");
            return false;
#endif
        }

        /// <summary>
        /// Returns the internal CharacterLoader singleton. Used internally by OpenCustomizerAsync
        /// for the iframe/UI flow. Not recommended for direct use — prefer LoadAvatar() for loading.
        /// </summary>
        public static CharacterLoader GetLoader()
        {
            var loader = Object.FindObjectOfType<CharacterLoader>();
            if (loader == null)
            {
                var go = new GameObject("[AvaTwin]");
                go.hideFlags = HideFlags.HideInHierarchy;
                Object.DontDestroyOnLoad(go);
                loader = go.AddComponent<CharacterLoader>();
            }
            return loader;
        }

        /// <summary>
        /// Open the avatar customizer UI. Returns AvatarResult when user saves.
        /// Uses CharacterLoader internally for the iframe/native UI flow.
        /// For remote/additional players, use LoadAvatar() instead.
        ///
        /// Persistence: before opening, ensures a guest player session exists
        /// (<see cref="AvaTwinPlayer.EnsureAuthenticatedAsync"/>). After the
        /// user saves, the resulting avatar_id + skin tone are cached locally
        /// via <see cref="AvaTwinPlayer.SaveLastAvatarLocal"/> so the next
        /// session can restore via <see cref="LoadLastAvatarAsync"/>.
        /// </summary>
        public static async Task<AvatarResult> OpenCustomizerAsync()
        {
            if (!CheckPlatformSupport()) return null;

            // Best-effort guest identity — non-fatal if it fails (offline /
            // creds missing). The customizer falls back to anonymous mode.
            try { await AvaTwinPlayer.EnsureAuthenticatedAsync(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Ava-Twin] EnsureAuthenticatedAsync pre-customizer failed: {ex.Message}");
            }

            var loader = GetLoader();
            var tcs = new TaskCompletionSource<AvatarResult>();

            void OnLoaded(GameObject character)
            {
                loader.CharacterLoaded -= OnLoaded;
                loader.CharacterLoadFailed -= OnFailed;
                tcs.TrySetResult(new AvatarResult(character, loader.LastAvatarId, loader.SkinToneHex));
            }

            void OnFailed(string error)
            {
                loader.CharacterLoaded -= OnLoaded;
                loader.CharacterLoadFailed -= OnFailed;
                tcs.TrySetResult(null);
            }

            loader.CharacterLoaded += OnLoaded;
            loader.CharacterLoadFailed += OnFailed;
            loader.OpenCustomizer();

            return await tcs.Task;
        }

        /// <summary>
        /// Load an avatar by ID. Stateless — safe for concurrent multiplayer loads.
        /// Credentials loaded automatically from Resources/Credentials.
        /// </summary>
        public static async Task<AvatarResult> LoadAvatar(string avatarId, string skinToneHex = null)
        {
            if (!CheckPlatformSupport()) return null;

            var credentials = Resources.Load<Credentials>("Credentials");
            if (credentials == null || string.IsNullOrEmpty(credentials.AppId))
            {
                Debug.LogError("[Ava-Twin] Credentials not found. Place a Credentials asset in Resources/.");
                return null;
            }

            var config = Resources.Load<AvaTwinConfig>("AvaTwinConfig");
            var baseUrl = config != null ? config.baseApiUrl : "https://customizer.ava-twin.me";

            var effectiveSkinTone = skinToneHex ?? AvatarPipeline.GetDefaultSkinTone(avatarId);
            return await AvatarPipeline.LoadAsync(avatarId, credentials, baseUrl, effectiveSkinTone);
        }

        /// <summary>
        /// Load an avatar with explicit credentials. For consumers who manage credentials programmatically.
        /// </summary>
        public static async Task<AvatarResult> LoadAvatar(string avatarId, string appId, string apiKey, string baseUrl, string skinToneHex = null)
        {
            if (!CheckPlatformSupport()) return null;
            var effectiveSkinTone = skinToneHex ?? AvatarPipeline.GetDefaultSkinTone(avatarId);
            return await AvatarPipeline.LoadAsync(avatarId, appId, apiKey, baseUrl, effectiveSkinTone);
        }
    }
}
