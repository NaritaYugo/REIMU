using UnityEngine;

public partial class SimulationManager
{
    [Header("Rendering")]
    [SerializeField] private ComputeShader voxelizerCS;
    [SerializeField] private Material fluidMeshMaterial;
    [SerializeField] private Material splashMaterial;
    [SerializeField] private Material fftOceanMaterial;
    [SerializeField] private float m_SplashStretchTH = 10.0f;

    [Header("Voxelizer Settings")]
    [SerializeField] private float m_McParticleRadius = 1.5f;
    [SerializeField] private float m_IsoLevelTH = 0.2f; 
    [SerializeField] private int m_McCellsPerApicCell = 2;

    private void InitializeVoxelizer()
    {
        voxKernels.Initialize(voxelizerCS);

        buffers.edgeTable = new ComputeBuffer(256, sizeof(int));
        buffers.edgeTable.SetData(MarchingCubesTables.EdgeTable);
        buffers.triTable = new ComputeBuffer(4096, sizeof(int));
        buffers.triTable.SetData(MarchingCubesTables.TriTable);

        Vector3Int voxelGridRes = apicGridRes * m_McCellsPerApicCell;
        int totalVoxels = voxelGridRes.x * voxelGridRes.y * voxelGridRes.z;

        buffers.voxelGrid = new ComputeBuffer(totalVoxels, sizeof(int));
        buffers.voxelMomX = new ComputeBuffer(totalVoxels, sizeof(int));
        buffers.voxelMomY = new ComputeBuffer(totalVoxels, sizeof(int));
        buffers.voxelMomZ = new ComputeBuffer(totalVoxels, sizeof(int));
        buffers.triangle = new ComputeBuffer(totalVoxels * 5, 60, ComputeBufferType.Append);
        buffers.drawArgs = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
        buffers.drawArgs.SetData(new uint[] { 3, 0, 0, 0 });
        buffers.voxelBlurA = new ComputeBuffer(totalVoxels, sizeof(float));
        buffers.voxelBlurB = new ComputeBuffer(totalVoxels, sizeof(float));
        buffers.voxelFinalDensity = new ComputeBuffer(totalVoxels, sizeof(float));

        buffers.edgeToVertexTable = new ComputeBuffer(12, sizeof(int) * 2);
        // エッジ番号から頂点番号を引くLUT
        Vector2Int[] edgeToVertexData = new Vector2Int[12]
        {
            new(0, 1), new(1, 2), new(2, 3), new(3, 0),
            new(4, 5), new(5, 6), new(6, 7), new(7, 4),
            new(0, 4), new(1, 5), new(2, 6), new(3, 7)
        };
        buffers.edgeToVertexTable.SetData(edgeToVertexData);
    }

    private void DispatchAndRenderVoxelizer()
    {
        Vector3Int voxelGridRes = apicGridRes * m_McCellsPerApicCell;
        Vector3Int tgVoxel = (voxelGridRes + new Vector3Int(7,7,7)) / 8;
        Vector3Int tgMarching = (voxelGridRes + new Vector3Int(3,3,3)) / 4;

        float currentCellSize = dxApic / m_McCellsPerApicCell;

        if (voxelizerCS != null) {
            voxelizerCS.SetVector("_WorldOffset", worldOffset);
            voxelizerCS.SetVector("_GridSize", new Vector4(voxelGridRes.x, voxelGridRes.y, voxelGridRes.z, 0));
            voxelizerCS.SetFloat("_CellSize", currentCellSize);        
            voxelizerCS.SetFloat("_ParticleRadius", m_McParticleRadius);  
            voxelizerCS.SetFloat("_IsoLevel", m_IsoLevelTH);   
            voxelizerCS.SetFloat("_SplashSpeedThreshold", m_SplashStretchTH);

            // =========================================================
            // Step 13-1: ボクセルグリッドの初期化
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "_VoxelDensity", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "_VoxelMomX", buffers.voxelMomX);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "_VoxelMomY", buffers.voxelMomY);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "_VoxelMomZ", buffers.voxelMomZ);
            voxelizerCS.Dispatch(voxKernels.ClearVoxelGrid, tgVoxel.x, tgVoxel.y, tgVoxel.z);

            // =========================================================
            // Step 13-2: APICのスプラッテング
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "_ActiveParticleList_R", buffers.activeParticleList);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "_ActiveParticleCount", buffers.activeParticleCount);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "_VoxelDensity", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "_VoxelMomX", buffers.voxelMomX);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "_VoxelMomY", buffers.voxelMomY);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "_VoxelMomZ", buffers.voxelMomZ);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "_ParticleBuffer", buffers.apicParticle);
            voxelizerCS.DispatchIndirect(voxKernels.SplatParticles, buffers.particleDispatchArgs);

            if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                voxelizerCS.SetTexture(voxKernels.SplatSWE, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
                voxelizerCS.SetTexture(voxKernels.SplatSWE, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
                voxelizerCS.SetTexture(voxKernels.SplatSWE, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
                voxelizerCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                voxelizerCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                voxelizerCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
            }

            // =========================================================
            // Step 13-3: SWEのスプラッティング
            // =========================================================
            voxelizerCS.SetTexture(voxKernels.SplatSWE, "TerrainHeightMap", terrainHeightMap);
            voxelizerCS.SetBuffer(voxKernels.SplatSWE, "_VoxelDensity", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.SplatSWE, "_SweState_R", buffers.sweStateRead);
            voxelizerCS.SetInts("_SweGridRes", new int[] { sweGridRes.x, sweGridRes.y });
            voxelizerCS.SetFloat("_dxSwe", dxSwe);
            voxelizerCS.Dispatch(voxKernels.SplatSWE, tgVoxel.x, tgVoxel.y, tgVoxel.z);

            // =========================================================
            // Step 13-4: 密度のブラー処理
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.BlurX, "_VoxelDensity", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.BlurX, "_VoxelBlurA", buffers.voxelBlurA);
            voxelizerCS.Dispatch(voxKernels.BlurX, tgVoxel.x, tgVoxel.y, tgVoxel.z);

            voxelizerCS.SetBuffer(voxKernels.BlurY, "_VoxelBlurA", buffers.voxelBlurA);
            voxelizerCS.SetBuffer(voxKernels.BlurY, "_VoxelBlurB", buffers.voxelBlurB);
            voxelizerCS.Dispatch(voxKernels.BlurY, tgVoxel.x, tgVoxel.y, tgVoxel.z);

            voxelizerCS.SetBuffer(voxKernels.BlurZ, "_VoxelBlurB", buffers.voxelBlurB);
            voxelizerCS.SetBuffer(voxKernels.BlurZ, "_VoxelFinalDensity", buffers.voxelFinalDensity);
            voxelizerCS.Dispatch(voxKernels.BlurZ, tgVoxel.x, tgVoxel.y, tgVoxel.z);

            // =========================================================
            // Step 13-5: マーチングキューブ
            // =========================================================
            buffers.triangle.SetCounterValue(0); 
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_VoxelDensity", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_VoxelMomX", buffers.voxelMomX);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_VoxelMomY", buffers.voxelMomY);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_VoxelMomZ", buffers.voxelMomZ);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_VoxelFinalDensity", buffers.voxelFinalDensity);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_TriangleBuffer", buffers.triangle);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_EdgeTable", buffers.edgeTable);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_TriTable", buffers.triTable);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "_EdgeToVertexTable", buffers.edgeToVertexTable);
            voxelizerCS.Dispatch(voxKernels.MarchingCubes, tgMarching.x, tgMarching.y, tgMarching.z);

            ComputeBuffer.CopyCount(buffers.triangle, buffers.drawArgs, 4);
        }

        // =========================================================
        // Step 14: プロシージャル描画
        // =========================================================

        // APIC流体本体のメッシュ描画
        if (fluidMeshMaterial != null) {
            fluidMeshMaterial.SetBuffer("TriangleBuffer", buffers.triangle);
            Graphics.DrawProceduralIndirect(fluidMeshMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, buffers.drawArgs, 0);
        }

        // 飛沫（スプラッシュ）のビルボード描画
        if (splashMaterial != null && buffers.apicParticle != null) {
            splashMaterial.SetVector("_GridSize", new Vector4(voxelGridRes.x, voxelGridRes.y, voxelGridRes.z, 0));
            splashMaterial.SetFloat("_CellSize", currentCellSize);
            splashMaterial.SetFloat("_SpeedThreshold", m_SplashStretchTH);
            splashMaterial.SetBuffer("_ApicParticle", buffers.apicParticle);
            splashMaterial.SetVector("_WorldOffset", worldOffset);

            if (buffers.voxelFinalDensity != null) {
                splashMaterial.SetBuffer("_VoxelFinalDensity", buffers.voxelFinalDensity);
                splashMaterial.SetFloat("_IsoLevel", m_IsoLevelTH);
            }
            Graphics.DrawProcedural(splashMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, m_MaxParticles * 6, 1);
        }

        // SWEとFFTの描画
        if (fftOceanMaterial != null) {
            if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                fftOceanMaterial.SetTexture("FFT_DispLOD0", fftOcean.displacementMaps[0]);
                fftOceanMaterial.SetTexture("FFT_DispLOD1", fftOcean.displacementMaps[1]);
                fftOceanMaterial.SetTexture("FFT_DispLOD2", fftOcean.displacementMaps[2]);
                fftOceanMaterial.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                fftOceanMaterial.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                fftOceanMaterial.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
            }
            if (buffers.sweStateRead != null) {
                fftOceanMaterial.SetBuffer("_SweState_R", buffers.sweStateRead);
                fftOceanMaterial.SetFloat("_IsoLevel", m_IsoLevelTH);
                fftOceanMaterial.SetFloat("_SweWidth", sweGridRes.x);
                fftOceanMaterial.SetFloat("_dxSwe", dxSwe);
                fftOceanMaterial.SetVector("_WorldOffset", worldOffset);
            }
            if (terrainHeightMap != null) {
                fftOceanMaterial.SetTexture("TerrainHeightMap", terrainHeightMap);
            }
        }
    }
}