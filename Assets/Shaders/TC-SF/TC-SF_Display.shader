Shader "Custom/TC-SF_Display"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        // ===============================================
        // Pass 0: Particles (Existing)
        // ===============================================
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle {
                float2 position;
                float2 velocity;
                int active;    // 変更: active追加
                float padding;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            static const float2 kOffsets[6] = {
                float2(-1,-1), float2(-1,1), float2(1,-1),
                float2(1,-1),  float2(-1,1), float2(1,1)
            };

            StructuredBuffer<Particle> _Particles;
            float3 _ObjPos;
            float3 _ObjScale;
            float _width; 
            float _height; 

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings OUT;
                uint particleIdx = id / 6;
                uint vertexIdx = id % 6;
                
                Particle p = _Particles[particleIdx];
                
                // 死んだ粒子は描画しない (縮退させる)
                if (p.active == 0) {
                    OUT.positionCS = float4(0,0,0,0);
                    OUT.uv = 0;
                    OUT.color = 0;
                    return OUT;
                }
                
                float aspect = _width / _height;
                float2 offset = kOffsets[vertexIdx] * 0.008; // 少し小さく
                offset.x /= aspect;

                float2 normPos = float2(p.position.x / _width, p.position.y / _height);
                float3 localPos = float3(normPos.x - 0.5 + offset.x, normPos.y - 0.5 + offset.y, 0);
                float3 worldPos = (localPos * _ObjScale) + _ObjPos;
                
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = kOffsets[vertexIdx];

                float speed = length(p.velocity);
                float val = saturate(speed * 0.2);
                OUT.color = lerp(float4(0,0.5,1,1), float4(1,1,1,1), val); // 青～白
                
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float dist = dot(IN.uv, IN.uv);
                if (dist > 1.0) discard;
                return half4(IN.color.rgb, 1.0);
            }
            ENDHLSL
        }


        // ===============================================
        // Pass 1: SWE Surface (Debug Version)
        // ===============================================
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_swe
            #pragma fragment frag_swe
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct SWEState {
                float h;
                float u;
                float h_new;
                float u_new;
            };

            StructuredBuffer<SWEState> _SWEBuffer;
            float3 _ObjPos;
            float3 _ObjScale;
            float _width; 
            float _height; 
            int _nx;

            struct VaryingsSWE {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
            };

            VaryingsSWE vert_swe(uint id : SV_VertexID)
            {
                VaryingsSWE OUT;
                uint cellIdx = id / 6;
                uint vIdx = id % 6;
                
                // バッファから読み出し (NaNなら強制的に安全な値にする)
                float h_val = _SWEBuffer[cellIdx].h;
                if (isnan(h_val)) h_val = 0.5; // エラー時は0.5の高さで表示

                // ★デバッグ用: 最小でも少しだけ高さを付ける (0.05)
                // これで h=0 でも「青い線」が底に見えるはず
                float h_vis = max(h_val, 0.05);

                float x_l = (float)cellIdx / _nx;
                float x_r = (float)(cellIdx + 1) / _nx;
                float h_norm = h_vis / _height;
                
                float x = (vIdx == 0 || vIdx == 1 || vIdx == 4) ? x_l : x_r;
                float y = (vIdx == 0 || vIdx == 2 || vIdx == 3) ? 0.0 : h_norm;
                
                float3 localPos = float3(x - 0.5, y - 0.5, 0); 
                float3 worldPos = (localPos * _ObjScale) + _ObjPos;
                
                OUT.positionCS = TransformWorldToHClip(worldPos);

                // 高さによって色を変える (低い=青, 高い=水色, エラー=赤)
                float4 baseColor = float4(0.0, 0.2, 0.8, 0.6);
                float4 highColor = float4(0.0, 0.8, 1.0, 0.8);
                OUT.color = lerp(baseColor, highColor, saturate(h_val / 3.0));

                // もし元の値がNaNだったら赤くする
                if (isnan(_SWEBuffer[cellIdx].h)) OUT.color = float4(1, 0, 0, 1);

                return OUT;
            }

            half4 frag_swe(VaryingsSWE IN) : SV_Target
            {
                return IN.color;
            }
            ENDHLSL
        }
    }
}