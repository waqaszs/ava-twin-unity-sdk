#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using System;

namespace AvaTwin.Setup
{
    [InitializeOnLoad]
    public static class AvaTwinNotificationChecker
    {
        private const string LastCheckKey = "AvaTwin_LastNotificationCheck";
        private const string DismissedKey = "AvaTwin_DismissedNotifications";
        private const string SdkVersion = "1.0.0";
        private const string NotificationUrl = "https://customizer.ava-twin.me/api/sdk-notifications";
        private const double CheckIntervalHours = 24.0;

        static AvaTwinNotificationChecker()
        {
            // Ensure GLTFAST_DISABLE_BURST is set to avoid Burst version conflicts
            EnsureBurstDisabled();

            // Only check once per day
            string lastCheck = EditorPrefs.GetString(LastCheckKey, "");
            if (!string.IsNullOrEmpty(lastCheck))
            {
                if (DateTime.TryParse(lastCheck, out DateTime last))
                {
                    if ((DateTime.UtcNow - last).TotalHours < CheckIntervalHours)
                        return;
                }
            }

            EditorApplication.delayCall += CheckNotifications;
        }

        private static void CheckNotifications()
        {
            var request = UnityWebRequest.Get($"{NotificationUrl}?v={SdkVersion}");
            var op = request.SendWebRequest();

            EditorApplication.update += PollRequest;

            void PollRequest()
            {
                if (!op.isDone) return;
                EditorApplication.update -= PollRequest;

                EditorPrefs.SetString(LastCheckKey, DateTime.UtcNow.ToString("o"));

                if (request.result != UnityWebRequest.Result.Success)
                {
                    request.Dispose();
                    return; // Silent fail — don't bother developers with network errors
                }

                try
                {
                    string json = request.downloadHandler.text;
                    var response = JsonUtility.FromJson<NotificationResponse>(json);

                    if (response.notifications != null)
                    {
                        string dismissed = EditorPrefs.GetString(DismissedKey, "");

                        foreach (var n in response.notifications)
                        {
                            if (dismissed.Contains(n.id)) continue;

                            string prefix = n.type == "critical" ? "[CRITICAL] " :
                                           n.type == "warning" ? "[WARNING] " :
                                           n.type == "update" ? "[UPDATE] " : "";

                            if (n.type == "critical" || n.type == "warning")
                            {
                                Debug.LogWarning($"[Ava-Twin] {prefix}{n.title}: {n.message}");
                            }
                            else
                            {
                                Debug.Log($"[Ava-Twin] {prefix}{n.title}: {n.message}");
                            }

                            // Dismiss after showing
                            dismissed += n.id + ";";
                        }

                        EditorPrefs.SetString(DismissedKey, dismissed);
                    }

                    // Check for newer SDK version
                    if (!string.IsNullOrEmpty(response.latest_version) &&
                        string.Compare(response.latest_version, SdkVersion, StringComparison.Ordinal) > 0)
                    {
                        Debug.Log($"[Ava-Twin] A newer SDK version is available: v{response.latest_version}. " +
                            "Download from https://github.com/waqaszs/ava-twin-sdk/releases");
                    }
                }
                catch { /* Silent fail */ }
                finally
                {
                    request.Dispose();
                }
            }
        }

        private static void EnsureBurstDisabled()
        {
            var target = EditorUserBuildSettings.selectedBuildTargetGroup;
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(target);
            if (!defines.Contains("GLTFAST_DISABLE_BURST"))
            {
                defines = string.IsNullOrEmpty(defines)
                    ? "GLTFAST_DISABLE_BURST"
                    : defines + ";GLTFAST_DISABLE_BURST";
                PlayerSettings.SetScriptingDefineSymbolsForGroup(target, defines);
            }
        }

        [Serializable]
        private class NotificationResponse
        {
            public Notification[] notifications;
            public string latest_version;
        }

        [Serializable]
        private class Notification
        {
            public string id;
            public string title;
            public string message;
            public string type;
        }
    }
}
#endif
