Shader "Custom/FisheyeShader"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        _Distortion ("Distortion", Float) = 1.0
        _ColorShift ("Color Shift", Float) = 0.01
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

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
            sampler2D _CameraOpaqueTexture;
            float _Distortion;
            float _ColorShift;

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
                float2 screenUV = IN.uv;

                float2 st = IN.uv - 0.5;
                float r = length(st);
                float r_max = 0.5;
                float r_distorted = r + _Distortion * ( r * r * r ); 
                float r_distorted_max = r_max + _Distortion * ( r_max * r_max * r_max );
                r_distorted = r_distorted * (r_max / r_distorted_max);

                float2 dir = (r > 0.0001) ? normalize(st) : float2(0,0);
                
                float2 uv_r = dir*(r_distorted + _ColorShift) + 0.5;
                float2 uv_g = dir*r_distorted + 0.5;
                float2 uv_b = dir*(r_distorted - _ColorShift) + 0.5;

                float r_channel = tex2D(_CameraOpaqueTexture, uv_r).r;
                float g_channel = tex2D(_CameraOpaqueTexture, uv_g).g;
                float b_channel = tex2D(_CameraOpaqueTexture, uv_b).b;
                
                half4 col = half4(r_channel, g_channel, b_channel, 1.0);

                return col;
            }
            ENDHLSL
        }
    }
}
