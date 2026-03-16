#ifndef SWE_FFT_FUNCTIONS_INCLUDED
#define SWE_FFT_FUNCTIONS_INCLUDED

// ==========================================================
// 共通のテクスチャ・バッファ宣言
// ==========================================================
Texture2D<float4> FFT_DispLOD0; SamplerState samplerFFT_DispLOD0;
Texture2D<float4> FFT_DispLOD1; SamplerState samplerFFT_DispLOD1;
Texture2D<float4> FFT_DispLOD2; SamplerState samplerFFT_DispLOD2;

Texture2D<float> TerrainHeightMap; SamplerState samplerTerrainHeightMap;

struct SWECell { float h; float hu; float hv; float foam; };
StructuredBuffer<SWECell> SWE_State_Buffer;

// ==========================================================
// 1. 深度計算 (DepthFunc)
// ==========================================================
void GetSeabedData_float(float2 ScreenUV, float RawDepth, float3 WaterSurfaceWorldPos, out float3 SeabedWorldPos, out float TrueWaterDepth)
{
    float2 ndc_xy = ScreenUV * 2.0 - 1.0;
    
    #if UNITY_UV_STARTS_AT_TOP
    ndc_xy.y = -ndc_xy.y;
    #endif

    float4 ndc = float4(ndc_xy, RawDepth, 1.0);
    float4 worldPos = mul(UNITY_MATRIX_I_VP, ndc);
    SeabedWorldPos = worldPos.xyz / worldPos.w;

    TrueWaterDepth = max(0.0, WaterSurfaceWorldPos.y - SeabedWorldPos.y);
}

// ==========================================================
// 2. コースティクス計算 (CausticsFunc)
// ==========================================================
void GetCausticsUVAndMask_float(
    float3 SeabedWorldPos, 
    float TrueWaterDepth, 
    float3 WaterSurfaceNormal, 
    float DistortionStrength, 
    out float2 OutCausticsUV, 
    out float OutIntensityMask 
) {
    // 波打ち際の不自然な切れ目を消すマスクは残す
    float shoreMask = smoothstep(0.0, 0.2, TrueWaterDepth);

    // 深度によるフェードアウトは水全体の濁度（透過率）に任せるため、ここでは計算しない
    OutIntensityMask = shoreMask;

    float2 baseUV = SeabedWorldPos.xz;
    
    // 深いほど光が拡散して歪みが大きくなる表現はそのまま活かす
    float2 distortion = WaterSurfaceNormal.xz * TrueWaterDepth * DistortionStrength;
    
    OutCausticsUV = baseUV + distortion;
}

// ==========================================================
// 3. FFT・SWEの結合処理 (SWEFFTFunc)
// ==========================================================
void GetFFTDisplacement_float(
    float3 WorldPos, 
    float Size0, float Size1, float Size2, 
    out float3 OutDisplacement)
{
    float2 uv0 = WorldPos.xz / Size0;
    float2 uv1 = WorldPos.xz / Size1;
    float2 uv2 = WorldPos.xz / Size2;

    float dist = distance(_WorldSpaceCameraPos, WorldPos);

    float4 d2 = FFT_DispLOD2.SampleLevel(samplerFFT_DispLOD2, uv2, 0);
    float3 totalDisp = float3(d2.x, d2.y, d2.z);

    float blend1 = 1.0 - saturate((dist - 400.0) / 100.0); 
    if (blend1 > 0.0)
    {
        float4 d1 = FFT_DispLOD1.SampleLevel(samplerFFT_DispLOD1, uv1, 0);
        totalDisp += float3(d1.x, d1.y, d1.z) * blend1;
    }

    float blend0 = 1.0 - saturate((dist - 100.0) / 50.0);
    if (blend0 > 0.0)
    {
        float4 d0 = FFT_DispLOD0.SampleLevel(samplerFFT_DispLOD0, uv0, 0);
        totalDisp += float3(d0.x, d0.y, d0.z) * blend0;
    }

    OutDisplacement = totalDisp;
}

void GetFFTNormal_float(
    float3 WorldPos, 
    float Size0, float Size1, float Size2, 
    out float3 OutNormal)
{
    float delta = 0.1; 

    float3 dispCenter, dispX, dispZ;
    GetFFTDisplacement_float(WorldPos, Size0, Size1, Size2, dispCenter);
    GetFFTDisplacement_float(WorldPos + float3(delta, 0, 0), Size0, Size1, Size2, dispX);
    GetFFTDisplacement_float(WorldPos + float3(0, 0, delta), Size0, Size1, Size2, dispZ);

    float3 pCenter = WorldPos + dispCenter;
    float3 pRight = WorldPos + float3(delta, 0, 0) + dispX;
    float3 pForward = WorldPos + float3(0, 0, delta) + dispZ;

    float3 tangent = pRight - pCenter;
    float3 bitangent = pForward - pCenter;

    OutNormal = normalize(cross(bitangent, tangent));
}

void GetClipmapMorph_float(float3 WorldPos, float3 ObjectPos, float MeshSize, float CurrentGridSize, out float3 MorphedPos)
{
    float targetGridSize = CurrentGridSize * 4.0;
    
    float2 localXZ = WorldPos.xz - ObjectPos.xz;
    float2 snappedLocalXZ = round(localXZ / targetGridSize) * targetGridSize;
    
    float3 targetPos = float3(ObjectPos.x + snappedLocalXZ.x, WorldPos.y, ObjectPos.z + snappedLocalXZ.y);
    
    float dist = max(abs(localXZ.x), abs(localXZ.y));
    float morphStart = (MeshSize * 0.5) * 0.8; 
    float morphAlpha = saturate((dist - morphStart) / ((MeshSize * 0.5) - morphStart));
    
    MorphedPos = lerp(WorldPos, targetPos, morphAlpha);
}

void GetSWEAndTerrainBilinear(int swe_width, float fx, float fz, out float outSWE, out float outTerrain)
{
    float shiftedX = fx - 0.5f;
    float shiftedZ = fz - 0.5f;

    int x0 = clamp((int)floor(shiftedX), 0, swe_width - 1);
    int z0 = clamp((int)floor(shiftedZ), 0, swe_width - 1);
    int x1 = clamp(x0 + 1, 0, swe_width - 1);
    int z1 = clamp(z0 + 1, 0, swe_width - 1);

    float tx = frac(shiftedX);
    float tz = frac(shiftedZ);

    float h00 = SWE_State_Buffer[z0 * swe_width + x0].h;
    float h10 = SWE_State_Buffer[z0 * swe_width + x1].h;
    float h01 = SWE_State_Buffer[z1 * swe_width + x0].h;
    float h11 = SWE_State_Buffer[z1 * swe_width + x1].h;

    float b00 = TerrainHeightMap.Load(int3(x0, z0, 0)).r;
    float b10 = TerrainHeightMap.Load(int3(x1, z0, 0)).r;
    float b01 = TerrainHeightMap.Load(int3(x0, z1, 0)).r;
    float b11 = TerrainHeightMap.Load(int3(x1, z1, 0)).r;

    float h0 = lerp(h00, h10, tx);
    float h1 = lerp(h01, h11, tx);
    outSWE = lerp(h0, h1, tz);

    float b0 = lerp(b00, b10, tx);
    float b1 = lerp(b01, b11, tx);
    outTerrain = lerp(b0, b1, tz);
}

// --- 統合海洋の頂点位置を決定 ---
void GetUnifiedOcean_float(
    float3 WorldPos, float Size0, float Size1, float Size2,
    float swe_width_In, float dx_swe_In, float2 swe_world_offset_In, 
    float sea_bottom_z_In, 
    out float3 OutPosition)
{
    // 1. FFT領域(背景)の波高を計算
    float3 fftDisp = float3(0.0f, 0.0f, 0.0f);
    GetFFTDisplacement_float(WorldPos, Size0, Size1, Size2, fftDisp);
    
    float localX = WorldPos.x - swe_world_offset_In.x;
    float localZ = WorldPos.z - swe_world_offset_In.y;
    float sweTotalSize = swe_width_In * dx_swe_In;
    
    float terrain_y = -30.0f; 
    float sweDepth = 0.0f;

    if (localX >= 0.0f && localX < sweTotalSize && localZ >= 0.0f && localZ < sweTotalSize) 
    {
        float fx = localX / dx_swe_In;
        float fz = localZ / dx_swe_In;
        int swe_w_int = (int)swe_width_In;
        GetSWEAndTerrainBilinear(swe_w_int, fx, fz, sweDepth, terrain_y);
    }

    float3 fftPos = WorldPos + fftDisp; 
    float blendMargin = 2.0f; 
    float blendX = smoothstep(0.0f, blendMargin, localX) * smoothstep(0.0f, blendMargin, sweTotalSize - localX);
    float blendZ = smoothstep(0.0f, blendMargin, localZ) * smoothstep(0.0f, blendMargin, sweTotalSize - localZ);
    float blendWeight = blendX * blendZ;

    // SWE領域内であれば、FFTの波とSWEの波をブレンドする
    if (blendWeight > 0.0f) 
    {
        float absoluteSweHeight = terrain_y + sweDepth;
        
        // 陸地との境界で水が不自然に盛り上がるのを防ぐため、浅瀬では地形を少し沈み込ませてごまかす
        float sink_offset = smoothstep(0.01f, 0.00f, sweDepth) * 0.05f;
        absoluteSweHeight -= sink_offset;

        // SWEの波に、FFTの細かいディティールを上乗せする
        // 陸地(terrain_y > 0)や極端な浅瀬ではFFTの波を消す
        float altitude_fade = 1.0f - smoothstep(0.0f, 1.0f, terrain_y);
        float fft_blend = smoothstep(0.01f, 0.1f, sweDepth) * altitude_fade;
        absoluteSweHeight += fftDisp.y * fft_blend;

        float3 swePos = float3(WorldPos.x + fftDisp.x * fft_blend, absoluteSweHeight, WorldPos.z + fftDisp.z * fft_blend);
        OutPosition = lerp(fftPos, swePos, blendWeight);
    } 
    else 
    {
        // SWE領域外は100%FFTの波
        OutPosition = fftPos;
    }
}

// --- 統合海洋の泡(Foam)量を決定 ---
void GetUnifiedFoam_float(
    float3 WorldPos, 
    float Size0, float Size1, float Size2,
    float swe_width_In, float dx_swe_In, float2 swe_world_offset_In, 
    out float OutFoam)
{
    // --- 1. FFT領域の砕波 ---
    // 変位ベクトルの空間微分(ヤコビアン)を用いて、波が急峻になり折り重なる箇所を「泡」とする
    float delta = 0.5f;
    float3 dispX = float3(0.0f, 0.0f, 0.0f);
    float3 dispZ = float3(0.0f, 0.0f, 0.0f);
    float3 dispCenter = float3(0.0f, 0.0f, 0.0f);
    
    GetFFTDisplacement_float(WorldPos + float3(delta, 0.0f, 0.0f), Size0, Size1, Size2, dispX);
    GetFFTDisplacement_float(WorldPos + float3(0.0f, 0.0f, delta), Size0, Size1, Size2, dispZ);
    GetFFTDisplacement_float(WorldPos, Size0, Size1, Size2, dispCenter);
    
    float dDx = (dispX.x - dispCenter.x) / delta;
    float dDz = (dispZ.z - dispCenter.z) / delta;
    float jacobian = dDx + dDz; 
    float fft_foam = smoothstep(-0.3f, -0.8f, jacobian); 

    // --- 2. SWE領域の泡 ---
    // (SWEバッファからの泡サンプリングとブレンド処理省略)
    float localX = WorldPos.x - swe_world_offset_In.x;
    float localZ = WorldPos.z - swe_world_offset_In.y;
    float sweTotalSize = swe_width_In * dx_swe_In;
    
    float terrain_y = -1000.0f; 
    float swe_foam = 0.0f;
    float blendWeight = 0.0f;

    float sweDepth = 0.0f; 

    if (localX >= 0.0f && localX < sweTotalSize && localZ >= 0.0f && localZ < sweTotalSize) 
    {
        float fx = localX / dx_swe_In;
        float fz = localZ / dx_swe_In;
        
        int swe_w_int = (int)swe_width_In;
        GetSWEAndTerrainBilinear(swe_w_int, fx, fz, sweDepth, terrain_y);
        
        float blendMargin = 2.0f; 
        float blendX = smoothstep(0.0f, blendMargin, localX) * smoothstep(0.0f, blendMargin, sweTotalSize - localX);
        float blendZ = smoothstep(0.0f, blendMargin, localZ) * smoothstep(0.0f, blendMargin, sweTotalSize - localZ);
        blendWeight = blendX * blendZ;

        if (blendWeight > 0.0f) 
        {
            float shiftedX = fx - 0.5f;
            float shiftedZ = fz - 0.5f;

            int x0 = clamp((int)floor(shiftedX), 0, swe_w_int - 1);
            int z0 = clamp((int)floor(shiftedZ), 0, swe_w_int - 1);
            int x1 = clamp(x0 + 1, 0, swe_w_int - 1);
            int z1 = clamp(z0 + 1, 0, swe_w_int - 1);

            float tx = frac(shiftedX);
            float tz = frac(shiftedZ);

            float f00 = SWE_State_Buffer[z0 * swe_w_int + x0].foam;
            float f10 = SWE_State_Buffer[z0 * swe_w_int + x1].foam;
            float f01 = SWE_State_Buffer[z1 * swe_w_int + x0].foam;
            float f11 = SWE_State_Buffer[z1 * swe_w_int + x1].foam;

            float f0 = lerp(f00, f10, tx);
            float f1 = lerp(f01, f11, tx);
            swe_foam = lerp(f0, f1, tz);
        }
    }

    float base_foam = saturate(fft_foam + (swe_foam * blendWeight));

    // --- 3. 陸地との交差部分（波打ち際）の泡 ---
    // 水深が浅い箇所を波打ち際とみなし、泡を強制的に発生させる
    float actual_wave_y = WorldPos.y + dispCenter.y; 
    
    if (blendWeight > 0.0f)
    {
        float absoluteSweHeight = terrain_y + sweDepth;
        float sink_offset = smoothstep(0.01f, 0.00f, sweDepth) * 0.05f;
        absoluteSweHeight -= sink_offset;
        
        float altitude_fade = 1.0f - smoothstep(0.0f, 1.0f, terrain_y);
        float fft_blend = smoothstep(0.01f, 0.1f, sweDepth) * altitude_fade;
        absoluteSweHeight += dispCenter.y * fft_blend;
        
        actual_wave_y = lerp(actual_wave_y, absoluteSweHeight, blendWeight);
    }

    float water_depth = actual_wave_y - terrain_y;
    float intersection_foam = 0.0f;

    if (water_depth > 0.0f) 
    {
        float foam_in = smoothstep(0.0f, 0.05f, water_depth);
        float foam_out = 1.0f - smoothstep(0.05f, 1.0f, water_depth);
        intersection_foam = foam_in * foam_out;
    }

    float final_foam = saturate(base_foam + intersection_foam);

    if (blendWeight > 0.0f) 
    {
        float dry_cutoff = smoothstep(0.001f, 0.005f, sweDepth);
        final_foam *= dry_cutoff;
    }

    OutFoam = final_foam;
}

void GetPanoramicUV_float(float3 ReflectionDir, out float2 PanoramicUV)
{
    // ベクトルを正規化
    float3 dir = normalize(ReflectionDir);
    
    // Atan2とAsinを使って、3D方向を2DのUV（0.0～1.0）にマッピング
    float u = atan2(dir.x, dir.z) / (2.0 * 3.1415926535) + 0.5;
    float v = asin(dir.y) / 3.1415926535 + 0.5;
    
    PanoramicUV = float2(u, v);
}

#endif // OCEAN_CUSTOM_FUNCTIONS_INCLUDED