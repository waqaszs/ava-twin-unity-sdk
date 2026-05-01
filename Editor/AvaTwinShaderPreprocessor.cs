using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvaTwin.Editor
{
    /// <summary>
    /// Strips Ava-Twin/Stylized shader variants that runtime-loaded avatar
    /// meshes never use, keeping cold-workspace build times small.
    ///
    /// Without this, every clean CI workspace pays ~20 minutes to compile
    /// the URP variant matrix (~73k variants). Ava-Twin avatars actually
    /// render with only a handful of keyword combinations — main light
    /// cascade shadows + additional pixel lights + soft shadows + fog on.
    /// The rest (mixed lighting, dynamic lightmap, vertex additional lights,
    /// no-shadow variants, etc.) are included by URP's scriptable stripper
    /// but never executed at runtime for avatar meshes.
    ///
    /// This callback runs during build shader-processing and discards
    /// variants whose keyword combination is provably unused.
    ///
    /// Uses <see cref="ShaderKeywordSet.IsEnabled"/> with
    /// pre-allocated <see cref="ShaderKeyword"/>(string) handles — the
    /// cross-version-safe API that works from Unity 2019.3+ through
    /// Unity 6 without relying on version-specific keyword-name accessors.
    /// </summary>
    internal class AvaTwinShaderPreprocessor : IPreprocessShaders
    {
        // Higher number = runs later. URP's own stripper is at 0 — we run
        // after it so we only see variants URP chose to keep.
        public int callbackOrder => 100;

        // ── Pre-allocated keyword handles ─────────────────────────────────
        // Constructing ShaderKeyword(name) once is cheaper than constructing
        // per-variant. These are the exact URP keyword names.

        // Lightmap keywords — avatars are dynamic, never baked into lightmap.
        private static readonly ShaderKeyword KwLightmapOn            = new ShaderKeyword("LIGHTMAP_ON");
        private static readonly ShaderKeyword KwDirLightmapCombined   = new ShaderKeyword("DIRLIGHTMAP_COMBINED");
        private static readonly ShaderKeyword KwDynamicLightmapOn     = new ShaderKeyword("DYNAMICLIGHTMAP_ON");
        private static readonly ShaderKeyword KwLightmapShadowMixing  = new ShaderKeyword("LIGHTMAP_SHADOW_MIXING");
        private static readonly ShaderKeyword KwShadowsShadowmask     = new ShaderKeyword("SHADOWS_SHADOWMASK");

        // Vertex-lit additional lights — avatars always pixel lit.
        private static readonly ShaderKeyword KwAdditionalLightsVertex = new ShaderKeyword("_ADDITIONAL_LIGHTS_VERTEX");

        // Legacy / VR screen-space main-light shadows — we use cascade only.
        private static readonly ShaderKeyword KwMainLightShadowsScreen = new ShaderKeyword("_MAIN_LIGHT_SHADOWS_SCREEN");

        // Plain _MAIN_LIGHT_SHADOWS without cascade — the non-cascade sampler
        // path; consolidated on the cascade variant.
        private static readonly ShaderKeyword KwMainLightShadows         = new ShaderKeyword("_MAIN_LIGHT_SHADOWS");
        private static readonly ShaderKeyword KwMainLightShadowsCascade  = new ShaderKeyword("_MAIN_LIGHT_SHADOWS_CASCADE");

        // Reflection probe heavy paths — invisible on avatar faces.
        private static readonly ShaderKeyword KwReflProbeBlending = new ShaderKeyword("_REFLECTION_PROBE_BLENDING");
        private static readonly ShaderKeyword KwReflProbeBoxProj  = new ShaderKeyword("_REFLECTION_PROBE_BOX_PROJECTION");

        // SSAO — expensive on mobile.
        private static readonly ShaderKeyword KwScreenSpaceOcclusion = new ShaderKeyword("_SCREEN_SPACE_OCCLUSION");

        // Defensive strips — source-level shader already drops these but we
        // guard in case URP re-adds. Light cookies / layers / clustered /
        // decals (DBuffer) / debug / DOTS instancing.
        private static readonly ShaderKeyword KwLightCookies        = new ShaderKeyword("_LIGHT_COOKIES");
        private static readonly ShaderKeyword KwLightLayers         = new ShaderKeyword("_LIGHT_LAYERS");
        private static readonly ShaderKeyword KwClusteredRendering  = new ShaderKeyword("_CLUSTERED_RENDERING");
        private static readonly ShaderKeyword KwDbufferMrt1         = new ShaderKeyword("_DBUFFER_MRT1");
        private static readonly ShaderKeyword KwDbufferMrt2         = new ShaderKeyword("_DBUFFER_MRT2");
        private static readonly ShaderKeyword KwDbufferMrt3         = new ShaderKeyword("_DBUFFER_MRT3");
        private static readonly ShaderKeyword KwDebugDisplay        = new ShaderKeyword("DEBUG_DISPLAY");
        private static readonly ShaderKeyword KwDotsInstancing      = new ShaderKeyword("DOTS_INSTANCING_ON");

        private const string TargetShaderName = "Ava-Twin/Stylized";

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            if (shader == null || shader.name != TargetShaderName) return;

            int removed = 0;
            for (int i = data.Count - 1; i >= 0; i--)
            {
                if (ShouldStripVariant(data[i].shaderKeywordSet))
                {
                    data.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
            {
                Debug.Log($"[AvaTwinShaderPreprocessor] {shader.name} / " +
                          $"{snippet.passType} / {snippet.shaderType}: " +
                          $"stripped {removed}, kept {data.Count} variants");
            }
        }

        private static bool ShouldStripVariant(ShaderKeywordSet keywords)
        {
            // 1. Lightmap family — not reachable on runtime-loaded avatars.
            if (keywords.IsEnabled(KwLightmapOn))           return true;
            if (keywords.IsEnabled(KwDirLightmapCombined))  return true;
            if (keywords.IsEnabled(KwDynamicLightmapOn))    return true;
            if (keywords.IsEnabled(KwLightmapShadowMixing)) return true;
            if (keywords.IsEnabled(KwShadowsShadowmask))    return true;

            // 2. Vertex additional-lights — avatars use pixel lights.
            if (keywords.IsEnabled(KwAdditionalLightsVertex)) return true;

            // 3. Legacy main-light shadow paths.
            if (keywords.IsEnabled(KwMainLightShadowsScreen)) return true;
            if (keywords.IsEnabled(KwMainLightShadows) &&
                !keywords.IsEnabled(KwMainLightShadowsCascade))
                return true;

            // 4. Reflection-probe heavy variants.
            if (keywords.IsEnabled(KwReflProbeBlending))    return true;
            if (keywords.IsEnabled(KwReflProbeBoxProj))     return true;

            // 5. SSAO.
            if (keywords.IsEnabled(KwScreenSpaceOcclusion)) return true;

            // 6. Defensive: features already removed at source level.
            if (keywords.IsEnabled(KwLightCookies))         return true;
            if (keywords.IsEnabled(KwLightLayers))          return true;
            if (keywords.IsEnabled(KwClusteredRendering))   return true;
            if (keywords.IsEnabled(KwDbufferMrt1))          return true;
            if (keywords.IsEnabled(KwDbufferMrt2))          return true;
            if (keywords.IsEnabled(KwDbufferMrt3))          return true;
            if (keywords.IsEnabled(KwDebugDisplay))         return true;
            if (keywords.IsEnabled(KwDotsInstancing))       return true;

            return false;
        }
    }
}
