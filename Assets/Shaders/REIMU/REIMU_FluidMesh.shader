Shader "REIMU/FluidMesh"
{
    Properties {
        // 【追加】この値より「周囲の水が少ない」と白くなる
        _FoamFactorThreshold ("Foam Factor Threshold", Range(0.01, 1.0)) = 0.3
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Triangle { float3 v0, v1, v2; float3 norm; float3 velocity; };
            StructuredBuffer<Triangle> TriangleBuffer;
            
            StructuredBuffer<float> VoxelGrid_FoamFactor; // 【変更】
            float3 _GridSize;
            float _CellSize;
            float _FoamFactorThreshold;

            struct v2f { 
                float4 pos : SV_POSITION; 
                float3 normal : TEXCOORD0; 
                float3 worldPos : TEXCOORD1;
            };

            v2f vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID) {
                Triangle tri = TriangleBuffer[instanceID];
                float3 pos = (vertexID == 0) ? tri.v0 : ((vertexID == 1) ? tri.v1 : tri.v2);
                v2f o;
                o.pos = TransformObjectToHClip(pos);
                o.normal = tri.norm;
                o.worldPos = pos;
                return o;
            }

            float4 frag(v2f i) : SV_Target {
                float3 lightDir = normalize(float3(-1.0, 1.0, 1.0));
                float diffuse = max(0.0, dot(i.normal, lightDir));
                float lighting = diffuse * 0.5 + 0.5;

                float t = saturate((i.worldPos.y - 15.0) / 20.0); 
                float3 deepColor = float3(0.02, 0.15, 0.4);
                float3 waterColor = float3(0.1, 0.6, 0.9);
                float3 baseColor = lerp(deepColor, waterColor, t);

                // --- 孤立度による白波判定 ---
                int3 voxelIdx = int3(i.worldPos / _CellSize);
                float foamVal = 1.0; // デフォルトは海の中(1.0)
                
                if (all(voxelIdx >= 0) && all(voxelIdx < _GridSize)) {
                    int flatIdx = voxelIdx.x + voxelIdx.y * _GridSize.x + voxelIdx.z * _GridSize.x * _GridSize.y;
                    foamVal = VoxelGrid_FoamFactor[flatIdx]; 
                }

                // 周囲に水が少ない（foamValが低い）ほど白くなる
                float foamFactor = 1.0 - smoothstep(0.0, _FoamFactorThreshold, foamVal);
                float3 finalColor = lerp(baseColor, float3(0.8, 0.9, 1.0), foamFactor);
                
                return float4(finalColor * lighting, 1.0);
            }
            ENDHLSL
        }
    }
}