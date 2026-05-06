using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// Third-person character controller for Ava-Twin avatars.
    /// Handles camera-relative movement, jumping, gravity, and animator parameters.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(Animator))]
    public class AvaTwinCharacterController : MonoBehaviour
    {
        [Header("Movement")]
        public float moveSpeed = 2.0f;
        public float sprintSpeed = 5.335f;
        public float rotationSmoothTime = 0.12f;
        public float speedChangeRate = 100.0f;

        [Header("Jump & Gravity")]
        public float jumpHeight = 2.5f;
        public float gravity = -15.0f;
        public float jumpTimeout = 0.5f;
        public float fallTimeout = 0.15f;

        [Header("Flying")]
        public float flySpeed = 8f;
        public float flySprintSpeed = 16f;

        [Header("Grounding")]
        public float groundedOffset = 0.14f;
        public float groundedRadius = 0.28f;
        public LayerMask groundLayers = ~0;
        [HideInInspector] public bool grounded = true;

        [Header("Options")]
        [Tooltip("When enabled, character moves sideways without turning (strafe movement).")]
        public bool enableStrafe = false;
        [Tooltip("When enabled, character walks/runs backward without turning around.")]
        public bool enableBackwardMovement = false;

        [Header("Camera")]
        [Tooltip("The transform the camera follows. Typically a child at head height.")]
        [SerializeField] private Transform cameraTarget;
        public float topClamp = 70.0f;
        public float bottomClamp = -30.0f;

        // Private state
        private CharacterController _controller;
        private Animator _animator;
        private AvaTwinInput _input;
        private Transform _mainCamera;

        private float _speed;
        private float _animBlend;
        private float _targetRotation;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _cameraYaw;
        private float _cameraPitch;
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;
        private const float TerminalVelocity = 53.0f;
        private const float Threshold = 0.01f;
        private bool _isFlying;

        // Animator hashes
        private static readonly int AnimSpeed = Animator.StringToHash("Speed");
        private static readonly int AnimGrounded = Animator.StringToHash("Grounded");
        private static readonly int AnimJump = Animator.StringToHash("Jump");
        private static readonly int AnimMoveX = Animator.StringToHash("MoveX");
        private static readonly int AnimMoveY = Animator.StringToHash("MoveY");
        private static readonly int AnimFlying = Animator.StringToHash("Flying");
        // Note: FreeFall parameter removed — not in the default AnimatorController
        // TODO: Add MotionSpeed parameter to AnimatorController, then uncomment:
        // private static readonly int AnimMotionSpeed = Animator.StringToHash("MotionSpeed");
        // In Move(), after setting AnimSpeed:
        //   float motionSpeed = _input.analogMovement ? _input.move.magnitude : 1f;
        //   _animator?.SetFloat(AnimMotionSpeed, motionSpeed);

        private void Start()
        {
            _controller = GetComponent<CharacterController>();
            _animator = GetComponent<Animator>();
            _input = GetComponent<AvaTwinInput>();
            _mainCamera = Camera.main?.transform;

            if (cameraTarget != null)
            {
                _cameraYaw = cameraTarget.eulerAngles.y;
                float pitch = cameraTarget.eulerAngles.x;
                if (pitch > 180f) pitch -= 360f; // normalize 350 -> -10
                _cameraPitch = pitch;
            }

            _jumpTimeoutDelta = jumpTimeout;
            _fallTimeoutDelta = fallTimeout;
        }

        private void Update()
        {
            // Lazy init — handles runtime AddComponent where Start order isn't guaranteed
            if (_controller == null) _controller = GetComponent<CharacterController>();
            if (_animator == null) _animator = GetComponent<Animator>();
            if (_input == null) _input = GetComponent<AvaTwinInput>();
            if (_input == null || _controller == null) return;
            if (_mainCamera == null) _mainCamera = Camera.main?.transform;

            // Toggle fly mode
            if (_input.fly)
            {
                _isFlying = !_isFlying;
                _input.fly = false; // consume the toggle
                _animator?.SetBool(AnimFlying, _isFlying);
                if (_isFlying) _verticalVelocity = 0f; // stop falling
            }

            // Smoothly blend the Flying layer weight (index 1)
            if (_animator != null)
            {
                float targetWeight = _isFlying ? 1f : 0f;
                float currentWeight = _animator.GetLayerWeight(1);
                float blendedWeight = Mathf.MoveTowards(currentWeight, targetWeight, Time.deltaTime * 1.5f);
                _animator.SetLayerWeight(1, blendedWeight);
            }

            if (_isFlying)
            {
                FlyMovement();
            }
            else
            {
                GroundCheck();
                JumpAndGravity();
                Move();
            }
        }

        private void LateUpdate()
        {
            CameraRotation();
        }

        private void FlyMovement()
        {
            float targetSpeed = _input.sprint ? flySprintSpeed : flySpeed;
            if (_input.move == Vector2.zero && !_input.jump) targetSpeed = 0f;

            float cameraYaw = _mainCamera != null ? _mainCamera.eulerAngles.y : 0f;
            float cameraPitch = _mainCamera != null ? _mainCamera.eulerAngles.x : 0f;
            if (cameraPitch > 180f) cameraPitch -= 360f;

            Vector3 moveDir = Vector3.zero;

            if (_input.move != Vector2.zero)
            {
                float inputAngle = Mathf.Atan2(_input.move.x, _input.move.y) * Mathf.Rad2Deg;
                // Fly in the direction the camera is looking (including pitch for up/down)
                moveDir = Quaternion.Euler(cameraPitch, cameraYaw + inputAngle, 0) * Vector3.forward;

                // Rotate character to face movement direction (yaw only)
                float targetRot = inputAngle + cameraYaw;
                float rot = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetRot, ref _rotationVelocity, rotationSmoothTime);
                transform.rotation = Quaternion.Euler(0, rot, 0);
            }

            // Vertical: jump = ascend
            if (_input.jump)
            {
                moveDir += Vector3.up * 0.5f;
                _input.jump = false;
            }

            _controller.Move(moveDir.normalized * (targetSpeed * Time.deltaTime));

            // Animator speed for fly idle vs fly dive
            float speed = _input.move != Vector2.zero ? 1f : 0f;
            _animator?.SetFloat(AnimSpeed, speed);

            // Set MoveX/MoveY for blend tree (even in fly mode)
            _animator?.SetFloat(AnimMoveX, _input.move.x, 0.15f, Time.deltaTime);
            _animator?.SetFloat(AnimMoveY, _input.move.y, 0.15f, Time.deltaTime);
        }

        private void GroundCheck()
        {
            var pos = transform.position;
            var spherePos = new Vector3(pos.x, pos.y - groundedOffset, pos.z);
            grounded = Physics.CheckSphere(spherePos, groundedRadius, groundLayers, QueryTriggerInteraction.Ignore);
            if (_animator) _animator.SetBool(AnimGrounded, grounded);
        }

        private void Move()
        {
            float targetSpeed = _input.sprint ? sprintSpeed : moveSpeed;
            if (_input.move == Vector2.zero) targetSpeed = 0f;

            float currentHSpeed = new Vector3(_controller.velocity.x, 0, _controller.velocity.z).magnitude;
            float inputMag = _input.analogMovement ? _input.move.magnitude : (_input.move != Vector2.zero ? 1f : 0f);

            _speed = Mathf.Lerp(currentHSpeed, targetSpeed * inputMag, Time.deltaTime * speedChangeRate);
            _speed = Mathf.Round(_speed * 1000f) / 1000f;
            // Snap to target when within tolerance (avoid perpetual micro-lerp)
            if (Mathf.Abs(_speed - targetSpeed * inputMag) < 0.1f)
                _speed = targetSpeed * inputMag;

            // Animation blend: 0 = idle, 0.5 = walk, 1.0 = run (matches blend tree thresholds)
            float targetBlend = targetSpeed > 0f ? (_input.sprint ? 1.0f : 0.5f) : 0f;
            _animBlend = Mathf.Lerp(_animBlend, targetBlend, Time.deltaTime * speedChangeRate);
            if (_animBlend < 0.01f) _animBlend = 0f;

            var inputDir = new Vector3(_input.move.x, 0, _input.move.y).normalized;
            float cameraYaw = _mainCamera != null ? _mainCamera.eulerAngles.y : 0f;

            if (_input.move != Vector2.zero)
            {
                float inputAngle = Mathf.Atan2(inputDir.x, inputDir.z) * Mathf.Rad2Deg;

                if (enableStrafe)
                {
                    // Face camera direction, move relative to camera
                    _targetRotation = cameraYaw;
                    float rot = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation, ref _rotationVelocity, rotationSmoothTime);
                    transform.rotation = Quaternion.Euler(0, rot, 0);

                    var moveDir = Quaternion.Euler(0, cameraYaw + inputAngle, 0) * Vector3.forward;
                    _controller.Move(moveDir.normalized * (_speed * Time.deltaTime) + new Vector3(0, _verticalVelocity, 0) * Time.deltaTime);
                }
                else if (enableBackwardMovement && _input.move.y < 0)
                {
                    // Moving backward: keep facing camera direction, move in reverse
                    _targetRotation = cameraYaw;
                    float rot = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation, ref _rotationVelocity, rotationSmoothTime);
                    transform.rotation = Quaternion.Euler(0, rot, 0);

                    var moveDir = Quaternion.Euler(0, cameraYaw + inputAngle, 0) * Vector3.forward;
                    _controller.Move(moveDir.normalized * (_speed * Time.deltaTime) + new Vector3(0, _verticalVelocity, 0) * Time.deltaTime);
                }
                else
                {
                    // Default: rotate to face movement direction
                    _targetRotation = inputAngle + cameraYaw;
                    float rot = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation, ref _rotationVelocity, rotationSmoothTime);
                    transform.rotation = Quaternion.Euler(0, rot, 0);

                    var moveDir = Quaternion.Euler(0, _targetRotation, 0) * Vector3.forward;
                    _controller.Move(moveDir.normalized * (_speed * Time.deltaTime) + new Vector3(0, _verticalVelocity, 0) * Time.deltaTime);
                }
            }
            else
            {
                // No input — apply only gravity
                _controller.Move(new Vector3(0, _verticalVelocity, 0) * Time.deltaTime);
            }

            if (_animator)
            {
                // Speed multiplier: walk positions at 1, run positions at 2 in the blend tree
                float speedMultiplier = _input.sprint ? 2f : 1f;
                float dampTime = 0.15f; // smooth blending between walk/run

                if (enableStrafe || (enableBackwardMovement && _input.move.y < 0))
                {
                    // Camera-relative: send actual input direction scaled for walk/run
                    _animator.SetFloat(AnimMoveX, _input.move.x * speedMultiplier, dampTime, Time.deltaTime);
                    _animator.SetFloat(AnimMoveY, _input.move.y * speedMultiplier, dampTime, Time.deltaTime);
                }
                else
                {
                    // Character faces movement direction: always "forward" from character's perspective
                    float forwardAmount = _input.move != Vector2.zero ? speedMultiplier : 0f;
                    _animator.SetFloat(AnimMoveX, 0f, dampTime, Time.deltaTime);
                    _animator.SetFloat(AnimMoveY, forwardAmount, dampTime, Time.deltaTime);
                }

                _animator.SetFloat(AnimSpeed, _animBlend);
                _animator.speed = 1f;
            }
        }

        private void JumpAndGravity()
        {
            if (grounded)
            {
                // Reset fall timeout (coyote time)
                _fallTimeoutDelta = fallTimeout;

                if (_verticalVelocity < 0f) _verticalVelocity = -2f;
                if (_animator) _animator.SetBool(AnimJump, false);

                if (_input.jump && _jumpTimeoutDelta <= 0f)
                {
                    _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                    if (_animator) _animator.SetBool(AnimJump, true);
                }
                _input.jump = false;

                if (_jumpTimeoutDelta >= 0f)
                    _jumpTimeoutDelta -= Time.deltaTime;
            }
            else
            {
                // Reset jump timeout
                _jumpTimeoutDelta = jumpTimeout;

                // Coyote time: only update grounded animator param after fall timeout expires
                if (_fallTimeoutDelta >= 0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else
                {
                    if (_animator) _animator.SetBool(AnimGrounded, false);
                }

                // Don't clear jump here — let it buffer so pressing
                // jump just before landing still triggers on the next grounded frame.
                // The grounded branch already clears it after processing.
            }

            if (_verticalVelocity > -TerminalVelocity)
                _verticalVelocity += gravity * Time.deltaTime;
        }

        private void CameraRotation()
        {
            if (cameraTarget == null || _input == null) return;
            if (_input.look.sqrMagnitude >= Threshold)
            {
                // Mouse/pointer look input is already a per-frame delta (pixels moved),
                // so no deltaTime multiplication — that would make it frame-rate dependent.
                _cameraYaw += _input.look.x;
                _cameraPitch -= _input.look.y;
            }
            _cameraYaw = ClampAngle(_cameraYaw, float.MinValue, float.MaxValue);
            _cameraPitch = ClampAngle(_cameraPitch, bottomClamp, topClamp);
            cameraTarget.rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0);
        }

        /// <summary>
        /// Sets the camera target transform at runtime.
        /// Use this instead of reflection to assign the camera pivot after spawning a character.
        /// </summary>
        public void SetCameraTarget(Transform target)
        {
            cameraTarget = target;
            if (cameraTarget != null)
            {
                _cameraYaw = cameraTarget.eulerAngles.y;
                float pitch = cameraTarget.eulerAngles.x;
                if (pitch > 180f) pitch -= 360f; // normalize 350 -> -10
                _cameraPitch = pitch;
            }
        }

        private static float ClampAngle(float angle, float min, float max)
        {
            if (angle < -360f) angle += 360f;
            if (angle > 360f) angle -= 360f;
            return Mathf.Clamp(angle, min, max);
        }
    }
}
