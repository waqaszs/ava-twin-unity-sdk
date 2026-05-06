using UnityEngine;

namespace AvaTwin
{

/// <summary>
/// Simple auto-rotate behaviour added at runtime when no character controller
/// is assigned. Rotates the object slowly so the loaded avatar is visible
/// from all angles in the demo scene.
/// </summary>
[DisallowMultipleComponent]
public sealed class AutoRotate : MonoBehaviour
{
    [SerializeField] private float degreesPerSecond = 30f;

    private void Update()
    {
        transform.Rotate(Vector3.up, degreesPerSecond * Time.deltaTime, Space.World);
    }
}

} // namespace AvaTwin
