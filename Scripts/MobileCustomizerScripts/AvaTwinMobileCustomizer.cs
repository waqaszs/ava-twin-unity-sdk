using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace AvaTwin
{
public class AvaTwinMobileCustomizer : MonoBehaviour
{
    private const string HeadCategory = "head";
    private const string TopCategory = "top";
    private const string BottomCategory = "bottom";
    private const string ShoesCategory = "shoes";

    private static readonly string[] AllCategories = { HeadCategory, TopCategory, BottomCategory, ShoesCategory };

    [Serializable]
    public class SkinToneOption
    {
        public string id;
        public string label;
        public string hex;
    }

    [Header("Config")]
    [SerializeField] private AvaTwinConfig config;
    [Tooltip("If Config is not assigned in inspector, loads from Resources.")]
    [SerializeField] private string configResourcePath = "AvaTwinConfig";

    [Header("UI Containers")]
    [SerializeField] private Transform variationsContainer;
    [SerializeField] private Transform skinToneContainer;
    [SerializeField] private AvaVariationButton buttonPrefab;
    [SerializeField] private Button skinToneButtonPrefab;

    [Header("Category Tabs")]
    [SerializeField] private Button headTabButton;
    [SerializeField] private Button topTabButton;
    [SerializeField] private Button bottomTabButton;
    [SerializeField] private Button shoesTabButton;
    [SerializeField] private string defaultCategory = HeadCategory;
    [SerializeField] private SkinToneOption[] skinTones = new[]
    {
        new SkinToneOption { id = "default", label = "Default", hex = "" },
        new SkinToneOption { id = "sk1", label = "Light", hex = "#FFDFC4" },
        new SkinToneOption { id = "sk2", label = "Fair", hex = "#F0C8A0" },
        new SkinToneOption { id = "sk3", label = "Medium Light", hex = "#D4A574" },
        new SkinToneOption { id = "sk4", label = "Medium", hex = "#C68642" },
        new SkinToneOption { id = "sk5", label = "Medium Dark", hex = "#8D5524" },
        new SkinToneOption { id = "sk6", label = "Dark", hex = "#5C3A1E" },
        new SkinToneOption { id = "sk7", label = "Deep", hex = "#3B2210" },
    };

    [Header("Flow Integration")]
    [SerializeField] private CharacterLoader characterLoader;
    [SerializeField] private GameObject loadingGameObject;
    [SerializeField] private bool autoInitializeOnEnable = true;
    [SerializeField] private bool selectFirstOptionIfDefaultMissing = true;
    [SerializeField] private bool loadPreviewOnInitialize = true;
    [SerializeField] private bool livePreviewOnSelection = true;
    [SerializeField] private bool logReceivedCategories = true;

    [Header("Preview")]
    [SerializeField] private AvaTwinPreviewCameraController previewCameraController;
    [Tooltip("Resources path for the camera setup prefab (without extension).")]
    [SerializeField] private string cameraSetupResourcePath = "CameraSetup";
    [Tooltip("Where the avatar character is placed for preview. Create an empty child GameObject and assign it.")]
    [SerializeField] private Transform previewSpawnPoint;
    [Tooltip("Rotation speed when dragging the character preview.")]
    [SerializeField] private float dragRotationSpeed = 0.5f;

    // Events
    public event Action<string> OnAvatarUrlReady;
    public event Action<string, string> OnAvatarUrlAndSkinToneReady;
    public event Action<string, string, string> OnAvatarReady; // avatarId, url, skinTone
    public event Action<string> OnError;
    public event Action OnReady;

    /// <summary>
    /// The public_combo_id returned by the last successful avatar save.
    /// Set before OnAvatarUrlReady fires so CharacterLoader can read it.
    /// </summary>
    public string LastAvatarId { get; private set; }

    private AvaTwinApiClient _api;
    private bool _isInitialized;
    private bool _isBusy;
    private int _previewRequestId;
    private string _selectedSkinToneHex = "";
    private bool _isDefaultSkinToneSelected = true;
    private CharacterLoader _eventSubscribedLoader;
    private readonly List<SkinToneButtonBinding> _skinToneButtons = new List<SkinToneButtonBinding>();
    private readonly Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
    private readonly Dictionary<string, string> _selectedByCategory = new Dictionary<string, string>();
    private readonly Dictionary<string, List<AvaVariationButton>> _buttonsByCategory = new Dictionary<string, List<AvaVariationButton>>();
    private readonly Dictionary<string, List<AvatarVariation>> _variationsByCategory = new Dictionary<string, List<AvatarVariation>>();
    private string _activeCategory;
    private bool _isDragging;
    private float _lastDragX;
    private int _pendingThumbnails;
    private GameObject _cameraSetupInstance;

    private class SkinToneButtonBinding
    {
        public Button button;
        public string hex;
        public bool isDefault;
        public GameObject selectedIndicator;
    }

    private void OnEnable()
    {
        SetCameraSetupActive(true);

        if (autoInitializeOnEnable)
            Initialize();
    }

    private void Update()
    {
        HandleDragRotation();
    }

    private void HandleDragRotation()
    {
        var loader = GetOrFindCharacterLoader();
        var character = loader != null ? loader.GetLoadedCharacter() : null;
        if (character == null)
            return;

        // Touch input
        if (Input.touchCount == 1)
        {
            var touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                _isDragging = true;
                _lastDragX = touch.position.x;
            }
            else if (touch.phase == TouchPhase.Moved && _isDragging)
            {
                float delta = touch.position.x - _lastDragX;
                character.transform.Rotate(Vector3.up, -delta * dragRotationSpeed, Space.World);
                _lastDragX = touch.position.x;
            }
            else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
            {
                _isDragging = false;
            }
        }
        // Mouse fallback (editor testing)
        else if (Input.touchCount == 0)
        {
            if (Input.GetMouseButtonDown(0))
            {
                _isDragging = true;
                _lastDragX = Input.mousePosition.x;
            }
            else if (Input.GetMouseButton(0) && _isDragging)
            {
                float delta = Input.mousePosition.x - _lastDragX;
                character.transform.Rotate(Vector3.up, -delta * dragRotationSpeed, Space.World);
                _lastDragX = Input.mousePosition.x;
            }
            else if (Input.GetMouseButtonUp(0))
            {
                _isDragging = false;
            }
        }
    }

    private void OnDisable()
    {
        UnsubscribeLoaderEvents();
        SetCameraSetupActive(false);
        SetLoadingVisible(false);
    }

    public void Configure(AvaTwinConfig runtimeConfig = null, CharacterLoader runtimeLoader = null)
    {
        if (runtimeConfig != null)
            config = runtimeConfig;
        if (runtimeLoader != null)
            characterLoader = runtimeLoader;
    }

    public string GetSelectedSkinToneHex()
    {
        return _selectedSkinToneHex;
    }

    public void SetSkinToneHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;
        _selectedSkinToneHex = hex;
        _isDefaultSkinToneSelected = false;
        RefreshSkinToneSelectionVisuals();
        var loader = GetOrFindCharacterLoader();
        if (loader != null)
            loader.SetSkinToneHex(_selectedSkinToneHex);
    }

    public async void Initialize()
    {
        if (_isBusy)
            return;

        try
        {
            _isBusy = true;

            if (config == null)
                config = LoadConfigFromResources();
            if (config == null)
                throw new Exception(
                    $"Customizer config is missing. Assign it in inspector or place it in Resources/{configResourcePath}.");

            if (buttonPrefab == null)
                throw new Exception("Button prefab is missing.");

            EnsureCameraSetupLoaded();

            // Get credentials from CharacterLoader (single source of truth)
            var loader = GetOrFindCharacterLoader();
            Credentials creds = loader != null ? loader.GetCredentials() : null;

            _api = new AvaTwinApiClient(config, creds);
            _selectedByCategory.Clear();
            _buttonsByCategory.Clear();
            _selectedSkinToneHex = "";
            _isDefaultSkinToneSelected = true;

            string token = await _api.MintTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Token mint returned an empty token.");

            var library = await _api.GetLibraryAsync();

            if (library?.library == null || !library.library.ContainsKey("generic"))
                throw new Exception("Library missing generic data");

            var generic = library.library["generic"];
            LogGenericCategories(generic);

            // Web-parity: before category build picks first-library-item
            // defaults, try to restore previously-saved selections for this
            // player. EnsureValidSelection only overwrites categories that
            // are still missing, so seeded values survive.
            await TryRestoreSavedSelectionsAsync();

            // Build variation data for all categories
            foreach (var cat in AllCategories)
                BuildCategoryData(generic, cat);

            BuildSkinToneUI();
            WireCategoryTabs();

            _isInitialized = true;
            if (loader != null)
                loader.SetSkinToneHex(_selectedSkinToneHex);

            // Hide loading — customizer is ready for interaction
            SetLoadingVisible(false);

            // Show default category
            ShowCategory(defaultCategory);
            OnReady?.Invoke();
            if (loadPreviewOnInitialize)
                RequestPreviewLoad();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Ava-Twin] Customizer encountered an error: {ex.Message}\n{ex.StackTrace}");
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    public async void FinishCustomization()
    {
        if (_isBusy)
            return;

        try
        {
            if (!_isInitialized)
                throw new Exception("Customizer is not initialized yet.");

            _isBusy = true;
            SetLoadingVisible(true);

            // Step 1: Save selection and get public_combo_id
            string comboId = await SaveAndGetComboIdAsync();
            if (string.IsNullOrEmpty(comboId))
                throw new Exception("Avatar save failed — no combo ID returned.");

            // Step 1b: Mirror to player_avatars so the next customizer open
            // can restore via TryRestoreSavedSelectionsAsync. Fire-and-forget
            // — failure here doesn't block the user's in-session avatar load.
            _ = TryPersistPlayerAvatarAsync();

            // Step 2: Resolve the combo ID to a GLB URL
            var resolved = await _api.ResolveAvatarAsync(comboId);

            if (string.IsNullOrWhiteSpace(resolved?.url))
                throw new Exception($"Resolve failed: {resolved?.error ?? "No URL"}");

            // Step 3: Set LastAvatarId before firing events so consumers can read it
            LastAvatarId = comboId;

            OnAvatarUrlReady?.Invoke(resolved.url);
            OnAvatarUrlAndSkinToneReady?.Invoke(resolved.url, _selectedSkinToneHex);
            OnAvatarReady?.Invoke(comboId, resolved.url, _selectedSkinToneHex);

            var loader = GetOrFindCharacterLoader();
            if (loader != null)
            {
                loader.SetSkinToneHex(_selectedSkinToneHex);
                // NOTE: Do not call loader.LoadCharacterFromUrl here.
                // CharacterLoader already subscribes to OnAvatarUrlReady (fired
                // above) and will load via its handler. A direct call here
                // caused a double-load race where the second load destroyed
                // the already-parented character from the first load.
            }
            else
            {
                SetLoadingVisible(false);
                Debug.LogWarning("[Ava-Twin] No CharacterLoader found. Avatar URL will be emitted via event only.");
            }
        }
        catch (Exception ex)
        {
            SetLoadingVisible(false);
            Debug.LogError($"[Ava-Twin] Customizer encountered an error: {ex.Message}\n{ex.StackTrace}");
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void BuildCategoryData(
        Dictionary<string, List<AvatarVariation>> generic,
        string categoryKey)
    {
        if (!generic.TryGetValue(categoryKey, out var items) || items == null || items.Count == 0)
            return;

        var orderedItems = items
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.variationId))
            .OrderBy(item => item.sortOrder)
            .ThenBy(item => item.displayName)
            .ToList();

        if (orderedItems.Count == 0)
            return;

        _variationsByCategory[categoryKey] = orderedItems;
        EnsureValidSelection(categoryKey, orderedItems);
    }

    private void ShowCategory(string categoryKey)
    {
        _activeCategory = categoryKey;

        if (variationsContainer == null)
            return;

        // Clear existing buttons
        ClearChildren(variationsContainer);
        _buttonsByCategory.Remove(categoryKey);

        if (!_variationsByCategory.TryGetValue(categoryKey, out var items))
            return;

        // Track thumbnail loading — show loading if thumbnails aren't cached
        int uncachedCount = items.Count(item => !_thumbCache.ContainsKey(item.variationId));
        _pendingThumbnails = uncachedCount;
        if (uncachedCount > 0)
            SetLoadingVisible(true);

        var buttons = new List<AvaVariationButton>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var button = Instantiate(buttonPrefab, variationsContainer);
            buttons.Add(button);

            button.Bind(item, null, variationId =>
            {
                _selectedByCategory[categoryKey] = variationId;
                RefreshSelectionVisuals(categoryKey);

                // Keep the resolved skin tone synced to the selected head when "Default" is active.
                if (_isDefaultSkinToneSelected &&
                    string.Equals(categoryKey, HeadCategory, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedSkinToneHex = GetDefaultSkinToneForSelectedHead();
                    RefreshSkinToneSelectionVisuals();
                    var loader = GetOrFindCharacterLoader();
                    if (loader != null)
                        loader.SetSkinToneHex(_selectedSkinToneHex);
                }

                if (livePreviewOnSelection)
                    RequestPreviewLoad();
            });

            _ = LoadAndApplyThumbnailAsync(button, item.variationId);
        }

        _buttonsByCategory[categoryKey] = buttons;
        RefreshSelectionVisuals(categoryKey);

        // Move camera to matching focus point
        var cameraController = GetOrFindPreviewCameraController();
        if (cameraController != null)
            cameraController.TransitionToCategory(categoryKey);
    }

    private void WireCategoryTabs()
    {
        if (headTabButton != null)
            headTabButton.onClick.AddListener(() => ShowCategory(HeadCategory));
        if (topTabButton != null)
            topTabButton.onClick.AddListener(() => ShowCategory(TopCategory));
        if (bottomTabButton != null)
            bottomTabButton.onClick.AddListener(() => ShowCategory(BottomCategory));
        if (shoesTabButton != null)
            shoesTabButton.onClick.AddListener(() => ShowCategory(ShoesCategory));
    }

    private async Task<Texture2D> GetThumb(string variationId)
    {
        if (_thumbCache.TryGetValue(variationId, out var cached))
            return cached;

        try
        {
            var tex = await _api.GetVariationThumbnailAsync(variationId);
            _thumbCache[variationId] = tex;
            return tex;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Ava-Twin] Failed to load thumbnail.");
            return null;
        }
    }

    private string GetRequiredSelection(string categoryKey)
    {
        if (_selectedByCategory.TryGetValue(categoryKey, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new Exception($"Missing selection for category: {categoryKey}");
    }

    private void EnsureValidSelection(string categoryKey, List<AvatarVariation> items)
    {
        string preferred;
        _selectedByCategory.TryGetValue(categoryKey, out preferred);

        if (!string.IsNullOrWhiteSpace(preferred) &&
            items.Any(item => string.Equals(item.variationId, preferred, StringComparison.OrdinalIgnoreCase)))
            return;

        _selectedByCategory[categoryKey] = selectFirstOptionIfDefaultMissing ? items[0].variationId : string.Empty;
    }

    private void RefreshSelectionVisuals(string categoryKey)
    {
        List<AvaVariationButton> buttons;
        if (!_buttonsByCategory.TryGetValue(categoryKey, out buttons))
            return;

        string selectedId;
        _selectedByCategory.TryGetValue(categoryKey, out selectedId);

        foreach (var button in buttons)
        {
            if (button == null)
                continue;
            bool isSelected = string.Equals(button.GetVariationId(), selectedId, StringComparison.OrdinalIgnoreCase);
            button.SetSelected(isSelected);
        }
    }

    private static void ClearChildren(Transform container)
    {
        for (int i = container.childCount - 1; i >= 0; i--)
            Destroy(container.GetChild(i).gameObject);
    }

    private AvaTwinConfig LoadConfigFromResources()
    {
        if (string.IsNullOrWhiteSpace(configResourcePath))
            return null;
        return Resources.Load<AvaTwinConfig>(configResourcePath);
    }

    private CharacterLoader GetOrFindCharacterLoader()
    {
        if (characterLoader == null)
            characterLoader = FindObjectOfType<CharacterLoader>();
        SubscribeLoaderEvents(characterLoader);
        return characterLoader;
    }

    private AvaTwinPreviewCameraController GetOrFindPreviewCameraController()
    {
        if (previewCameraController == null)
            previewCameraController = FindObjectOfType<AvaTwinPreviewCameraController>();
        return previewCameraController;
    }

    private void EnsureCameraSetupLoaded()
    {
        if (previewCameraController != null || _cameraSetupInstance != null)
            return;

        if (string.IsNullOrWhiteSpace(cameraSetupResourcePath))
        {
            Debug.LogWarning("[Ava-Twin] Camera setup resource path is empty.");
            return;
        }

        var cameraSetupPrefab = Resources.Load<GameObject>(cameraSetupResourcePath);
        if (cameraSetupPrefab == null)
        {
            Debug.LogWarning($"[Ava-Twin] Camera setup prefab not found at Resources/{cameraSetupResourcePath}.");
            return;
        }

        _cameraSetupInstance = Instantiate(cameraSetupPrefab);
        previewCameraController = _cameraSetupInstance.GetComponentInChildren<AvaTwinPreviewCameraController>(true);

        if (previewCameraController == null)
            Debug.LogWarning("[Ava-Twin] Camera setup prefab is missing AvaTwinPreviewCameraController.");

        // Auto-wire previewSpawnPoint from the CameraSetup prefab's child.
        // The serialized field on the customizer prefab is null because CameraSetup
        // is instantiated at runtime — wire it here so the avatar spawns at the
        // correct position and camera focus points align.
        if (previewSpawnPoint == null)
        {
            var spawnTf = _cameraSetupInstance.transform.Find("PreviewSpawnPoint");
            if (spawnTf != null)
                previewSpawnPoint = spawnTf;
            else
                Debug.LogWarning("[Ava-Twin] CameraSetup prefab is missing PreviewSpawnPoint child.");
        }
    }

    private void SetCameraSetupActive(bool isActive)
    {
        if (_cameraSetupInstance == null)
            return;

        _cameraSetupInstance.SetActive(isActive);
    }

    /// <summary>
    /// Fetches the player's previously-saved variation_selections (if any)
    /// and seeds <see cref="_selectedByCategory"/> + skin tone. Called from
    /// <see cref="Initialize"/> before BuildCategoryData, so that
    /// EnsureValidSelection's first-item fallback only fires on the
    /// categories that are still missing.
    /// Best-effort: logs and continues on any failure (no saved avatar,
    /// auth failure, network error).
    /// </summary>
    private async Task TryRestoreSavedSelectionsAsync()
    {
        try
        {
            if (!await AvaTwinPlayer.EnsureAuthenticatedAsync()) return;

            var record = await AvaTwinPlayer.GetActiveAvatarAsync();
            var sel = record?.variation_selections;
            if (sel == null) return;

            if (!string.IsNullOrEmpty(sel.head))   _selectedByCategory[HeadCategory]   = sel.head;
            if (!string.IsNullOrEmpty(sel.top))    _selectedByCategory[TopCategory]    = sel.top;
            if (!string.IsNullOrEmpty(sel.bottom)) _selectedByCategory[BottomCategory] = sel.bottom;
            if (!string.IsNullOrEmpty(sel.shoes))  _selectedByCategory[ShoesCategory]  = sel.shoes;

            if (!string.IsNullOrEmpty(sel.skin_tone))
            {
                _selectedSkinToneHex = sel.skin_tone;
                _isDefaultSkinToneSelected = false;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Ava-Twin] Restore saved selections failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists the current <see cref="_selectedByCategory"/> + skin tone
    /// to the player's active avatar row (player_avatars). This is the
    /// write side of the persistence round-trip that
    /// <see cref="TryRestoreSavedSelectionsAsync"/> reads.
    /// Best-effort — failure is logged but does not block the save flow.
    /// </summary>
    private async Task TryPersistPlayerAvatarAsync()
    {
        try
        {
            _selectedByCategory.TryGetValue(HeadCategory, out var head);
            _selectedByCategory.TryGetValue(TopCategory, out var top);
            _selectedByCategory.TryGetValue(BottomCategory, out var bottom);
            _selectedByCategory.TryGetValue(ShoesCategory, out var shoes);

            if (string.IsNullOrEmpty(head) || string.IsNullOrEmpty(top) ||
                string.IsNullOrEmpty(bottom) || string.IsNullOrEmpty(shoes))
                return;

            var selections = new VariationSelections
            {
                gender = "generic",
                head = head,
                top = top,
                bottom = bottom,
                shoes = shoes,
                skin_tone = string.IsNullOrEmpty(_selectedSkinToneHex) ? null : _selectedSkinToneHex
            };
            await AvaTwinPlayer.SaveActiveAvatarAsync(selections);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Ava-Twin] Persist to player_avatars failed: {ex.Message}");
        }
    }

    private async Task<string> SaveAndGetComboIdAsync()
    {
        string head = null;
        string top = null;
        string bottom = null;
        string shoes = null;

        _selectedByCategory.TryGetValue(HeadCategory, out head);
        _selectedByCategory.TryGetValue(TopCategory, out top);
        _selectedByCategory.TryGetValue(BottomCategory, out bottom);
        _selectedByCategory.TryGetValue(ShoesCategory, out shoes);

        if (string.IsNullOrEmpty(head) || string.IsNullOrEmpty(top) ||
            string.IsNullOrEmpty(bottom) || string.IsNullOrEmpty(shoes))
        {
            Debug.LogWarning("[AvaTwin] Missing piece selection, cannot save.");
            return null;
        }

        var response = await _api.SdkAvatarSaveAsync("generic", head, top, bottom, shoes, _selectedSkinToneHex);

        if (response == null || !response.success || string.IsNullOrEmpty(response.avatar_id))
        {
            Debug.LogWarning($"[AvaTwin] Avatar save failed: {response?.error}");
            return null;
        }

        return response.avatar_id;
    }

    private void RequestPreviewLoad()
    {
        if (!_isInitialized || _api == null)
            return;

        SetLoadingVisible(true);
        _previewRequestId++;
        int requestId = _previewRequestId;
        _ = LoadPreviewForCurrentSelectionAsync(requestId);
    }

    private async Task LoadPreviewForCurrentSelectionAsync(int requestId)
    {
        try
        {
            // Save the current selection to get a public_combo_id, then resolve it
            string comboId = await SaveAndGetComboIdAsync();
            if (requestId != _previewRequestId)
                return; // stale request result

            if (string.IsNullOrEmpty(comboId))
            {
                SetLoadingVisible(false);
                return;
            }

            var resolved = await _api.ResolveAvatarAsync(comboId);

            if (requestId != _previewRequestId)
                return; // stale request result
            if (string.IsNullOrWhiteSpace(resolved?.url))
            {
                SetLoadingVisible(false);
                return;
            }

            var loader = GetOrFindCharacterLoader();
            if (loader != null)
            {
                loader.SetSkinToneHex(_selectedSkinToneHex);
                loader.LoadPreviewCharacterFromUrl(resolved.url);
            }
            else
            {
                SetLoadingVisible(false);
            }
        }
        catch (Exception ex)
        {
            SetLoadingVisible(false);
            Debug.LogWarning("[Ava-Twin] Failed to load avatar preview.");
        }
    }

    private void BuildSkinToneUI()
    {
        if (skinToneContainer == null || skinToneButtonPrefab == null || skinTones == null || skinTones.Length == 0)
            return;

        ClearChildren(skinToneContainer);
        _skinToneButtons.Clear();

        foreach (var tone in skinTones)
        {
            if (tone == null)
                continue;

            bool isDefault = string.IsNullOrWhiteSpace(tone.hex);

            var button = Instantiate(skinToneButtonPrefab, skinToneContainer);
            var label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                string baseLabel = string.IsNullOrWhiteSpace(tone.label) ? tone.id : tone.label;
                label.text = isDefault ? "X" : "";
            }

            var swatch = button.GetComponent<Image>();
            if (swatch != null)
            {
                // For "Default" (empty hex), keep the prefab's swatch color.
                if (!isDefault && ColorUtility.TryParseHtmlString(tone.hex, out var toneColor))
                    swatch.color = toneColor;
            }

            string toneHex = isDefault ? "" : tone.hex;
            button.onClick.AddListener(() =>
            {
                if (string.IsNullOrEmpty(toneHex))
                {
                    _selectedSkinToneHex = GetDefaultSkinToneForSelectedHead();
                    _isDefaultSkinToneSelected = true;
                }
                else
                {
                    _selectedSkinToneHex = toneHex;
                    _isDefaultSkinToneSelected = false;
                }

                RefreshSkinToneSelectionVisuals();
                var loader = GetOrFindCharacterLoader();
                if (loader != null)
                    loader.SetSkinToneHex(_selectedSkinToneHex);
            });

            _skinToneButtons.Add(new SkinToneButtonBinding
            {
                button = button,
                hex = toneHex,
                isDefault = isDefault,
                selectedIndicator = FindFirstChildImageObject(button)
            });
        }

        EnsureValidSkinToneSelection();
        RefreshSkinToneSelectionVisuals();
    }

    private string GetDefaultSkinToneForSelectedHead()
    {
        string headId;
        _selectedByCategory.TryGetValue(HeadCategory, out headId);
        if (string.IsNullOrEmpty(headId))
            return "#FFDFC4"; // fallback

        if (_variationsByCategory.TryGetValue(HeadCategory, out var heads))
        {
            var head = heads.FirstOrDefault(h =>
                string.Equals(h.variationId, headId, StringComparison.OrdinalIgnoreCase));
            if (head != null && !string.IsNullOrWhiteSpace(head.defaultSkinTone))
                return head.defaultSkinTone;
        }

        return "#FFDFC4"; // fallback
    }

    private void EnsureValidSkinToneSelection()
    {
        if (skinTones == null || skinTones.Length == 0)
            return;

        bool hasDefaultOption = skinTones.Any(tone =>
            tone != null && string.IsNullOrWhiteSpace(tone.hex));

        if (_isDefaultSkinToneSelected)
        {
            if (hasDefaultOption)
            {
                if (string.IsNullOrWhiteSpace(_selectedSkinToneHex))
                    _selectedSkinToneHex = GetDefaultSkinToneForSelectedHead();
                return;
            }

            _isDefaultSkinToneSelected = false;
        }

        bool exists = skinTones.Any(tone =>
            tone != null &&
            !string.IsNullOrWhiteSpace(tone.hex) &&
            string.Equals(tone.hex, _selectedSkinToneHex ?? "", StringComparison.OrdinalIgnoreCase));

        if (!exists)
        {
            if (hasDefaultOption)
            {
                _isDefaultSkinToneSelected = true;
                _selectedSkinToneHex = GetDefaultSkinToneForSelectedHead();
            }
            else
            {
                var firstTone = skinTones.FirstOrDefault(tone => tone != null);
                _selectedSkinToneHex = firstTone != null ? firstTone.hex : _selectedSkinToneHex;
            }
        }
    }

    private void RefreshSkinToneSelectionVisuals()
    {
        if (_skinToneButtons.Count == 0)
            return;

        for (int i = 0; i < _skinToneButtons.Count; i++)
        {
            var binding = _skinToneButtons[i];
            var button = binding.button;
            if (button == null)
                continue;

            var image = button.GetComponent<Image>();
            if (image == null)
                continue;

            bool isSelected = binding.isDefault
                ? _isDefaultSkinToneSelected
                : !_isDefaultSkinToneSelected &&
                  string.Equals(binding.hex, _selectedSkinToneHex, StringComparison.OrdinalIgnoreCase);
            if (binding.selectedIndicator != null)
                binding.selectedIndicator.SetActive(isSelected);
            else
            {
                // Fallback if no selected-indicator child exists in the prefab.
                button.transform.localScale = isSelected ? new Vector3(1.08f, 1.08f, 1f) : Vector3.one;
                image.transform.localScale = isSelected ? new Vector3(0.92f, 0.92f, 1f) : Vector3.one;
            }
        }
    }

    public void ShowHeadVariations() => ShowCategory(HeadCategory);
    public void ShowTopVariations() => ShowCategory(TopCategory);
    public void ShowBottomVariations() => ShowCategory(BottomCategory);
    public void ShowShoesVariations() => ShowCategory(ShoesCategory);

    private void LogGenericCategories(Dictionary<string, List<AvatarVariation>> generic)
    {
        if (!logReceivedCategories || generic == null)
            return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log("[Ava-Twin] Avatar categories loaded.");
#endif

        string[] skinKeys = { "skin", "skin_tone", "skintone", "tone" };
        var foundSkinKey = generic.Keys.FirstOrDefault(key =>
            skinKeys.Any(match => key.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0));

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (!string.IsNullOrWhiteSpace(foundSkinKey))
            Debug.Log("[Ava-Twin] Skin tone options loaded.");
#endif
    }

    private async Task LoadAndApplyThumbnailAsync(AvaVariationButton button, string variationId)
    {
        bool wasCached = _thumbCache.ContainsKey(variationId);
        var tex = await GetThumb(variationId);
        if (button != null && tex != null)
            button.SetThumbnail(tex);

        if (!wasCached)
        {
            _pendingThumbnails = Mathf.Max(0, _pendingThumbnails - 1);
            if (_pendingThumbnails == 0)
                SetLoadingVisible(false);
        }
    }

    private static GameObject FindFirstChildImageObject(Button button)
    {
        if (button == null)
            return null;

        var images = button.GetComponentsInChildren<Image>(true);
        foreach (var img in images)
        {
            // Root image is the swatch itself; use first child image as selection indicator.
            if (img != null && img.gameObject != button.gameObject)
                return img.gameObject;
        }

        return null;
    }

    private void SubscribeLoaderEvents(CharacterLoader loader)
    {
        if (loader == null || _eventSubscribedLoader == loader)
            return;

        UnsubscribeLoaderEvents();
        _eventSubscribedLoader = loader;
        _eventSubscribedLoader.CharacterVisualLoaded += OnCharacterVisualLoaded;
        _eventSubscribedLoader.CharacterLoadFailed += OnCharacterLoadFailed;
    }

    private void UnsubscribeLoaderEvents()
    {
        if (_eventSubscribedLoader == null)
            return;

        _eventSubscribedLoader.CharacterVisualLoaded -= OnCharacterVisualLoaded;
        _eventSubscribedLoader.CharacterLoadFailed -= OnCharacterLoadFailed;
        _eventSubscribedLoader = null;
    }

    private void OnCharacterVisualLoaded()
    {
        SetLoadingVisible(false);
        PositionCharacterAtSpawnPoint();
    }

    private void PositionCharacterAtSpawnPoint()
    {
        if (previewSpawnPoint == null)
            return;

        var loader = GetOrFindCharacterLoader();
        if (loader == null)
            return;

        var character = loader.GetLoadedCharacter();
        if (character == null)
            return;

        // Position at spawn point but do NOT parent — the customizer canvas
        // gets disabled after save, which would hide a parented character.
        character.transform.position = previewSpawnPoint.position;
        character.transform.rotation = previewSpawnPoint.rotation;
    }

    private void OnCharacterLoadFailed(string _)
    {
        SetLoadingVisible(false);
    }

    private void SetLoadingVisible(bool visible)
    {
        if (loadingGameObject != null)
            loadingGameObject.SetActive(visible);
    }

}
}
