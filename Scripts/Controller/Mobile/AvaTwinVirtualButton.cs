using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace AvaTwin
{
    /// <summary>
    /// Virtual button for mobile input.
    /// Fires a bool event for held actions and a void event for one-shot clicks.
    /// </summary>
    public class AvaTwinVirtualButton : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        [Header("Output")]
        public UnityEvent<bool> buttonStateEvent;
        public UnityEvent buttonClickEvent;

        public bool IsPressed { get; private set; }

        public void OnPointerDown(PointerEventData eventData)
        {
            IsPressed = true;
            buttonStateEvent?.Invoke(true);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            IsPressed = false;
            buttonStateEvent?.Invoke(false);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            buttonClickEvent?.Invoke();
        }
    }
}
