#ifndef APIC_MESH_FUNCTIONS_INCLUDED
#define APIC_MESH_FUNCTIONS_INCLUDED

struct Triangle { float3 v0, v1, v2; float3 norm; float3 velocity; };

StructuredBuffer<Triangle> TriangleBuffer;

void GetFluidData_float(
    float vertexID_In, 
    float instanceID_In, 
    out float3 OutPosition, 
    out float3 OutNormal,
    out float3 OutVelocity)
{
    uint vertexID = (uint)vertexID_In;
    uint instanceID = (uint)instanceID_In;

    Triangle tri = TriangleBuffer[instanceID];
    
    OutPosition = (vertexID == 0) ? tri.v0 : ((vertexID == 1) ? tri.v1 : tri.v2);
    OutNormal = tri.norm;
    
    // 自分の頂点用の泡データだけを取り出し、X成分(R)に入れて出力する
    float myFoam = (vertexID == 0) ? tri.velocity.x : ((vertexID == 1) ? tri.velocity.y : tri.velocity.z);
    
    OutVelocity = float3(myFoam, 0.0, 0.0); 
}
#endif