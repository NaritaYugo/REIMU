Shader "Custom/FLIP2D_Display"
{
    Properties
    {
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle {
                float2 position;
                float2 velocity;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            StructuredBuffer<Particle> _Particles;

            struct Attributes
            {
                uint id : SV_VertexID;
            };

            // 変数の定義を追加
            float3 _ObjPos;
            float3 _ObjScale;

Varyings vert(uint id : SV_VertexID)
    {
        // 以前の修正コード（_ObjPos, _ObjScale を使うもの）をそのまま使用
        Varyings OUT;
        Particle p = _Particles[id];
        
        // 異常な座標（0,0など）の粒子を画面外に飛ばして、三角形の頂点にならないようにガード
        if (p.position.x <= 0.0 || p.position.x >= 1.0) {
            OUT.positionCS = float4(0, 0, 0, 0);
            return OUT;
        }

        float3 localPos = float3(p.position.x - 0.5, p.position.y - 0.5, 0);
        float3 worldPos = (localPos * _ObjScale) + _ObjPos;
        OUT.positionCS = TransformWorldToHClip(worldPos);
        return OUT;
    }

            half4 frag(Varyings IN) : SV_Target
            {
                return half4(0.0, 0.5, 1.0, 1.0); // 水色
            }
            ENDHLSL
        }
    }
}