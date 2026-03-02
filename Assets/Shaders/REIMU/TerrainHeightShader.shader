Shader "Hidden/TerrainHeightShader"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float worldY : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // 頂点のワールド座標のY（高さ）を取得
                o.worldY = mul(unity_ObjectToWorld, v.vertex).y;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                // 高さをRチャンネル（赤色）にそのまま書き出す
                return float4(i.worldY, 0, 0, 0);
            }
            ENDCG
        }
    }
}