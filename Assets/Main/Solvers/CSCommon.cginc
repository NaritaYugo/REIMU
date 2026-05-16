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

float _dtApic;
float _dtSwe;
float2 _WorldOffset; // 領域追従用

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