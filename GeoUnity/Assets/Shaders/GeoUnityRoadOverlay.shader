Shader "GeoUnity/RoadOverlay"
{
    Properties
    {
        _BaseColor ("Road Tint", Color) = (0.14, 0.72, 0.95, 0.36)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-10"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "RoadOverlayPass"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Soft edge fade + subtle dashed center line for road readability
                float edge = 1.0 - smoothstep(0.38, 0.50, abs(IN.uv.x - 0.5));
                float centerLine = (1.0 - smoothstep(0.03, 0.06, abs(IN.uv.x - 0.5))) * step(0.45, frac(IN.uv.y * 0.12));
                half3 col = lerp(_BaseColor.rgb, half3(0.95, 0.97, 1.0), centerLine * 0.55);
                half alpha = _BaseColor.a * edge + centerLine * 0.18;
                return half4(col, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
