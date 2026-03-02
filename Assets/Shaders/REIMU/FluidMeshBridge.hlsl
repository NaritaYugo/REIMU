#ifndef FLUID_MESH_BRIDGE_INCLUDED
#define FLUID_MESH_BRIDGE_INCLUDED

struct Triangle { float3 v0, v1, v2; float3 norm; float3 velocity; };

StructuredBuffer<Triangle> TriangleBuffer;
StructuredBuffer<float> VoxelGrid_FoamFactor;

// 【変更】引数に apic_world_offset_In を追加
void GetFluidData_float(
    float vertexID_In, 
    float instanceID_In, 
    float3 GridSize_In, 
    float CellSize_In, 
    float FoamFactorThreshold_In,
    float3 apic_world_offset_In, 
    out float3 OutPosition, 
    out float3 OutNormal, 
    out float OutFoam)
{
    uint vertexID = (uint)vertexID_In;
    uint instanceID = (uint)instanceID_In;

    Triangle tri = TriangleBuffer[instanceID];
    
    // OutPosition は Compute Shader 側ですでにワールド座標化されているのでそのまま出力
    OutPosition = (vertexID == 0) ? tri.v0 : ((vertexID == 1) ? tri.v1 : tri.v2);
    OutNormal = tri.norm;

    // 【修正】ワールド座標からオフセットを引いて、ボクセルのローカルインデックスを計算
    float3 localPos = OutPosition - apic_world_offset_In;
    int3 voxelIdx = int3(localPos / CellSize_In);
    
    float foamVal = 1.0; 
    
    if (all(voxelIdx >= 0) && all(voxelIdx < GridSize_In)) {
        int flatIdx = voxelIdx.x + voxelIdx.y * GridSize_In.x + voxelIdx.z * GridSize_In.x * GridSize_In.y;
        foamVal = VoxelGrid_FoamFactor[flatIdx]; 
    }

    OutFoam = 1.0 - smoothstep(0.0, FoamFactorThreshold_In, foamVal);
}
#endif