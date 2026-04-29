using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace AvaTwin
{
    /// <summary>
    /// Touch zone that reports drag deltas (trackpad-style).
    /// Handle appears at the touch point and disappears on release.
    /// </summary>
    public class AvaTwinVirtualTouchZone : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [Header("References")]
        [SerializeField] private RectTransform container;
        [SerializeField] private RectTransform handle;

        [Header("Settings")]
        [SerializeField] private float magnitudeMultiplier = 1f;
        [SerializeField] private float clampMagnitude = 100f;
        [SerializeField] private bool invertX;
        [SerializeField] private bool invertY;

        [Header("Output")]
        public UnityEvent<Vector2> outputEvent;

        private Vector2 _previousPointerPosition;
        private Canvas _canvas;
        private Camera _canvasCamera;

        private void Start()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                _canvasCamera = _canvas.worldCamera;

            if (handle != null)
                handle.gameObject.SetActive(false);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                container, eventData.position, _canvasCamera, out Vector2 localPoint);

            if (handle != null)
            {
                handle.gameObject.SetActive(true);
                handle.anchoredPosition = localPoint;
            }

            _previousPointerPosition = eventData.position;
        }

        public void OnDrag(PointerEventData eventData)
        {
            Vector2 delta = eventData.position - _previousPointerPosition;
            _previousPointerPosition = eventData.position;

            delta = Vector2.ClampMagnitude(delta, clampMagnitude);

            Vector2 output = new Vector2(
                invertX ? -delta.x : delta.x,
                invertY ? -delta.y : delta.y) * magnitudeMultiplier;

            outputEvent?.Invoke(output);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (handle != null)
                handle.gameObject.SetActive(false);

            outputEvent?.Invoke(Vector2.zero);
        }
    }
}
