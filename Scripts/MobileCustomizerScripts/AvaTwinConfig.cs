using UnityEngine;

namespace AvaTwin
{
    [CreateAssetMenu(fileName = "AvaTwinConfig", menuName = "Ava-Twin/Customizer Config")]
    public class AvaTwinConfig : ScriptableObject
    {
        [Header("API")]
        [Tooltip("Base URL for the customizer API.")]
        public string baseApiUrl = "https://customizer.ava-twin.me";
    }
}
