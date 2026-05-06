using UnityEngine;
using UnityEngine.EventSystems;

namespace AvaTwin
{

[DisallowMultipleComponent]
public sealed class DemoController : MonoBehaviour
{
    private CharacterLoader characterLoader;

    [Header("Scene")]
    [Tooltip("Where the avatar spawns. If empty, spawns at world origin.")]
    [SerializeField] private Transform spawnPoint;

    [Header("Movement Options")]
    [Tooltip("Enable strafe movement (sidestep without turning).")]
    public bool enableStrafe = false;
    [Tooltip("Enable backward movement (walk backward without turning around).")]
    public bool enableBackward = false;

    private GameObject customizerBg;

    private void Start()
    {
        characterLoader = SDK.GetLoader();

        // Find UI background overlay if present in scene
        var bg = GameObject.Find("Background");
        if (bg != null) customizerBg = bg;

        // Create a default spawn point at origin if none assigned
        if (spawnPoint == null)
        {
            var spawnGo = new GameObject("SpawnPoint");
            spawnGo.transform.position = Vector3.zero;
            spawnPoint = spawnGo.transform;
        }

        // Ensure EventSystem exists for UI interaction (works with both legacy and new Input System)
        if (EventSystem.current == null)
        {
            var esGo = new GameObject("EventSystem");
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<StandaloneInputModule>();
        }

        // Ensure a Main Camera exists so the scene is visible out of the box
        if (Camera.main == null)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = new Vector3(0f, 1.5f, -3f);
            camGo.transform.LookAt(Vector3.zero);
        }

        // Ensure a ground plane exists so the character doesn't fall through
        if (GameObject.Find("Ground") == null)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(10f, 1f, 10f);
            // Use a simple dark material
            var renderer = ground.GetComponent<Renderer>();
            if (renderer != null)
            {
                // Use Unlit shader for the ground — guaranteed to exist in URP projects
                // and avoids pink/magenta fallback when Simple Lit isn't included in builds
                var shader = Shader.Find("Universal Render Pipeline/Unlit")
                          ?? Shader.Find("Unlit/Color")
                          ?? Shader.Find("Standard");
                var mat = new Material(shader);
                mat.color = new Color(0.2f, 0.2f, 0.2f);
                renderer.material = mat;
            }
        }

        characterLoader.CharacterLoaded += OnCharacterLoaded;
        characterLoader.CharacterLoadFailed += OnCharacterLoadFailed;
        characterLoader.LoadingStatusChanged += OnLoadingStatus;
    }

    private void OnCharacterLoaded(GameObject character)
    {
        SetupPlayableCharacter(character);
    }

    private void OnLoadingStatus(string status)
    {
        var btn = customizerBg?.GetComponentInChildren<UnityEngine.UI.Button>();
        if (btn != null)
        {
            var txt = btn.GetComponentInChildren<UnityEngine.UI.Text>();
            if (txt != null) txt.text = status;
        }
    }

    private void OnCharacterLoadFailed(string error)
    {
        Debug.LogError("[Ava-Twin] Failed to load character.");

        // Reset button so the user can retry
        var btn = customizerBg?.GetComponentInChildren<UnityEngine.UI.Button>();
        if (btn != null)
        {
            btn.interactable = true;
            var txt = btn.GetComponentInChildren<UnityEngine.UI.Text>();
            if (txt != null) txt.text = "Retry";
        }

        if (customizerBg != null) customizerBg.SetActive(true);
    }

    private void SetupPlayableCharacter(GameObject characterRoot)
    {
        if (characterRoot == null)
        {
            Debug.LogWarning("[Ava-Twin] No character available to configure.");
            return;
        }

        // Position at spawn point
        characterRoot.transform.position = spawnPoint != null ? spawnPoint.position : Vector3.zero;
        characterRoot.transform.rotation = Quaternion.identity;

        // Add CharacterController (Unity's built-in collider/movement)
        var cc = characterRoot.GetComponent<CharacterController>();
        if (cc == null)
        {
            cc = characterRoot.AddComponent<CharacterController>();
            // Size the capsule to fit the avatar
            var renderers = characterRoot.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);

                cc.height = bounds.size.y;
                cc.radius = Mathf.Min(bounds.size.x, bounds.size.z) * 0.3f;
                cc.center = new Vector3(0f, bounds.size.y * 0.5f, 0f);
            }
            else
            {
                cc.height = 1.8f;
                cc.radius = 0.3f;
                cc.center = new Vector3(0f, 0.9f, 0f);
            }
        }

        // Add input handler
        var input = characterRoot.GetComponent<AvaTwinInput>();
        if (input == null)
            input = characterRoot.AddComponent<AvaTwinInput>();

        // Add character controller script
        var controller = characterRoot.GetComponent<AvaTwinCharacterController>();
        if (controller == null)
            controller = characterRoot.AddComponent<AvaTwinCharacterController>();

        // Apply movement options from DemoController Inspector
        controller.enableStrafe = enableStrafe;
        controller.enableBackwardMovement = enableBackward;

        // Create a camera target at head height for third-person camera
        var headBone = FindBoneByName(characterRoot.transform, "Head");
        if (headBone != null)
        {
            var camTarget = new GameObject("CameraTarget");
            camTarget.transform.SetParent(characterRoot.transform);
            camTarget.transform.position = headBone.position;
            controller.SetCameraTarget(camTarget.transform);
        }

        // Hide the customizer UI overlay
        if (customizerBg != null) customizerBg.SetActive(false);

        // Attach camera follow script so the camera tracks the character
        var cam = Camera.main;
        if (cam != null)
        {
            var follow = cam.GetComponent<AvaTwinCameraFollow>();
            if (follow == null)
                follow = cam.gameObject.AddComponent<AvaTwinCameraFollow>();

            // Point the follow script at the camera target (or fall back to character root)
            var existingTarget = characterRoot.transform.Find("CameraTarget");
            follow.target = existingTarget != null ? existingTarget : characterRoot.transform;
        }

        Debug.Log("[Ava-Twin] Playable character ready — WASD to move, Shift to run, Space to jump, F to fly.");
    }

    private static Transform FindBoneByName(Transform root, string boneName)
    {
        Transform fallback = null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == boneName) return t; // exact match wins
            if (fallback == null && t.name.Contains(boneName)) fallback = t;
        }
        return fallback;
    }

    /// <summary>
    /// Called by UI buttons to open the customizer.
    /// The background UI stays visible until the character is fully loaded
    /// and set up (hidden in SetupPlayableCharacter, not here).
    /// </summary>
    public void OpenCustomizer()
    {
        if (characterLoader == null)
            characterLoader = SDK.GetLoader();

        // Show loading state — disable button and update text to prevent double-clicks
        var btn = customizerBg?.GetComponentInChildren<UnityEngine.UI.Button>();
        if (btn != null)
        {
            btn.interactable = false;
            var txt = btn.GetComponentInChildren<UnityEngine.UI.Text>();
            if (txt != null) txt.text = "Loading...";
        }

        characterLoader.OpenCustomizer();
    }

    private void OnDestroy()
    {
        if (characterLoader != null)
        {
            characterLoader.CharacterLoaded -= OnCharacterLoaded;
            characterLoader.CharacterLoadFailed -= OnCharacterLoadFailed;
            characterLoader.LoadingStatusChanged -= OnLoadingStatus;
        }
    }
}

} // namespace AvaTwin
