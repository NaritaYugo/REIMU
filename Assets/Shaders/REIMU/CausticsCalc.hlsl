#ifndef CAUSTICS_CALC_INCLUDED
#define CAUSTICS_CALC_INCLUDED

#include "FFTBridge.hlsl"

#ifndef SWE_CELL_DEFINED
#define SWE_CELL_DEFINED
#endif

// 新しいコースティクス制御関数（深さ計算が不要になった版）
void GetCausticsUVAndMask_float(
    float TrueWaterDepth, 
    float3 SeabedWorldPos, 
    float3 WaterSurfaceNormal, 
    float DistortionStrength, 
    float DepthFadeStrength, 
    out float2 OutCausticsUV, 
    out float OutIntensityMask 
) {
    // 1. 波打ち際のマスク（陸地への漏れを100%防ぐ）
    // 水深0.0mで完全に消え(0)、0.2mで完全に表示される(1)
    float shoreMask = smoothstep(0.0, 0.2, TrueWaterDepth);

    // 2. 深海のマスク（【修正】Smoothstepでパキッと管理する）
    // DepthFadeStrength が例えば 0.1 なら、10mでフェード開始、15mで消滅するような設定にします
    float fadeOutStart = 1.0 / max(0.001, DepthFadeStrength); 
    float fadeOutEnd   = fadeOutStart * 1.5; 
    
    float depthFade = 1.0 - smoothstep(fadeOutStart, fadeOutEnd, TrueWaterDepth);

    // 最終的な明るさマスク
    OutIntensityMask = shoreMask * depthFade;

    // 3. コースティクスのUV座標と歪み計算
    float2 baseUV = SeabedWorldPos.xz;

    // 【修正】DDX/DDY法線の暴走を防ぐため、saturate でXZ成分を -1～1 にクランプ
    float2 safeNormal = saturate(WaterSurfaceNormal.xz); 
    
    float2 distortion = safeNormal * TrueWaterDepth * DistortionStrength;
    
    OutCausticsUV = baseUV + distortion;
}
#endif