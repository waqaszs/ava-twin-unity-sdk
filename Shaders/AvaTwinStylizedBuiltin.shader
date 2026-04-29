Shader "Ava-Twin/Stylized Builtin"
{
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
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _BaseMap;
            float4 _BaseMap_ST;
            fixed4 _BaseColor;
            float _HasSkinMask;
            sampler2D _SkinMask;
            fixed4 _SkinColor;

            float _Smoothness;
            float _SpecStrength;

            fixed4 _SkyColor;
            fixed4 _GroundColor;
            float _HemiStrength;
            float _MinBrightness;

            fixed4 _RimColor;
            float _RimPower;
            float _RimStrength;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                UNITY_FOG_COORDS(3)
                SHADOW_COORDS(4)
            };

            // ---- ACES filmic tonemapping (matches Three.js ACESFilmicToneMapping) ----
            // Input/output are linear RGB.
            float3 ACESFilmic(float3 x)
            {
                // Narkowicz 2015 fit — same curve Three.js uses
                float a = 2.51;
                float b = 0.03;
                float c = 2.43;
                float d = 0.59;
                float e = 0.14;
                return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o, o.pos);
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // ---- Albedo ----
                fixed4 albedo = tex2D(_BaseMap, i.uv) * _BaseColor;

                // Skin mask tinting — binary step at 0.5
                if (_HasSkinMask > 0.5)
                {
                    float mask = step(0.5, tex2D(_SkinMask, i.uv).r);
                    albedo.rgb = lerp(albedo.rgb, albedo.rgb * _SkinColor.rgb, mask);
                }

                float3 N = normalize(i.worldNormal);
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 H = normalize(L + V);

                // ---- Lambertian diffuse ----
                float NdotL = saturate(dot(N, L));

                // ---- Blinn-Phong specular (tighter lobe, closer to PBR) ----
                float NdotH = saturate(dot(N, H));
                float specExp = exp2(_Smoothness * 11.0 + 2.0);
                float spec = pow(NdotH, specExp) * _SpecStrength;

                // ---- Shadow attenuation ----
                float shadow = SHADOW_ATTENUATION(i);

                // ---- Hemisphere ambient (sky/ground blend by normal.y) ----
                float hemiBlend = N.y * 0.5 + 0.5;  // remap -1..1 to 0..1
                float3 hemiAmbient = lerp(_GroundColor.rgb, _SkyColor.rgb, hemiBlend) * _HemiStrength;

                // Also grab Unity's built-in SH ambient (picks up scene ambient + light probes)
                float3 shAmbient = ShadeSH9(float4(N, 1.0));

                // Combine: use the brighter of hemisphere or SH so the shader works
                // well both with and without baked lighting
                float3 ambient = max(hemiAmbient, shAmbient);

                // ---- Directional light contribution ----
                float3 directDiffuse = _LightColor0.rgb * NdotL * shadow;
                float3 directSpecular = _LightColor0.rgb * spec * shadow;

                // ---- Rim light (default OFF — _RimStrength = 0) ----
                float rim = 1.0 - saturate(dot(V, N));
                float3 rimLight = _RimColor.rgb * pow(rim, _RimPower) * _RimStrength;

                // ---- Combine lighting ----
                float3 lighting = ambient + directDiffuse;

                // Enforce minimum brightness (matches Three.js minBrightness 0.0167)
                float lightLum = dot(lighting, float3(0.2126, 0.7152, 0.0722));
                lighting = lighting * max(1.0, _MinBrightness / max(lightLum, 0.0001));

                float3 color = albedo.rgb * lighting + directSpecular + rimLight;

                // ---- ACES filmic tonemapping ----
                color = ACESFilmic(color);

                fixed4 col;
                col.rgb = color;
                col.a = 1.0;

                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }

        // Additional lights pass (point/spot)
        Pass
        {
            Name "FORWARD_ADD"
            Tags { "LightMode"="ForwardAdd" }
            Blend One One
            ZWrite Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _BaseMap;
            float4 _BaseMap_ST;
            fixed4 _BaseColor;
            float _HasSkinMask;
            sampler2D _SkinMask;
            fixed4 _SkinColor;
            float _Smoothness;
            float _SpecStrength;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                UNITY_FOG_COORDS(3)
                SHADOW_COORDS(4)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o, o.pos);
                TRANSFER_SHADOW(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 albedo = tex2D(_BaseMap, i.uv) * _BaseColor;

                if (_HasSkinMask > 0.5)
                {
                    float mask = step(0.5, tex2D(_SkinMask, i.uv).r);
                    albedo.rgb = lerp(albedo.rgb, albedo.rgb * _SkinColor.rgb, mask);
                }

                float3 N = normalize(i.worldNormal);
                float3 worldPos = i.worldPos;

                // Light direction — handles both directional and point/spot
                #ifdef USING_DIRECTIONAL_LIGHT
                    float3 L = normalize(_WorldSpaceLightPos0.xyz);
                #else
                    float3 L = normalize(_WorldSpaceLightPos0.xyz - worldPos);
                #endif

                float3 V = normalize(_WorldSpaceCameraPos - worldPos);
                float3 H = normalize(L + V);

                float NdotL = saturate(dot(N, L));
                float NdotH = saturate(dot(N, H));
                float specExp = exp2(_Smoothness * 10.0 + 1.0);
                float spec = pow(NdotH, specExp) * _SpecStrength;

                UNITY_LIGHT_ATTENUATION(atten, i, worldPos);

                float3 diffuse = _LightColor0.rgb * NdotL * atten;
                float3 specular = _LightColor0.rgb * spec * atten;

                fixed4 col;
                col.rgb = albedo.rgb * diffuse + specular;
                col.a = 1.0;

                UNITY_APPLY_FOG_COLOR(i.fogCoord, col, fixed4(0,0,0,0));
                return col;
            }
            ENDCG
        }

        // Shadow caster pass
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster

            #include "UnityCG.cginc"

            struct v2f
            {
                V2F_SHADOW_CASTER;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                SHADOW_CASTER_FRAGMENT(i);
            }
            ENDCG
        }
    }

    Fallback "Standard"
}
