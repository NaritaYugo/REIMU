Shader "Custom/FisheyeShader"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        _Distortion ("Distortion", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM

            // 1. 使用する関数の名前を宣言
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 2. 構造体の定義：頂点データやピクセルへの渡し方
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // 3. C#側から送られてくるデータの受け取り
            sampler2D _MainTex;
            float _Distortion;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _BaseMap_ST;
            CBUFFER_END

            // 4. 頂点シェーダー（座標変換）
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                
                // マクロを使わず、そのままUVをコピーする
                OUT.uv = IN.uv; 
                
                return OUT;
            }

            // 5. フラグメントシェーダー（色決定）
            half4 frag(Varyings IN) : SV_Target
            {
                float2 st = IN.uv - 0.5;
                float r = length(st);
                float r_distorted =r + _Distortion * ( r * r *r ); 
                
                // ゼロ除算対策を含めた歪み
                float2 dir = (r > 0.0001) ? normalize(st) : float2(0,0);
                float2 distortedUV = dir * r_distorted + 0.5;

                return tex2D(_MainTex, distortedUV);
            }
            ENDHLSL
        }
    }
}
