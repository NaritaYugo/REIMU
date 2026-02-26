Shader "REIMU/Debug_FFT_Displacement"
{
    Properties
    {
        _DispLOD0 ("Displacement LOD0 (20m)", 2D) = "black" {}
        _DispLOD1 ("Displacement LOD1 (80m)", 2D) = "black" {}
        _DispLOD2 ("Displacement LOD2 (320m)", 2D) = "black" {}
        _Size0 ("LOD0 Size", Float) = 20.0
        _Size1 ("LOD1 Size", Float) = 80.0
        _Size2 ("LOD2 Size", Float) = 320.0
        
        _HeightScale ("Height Scale", Float) = 1.0
        _ChoppyScale ("Choppiness Scale", Float) = 1.0
        
        _BaseColor ("Base Color", Color) = (0.0, 0.2, 0.5, 1.0)
        _TipColor ("Tip Color", Color) = (0.6, 0.9, 1.0, 1.0)
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
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            sampler2D _DispLOD0, _DispLOD1, _DispLOD2;
            float _Size0, _Size1, _Size2;
            float _HeightScale, _ChoppyScale;
            float4 _BaseColor, _TipColor;

            v2f vert (appdata v)
            {
                v2f o;
                
                // 1. 頂点を一旦完全にワールド座標に変換する
                float4 worldPos = mul(unity_ObjectToWorld, v.vertex);

                // 2. ワールド座標(X, Z)を使って各LODのUVを計算
                float2 uv0 = worldPos.xz / _Size0;
                float2 uv1 = worldPos.xz / _Size1;
                float2 uv2 = worldPos.xz / _Size2;

                // 3. 各LODの変位をサンプリング
                float4 d0 = tex2Dlod(_DispLOD0, float4(uv0, 0, 0));
                float4 d1 = tex2Dlod(_DispLOD1, float4(uv1, 0, 0));
                float4 d2 = tex2Dlod(_DispLOD2, float4(uv2, 0, 0));

                // 変位の合成
                float3 totalDisp = float3(d0.x + d1.x + d2.x, 
                                          d0.y + d1.y + d2.y, 
                                          d0.z + d1.z + d2.z);

                // 4. 【重要】ローカル座標ではなく、ワールド座標に対して直接変位を加算する
                worldPos.x += totalDisp.x * _ChoppyScale;
                worldPos.y += totalDisp.y * _HeightScale;
                worldPos.z += totalDisp.z * _ChoppyScale;

                // 5. 変位後のワールド座標からクリップ空間（画面上の位置）へ変換
                o.pos = mul(UNITY_MATRIX_VP, worldPos);
                
                // フラグメントシェーダー用に変位後のワールド座標を渡す
                o.worldPos = worldPos.xyz;

                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 dx = ddx(i.worldPos);
                float3 dy = ddy(i.worldPos);
                float3 normal = normalize(cross(dy, dx));

                float3 lightDir = normalize(float3(1.0, 1.5, 1.0));
                float nDotL = max(0.2, dot(normal, lightDir));

                float height = i.worldPos.y; 
                float blend = saturate((height + 1.0) / 4.0);
                float3 albedo = lerp(_BaseColor.rgb, _TipColor.rgb, blend);

                return fixed4(albedo * nDotL, 1.0);
            }
            ENDCG
        }
    }
}