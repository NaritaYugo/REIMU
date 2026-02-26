#ifndef SWE_BRIDGE_INCLUDED
#define SWE_BRIDGE_INCLUDED

struct SWECell { float h; float hu; float hv; float padding; };
StructuredBuffer<SWECell> SWE_State_Buffer;

void GetSWEData_float(
    float vertexID_In, 
    float swe_width_In, 
    float dx_swe_In, 
    out float3 OutPosition)
{
    uint id = (uint)vertexID_In;
    uint swe_width = (uint)swe_width_In;
    
    uint quadID = id / 6;
    uint vID = id % 6;
    uint qx = quadID % (swe_width - 1);
    uint qy = quadID / (swe_width - 1);
    uint2 offsets[6] = { uint2(0,0), uint2(0,1), uint2(1,0), uint2(1,0), uint2(0,1), uint2(1,1) };
    uint2 offset = offsets[vID];
    
    uint vx = qx + offset.x;
    uint vy = qy + offset.y;
    uint bufIdx = vy * swe_width + vx;
    
    float h = SWE_State_Buffer[bufIdx].h;
    OutPosition = float3(vx * dx_swe_In, h, vy * dx_swe_In);
}
#endif