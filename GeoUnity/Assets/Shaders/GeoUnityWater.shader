Shader "GeoUnity/Water"
{
    Properties
    {
        _ShallowColor ("Shallow Color", Color) = (0.10, 0.42, 0.52, 0.62)
        _DeepColor    ("Deep Color", Color)    = (0.04, 0.20, 0.34, 0.84)
        _SkyColor     ("Sky Reflection", Color)= (0.58, 0.78, 0.96, 1.0)
        _WaveSpeed    ("Wave Speed", Float)    = 0.45
        _WaveScale    ("Wave Scale", Float)    = 0.14
        _WaveStrength ("Wave Strength", Float) = 0.08
        _SpecularPower("Specular Power", Float)= 96.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLitWater"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _SkyColor;
                float _WaveSpeed;
                float _WaveScale;
                float _WaveStrength;
                float _SpecularPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            // Compact smooth value noise for dual-layer scrolling ripples
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Noise2D(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float WaveHeight(float2 xz, float t)
            {
                float2 p1 = xz * _WaveScale + float2(t * 0.85, t * 0.55);
                float2 p2 = xz * (_WaveScale * 1.9) + float2(-t * 0.65, t * 0.95);
                float w1 = Noise2D(p1) * 0.6 + sin(p1.x * 2.1 + p1.y * 1.3) * 0.2;
                float w2 = Noise2D(p2) * 0.5 + cos(p2.x * 1.7 - p2.y * 2.3) * 0.2;
                return (w1 + w2) * 0.5;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                // Subtle gentle vertical wave undulation
                float t = _Time.y * _WaveSpeed;
                posWS.y += sin(posWS.x * 0.08 + t * 1.4) * cos(posWS.z * 0.08 - t * 1.1) * 0.08;

                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float t = _Time.y * _WaveSpeed;
                float2 xz = IN.positionWS.xz;

                // Finite-difference ripple normal in world space
                float eps = 0.6;
                float hC = WaveHeight(xz, t);
                float hX = WaveHeight(xz + float2(eps, 0.0), t);
                float hZ = WaveHeight(xz + float2(0.0, eps), t);

                float3 rippleN = normalize(float3(
                    (hC - hX) * _WaveStrength * 6.0,
                    1.0,
                    (hC - hZ) * _WaveStrength * 6.0
                ));

                float3 viewDir = normalize(_WorldSpaceCameraPos - IN.positionWS);
                float ndotv = saturate(dot(rippleN, viewDir));
                float fresnel = pow(1.0 - ndotv, 3.2);

                // Base translucent tint blended by Fresnel & subtle wave variation
                half3 waterCol = lerp(_ShallowColor.rgb, _DeepColor.rgb, saturate(fresnel * 0.85 + hC * 0.25));
                waterCol = lerp(waterCol, _SkyColor.rgb, fresnel * 0.58);

                // Main directional sun light diffuse + animated specular sparkle
                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);
                float3 halfVec = normalize(lightDir + viewDir);
                float spec = pow(saturate(dot(rippleN, halfVec)), _SpecularPower);
                float ndotl = saturate(dot(rippleN, lightDir));

                half3 color = waterCol * (0.65 + 0.35 * ndotl) + mainLight.color * (spec * 1.15);

                // Looking straight down -> more transparent (shows orthophoto riverbed), grazing angle -> more reflective
                half alpha = lerp(_ShallowColor.a, _DeepColor.a, saturate(fresnel * 1.15));
                alpha = saturate(alpha + spec * 0.35);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
