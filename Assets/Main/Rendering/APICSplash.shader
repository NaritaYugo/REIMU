Shader "APICSplash"
{
    Properties {
        _MinSize ("Min Size (Low Density)", Range(0.005, 0.1)) = 0.02
        _MaxSize ("Max Size (High Density)", Range(0.05, 0.5)) = 0.15
        
        [HDR] _SplashColor("Splash Color", Color) = (0.9, 0.95, 1.0, 1.0)
        _StretchMultiplier ("Stretch Multiplier", Range(0.0, 0.5)) = 0.05
        _SparkleProbability ("Sparkle Probability", Range(0.0, 1.0)) = 0.1
        
        [HideInInspector] _SpeedThreshold ("Speed Threshold", Float) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        // 加算＋アルファブレンド (発光しつつ透明度を持つ)
        Blend One OneMinusSrcAlpha
        ZWrite Off  
        ZTest LEqual
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ComputeShader非対応の命令を避けるため、構造体はここで個別定義する
            struct APICParticle {
                float3 position; float mass; float3 velocity; float age;
                float3 c1; float pad_c1; float3 c2; float pad_c2; float3 c3; float pad_c3;
            };

            StructuredBuffer<APICParticle> APIC_Particle_Buffer;
            StructuredBuffer<float> VoxelGrid_FinalDensity; 
            
            float3 _ApicWorldOffset;
            float3 _GridSize;
            float _CellSize;
            float _MinSize;
            float _MaxSize;
            float4 _SplashColor;
            float _StretchMultiplier;
            float _IsoLevel;
            float _SpeedThreshold;
            float _SparkleProbability;

            struct v2f { 
                float4 pos : SV_POSITION; 
                float2 uv : TEXCOORD0; 
                float sparkle : TEXCOORD1; 
            };

            // ==========================================================
            // 頂点シェーダー (Vertex Shader)
            // APICParticleBufferから情報を取り出し、カメラに向けたビルボード(ひし形)を生成する
            // ==========================================================
            v2f vert (uint vertexID : SV_VertexID) {
                v2f o;
                
                uint particleIndex = vertexID / 6;
                uint cornerIndex = vertexID % 6;

                // --- 1. ボクセル密度によるカリング判定 ---
                // ボクセル内部（水の中）にある飛沫を描画しないように、自身の位置の密度を取得する
                APICParticle p = APIC_Particle_Buffer[particleIndex];
                
                float3 origin_zup = _ApicWorldOffset;
                origin_zup.z -= (_GridSize.z * _CellSize) * 0.5f;

                float3 localPos_zup = p.position - origin_zup;
                int3 idx_zup = (int3)round(localPos_zup / _CellSize);

                float density = 0.0;
                int gx = (int)_GridSize.x;
                int gy = (int)_GridSize.y;
                int gz = (int)_GridSize.z;

                if (idx_zup.x >= 0 && idx_zup.x < gx &&
                    idx_zup.y >= 0 && idx_zup.y < gy &&
                    idx_zup.z >= 0 && idx_zup.z < gz) {
                    
                    int flatIdx = idx_zup.x + idx_zup.y * gx + idx_zup.z * gx * gy;
                    density = VoxelGrid_FinalDensity[flatIdx];
                }

                float3 unityPos = float3(p.position.x, p.position.z, p.position.y);
                float3 unityVel = float3(p.velocity.x, p.velocity.z, p.velocity.y);
                float speed_particle = length(unityVel);
                
                // 密度が閾値以下(ボクセルの外) かつ 速度が一定以上の場合のみ飛沫として描画
                bool isSplash = (density < _IsoLevel * 0.8f) && (speed_particle > _SpeedThreshold);

                if (p.mass <= 0.0f || !isSplash) {
                    o.pos = float4(0.0, -99999.0, 0.0, 1.0);
                    o.uv = float2(0.0, 0.0);
                    o.sparkle = 0.0;
                    return o;
                }

                // --- 2. サイズと発光(Sparkle)の計算 ---
                float densityRatio = saturate(density / max(_IsoLevel * 0.8f, 0.001f));
                float halfSize = lerp(_MinSize, _MaxSize, densityRatio);

                float sparkleAmount = 0.0;
                float hash = frac(sin(particleIndex * 12.9898f) * 43758.5453f);
                
                if (hash < _SparkleProbability) {
                    float phase = frac(sin(particleIndex * 33.33f) * 1333.33f) * 6.283f;
                    float blinkSpeed = frac(sin(particleIndex * 77.77f) * 7777.77f) * 5.0f + 5.0f; 
                    
                    float blink = sin(_Time.y * blinkSpeed + phase);
                    sparkleAmount = smoothstep(0.95f, 1.0f, blink); 
                }
                o.sparkle = sparkleAmount;

                // --- 3. ビルボードの生成と引き伸ばし (Motion Blur) ---
                float2 uvArray[6] = {
                    float2(-1, -1), float2( 1, -1), float2(-1,  1), 
                    float2(-1,  1), float2( 1, -1), float2(  1,  1)  
                };
                float2 uv = uvArray[cornerIndex];
                o.uv = uv;

                float3 viewPos = TransformWorldToView(unityPos);
                float3 viewVel = mul((float3x3)UNITY_MATRIX_V, unityVel);
                float speed_view = length(viewVel.xy);
                
                float2 dirY = (speed_view > 0.001f) ? (viewVel.xy / speed_view) : float2(0.0, 1.0);
                float2 dirX = float2(-dirY.y, dirY.x);
                
                float stretch = 1.0f + speed_view * _StretchMultiplier;
                float2 offset = (dirX * uv.x + dirY * uv.y * stretch) * halfSize;
                viewPos.xy += offset;

                o.pos = mul(UNITY_MATRIX_P, float4(viewPos, 1.0));
                return o;
            }

            // ==========================================================
            // フラグメントシェーダー (Fragment Shader)
            // ==========================================================
            float4 frag (v2f i) : SV_Target
            {
                // 45度回転した正方形（ひし形）の生成
                float shape = abs(i.uv.x) + abs(i.uv.y);
                if (shape > 1.0f) discard;

                float finalAlpha = i.sparkle;
                float3 finalColor = _SplashColor.rgb * i.sparkle * 15.0f;

                // Blend One OneMinusSrcAlpha に合わせて、色にAlphaを乗算して出力
                return float4(finalColor * finalAlpha, finalAlpha); 
            }
            ENDHLSL
        }
    }
}