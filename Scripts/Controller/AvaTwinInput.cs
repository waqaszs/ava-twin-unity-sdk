using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AvaTwin
{
    /// <summary>
    /// Input state container for the Ava-Twin character controller.
    /// Receives input from InputSystem callbacks, mobile UI, or external scripts.
    /// </summary>
    public class AvaTwinInput : MonoBehaviour
    {
        [Header("Input Values")]
        public Vector2 move;
        public Vector2 look;
        public bool jump;
        public bool sprint;
        public bool fly;

        [Header("Settings")]
        public bool analogMovement;
        public bool cursorLocked = false;
        public bool cursorInputForLook = true;

        // Public setters — called by mobile UI bridge or external scripts
        public void MoveInput(Vector2 newMove) => move = newMove;
        public void LookInput(Vector2 newLook) => look = newLook;
        public void JumpInput(bool newJump) => jump = newJump;
        public void SprintInput(bool newSprint) => sprint = newSprint;

#if ENABLE_INPUT_SYSTEM
        // InputSystem message callbacks (auto-invoked by PlayerInput component)
        public void OnMove(InputValue value) => MoveInput(value.Get<Vector2>());
        public void OnLook(InputValue value) { if (cursorInputForLook) LookInput(value.Get<Vector2>()); }
        public void OnJump(InputValue value) => JumpInput(value.isPressed);
        public void OnSprint(InputValue value) => SprintInput(value.isPressed);
        public void OnFly(InputValue value) => fly = value.isPressed;
#endif

        private void Start()
        {
            SetCursorState(cursorLocked);
        }

        private void Update()
        {
            // Fallback: read from legacy Input Manager when no PlayerInput is driving values
#if ENABLE_INPUT_SYSTEM
            if (GetComponent<UnityEngine.InputSystem.PlayerInput>() != null) return;
#endif
            move = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
            look = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
            if (Input.GetButtonDown("Jump")) jump = true;
            if (Input.GetButtonUp("Jump")) jump = false;
            sprint = Input.GetKey(KeyCode.LeftShift);
            if (Input.GetKeyDown(KeyCode.F)) fly = !fly; // F key toggles fly mode
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            SetCursorState(cursorLocked);
        }

        private void SetCursorState(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
