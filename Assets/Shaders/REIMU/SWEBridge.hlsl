#ifndef SWE_BRIDGE_INCLUDED
#define SWE_BRIDGE_INCLUDED

// --- FFTのテクスチャとサンプラーの宣言 ---
Texture2D<float4> FFT_DispLOD0; SamplerState samplerFFT_DispLOD0;
Texture2D<float4> FFT_DispLOD1; SamplerState samplerFFT_DispLOD1;
Texture2D<float4> FFT_DispLOD2; SamplerState samplerFFT_DispLOD2;

struct SWECell { float h; float hu; float hv; float padding; };
StructuredBuffer<SWECell> SWE_State_Buffer;

void GetSWEData_float(
    float vertexID_In, 
    float swe_width_In, 
    float dx_swe_In, 
    float2 swe_world_offset_In,
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
    
    OutPosition = float3(vx * dx_swe_In + swe_world_offset_In.x, h, vy * dx_swe_In + swe_world_offset_In.y);
}

void GetFFTDisplacement_float(
    float3 WorldPos, 
    float Size0, float Size1, float Size2, 
    out float3 OutDisplacement)
{
    float2 uv0 = WorldPos.xz / Size0;
    float2 uv1 = WorldPos.xz / Size1;
    float2 uv2 = WorldPos.xz / Size2;

    float dist = distance(_WorldSpaceCameraPos, WorldPos);

    float4 d2 = FFT_DispLOD2.SampleLevel(samplerFFT_DispLOD2, uv2, 0);
    float3 totalDisp = float3(d2.x, d2.y, d2.z);

    float blend1 = 1.0 - saturate((dist - 400.0) / 100.0); 
    if (blend1 > 0.0)
    {
        float4 d1 = FFT_DispLOD1.SampleLevel(samplerFFT_DispLOD1, uv1, 0);
        totalDisp += float3(d1.x, d1.y, d1.z) * blend1;
    }

    float blend0 = 1.0 - saturate((dist - 100.0) / 50.0);
    if (blend0 > 0.0)
    {
        float4 d0 = FFT_DispLOD0.SampleLevel(samplerFFT_DispLOD0, uv0, 0);
        totalDisp += float3(d0.x, d0.y, d0.z) * blend0;
    }

    OutDisplacement = totalDisp;
}
#endif