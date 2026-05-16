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
static const float GRAVITY = 9.81f;
static const float H_MIN = 0.001f;
static const float ATOMIC_SCALE = 10000.0f;

// ==========================================
// 共通のシミュレーション変数
// ==========================================
float2 _SweGridRes;
float3 _ApicGridRes;
float _dxSwe;
float _dxApic;

float base_mass;         // 粒子の基準質量
float sea_bottom_z;      // 仮想水深（海底の基準高さ）

float _dtApic;
float _dtSwe;
float3 apic_world_offset;// APIC領域のワールド座標オフセット
float2 swe_world_offset; // SWE領域のワールド座標オフセット

// ==========================================
// 共通のヘルパー関数
// ==========================================
// 浮動小数点型のアトミック加算
inline void AtomicAddFloat(RWStructuredBuffer<uint> buffer, uint index, float value) {
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