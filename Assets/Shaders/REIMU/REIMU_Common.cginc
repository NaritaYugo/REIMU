struct SWECell {
    float h;
    float hu;
    float hv;
    float padding;
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

// --- 定数・解像度情報 ---
float2 swe_grid_size;     // SWEのグリッド解像度
float3 apic_grid_size;    // APICの3Dグリッド解像度
float dx_swe;           // SWEのセル幅
float dx_apic;          // APICのセル幅
int M_ratio;            // 解像度比 (M = dx_apic / dx_swe)

float rho;              // 密度 (1.0)
float base_mass;        // 粒子の基準質量

float dt_apic;          // APICのタイムステップ
float dt_swe;           // SWEのタイムステップ(サブサイクリング用)

static const float EPSILON = 1e-6f;

// 浮動小数点型のアトミック加算 (CASループ)
// buffer: 対象のuint型バッファ, index: 配列インデックス, value: 加算したい実数値
inline void AtomicAddFloat(RWStructuredBuffer<uint> buffer, uint index, float value)
{
    uint comp = buffer[index];
    uint orig;
    
    // スレッド競合が解消されるまでリトライするループ
    [allow_uav_condition]
    while (true)
    {
        // 現在のuint値をfloatに解釈して加算し、再度uintに変換
        float newValue = asfloat(comp) + value;
        uint newValueUint = asuint(newValue);
        
        // compと等しければnewValueUintを書き込み、元の値をorigに返す
        InterlockedCompareExchange(buffer[index], comp, newValueUint, orig);
        
        // 元の値がcompと同じなら書き込み成功
        if (orig == comp) break;
        
        // 失敗した場合は、他のスレッドによって書き換えられた新しい値でリトライ
        comp = orig;
    }
}