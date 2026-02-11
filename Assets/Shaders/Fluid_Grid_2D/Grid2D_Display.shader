Shader "Custom/Grid2D_Display"
{
    Properties
    {
        _MainTex ("Fluid Velocity (RG)", 2D) = "white" {} // 速度バッファ
        _BaseColor ("Base Color", Color) = (0.2, 0.3, 0.8, 1) // 水の色
        _SurfaceColor ("Surface Color", Color) = (0.8, 0.9, 1.0, 1) // 表面の光沢色
        _Reflectivity ("Reflectivity", Range(0, 1)) = 0.5 // 反射の強さ
        _WaveScale ("Wave Scale", Range(0, 10)) = 1.0 // 速度から波紋への影響度
        _LightDir ("Light Direction", Vector) = (0.5, 1, 0.5, 0) // ライトの方向
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize; // テクセルサイズは自動で入る
            fixed4 _BaseColor;
            fixed4 _SurfaceColor;
            float _Reflectivity;
            float _WaveScale;
            float4 _LightDir; // w成分は使わないがVectorで渡すため

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 1. 速度を読み取る (R:X速度, G:Y速度)
                float4 fluidData = tex2D(_MainTex, i.uv);
                float2 velocity = fluidData.rg; // RGが速度

                // 2. 速度から「法線マップのオフセット」を計算
                // 速度の方向が強いほど、波紋がその方向に歪むように見せる
                // dx, dyは隣のピクセルの速度をサンプリングするオフセット
                float2 dx = float2(_MainTex_TexelSize.x, 0);
                float2 dy = float2(0, _MainTex_TexelSize.y);

                float2 vel_x = tex2D(_MainTex, i.uv + dx).rg;
                float2 vel_y = tex2D(_MainTex, i.uv + dy).rg;

                // 速度の勾配から擬似的な法線（波紋）を生成
                // (vel_x.x - velocity.x) は速度のX成分の変化 = X方向の傾き
                // (vel_y.y - velocity.y) は速度のY成分の変化 = Y方向の傾き
                float3 normal = normalize(float3(
                    (vel_x.x - velocity.x) * _WaveScale, // X方向の傾き
                    (vel_y.y - velocity.y) * _WaveScale, // Y方向の傾き
                    1.0 // Z方向は常に上を向く
                ));
                
                // 3. ライト計算（簡易的なPhongモデル）
                float3 lightDir = normalize(_LightDir.xyz);
                float diffuse = max(0, dot(normal, lightDir)); // 拡散反射

                // 4. 反射表現（簡易的なフレネル効果）
                // 視線と法線の角度によって反射率が変わるようにする
                float3 viewDir = normalize(UnityWorldSpaceViewDir(i.vertex));
                float fresnel = pow(1.0 - max(0, dot(normal, viewDir)), 2.0); // フレネル項
                fresnel *= _Reflectivity; // 反射の強さを調整

                // 5. 最終的な色
                fixed4 finalColor = _BaseColor * diffuse; // 基本色に拡散反射
                finalColor = lerp(finalColor, _SurfaceColor, fresnel); // フレネルで表面色を混ぜる

                return finalColor;
            }
            ENDCG
        }
    }
}