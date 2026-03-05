#ifndef FLUID_MESH_BRIDGE_INCLUDED
#define FLUID_MESH_BRIDGE_INCLUDED

struct Triangle { float3 v0, v1, v2; float3 norm; float3 velocity; };

struct SWECell {
    float h; float hu; float hv; float foam;
};

StructuredBuffer<Triangle> TriangleBuffer;
StructuredBuffer<SWECell> SWE_State_Buffer;

void GetFluidData_float(
    float vertexID_In, 
    float instanceID_In, 
    float2 swe_world_offset_In, // 【追加】SWEのオフセット
    float swe_width_In,         // 【追加】SWEのグリッド幅
    float dx_swe_In,            // 【追加】SWEのセルサイズ
    out float3 OutPosition, 
    out float3 OutNormal, 
    out float OutFoam)
{
    uint vertexID = (uint)vertexID_In;
    uint instanceID = (uint)instanceID_In;

    Triangle tri = TriangleBuffer[instanceID];
    
    OutPosition = (vertexID == 0) ? tri.v0 : ((vertexID == 1) ? tri.v1 : tri.v2);
    OutNormal = tri.norm;

    // 【修正】3Dボクセル座標ではなく、描画座標(ワールドX,Z)から直接SWEの2Dインデックスを計算
    float2 sweLocal = OutPosition.xz - swe_world_offset_In;
    
    int sweX = clamp((int)floor(sweLocal.x / dx_swe_In), 0, (int)swe_width_In - 1);
    int sweY = clamp((int)floor(sweLocal.y / dx_swe_In), 0, (int)swe_width_In - 1);
    
    int sweIdx = sweY * (int)swe_width_In + sweX;

    // SWEグリッドの範囲内ならfoam値を読み取る
    float base_foam = 0.0;
    if (sweLocal.x >= 0.0 && sweLocal.x < swe_width_In * dx_swe_In &&
        sweLocal.y >= 0.0 && sweLocal.y < swe_width_In * dx_swe_In) 
    {
        base_foam = SWE_State_Buffer[sweIdx].foam;
    }

    // 取得したfoamの値をそのまま出力（ShaderGraph側でノイズテクスチャと掛け合わせてパキッとさせます）
    OutFoam = saturate(base_foam);
}
#endif