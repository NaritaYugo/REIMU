Shader "Custom/LOD2D_Display"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _ThresholdRatio ("FLIP Threshold", Range(0, 1)) = 0.8
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        // --- Pass 0: 背景 ---
        Pass
        {
            Name "BackgroundGrid"
            HLSLPROGRAM
            #pragma vertex vert_bg
            #pragma fragment frag_bg
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _width, _height, _flipPos,_picPos,_eulerPos,_swePos,_fftPos,_aspectRatio;
            int _nx, _ny;

            struct Appdata { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct VaryingsBG { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            VaryingsBG vert_bg(Appdata v) {
                VaryingsBG o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            // 指定した範囲内でのフェード値を計算するヘルパー
            float get_zone_fade(float t, float start, float end) {
                return saturate((t - start) / max(end - start, 0.0001));
            }

            // 0～flipPos: FLIP             白
            // flipPos～picPos: FLIP/PIC    白～グレー
            // picPos～eulerPos: PIC/Euler  グレー～白
            // eulerPos～swePos: Euler/SWE  白～グレー
            // swePos～fftPos: SWE/FFT      グレー～白
            // fftPos～: FFT                白
            half4 frag_bg(VaryingsBG i) : SV_Target {
                // UV(0~1)を中心基準(-0.5~0.5)にして、距離(0~1)を計算
                float2 diff = i.uv - 0.5;
                // 中心からの円形距離 (0.5が端になるので2倍して1.0を最大にする)
                diff.y *= _aspectRatio;
                float t = length(diff) * 2.0;

                float3 colorWhite = float3(1, 1, 1);
                float3 colorGray  = float3(0.7, 0.7, 0.7);
                float3 finalCol = colorWhite;

                // 領域ごとのグラデーション処理
                if (t < _flipPos) {
                    finalCol = colorWhite;
                } else if (t < _picPos) {
                    finalCol = lerp(colorWhite, colorGray, get_zone_fade(t, _flipPos, _picPos));
                } else if (t < _eulerPos) {
                    finalCol = lerp(colorGray, colorWhite, get_zone_fade(t, _picPos, _eulerPos));
                } else if (t < _swePos) {
                    finalCol = lerp(colorWhite, colorGray, get_zone_fade(t, _eulerPos, _swePos));
                } else if (t < _fftPos) {
                    finalCol = lerp(colorGray, colorWhite, get_zone_fade(t, _swePos, _fftPos));
                } else {
                    finalCol = colorWhite;
                }

                return half4(finalCol, 1.0);
            }
            ENDHLSL
        }

        // --- Pass 1: パーティクル ---
        Pass
        {
            Name "Particles"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle {
                float2 position; // 8 byte
                float2 velocity; // 8 byte
                int active;      // 4 byte
                int padding;     // 4 byte
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            // 表裏があるので向きに注意する
            static const float2 kOffsets[6] = {
                float2(-1,-1), float2(-1,1), float2(1,-1),
                float2(1,-1),  float2(-1,1), float2(1,1)
            };

            // C#からの受け取り
            StructuredBuffer<Particle> _Particles;
            StructuredBuffer<uint> _ActiveList;
            float3 _ObjPos;
            float3 _ObjScale;
            float _width; 
            float _height; 
            float _aspectRatio; 
            int _nx;
            int _ny;

            // instanceID を用いて ActiveList からインデックスを取得
            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;

                // instanceID番目の有効な粒子のインデックスを取得
                uint pIdx = _ActiveList[instanceID];
                Particle p = _Particles[pIdx];

                // 粒子の大きさ
                float2 offset = kOffsets[vertexID] * 0.01;
                offset.x *= (_height / _width);

                float2 normPos = float2(p.position.x / _width, p.position.y / _height);
                float3 localPos = float3(normPos.x - 0.5 + offset.x, normPos.y - 0.5 + offset.y, 0);
                float3 worldPos = (localPos * _ObjScale) + _ObjPos;
                
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = kOffsets[vertexID];
                
                // 速度に応じて水色～黄色～赤
                float speed = length(p.velocity);
                float4 colorRed    = float4(1, 0, 0, 1);
                float4 colorYellow = float4(0.5, 1, 0, 1);
                float4 colorCyan   = float4(0, 0.5, 1, 1);

                float velT = saturate(speed * 0.15);
                if (velT < 0.5) {
                    OUT.color = lerp(colorCyan, colorYellow, velT * 2.0);
                } else {
                    OUT.color = lerp(colorYellow, colorRed, (velT - 0.5) * 2.0);
                }
                
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float dist = dot(IN.uv, IN.uv);
                if (dist > 1.0) discard;
                float alpha = 1.0 - smoothstep(0.8, 1.0, dist);
                return half4(IN.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}