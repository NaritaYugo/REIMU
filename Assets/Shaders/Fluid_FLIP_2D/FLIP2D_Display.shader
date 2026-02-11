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
            StructuredBuffer<float2> _Gradients;
            int _Res;

        Varyings vert(uint id : SV_VertexID)
            {
                Varyings OUT;
                Particle p = _Particles[id];
                

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