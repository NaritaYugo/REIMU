using UnityEngine;

public partial class SimulationManager
{
    [Header("Environment Capture")]
    public LayerMask terrainLayer;
    private Texture2D terrainHeightMap;
    private bool isSWEInitialized = false;

    private void InitializeTerrain()
    {
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
            Vector3 gridCenter = trackTarget.position;
            float targetBaseX = gridCenter.x - sweGridRes.x * dxSwe * 0.5f;
            float targetBaseZ = gridCenter.z - sweGridRes.y * dxSwe * 0.5f;

            int shiftCellsX = Mathf.RoundToInt((targetBaseX - worldOffset.x) / dxSwe);
            int shiftCellsY = Mathf.RoundToInt((targetBaseZ - worldOffset.y) / dxSwe);

            Vector2 nextWorldOffset = worldOffset + new Vector2(shiftCellsX * dxSwe, shiftCellsY * dxSwe);
            
            // グリッドが1セル以上移動した場合のみ、情報をシフト（スクロール）させる
            if (shiftCellsX != 0 || shiftCellsY != 0)
            {
                worldOffset = nextWorldOffset; 
                CaptureTerrainHeight();
                
                sweCS.SetInts("shift_amount", new int[] { shiftCellsX, shiftCellsY });
                sweCS.SetVector("_WorldOffset", worldOffset);
                
                if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                    sweCS.SetTexture(sweKernels.ShiftSweGrid, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
                    sweCS.SetTexture(sweKernels.ShiftSweGrid, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
                    sweCS.SetTexture(sweKernels.ShiftSweGrid, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
                    sweCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                    sweCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                    sweCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
                }
                
                sweCS.SetTexture(sweKernels.ShiftSweGrid, "TerrainHeightMap", terrainHeightMap); 
                sweCS.SetBuffer(sweKernels.ShiftSweGrid, "_SweState_R", buffers.sweStateRead);
                sweCS.SetBuffer(sweKernels.ShiftSweGrid, "_SweState_W", buffers.sweStateWrite);
                sweCS.Dispatch(sweKernels.ShiftSweGrid, (sweGridRes.x + 7) / 8, (sweGridRes.y + 7) / 8, 1);
                SwapSWEBuffers();
            }
        }

        if (!isSWEInitialized)
        {
            CaptureTerrainHeight();
            sweCS.SetVector("_WorldOffset", worldOffset);
            
            sweCS.SetTexture(sweKernels.InitSwe, "TerrainHeightMap", terrainHeightMap);
            sweCS.SetBuffer(sweKernels.InitSwe, "_SweState_W", buffers.sweStateWrite);
            if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                sweCS.SetTexture(sweKernels.InitSwe, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
                sweCS.SetTexture(sweKernels.InitSwe, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
                sweCS.SetTexture(sweKernels.InitSwe, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
            }
            sweCS.Dispatch(sweKernels.InitSwe, (sweGridRes.x + 7) / 8, (sweGridRes.y + 7) / 8, 1);
            
            SwapSWEBuffers();
            isSWEInitialized = true;
        }
    }

    private void CaptureTerrainHeight()
    {
        if (terrainHeightMap == null) return;
        float[] heights = new float[sweGridRes.x * sweGridRes.y];
        float startX = worldOffset.x + dxSwe * 0.5f;
        float startZ = worldOffset.y + dxSwe * 0.5f;

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
                Vector3 rayStart = new(worldX, 1000f, worldZ);
                
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