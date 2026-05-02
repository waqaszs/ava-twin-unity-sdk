using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace AvaTwin
{
    /// <summary>
    /// Virtual joystick for mobile movement/look input.
    /// Drag the handle within the container; outputs a normalized Vector2.
    /// </summary>
    public class AvaTwinVirtualJoystick : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [Header("References")]
        [SerializeField] private RectTransform container;
        [SerializeField] private RectTransform handle;

        [Header("Settings")]
        [SerializeField] private float joystickRange = 50f;
        [SerializeField] private float magnitudeMultiplier = 1f;
        [SerializeField] private bool invertX;
        [SerializeField] private bool invertY;

        [Header("Output")]
        public UnityEvent<Vector2> outputEvent;

        private Canvas _canvas;
        private Camera _canvasCamera;

        private void Start()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                _canvasCamera = _canvas.worldCamera;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            OnDrag(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                container, eventData.position, _canvasCamera, out Vector2 localPoint);

            // Normalize to [-1, 1] based on container size
            Vector2 containerSize = container.sizeDelta;
            Vector2 normalized = new Vector2(
                localPoint.x / (containerSize.x * 0.5f),
                localPoint.y / (containerSize.y * 0.5f));

            // Clamp to unit circle
            normalized = Vector2.ClampMagnitude(normalized, 1f);

            // Move handle visual
            handle.anchoredPosition = normalized * joystickRange;

            // Apply inversion and multiplier
            Vector2 output = new Vector2(
                invertX ? -normalized.x : normalized.x,
                invertY ? -normalized.y : normalized.y) * magnitudeMultiplier;

            outputEvent?.Invoke(output);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            handle.anchoredPosition = Vector2.zero;
            outputEvent?.Invoke(Vector2.zero);
        }
    }
}
