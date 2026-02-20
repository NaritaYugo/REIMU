Shader "REIMU/SWERender"
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

            struct SWEGrid {
                float h;
                float u;
            };

            StructuredBuffer<float> _HFlipBuffer;
            float _GridSpacing; // グリッド幅

            struct v2f {
                float4 pos : SV_POSITION;
            };

            v2f vert (uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                v2f o;
                
                // 修正後: h_FLIP を直接取得
                float h_flip = _HFlipBuffer[instanceID];
                
                float x = instanceID * _GridSpacing;
                
                bool isTop = (vertexID == 1 || vertexID == 2 || vertexID == 4);
                bool isRight = (vertexID == 2 || vertexID == 4 || vertexID == 5);
                
                // 上部頂点の高さを h_flip に依存させる
                float y = isTop ? h_flip : 0.0;
                float xOffset = isRight ? _GridSpacing : 0.0;

                float4 worldPos = float4(x + xOffset, y, 0, 1.0);
                o.pos = mul(UNITY_MATRIX_VP, worldPos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // SWEは青色で描画
                return fixed4(0.2, 0.5, 0.9, 1.0);
            }
            ENDCG
        }
    }
}