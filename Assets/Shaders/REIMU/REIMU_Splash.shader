Shader "REIMU/Splash"
{
    Properties {
        _MinSize ("Min Size (Isolated)", Range(0.01, 0.2)) = 0.05
        _MaxSize ("Max Size (Near Mesh)", Range(0.1, 1.0)) = 0.3
        _FoamFactorThreshold ("Foam Factor Threshold", Float) = 0.3 
        _ScatterSpread ("Scatter Spread", Range(0.0, 0.2)) = 0.05
        
        [HDR] _SplashColor("Splash Color", Color) = (0.9, 0.95, 1.0, 1.0)
        _Shininess ("Shininess (Specular)", Range(10.0, 256.0)) = 128.0 
        
        // 【追加】速度による引き伸ばしの強さ
        _StretchMultiplier ("Stretch Multiplier", Range(0.0, 0.5)) = 0.05
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct APICParticle {
                float3 position; float mass; float3 velocity; float age;
                float3 c1; float pad_c1; float3 c2; float pad_c2; float3 c3; float pad_c3;
            };

            StructuredBuffer<APICParticle> APIC_Particle_Buffer;
            StructuredBuffer<float> VoxelGrid_FoamFactor; 
            float3 _GridSize;
            float _CellSize;
            float _MinSize;
            float _MaxSize;
            float _FoamFactorThreshold;
            float _ScatterSpread;
            float4 _SplashColor;
            float _Shininess;
            float _StretchMultiplier; // 【追加】

            struct v2f { 
                float4 pos : SV_POSITION; 
                float2 uv : TEXCOORD0; 
            };

            v2f vert (uint vertexID : SV_VertexID) {
                v2f o;
                
                uint particleIndex = vertexID / 6;
                uint cornerIndex = vertexID % 6;

                APICParticle p = APIC_Particle_Buffer[particleIndex];
                float3 unityPos = float3(p.position.x, p.position.z, p.position.y);
                float3 unityVel = float3(p.velocity.x, p.velocity.z, p.velocity.y);

                int3 idx = int3(unityPos / _CellSize);
                float foamVal = 1.0; 
                
                if (all(idx >= 0) && all(idx < _GridSize)) {
                    int flatIdx = idx.x + idx.y * _GridSize.x + idx.z * _GridSize.x * _GridSize.y;
                    foamVal = VoxelGrid_FoamFactor[flatIdx];
                }

                bool isSplash = (foamVal < _FoamFactorThreshold);
                
                if (p.mass <= 0.0f || !isSplash) {
                    o.pos = float4(0.0, -99999.0, 0.0, 1.0);
                    o.uv = float2(0.0, 0.0);
                    return o;
                }

                float densityFactor = saturate(foamVal / _FoamFactorThreshold);
                float halfSize = lerp(_MinSize, _MaxSize, densityFactor) * 1.2f;

                float2 uvArray[6] = {
                    float2(-1, -1), float2( 1, -1), float2(-1,  1), 
                    float2(-1,  1), float2( 1, -1), float2( 1,  1)  
                };
                float2 uv = uvArray[cornerIndex];
                o.uv = uv;

                // --- 【変更】速度方向への引き伸ばし（Velocity Stretch） ---
                float3 viewPos = TransformWorldToView(unityPos);
                
                // 速度ベクトルをビュー空間（カメラから見た2D平面）に変換
                float3 viewVel = mul((float3x3)UNITY_MATRIX_V, unityVel);
                float speed = length(viewVel.xy);
                
                // 速度方向（Y軸）と、それに直交する方向（X軸）を計算
                float2 dirY = (speed > 0.001f) ? (viewVel.xy / speed) : float2(0.0, 1.0);
                float2 dirX = float2(-dirY.y, dirY.x);
                
                // 速度に応じて縦（uv.y）方向だけを伸ばす
                float stretch = 1.0f + speed * _StretchMultiplier;
                
                // 算出した軸を使ってオフセットを適用
                float2 offset = (dirX * uv.x + dirY * uv.y * stretch) * halfSize;
                viewPos.xy += offset;
                // -------------------------------------------------------------

                o.pos = mul(UNITY_MATRIX_P, float4(viewPos, 1.0));
                
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float distSq = dot(i.uv, i.uv);
                if (distSq > 1.0f) discard;

                float dist = sqrt(distSq);
                float softAlpha = smoothstep(1.0, 0.8, dist); 

                float z = sqrt(1.0f - distSq);
                float3 viewNormal = normalize(float3(i.uv.x, i.uv.y, z));

                Light mainLight = GetMainLight();
                float3 viewLightDir = normalize(mul((float3x3)UNITY_MATRIX_V, mainLight.direction));

                float wrap = 0.5;
                float diffuse = max(0.0, (dot(viewNormal, viewLightDir) + wrap) / (1.0 + wrap));

                float3 viewDir = float3(0.0, 0.0, 1.0);
                float3 halfVector = normalize(viewLightDir + viewDir);
                float specular = pow(max(0.0, dot(viewNormal, halfVector)), _Shininess);
                specular *= smoothstep(0.4, 0.0, distSq);

                // --- 【変更】中心の透明化とフチの白発光（フレネルエッジ） ---
                
                // distを3乗して、フチ（1.0に近い部分）だけ急激に立ち上がるエッジ係数を作る
                float edge = pow(dist, 3.0); 
                
                // 中心は透明度10%、フチに行くほど100%不透明になる
                float dropletAlpha = lerp(0.1, 1.0, edge);
                float finalAlpha = softAlpha * dropletAlpha;

                // フチの部分だけ強制的に白く発光させる（エッジグロウ）
                float3 edgeGlow = float3(1.0, 1.0, 1.0) * edge;
                
                // 最終カラー合成（エッジの白さを加算）
                float3 finalColor = _SplashColor.rgb * (diffuse * 0.5 + 0.2) + (specular * 2.0) + edgeGlow;
                // -------------------------------------------------------------

                return float4(finalColor, finalAlpha); 
            }
            ENDHLSL
        }
    }
}