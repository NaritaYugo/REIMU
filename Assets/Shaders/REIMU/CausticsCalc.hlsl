#ifndef CAUSTICS_CALC_INCLUDED
#define CAUSTICS_CALC_INCLUDED

struct SWECell { float h; float hu; float hv; float padding; };
StructuredBuffer<SWECell> SWE_State_Buffer;

// 1つのグリッドポイント(ix, iy)における、コースティクスの生の強度を計算する関数
float GetRawCausticsAtGrid(int ix, int iy, float seabedY, uint swe_width, float dx, float a, float2 grad_seabed)
{
    // グリッド範囲外の安全対策
    if (ix < 1 || ix >= (int)swe_width - 1 || iy < 1 || iy >= (int)swe_width - 1) return 1.0;

    int idx_c = iy * swe_width + ix;
    float f_c = SWE_State_Buffer[idx_c].h;
    float f_r = SWE_State_Buffer[idx_c + 1].h;
    float f_l = SWE_State_Buffer[idx_c - 1].h;
    float f_u = SWE_State_Buffer[idx_c + swe_width].h;
    float f_d = SWE_State_Buffer[idx_c - swe_width].h;

    float df_dx = (f_r - f_l) / (2.0 * dx);
    float df_dz = (f_u - f_d) / (2.0 * dx);
    float2 grad_f = float2(df_dx, df_dz);

    float laplacian_f = (f_r - 2.0 * f_c + f_l + f_u - 2.0 * f_c + f_d) / (dx * dx);

    float H = max(0.0, f_c - seabedY);
    float2 grad_H = grad_f - grad_seabed;

    // あなたの導出した数式
    return 1.0 - a * (H * laplacian_f + dot(grad_H, grad_f));
}

void GetCausticsAlpha_float(
    float3 WorldPos, float3 WorldNormal, float swe_width_In, float dx_swe_In, float n1, float n2, out float OutAlpha)
{
    uint swe_width = (uint)swe_width_In;
    float dx = dx_swe_In;
    float a = 1.0 - (n1 / n2);

    // 海底の勾配
    float2 grad_seabed = float2(0.0, 0.0);
    if (abs(WorldNormal.y) > 0.001) {
        grad_seabed = float2(-WorldNormal.x / WorldNormal.y, -WorldNormal.z / WorldNormal.y);
    }

    // 現在の座標をグリッド単位に変換（-0.5してセル中心を基準にするのがバイリニアの定石）
    float px = WorldPos.x / dx - 0.5;
    float pz = WorldPos.z / dx - 0.5;

    // 周囲4つのグリッドインデックス (左下を基準とする)
    int ix = floor(px);
    int iz = floor(pz);

    // 小数点以下の重み (0.0 ～ 1.0)
    float fx = frac(px);
    float fz = frac(pz);

    // 周囲4点のコースティクス強度を計算
    float c00 = GetRawCausticsAtGrid(ix,     iz,     WorldPos.y, swe_width, dx, a, grad_seabed);
    float c10 = GetRawCausticsAtGrid(ix + 1, iz,     WorldPos.y, swe_width, dx, a, grad_seabed);
    float c01 = GetRawCausticsAtGrid(ix,     iz + 1, WorldPos.y, swe_width, dx, a, grad_seabed);
    float c11 = GetRawCausticsAtGrid(ix + 1, iz + 1, WorldPos.y, swe_width, dx, a, grad_seabed);

    // バイリニア補間で滑らかにブレンド
    float c0 = lerp(c00, c10, fx);
    float c1 = lerp(c01, c11, fx);
    float raw_alpha = lerp(c0, c1, fz);

    float shadow_intensity = 0.4; // 影の濃さ
    float light_intensity  = 1.5; // 光のブースト量

    float final_alpha;
    
    if (raw_alpha < 1.0) {
        // 1.0未満（影の部分）の処理
        final_alpha = 1.0 + (raw_alpha - 1.0) * shadow_intensity;
    } else {
        // 1.0以上（光の部分）の処理
        final_alpha = 1.0 + pow(raw_alpha - 1.0, 2.0) * light_intensity;
    }

    OutAlpha = clamp(final_alpha, 0.7, 3.0);
}
#endif