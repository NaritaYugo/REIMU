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
    [SerializeField] private float m_FoamSampleRadius = 3.0f;
    
    // カーネルキャッシュ
    private void InitializeVoxelizer()
    {
        if (voxelizerCS != null)
        {
            voxKernels.Initialize(voxelizerCS);
        }

        buffers.edgeTable = new ComputeBuffer(256, sizeof(int));
        buffers.edgeTable.SetData(MarchingCubesTables.EdgeTable);
        buffers.triTable = new ComputeBuffer(4096, sizeof(int));
        buffers.triTable.SetData(MarchingCubesTables.TriTable);

        int gridX = apicGridRes.x * m_McCellsPerApicCell;
        int gridY = apicGridRes.y * m_McCellsPerApicCell; 
        int gridZ = apicGridRes.z * m_McCellsPerApicCell;
        int totalVoxels = gridX * gridY * gridZ;

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
    }

    private void DispatchAndRenderVoxelizer()
    {
        int gridX = apicGridRes.x * m_McCellsPerApicCell;
        int gridY = apicGridRes.y * m_McCellsPerApicCell; 
        int gridZ = apicGridRes.z * m_McCellsPerApicCell;
        int tgVoxelX = (gridX + 7) / 8;
        int tgVoxelY = (gridY + 7) / 8;
        int tgVoxelZ = (gridZ + 7) / 8;
        float currentCellSize = dxApic / m_McCellsPerApicCell;

        if (voxelizerCS != null) {
            voxelizerCS.SetVector("_ApicWorldOffset", apicWorldOffset);
            voxelizerCS.SetVector("_SweWorldOffset", sweWorldOffset);
            voxelizerCS.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
            voxelizerCS.SetFloat("_CellSize", currentCellSize);        
            voxelizerCS.SetFloat("_ParticleRadius", m_McParticleRadius);  
            voxelizerCS.SetFloat("_IsoLevel", m_IsoLevelTH);   
            voxelizerCS.SetFloat("_SplashSpeedThreshold", m_SplashStretchTH);
            voxelizerCS.SetFloat("sea_bottom_z", m_VirtualSeaBottomHeight);

            // =========================================================
            // Step 13-1: ボクセルグリッドの初期化
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_Density", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_MomX", buffers.voxelMomX);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_MomY", buffers.voxelMomY);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_MomZ", buffers.voxelMomZ);
            voxelizerCS.Dispatch(voxKernels.ClearVoxelGrid, tgVoxelX, tgVoxelY, tgVoxelZ);

            // =========================================================
            // Step 13-2: 粒子のスプラッティング (Splat)
            // APIC粒子が持つ質量(密度)と運動量を、描画用の高解像度ボクセルグリッドに焼き付ける
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "ActiveParticleList_Read", buffers.activeParticleList);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "ActiveParticleCount", buffers.activeParticleCount);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_Density", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_MomX", buffers.voxelMomX);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_MomY", buffers.voxelMomY);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_MomZ", buffers.voxelMomZ);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "ParticleBuffer", buffers.apicParticle);
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
            // Step 13-3: SWE層のスプラッティング
            // APICボクセルとSWE/FFTの境界が不自然に切れないよう、SWEの水面も密度として加算する
            // =========================================================
            voxelizerCS.SetTexture(voxKernels.SplatSWE, "TerrainHeightMap", terrainHeightMap);
            voxelizerCS.SetBuffer(voxKernels.SplatSWE, "VoxelGrid_Density", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.SplatSWE, "SWE_State_Read", buffers.sweStateRead);
            voxelizerCS.SetInts("_SweGridRes", new int[] { sweGridRes.x, sweGridRes.y });
            voxelizerCS.SetFloat("_dxSwe", dxSwe);
            voxelizerCS.Dispatch(voxKernels.SplatSWE, tgVoxelX, tgVoxelY, tgVoxelZ);

            // =========================================================
            // Step 13-4: 密度のブラー処理 (XYZ 3パス)
            // 少し離れた位置の密度をサンプリングできるよう、グリッド全体にぼかしをかける
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.BlurX, "VoxelGrid_Density", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.BlurX, "VoxelGrid_BlurA", buffers.voxelBlurA);
            voxelizerCS.Dispatch(voxKernels.BlurX, tgVoxelX, tgVoxelY, tgVoxelZ);

            voxelizerCS.SetBuffer(voxKernels.BlurY, "VoxelGrid_BlurA", buffers.voxelBlurA);
            voxelizerCS.SetBuffer(voxKernels.BlurY, "VoxelGrid_BlurB", buffers.voxelBlurB);
            voxelizerCS.Dispatch(voxKernels.BlurY, tgVoxelX, tgVoxelY, tgVoxelZ);

            voxelizerCS.SetBuffer(voxKernels.BlurZ, "VoxelGrid_BlurB", buffers.voxelBlurB);
            voxelizerCS.SetBuffer(voxKernels.BlurZ, "VoxelGrid_FinalDensity", buffers.voxelFinalDensity);
            voxelizerCS.Dispatch(voxKernels.BlurZ, tgVoxelX, tgVoxelY, tgVoxelZ);

            // =========================================================
            // Step 13-5: マーチングキューブ (Marching Cubes)
            // スカラー場（密度グリッド）から等値面（IsoLevel）を抽出し、ポリゴン(Triangle)を生成する
            // =========================================================
            buffers.triangle.SetCounterValue(0); 
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_Density", buffers.voxelGrid);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_MomX", buffers.voxelMomX);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_MomY", buffers.voxelMomY);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_MomZ", buffers.voxelMomZ);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_FinalDensity", buffers.voxelFinalDensity);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "TriangleBuffer", buffers.triangle);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "edgeTable", buffers.edgeTable);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "triTable", buffers.triTable);
            voxelizerCS.Dispatch(voxKernels.MarchingCubes, tgVoxelX, tgVoxelY, tgVoxelZ);

            ComputeBuffer.CopyCount(buffers.triangle, buffers.drawArgs, 4);
        }

        // =========================================================
        // Step 14: プロシージャル描画 (Graphics.DrawProcedural)
        // CPUを介さず、GPU上のバッファから直接メッシュを描画する
        // =========================================================

        // 1. APIC流体本体のメッシュ描画
        if (fluidMeshMaterial != null) {
            fluidMeshMaterial.SetBuffer("TriangleBuffer", buffers.triangle);
            Graphics.DrawProceduralIndirect(fluidMeshMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, buffers.drawArgs, 0);
        }

        // 2. 飛沫（スプラッシュ）のビルボード描画
        if (splashMaterial != null && buffers.apicParticle != null) {
            splashMaterial.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
            splashMaterial.SetFloat("_CellSize", currentCellSize);
            splashMaterial.SetFloat("_SpeedThreshold", m_SplashStretchTH);
            splashMaterial.SetBuffer("APIC_Particle_Buffer", buffers.apicParticle);
            splashMaterial.SetVector("_ApicWorldOffset", apicWorldOffset);

            if (buffers.voxelFinalDensity != null) {
                splashMaterial.SetBuffer("VoxelGrid_FinalDensity", buffers.voxelFinalDensity);
                splashMaterial.SetFloat("_IsoLevel", m_IsoLevelTH);
            }
            Graphics.DrawProcedural(splashMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, m_MaxParticles * 6, 1);
        }

        // 3. SWEとFFTの描画
        if (fftOceanMaterial != null) {
            if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                fftOceanMaterial.SetTexture("FFT_DispLOD0", fftOcean.displacementMaps[0]);
                fftOceanMaterial.SetTexture("FFT_DispLOD1", fftOcean.displacementMaps[1]);
                fftOceanMaterial.SetTexture("FFT_DispLOD2", fftOcean.displacementMaps[2]);
                fftOceanMaterial.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                fftOceanMaterial.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                fftOceanMaterial.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
                fftOceanMaterial.SetFloat("SeaBottomZ", m_VirtualSeaBottomHeight);
            }
            if (buffers.sweStateRead != null) {
                fftOceanMaterial.SetBuffer("SWE_State_Buffer", buffers.sweStateRead);
                fftOceanMaterial.SetFloat("_swe_width", sweGridRes.x);
                fftOceanMaterial.SetFloat("_dxSwe", dxSwe);
                fftOceanMaterial.SetVector("_SweWorldOffset", sweWorldOffset);
            }
            if (buffers.voxelFinalDensity != null) {
                fftOceanMaterial.SetBuffer("VoxelGrid_FinalDensity", buffers.voxelFinalDensity);
                fftOceanMaterial.SetFloat("_IsoLevel", m_IsoLevelTH);
            }
            if (terrainHeightMap != null) {
                fftOceanMaterial.SetTexture("TerrainHeightMap", terrainHeightMap);
            }
        }
    }
}