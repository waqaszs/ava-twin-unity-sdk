using UnityEngine;
using UnityEngine.Serialization;

namespace AvaTwin
{
public class AvaTwinPreviewCameraController : MonoBehaviour
{
    [System.Serializable]
    private class CategoryFocusPoints
    {
        public Transform portrait;
        public Transform landscape;
    }

    private const string HeadCategory = "head";
    private const string TopCategory = "top";
    private const string BottomCategory = "bottom";
    private const string ShoesCategory = "shoes";

    [SerializeField] private Transform previewCamera;
    [Header("Focus Points")]
    [SerializeField] private Transform headPortraitFocusPoint;
    [SerializeField] private Transform topPortraitFocusPoint;
    [SerializeField] private Transform bottomPortraitFocusPoint;
    [SerializeField] private Transform shoesPortraitFocusPoint;
    [SerializeField] private Transform headLandscapeFocusPoint;
    [SerializeField] private Transform topLandscapeFocusPoint;
    [SerializeField] private Transform bottomLandscapeFocusPoint;
    [SerializeField] private Transform shoesLandscapeFocusPoint;
    [SerializeField] private float cameraTransitionDuration = 0.35f;
    [SerializeField] private float landscapeFov = 40f;
    [SerializeField] private float portraitFov = 70f;

    private Coroutine _cameraTransitionRoutine;
    private bool _isPortrait = true;
    private string _currentCategory = HeadCategory;

    private void OnEnable()
    {
        // Ensure FOV is correct on first spawn/activation too.
        ApplyOrientation(Screen.height >= Screen.width);
    }

    public void TransitionToCategory(string categoryKey)
    {
        _currentCategory = categoryKey;
        var focusPoint = GetFocusPointForCategory(categoryKey, _isPortrait);
        if (focusPoint == null)
            return;

        TransitionCameraToFocus(focusPoint);
    }

    public void SnapToCategory(string categoryKey)
    {
        _currentCategory = categoryKey;
        var focusPoint = GetFocusPointForCategory(categoryKey, _isPortrait);
        if (focusPoint == null)
            return;

        SnapCameraToFocus(focusPoint);
    }

    public void ApplyOrientation(bool isPortrait)
    {
        _isPortrait = isPortrait;

        if (previewCamera == null)
            return;

        var cameraToUse = previewCamera.GetComponent<Camera>();
        if (cameraToUse == null)
            return;

        cameraToUse.fieldOfView = isPortrait ? portraitFov : landscapeFov;

        // Keep current category in sync when orientation changes.
        var focusPoint = GetFocusPointForCategory(_currentCategory, _isPortrait);
        if (focusPoint != null)
            TransitionCameraToFocus(focusPoint);
    }

    private Transform GetFocusPointForCategory(string categoryKey, bool isPortrait)
    {
        CategoryFocusPoints points;
        switch (categoryKey)
        {
            case HeadCategory:
                points = new CategoryFocusPoints { portrait = headPortraitFocusPoint, landscape = headLandscapeFocusPoint };
                break;
            case TopCategory:
                points = new CategoryFocusPoints { portrait = topPortraitFocusPoint, landscape = topLandscapeFocusPoint };
                break;
            case BottomCategory:
                points = new CategoryFocusPoints { portrait = bottomPortraitFocusPoint, landscape = bottomLandscapeFocusPoint };
                break;
            case ShoesCategory:
                points = new CategoryFocusPoints { portrait = shoesPortraitFocusPoint, landscape = shoesLandscapeFocusPoint };
                break;
            default: return null;
        }

        Transform portraitFocus = points.portrait;
        Transform landscapeFocus = points.landscape;
        if (isPortrait)
            return portraitFocus != null ? portraitFocus : landscapeFocus;
        return landscapeFocus != null ? landscapeFocus : portraitFocus;
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
