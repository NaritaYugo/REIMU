#ifndef DEPTH_RECONSTRUCT_INCLUDED
#define DEPTH_RECONSTRUCT_INCLUDED

void GetSeabedData_float(float2 ScreenUV, float RawDepth, float3 WaterSurfaceWorldPos, out float3 SeabedWorldPos, out float TrueWaterDepth)
{
    // ScreenUV (0.0 ~ 1.0) と RawDepth から NDC座標 (-1.0 ~ 1.0) を構築
    float2 ndc_xy = ScreenUV * 2.0 - 1.0;
    
    // プラットフォームによるUVの反転対策
    #if UNITY_UV_STARTS_AT_TOP
    ndc_xy.y = -ndc_xy.y;
    #endif

    float4 ndc = float4(ndc_xy, RawDepth, 1.0);

    // ビュー・プロジェクション逆行列を使ってワールド座標を復元
    float4 worldPos = mul(UNITY_MATRIX_I_VP, ndc);
    SeabedWorldPos = worldPos.xyz / worldPos.w;

    // 現在描画している水面の高さ(Y)から海底の高さ(Y)を引いて、真の水深を計算
    TrueWaterDepth = max(0.0, WaterSurfaceWorldPos.y - SeabedWorldPos.y);
}

#endif