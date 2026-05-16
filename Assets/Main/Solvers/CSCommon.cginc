#ifndef CS_COMMON_CGINC
#define CS_COMMON_CGINC

// ==========================================
// データ構造体
// ==========================================
struct SWECell {
    float h;
    float hu;
    float hv;
    float foam;
};

struct APICParticle {
    float3 position;
    float mass;
    float3 velocity;
    float age;
    float3 c1; float pad_c1;
    float3 c2; float pad_c2;
    float3 c3; float pad_c3;
};

// ==========================================
// 共通の定数
// ==========================================
static const float G = 9.81f;
static const float EPSILON = 1e-6f;
static const float H_MIN = 0.001f;

// ==========================================
// 共通のシミュレーション変数
// ==========================================
float2 _SweGridRes;    // SWEのグリッド解像度
float3 _ApicGridRes;   // APICの3Dグリッド解像度
float _dxSwe;            // SWEのセル幅
float _dxApic;           // APICのセル幅
int M_ratio;             // 解像度比 (M = _dxApic / _dxSwe)

float base_mass;         // 粒子の基準質量
float sea_bottom_z;      // 仮想水深（海底の基準高さ）

float dt_apic;           // APICのタイムステップ
float dt_swe;            // SWEのサブタイムステップ
float3 apic_world_offset;// APIC領域のワールド座標オフセット
float2 swe_world_offset; // SWE領域のワールド座標オフセット

// ==========================================
// 共通のヘルパー関数
// ==========================================
// 浮動小数点型のアトミック加算 (CASループによる擬似実装)
// 古いiGPUでは float に対する InterlockedAdd がハードウェアサポートされていないため、
// Compare-And-Swap (InterlockedCompareExchange) を用いてスレッドセーフな加算を行う。
inline void AtomicAddFloat(RWStructuredBuffer<uint> buffer, uint index, float value)
{
    uint comp = buffer[index];
    uint orig;
    
    [allow_uav_condition]
    while (true)
    {
        float newValue = asfloat(comp) + value;
        uint newValueUint = asuint(newValue);
        InterlockedCompareExchange(buffer[index], comp, newValueUint, orig);
        if (orig == comp) break;
        comp = orig;
    }
}

#endif