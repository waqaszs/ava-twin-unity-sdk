using UnityEngine;

namespace AvaTwin
{
    /// <summary>
    /// Simple third-person camera that follows and orbits a target.
    /// Attach to the Main Camera. Set target to the character's camera target transform.
    /// </summary>
    public class AvaTwinCameraFollow : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The transform to follow (typically the CameraTarget on the character).")]
        public Transform target;

        [Header("Distance")]
        public float distance = 3.5f;
        public float minDistance = 1f;
        public float maxDistance = 10f;

        [Header("Smoothing")]
        public float followSpeed = 10f;

        [Header("Collision")]
        [Tooltip("Layers the camera collides with to avoid clipping through walls.")]
        public LayerMask collisionLayers = ~0;
        public float collisionRadius = 0.2f;

        private void LateUpdate()
        {
            if (target == null) return;

            // The target's rotation is set by AvaTwinCharacterController.CameraRotation()
            // We just need to position the camera behind the target at the right distance

            Vector3 desiredPosition = target.position - target.forward * distance;

            // Simple collision: raycast from target to desired position
            if (Physics.SphereCast(target.position, collisionRadius,
                (desiredPosition - target.position).normalized,
                out RaycastHit hit, distance, collisionLayers,
                QueryTriggerInteraction.Ignore))
            {
                desiredPosition = hit.point + hit.normal * collisionRadius;
            }

            transform.position = Vector3.Lerp(transform.position, desiredPosition, Time.deltaTime * followSpeed);
            transform.LookAt(target.position);
        }
    }
}
