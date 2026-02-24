#ifndef FLUID_MESH_BRIDGE_INCLUDED
#define FLUID_MESH_BRIDGE_INCLUDED

struct Triangle { float3 v0, v1, v2; float3 norm; float3 velocity; };

// C#から渡されるバッファ（これらはBlackboardに無いのでここで宣言）
StructuredBuffer<Triangle> TriangleBuffer;
StructuredBuffer<float> VoxelGrid_FoamFactor;

// Shader Graphから呼び出される関数
// 入力引数に _GridSize, _CellSize, _FoamFactorThreshold を追加しました
void GetFluidData_float(
    float vertexID_In, 
    float instanceID_In, 
    float3 GridSize_In, 
    float CellSize_In, 
    float FoamFactorThreshold_In,
    out float3 OutPosition, 
    out float3 OutNormal, 
    out float OutFoam)
{
    uint vertexID = (uint)vertexID_In;
    uint instanceID = (uint)instanceID_In;

    Triangle tri = TriangleBuffer[instanceID];
    
    OutPosition = (vertexID == 0) ? tri.v0 : ((vertexID == 1) ? tri.v1 : tri.v2);
    OutNormal = tri.norm;

    int3 voxelIdx = int3(OutPosition / CellSize_In);
    float foamVal = 1.0; 
    
    if (all(voxelIdx >= 0) && all(voxelIdx < GridSize_In)) {
        int flatIdx = voxelIdx.x + voxelIdx.y * GridSize_In.x + voxelIdx.z * GridSize_In.x * GridSize_In.y;
        foamVal = VoxelGrid_FoamFactor[flatIdx]; 
    }

    OutFoam = 1.0 - smoothstep(0.0, FoamFactorThreshold_In, foamVal);
}
#endif