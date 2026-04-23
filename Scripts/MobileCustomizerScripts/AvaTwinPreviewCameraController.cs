using UnityEngine;

namespace AvaTwin
{
public class AvaTwinPreviewCameraController : MonoBehaviour
{
    private const string HeadCategory = "head";
    private const string TopCategory = "top";
    private const string BottomCategory = "bottom";
    private const string ShoesCategory = "shoes";

    [SerializeField] private Transform previewCamera;
    // [SerializeField] private Camera previewCameraComponent;
    [SerializeField] private Transform headFocusPoint;
    [SerializeField] private Transform topFocusPoint;
    [SerializeField] private Transform bottomFocusPoint;
    [SerializeField] private Transform shoesFocusPoint;
    [SerializeField] private float cameraTransitionDuration = 0.35f;
    [SerializeField] private float landscapeFov = 40f;
    [SerializeField] private float portraitFov = 70f;

    private Coroutine _cameraTransitionRoutine;

    private void OnEnable()
    {
        // Ensure FOV is correct on first spawn/activation too.
        ApplyOrientation(Screen.height >= Screen.width);
    }

    public void TransitionToCategory(string categoryKey)
    {
        var focusPoint = GetFocusPointForCategory(categoryKey);
        if (focusPoint == null)
            return;

        TransitionCameraToFocus(focusPoint);
    }

    public void SnapToCategory(string categoryKey)
    {
        var focusPoint = GetFocusPointForCategory(categoryKey);
        if (focusPoint == null)
            return;

        SnapCameraToFocus(focusPoint);
    }

    public void ApplyOrientation(bool isPortrait)
    {
        var cameraToUse = previewCamera.GetComponent<Camera>();
        if (cameraToUse == null)
            return;

        cameraToUse.fieldOfView = isPortrait ? portraitFov : landscapeFov;
    }

    private Transform GetFocusPointForCategory(string categoryKey)
    {
        switch (categoryKey)
        {
            case HeadCategory: return headFocusPoint;
            case TopCategory: return topFocusPoint;
            case BottomCategory: return bottomFocusPoint;
            case ShoesCategory: return shoesFocusPoint;
            default: return null;
        }
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
