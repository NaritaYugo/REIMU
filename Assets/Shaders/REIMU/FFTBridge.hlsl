#ifndef FFT_CALC_INCLUDED
#define FFT_CALC_INCLUDED

// C#から送られてくるテクスチャ
Texture2D<float4> FFT_DispLOD0; SamplerState samplerFFT_DispLOD0;
Texture2D<float4> FFT_DispLOD1; SamplerState samplerFFT_DispLOD1;
Texture2D<float4> FFT_DispLOD2; SamplerState samplerFFT_DispLOD2;

// ★追加：地形のハイトマップとサンプラー
Texture2D<float> TerrainHeightMap; SamplerState samplerTerrainHeightMap;

struct SWECell { float h; float hu; float hv; float foam; };
StructuredBuffer<SWECell> SWE_State_Buffer;

void GetFFTDisplacement_float(
    float3 WorldPos, 
    float Size0, float Size1, float Size2, 
    out float3 OutDisplacement)
{
    float2 uv0 = WorldPos.xz / Size0;
    float2 uv1 = WorldPos.xz / Size1;
    float2 uv2 = WorldPos.xz / Size2;

    float dist = distance(_WorldSpaceCameraPos, WorldPos);

    // LOD2: 無限遠まで常に計算
    float4 d2 = FFT_DispLOD2.SampleLevel(samplerFFT_DispLOD2, uv2, 0);
    float3 totalDisp = float3(d2.x, d2.y, d2.z);

    // LOD1: 400m〜500mの間で徐々にフェードアウトして消える
    float blend1 = 1.0 - saturate((dist - 400.0) / 100.0); 
    if (blend1 > 0.0)
    {
        float4 d1 = FFT_DispLOD1.SampleLevel(samplerFFT_DispLOD1, uv1, 0);
        totalDisp += float3(d1.x, d1.y, d1.z) * blend1;
    }

    // LOD0: 100m〜150mの間で徐々にフェードアウトして消える
    float blend0 = 1.0 - saturate((dist - 100.0) / 50.0);
    if (blend0 > 0.0)
    {
        float4 d0 = FFT_DispLOD0.SampleLevel(samplerFFT_DispLOD0, uv0, 0);
        totalDisp += float3(d0.x, d0.y, d0.z) * blend0;
    }

    OutDisplacement = totalDisp;
}

// ピクセル単位の法線計算（変更なし）
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
    
    // ワールド座標ではなく、ObjectPosからのローカル座標でグリッドスナップさせる
    float2 localXZ = WorldPos.xz - ObjectPos.xz;
    float2 snappedLocalXZ = round(localXZ / targetGridSize) * targetGridSize;
    
    // スナップしたローカル座標をワールド座標に戻す
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

    // アンダーフローを防ぐためのintキャスト
    int x0 = clamp((int)floor(shiftedX), 0, swe_width - 1);
    int z0 = clamp((int)floor(shiftedZ), 0, swe_width - 1);
    int x1 = clamp(x0 + 1, 0, swe_width - 1);
    int z1 = clamp(z0 + 1, 0, swe_width - 1);

    float tx = frac(shiftedX);
    float tz = frac(shiftedZ);

    // SWEの水深
    float h00 = SWE_State_Buffer[z0 * swe_width + x0].h;
    float h10 = SWE_State_Buffer[z0 * swe_width + x1].h;
    float h01 = SWE_State_Buffer[z1 * swe_width + x0].h;
    float h11 = SWE_State_Buffer[z1 * swe_width + x1].h;

    // 地形（Loadを使ってSWEと全く同じピクセルを読む）
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
// ==========================================================
// 修正版：GetUnifiedOcean_float
// ==========================================================
void GetUnifiedOcean_float(
    float3 WorldPos, float Size0, float Size1, float Size2,
    float swe_width_In, float dx_swe_In, float2 swe_world_offset_In, 
    float sea_bottom_z_In, 
    out float3 OutPosition)
{
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

    if (blendWeight > 0.0f) 
    {
        float absoluteSweHeight = terrain_y + sweDepth;
        
        // 【修正】1.5mの極端な沈み込みを廃止。Zファイティング防止用の数cmのオフセットのみにする。
        // 水深が 1cm(0.01) 未満の極めて薄い場所でのみ、最大 5cm(0.05) だけ地下に隠す
        float sink_offset = smoothstep(0.01f, 0.00f, sweDepth) * 0.05f;
        absoluteSweHeight -= sink_offset;

        // 地形が海面より高い場所ではFFTの揺れを無効化
        float altitude_fade = 1.0f - smoothstep(0.0f, 1.0f, terrain_y);
        float fft_blend = smoothstep(0.01f, 0.1f, sweDepth) * altitude_fade;
        absoluteSweHeight += fftDisp.y * fft_blend;

        float3 swePos = float3(WorldPos.x + fftDisp.x * fft_blend, absoluteSweHeight, WorldPos.z + fftDisp.z * fft_blend);
        OutPosition = lerp(fftPos, swePos, blendWeight);
    } 
    else 
    {
        OutPosition = fftPos;
    }
}

// ==========================================================
// 修正版：GetUnifiedFoam_float (輪郭の泡残り解消)
// ==========================================================
void GetUnifiedFoam_float(
    float3 WorldPos, 
    float Size0, float Size1, float Size2,
    float swe_width_In, float dx_swe_In, float2 swe_world_offset_In, 
    out float OutFoam)
{
    // --- 1. FFT領域の砕波 ---
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

    // --- 3. 陸地との交差部分（Intersection）の泡 ---
    float actual_wave_y = WorldPos.y + dispCenter.y; 
    
    if (blendWeight > 0.0f)
    {
        float absoluteSweHeight = terrain_y + sweDepth;
        // Vertexシェーダーと同じオフセットを適用
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
        // 【修正】波打ち際の境界線を柔らかくする
        // 0.0m〜0.05m(5cm)で泡をフワッと出し、そこから1.0mにかけて海側へ消していく
        float foam_in = smoothstep(0.0f, 0.05f, water_depth);
        float foam_out = 1.0f - smoothstep(0.05f, 1.0f, water_depth);
        intersection_foam = foam_in * foam_out;
    }

    float final_foam = saturate(base_foam + intersection_foam);

    // 【修正】無理なフェードアウトをやめ、水深がほぼゼロ（5mm以下）の陸地でのみ泡を確実に消去
    if (blendWeight > 0.0f) 
    {
        // 水深が 5mm 〜 1mm になるにつれて泡を消す（陸地に残る不自然な泡の除去）
        float dry_cutoff = smoothstep(0.001f, 0.005f, sweDepth);
        final_foam *= dry_cutoff;
    }

    OutFoam = final_foam;
}
#endif