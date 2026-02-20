Shader "Custom/FLIPSWEFFT_Display"
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

        // --- Pass 0: SWE Height Field Background ---
        Pass
        {
            Name "SWEBackground"
            HLSLPROGRAM
            #pragma vertex vert_bg
            #pragma fragment frag_bg

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Varyings_BG
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Texture2D<float> _SWEHeightTex;
            Texture2D<float> _FLIPFloorHeightTex;
            SamplerState sampler_SWEHeightTex;
            SamplerState sampler_FLIPFloorHeightTex;
            float3 _ObjPos;
            float3 _ObjScale;
            float _width; 
            float _height; 

            Varyings_BG vert_bg(uint id : SV_VertexID)
            {
                Varyings_BG OUT;
                float2 quadUVs[6] = {
                    float2(0,0), float2(0,1), float2(1,0),
                    float2(1,0), float2(0,1), float2(1,1)
                };
                float2 uv = quadUVs[id % 6];
                
                // 粒子と同じローカル空間(-0.5 ～ 0.5)からワールド空間へ
                float3 localPos = float3(uv.x - 0.5, uv.y - 0.5, 0.05); // 粒子の少し奥
                float3 worldPos = (localPos * _ObjScale) + _ObjPos;
                
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = uv;
                return OUT;
            }
            half4 frag_bg(Varyings_BG IN) : SV_Target
            {
                // y=0の行から物理的な水位(m)を取得
                float h_val = _SWEHeightTex.Sample(sampler_SWEHeightTex, float2(IN.uv.x, 0));
                float floorHeight = _FLIPFloorHeightTex.Sample(sampler_FLIPFloorHeightTex, float2(IN.uv.x, 0));

                float waterSurfaceY = h_val / _height; 
                float floorSurfaceY = floorHeight / _height;

                if (IN.uv.y < floorSurfaceY) {
                    return half4(0.1, 0.4, 0.8, 1);
                } else if (IN.uv.y < waterSurfaceY) {
                    return half4(0.8, 0.4, 0.1, 1);
                } else {
                    return half4(0, 0, 0, 0); // 透明
                }
            }
            ENDHLSL
        }

        // --- Pass 1: FLIP Particles (Original) ---
        Pass
        {
            Name "FLIPParticles"
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
                
                float aspect = _width / _height;
                float2 offset = kOffsets[vertexIdx] * 0.008; // 少し小さく調整
                offset.x /= aspect;

                float2 normPos = float2(p.position.x / _width, p.position.y / _height);
                float3 localPos = float3(normPos.x - 0.5 + offset.x, normPos.y - 0.5 + offset.y, 0);
                float3 worldPos = (localPos * _ObjScale) + _ObjPos;
                
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = kOffsets[vertexIdx];

                // 速度に応じた色付け
                float speed = length(p.velocity);
                float4 colorRed    = float4(1, 0, 0, 1);
                float4 colorYellow = float4(0.5, 1, 0, 1);
                float4 colorCyan   = float4(0, 0.5, 1, 1);

                float4 finalColor;
                float _T = saturate(speed * 0.1);
                if (_T < 0.4) {
                    // 0.0 ～ 0.5 の区間を 0.0 ～ 1.0 に引き伸ばして補間
                    finalColor = lerp(colorCyan, colorYellow, _T * 2.5);
                } else {
                    // 0.5 ～ 1.0 の区間を 0.0 ～ 1.0 に引き伸ばして補間
                    finalColor = lerp(colorYellow, colorRed, (_T - 0.5) * 1.666);
                }
                OUT.color = finalColor;
                
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float dist = dot(IN.uv, IN.uv);
                if (dist > 1.0) discard;
                float alpha = 0.5;
                return half4(IN.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}