#ifndef CAUSTICS_CALC_INCLUDED
#define CAUSTICS_CALC_INCLUDED

#include "FFTBridge.hlsl"

#ifndef SWE_CELL_DEFINED
#define SWE_CELL_DEFINED
#endif

// 1. SWE側の水面高さと法線（傾き）を取得
void GetSWEData(float localX, float localZ, float dx, uint swe_width, out float outHeight, out float3 outNormal)
{
    float px = localX / dx - 0.5;
    float pz = localZ / dx - 0.5;
    int ix = clamp(floor(px), 1, swe_width - 2);
    int iz = clamp(floor(pz), 1, swe_width - 2);

    int idx_c = iz * swe_width + ix;
    float h_c = SWE_State_Buffer[idx_c].h;
    float h_r = SWE_State_Buffer[idx_c + 1].h;
    float h_l = SWE_State_Buffer[idx_c - 1].h;
    float h_u = SWE_State_Buffer[idx_c + swe_width].h;
    float h_d = SWE_State_Buffer[idx_c - swe_width].h;

    // 高さの勾配から法線（傾き）を計算
    float3 normal = normalize(float3(-(h_r - h_l) / (2.0 * dx), 1.0, -(h_u - h_d) / (2.0 * dx)));
    
    outHeight = h_c;
    outNormal = normal;
}

// 2. メイン関数（Shader Graphへのデータ受け渡し用）
// ※引数の構成は今までと同じにしていますので、ノードの繋ぎ変えは最小限で済みます
void GetUnifiedCaustics_float(
    float3 WorldPos_seabed, 
    float Size0, float Size1, float Size2,
    float swe_width_In, float dx_swe_In, float2 swe_world_offset_In, float sea_bottom_z_In,
    float DistortionStrength,
    float DepthFadeStrength,
    out float2 OutUVDistortion, // テクスチャを歪ませるためのズレ幅
    out float OutDepthFade)     // 深海でのフェードアウト用係数
{
    float dx = dx_swe_In;
    float localX = WorldPos_seabed.x - swe_world_offset_In.x;
    float localZ = WorldPos_seabed.z - swe_world_offset_In.y;

    // --- A: FFTの高さと法線 ---
    float delta = dx;
    float3 dispC, dispR, dispL, dispU, dispD;
    
    // 単純に現在の座標の十字サンプリングで法線を計算（大きな波S1, S2を使用）
    GetFFTDisplacement_float(WorldPos_seabed, 0.0, Size1, Size2, dispC);
    GetFFTDisplacement_float(WorldPos_seabed + float3(delta, 0, 0), 0.0, Size1, Size2, dispR);
    GetFFTDisplacement_float(WorldPos_seabed + float3(-delta, 0, 0), 0.0, Size1, Size2, dispL);
    GetFFTDisplacement_float(WorldPos_seabed + float3(0, 0, delta), 0.0, Size1, Size2, dispU);
    GetFFTDisplacement_float(WorldPos_seabed + float3(0, 0, -delta), 0.0, Size1, Size2, dispD);

    float base_depth = max(0.1, 0.0 - WorldPos_seabed.y);
    float fft_height = base_depth + dispC.y;
    float3 fft_normal = normalize(float3(-(dispR.y - dispL.y) / (2.0 * delta), 1.0, -(dispU.y - dispD.y) / (2.0 * delta)));

    // --- B: SWEの高さと法線 ---
    float swe_height = fft_height; // 初期値
    float3 swe_normal = float3(0, 1, 0);
    float seabedTotalSize = swe_width_In * dx_swe_In;
    
    if (localX > dx && localX < seabedTotalSize - dx && localZ > dx && localZ < seabedTotalSize - dx) 
    {
        GetSWEData(localX, localZ, dx, (uint)swe_width_In, swe_height, swe_normal);
    }

    // --- C: ブレンド ---
    float halfSize = seabedTotalSize * 0.5;
    float distFromCenter = max(abs(localX - halfSize), abs(localZ - halfSize));
    float blendStart = halfSize - 18.0 * dx; 
    float blendEnd = halfSize - 6.0 * dx;
    float sweWeight = 1.0 - smoothstep(blendStart, blendEnd, distFromCenter);

    float final_height = lerp(fft_height, swe_height, sweWeight);
    float3 final_normal = normalize(lerp(fft_normal, swe_normal, sweWeight));

    // --- D: 歪みベクトルと減衰の出力 ---
    float depth = max(0.1, final_height - WorldPos_seabed.y);
    
    // 法線のXZ成分（水面の傾き）に水深を掛けることで、深いほど光が大きくズレる物理現象を再現
    OutUVDistortion = final_normal.xz * depth * DistortionStrength;
    
    // 水深による減衰
    OutDepthFade = exp(-base_depth * DepthFadeStrength);
}
#endif