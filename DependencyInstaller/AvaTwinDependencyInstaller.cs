/*
 * Ava-Twin SDK — Automatic Dependency Installer
 *
 * This script lives in its own assembly (AvaTwin.DependencyInstaller.asmdef)
 * with ZERO external references. It compiles even when glTFast/Newtonsoft
 * are missing, so it can install them before the main SDK scripts compile.
 *
 * How it works:
 * 1. User imports .unitypackage → main SDK scripts are silently skipped (defineConstraints)
 * 2. BUT this script compiles (isolated assembly, no external deps)
 * 3. [InitializeOnLoad] runs → checks installed packages via Client.List()
 * 4. Installs ALL missing packages in one batch via Client.AddAndRemove()
 * 5. Progress bar persists across domain reload via SessionState
 * 6. After reload, Events.registeredPackages confirms completion → progress bar clears
 * 7. defineConstraints are satisfied → main SDK compiles
 */

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace AvaTwin.Setup
{
    [InitializeOnLoad]
    public static class AvaTwinDependencyInstaller
    {
        private static readonly string[][] RequiredPackages = new string[][]
        {
            new[] { "com.unity.cloud.gltfast", "5.2.0" },
            new[] { "com.unity.nuget.newtonsoft-json", "3.2.1" },
        };

        private const string SessionKey_AllVerified = "AvaTwin_AllDepsVerified";
        private const string SessionKey_Installing = "AvaTwin_Installing";

        static AvaTwinDependencyInstaller()
        {
            if (SessionState.GetBool(SessionKey_AllVerified, false))
                return;

            // Re-subscribe every domain reload so we catch completion
            Events.registeredPackages += OnPackagesRegistered;

            // If we were installing before domain reload, re-show progress bar
            // and let the registeredPackages event handle clearing it
            if (SessionState.GetBool(SessionKey_Installing, false))
            {
                EditorUtility.DisplayProgressBar(
                    "Ava-Twin SDK",
                    "Resolving packages...",
                    0.7f);
                // Check if deps are now present after this reload
                EditorApplication.delayCall += VerifyAfterReload;
                return;
            }

            EditorApplication.delayCall += CheckAndInstall;
        }

        private static void VerifyAfterReload()
        {
            var listRequest = Client.List(true);
            EditorApplication.update += Poll;

            void Poll()
            {
                if (!listRequest.IsCompleted) return;
                EditorApplication.update -= Poll;

                if (listRequest.Status != StatusCode.Success) return;

                var installed = new HashSet<string>(
                    listRequest.Result.Select(p => p.name));

                bool allPresent = true;
                foreach (var pkg in RequiredPackages)
                {
                    if (!installed.Contains(pkg[0]))
                    {
                        allPresent = false;
                        break;
                    }
                }

                if (allPresent)
                {
                    SessionState.SetBool(SessionKey_Installing, false);
                    SessionState.SetBool(SessionKey_AllVerified, true);
                    EditorUtility.ClearProgressBar();
                    Debug.Log("[Ava-Twin] All dependencies installed. SDK is ready.");
                }
            }
        }

        private static void OnPackagesRegistered(PackageRegistrationEventArgs args)
        {
            if (!SessionState.GetBool(SessionKey_Installing, false))
                return;

            // Check if our packages are in the newly registered set
            var addedNames = new HashSet<string>(args.added.Select(p => p.name));
            bool allFound = true;
            foreach (var pkg in RequiredPackages)
            {
                if (!addedNames.Contains(pkg[0]))
                {
                    allFound = false;
                    break;
                }
            }

            if (allFound)
            {
                SessionState.SetBool(SessionKey_Installing, false);
                SessionState.SetBool(SessionKey_AllVerified, true);
                EditorUtility.ClearProgressBar();
                Debug.Log("[Ava-Twin] All dependencies installed. SDK is ready.");
            }
        }

        private static void CheckAndInstall()
        {
            var listRequest = Client.List(true);
            EditorApplication.update += PollList;

            void PollList()
            {
                if (!listRequest.IsCompleted) return;
                EditorApplication.update -= PollList;

                if (listRequest.Status != StatusCode.Success)
                {
                    Debug.LogWarning("[Ava-Twin] Failed to list packages: " +
                        listRequest.Error?.message);
                    return;
                }

                var installed = new HashSet<string>(
                    listRequest.Result.Select(p => p.name));

                var missing = new List<string>();
                foreach (var pkg in RequiredPackages)
                {
                    if (!installed.Contains(pkg[0]))
                        missing.Add($"{pkg[0]}@{pkg[1]}");
                }

                if (missing.Count == 0)
                {
                    SessionState.SetBool(SessionKey_AllVerified, true);
                    return;
                }

                // Show progress bar and start batch install
                Debug.Log($"[Ava-Twin] Installing {missing.Count} required " +
                    $"package{(missing.Count == 1 ? "" : "s")}: " +
                    string.Join(", ", missing));

                SessionState.SetBool(SessionKey_Installing, true);
                EditorUtility.DisplayProgressBar(
                    "Ava-Twin SDK",
                    "Installing required packages...",
                    0.1f);

                var addRequest = Client.AddAndRemove(missing.ToArray());
                EditorApplication.update += PollInstall;

                void PollInstall()
                {
                    if (!addRequest.IsCompleted)
                    {
                        float t = Mathf.PingPong(
                            (float)EditorApplication.timeSinceStartup * 0.4f, 0.8f) + 0.1f;
                        EditorUtility.DisplayProgressBar(
                            "Ava-Twin SDK",
                            "Installing required packages...",
                            t);
                        return;
                    }

                    EditorApplication.update -= PollInstall;

                    if (addRequest.Status == StatusCode.Success)
                    {
                        EditorUtility.DisplayProgressBar(
                            "Ava-Twin SDK",
                            "Packages installed. Reloading...",
                            0.9f);
                        Debug.Log("[Ava-Twin] Dependencies installed. Editor will reload.");
                        // Don't clear progress bar — domain reload will happen,
                        // [InitializeOnLoad] will re-show it, then VerifyAfterReload
                        // or OnPackagesRegistered will clear it once fully ready.
                    }
                    else
                    {
                        SessionState.SetBool(SessionKey_Installing, false);
                        EditorUtility.ClearProgressBar();
                        Debug.LogError("[Ava-Twin] Failed to install dependencies: " +
                            addRequest.Error?.message +
                            "\nPlease install manually via Window > Package Manager.");
                    }
                }
            }
        }
    }
}
#endif
