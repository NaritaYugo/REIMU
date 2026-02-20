Shader "REIMU/ParticleRender"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            #include "UnityCG.cginc"

            struct Particle {
                float2 position;
                float2 velocity;
                float mass;
                float4 C;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            StructuredBuffer<uint> _ActiveBuffer;
            float _ParticleSize;

            struct v2f {
                float4 pos : SV_POSITION;
            };

            v2f vert (uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                v2f o;
                Particle p = _ParticleBuffer[instanceID];

                // 死んでいる粒子は描画しない
                if (p.mass <= 0) {
                    o.pos = float4(0, 0, 0, 0);
                    return o;
                }
                
                // Triangles (6頂点) でQuadを構築
                float xSign = (vertexID == 2 || vertexID == 4 || vertexID == 5) ? 1.0 : -1.0;
                float ySign = (vertexID == 1 || vertexID == 2 || vertexID == 4) ? 1.0 : -1.0;
                
                float2 offset = float2(xSign, ySign);
                
                float4 worldPos = float4(p.position + offset * _ParticleSize, 0, 1.0);
                o.pos = mul(UNITY_MATRIX_VP, worldPos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 粒子は水色で描画
                return fixed4(0.5, 0.8, 1.0, 1.0);
            }
            ENDCG
        }
    }
}