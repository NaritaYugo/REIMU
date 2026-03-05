Shader "Hidden/TerrainHeightShaderURP"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float worldY : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                // URP専用の座標変換
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                // ワールドY座標（高さ）を保存
                output.worldY = positionWS.y;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                // Rチャンネルに高さを書き込む
                return float4(input.worldY, 0.0, 0.0, 0.0);
            }
            ENDHLSL
        }
    }
}