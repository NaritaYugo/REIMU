using UnityEngine;

public partial class SimulationManager
{
    [Header("Rendering")]
    public ComputeShader voxelizerCS;
    public Material fluidMeshMaterial;
    public Material splashMaterial;
    public Material fftOceanMaterial;
    [Tooltip("飛沫(ビルボード)が引き伸ばされる速度の閾値")]
    public float splashSpeedThreshold = 10.0f;

    [Header("Fluid Mesh (Voxelizer) Settings")]
    [Tooltip("マーチングキューブでメッシュ化する際の、1パーティクルあたりの影響半径")]
    public float particleRadius = 1.5f;
    
    [Range(0.01f, 1.0f)]
    [Tooltip("密度がこの値を超えた境界をメッシュの表面とする(等値面)")]
    public float isoLevel = 0.2f;
    
    [Range(1, 3)]
    [Tooltip("APICの計算グリッドに対する、メッシュ描画用グリッドの解像度倍率")]
    public int meshResolutionMultiplier = 2;
    
    [Range(1.0f, 5.0f)]
    [Tooltip("泡の情報を取得するために、周囲の密度をサンプリングする半径")]
    public float foamSampleRadius = 3.0f;

    private ComputeBuffer voxelGridBuffer, voxelMomXBuffer, voxelMomYBuffer, voxelMomZBuffer;
    private ComputeBuffer triangleBuffer, drawArgsBuffer, triTableBuffer, edgeTableBuffer;
    private ComputeBuffer voxelBlurABuffer, voxelBlurBBuffer, voxelFinalDensityBuffer;
    
    // カーネルキャッシュ
    private void InitializeVoxelizer()
    {
        if (voxelizerCS != null)
        {
            voxKernels.Initialize(voxelizerCS);
        }

        edgeTableBuffer = new ComputeBuffer(256, sizeof(int));
        edgeTableBuffer.SetData(MarchingCubesTables.EdgeTable);
        triTableBuffer = new ComputeBuffer(4096, sizeof(int));
        triTableBuffer.SetData(MarchingCubesTables.TriTable);

        int gridX = apicGridRes.x * meshResolutionMultiplier;
        int gridY = apicGridRes.y * meshResolutionMultiplier; 
        int gridZ = apicGridRes.z * meshResolutionMultiplier;
        int totalVoxels = gridX * gridY * gridZ;

        voxelGridBuffer = new ComputeBuffer(totalVoxels, sizeof(int));
        voxelMomXBuffer = new ComputeBuffer(totalVoxels, sizeof(int));
        voxelMomYBuffer = new ComputeBuffer(totalVoxels, sizeof(int));
        voxelMomZBuffer = new ComputeBuffer(totalVoxels, sizeof(int));
        triangleBuffer = new ComputeBuffer(totalVoxels * 5, 60, ComputeBufferType.Append);
        drawArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
        drawArgsBuffer.SetData(new uint[] { 3, 0, 0, 0 });
        voxelBlurABuffer = new ComputeBuffer(totalVoxels, sizeof(float));
        voxelBlurBBuffer = new ComputeBuffer(totalVoxels, sizeof(float));
        voxelFinalDensityBuffer = new ComputeBuffer(totalVoxels, sizeof(float));
    }

    private void DispatchAndRenderVoxelizer()
    {
        int gridX = apicGridRes.x * meshResolutionMultiplier;
        int gridY = apicGridRes.y * meshResolutionMultiplier; 
        int gridZ = apicGridRes.z * meshResolutionMultiplier;
        int tgVoxelX = (gridX + 7) / 8;
        int tgVoxelY = (gridY + 7) / 8;
        int tgVoxelZ = (gridZ + 7) / 8;
        float currentCellSize = dxApic / meshResolutionMultiplier;

        if (voxelizerCS != null) {
            voxelizerCS.SetVector("apic_world_offset", apicWorldOffset);
            voxelizerCS.SetVector("swe_world_offset", sweWorldOffset);
            voxelizerCS.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
            voxelizerCS.SetFloat("_CellSize", currentCellSize);        
            voxelizerCS.SetFloat("_ParticleRadius", particleRadius);  
            voxelizerCS.SetFloat("_IsoLevel", isoLevel);   
            voxelizerCS.SetFloat("_SplashSpeedThreshold", splashSpeedThreshold);
            voxelizerCS.SetFloat("sea_bottom_z", seaBottomHeight);

            // =========================================================
            // Step 13-1: ボクセルグリッドの初期化
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_MomX", voxelMomXBuffer);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_MomY", voxelMomYBuffer);
            voxelizerCS.SetBuffer(voxKernels.ClearVoxelGrid, "VoxelGrid_MomZ", voxelMomZBuffer);
            voxelizerCS.Dispatch(voxKernels.ClearVoxelGrid, tgVoxelX, tgVoxelY, tgVoxelZ);

            // =========================================================
            // Step 13-2: 粒子のスプラッティング (Splat)
            // APIC粒子が持つ質量(密度)と運動量を、描画用の高解像度ボクセルグリッドに焼き付ける
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "ActiveParticleList_Read", buffers.activeParticleList);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "ActiveParticleCount", buffers.activeParticleCount);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_MomX", voxelMomXBuffer);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_MomY", voxelMomYBuffer);
            voxelizerCS.SetBuffer(voxKernels.SplatParticles, "VoxelGrid_MomZ", voxelMomZBuffer);
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
            voxelizerCS.SetBuffer(voxKernels.SplatSWE, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(voxKernels.SplatSWE, "SWE_State_Read", buffers.sweStateRead);
            voxelizerCS.SetInts("_SweGridRes", new int[] { sweGridRes.x, sweGridRes.y });
            voxelizerCS.SetFloat("_dxSwe", dxSwe);
            voxelizerCS.Dispatch(voxKernels.SplatSWE, tgVoxelX, tgVoxelY, tgVoxelZ);

            // =========================================================
            // Step 13-4: 密度のブラー処理 (XYZ 3パス)
            // 少し離れた位置の密度をサンプリングできるよう、グリッド全体にぼかしをかける
            // =========================================================
            voxelizerCS.SetBuffer(voxKernels.BlurX, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(voxKernels.BlurX, "VoxelGrid_BlurA", voxelBlurABuffer);
            voxelizerCS.Dispatch(voxKernels.BlurX, tgVoxelX, tgVoxelY, tgVoxelZ);

            voxelizerCS.SetBuffer(voxKernels.BlurY, "VoxelGrid_BlurA", voxelBlurABuffer);
            voxelizerCS.SetBuffer(voxKernels.BlurY, "VoxelGrid_BlurB", voxelBlurBBuffer);
            voxelizerCS.Dispatch(voxKernels.BlurY, tgVoxelX, tgVoxelY, tgVoxelZ);

            voxelizerCS.SetBuffer(voxKernels.BlurZ, "VoxelGrid_BlurB", voxelBlurBBuffer);
            voxelizerCS.SetBuffer(voxKernels.BlurZ, "VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
            voxelizerCS.Dispatch(voxKernels.BlurZ, tgVoxelX, tgVoxelY, tgVoxelZ);

            // =========================================================
            // Step 13-5: マーチングキューブ (Marching Cubes)
            // スカラー場（密度グリッド）から等値面（IsoLevel）を抽出し、ポリゴン(Triangle)を生成する
            // =========================================================
            triangleBuffer.SetCounterValue(0); 
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_MomX", voxelMomXBuffer);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_MomY", voxelMomYBuffer);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_MomZ", voxelMomZBuffer);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "TriangleBuffer", triangleBuffer);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "edgeTable", edgeTableBuffer);
            voxelizerCS.SetBuffer(voxKernels.MarchingCubes, "triTable", triTableBuffer);
            voxelizerCS.Dispatch(voxKernels.MarchingCubes, tgVoxelX, tgVoxelY, tgVoxelZ);

            ComputeBuffer.CopyCount(triangleBuffer, drawArgsBuffer, 4);
        }

        // =========================================================
        // Step 14: プロシージャル描画 (Graphics.DrawProcedural)
        // CPUを介さず、GPU上のバッファから直接メッシュを描画する
        // =========================================================

        // 1. APIC流体本体のメッシュ描画
        if (fluidMeshMaterial != null) {
            fluidMeshMaterial.SetBuffer("TriangleBuffer", triangleBuffer);
            Graphics.DrawProceduralIndirect(fluidMeshMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, drawArgsBuffer, 0);
        }

        // 2. 飛沫（スプラッシュ）のビルボード描画
        if (splashMaterial != null && buffers.apicParticle != null) {
            splashMaterial.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
            splashMaterial.SetFloat("_CellSize", currentCellSize);
            splashMaterial.SetFloat("_SpeedThreshold", splashSpeedThreshold);
            splashMaterial.SetBuffer("APIC_Particle_Buffer", buffers.apicParticle);
            splashMaterial.SetVector("apic_world_offset", apicWorldOffset);

            if (voxelFinalDensityBuffer != null) {
                splashMaterial.SetBuffer("VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
                splashMaterial.SetFloat("_IsoLevel", isoLevel);
            }
            Graphics.DrawProcedural(splashMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, maxParticles * 6, 1);
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
                fftOceanMaterial.SetFloat("SeaBottomZ", seaBottomHeight);
            }
            if (buffers.sweStateRead != null) {
                fftOceanMaterial.SetBuffer("SWE_State_Buffer", buffers.sweStateRead);
                fftOceanMaterial.SetFloat("_swe_width", sweGridRes.x);
                fftOceanMaterial.SetFloat("_dxSwe", dxSwe);
                fftOceanMaterial.SetVector("_swe_world_offset", sweWorldOffset);
            }
            if (voxelFinalDensityBuffer != null) {
                fftOceanMaterial.SetBuffer("VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
                fftOceanMaterial.SetFloat("_IsoLevel", isoLevel);
            }
            if (terrainHeightMap != null) {
                fftOceanMaterial.SetTexture("TerrainHeightMap", terrainHeightMap);
            }
        }
    }
}