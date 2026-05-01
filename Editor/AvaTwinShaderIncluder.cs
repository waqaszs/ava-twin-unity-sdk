using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaTwin.Editor
{
    /// <summary>
    /// Automatically adds Ava-Twin shaders to "Always Included Shaders" in GraphicsSettings.
    /// This ensures runtime-created materials (via CharacterLoader) work in builds
    /// where Unity would otherwise strip unreferenced shaders.
    /// Runs once on import and on every build.
    /// </summary>
    [InitializeOnLoad]
    internal static class AvaTwinShaderIncluder
    {
        private static readonly string[] ShaderNames =
        {
            "Ava-Twin/Stylized Builtin",
            "Ava-Twin/Stylized",
        };

        static AvaTwinShaderIncluder()
        {
            EnsureShadersIncluded();
        }

        [InitializeOnLoadMethod]
        private static void EnsureShadersIncluded()
        {
            var graphicsSettings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettings == null) return;

            var so = new SerializedObject(graphicsSettings);
            var arrayProp = so.FindProperty("m_AlwaysIncludedShaders");
            if (arrayProp == null || !arrayProp.isArray) return;

            bool changed = false;

            foreach (var shaderName in ShaderNames)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null) continue;

                // Check if already in the list
                bool found = false;
                for (int i = 0; i < arrayProp.arraySize; i++)
                {
                    var element = arrayProp.GetArrayElementAtIndex(i);
                    if (element.objectReferenceValue == shader)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    int index = arrayProp.arraySize;
                    arrayProp.InsertArrayElementAtIndex(index);
                    arrayProp.GetArrayElementAtIndex(index).objectReferenceValue = shader;
                    changed = true;
                }
            }

            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
            }
        }
    }
}
