using UnityEngine;

public partial class SimulationManager
{
    [Header("Environment Capture")]
    [Tooltip("地形としてハイトマップに焼き付ける対象のレイヤー")]
    public LayerMask terrainLayer;
    private Texture2D terrainHeightMap;
    private bool isSWEInitialized = false;

    private void InitializeTerrain()
    {
        // 浮動小数点テクスチャ(RFloat)を使うことで、精度の高い地形高をGPUに渡す
        terrainHeightMap = new Texture2D(sweGridRes.x, sweGridRes.y, TextureFormat.RFloat, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
    }

    private void UpdateTrackingAndTerrain()
    {
        if (trackTarget != null)
        {
            // プレイヤー（ターゲット）を中心にシミュレーション領域を追従させる
            Vector3 gridCenter = trackTarget.position;
            float targetBaseX = gridCenter.x - sweGridRes.x * dxSwe * 0.5f;
            float targetBaseZ = gridCenter.z - sweGridRes.y * dxSwe * 0.5f;

            // セル単位（dxSwe）でどれだけ移動したかを計算
            int shiftCellsX = Mathf.RoundToInt((targetBaseX - sweWorldOffset.x) / dxSwe);
            int shiftCellsY = Mathf.RoundToInt((targetBaseZ - sweWorldOffset.y) / dxSwe);

            Vector2 nextSweWorldOffset = sweWorldOffset + new Vector2(shiftCellsX * dxSwe, shiftCellsY * dxSwe);
            
            // グリッドが1セル以上移動した場合のみ、情報をシフト（スクロール）させる
            if (shiftCellsX != 0 || shiftCellsY != 0)
            {
                sweWorldOffset = nextSweWorldOffset; 
                CaptureTerrainHeight();
                
                sweCS.SetInts("shift_amount", new int[] { shiftCellsX, shiftCellsY });
                sweCS.SetVector("swe_world_offset", sweWorldOffset);
                
                if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                    sweCS.SetTexture(kernelSweShift, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
                    sweCS.SetTexture(kernelSweShift, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
                    sweCS.SetTexture(kernelSweShift, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
                    sweCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                    sweCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                    sweCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
                }
                
                sweCS.SetTexture(kernelSweShift, "TerrainHeightMap", terrainHeightMap); 
                sweCS.SetBuffer(kernelSweShift, "SWE_State_Read", buffers.sweStateRead);
                sweCS.SetBuffer(kernelSweShift, "SWE_State_Write", buffers.sweStateWrite);
                sweCS.Dispatch(kernelSweShift, (sweGridRes.x + 7) / 8, (sweGridRes.y + 7) / 8, 1);
                SwapSWEBuffers();
            }
            
            apicWorldOffset = new Vector3(sweWorldOffset.x, sweWorldOffset.y, seaBottomHeight);
        }

        if (!isSWEInitialized)
        {
            CaptureTerrainHeight();
            sweCS.SetVector("swe_world_offset", sweWorldOffset);
            
            sweCS.SetTexture(kernelSweInit, "TerrainHeightMap", terrainHeightMap);
            sweCS.SetBuffer(kernelSweInit, "SWE_State_Write", buffers.sweStateWrite);
            if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                sweCS.SetTexture(kernelSweInit, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
                sweCS.SetTexture(kernelSweInit, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
                sweCS.SetTexture(kernelSweInit, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
            }
            sweCS.Dispatch(kernelSweInit, (sweGridRes.x + 7) / 8, (sweGridRes.y + 7) / 8, 1);
            
            SwapSWEBuffers();
            isSWEInitialized = true;
        }
    }

    private void CaptureTerrainHeight()
    {
        if (terrainHeightMap == null) return;
        float[] heights = new float[sweGridRes.x * sweGridRes.y];
        float startX = sweWorldOffset.x + dxSwe * 0.5f;
        float startZ = sweWorldOffset.y + dxSwe * 0.5f;

        float waterSurfaceY = 0f; 
        float maxSimulationDepth = 5.0f; 
        float lowestSimulationBedY = waterSurfaceY - maxSimulationDepth;

        for (int y = 0; y < sweGridRes.y; y++)
        {
            for (int x = 0; x < sweGridRes.x; x++)
            {
                float worldX = startX + x * dxSwe;
                float worldZ = startZ + y * dxSwe;
                // 上空(Y=1000)から真下へレイを飛ばす
                Vector3 rayStart = new Vector3(worldX, 1000f, worldZ);
                
                if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 2000f, terrainLayer))
                {
                    // シミュレーションの底を突き抜けないようにMaxを取る
                    heights[y * sweGridRes.x + x] = Mathf.Max(hit.point.y, lowestSimulationBedY);
                }
                else
                {
                    heights[y * sweGridRes.x + x] = lowestSimulationBedY; 
                }
            }
        }
        terrainHeightMap.SetPixelData(heights, 0);
        terrainHeightMap.Apply();
        Shader.SetGlobalTexture("TerrainHeightMap", terrainHeightMap);
    }

    private void ReleaseTerrain()
    {
        if (terrainHeightMap != null) Destroy(terrainHeightMap);
    }
}