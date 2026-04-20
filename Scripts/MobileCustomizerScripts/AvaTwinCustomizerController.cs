using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace AvaTwin
{
public class AvaTwinCustomizerController : MonoBehaviour
{
    private const string BaseCategory = "base";
    private const string HeadCategory = "head";
    private const string TopCategory = "top";
    private const string BottomCategory = "bottom";
    private const string ShoesCategory = "shoes";

    [Serializable]
    public class SkinToneOption
    {
        public string id;
        public string label;
        public string hex;
    }

    [Header("Config")]
    [SerializeField] private AvaTwinConfig config;
    [Tooltip("If Config is not assigned in inspector, controller will try to load this from Resources.")]
    [SerializeField] private string configResourcePath = "AvaTwinConfig";

    [Header("UI Containers")]
    [SerializeField] private Transform modelContainer;
    [SerializeField] private Transform headContainer;
    [SerializeField] private Transform topContainer;
    [SerializeField] private Transform bottomContainer;
    [SerializeField] private Transform shoesContainer;
    [SerializeField] private Transform skinToneContainer;
    [SerializeField] private AvaVariationButton buttonPrefab;
    [SerializeField] private Button skinToneButtonPrefab;
    [SerializeField] private bool showTopByDefault = true;
    [SerializeField] private SkinToneOption[] skinTones = new[]
    {
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

    [Header("Preview Camera Focus")]
    [SerializeField] private Transform previewCamera;
    [SerializeField] private Transform headFocusPoint;
    [SerializeField] private Transform topFocusPoint;
    [SerializeField] private Transform bottomFocusPoint;
    [SerializeField] private Transform shoesFocusPoint;
    [SerializeField] private float cameraTransitionDuration = 0.35f;
    [SerializeField] private bool snapToTopOnInitialize = true;

    // Events
    public event Action<string> OnAvatarUrlReady;
    public event Action<string, string> OnAvatarUrlAndSkinToneReady;
    public event Action<string> OnError;
    public event Action OnReady;

    private AvaTwinApiClient _api;
    private bool _isInitialized;
    private bool _isBusy;
    private int _previewRequestId;
    private string _selectedSkinToneHex = "#FFDFC4";
    private CharacterLoader _eventSubscribedLoader;
    private Coroutine _cameraTransitionRoutine;
    private readonly List<SkinToneButtonBinding> _skinToneButtons = new List<SkinToneButtonBinding>();
    private readonly Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
    private readonly Dictionary<string, string> _selectedByCategory = new Dictionary<string, string>();
    private readonly Dictionary<string, List<AvaVariationButton>> _buttonsByCategory = new Dictionary<string, List<AvaVariationButton>>();

    private class SkinToneButtonBinding
    {
        public Button button;
        public string hex;
        public GameObject selectedIndicator;
    }

    private void OnEnable()
    {
        if (autoInitializeOnEnable)
            Initialize();
    }

    private void OnDisable()
    {
        UnsubscribeLoaderEvents();
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

            // Get credentials from CharacterLoader (single source of truth)
            var loader = GetOrFindCharacterLoader();
            Credentials creds = loader != null ? loader.GetCredentials() : null;

            _api = new AvaTwinApiClient(config, creds);
            _selectedByCategory.Clear();
            _buttonsByCategory.Clear();
            _selectedSkinToneHex = "#FFDFC4";

            string token = await _api.MintTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Token mint returned an empty token.");

            var library = await _api.GetLibraryAsync();

            if (library?.library == null || !library.library.ContainsKey("generic"))
                throw new Exception("Library missing generic data");

            var generic = library.library["generic"];
            LogGenericCategories(generic);
            BuildCategoryUI(generic, BaseCategory, modelContainer);
            BuildCategoryUI(generic, HeadCategory, headContainer);
            BuildCategoryUI(generic, TopCategory, topContainer);
            BuildCategoryUI(generic, BottomCategory, bottomContainer);
            BuildCategoryUI(generic, ShoesCategory, shoesContainer);
            BuildSkinToneUI();

            _isInitialized = true;
            if (loader != null)
                loader.SetSkinToneHex(_selectedSkinToneHex);
            if (showTopByDefault)
                ShowTopVariations();
            else
                ShowAllVariationPanels();

            if (snapToTopOnInitialize)
                SnapCameraToFocus(topFocusPoint);
            OnReady?.Invoke();
            if (loadPreviewOnInitialize)
                RequestPreviewLoad();
        }
        catch (Exception ex)
        {
            Debug.LogError("[Ava-Twin] Customizer encountered an error.");
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
            string avatarId = BuildAvatarIdFromSelection();
            var resolved = await _api.ResolveAvatarAsync(avatarId);

            if (string.IsNullOrWhiteSpace(resolved?.url))
                throw new Exception($"Resolve failed: {resolved?.error ?? "No URL"}");

            OnAvatarUrlReady?.Invoke(resolved.url);
            OnAvatarUrlAndSkinToneReady?.Invoke(resolved.url, _selectedSkinToneHex);
            var loader = GetOrFindCharacterLoader();
            if (loader != null)
            {
                loader.SetSkinToneHex(_selectedSkinToneHex);
                loader.LoadCharacterFromUrl(resolved.url);
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
            Debug.LogError("[Ava-Twin] Customizer encountered an error.");
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void BuildCategoryUI(
        Dictionary<string, List<AvatarVariation>> generic,
        string categoryKey,
        Transform container)
    {
        if (container == null)
            return;
        if (!generic.TryGetValue(categoryKey, out var items) || items == null || items.Count == 0)
            return;

        ClearChildren(container);

        var orderedItems = items
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.variationId))
            .OrderBy(item => item.sortOrder)
            .ThenBy(item => item.displayName)
            .ToList();

        if (orderedItems.Count == 0)
            return;

        var buttons = new List<AvaVariationButton>(orderedItems.Count);
        for (int i = 0; i < orderedItems.Count; i++)
        {
            var item = orderedItems[i];
            var button = Instantiate(buttonPrefab, container);
            buttons.Add(button);

            // Bind immediately so UI becomes interactive without waiting thumbnails.
            button.Bind(item, null, variationId =>
            {
                _selectedByCategory[categoryKey] = variationId;
                RefreshSelectionVisuals(categoryKey);
                if (livePreviewOnSelection)
                    RequestPreviewLoad();
            });

            _ = LoadAndApplyThumbnailAsync(button, item.variationId);
        }

        _buttonsByCategory[categoryKey] = buttons;
        EnsureValidSelection(categoryKey, orderedItems);
        RefreshSelectionVisuals(categoryKey);
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

    private string BuildAvatarIdFromSelection()
    {
        string model = GetRequiredSelection(BaseCategory);
        string head = GetRequiredSelection(HeadCategory);
        string top = GetRequiredSelection(TopCategory);
        string bottom = GetRequiredSelection(BottomCategory);
        string shoes = GetRequiredSelection(ShoesCategory);
        return $"generic_{model}_{head}_{top}_{bottom}_{shoes}";
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
            string avatarId = BuildAvatarIdFromSelection();
            var resolved = await _api.ResolveAvatarAsync(avatarId);

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
            if (tone == null || string.IsNullOrWhiteSpace(tone.hex))
                continue;

            var button = Instantiate(skinToneButtonPrefab, skinToneContainer);
            var label = button.GetComponentInChildren<Text>();
            if (label != null)
                label.text = string.IsNullOrWhiteSpace(tone.label) ? tone.id : tone.label;

            var swatch = button.GetComponent<Image>();
            if (swatch != null && ColorUtility.TryParseHtmlString(tone.hex, out var toneColor))
                swatch.color = toneColor;

            string toneHex = tone.hex;
            button.onClick.AddListener(() =>
            {
                _selectedSkinToneHex = toneHex;
                RefreshSkinToneSelectionVisuals();
                var loader = GetOrFindCharacterLoader();
                if (loader != null)
                    loader.SetSkinToneHex(_selectedSkinToneHex);
            });

            _skinToneButtons.Add(new SkinToneButtonBinding
            {
                button = button,
                hex = tone.hex,
                selectedIndicator = FindFirstChildImageObject(button)
            });
        }

        EnsureValidSkinToneSelection();
        RefreshSkinToneSelectionVisuals();
    }

    private void EnsureValidSkinToneSelection()
    {
        if (skinTones == null || skinTones.Length == 0)
            return;

        bool exists = skinTones.Any(tone =>
            tone != null &&
            !string.IsNullOrWhiteSpace(tone.hex) &&
            string.Equals(tone.hex, _selectedSkinToneHex, StringComparison.OrdinalIgnoreCase));

        if (!exists)
            _selectedSkinToneHex = skinTones[0].hex;
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

            bool isSelected = string.Equals(binding.hex, _selectedSkinToneHex, StringComparison.OrdinalIgnoreCase);
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

    public void ShowHeadVariations()
    {
        SetCategoryPanelVisibility(showHead: true, showTop: false, showBottom: false, showShoes: false);
        TransitionCameraToFocus(headFocusPoint);
    }

    public void ShowTopVariations()
    {
        SetCategoryPanelVisibility(showHead: false, showTop: true, showBottom: false, showShoes: false);
        TransitionCameraToFocus(topFocusPoint);
    }

    public void ShowBottomVariations()
    {
        SetCategoryPanelVisibility(showHead: false, showTop: false, showBottom: true, showShoes: false);
        TransitionCameraToFocus(bottomFocusPoint);
    }

    public void ShowShoesVariations()
    {
        SetCategoryPanelVisibility(showHead: false, showTop: false, showBottom: false, showShoes: true);
        TransitionCameraToFocus(shoesFocusPoint);
    }

    public void ShowAllVariationPanels()
    {
        SetCategoryPanelVisibility(showHead: true, showTop: true, showBottom: true, showShoes: true);
    }

    private void SetCategoryPanelVisibility(bool showHead, bool showTop, bool showBottom, bool showShoes)
    {
        if (headContainer != null)
            headContainer.gameObject.SetActive(showHead);
        if (topContainer != null)
            topContainer.gameObject.SetActive(showTop);
        if (bottomContainer != null)
            bottomContainer.gameObject.SetActive(showBottom);
        if (shoesContainer != null)
            shoesContainer.gameObject.SetActive(showShoes);
    }

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
        var tex = await GetThumb(variationId);
        if (button != null && tex != null)
            button.SetThumbnail(tex);
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

    private void TransitionCameraToFocus(Transform focusPoint)
    {
        if (previewCamera == null || focusPoint == null)
            return;

        if (_cameraTransitionRoutine != null)
            StopCoroutine(_cameraTransitionRoutine);
        _cameraTransitionRoutine = StartCoroutine(SmoothMoveCameraRoutine(focusPoint));
    }

    private void SnapCameraToFocus(Transform focusPoint)
    {
        if (previewCamera == null || focusPoint == null)
            return;

        previewCamera.position = focusPoint.position;
        previewCamera.rotation = focusPoint.rotation;
    }

    private System.Collections.IEnumerator SmoothMoveCameraRoutine(Transform focusPoint)
    {
        if (previewCamera == null || focusPoint == null)
            yield break;

        var startPos = previewCamera.position;
        var startRot = previewCamera.rotation;
        var endPos = focusPoint.position;
        var endRot = focusPoint.rotation;
        float duration = Mathf.Max(0.01f, cameraTransitionDuration);
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            previewCamera.position = Vector3.Lerp(startPos, endPos, eased);
            previewCamera.rotation = Quaternion.Slerp(startRot, endRot, eased);
            yield return null;
        }

        previewCamera.position = endPos;
        previewCamera.rotation = endRot;
        _cameraTransitionRoutine = null;
    }
}
}
