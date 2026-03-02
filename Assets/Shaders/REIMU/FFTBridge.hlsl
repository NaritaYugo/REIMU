#ifndef FFT_CALC_INCLUDED
#define FFT_CALC_INCLUDED

// C#から送られてくるテクスチャ
Texture2D<float4> FFT_DispLOD0; SamplerState samplerFFT_DispLOD0;
Texture2D<float4> FFT_DispLOD1; SamplerState samplerFFT_DispLOD1;
Texture2D<float4> FFT_DispLOD2; SamplerState samplerFFT_DispLOD2;

struct SWECell { float h; float hu; float hv; float padding; };
StructuredBuffer<SWECell> SWE_State_Buffer;
float sea_bottom_z;

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
    // (400m以下なら blend1=1.0、500m以上なら blend1=0.0 になる)
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
    
    // 【修正】ワールド座標ではなく、ObjectPosからのローカル座標でグリッドスナップさせる
    float2 localXZ = WorldPos.xz - ObjectPos.xz;
    float2 snappedLocalXZ = round(localXZ / targetGridSize) * targetGridSize;
    
    // スナップしたローカル座標をワールド座標に戻す
    float3 targetPos = float3(ObjectPos.x + snappedLocalXZ.x, WorldPos.y, ObjectPos.z + snappedLocalXZ.y);
    
    float dist = max(abs(localXZ.x), abs(localXZ.y));
    float morphStart = (MeshSize * 0.5) * 0.8; 
    float morphAlpha = saturate((dist - morphStart) / ((MeshSize * 0.5) - morphStart));
    
    MorphedPos = lerp(WorldPos, targetPos, morphAlpha);
}

// FFTの変位とSWEのシミュレーションを1つのメッシュ上で合成する関数
void GetUnifiedOcean_float(
    float3 WorldPos, 
    float Size0, float Size1, float Size2, // FFT用
    float swe_width_In, float dx_swe_In, float2 swe_world_offset_In, // SWE用
    float sea_bottom_z_In,
    out float3 OutPosition)
{
    // 1. FFTの波（変位）を計算して取得する
    // fftDisp には、X(横揺れ), Y(うねり), Z(横揺れ) が入っている
    float3 fftDisp;
    GetFFTDisplacement_float(WorldPos, Size0, Size1, Size2, fftDisp);
    
    // 2. FFT領域用の座標（横揺れXZ ＋ うねりY をすべて足す）
    float3 fftPos = WorldPos + fftDisp; 
    
    // 3. SWEのローカル座標とブレンド率の計算
    float localX = WorldPos.x - swe_world_offset_In.x;
    float localZ = WorldPos.z - swe_world_offset_In.y;
    float sweTotalSize = swe_width_In * dx_swe_In;
    
    float blendMargin = 2.0; 
    float blendX = smoothstep(0.0, blendMargin, localX) * smoothstep(0.0, blendMargin, sweTotalSize - localX);
    float blendZ = smoothstep(0.0, blendMargin, localZ) * smoothstep(0.0, blendMargin, sweTotalSize - localZ);
    float blendWeight = blendX * blendZ;
    
    if (blendWeight > 0.0)
    {
        // 4. SWEのバッファから水深を読み取る
        uint vx = clamp((uint)round(localX / dx_swe_In), 0, (uint)swe_width_In - 1);
        uint vy = clamp((uint)round(localZ / dx_swe_In), 0, (uint)swe_width_In - 1);
        uint bufIdx = vy * (uint)swe_width_In + vx;
        
        float sweDepth = SWE_State_Buffer[bufIdx].h;
        
        // ★修正: 海底座標 + 水深 = 絶対的な水面Y座標
        float absoluteSweHeight = sea_bottom_z_In + sweDepth; 
        
        // 5. 【ここで横揺れを足す！】
        // XとZは「WorldPos ＋ FFTの横揺れ」、Yは「絶対的な水面Y座標」にする
        float3 swePos = float3(WorldPos.x + fftDisp.x, absoluteSweHeight, WorldPos.z + fftDisp.z);
        
        // 境界付近は lerp で滑らかに繋ぐ
        OutPosition = lerp(fftPos, swePos, blendWeight);
    }
    else
    {
        // 領域外は完全にFFTの波
        OutPosition = fftPos;
    }
}
#endif