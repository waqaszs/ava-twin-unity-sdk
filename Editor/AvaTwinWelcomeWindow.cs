using UnityEngine;
using UnityEngine.Networking;
using UnityEditor;
using System.IO;

namespace AvaTwin.Editor
{
    [InitializeOnLoad]
    public class AvaTwinWelcomeWindow : EditorWindow
    {
        private const string ShownForVersionPref = "AvaTwin_ShownForVersion";
        private const string SdkVersion = "1.0.0";
        private const string CredentialsAssetPath = "Assets/Resources/Credentials.asset";
        private const string ConfigAssetPath = "Assets/Resources/AvaTwinConfig.asset";
        private const string DocsUrl = "https://ava-twin.me/docs/unity-sdk";
        private const string ConsoleUrl = "https://console.ava-twin.me";
        private const string ReleaseNotesUrl = "https://github.com/waqaszs/ava-twin-unity-sdk/releases";
        private const string KevinIglesiasUrl = "https://assetstore.unity.com/publishers/36307";
        private const string DefaultBaseUrl = "https://customizer.ava-twin.me";
        private const string BaseUrlOverridePref = "AvaTwin_BaseUrlOverride";

        private string appId = string.Empty;
        private string apiKey = string.Empty;

        private bool credentialsSaved;
        private string testResultMessage = string.Empty;
        private bool testPassed;
        private bool testInProgress;
        private UnityWebRequestAsyncOperation pendingRequest;

        private string importResultMessage = string.Empty;
        private bool importSucceeded;

        private GUIStyle headerStyle;
        private GUIStyle subHeaderStyle;
        private GUIStyle stepLabelStyle;
        private GUIStyle testPassStyle;
        private GUIStyle testFailStyle;
        private bool stylesInitialized;
        private Texture2D logoTexture;
        private Vector2 scrollPosition;

        private bool showDevSettings;
        private string baseUrlOverride = "";

        // ── Auto-Open on First Install / Version Upgrade ─────────────────
        static AvaTwinWelcomeWindow()
        {
            EditorApplication.delayCall += ShowOnFirstImport;
        }

        private static void ShowOnFirstImport()
        {
            // If the marker file exists, we're running inside the SDK source
            // project itself — skip auto-opening the welcome window and skip
            // creating Assets/Resources/. Source project devs don't need the
            // welcome flow (they have Window/Ava-Twin/Setup menu if needed).
            if (File.Exists("Assets/Ava-Twin/.source-project")) return;

            // Use a project-level file marker instead of per-machine EditorPrefs
            // so the welcome window shows once per project, not once per machine
            string markerPath = $"Assets/Resources/.avatwin-setup-{SdkVersion}";
            if (File.Exists(markerPath)) return;

            // Ensure directory exists (ResourceCopier may not have run yet)
            if (!Directory.Exists("Assets/Resources"))
                Directory.CreateDirectory("Assets/Resources");

            // Create marker file
            File.WriteAllText(markerPath, SdkVersion);
            AssetDatabase.Refresh();

            // Also set EditorPrefs as backup (for same-session checks)
            EditorPrefs.SetString(ShownForVersionPref, SdkVersion);

            OpenWindow();
        }

        // ── Menu Items ───────────────────────────────────────────────────
        [MenuItem("Window/Ava-Twin/Setup")]
        public static void OpenWindow()
        {
            var window = GetWindow<AvaTwinWelcomeWindow>(
                utility: true,
                title: "Ava-Twin SDK Setup",
                focus: true);
            window.minSize = new Vector2(440, 600);
            window.Show();
        }

        [MenuItem("Window/Ava-Twin/Import Demo Scene")]
        public static void ImportDemoScene()
        {
            if (AssetDatabase.IsValidFolder("Assets/Samples/Ava-Twin SDK"))
            {
                EditorUtility.DisplayDialog("Ava-Twin SDK",
                    "Demo scene is already imported.\n\nFind it at: Assets/Samples/Ava-Twin SDK/", "OK");
                return;
            }

            var listRequest = UnityEditor.PackageManager.Client.List(true);
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!listRequest.IsCompleted) return;
                EditorApplication.update -= poll;

                if (listRequest.Status != UnityEditor.PackageManager.StatusCode.Success)
                {
                    EditorUtility.DisplayDialog("Ava-Twin SDK", "Could not list packages.", "OK");
                    return;
                }

                foreach (var pkg in listRequest.Result)
                {
                    if (pkg.name == "me.avatwin.sdk")
                    {
                        string sampleSrc = System.IO.Path.Combine(pkg.resolvedPath, "Samples~", "Demo");
                        if (!System.IO.Directory.Exists(sampleSrc))
                        {
                            EditorUtility.DisplayDialog("Ava-Twin SDK",
                                "Demo scene not found in package.", "OK");
                            return;
                        }

                        string destDir = "Assets/Samples/Ava-Twin SDK/1.0.0/Demo";
                        string destFull = System.IO.Path.Combine(Application.dataPath, "..", destDir);
                        System.IO.Directory.CreateDirectory(destFull);

                        foreach (var file in System.IO.Directory.GetFiles(sampleSrc))
                        {
                            string destFile = System.IO.Path.Combine(destFull, System.IO.Path.GetFileName(file));
                            System.IO.File.Copy(file, destFile, true);
                        }

                        AssetDatabase.Refresh();
                        Debug.Log("[Ava-Twin] Demo scene imported to " + destDir);
                        EditorUtility.DisplayDialog("Ava-Twin SDK",
                            $"Demo scene imported!\n\nOpen it from:\n{destDir}/Demo.unity", "OK");
                        return;
                    }
                }

                EditorUtility.DisplayDialog("Ava-Twin SDK", "Ava-Twin SDK package not found.", "OK");
            };
            EditorApplication.update += poll;
        }

        [MenuItem("Window/Ava-Twin/Check for Updates")]
        public static void CheckForUpdates()
        {
            var request = UnityWebRequest.Get(
                "https://api.github.com/repos/waqaszs/ava-twin-unity-sdk/tags?per_page=1");
            request.SetRequestHeader("User-Agent", "AvaTwin-Unity-SDK");
            var op = request.SendWebRequest();

            EditorUtility.DisplayProgressBar("Ava-Twin SDK", "Checking for updates...", 0.5f);

            EditorApplication.CallbackFunction pollUpdate = null;
            pollUpdate = () =>
            {
                if (!op.isDone) return;
                EditorApplication.update -= pollUpdate;
                EditorUtility.ClearProgressBar();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    EditorUtility.DisplayDialog("Ava-Twin SDK",
                        "Could not check for updates.\n\n" + request.error, "OK");
                    request.Dispose();
                    return;
                }

                string json = request.downloadHandler.text;
                request.Dispose();

                // Parse latest tag name from response: [{"name":"v1.0.0",...}]
                string latestTag = "";
                int nameIdx = json.IndexOf("\"name\"");
                if (nameIdx >= 0)
                {
                    int startQuote = json.IndexOf('"', nameIdx + 7);
                    int endQuote = json.IndexOf('"', startQuote + 1);
                    if (startQuote >= 0 && endQuote > startQuote)
                        latestTag = json.Substring(startQuote + 1, endQuote - startQuote - 1);
                }

                string latestVersion = latestTag.TrimStart('v');
                if (string.IsNullOrEmpty(latestVersion))
                {
                    EditorUtility.DisplayDialog("Ava-Twin SDK",
                        "Could not determine the latest version.", "OK");
                    return;
                }

                if (string.Compare(latestVersion, SdkVersion, System.StringComparison.Ordinal) > 0)
                {
                    bool update = EditorUtility.DisplayDialog("Ava-Twin SDK — Update Available",
                        $"A newer version is available!\n\n" +
                        $"Installed: v{SdkVersion}\n" +
                        $"Latest: {latestTag}\n\n" +
                        "To update, change the version in your manifest.json:\n" +
                        $"Packages/manifest.json → \"me.avatwin.sdk\": \"...#{latestTag}\"",
                        "Open Releases Page", "Later");

                    if (update)
                        Application.OpenURL("https://github.com/waqaszs/ava-twin-unity-sdk/releases");
                }
                else
                {
                    EditorUtility.DisplayDialog("Ava-Twin SDK",
                        $"You're up to date! (v{SdkVersion})", "OK");
                }
            };
            EditorApplication.update += pollUpdate;
        }

        // ── Lifecycle ────────────────────────────────────────────────────
        private void OnEnable()
        {
            baseUrlOverride = EditorPrefs.GetString(BaseUrlOverridePref, "");
            LoadCredentials();
            CheckCredentialsSaved();
        }

        private void LoadCredentials()
        {
            var asset = Resources.Load<Credentials>("Credentials");
            if (asset == null)
                asset = AssetDatabase.LoadAssetAtPath<Credentials>(CredentialsAssetPath);
            if (asset != null)
            {
                var so = new SerializedObject(asset);
                appId = so.FindProperty("appId").stringValue;
                apiKey = so.FindProperty("apiKey").stringValue;
            }
        }

        private void InitStyles()
        {
            if (stylesInitialized) return;

            headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(0, 0, 4, 4)
            };

            subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin = new RectOffset(0, 0, 8, 4)
            };

            stepLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                margin = new RectOffset(0, 0, 6, 2)
            };

            testPassStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.2f, 0.8f, 0.2f) },
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };

            testFailStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.9f, 0.2f, 0.2f) },
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };

            stylesInitialized = true;
        }

        // ── Main GUI ─────────────────────────────────────────────────────
        private void OnGUI()
        {
            InitStyles();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.Space(12);

            // ── Header: Logo + Title ─────────────────────────────────────
            if (logoTexture == null)
            {
                // Try package path first, then Assets path
                // UPM package path (no Assets/Ava-Twin prefix)
                logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Packages/me.avatwin.sdk/UI/ava-twin-logo.png");
                // Fallback: Asset Store import (files under Assets/)
                if (logoTexture == null)
                    logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                        "Assets/Ava-Twin/UI/ava-twin-logo.png");
            }

            if (logoTexture != null)
            {
                const float logoAreaHeight = 60f;
                var logoRect = GUILayoutUtility.GetRect(0, logoAreaHeight, GUILayout.ExpandWidth(true));
                float logoWidth = Mathf.Min(logoRect.width * 0.5f, 180f);
                float logoHeight = logoWidth * ((float)logoTexture.height / logoTexture.width);
                float clampedHeight = Mathf.Min(logoHeight, logoAreaHeight);
                float clampedWidth = clampedHeight * (logoWidth / Mathf.Max(logoHeight, 1f));
                var centeredRect = new Rect(
                    logoRect.x + (logoRect.width - clampedWidth) / 2f,
                    logoRect.y + (logoAreaHeight - clampedHeight) / 2f,
                    clampedWidth, clampedHeight);
                GUI.DrawTexture(centeredRect, logoTexture, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.LabelField($"Ava-Twin SDK v{SdkVersion}", headerStyle);
            EditorGUILayout.Space(4);
            DrawSeparator();

            // ── Step 1: Credentials ──────────────────────────────────────
            EditorGUILayout.LabelField("Step 1 — Credentials", stepLabelStyle);
            EditorGUILayout.Space(2);
            EditorGUILayout.HelpBox(
                "Enter your App ID and API Key from console.ava-twin.me",
                MessageType.Info);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();
            appId = EditorGUILayout.TextField("App ID", appId);
            apiKey = EditorGUILayout.TextField("API Key", apiKey);
            if (EditorGUI.EndChangeCheck())
            {
                credentialsSaved = false;
                testPassed = false;
                testResultMessage = string.Empty;
                importResultMessage = string.Empty;
            }

            EditorGUILayout.Space(4);

            bool bothFieldsFilled = !string.IsNullOrWhiteSpace(appId)
                                 && !string.IsNullOrWhiteSpace(apiKey);

            EditorGUI.BeginDisabledGroup(!bothFieldsFilled);
            if (GUILayout.Button("Save Credentials", GUILayout.Height(28)))
            {
                SaveCredentials();
                EnsureConfigAsset();
                credentialsSaved = true;
                testPassed = false;
                testResultMessage = string.Empty;
                importResultMessage = string.Empty;
            }
            EditorGUI.EndDisabledGroup();

            if (credentialsSaved)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Credentials saved.", testPassStyle);
            }

            EditorGUILayout.Space(6);
            DrawSeparator();

            // ── Step 2: Test Connection ──────────────────────────────────
            EditorGUILayout.LabelField("Step 2 — Test Connection", stepLabelStyle);
            EditorGUILayout.Space(4);

            EditorGUI.BeginDisabledGroup(!credentialsSaved || testInProgress);
            string testLabel = testInProgress ? "Testing..." : "Test Connection";
            if (!credentialsSaved)
                testLabel = "Test Connection  (save credentials first)";
            if (GUILayout.Button(testLabel, GUILayout.Height(28)))
            {
                TestConnection();
            }
            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(testResultMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(testResultMessage, testPassed ? testPassStyle : testFailStyle);
            }

            EditorGUILayout.Space(6);
            DrawSeparator();

            // ── Step 3: Import Demo Scene ────────────────────────────────
            EditorGUILayout.LabelField("Step 3 — Import Demo Scene", stepLabelStyle);
            EditorGUILayout.Space(4);

            EditorGUI.BeginDisabledGroup(!testPassed);
            var importContent = new GUIContent(
                "Import Demo Scene",
                testPassed ? "" : "Test connection first");
            if (GUILayout.Button(importContent, GUILayout.Height(28)))
            {
                ImportDemoSceneInWindow();
            }
            EditorGUI.EndDisabledGroup();

            if (!testPassed && credentialsSaved)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Test connection first.", EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(importResultMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(importResultMessage,
                    importSucceeded ? testPassStyle : testFailStyle);
            }

            EditorGUILayout.Space(6);
            DrawSeparator();

            // ── Developer Settings (collapsed) ───────────────────────────
            showDevSettings = EditorGUILayout.Foldout(showDevSettings, "Developer Settings", true);
            if (showDevSettings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Override the API base URL for testing against a development server.\n" +
                    "Leave empty to use production (customizer.ava-twin.me).",
                    MessageType.Info);

                EditorGUI.BeginChangeCheck();
                baseUrlOverride = EditorGUILayout.TextField("Base URL Override", baseUrlOverride);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(BaseUrlOverridePref, baseUrlOverride);
                    UpdateConfigBaseUrl(
                        string.IsNullOrWhiteSpace(baseUrlOverride) ? DefaultBaseUrl : baseUrlOverride);
                }

                if (!string.IsNullOrEmpty(baseUrlOverride))
                {
                    EditorGUILayout.HelpBox($"Currently using: {baseUrlOverride}", MessageType.Warning);
                    if (GUILayout.Button("Reset to Production"))
                    {
                        baseUrlOverride = "";
                        EditorPrefs.SetString(BaseUrlOverridePref, "");
                        UpdateConfigBaseUrl(DefaultBaseUrl);
                    }
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            DrawSeparator();

            // ── Third-Party Attribution ──────────────────────────────────
            EditorGUILayout.LabelField("Third-Party Attribution", subHeaderStyle);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Animation assets by Kevin Iglesias.\nUsed under license from the Unity Asset Store.",
                MessageType.None);
            EditorGUILayout.Space(2);
            if (GUILayout.Button("Kevin Iglesias — Unity Asset Store"))
                Application.OpenURL(KevinIglesiasUrl);

            EditorGUILayout.Space(4);
            DrawSeparator();

            // ── Links ────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Links", subHeaderStyle);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Documentation", GUILayout.Height(26)))
                Application.OpenURL(DocsUrl);
            if (GUILayout.Button("Console", GUILayout.Height(26)))
                Application.OpenURL(ConsoleUrl);
            if (GUILayout.Button("Release Notes", GUILayout.Height(26)))
                Application.OpenURL(ReleaseNotesUrl);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            DrawSeparator();

            // ── Footer ──────────────────────────────────────────────────
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"v{SdkVersion}", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(4);

            if (GUILayout.Button("Close", GUILayout.Height(26)))
                Close();

            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        // ── Credentials Save / Load ──────────────────────────────────────
        private void SaveCredentials()
        {
            EnsureDirectoryExists(CredentialsAssetPath);

            var asset = AssetDatabase.LoadAssetAtPath<Credentials>(CredentialsAssetPath);
            if (asset == null)
            {
                asset = CreateInstance<Credentials>();
                AssetDatabase.CreateAsset(asset, CredentialsAssetPath);
            }

            var so = new SerializedObject(asset);
            so.FindProperty("appId").stringValue = appId;
            so.FindProperty("apiKey").stringValue = apiKey;
            so.ApplyModifiedProperties();

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log("[Ava-Twin] Credentials saved to " + CredentialsAssetPath);
        }

        private void EnsureConfigAsset()
        {
            if (AssetDatabase.LoadAssetAtPath<AvaTwinConfig>(ConfigAssetPath) != null)
                return;

            EnsureDirectoryExists(ConfigAssetPath);

            var config = CreateInstance<AvaTwinConfig>();
            config.baseApiUrl = string.IsNullOrWhiteSpace(baseUrlOverride)
                ? DefaultBaseUrl
                : baseUrlOverride;

            AssetDatabase.CreateAsset(config, ConfigAssetPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[Ava-Twin] Config created at " + ConfigAssetPath);
        }

        private void UpdateConfigBaseUrl(string url)
        {
            var config = AssetDatabase.LoadAssetAtPath<AvaTwinConfig>(ConfigAssetPath);
            if (config == null)
            {
                EnsureConfigAsset();
                config = AssetDatabase.LoadAssetAtPath<AvaTwinConfig>(ConfigAssetPath);
            }

            if (config != null)
            {
                config.baseApiUrl = url;
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssets();
            }
        }

        private void CheckCredentialsSaved()
        {
            var asset = AssetDatabase.LoadAssetAtPath<Credentials>(CredentialsAssetPath);
            if (asset != null)
            {
                var so = new SerializedObject(asset);
                string savedAppId = so.FindProperty("appId").stringValue;
                string savedApiKey = so.FindProperty("apiKey").stringValue;
                credentialsSaved = !string.IsNullOrWhiteSpace(savedAppId)
                                && !string.IsNullOrWhiteSpace(savedApiKey);
            }
            else
            {
                credentialsSaved = false;
            }
        }

        // ── Test Connection ──────────────────────────────────────────────
        private void TestConnection()
        {
            testInProgress = true;
            testResultMessage = string.Empty;

            string jsonBody = JsonUtility.ToJson(
                new TokenMintRequest { appId = appId.Trim(), apiKey = apiKey.Trim() });
            byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            string baseUrl = DefaultBaseUrl;
            var config = AssetDatabase.LoadAssetAtPath<AvaTwinConfig>(ConfigAssetPath);
            if (config == null) config = Resources.Load<AvaTwinConfig>("AvaTwinConfig");
            if (config != null && !string.IsNullOrWhiteSpace(config.baseApiUrl))
                baseUrl = config.baseApiUrl.TrimEnd('/');
            string tokenMintUrl = $"{baseUrl}/api/token-mint";

            var request = new UnityWebRequest(tokenMintUrl, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 10;

            pendingRequest = request.SendWebRequest();
            EditorApplication.update += PollTestConnection;
        }

        private void PollTestConnection()
        {
            if (pendingRequest == null || !pendingRequest.isDone)
                return;

            EditorApplication.update -= PollTestConnection;

            var request = pendingRequest.webRequest;
            testInProgress = false;

            if (request.result == UnityWebRequest.Result.Success)
            {
                testPassed = true;
                testResultMessage = "Connection successful! Credentials are valid.";
            }
            else if (request.result == UnityWebRequest.Result.ProtocolError)
            {
                testPassed = false;
                testResultMessage = $"Connection failed: HTTP {request.responseCode} — credentials may be invalid.";
            }
            else
            {
                testPassed = false;
                testResultMessage = $"Connection failed: {request.error}";
            }

            request.Dispose();
            pendingRequest = null;
            Repaint();
        }

        // ── Import Demo Scene (in-window version) ───────────────────────
        private void ImportDemoSceneInWindow()
        {
            importResultMessage = string.Empty;
            importSucceeded = false;

            if (AssetDatabase.IsValidFolder("Assets/Samples/Ava-Twin SDK"))
            {
                importSucceeded = true;
                importResultMessage = "Demo scene already imported at Assets/Samples/Ava-Twin SDK/";
                return;
            }

            var listRequest = UnityEditor.PackageManager.Client.List(true);
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!listRequest.IsCompleted) return;
                EditorApplication.update -= poll;

                if (listRequest.Status != UnityEditor.PackageManager.StatusCode.Success)
                {
                    importResultMessage = "Could not list packages.";
                    Repaint();
                    return;
                }

                foreach (var pkg in listRequest.Result)
                {
                    if (pkg.name == "me.avatwin.sdk")
                    {
                        string sampleSrc = System.IO.Path.Combine(pkg.resolvedPath, "Samples~", "Demo");
                        if (!System.IO.Directory.Exists(sampleSrc))
                        {
                            importResultMessage = "Demo scene not found in package.";
                            Repaint();
                            return;
                        }

                        string destDir = $"Assets/Samples/Ava-Twin SDK/{SdkVersion}/Demo";
                        string destFull = System.IO.Path.Combine(Application.dataPath, "..", destDir);
                        System.IO.Directory.CreateDirectory(destFull);

                        foreach (var file in System.IO.Directory.GetFiles(sampleSrc))
                        {
                            string destFile = System.IO.Path.Combine(
                                destFull, System.IO.Path.GetFileName(file));
                            System.IO.File.Copy(file, destFile, true);
                        }

                        AssetDatabase.Refresh();
                        importSucceeded = true;
                        importResultMessage = $"Demo scene imported to {destDir}/";
                        Debug.Log("[Ava-Twin] Demo scene imported to " + destDir);
                        Repaint();
                        return;
                    }
                }

                importResultMessage = "Ava-Twin SDK package not found.";
                Repaint();
            };
            EditorApplication.update += poll;
        }

        // ── Helpers ──────────────────────────────────────────────────────
        private static void EnsureDirectoryExists(string assetPath)
        {
            string directory = System.IO.Path.GetDirectoryName(assetPath);
            if (!AssetDatabase.IsValidFolder(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }
        }

        [System.Serializable]
        private struct TokenMintRequest
        {
            public string appId;
            public string apiKey;
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(4);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(4);
        }
    }
}
