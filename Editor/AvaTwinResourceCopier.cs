#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;

namespace AvaTwin.Editor
{
    /// <summary>
    /// Auto-copies template Resources (Credentials, AvaTwinConfig) from the UPM package
    /// to Assets/Resources/ on first import. Never overwrites existing files.
    /// This is needed because Unity's Resources.Load() only searches Assets/Resources/,
    /// not Resources/ folders inside UPM packages.
    /// </summary>
    [InitializeOnLoad]
    public static class AvaTwinResourceCopier
    {
        private const string MarkerFile = "Assets/Resources/.avatwin-resources-copied";

        static AvaTwinResourceCopier()
        {
            EditorApplication.delayCall += CopyResourcesIfNeeded;
        }

        private static void CopyResourcesIfNeeded()
        {
            // If the marker file exists, we're running inside the SDK source
            // project itself — skip copying to prevent duplicates in source.
            // The marker is excluded from the release dist, so host projects
            // never see it.
            if (File.Exists("Assets/Ava-Twin/.source-project")) return;

            // Find the package path
            string packagePath = GetPackagePath();
            if (string.IsNullOrEmpty(packagePath)) return;

            string packageResources = Path.Combine(packagePath, "Resources");
            if (!Directory.Exists(packageResources)) return;

            // Ensure Assets/Resources exists
            string targetDir = "Assets/Resources";
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                AssetDatabase.Refresh();
            }

            bool copiedAnything = false;

            // Copy Credentials.asset (template with empty appId/apiKey)
            copiedAnything |= CopyIfMissing(
                Path.Combine(packageResources, "Credentials.asset"),
                Path.Combine(targetDir, "Credentials.asset")
            );

            // Copy AvaTwinConfig.asset
            copiedAnything |= CopyIfMissing(
                Path.Combine(packageResources, "AvaTwinConfig.asset"),
                Path.Combine(targetDir, "AvaTwinConfig.asset")
            );

            // Copy AvaTwinMobileCustomizer.prefab — required for runtime Resources.Load
            // on mobile/editor so the customizer UI can instantiate
            copiedAnything |= CopyIfMissing(
                Path.Combine(packageResources, "AvaTwinMobileCustomizer.prefab"),
                Path.Combine(targetDir, "AvaTwinMobileCustomizer.prefab")
            );

            if (copiedAnything)
            {
                AssetDatabase.Refresh();
                Debug.Log("[Ava-Twin] Template resources copied to Assets/Resources/. Open Ava-Twin > Setup to enter your credentials.");
            }
        }

        private static bool CopyIfMissing(string srcAsset, string dstAsset)
        {
            // Never overwrite existing files
            if (File.Exists(dstAsset)) return false;
            if (!File.Exists(srcAsset)) return false;

            File.Copy(srcAsset, dstAsset);

            // Don't copy .meta — let Unity generate a fresh one for the project
            // Copying .meta from the package would create GUID conflicts

            return true;
        }

        private static string GetPackagePath()
        {
            // Try to find the package via its known assembly
            string[] guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset AvaTwin.Editor");
            if (guids.Length > 0)
            {
                string asmdefPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                // asmdefPath = "Packages/me.avatwin.sdk/Editor/AvaTwin.Editor.asmdef"
                // We need "Packages/me.avatwin.sdk"
                string editorDir = Path.GetDirectoryName(asmdefPath);
                return Path.GetDirectoryName(editorDir);
            }

            // Fallback: check common UPM paths
            string[] possiblePaths = new[]
            {
                "Packages/me.avatwin.sdk",
                Path.Combine("Library", "PackageCache", "me.avatwin.sdk"),
            };

            foreach (string p in possiblePaths)
            {
                if (Directory.Exists(p)) return p;
            }

            // If running from Assets/ directly (Asset Store import), resources are already in place
            if (Directory.Exists("Assets/Ava-Twin/Resources"))
                return "Assets/Ava-Twin";

            return null;
        }
    }
}
#endif
