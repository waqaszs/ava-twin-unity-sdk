using UnityEngine;
using UnityEngine.UI;
using AvaTwin;

public class OrientationLayoutController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private RectTransform bg;
    [SerializeField] private RectTransform loading;
    [SerializeField] private RectTransform categories;
    [SerializeField] private RectTransform variations;
    private CanvasScaler canvasScaler;

    [Header("Portrait")]
    [SerializeField] private Vector2 portraitReferenceResolution = new Vector2(1080, 1920);

    [Header("Landscape")]
    [SerializeField] private Vector2 landscapeReferenceResolution = new Vector2(1920, 1080);

    [Header("Widths")]
    [SerializeField] private float portraitWidth = 250f;
    [SerializeField] private float landscapeWidth = 380f;
    
    [SerializeField] private float landscapeCategoriesY = -214f;
    [SerializeField] private float portraitCategoriesY = -270f;
    
    [SerializeField] private float landscapeVariationsTop = 260f;
    [SerializeField] private float portraitVariationsTop = 330f;
    [SerializeField] private AvaTwinPreviewCameraController previewCameraController;

    private bool _isPortrait;

    private void Awake()
    {
        canvasScaler = GetComponent<CanvasScaler>();
    }

    private void Start()
    {
        ApplyIfChanged(true);
        
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
        float categoriesY = portrait ? portraitCategoriesY : landscapeCategoriesY;
        float variationsTop = portrait ? portraitVariationsTop : landscapeVariationsTop;

        if (bg != null)
        {
            bg.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        }

        if (loading != null)
        {
            loading.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        }

        if (categories != null)
        {
            categories.anchoredPosition = new Vector2(categories.anchoredPosition.x, categoriesY);
        }

        if (variations != null)
        {
            // For vertically stretched RectTransform, Top is controlled by offsetMax.y (inverted sign).
            variations.offsetMax = new Vector2(variations.offsetMax.x, -variationsTop);
        }

        if (previewCameraController != null)
            previewCameraController.ApplyOrientation(portrait);
    }
}