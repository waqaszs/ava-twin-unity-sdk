using System;
using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// PlayerPrefs-backed storage for device identity, auth token, and cached
    /// avatar. Keys are namespaced with "ava_" to avoid collisions with host
    /// project PlayerPrefs usage.
    ///
    /// Upgrade path: swap this class's internals for iOS Keychain / Android
    /// Keystore in a future release for better token security. Public API
    /// stays identical.
    /// </summary>
    internal static class AvaTwinPlayerStorage
    {
        private const string DeviceIdKey = "ava_device_id";
        private const string PlayerTokenKey = "ava_player_token";
        private const string CachedAvatarKey = "ava_cached_avatar_json";

        /// <summary>
        /// Returns a stable per-device identifier. On platforms where
        /// <see cref="SystemInfo.deviceUniqueIdentifier"/> is stable across
        /// reinstalls (Android/iOS hardware IDs) we use it directly. If that
        /// value is unavailable or returns a special "n/a" sentinel (e.g. in
        /// the Editor some versions), we fall back to a PlayerPrefs-persisted
        /// GUID generated once.
        ///
        /// The result is guaranteed non-null/non-empty.
        /// </summary>
        public static string GetDeviceId()
        {
            var cached = PlayerPrefs.GetString(DeviceIdKey, null);
            if (!string.IsNullOrEmpty(cached)) return cached;

            string id = null;
            try
            {
                var sysId = SystemInfo.deviceUniqueIdentifier;
                if (!string.IsNullOrEmpty(sysId)
                    && sysId != SystemInfo.unsupportedIdentifier)
                {
                    id = sysId;
                }
            }
            catch
            {
                // SystemInfo occasionally throws in headless builds — fall through.
            }

            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString();

            PlayerPrefs.SetString(DeviceIdKey, id);
            PlayerPrefs.Save();
            return id;
        }

        /// <summary>Stored player JWT (string) or null if not signed in.</summary>
        public static string GetPlayerToken()
        {
            var t = PlayerPrefs.GetString(PlayerTokenKey, null);
            return string.IsNullOrEmpty(t) ? null : t;
        }

        public static void SetPlayerToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                PlayerPrefs.DeleteKey(PlayerTokenKey);
            }
            else
            {
                PlayerPrefs.SetString(PlayerTokenKey, token);
            }
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Local cache of the player's active avatar (variation_selections JSON).
        /// Used for offline startup — load instantly from here, then sync with
        /// the server in the background.
        /// </summary>
        public static string GetCachedAvatarJson()
        {
            var s = PlayerPrefs.GetString(CachedAvatarKey, null);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public static void SetCachedAvatarJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                PlayerPrefs.DeleteKey(CachedAvatarKey);
            }
            else
            {
                PlayerPrefs.SetString(CachedAvatarKey, json);
            }
            PlayerPrefs.Save();
        }

        /// <summary>Clears token + cached avatar. Keeps device_id (logout ≠ forget device).</summary>
        public static void ClearSession()
        {
            PlayerPrefs.DeleteKey(PlayerTokenKey);
            PlayerPrefs.DeleteKey(CachedAvatarKey);
            PlayerPrefs.Save();
        }
    }
}
