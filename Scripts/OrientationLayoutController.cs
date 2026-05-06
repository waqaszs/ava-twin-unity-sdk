using UnityEngine;
using UnityEngine.UI;
using AvaTwin;
using System.Collections;

public class OrientationLayoutController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private RectTransform bg;
    [SerializeField] private RectTransform loading;
    [SerializeField] private RectTransform skinTone;
    [SerializeField] private RectTransform categories;
    [SerializeField] private RectTransform variations;
    [SerializeField] private RectTransform verticalLayoutRoot;
    private CanvasScaler canvasScaler;

    [Header("Portrait")]
    [SerializeField] private Vector2 portraitReferenceResolution = new Vector2(1080, 1920);

    [Header("Landscape")]
    [SerializeField] private Vector2 landscapeReferenceResolution = new Vector2(1920, 1080);

    [Header("Widths")]
    [SerializeField] private float portraitWidth = 250f;
    [SerializeField] private float landscapeWidth = 380f;
    [SerializeField] private int layoutRebuildFrames = 2;
    
    // [SerializeField] private float landscapeCategoriesY = -214f;
    // [SerializeField] private float portraitCategoriesY = -270f;
    
    // [SerializeField] private float landscapeVariationsTop = 260f;
    // [SerializeField] private float portraitVariationsTop = 330f;
    [SerializeField] private AvaTwinPreviewCameraController previewCameraController;

    private bool _isPortrait;
    private Coroutine _layoutRefreshRoutine;

    private void Awake()
    {
        canvasScaler = GetComponent<CanvasScaler>();
    }

    private void Start()
    {
        ApplyIfChanged(true);
        RequestLayoutRefresh();
        
        if (previewCameraController == null)
            previewCameraController = FindObjectOfType<AvaTwinPreviewCameraController>();
    }

    private void Update()
    {
        ApplyIfChanged(false); // catches runtime rotation
    }

    private void ApplyIfChanged(bool force)
    {
        // More reliable than Screen.orientation in many cases
        bool nowPortrait = Screen.height >= Screen.width;
        if (!force && nowPortrait == _isPortrait) return;

        _isPortrait = nowPortrait;
        ApplyLayout(_isPortrait);
    }

    private void ApplyLayout(bool portrait)
    {
        if (canvasScaler != null)
        {
            canvasScaler.referenceResolution = portrait
                ? portraitReferenceResolution
                : landscapeReferenceResolution;
        }

        float targetWidth = portrait ? portraitWidth : landscapeWidth;
        // float categoriesY = portrait ? portraitCategoriesY : landscapeCategoriesY;
        // float variationsTop = portrait ? portraitVariationsTop : landscapeVariationsTop;

        if (bg != null)
        {
            bg.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        }

        if (loading != null)
        {
            loading.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        }
        
        if (skinTone != null)
        {
            // For vertically stretched RectTransform, Top is controlled by offsetMax.y (inverted sign).
            // variations.offsetMax = new Vector2(variations.offsetMax.x, -variationsTop);
            skinTone.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        }

        if (categories != null)
        {
            // categories.anchoredPosition = new Vector2(categories.anchoredPosition.x, categoriesY);
            categories.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        }

        if (variations != null)
        {
            // For vertically stretched RectTransform, Top is controlled by offsetMax.y (inverted sign).
            // variations.offsetMax = new Vector2(variations.offsetMax.x, -variationsTop);
            variations.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        }

        if (previewCameraController != null)
            previewCameraController.ApplyOrientation(portrait);

        RequestLayoutRefresh();
    }

    public void RequestLayoutRefresh()
    {
        if (!isActiveAndEnabled) return;

        if (_layoutRefreshRoutine != null)
        {
            StopCoroutine(_layoutRefreshRoutine);
        }

        _layoutRefreshRoutine = StartCoroutine(RefreshLayoutOverFrames());
    }

    public void RefreshLayoutNow()
    {
        ForceRebuildLayouts();
    }

    private IEnumerator RefreshLayoutOverFrames()
    {
        int frames = Mathf.Max(1, layoutRebuildFrames);

        for (int i = 0; i < frames; i++)
        {
            yield return null; // wait for late runtime UI/content changes
            ForceRebuildLayouts();
        }

        _layoutRefreshRoutine = null;
    }

    private void ForceRebuildLayouts()
    {
        Canvas.ForceUpdateCanvases();

        if (verticalLayoutRoot != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(verticalLayoutRoot);
        }

        if (skinTone != null) LayoutRebuilder.ForceRebuildLayoutImmediate(skinTone);
        if (categories != null) LayoutRebuilder.ForceRebuildLayoutImmediate(categories);
        if (variations != null) LayoutRebuilder.ForceRebuildLayoutImmediate(variations);
        if (loading != null) LayoutRebuilder.ForceRebuildLayoutImmediate(loading);
        if (bg != null) LayoutRebuilder.ForceRebuildLayoutImmediate(bg);
    }
}