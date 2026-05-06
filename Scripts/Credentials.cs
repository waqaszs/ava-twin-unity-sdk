using UnityEngine;

namespace AvaTwin
{

[CreateAssetMenu(
    fileName = "Credentials",
    menuName = "Ava-Twin/Credentials",
    order = 1)]
public sealed class Credentials : ScriptableObject
{
    [SerializeField] private string appId = string.Empty;
    [SerializeField] private string apiKey = string.Empty;

    public string AppId => appId;
    public string ApiKey => apiKey;
}

} // namespace AvaTwin
