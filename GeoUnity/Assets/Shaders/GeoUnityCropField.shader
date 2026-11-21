Shader "GeoUnity/CropField"
{
    Properties
    {
        _BaseColor   ("Crop Color", Color) = (0.84, 0.70, 0.28, 0.52)
        _StripeScale ("Furrow Scale", Float) = 0.45
        _StripeAmp   ("Furrow Contrast", Float) = 0.14
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-5"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "CropFieldPass"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _StripeScale;
                float _StripeAmp;
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

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Subtle procedural furrow stripes in world-space XZ so crops look textured while staying translucent
                float furrow = sin((IN.positionWS.x + IN.positionWS.z * 0.35) * _StripeScale * 6.28318);
                float detail = sin((IN.positionWS.x * 0.2 - IN.positionWS.z) * _StripeScale * 2.5) * 0.35;
                float shade = 1.0 + (furrow * 0.75 + detail * 0.25) * _StripeAmp;

                Light mainLight = GetMainLight();
                float ndotl = saturate(dot(normalize(IN.normalWS), normalize(mainLight.direction)));
                half3 col = _BaseColor.rgb * shade * (0.68 + 0.32 * ndotl);

                return half4(col, _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
