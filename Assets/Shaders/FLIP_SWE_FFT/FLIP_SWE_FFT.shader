Shader "Custom/FLIPSWEFFT"
{
    Properties
    {
        _Color ("Line Color", Color) = (0, 0.8, 1, 1)
        _HeightScale ("Height Scale", Float) = 2.0
        _WidthScale ("Width Scale", Float) = 0.1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "SWE"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct state {
                float h; // 水位
                float u; // 速度
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
            };

            // C#から渡されるバッファ
            StructuredBuffer<state> _MainBuffer;
            
            // プロパティ
            float4 _Color;
            float _HeightScale;
            float _WidthScale;
            uint _Count; // データ数

            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings OUT;

                // バッファから該当するインデックスのデータを取得
                state s = _MainBuffer[vertexID];

                float3 positionOS = float3(
                    (float)vertexID * _WidthScale - ((float)_Count * _WidthScale * 0.5), 
                    s.h * _HeightScale, 
                    0.0
                );

                OUT.positionCS = TransformObjectToHClip(positionOS);
                
                OUT.color = _Color;

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return IN.color;
            }
            ENDHLSL
        }
    }
}