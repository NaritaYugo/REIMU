Shader "Custom/LOD2D_Display"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        // 透明度を使えるように設定変更
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off


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
                float4 color : COLOR;
            };

            // 頂点ごとのオフセット
            static const float2 kOffsets[6] = {
                float2(-1,-1), float2(-1,1), float2(1,-1),
                float2(1,-1),  float2(-1,1), float2(1,1)
            };

            // C#から送られてくるバッファと変数
            StructuredBuffer<Particle> _Particles;
            float3 _ObjPos;
            float3 _ObjScale;
            float _width; 
            float _height; 
            int _nx;
            int _ny;

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings OUT;
                
                uint particleIdx = id / 6;
                uint vertexIdx = id % 6;
                
                Particle p = _Particles[particleIdx];
                
                float aspect = _width / _height;
                float2 offset = kOffsets[vertexIdx] * 0.01;
                offset.x /= aspect; // 横方向の伸びをキャンセル

                // normPos を「0～1」にする
                float2 normPos = float2(p.position.x / _width, p.position.y / _height);

                // シミュレーション空間(0~1)をローカル空間(-0.5~0.5)へ
                float3 localPos = float3(normPos.x - 0.5 + offset.x, normPos.y - 0.5 + offset.y, 0);
                
                // ワールド変換
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
                
                float alpha = 1.0 - smoothstep(0.8, 1.0, dist);
                return half4(IN.color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}