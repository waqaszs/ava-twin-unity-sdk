using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// Bridges mobile UI widgets to the AvaTwinInput component.
    /// Assign these methods as UnityEvent targets on joysticks, touch zones, and buttons.
    /// </summary>
    public class AvaTwinMobileInputBridge : MonoBehaviour
    {
        [SerializeField] private AvaTwinInput _input;

        public void VirtualMoveInput(Vector2 value) => _input.MoveInput(value);
        public void VirtualLookInput(Vector2 value) => _input.LookInput(value);
        public void VirtualJumpInput(bool value) => _input.JumpInput(value);
        public void VirtualSprintInput(bool value) => _input.SprintInput(value);
    }
}
