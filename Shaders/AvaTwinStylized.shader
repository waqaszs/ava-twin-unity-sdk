Shader "Ava-Twin/Stylized"
{
    // Single-variant URP shader — same lighting math as Ava-Twin/Stylized
    // Builtin (Lambert + Blinn-Phong + hemisphere + SampleSH + ACES + skin
    // mask tint + rim).
    //
    // Optimization vs. previous URP shader:
    //   - One ForwardLit pass with NO multi_compile (was: full URP Lit
    //     forward with thousands of variants from main-light shadows /
    //     additional lights / fog / instancing combinations).
    //   - GBuffer / Meta / Universal2D passes already dropped in v1.1.6.
    //   - AvaTwinLitForwardPass.hlsl no longer included — its full URP
    //     Lit machinery was the dominant variant-explosion source.
    //
    // Visual deltas vs. previous URP shader (acceptable for stylized
    // avatars; matches Built-in RP variant):
    //   - No real-time shadow received on avatar surface (avatars still
    //     CAST shadows on world via the ShadowCaster pass kept below).
    //   - No additional point/spot light contribution on avatar.
    //   - No fog applied to avatar.
    //   - Toon SSS / ShadowStep stylization removed; replaced with smooth
    //     Lambert + Blinn-Phong (matches Built-in shader).

    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [HideInInspector][MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        [HideInInspector] _MainTex("", 2D) = "white" {}
        [HideInInspector] _Color("", Color) = (1,1,1,1)
        _SkinColor("Skin Color", Color) = (1,1,1,1)
        _SkinMask("Skin Mask", 2D) = "white" {}
        [Toggle] _HasSkinMask("Has Skin Mask", Float) = 0

        [Header(Specular)]
        _Smoothness("Smoothness", Range(0, 1)) = 0.3
        _SpecStrength("Specular Strength", Range(0, 1)) = 0.04

        [Header(Ambient and Fill)]
        _SkyColor("Sky Color", Color) = (0.35, 0.40, 0.50, 1)
        _GroundColor("Ground Color", Color) = (0.08, 0.06, 0.06, 1)
        _HemiStrength("Hemisphere Strength", Range(0, 1)) = 0.20
        _MinBrightness("Min Brightness", Range(0, 0.1)) = 0.0167

        [Header(Rim Light)]
        _RimColor("Rim Light Color", Color) = (0, 0, 0, 1)
        _RimPower("Rim Power", Range(1, 8)) = 4.0
        _RimStrength("Rim Strength", Range(0, 1)) = 0.0

        // ---- Inert legacy properties — kept hidden so existing materials
        //      created before this rewrite don't lose their serialized
        //      defaults / cause "missing property" warnings. They are read
        //      by the shadow/depth pass machinery (Cull, Cutoff, ZWrite,
        //      etc.) but ignored by the ForwardLit pass.
        [HideInInspector] _Cutoff("", Float) = 0.5
        [HideInInspector] _Cull("", Float) = 2.0
        [HideInInspector] _ZWrite("", Float) = 1.0
        [HideInInspector] _SrcBlend("", Float) = 1.0
        [HideInInspector] _DstBlend("", Float) = 0.0
        [HideInInspector] _AlphaClip("", Float) = 0.0
        [HideInInspector] _Surface("", Float) = 0.0
        [HideInInspector] _Blend("", Float) = 0.0
        [HideInInspector] _ReceiveShadows("", Float) = 1.0
        [HideInInspector] _QueueOffset("", Float) = 0.0
        // Old toon parameters — inert in this version.
        [HideInInspector] _ShadowStep("", Float) = 1.0
        [HideInInspector] _ShadowSoftness("", Float) = 2.0
        [HideInInspector] _ShadowColor("", Color) = (0, 0, 0, 1)
        [HideInInspector] _ShadowIntensity("", Float) = 0.0
        [HideInInspector] _SSSColor("", Color) = (0, 1, 0, 1)
        [HideInInspector] _SSSIntensity("", Float) = 0.12
        [HideInInspector] _SSSPower("", Float) = 4.0
        [HideInInspector] _RimThreshold("", Float) = 0.438
        [HideInInspector] _RimSmooth("", Float) = 0.241
        [HideInInspector] _RimIntensity("", Float) = 0.114
        [HideInInspector] _WarmShift("", Float) = 0.0
        [HideInInspector] _Contrast("", Float) = 1.034
        [HideInInspector] _Saturation("", Float) = 1.295
        // URP Lit machinery defaults (referenced by ShadowCaster / DepthOnly
        // include chains and SRP Batcher — must remain in CBUFFER layout).
        [HideInInspector] _Metallic("", Float) = 0.0
        [HideInInspector] _SmoothnessTextureChannel("", Float) = 0
        [HideInInspector] _MetallicGlossMap("", 2D) = "white" {}
        [HideInInspector] _SpecColor("", Color) = (0.2, 0.2, 0.2, 1)
        [HideInInspector] _SpecGlossMap("", 2D) = "white" {}
        [HideInInspector] _SpecularHighlights("", Float) = 1.0
        [HideInInspector] _EnvironmentReflections("", Float) = 1.0
        [HideInInspector] _BumpScale("", Float) = 1.0
        [HideInInspector] _BumpMap("", 2D) = "bump" {}
        [HideInInspector] _Parallax("", Float) = 0.005
        [HideInInspector] _ParallaxMap("", 2D) = "black" {}
        [HideInInspector] _OcclusionStrength("", Float) = 1.0
        [HideInInspector] _OcclusionMap("", 2D) = "white" {}
        [HideInInspector] _EmissionColor("", Color) = (0,0,0,1)
        [HideInInspector] _EmissionMap("", 2D) = "white" {}
        [HideInInspector] _DetailMask("", 2D) = "white" {}
        [HideInInspector] _DetailAlbedoMapScale("", Float) = 1.0
        [HideInInspector] _DetailAlbedoMap("", 2D) = "linearGrey" {}
        [HideInInspector] _DetailNormalMapScale("", Float) = 1.0
        [HideInInspector] _DetailNormalMap("", 2D) = "bump" {}
        [HideInInspector] _ClearCoatMask("", Float) = 0.0
        [HideInInspector] _ClearCoatSmoothness("", Float) = 0.0
        [HideInInspector] _GlossMapScale("", Float) = 0.0
        [HideInInspector] _Glossiness("", Float) = 0.0
        [HideInInspector] _GlossyReflections("", Float) = 0.0
        [HideInInspector] _WorkflowMode("", Float) = 1.0
        [HideInInspector][NoScaleOffset] unity_Lightmaps("", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] unity_LightmapsInd("", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] unity_ShadowMasks("", 2DArray) = "" {}
    }

    SubShader
    {
        PackageRequirements { "com.unity.render-pipelines.universal": "10.0.0" }
        Tags{"RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True"}
        LOD 200

        // ---- ForwardLit (single variant — the whole point of this rewrite) ----
        Pass
        {
            Name "ForwardLit"
            Tags{"LightMode" = "UniversalForward"}

            Cull[_Cull]
            ZWrite[_ZWrite]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // NO multi_compile_*. Single variant.
            // (Previous version had: _MAIN_LIGHT_SHADOWS{,_CASCADE,_SCREEN},
            //  _ADDITIONAL_LIGHTS{,_VERTEX}, _ADDITIONAL_LIGHT_SHADOWS,
            //  _SHADOWS_SOFT, multi_compile_fog, multi_compile_instancing —
            //  several thousand variants total.)

            #include "AvaTwinLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            // ACES filmic tonemap (Narkowicz 2015) — same as Built-in shader.
            float3 ACESFilmic(float3 x)
            {
                float a = 2.51;
                float b = 0.03;
                float c = 2.43;
                float d = 0.59;
                float e = 0.14;
                return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   vni = GetVertexNormalInputs(IN.normalOS);
                OUT.positionCS = vpi.positionCS;
                OUT.uv         = TRANSFORM_TEX(IN.texcoord, _BaseMap);
                OUT.normalWS   = vni.normalWS;
                OUT.positionWS = vpi.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // ---- Albedo ----
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                // Skin mask tinting (binary step at 0.5).
                if (_HasSkinMask > 0.5)
                {
                    half mask = step(0.5, SAMPLE_TEXTURE2D(_SkinMask, sampler_SkinMask, IN.uv).r);
                    albedo.rgb = lerp(albedo.rgb, albedo.rgb * _SkinColor.rgb, mask);
                }

                float3 N = normalize(IN.normalWS);
                Light  mainLight = GetMainLight();
                float3 L = normalize(mainLight.direction);
                float3 V = normalize(_WorldSpaceCameraPos - IN.positionWS);
                float3 H = normalize(L + V);

                // ---- Lambertian diffuse ----
                float NdotL = saturate(dot(N, L));

                // ---- Blinn-Phong specular ----
                float NdotH = saturate(dot(N, H));
                float specExp = exp2(_Smoothness * 11.0 + 2.0);
                float spec = pow(NdotH, specExp) * _SpecStrength;

                // ---- Hemisphere ambient (sky/ground blend by normal.y) ----
                float hemiBlend = N.y * 0.5 + 0.5;
                float3 hemiAmbient = lerp(_GroundColor.rgb, _SkyColor.rgb, hemiBlend) * _HemiStrength;

                // ---- Built-in SH ambient (zero variant cost) ----
                float3 shAmbient = SampleSH(N);

                // Combine: brighter of hemisphere or SH (works with or
                // without baked ambient).
                float3 ambient = max(hemiAmbient, shAmbient);

                // ---- Direct lighting (no shadow attenuation — single variant) ----
                float3 directDiffuse  = mainLight.color * NdotL;
                float3 directSpecular = mainLight.color * spec;

                // ---- Rim light (default OFF — _RimStrength = 0) ----
                float rim = 1.0 - saturate(dot(V, N));
                float3 rimLight = _RimColor.rgb * pow(rim, _RimPower) * _RimStrength;

                // ---- Combine ----
                float3 lighting = ambient + directDiffuse;

                // Min brightness clamp (matches Built-in / Three.js parity).
                float lightLum = dot(lighting, float3(0.2126, 0.7152, 0.0722));
                lighting = lighting * max(1.0, _MinBrightness / max(lightLum, 0.0001));

                float3 color = albedo.rgb * lighting + directSpecular + rimLight;

                // ---- ACES filmic tonemap ----
                color = ACESFilmic(color);

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ---- ShadowCaster pass (so avatars cast shadows on world) ----
        Pass
        {
            Name "ShadowCaster"
            Tags{"LightMode" = "ShadowCaster"}

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma vertex   ShadowPassVertex
            #pragma fragment ShadowPassFragment
            // Differentiate directional vs punctual light shadows for
            // normal bias. Variant cost: 2.
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "AvaTwinLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        // ---- DepthOnly pass (depth prepass + SSAO + screen-space effects) ----
        Pass
        {
            Name "DepthOnly"
            Tags{"LightMode" = "DepthOnly"}

            ZWrite On
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma vertex   DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #include "AvaTwinLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Ava-Twin/Stylized Builtin"
}
