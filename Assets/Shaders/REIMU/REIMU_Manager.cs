using UnityEngine;
using System.Runtime.InteropServices;

public class REIMUManager : MonoBehaviour
{
    [Header("Simulation Settings")]
    public int sweGridWidth = 64;
    public int sweGridHeight = 64;
    public float dx_swe = 1.0f;
    private int M_ratio; // 解像度比 (M = dx_apic / dx_swe)
    public int maxParticles = 1000000;

    [Header("APIC Settings")]
    public int apicGridWidth = 64;
    public int apicGridHeight = 64;
    public int apicGridDepth = 64; // z方向(高さ)の解像度
    public float dx_apic = 1.0f;

    [Header("Fluid Settings")]
    public float rho = 1.0f;
    public float baseMass = 0.5f;

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
    public ComputeShader voxelizerCS; // 追加

    [Header("Rendering")]
    public Material sweSurfaceMaterial;
    public Material fluidMeshMaterial; // 追加
    public Material splashMaterial;
    public Material seaBottomMaterial;
    
    public float splashSpeedThreshold = 10.0f;

    [Header("Fluid Mesh (Voxelizer) Settings")]
    public float particleRadius = 1.5f;
    [Range(0.01f, 1.0f)] public float isoLevel = 0.2f; // メッシュ化のしきい値。出ない時はこれを下げます
    [Range(1, 3)] public int meshResolutionMultiplier = 2; // 表示の解像度
    [Range(1.0f, 5.0f)] public float foamSampleRadius = 3.0f;// 球面サンプリングの半径（マス目数）

    // --- Compute Buffers ---
    private ComputeBuffer apicParticleBuffer;
    private ComputeBuffer deltaHBuffer;
    private ComputeBuffer deltaHUBuffer;
    private ComputeBuffer deltaHVBuffer;
    private ComputeBuffer sweStateBufferRead;
    private ComputeBuffer sweStateBufferWrite;
    private ComputeBuffer apicGridMassBuffer;
    private ComputeBuffer apicGridVelXBuffer;
    private ComputeBuffer apicGridVelYBuffer;
    private ComputeBuffer apicGridVelZBuffer;
    private ComputeBuffer apicDivergenceBuffer;
    private ComputeBuffer apicPressureBufferWrite;
    private ComputeBuffer particleCounterBuffer;

    // --- Voxelizer Buffers ---
    private ComputeBuffer voxelGridBuffer;
    private ComputeBuffer voxelMomXBuffer;
    private ComputeBuffer voxelMomYBuffer;
    private ComputeBuffer voxelMomZBuffer;
    private ComputeBuffer triangleBuffer;
    private ComputeBuffer drawArgsBuffer;
    private ComputeBuffer triTableBuffer;
    private ComputeBuffer edgeTableBuffer;
    private ComputeBuffer voxelBlurABuffer;
    private ComputeBuffer voxelBlurBBuffer;
    private ComputeBuffer voxelFinalDensityBuffer;
    private ComputeBuffer voxelFoamFactorBuffer;
    // --- PCG Solver Buffers ---
    private ComputeBuffer pcgRBuffer;
    private ComputeBuffer pcgPBuffer;
    private ComputeBuffer pcgQBuffer;
    private ComputeBuffer pcgPreconBuffer;
    private ComputeBuffer pcgDotResultBuffer;
    private ComputeBuffer pcgScalarsBuffer;
    private ComputeBuffer activeParticleListBuffer;
    private ComputeBuffer activeParticleCountBuffer;
    private ComputeBuffer particleDispatchArgsBuffer;

    public ComputeBuffer GetAPICParticleBuffer() { return apicParticleBuffer; }

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
        for (int y = 0; y < sweGridHeight; y++)
        {
            for (int x = 0; x < sweGridWidth; x++)
            {
                int index = y * sweGridWidth + x;
                float initial_h = 15.0f;
                initialSWE[index] = new SWECell { h = initial_h, hu = 0, hv = 0, padding = 0 };
            }
        }
        sweStateBufferRead.SetData(initialSWE);
        sweStateBufferWrite.SetData(initialSWE);

        // --- テーブルバッファ ---
        edgeTableBuffer = new ComputeBuffer(256, sizeof(int));
        edgeTableBuffer.SetData(MarchingCubesTables.EdgeTable);
        triTableBuffer = new ComputeBuffer(4096, sizeof(int));
        triTableBuffer.SetData(MarchingCubesTables.TriTable);

        // --- Voxelizer バッファ ---
        int gridX = apicGridWidth * meshResolutionMultiplier;
        int gridY = apicGridDepth * meshResolutionMultiplier; 
        int gridZ = apicGridHeight * meshResolutionMultiplier;
        int totalVoxels = gridX * gridY * gridZ;

        voxelGridBuffer = new ComputeBuffer(totalVoxels, sizeof(int));
        voxelMomXBuffer = new ComputeBuffer(totalVoxels, sizeof(int));
        voxelMomYBuffer = new ComputeBuffer(totalVoxels, sizeof(int));
        voxelMomZBuffer = new ComputeBuffer(totalVoxels, sizeof(int));

        int maxTriangles = totalVoxels * 5; 
        triangleBuffer = new ComputeBuffer(maxTriangles, 60, ComputeBufferType.Append);

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

        // AppendBuffer は要素を追加していく特殊なバッファ
        activeParticleListBuffer = new ComputeBuffer(maxParticles, sizeof(uint), ComputeBufferType.Append);
        // ByteAddressBuffer として読み込むための Raw 設定
        activeParticleCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        // IndirectArguments 用のバッファ
        particleDispatchArgsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);

        Debug.Log("REIMU: Buffers Initialized Successfully.");
    }

    private void BindBuffers()
    {
        ComputeShader[] shaders = { coreCS, sweCS, apicCS };
        int[] sweSize = new int[] { sweGridWidth, sweGridHeight };
        int[] apicSize = new int[] { apicGridWidth, apicGridHeight, apicGridDepth };
        
        foreach (var cs in shaders)
        {
            cs.SetInts("swe_grid_size", sweSize);
            cs.SetInts("apic_grid_size", apicSize);
            cs.SetFloat("dx_swe", dx_swe);
            cs.SetFloat("dx_apic", dx_apic);
            cs.SetInt("M_ratio", M_ratio);
            cs.SetFloat("rho", rho);            
            cs.SetFloat("base_mass", baseMass);
        }
        
        sweCS.SetInt("max_particles", maxParticles);

        int clearKernel = coreCS.FindKernel("ClearIntermediateBuffers");
        coreCS.SetBuffer(clearKernel, "Delta_H_Buffer", deltaHBuffer);
        coreCS.SetBuffer(clearKernel, "Delta_HU_Buffer", deltaHUBuffer);
        coreCS.SetBuffer(clearKernel, "Delta_HV_Buffer", deltaHVBuffer);

        int apicAdvectKernel = apicCS.FindKernel("AdvectParticles");
        apicCS.SetBuffer(apicAdvectKernel, "APIC_Particle_Buffer", apicParticleBuffer);

        int sublimateKernel = sweCS.FindKernel("SublimateSWEtoAPIC");
        sweCS.SetBuffer(sublimateKernel, "SWE_State_Read", sweStateBufferRead);
        sweCS.SetBuffer(sublimateKernel, "SWE_State_Write", sweStateBufferWrite);
        sweCS.SetBuffer(sublimateKernel, "APIC_Particle_Buffer", apicParticleBuffer);
        sweCS.SetBuffer(sublimateKernel, "ParticleCounter", particleCounterBuffer);
        
        sweCS.SetInt("max_particles", maxParticles);
        sweCS.SetFloat("fr_threshold", frThreshold);
        sweCS.SetFloat("base_mass", 1.0f);
    }

    void Update()
    {
        if (sweStateBufferRead == null) return;

        // --- 0. タイムステップ ---
        float dt_apic = Mathf.Min(Time.deltaTime, 0.0333f);
        float estimatedMaxWaveSpeed = Mathf.Sqrt(9.81f * 20.0f) + 15.0f; 
        float C_cfl = 0.2f * dx_swe;
        float dt_swe_max = C_cfl * dx_swe / estimatedMaxWaveSpeed;
        
        int subSteps = Mathf.CeilToInt(dt_apic / dt_swe_max);
        float dt_swe = dt_apic / subSteps;

        ComputeShader[] shaders = { coreCS, sweCS, apicCS };
        foreach (var cs in shaders)
        {
            cs.SetInt("M_ratio", M_ratio);
            cs.SetFloat("dx_swe", dx_swe);
            cs.SetFloat("dx_apic", dx_apic);
            cs.SetFloat("rho", rho);
            cs.SetFloat("base_mass", baseMass);
            cs.SetFloat("dt_swe", dt_swe);
            cs.SetFloat("dt_apic", dt_apic);
            cs.SetFloat("fr_threshold", frThreshold);
            cs.SetVector("apic_grid_size", new Vector4(apicGridWidth, apicGridHeight, apicGridDepth, 0));
            cs.SetVector("swe_grid_size", new Vector4(sweGridWidth, sweGridHeight, 0, 0));
        }
        sweCS.SetFloat("grad_threshold", gradThreshold);
        sweCS.SetFloat("laplacian_threshold", laplacianThreshold);
        sweCS.SetFloat("conversion_rate_multiplier", conversionRateMultiplier);
        sweCS.SetFloat("push_z_multiplier", pushZMultiplier);
        sweCS.SetFloat("splash_velocity_multiplier", splashVelocityMultiplier);
        apicCS.SetFloat("horizontal_momentum_transfer", horizontalMomentumTransfer);
        apicCS.SetFloat("vertical_momentum_to_wave", verticalMomentumToWave);

        // =========================================================
        // --- マウス入力 ---
        // =========================================================
        Vector2 mousePosSWE = Vector2.zero;
        Vector2 mouseDirSWE = Vector2.zero;
        int mouseActive = 0;

        if (Input.GetMouseButton(0))
        {
            Plane waterPlane = new Plane(Vector3.up, new Vector3(0, 10.0f, 0));
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

        int tgSWE_X = Mathf.CeilToInt(sweGridWidth / 8.0f);
        int tgSWE_Y = Mathf.CeilToInt(sweGridHeight / 8.0f);
        int tgAPIC_X = Mathf.CeilToInt(apicGridWidth / 8.0f);
        int tgAPIC_Y = Mathf.CeilToInt(apicGridHeight / 8.0f);
        int tgAPIC_Z = Mathf.CeilToInt(apicGridDepth / 8.0f);
        int tgParticles = Mathf.CeilToInt(maxParticles / 64.0f);

        // =========================================================
        // --- APIC/SWE シミュレーション ---
        // =========================================================
        coreCS.Dispatch(coreCS.FindKernel("ClearIntermediateBuffers"), tgSWE_X, tgSWE_Y, 1);
        apicCS.SetBuffer(apicCS.FindKernel("ClearAPICGrid"), "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(apicCS.FindKernel("ClearAPICGrid"), "APIC_Grid_VelX", apicGridVelXBuffer);
        apicCS.SetBuffer(apicCS.FindKernel("ClearAPICGrid"), "APIC_Grid_VelY", apicGridVelYBuffer);
        apicCS.SetBuffer(apicCS.FindKernel("ClearAPICGrid"), "APIC_Grid_VelZ", apicGridVelZBuffer);
        apicCS.SetBuffer(apicCS.FindKernel("ClearAPICGrid"), "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(apicCS.FindKernel("ClearAPICGrid"), "APIC_Pressure_Write", apicPressureBufferWrite);
        apicCS.SetBuffer(apicCS.FindKernel("ClearAPICGrid"), "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.Dispatch(apicCS.FindKernel("ClearAPICGrid"), tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // =========================================================
        // --- 1. アクティブ粒子リストの構築 ---
        // =========================================================
        int kBuildList = apicCS.FindKernel("BuildActiveParticleList");
        int kSetupArgs = apicCS.FindKernel("SetupParticleDispatchArgs");

        // リストを毎回空にする
        activeParticleListBuffer.SetCounterValue(0); 
        apicCS.SetBuffer(kBuildList, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.SetBuffer(kBuildList, "ActiveParticleList_Write", activeParticleListBuffer);
        apicCS.SetInt("max_particles", maxParticles);
        apicCS.Dispatch(kBuildList, tgParticles, 1, 1);

        // 追加された要素数を別バッファにコピー (GetDataでCPUに読み戻さないのが超高速の秘訣！)
        ComputeBuffer.CopyCount(activeParticleListBuffer, activeParticleCountBuffer, 0);
        ComputeBuffer.CopyCount(activeParticleListBuffer, particleDispatchArgsBuffer, 0);

        // 引数バッファをスレッドグループ数に変換
        apicCS.SetBuffer(kSetupArgs, "ParticleDispatchArgs", particleDispatchArgsBuffer);
        apicCS.Dispatch(kSetupArgs, 1, 1, 1);
        
        int advectKernel = apicCS.FindKernel("AdvectParticles");
        apicCS.SetBuffer(advectKernel, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(advectKernel, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.DispatchIndirect(advectKernel, particleDispatchArgsBuffer);

        int condenseKernel = apicCS.FindKernel("CondenseParticles");
        apicCS.SetBuffer(condenseKernel, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(condenseKernel, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(condenseKernel, "SWE_State_Read", sweStateBufferRead);
        apicCS.SetBuffer(condenseKernel, "Delta_H_Buffer", deltaHBuffer);
        apicCS.SetBuffer(condenseKernel, "Delta_HU_Buffer", deltaHUBuffer);
        apicCS.SetBuffer(condenseKernel, "Delta_HV_Buffer", deltaHVBuffer);
        apicCS.SetBuffer(condenseKernel, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.DispatchIndirect(condenseKernel, particleDispatchArgsBuffer);

        int applyKernel = sweCS.FindKernel("ApplyCondensation");
        sweCS.SetBuffer(applyKernel, "SWE_State_Read", sweStateBufferRead);
        sweCS.SetBuffer(applyKernel, "SWE_State_Write", sweStateBufferWrite);
        sweCS.SetBuffer(applyKernel, "Delta_H_Buffer", deltaHBuffer);
        sweCS.SetBuffer(applyKernel, "Delta_HU_Buffer", deltaHUBuffer);
        sweCS.SetBuffer(applyKernel, "Delta_HV_Buffer", deltaHVBuffer);
        sweCS.Dispatch(applyKernel, tgSWE_X, tgSWE_Y, 1);
        SwapSWEBuffers();

        if (mouseActive == 1)
        {
            int interactKernel = sweCS.FindKernel("InteractSWE");
            sweCS.SetBuffer(interactKernel, "SWE_State_Read", sweStateBufferRead);
            sweCS.SetBuffer(interactKernel, "SWE_State_Write", sweStateBufferWrite);
            sweCS.Dispatch(interactKernel, tgSWE_X, tgSWE_Y, 1);
            SwapSWEBuffers();
        }

        int updateSWEKernel = sweCS.FindKernel("UpdateSWE");
        for (int i = 0; i < subSteps; i++)
        {
            sweCS.SetBuffer(updateSWEKernel, "SWE_State_Read", sweStateBufferRead);
            sweCS.SetBuffer(updateSWEKernel, "SWE_State_Write", sweStateBufferWrite);
            sweCS.Dispatch(updateSWEKernel, tgSWE_X, tgSWE_Y, 1);
            SwapSWEBuffers();
        }

        int sublimateKernel = sweCS.FindKernel("SublimateSWEtoAPIC");
        sweCS.SetBuffer(sublimateKernel, "SWE_State_Read", sweStateBufferRead);
        sweCS.SetBuffer(sublimateKernel, "SWE_State_Write", sweStateBufferWrite);
        sweCS.SetBuffer(sublimateKernel, "APIC_Particle_Buffer", apicParticleBuffer);
        sweCS.SetBuffer(sublimateKernel, "ParticleCounter", particleCounterBuffer);
        sweCS.Dispatch(sublimateKernel, tgSWE_X, tgSWE_Y, 1);
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

        // =========================================================
        // --- Jacobi-PCG 圧力ソルバ ---
        // =========================================================
        int kBuildDiag = apicCS.FindKernel("BuildDiag");
        int kInitCG = apicCS.FindKernel("InitCG");
        int kDotPre = apicCS.FindKernel("DotProductPreconditioned");
        int kInitRTr = apicCS.FindKernel("ComputeInitialRTr");
        int kApplyA = apicCS.FindKernel("ApplyA");
        int kDotGen = apicCS.FindKernel("DotProductGeneric");
        int kCalcAlpha = apicCS.FindKernel("CalculateAlpha");
        int kUpdatePR = apicCS.FindKernel("UpdatePR");
        int kCalcBeta = apicCS.FindKernel("CalculateBeta");
        int kUpdateD = apicCS.FindKernel("UpdateD");

        // 内積用バッファの初期化(初回確実化)
        pcgDotResultBuffer.SetData(new int[] { 0 });

        // 0. 前処理行列の構築
        apicCS.SetBuffer(kBuildDiag, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(kBuildDiag, "PCG_Precon", pcgPreconBuffer);
        apicCS.Dispatch(kBuildDiag, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // 1. 初期化
        apicCS.SetBuffer(kInitCG, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(kInitCG, "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(kInitCG, "APIC_Pressure_Write", apicPressureBufferWrite); 
        apicCS.SetBuffer(kInitCG, "PCG_R", pcgRBuffer);
        apicCS.SetBuffer(kInitCG, "PCG_P", pcgPBuffer);
        apicCS.SetBuffer(kInitCG, "PCG_Q", pcgQBuffer);
        apicCS.SetBuffer(kInitCG, "PCG_Precon", pcgPreconBuffer);
        apicCS.Dispatch(kInitCG, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // 2. 初期 rTr の計算
        apicCS.SetBuffer(kDotPre, "APIC_Grid_Mass", apicGridMassBuffer);
        apicCS.SetBuffer(kDotPre, "PCG_R", pcgRBuffer);
        apicCS.SetBuffer(kDotPre, "PCG_Precon", pcgPreconBuffer);
        apicCS.SetBuffer(kDotPre, "PCG_DotResult", pcgDotResultBuffer);
        apicCS.Dispatch(kDotPre, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetBuffer(kInitRTr, "PCG_DotResult", pcgDotResultBuffer);
        apicCS.SetBuffer(kInitRTr, "PCG_Scalars", pcgScalarsBuffer);
        apicCS.Dispatch(kInitRTr, 1, 1, 1);

        // PCG反復ループ
        int pcgIterations = 8; 
        for (int i = 0; i < pcgIterations; i++)
        {
            // 3. Q = A * P
            apicCS.SetBuffer(kApplyA, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kApplyA, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kApplyA, "PCG_Q", pcgQBuffer);
            apicCS.Dispatch(kApplyA, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            // 4. Alpha の計算 (内積 P * Q)
            apicCS.SetBuffer(kDotGen, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kDotGen, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kDotGen, "PCG_Q", pcgQBuffer);
            apicCS.SetBuffer(kDotGen, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.Dispatch(kDotGen, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(kCalcAlpha, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.SetBuffer(kCalcAlpha, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kCalcAlpha, 1, 1, 1);

            // 5. 圧力と残差(R)の更新
            apicCS.SetBuffer(kUpdatePR, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kUpdatePR, "APIC_Pressure_Write", apicPressureBufferWrite);
            apicCS.SetBuffer(kUpdatePR, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kUpdatePR, "PCG_Q", pcgQBuffer);
            apicCS.SetBuffer(kUpdatePR, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kUpdatePR, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kUpdatePR, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            // 6. 新しい残差での rTrNew 計算と Beta の計算
            apicCS.SetBuffer(kDotPre, "APIC_Grid_Mass", apicGridMassBuffer);
            apicCS.SetBuffer(kDotPre, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kDotPre, "PCG_Precon", pcgPreconBuffer);
            apicCS.SetBuffer(kDotPre, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.Dispatch(kDotPre, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(kCalcBeta, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.SetBuffer(kCalcBeta, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kCalcBeta, 1, 1, 1);

            // 7. 探索方向(P)の更新
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

        // SetIntsのバグを回避し、SetVectorで安全に確実に送信
        voxelizerCS.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
        voxelizerCS.SetFloat("_CellSize", currentCellSize);        // ← currentCellSize を渡すように修正
        voxelizerCS.SetFloat("_ParticleRadius", particleRadius);  
        voxelizerCS.SetFloat("_IsoLevel", isoLevel);   
        voxelizerCS.SetFloat("_SplashSpeedThreshold", splashSpeedThreshold);

        // 1. ボクセルグリッドのクリア
        // --- 1. ボクセルグリッドのクリア ---
        int kernelClear = voxelizerCS.FindKernel("ClearGrid");
        voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_Density", voxelGridBuffer);
        voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_MomX", voxelMomXBuffer);
        voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_MomY", voxelMomYBuffer);
        voxelizerCS.SetBuffer(kernelClear, "VoxelGrid_MomZ", voxelMomZBuffer);
        voxelizerCS.Dispatch(kernelClear, tgVoxelX, tgVoxelY, tgVoxelZ);

        // --- 2. 粒子のスプラッティング ---
        int kernelSplat = voxelizerCS.FindKernel("SplatParticles");
        voxelizerCS.SetBuffer(kernelSplat, "ActiveParticleList_Read", activeParticleListBuffer);
        voxelizerCS.SetBuffer(kernelSplat, "ActiveParticleCount", activeParticleCountBuffer);
        voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_Density", voxelGridBuffer);
        voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_MomX", voxelMomXBuffer);
        voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_MomY", voxelMomYBuffer);
        voxelizerCS.SetBuffer(kernelSplat, "VoxelGrid_MomZ", voxelMomZBuffer);
        voxelizerCS.SetBuffer(kernelSplat, "ParticleBuffer", apicParticleBuffer);
        voxelizerCS.DispatchIndirect(kernelSplat, particleDispatchArgsBuffer);

        // --- 3. SWE海面密度のスプラッティング (新規) ---
        int kernelSplatSWE = voxelizerCS.FindKernel("SplatSWE");
        voxelizerCS.SetBuffer(kernelSplatSWE, "VoxelGrid_Density", voxelGridBuffer);
        voxelizerCS.SetBuffer(kernelSplatSWE, "SWE_State_Read", sweStateBufferRead);
        voxelizerCS.SetInts("swe_grid_size", new int[] { sweGridWidth, sweGridHeight });
        voxelizerCS.SetFloat("dx_swe", dx_swe);
        voxelizerCS.Dispatch(kernelSplatSWE, tgVoxelX, tgVoxelY, tgVoxelZ);

        // --- 4. 分離畳み込みブラー (新規 O(N)) ---
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

        // --- 4.5 球面サンプリングによるFoam計算 (新規) ---
        int kernelFoam = voxelizerCS.FindKernel("ComputeFoamFactor");
        voxelizerCS.SetBuffer(kernelFoam, "VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
        voxelizerCS.SetBuffer(kernelFoam, "VoxelGrid_FoamFactor", voxelFoamFactorBuffer);
        voxelizerCS.SetFloat("_FoamSampleRadius", foamSampleRadius);
        voxelizerCS.Dispatch(kernelFoam, tgVoxelX, tgVoxelY, tgVoxelZ);

        // --- 5. マーチングキューブ ---
        int kernelMC = voxelizerCS.FindKernel("MarchingCubes");
        triangleBuffer.SetCounterValue(0); 
        voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_Density", voxelGridBuffer); // 速度計算用
        voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_MomX", voxelMomXBuffer);
        voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_MomY", voxelMomYBuffer);
        voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_MomZ", voxelMomZBuffer);
        voxelizerCS.SetBuffer(kernelMC, "VoxelGrid_FinalDensity", voxelFinalDensityBuffer); // 形状用
        voxelizerCS.SetBuffer(kernelMC, "TriangleBuffer", triangleBuffer);
        voxelizerCS.SetBuffer(kernelMC, "edgeTable", edgeTableBuffer);
        voxelizerCS.SetBuffer(kernelMC, "triTable", triTableBuffer);
        voxelizerCS.Dispatch(kernelMC, tgVoxelX, tgVoxelY, tgVoxelZ);

        // 4. 描画命令のセット
        ComputeBuffer.CopyCount(triangleBuffer, drawArgsBuffer, 4);

        // --- SWE への送信 ---
        if (sweStateBufferRead != null && sweSurfaceMaterial != null)
        {
            sweSurfaceMaterial.SetBuffer("SWE_State_Buffer", sweStateBufferRead);
            sweSurfaceMaterial.SetFloat("_swe_width", sweGridWidth);
            sweSurfaceMaterial.SetFloat("_dx_swe", dx_swe);
            if (voxelFinalDensityBuffer != null) {
                sweSurfaceMaterial.SetBuffer("VoxelGrid_FinalDensity", voxelFinalDensityBuffer);
                sweSurfaceMaterial.SetFloat("_IsoLevel", isoLevel);
            }
            
            int numTriangles = (sweGridWidth - 1) * (sweGridHeight - 1) * 2;
            Bounds bounds = new Bounds(Vector3.zero, new Vector3(1000, 1000, 1000));
            Graphics.DrawProcedural(sweSurfaceMaterial, bounds, MeshTopology.Triangles, numTriangles * 3, 1);
        }
        
        // --- FluidMesh への送信 ---
        if (fluidMeshMaterial != null)
        {
            fluidMeshMaterial.SetBuffer("TriangleBuffer", triangleBuffer);
            if (voxelFoamFactorBuffer != null) // 【変更】FinalDensity から FoamFactor に変更
            {
                fluidMeshMaterial.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
                fluidMeshMaterial.SetFloat("_CellSize", currentCellSize);
                fluidMeshMaterial.SetBuffer("VoxelGrid_FoamFactor", voxelFoamFactorBuffer); 
            }
            Bounds bounds = new Bounds(Vector3.zero, new Vector3(1000, 1000, 1000));
            Graphics.DrawProceduralIndirect(fluidMeshMaterial, bounds, MeshTopology.Triangles, drawArgsBuffer, 0);
        }

        // --- Splash への送信 ---
        if (splashMaterial != null && apicParticleBuffer != null)
        {
            splashMaterial.SetVector("_GridSize", new Vector4(gridX, gridY, gridZ, 0));
            splashMaterial.SetFloat("_CellSize", currentCellSize);
            splashMaterial.SetBuffer("VoxelGrid_FoamFactor", voxelFoamFactorBuffer); // 【変更】
            splashMaterial.SetFloat("_SpeedThreshold", splashSpeedThreshold);
            splashMaterial.SetBuffer("APIC_Particle_Buffer", apicParticleBuffer);
            Bounds bounds = new Bounds(Vector3.zero, new Vector3(1000, 1000, 1000));
            Graphics.DrawProcedural(splashMaterial, bounds, MeshTopology.Triangles, maxParticles * 6, 1);
        }

        // --- seaBottom への送信 ---
        if (seaBottomMaterial != null && sweStateBufferRead != null)
        {
            seaBottomMaterial.SetBuffer("SWE_State_Buffer", sweStateBufferRead);
            seaBottomMaterial.SetFloat("_swe_width", sweGridWidth);
            seaBottomMaterial.SetFloat("_dx_swe", dx_swe);
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
        sweStateBufferRead?.Release();
        sweStateBufferWrite?.Release();
        apicParticleBuffer?.Release();
        deltaHBuffer?.Release();
        deltaHUBuffer?.Release();
        deltaHVBuffer?.Release();
        apicGridMassBuffer?.Release();
        apicGridVelXBuffer?.Release();
        apicGridVelYBuffer?.Release();
        apicGridVelZBuffer?.Release();
        apicDivergenceBuffer?.Release();
        apicPressureBufferWrite?.Release();
        particleCounterBuffer?.Release();
        // Voxelizer用
        voxelGridBuffer?.Release();
        voxelMomXBuffer?.Release();
        voxelMomYBuffer?.Release();
        voxelMomZBuffer?.Release();
        triangleBuffer?.Release();
        drawArgsBuffer?.Release();
        triTableBuffer?.Release();
        edgeTableBuffer?.Release();
        voxelBlurABuffer?.Release();
        voxelBlurBBuffer?.Release();
        voxelFinalDensityBuffer?.Release();
        voxelFoamFactorBuffer?.Release();
        // PCG用
        pcgRBuffer?.Release();
        pcgPBuffer?.Release();
        pcgQBuffer?.Release();
        pcgPreconBuffer?.Release();
        pcgDotResultBuffer?.Release();
        pcgScalarsBuffer?.Release();

        activeParticleListBuffer?.Release();
        activeParticleCountBuffer?.Release();
        particleDispatchArgsBuffer?.Release();
    }
}