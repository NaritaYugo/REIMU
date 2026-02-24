Shader "REIMU/SWE_Surface"
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

            struct SWECell {
                float h; float hu; float hv; float padding;
            };

            StructuredBuffer<SWECell> SWE_State_Buffer;
            int swe_width;
            float dx_swe;

            // APICグリッドの範囲情報を受け取る
            float3 _GridSize;
            float _CellSize;

            struct v2f {
                float4 pos : SV_POSITION;
                float height : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
            };

            v2f vert (uint id : SV_VertexID)
            {
                uint quadID = id / 6;
                uint vID = id % 6;
                uint qx = quadID % (swe_width - 1);
                uint qy = quadID / (swe_width - 1);
                uint2 offsets[6] = { uint2(0,0), uint2(0,1), uint2(1,0), uint2(1,0), uint2(0,1), uint2(1,1) };
                uint2 offset = offsets[vID];
                uint vx = qx + offset.x;
                uint vy = qy + offset.y;
                uint bufIdx = vy * swe_width + vx;
                float h = SWE_State_Buffer[bufIdx].h;
                float3 worldPos = float3(vx * dx_swe, h, vy * dx_swe);

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0f));
                o.height = h;
                o.worldPos = worldPos;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                // APICの計算グリッドの物理的な範囲を計算
                float apicMaxX = _GridSize.x * _CellSize;
                float apicMaxZ = _GridSize.y * _CellSize; // UnityのZ軸はグリッドのY成分

                // 自分がAPICの計算範囲内にいる場合は、描画を完全に放棄する。
                // これにより、領域内はFluidMeshだけが描画されることになり、つなぎ目が消滅する。
                if (i.worldPos.x >= 0 && i.worldPos.x < apicMaxX &&
                    i.worldPos.z >= 0 && i.worldPos.z < apicMaxZ)
                {
                    discard;
                }

                // --- 以下は遠景用の簡易な海面描画 ---
                float3 dx = ddx(i.worldPos);
                float3 dy = ddy(i.worldPos);
                float3 normal = normalize(cross(dy, dx));
                float3 lightDir = normalize(float3(-1.0f, 1.0f, 1.0f));
                float diffuse = max(0.0f, dot(normal, lightDir));
                float lighting = diffuse * 0.5f + 0.5f;
                float t = saturate((i.height - 15.0f) / 20.0f);
                float3 deepColor = float3(0.02f, 0.15f, 0.4f);
                float3 crestColor = float3(0.1f, 0.6f, 0.9f);
                float3 baseColor = lerp(deepColor, crestColor, t);

                // 遠景は白波なしでシンプルに
                return float4(baseColor * lighting, 1.0f);
            }
            ENDCG
        }
    }
}