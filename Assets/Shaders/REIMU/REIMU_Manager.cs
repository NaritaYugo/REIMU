using UnityEngine;
using System.Runtime.InteropServices;

public class REIMUManager : MonoBehaviour
{
    [Header("Simulation Settings")]
    public int sweGridWidth = 64;
    public int sweGridHeight = 64;
    public float dx_swe = 1.0f;
    private int M_ratio; 
    public int maxParticles = 1000000;

    [Header("APIC Settings")]
    public int apicGridWidth = 64;
    public int apicGridHeight = 64;
    public int apicGridDepth = 64; 
    public float dx_apic = 1.0f;

    [Header("Fluid Settings")]
    public float rho = 1.0f;
    public float baseMass = 0.5f;
    public float seaBottomHight = -15.0f;

    [Header("Sublimation (SWE -> APIC) Settings")]
    public float frThreshold = 1.5f;
    public float gradThreshold = 1.5f;
    public float laplacianThreshold = -3.0f;
    public float conversionRateMultiplier = 10.0f;
    public float pushZMultiplier = 0.2f;
    public float splashVelocityMultiplier = 0.5f;

    [Header("Condensation (APIC -> SWE) Settings")]
    [Range(0f, 1f)] public float horizontalMomentumTransfer = 0.3f;
    [Range(0f, 1f)] public float verticalMomentumToWave = 0.1f;

    [Header("Mouse Interaction")]
    public float mouseRadius = 5.0f;
    public float mouseForce = 50.0f;
    private Vector3 prevMousePos;
    private bool wasMouseDown = false;

    [Header("Compute Shaders")]
    public ComputeShader coreCS;
    public ComputeShader sweCS;
    public ComputeShader apicCS;
    public ComputeShader voxelizerCS;

    [Header("Rendering")]
    public Material sweSurfaceMaterial;
    public Material fluidMeshMaterial;
    public Material splashMaterial;
    public Material seaBottomMaterial;
    public Material fftOceanMaterial;
    
    public float splashSpeedThreshold = 10.0f;

    [Header("Fluid Mesh (Voxelizer) Settings")]
    public float particleRadius = 1.5f;
    [Range(0.01f, 1.0f)] public float isoLevel = 0.2f;
    [Range(1, 3)] public int meshResolutionMultiplier = 2;
    [Range(1.0f, 5.0f)] public float foamSampleRadius = 3.0f;
    
    [Header("Tracking")]
    public FFTOcean fftOcean;
    public Transform trackTarget;
    public Vector2 sweWorldOffset = Vector2.zero; 
    public Vector3 apicWorldOffset = Vector3.zero;

    // --- Compute Buffers ---
    private ComputeBuffer apicParticleBuffer, deltaHBuffer, deltaHUBuffer, deltaHVBuffer;
    private ComputeBuffer sweStateBufferRead, sweStateBufferWrite;
    private ComputeBuffer apicGridMassBuffer, apicGridVelXBuffer, apicGridVelYBuffer, apicGridVelZBuffer;
    private ComputeBuffer apicDivergenceBuffer, apicPressureBufferWrite, particleCounterBuffer;
    private ComputeBuffer voxelGridBuffer, voxelMomXBuffer, voxelMomYBuffer, voxelMomZBuffer;
    private ComputeBuffer triangleBuffer, drawArgsBuffer, triTableBuffer, edgeTableBuffer;
    private ComputeBuffer voxelBlurABuffer, voxelBlurBBuffer, voxelFinalDensityBuffer, voxelFoamFactorBuffer;
    private ComputeBuffer pcgRBuffer, pcgPBuffer, pcgQBuffer, pcgPreconBuffer, pcgDotResultBuffer, pcgScalarsBuffer;
    private ComputeBuffer activeParticleListBuffer, activeParticleCountBuffer, particleDispatchArgsBuffer;

    void Start()
    {
        M_ratio = (int)(dx_apic / dx_swe);
        InitializeBuffers();
        BindBuffers();
    }

    private void InitializeBuffers()
    {
        int sweTotalCells = sweGridWidth * sweGridHeight;
        sweStateBufferRead = new ComputeBuffer(sweTotalCells, Marshal.SizeOf(typeof(SWECell)));
        sweStateBufferWrite = new ComputeBuffer(sweTotalCells, Marshal.SizeOf(typeof(SWECell)));
        apicParticleBuffer = new ComputeBuffer(maxParticles, Marshal.SizeOf(typeof(APICParticle)), ComputeBufferType.Default);
        deltaHBuffer = new ComputeBuffer(sweTotalCells, sizeof(uint));
        deltaHUBuffer = new ComputeBuffer(sweTotalCells, sizeof(uint));
        deltaHVBuffer = new ComputeBuffer(sweTotalCells, sizeof(uint));

        int apicTotalCells = apicGridWidth * apicGridHeight * apicGridDepth;
        apicGridMassBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicGridVelXBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicGridVelYBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicGridVelZBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicDivergenceBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        apicPressureBufferWrite = new ComputeBuffer(apicTotalCells, sizeof(float));
        particleCounterBuffer = new ComputeBuffer(1, sizeof(uint));
        particleCounterBuffer.SetData(new uint[] { 0 });
        
        SWECell[] initialSWE = new SWECell[sweTotalCells];
        for (int i = 0; i < sweTotalCells; i++)
        {
            initialSWE[i] = new SWECell { h = Mathf.Abs(seaBottomHight), hu = 0, hv = 0, padding = 0 };
        }
        sweStateBufferRead.SetData(initialSWE);
        sweStateBufferWrite.SetData(initialSWE);

        edgeTableBuffer = new ComputeBuffer(256, sizeof(int));
        edgeTableBuffer.SetData(MarchingCubesTables.EdgeTable);
        triTableBuffer = new ComputeBuffer(4096, sizeof(int));
        triTableBuffer.SetData(MarchingCubesTables.TriTable);

        int gridX = apicGridWidth * meshResolutionMultiplier;
        int gridY = apicGridDepth * meshResolutionMultiplier; 
        int gridZ = apicGridHeight * meshResolutionMultiplier;
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
        voxelFoamFactorBuffer = new ComputeBuffer(totalVoxels, sizeof(float));
        
        pcgRBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgPBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgQBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgPreconBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgDotResultBuffer = new ComputeBuffer(1, sizeof(int));
        pcgScalarsBuffer = new ComputeBuffer(5, sizeof(float));

        activeParticleListBuffer = new ComputeBuffer(maxParticles, sizeof(uint), ComputeBufferType.Append);
        activeParticleCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        particleDispatchArgsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
    }

    private void BindBuffers()
    {
        ComputeShader[] shaders = { coreCS, sweCS, apicCS };
        foreach (var cs in shaders)
        {
            cs.SetInts("swe_grid_size", new int[] { sweGridWidth, sweGridHeight });
            cs.SetInts("apic_grid_size", new int[] { apicGridWidth, apicGridHeight, apicGridDepth });
            cs.SetFloat("dx_swe", dx_swe);
            cs.SetFloat("dx_apic", dx_apic);
            cs.SetInt("M_ratio", M_ratio);
            cs.SetFloat("rho", rho);            
            cs.SetFloat("base_mass", baseMass);
            cs.SetFloat("sea_bottom_z", seaBottomHight);
        }
        
        coreCS.SetBuffer(coreCS.FindKernel("ClearIntermediateBuffers"), "Delta_H_Buffer", deltaHBuffer);
        coreCS.SetBuffer(coreCS.FindKernel("ClearIntermediateBuffers"), "Delta_HU_Buffer", deltaHUBuffer);
        coreCS.SetBuffer(coreCS.FindKernel("ClearIntermediateBuffers"), "Delta_HV_Buffer", deltaHVBuffer);
    }

    void Update()
    {
        if (sweStateBufferRead == null) return;

        float dt_apic = Mathf.Min(Time.deltaTime, 0.0333f);
        float dt_swe_max = (0.2f * dx_swe) / (Mathf.Sqrt(9.81f * 20.0f) + 15.0f);
        int subSteps = Mathf.CeilToInt(dt_apic / dt_swe_max);
        float dt_swe = dt_apic / subSteps;

        // --- 追従処理 ---
        if (trackTarget != null)
        {
            Vector3 gridCenter = trackTarget.position;
            float targetBaseX = gridCenter.x - (sweGridWidth * dx_swe) * 0.5f;
            float targetBaseZ = gridCenter.z - (sweGridHeight * dx_swe) * 0.5f;

            int shiftCellsX = Mathf.RoundToInt((targetBaseX - sweWorldOffset.x) / dx_swe);
            int shiftCellsY = Mathf.RoundToInt((targetBaseZ - sweWorldOffset.y) / dx_swe);

            if (shiftCellsX != 0 || shiftCellsY != 0)
            {
                sweWorldOffset.x += shiftCellsX * dx_swe;
                sweWorldOffset.y += shiftCellsY * dx_swe;

                int shiftKernel = sweCS.FindKernel("ShiftSWEGrid");
                sweCS.SetInts("shift_amount", new int[] { shiftCellsX, shiftCellsY });
                sweCS.SetVector("swe_world_offset", sweWorldOffset);
                if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                    sweCS.SetTexture(shiftKernel, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
                    sweCS.SetTexture(shiftKernel, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
                    sweCS.SetTexture(shiftKernel, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
                    sweCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                    sweCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                    sweCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
                }
                sweCS.SetBuffer(shiftKernel, "SWE_State_Read", sweStateBufferRead);
                sweCS.SetBuffer(shiftKernel, "SWE_State_Write", sweStateBufferWrite);
                sweCS.Dispatch(shiftKernel, Mathf.CeilToInt(sweGridWidth / 8.0f), Mathf.CeilToInt(sweGridHeight / 8.0f), 1);
                SwapSWEBuffers();
            }

            apicWorldOffset = new Vector3(sweWorldOffset.x, 0f, sweWorldOffset.y);
        }

        ComputeShader[] shaders = { coreCS, sweCS, apicCS };
        foreach (var cs in shaders)
        {
            cs.SetFloat("dt_swe", dt_swe);
            cs.SetFloat("dt_apic", dt_apic);
            cs.SetVector("apic_world_offset", apicWorldOffset);
            cs.SetVector("swe_world_offset", sweWorldOffset);
        }

        // --- マウス入力 ---
        Vector2 mousePosSWE = Vector2.zero;
        Vector2 mouseDirSWE = Vector2.zero;
        int mouseActive = 0;

        if (Input.GetMouseButton(0))
        {
            Plane waterPlane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

            if (waterPlane.Raycast(ray, out float enter))
            {
                Vector3 hitPoint = ray.GetPoint(enter);
                
                if (wasMouseDown)
                {
                    Vector3 delta = hitPoint - prevMousePos;
                    mouseDirSWE = new Vector2(delta.x, delta.z) / dt_apic;
                    
                    if (mouseDirSWE.sqrMagnitude > 0.01f) {
                        mouseActive = 1;
                    }
                }
                prevMousePos = hitPoint;
                mousePosSWE = new Vector2(hitPoint.x, hitPoint.z);
            }
        }
        wasMouseDown = Input.GetMouseButton(0);

        sweCS.SetVector("mouse_pos", mousePosSWE);
        sweCS.SetVector("mouse_dir", mouseDirSWE);
        sweCS.SetFloat("mouse_radius", mouseRadius);
        sweCS.SetFloat("mouse_force", mouseForce);
        sweCS.SetInt("mouse_active", mouseActive);

        // --- 処理のディスパッチ ---
        int tgSWE_X = Mathf.CeilToInt(sweGridWidth / 8.0f);
        int tgSWE_Y = Mathf.CeilToInt(sweGridHeight / 8.0f);
        int tgAPIC_X = Mathf.CeilToInt(apicGridWidth / 8.0f);
        int tgAPIC_Y = Mathf.CeilToInt(apicGridHeight / 8.0f);
        int tgAPIC_Z = Mathf.CeilToInt(apicGridDepth / 8.0f);
        int tgParticles = Mathf.CeilToInt(maxParticles / 64.0f);

        coreCS.Dispatch(coreCS.FindKernel("ClearIntermediateBuffers"), tgSWE_X, tgSWE_Y, 1);
        
        int kClearApic = apicCS.FindKernel("ClearAPICGrid");
        apicCS.SetBuffer(kClearApic, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(kClearApic, "APIC_Grid_VelX", apicGridVelXBuffer);
        apicCS.SetBuffer(kClearApic, "APIC_Grid_VelY", apicGridVelYBuffer);
        apicCS.SetBuffer(kClearApic, "APIC_Grid_VelZ", apicGridVelZBuffer);
        apicCS.SetBuffer(kClearApic, "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(kClearApic, "APIC_Pressure_Write", apicPressureBufferWrite);
        apicCS.Dispatch(kClearApic, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        activeParticleListBuffer.SetCounterValue(0); 
        int kBuildList = apicCS.FindKernel("BuildActiveParticleList");
        apicCS.SetBuffer(kBuildList, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.SetBuffer(kBuildList, "ActiveParticleList_Write", activeParticleListBuffer);
        apicCS.SetInt("max_particles", maxParticles);
        apicCS.Dispatch(kBuildList, tgParticles, 1, 1);

        ComputeBuffer.CopyCount(activeParticleListBuffer, activeParticleCountBuffer, 0);
        ComputeBuffer.CopyCount(activeParticleListBuffer, particleDispatchArgsBuffer, 0);
        apicCS.SetBuffer(apicCS.FindKernel("SetupParticleDispatchArgs"), "ParticleDispatchArgs", particleDispatchArgsBuffer);
        apicCS.Dispatch(apicCS.FindKernel("SetupParticleDispatchArgs"), 1, 1, 1);
        
        int advectKernel = apicCS.FindKernel("AdvectParticles");
        apicCS.SetBuffer(advectKernel, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(advectKernel, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(advectKernel, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.DispatchIndirect(advectKernel, particleDispatchArgsBuffer);

        int condenseKernel = apicCS.FindKernel("CondenseParticles");
        apicCS.SetBuffer(condenseKernel, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(condenseKernel, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(condenseKernel, "SWE_State_Read", sweStateBufferRead);
        apicCS.SetBuffer(condenseKernel, "Delta_H_Buffer", deltaHBuffer);
        apicCS.SetBuffer(condenseKernel, "Delta_HU_Buffer", deltaHUBuffer);
        apicCS.SetBuffer(condenseKernel, "Delta_HV_Buffer", deltaHVBuffer);
        apicCS.SetBuffer(condenseKernel, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.SetFloat("horizontal_momentum_transfer", horizontalMomentumTransfer);
        apicCS.SetFloat("vertical_momentum_to_wave", verticalMomentumToWave);
        apicCS.DispatchIndirect(condenseKernel, particleDispatchArgsBuffer);

        int applyKernel = sweCS.FindKernel("ApplyCondensation");
        sweCS.SetBuffer(applyKernel, "SWE_State_Read", sweStateBufferRead);
        sweCS.SetBuffer(applyKernel, "SWE_State_Write", sweStateBufferWrite);
        sweCS.SetBuffer(applyKernel, "Delta_H_Buffer", deltaHBuffer);
        sweCS.SetBuffer(applyKernel, "Delta_HU_Buffer", deltaHUBuffer);
        sweCS.SetBuffer(applyKernel, "Delta_HV_Buffer", deltaHVBuffer);
        sweCS.Dispatch(applyKernel, tgSWE_X, tgSWE_Y, 1);
        SwapSWEBuffers();

        // ★ EvaluateActiveTiles周りの処理を全削除し、直接計算する
        if (mouseActive == 1) {
            int kInteract = sweCS.FindKernel("InteractSWE");
            sweCS.SetBuffer(kInteract, "SWE_State_Read", sweStateBufferRead);
            sweCS.SetBuffer(kInteract, "SWE_State_Write", sweStateBufferWrite);
            sweCS.Dispatch(kInteract, tgSWE_X, tgSWE_Y, 1);
            SwapSWEBuffers();
        }
        
        // ★ UpdateSWE を DispatchIndirect ではなく Dispatch で固定で回す
        int updateSWEKernel = sweCS.FindKernel("UpdateSWE");
        if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
            sweCS.SetTexture(updateSWEKernel, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
            sweCS.SetTexture(updateSWEKernel, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
            sweCS.SetTexture(updateSWEKernel, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
            sweCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
            sweCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
            sweCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
        }
        for (int i = 0; i < subSteps; i++) {
            sweCS.SetBuffer(updateSWEKernel, "SWE_State_Read", sweStateBufferRead);
            sweCS.SetBuffer(updateSWEKernel, "SWE_State_Write", sweStateBufferWrite);
            sweCS.Dispatch(updateSWEKernel, tgSWE_X, tgSWE_Y, 1);
            SwapSWEBuffers();
        }

        int kSublimate = sweCS.FindKernel("SublimateSWEtoAPIC");
        sweCS.SetFloat("fr_threshold", frThreshold);
        sweCS.SetFloat("grad_threshold", gradThreshold);
        sweCS.SetFloat("laplacian_threshold", laplacianThreshold);
        sweCS.SetFloat("conversion_rate_multiplier", conversionRateMultiplier);
        sweCS.SetFloat("push_z_multiplier", pushZMultiplier);
        sweCS.SetFloat("splash_velocity_multiplier", splashVelocityMultiplier);
        sweCS.SetInt("max_particles", maxParticles);
        sweCS.SetBuffer(kSublimate, "SWE_State_Read", sweStateBufferRead);
        sweCS.SetBuffer(kSublimate, "SWE_State_Write", sweStateBufferWrite);
        sweCS.SetBuffer(kSublimate, "APIC_Particle_Buffer", apicParticleBuffer);
        sweCS.SetBuffer(kSublimate, "ParticleCounter", particleCounterBuffer);
        sweCS.Dispatch(kSublimate, tgSWE_X, tgSWE_Y, 1);
        SwapSWEBuffers();

        int p2gKernel = apicCS.FindKernel("P2G_Transfer");
        apicCS.SetBuffer(p2gKernel, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(p2gKernel, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(p2gKernel, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(p2gKernel, "APIC_Grid_VelX", apicGridVelXBuffer);
        apicCS.SetBuffer(p2gKernel, "APIC_Grid_VelY", apicGridVelYBuffer);
        apicCS.SetBuffer(p2gKernel, "APIC_Grid_VelZ", apicGridVelZBuffer);
        apicCS.SetBuffer(p2gKernel, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.DispatchIndirect(p2gKernel, particleDispatchArgsBuffer);

        int normKernel = apicCS.FindKernel("NormalizeGridVelocity");
        apicCS.SetBuffer(normKernel, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(normKernel, "APIC_Grid_VelX", apicGridVelXBuffer);
        apicCS.SetBuffer(normKernel, "APIC_Grid_VelY", apicGridVelYBuffer);
        apicCS.SetBuffer(normKernel, "APIC_Grid_VelZ", apicGridVelZBuffer);
        apicCS.Dispatch(normKernel, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        int divKernel = apicCS.FindKernel("ComputeDivergence");
        apicCS.SetBuffer(divKernel, "APIC_Grid_VelX", apicGridVelXBuffer);
        apicCS.SetBuffer(divKernel, "APIC_Grid_VelY", apicGridVelYBuffer);
        apicCS.SetBuffer(divKernel, "APIC_Grid_VelZ", apicGridVelZBuffer);
        apicCS.SetBuffer(divKernel, "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(divKernel, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(divKernel, "SWE_State_Read", sweStateBufferRead);
        apicCS.Dispatch(divKernel, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // --- PCG ---
        pcgDotResultBuffer.SetData(new int[] { 0 });
        int kBuildDiag = apicCS.FindKernel("BuildDiag");
        apicCS.SetBuffer(kBuildDiag, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(kBuildDiag, "PCG_Precon", pcgPreconBuffer);
        apicCS.Dispatch(kBuildDiag, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        int kInitCG = apicCS.FindKernel("InitCG");
        apicCS.SetBuffer(kInitCG, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(kInitCG, "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(kInitCG, "APIC_Pressure_Write", apicPressureBufferWrite); 
        apicCS.SetBuffer(kInitCG, "PCG_R", pcgRBuffer);
        apicCS.SetBuffer(kInitCG, "PCG_P", pcgPBuffer);
        apicCS.SetBuffer(kInitCG, "PCG_Q", pcgQBuffer);
        apicCS.SetBuffer(kInitCG, "PCG_Precon", pcgPreconBuffer);
        apicCS.Dispatch(kInitCG, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        int kDotPre = apicCS.FindKernel("DotProductPreconditioned");
        apicCS.SetBuffer(kDotPre, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(kDotPre, "PCG_R", pcgRBuffer);
        apicCS.SetBuffer(kDotPre, "PCG_Precon", pcgPreconBuffer);
        apicCS.SetBuffer(kDotPre, "PCG_DotResult", pcgDotResultBuffer);
        apicCS.Dispatch(kDotPre, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetBuffer(apicCS.FindKernel("ComputeInitialRTr"), "PCG_DotResult", pcgDotResultBuffer);
        apicCS.SetBuffer(apicCS.FindKernel("ComputeInitialRTr"), "PCG_Scalars", pcgScalarsBuffer);
        apicCS.Dispatch(apicCS.FindKernel("ComputeInitialRTr"), 1, 1, 1);

        for (int i = 0; i < 8; i++)
        {
            int kApplyA = apicCS.FindKernel("ApplyA");
            apicCS.SetBuffer(kApplyA, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kApplyA, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kApplyA, "PCG_Q", pcgQBuffer);
            apicCS.Dispatch(kApplyA, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            int kDotGen = apicCS.FindKernel("DotProductGeneric");
            apicCS.SetBuffer(kDotGen, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kDotGen, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kDotGen, "PCG_Q", pcgQBuffer);
            apicCS.SetBuffer(kDotGen, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.Dispatch(kDotGen, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(apicCS.FindKernel("CalculateAlpha"), "PCG_DotResult", pcgDotResultBuffer);
            apicCS.SetBuffer(apicCS.FindKernel("CalculateAlpha"), "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(apicCS.FindKernel("CalculateAlpha"), 1, 1, 1);

            int kUpdatePR = apicCS.FindKernel("UpdatePR");
            apicCS.SetBuffer(kUpdatePR, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kUpdatePR, "APIC_Pressure_Write", apicPressureBufferWrite);
            apicCS.SetBuffer(kUpdatePR, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kUpdatePR, "PCG_Q", pcgQBuffer);
            apicCS.SetBuffer(kUpdatePR, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kUpdatePR, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kUpdatePR, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(kDotPre, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kDotPre, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kDotPre, "PCG_Precon", pcgPreconBuffer);
            apicCS.SetBuffer(kDotPre, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.Dispatch(kDotPre, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(apicCS.FindKernel("CalculateBeta"), "PCG_DotResult", pcgDotResultBuffer);
            apicCS.SetBuffer(apicCS.FindKernel("CalculateBeta"), "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(apicCS.FindKernel("CalculateBeta"), 1, 1, 1);

            int kUpdateD = apicCS.FindKernel("UpdateD");
            apicCS.SetBuffer(kUpdateD, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kUpdateD, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kUpdateD, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kUpdateD, "PCG_Precon", pcgPreconBuffer);
            apicCS.SetBuffer(kUpdateD, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kUpdateD, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);
        }

        int g2pKernel = apicCS.FindKernel("G2P_Transfer");
        apicCS.SetBuffer(g2pKernel, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(g2pKernel, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(g2pKernel, "APIC_Grid_VelX", apicGridVelXBuffer);
        apicCS.SetBuffer(g2pKernel, "APIC_Grid_VelY", apicGridVelYBuffer);
        apicCS.SetBuffer(g2pKernel, "APIC_Grid_VelZ", apicGridVelZBuffer);
        apicCS.SetBuffer(g2pKernel, "APIC_Pressure_Write", apicPressureBufferWrite);
        apicCS.SetBuffer(g2pKernel, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.DispatchIndirect(g2pKernel, particleDispatchArgsBuffer);

        // =========================================================
        // --- ボクセル化とメッシュ生成 (Fluid Mesh) ---
        // =========================================================
        int gridX = apicGridWidth * meshResolutionMultiplier;
        int gridY = apicGridDepth * meshResolutionMultiplier; 
        int gridZ = apicGridHeight * meshResolutionMultiplier;
        int tgVoxelX = Mathf.CeilToInt(gridX / 8.0f);
        int tgVoxelY = Mathf.CeilToInt(gridY / 8.0f);
        int tgVoxelZ = Mathf.CeilToInt(gridZ / 8.0f);
        float currentCellSize = dx_apic / meshResolutionMultiplier;

        if (voxelizerCS != null) {
            voxelizerCS.SetVector("apic_world_offset", apicWorldOffset);
            voxelizerCS.SetVector("swe_world_offset", sweWorldOffset);
            voxelizerCS.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
            voxelizerCS.SetFloat("_CellSize", currentCellSize);        
            voxelizerCS.SetFloat("_ParticleRadius", particleRadius);  
            voxelizerCS.SetFloat("_IsoLevel", isoLevel);   
            voxelizerCS.SetFloat("_SplashSpeedThreshold", splashSpeedThreshold);
            voxelizerCS.SetFloat("sea_bottom_z", seaBottomHight);

            int kernelClear = voxelizerCS.FindKernel("ClearGrid");
            voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_MomX", voxelMomXBuffer);
            voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_MomY", voxelMomYBuffer);
            voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_MomZ", voxelMomZBuffer);
            voxelizerCS.Dispatch(kernelClear, tgVoxelX, tgVoxelY, tgVoxelZ);

            int kernelSplat = voxelizerCS.FindKernel("SplatParticles");
            voxelizerCS.SetBuffer(kernelSplat, "ActiveParticleList_Read", activeParticleListBuffer);
            voxelizerCS.SetBuffer(kernelSplat, "ActiveParticleCount", activeParticleCountBuffer);
            voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_MomX", voxelMomXBuffer);
            voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_MomY", voxelMomYBuffer);
            voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_MomZ", voxelMomZBuffer);
            voxelizerCS.SetBuffer(kernelSplat, "ParticleBuffer", apicParticleBuffer);
            voxelizerCS.DispatchIndirect(kernelSplat, particleDispatchArgsBuffer);

            int kernelSplatSWE = voxelizerCS.FindKernel("SplatSWE");
            voxelizerCS.SetBuffer(kernelSplatSWE, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(kernelSplatSWE, "SWE_State_Read", sweStateBufferRead);
            voxelizerCS.SetInts("swe_grid_size", new int[] { sweGridWidth, sweGridHeight });
            voxelizerCS.SetFloat("dx_swe", dx_swe);
            voxelizerCS.Dispatch(kernelSplatSWE, tgVoxelX, tgVoxelY, tgVoxelZ);

            int kernelBlurX = voxelizerCS.FindKernel("BlurX");
            voxelizerCS.SetBuffer(kernelBlurX, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(kernelBlurX, "VoxelGrid_BlurA", voxelBlurABuffer);
            voxelizerCS.Dispatch(kernelBlurX, tgVoxelX, tgVoxelY, tgVoxelZ);

            int kernelBlurY = voxelizerCS.FindKernel("BlurY");
            voxelizerCS.SetBuffer(kernelBlurY, "VoxelGrid_BlurA", voxelBlurABuffer);
            voxelizerCS.SetBuffer(kernelBlurY, "VoxelGrid_BlurB", voxelBlurBBuffer);
            voxelizerCS.Dispatch(kernelBlurY, tgVoxelX, tgVoxelY, tgVoxelZ);

            int kernelBlurZ = voxelizerCS.FindKernel("BlurZ");
            voxelizerCS.SetBuffer(kernelBlurZ, "VoxelGrid_BlurB", voxelBlurBBuffer);
            voxelizerCS.SetBuffer(kernelBlurZ, "VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
            voxelizerCS.Dispatch(kernelBlurZ, tgVoxelX, tgVoxelY, tgVoxelZ);

            int kernelFoam = voxelizerCS.FindKernel("ComputeFoamFactor");
            voxelizerCS.SetBuffer(kernelFoam, "VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
            voxelizerCS.SetBuffer(kernelFoam, "VoxelGrid_FoamFactor", voxelFoamFactorBuffer);
            voxelizerCS.SetFloat("_FoamSampleRadius", foamSampleRadius);
            voxelizerCS.Dispatch(kernelFoam, tgVoxelX, tgVoxelY, tgVoxelZ);

            int kernelMC = voxelizerCS.FindKernel("MarchingCubes");
            triangleBuffer.SetCounterValue(0); 
            voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_Density", voxelGridBuffer);
            voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_MomX", voxelMomXBuffer);
            voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_MomY", voxelMomYBuffer);
            voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_MomZ", voxelMomZBuffer);
            voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
            voxelizerCS.SetBuffer(kernelMC, "TriangleBuffer", triangleBuffer);
            voxelizerCS.SetBuffer(kernelMC, "edgeTable", edgeTableBuffer);
            voxelizerCS.SetBuffer(kernelMC, "triTable", triTableBuffer);
            voxelizerCS.Dispatch(kernelMC, tgVoxelX, tgVoxelY, tgVoxelZ);

            ComputeBuffer.CopyCount(triangleBuffer, drawArgsBuffer, 4);
        }

        // --- レンダリングへの送信 ---
        if (fluidMeshMaterial != null) {
            fluidMeshMaterial.SetBuffer("TriangleBuffer", triangleBuffer);
            if (voxelFoamFactorBuffer != null) {
                fluidMeshMaterial.SetVector("apic_world_offset", apicWorldOffset);
                fluidMeshMaterial.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
                fluidMeshMaterial.SetFloat("_CellSize", currentCellSize);
                fluidMeshMaterial.SetBuffer("VoxelGrid_FoamFactor", voxelFoamFactorBuffer); 
            }
            Graphics.DrawProceduralIndirect(fluidMeshMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, drawArgsBuffer, 0);
        }

        if (splashMaterial != null && apicParticleBuffer != null) {
            splashMaterial.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
            splashMaterial.SetFloat("_CellSize", currentCellSize);
            splashMaterial.SetBuffer("VoxelGrid_FoamFactor", voxelFoamFactorBuffer); 
            splashMaterial.SetFloat("_SpeedThreshold", splashSpeedThreshold);
            splashMaterial.SetBuffer("APIC_Particle_Buffer", apicParticleBuffer);
            splashMaterial.SetVector("apic_world_offset", apicWorldOffset);
            Graphics.DrawProcedural(splashMaterial, new Bounds(Vector3.zero, Vector3.one * 1000), MeshTopology.Triangles, maxParticles * 6, 1);
        }

        if (seaBottomMaterial != null && sweStateBufferRead != null) {
            seaBottomMaterial.SetBuffer("SWE_State_Buffer", sweStateBufferRead);
            seaBottomMaterial.SetFloat("_swe_width", sweGridWidth);
            seaBottomMaterial.SetFloat("_dx_swe", dx_swe);
            seaBottomMaterial.SetVector("_swe_world_offset", sweWorldOffset);
            seaBottomMaterial.SetFloat("_sea_bottom_z", seaBottomHight); 

            if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                seaBottomMaterial.SetTexture("FFT_DispLOD0", fftOcean.displacementMaps[0]);
                seaBottomMaterial.SetTexture("FFT_DispLOD1", fftOcean.displacementMaps[1]);
                seaBottomMaterial.SetTexture("FFT_DispLOD2", fftOcean.displacementMaps[2]);
                seaBottomMaterial.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                seaBottomMaterial.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                seaBottomMaterial.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
            }
        }

        if (fftOceanMaterial != null)
        {
            if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
                fftOceanMaterial.SetTexture("FFT_DispLOD0", fftOcean.displacementMaps[0]);
                fftOceanMaterial.SetTexture("FFT_DispLOD1", fftOcean.displacementMaps[1]);
                fftOceanMaterial.SetTexture("FFT_DispLOD2", fftOcean.displacementMaps[2]);
                fftOceanMaterial.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                fftOceanMaterial.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                fftOceanMaterial.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
                fftOceanMaterial.SetFloat("SeaBottomZ", seaBottomHight);
            }

            if (sweStateBufferRead != null) {
                fftOceanMaterial.SetBuffer("SWE_State_Buffer", sweStateBufferRead);
                fftOceanMaterial.SetFloat("_swe_width", sweGridWidth);
                fftOceanMaterial.SetFloat("_dx_swe", dx_swe);
                fftOceanMaterial.SetVector("_swe_world_offset", sweWorldOffset);
            }

            if (voxelFinalDensityBuffer != null) {
                fftOceanMaterial.SetBuffer("VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
                fftOceanMaterial.SetFloat("_IsoLevel", isoLevel);
            }
        }
    }

    private void SwapSWEBuffers()
    {
        ComputeBuffer temp = sweStateBufferRead;
        sweStateBufferRead = sweStateBufferWrite;
        sweStateBufferWrite = temp;
    }

    void OnDisable()
    {
        sweStateBufferRead?.Release(); sweStateBufferWrite?.Release();
        apicParticleBuffer?.Release(); deltaHBuffer?.Release();
        deltaHUBuffer?.Release(); deltaHVBuffer?.Release();
        apicGridMassBuffer?.Release(); apicGridVelXBuffer?.Release();
        apicGridVelYBuffer?.Release(); apicGridVelZBuffer?.Release();
        apicDivergenceBuffer?.Release(); apicPressureBufferWrite?.Release();
        particleCounterBuffer?.Release(); voxelGridBuffer?.Release();
        voxelMomXBuffer?.Release(); voxelMomYBuffer?.Release();
        voxelMomZBuffer?.Release(); triangleBuffer?.Release();
        drawArgsBuffer?.Release(); triTableBuffer?.Release();
        edgeTableBuffer?.Release(); voxelBlurABuffer?.Release();
        voxelBlurBBuffer?.Release(); voxelFinalDensityBuffer?.Release();
        voxelFoamFactorBuffer?.Release(); pcgRBuffer?.Release();
        pcgPBuffer?.Release(); pcgQBuffer?.Release();
        pcgPreconBuffer?.Release(); pcgDotResultBuffer?.Release();
        pcgScalarsBuffer?.Release(); activeParticleListBuffer?.Release();
        activeParticleCountBuffer?.Release(); particleDispatchArgsBuffer?.Release();
    }
}