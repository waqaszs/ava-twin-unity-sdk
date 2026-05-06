using UnityEngine;

namespace AvaTwin
{
    // ─────────────────────────────────────────────────────────────────────
    // Centralized extraction of (Application.identifier, Application.platform)
    // for Category C runtime API calls — bundle_id + platform are sent on
    // every request so the server can enforce per-app platform_identifiers
    // allowlists. Server runs in lenient mode initially (missing fields are
    // logged but allowed); once SDK adoption catches up, server flips to
    // strict enforcement. Keeping the extraction in one place ensures every
    // caller emits identical values and makes future changes (e.g. adding a
    // sdk_version field) a single-file edit.
    // ─────────────────────────────────────────────────────────────────────

    internal static class PlatformInfo
    {
        /// <summary>
        /// Returns (bundleId, platform) from Unity's runtime.
        ///   bundleId — Application.identifier (reverse-DNS, set in Player Settings)
        ///   platform — Application.platform.ToString() (e.g. "WebGLPlayer",
        ///              "Android", "IPhonePlayer", "OSXEditor", "WindowsPlayer")
        /// Both default to empty string rather than null so JSON serialization
        /// always emits the keys — server distinguishes "absent" vs "empty"
        /// for telemetry and prefers consistent presence.
        /// </summary>
        public static (string bundleId, string platform) Get()
        {
            var bundle = Application.identifier ?? string.Empty;
            var plat = Application.platform.ToString();
            return (bundle, plat);
        }
    }
}
