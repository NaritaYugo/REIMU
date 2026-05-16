using UnityEngine;
using System.Runtime.InteropServices;
using FluidSimulation;

public partial class SimulationManager : MonoBehaviour
{
    [Header("Simulation Size")]
    [SerializeField] private Vector3Int m_DomainLength = new(64, 64, 64);
    [SerializeField] private int m_SweCellsPerMeter = 2;
    [SerializeField] private int m_ApicCellsPerMeter = 1;
    [SerializeField] private int maxParticles = 1000000;
    [SerializeField] private int pcgIterations = 8;

    [Header("Fluid Settings")]
    public float baseMass = 0.5f;
    public float seaBottomHeight = -3f; //浅水近似のための仮の水深

    [Header("Sublimation & Condensation")]
    [Tooltip("APIC化(昇華)が発生するフルード数(流れの慣性力と重力の比)の閾値")]
    public float frThreshold = 1.0f; 
    [Tooltip("APIC化が発生する水面の勾配(波の急峻さ)の閾値")]
    public float gradThreshold = 1.5f; 
    [Tooltip("APIC化が発生する水面のラプラシアン(波の尖り具合)の閾値。負の値")]
    public float laplacianThreshold = -2.5f; 
    
    [Tooltip("昇華条件を満たした際、1秒間にSWEの何割をAPIC粒子に変換するかの減衰係数。大きいほど一瞬で水柱が粒子化する")]
    public float conversionRateMultiplier = 20.0f;
    [Tooltip("昇華時にAPIC粒子へ与えられる鉛直方向(Z軸)の初速度の倍率")]
    public float pushZMultiplier = 1.0f; 
    [Tooltip("昇華時に追加されるランダムな飛沫の初速度の倍率")]
    public float splashVelocityMultiplier = 1.0f;
    
    [Range(0f, 1f), Tooltip("凝縮時(落下時)の衝撃を、SWEの水平方向の波紋にどれくらい変換するか")]
    public float horizontalMomentumTransfer = 0.3f;
    [Range(0f, 1f), Tooltip("凝縮時の鉛直方向の運動量をどれくらい波のエネルギーとして扱うか")]
    public float verticalMomentumToWave = 0.1f;

    [Header("Mouse Interaction")]
    public float mouseRadius = 3.0f;
    public float mouseForce = 3.0f;
    private Vector3 prevMousePos;
    private bool wasMouseDown = false;

    [Header("Compute Shaders")]
    public ComputeShader sweCS;
    public ComputeShader apicCS;

    [Header("Tracking")]
    public FFTManager fftOcean;
    public Transform trackTarget;
    public Vector2 sweWorldOffset = Vector2.zero; 
    public Vector3 apicWorldOffset = Vector3.zero;

    private float dxSwe, dxApic;
    private Vector2Int sweGridRes;
    private Vector3Int apicGridRes;
    private int M_ratio;

    [Header("Compute Buffers (APIC/SWE用)")]
    // アトミック加算の競合によるGPUフリーズを防ぐため、
    // C#側でuintとしてバッファを確保し、HLSL側で固定小数点ハック(InterlockedAdd)を使用する
    private ComputeBuffer apicParticleBuffer, deltaHBuffer, deltaHUBuffer, deltaHVBuffer;
    private ComputeBuffer sweStateBufferRead, sweStateBufferWrite;
    private ComputeBuffer apicGridMassBuffer, apicGridVelXBuffer, apicGridVelYBuffer, apicGridVelZBuffer;
    private ComputeBuffer apicDivergenceBuffer, apicPressureBufferWrite, particleCounterBuffer;
    
    // 圧力計算(Jacobi-PCG法)用のバッファ
    private ComputeBuffer pcgRBuffer, pcgPBuffer, pcgQBuffer, pcgPreconBuffer, pcgDotResultBuffer, pcgScalarsBuffer;
    
    // 計算負荷削減のための、生存粒子リストと間接ディスパッチ(DispatchIndirect)用バッファ
    private ComputeBuffer activeParticleListBuffer, activeParticleCountBuffer, particleDispatchArgsBuffer;

    // --- カーネルIDのキャッシュ ---
    private int kernelSweClear;
    private int kernelSweShift, kernelSweInit, kernelSweApply, kernelSweInteract, kernelSweUpdate, kernelSweSublimate;
    private int kernelApicClear, kernelApicBuildList, kernelApicSetupArgs, kernelApicAdvect, kernelApicCondense;
    private int kernelApicP2G, kernelApicNormVel, kernelApicDiv, kernelApicBuildDiag, kernelApicInitCG;
    private int kernelApicDotPre, kernelApicInitRTr, kernelApicApplyA, kernelApicDotGen, kernelApicCalcAlpha;
    private int kernelApicUpdatePR, kernelApicCalcBeta, kernelApicUpdateD, kernelApicG2P;

    void Start()
    {
        dxSwe = 1/(float)m_SweCellsPerMeter;
        dxApic = 1/(float)m_ApicCellsPerMeter;
        sweGridRes = new Vector2Int(m_DomainLength.x, m_DomainLength.z) * m_SweCellsPerMeter;
        apicGridRes = m_DomainLength * m_ApicCellsPerMeter;

        if (trackTarget != null)
        {
            float targetBaseX = trackTarget.position.x - sweGridRes.x * dxSwe * 0.5f;
            float targetBaseZ = trackTarget.position.z - sweGridRes.y * dxSwe * 0.5f;
            sweWorldOffset = new Vector2(targetBaseX, targetBaseZ);
            apicWorldOffset = new Vector3(sweWorldOffset.x, sweWorldOffset.y, seaBottomHeight);
        }

        M_ratio = (int)(dxApic / dxSwe);
        
        CacheKernels();
        InitializeBuffers();
        InitializeTerrain();
        InitializeVoxelizer();
        BindBuffers();
    }

    private void CacheKernels()
    {
        kernelSweClear = sweCS.FindKernel("ClearIntermediateBuffers");
        kernelSweShift = sweCS.FindKernel("ShiftSWEGrid");
        kernelSweInit = sweCS.FindKernel("InitSWE");
        kernelSweApply = sweCS.FindKernel("ApplyCondensation");
        kernelSweInteract = sweCS.FindKernel("InteractSWE");
        kernelSweUpdate = sweCS.FindKernel("UpdateSWE");
        kernelSweSublimate = sweCS.FindKernel("SublimateSWEtoAPIC");

        kernelApicClear = apicCS.FindKernel("ClearAPICGrid");
        kernelApicBuildList = apicCS.FindKernel("BuildActiveParticleList");
        kernelApicSetupArgs = apicCS.FindKernel("SetupParticleDispatchArgs");
        kernelApicAdvect = apicCS.FindKernel("AdvectParticles");
        kernelApicCondense = apicCS.FindKernel("CondenseParticles");
        kernelApicP2G = apicCS.FindKernel("P2G_Transfer");
        kernelApicNormVel = apicCS.FindKernel("NormalizeGridVelocity");
        kernelApicDiv = apicCS.FindKernel("ComputeDivergence");
        kernelApicBuildDiag = apicCS.FindKernel("BuildDiag");
        kernelApicInitCG = apicCS.FindKernel("InitCG");
        kernelApicDotPre = apicCS.FindKernel("DotProductPreconditioned");
        kernelApicInitRTr = apicCS.FindKernel("ComputeInitialRTr");
        kernelApicApplyA = apicCS.FindKernel("ApplyA");
        kernelApicDotGen = apicCS.FindKernel("DotProductGeneric");
        kernelApicCalcAlpha = apicCS.FindKernel("CalculateAlpha");
        kernelApicUpdatePR = apicCS.FindKernel("UpdatePR");
        kernelApicCalcBeta = apicCS.FindKernel("CalculateBeta");
        kernelApicUpdateD = apicCS.FindKernel("UpdateD");
        kernelApicG2P = apicCS.FindKernel("G2P_Transfer");
    }

    private void InitializeBuffers()
    {
        int sweTotalCells = sweGridRes.x * sweGridRes.y;
        sweStateBufferRead = new ComputeBuffer(sweTotalCells, Marshal.SizeOf(typeof(SWECell)));
        sweStateBufferWrite = new ComputeBuffer(sweTotalCells, Marshal.SizeOf(typeof(SWECell)));
        apicParticleBuffer = new ComputeBuffer(maxParticles, Marshal.SizeOf(typeof(APICParticle)), ComputeBufferType.Default);
        deltaHBuffer = new ComputeBuffer(sweTotalCells, sizeof(uint));
        deltaHUBuffer = new ComputeBuffer(sweTotalCells, sizeof(uint));
        deltaHVBuffer = new ComputeBuffer(sweTotalCells, sizeof(uint));

        int apicTotalCells = apicGridRes.x * apicGridRes.y * apicGridRes.z;
        apicGridMassBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicGridVelXBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicGridVelYBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicGridVelZBuffer = new ComputeBuffer(apicTotalCells, sizeof(uint));
        apicDivergenceBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        apicPressureBufferWrite = new ComputeBuffer(apicTotalCells, sizeof(float));
        particleCounterBuffer = new ComputeBuffer(1, sizeof(uint));
        particleCounterBuffer.SetData(new uint[] { 0 });

        APICParticle[] emptyParticles = new APICParticle[maxParticles];
        apicParticleBuffer.SetData(emptyParticles);
        
        SWECell[] initialSWE = new SWECell[sweTotalCells];
        for (int i = 0; i < sweTotalCells; i++)
        {
            initialSWE[i] = new SWECell { h = 0f, hu = 0f, hv = 0f, padding = 0f };
        }
        sweStateBufferRead.SetData(initialSWE);
        sweStateBufferWrite.SetData(initialSWE);

        pcgRBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgPBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgQBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgPreconBuffer = new ComputeBuffer(apicTotalCells, sizeof(float));
        pcgDotResultBuffer = new ComputeBuffer(1, sizeof(uint));
        pcgScalarsBuffer = new ComputeBuffer(5, sizeof(float));

        activeParticleListBuffer = new ComputeBuffer(maxParticles, sizeof(uint), ComputeBufferType.Append);
        activeParticleCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        particleDispatchArgsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
    }

    private void BindBuffers()
    {
        ComputeShader[] shaders = { sweCS, apicCS };
        foreach (var cs in shaders)
        {
            cs.SetInts("_SweGridRes", new int[] { sweGridRes.x, sweGridRes.y });
            cs.SetInts("_ApicGridRes", new int[] { apicGridRes.x, apicGridRes.z, apicGridRes.y });
            cs.SetFloat("_dxSwe", dxSwe);
            cs.SetFloat("_dxApic", dxApic);
            cs.SetInt("M_ratio", M_ratio);  
            cs.SetFloat("base_mass", baseMass);
            cs.SetFloat("sea_bottom_z", seaBottomHeight);
        }
        
        sweCS.SetBuffer(kernelSweClear, "Delta_H_Buffer", deltaHBuffer);
        sweCS.SetBuffer(kernelSweClear, "Delta_HU_Buffer", deltaHUBuffer);
        sweCS.SetBuffer(kernelSweClear, "Delta_HV_Buffer", deltaHVBuffer);
    }

    void Update()
    {
        if (sweStateBufferRead == null || trackTarget == null) return;

        // --- 動的タイムステップ(CFL条件)の計算 ---
        float dtApic = Mathf.Min(Time.deltaTime, 0.0333f);
        
        // サブサイクリング回数を決定するためのヒューリスティックな予測値
        float expected_max_depth = 50.0f;
        float expected_wave_speed = Mathf.Sqrt(9.81f * expected_max_depth);
        float expected_max_velocity = 15.0f; 
        
        // 波の速度がセルを飛び越えない安全なSWEの最大タイムステップを算出
        float dtSwe_max = 0.20f * dxSwe / (expected_wave_speed + expected_max_velocity); 
        int subSteps = Mathf.CeilToInt(dtApic / dtSwe_max);
        float dtSwe = dtApic / subSteps;

        // 追従・地形処理は別ファイルに分割したメソッドを呼ぶ
        UpdateTrackingAndTerrain();

        ComputeShader[] shaders = { sweCS, apicCS };
        foreach (var cs in shaders)
        {
            cs.SetFloat("_dtSwe", dtSwe);
            cs.SetFloat("_dtApic", dtApic);
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
                    mouseDirSWE = new Vector2(delta.x, delta.z) / dtApic;
                    if (mouseDirSWE.sqrMagnitude > 0.01f) mouseActive = 1;
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

        // --- スレッドグループ数の計算 ---
        int tgSWE_X = (sweGridRes.x + 7) / 8;
        int tgSWE_Y = (sweGridRes.y + 7) / 8;
        int tgAPIC_X = (apicGridRes.x + 7) / 8;
        int tgAPIC_Y = (apicGridRes.z + 7) / 8;  
        int tgAPIC_Z = (apicGridRes.y + 7) / 8; 
        int tgParticles = (maxParticles + 63) / 64;

        if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
            int[] apicFFTKernels = { kernelApicBuildList, kernelApicCondense, kernelApicDiv, kernelApicBuildDiag };
            foreach (var k in apicFFTKernels) {
                apicCS.SetTexture(k, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
                apicCS.SetTexture(k, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
                apicCS.SetTexture(k, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
                apicCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
                apicCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
                apicCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
            }
        }

        // =========================================================
        // Step 1: バッファの初期化・クリア
        // =========================================================
        sweCS.Dispatch(kernelSweClear, tgSWE_X, tgSWE_Y, 1);
        
        apicCS.SetBuffer(kernelApicClear, "_ApicMassInt", apicGridMassBuffer);
        apicCS.SetBuffer(kernelApicClear, "_ApicVelIntX", apicGridVelXBuffer);
        apicCS.SetBuffer(kernelApicClear, "_ApicVelIntY", apicGridVelYBuffer);
        apicCS.SetBuffer(kernelApicClear, "_ApicVelIntZ", apicGridVelZBuffer);
        apicCS.SetBuffer(kernelApicClear, "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(kernelApicClear, "APIC_Pressure_Write", apicPressureBufferWrite);
        apicCS.Dispatch(kernelApicClear, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // =========================================================
        // Step 2: 生存粒子のリスト構築と間接ディスパッチ(DispatchIndirect)の準備
        // 無駄な計算を省くため、生きているAPIC粒子だけを抽出する
        // =========================================================
        activeParticleListBuffer.SetCounterValue(0); 
        apicCS.SetBuffer(kernelApicBuildList, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.SetBuffer(kernelApicBuildList, "ActiveParticleList_Write", activeParticleListBuffer);
        apicCS.SetInt("max_particles", maxParticles);
        apicCS.Dispatch(kernelApicBuildList, tgParticles, 1, 1);

        ComputeBuffer.CopyCount(activeParticleListBuffer, activeParticleCountBuffer, 0);
        ComputeBuffer.CopyCount(activeParticleListBuffer, particleDispatchArgsBuffer, 0);
        apicCS.SetBuffer(kernelApicSetupArgs, "ParticleDispatchArgs", particleDispatchArgsBuffer);
        apicCS.Dispatch(kernelApicSetupArgs, 1, 1, 1);
        
        // =========================================================
        // Step 3: APIC粒子の移流 (Advect)
        // 粒子を速度に従って移動させ、地形等との衝突判定を行う
        // =========================================================
        apicCS.SetBuffer(kernelApicAdvect, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(kernelApicAdvect, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(kernelApicAdvect, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.SetTexture(kernelApicAdvect, "TerrainHeightMap", terrainHeightMap);
        apicCS.DispatchIndirect(kernelApicAdvect, particleDispatchArgsBuffer);

        // =========================================================
        // Step 4: APIC → SWE 凝縮 (Condensation)
        // 水面に落下したAPIC粒子を消滅させ、運動量をSWEの中間バッファに書き込む
        // =========================================================
        apicCS.SetTexture(kernelApicCondense, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(kernelApicCondense, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(kernelApicCondense, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(kernelApicCondense, "SWE_State_Read", sweStateBufferRead);
        apicCS.SetBuffer(kernelApicCondense, "Delta_H_Buffer", deltaHBuffer);
        apicCS.SetBuffer(kernelApicCondense, "Delta_HU_Buffer", deltaHUBuffer);
        apicCS.SetBuffer(kernelApicCondense, "Delta_HV_Buffer", deltaHVBuffer);
        apicCS.SetBuffer(kernelApicCondense, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.SetFloat("horizontal_momentum_transfer", horizontalMomentumTransfer);
        apicCS.SetFloat("vertical_momentum_to_wave", verticalMomentumToWave);
        apicCS.DispatchIndirect(kernelApicCondense, particleDispatchArgsBuffer);

        // =========================================================
        // Step 5: SWE 凝縮・マウスインタラクションの適用
        // 中間バッファに溜まった凝縮の衝撃やマウスの力をSWE本体のステートに反映する
        // =========================================================
        sweCS.SetBuffer(kernelSweApply, "SWE_State_Read", sweStateBufferRead);
        sweCS.SetBuffer(kernelSweApply, "SWE_State_Write", sweStateBufferWrite);
        sweCS.SetBuffer(kernelSweApply, "Delta_H_Buffer", deltaHBuffer);
        sweCS.SetBuffer(kernelSweApply, "Delta_HU_Buffer", deltaHUBuffer);
        sweCS.SetBuffer(kernelSweApply, "Delta_HV_Buffer", deltaHVBuffer);
        sweCS.Dispatch(kernelSweApply, tgSWE_X, tgSWE_Y, 1);
        SwapSWEBuffers();

        if (mouseActive == 1) {
            sweCS.SetBuffer(kernelSweInteract, "SWE_State_Read", sweStateBufferRead);
            sweCS.SetBuffer(kernelSweInteract, "SWE_State_Write", sweStateBufferWrite);
            sweCS.Dispatch(kernelSweInteract, tgSWE_X, tgSWE_Y, 1);
            SwapSWEBuffers();
        }

        // =========================================================
        // Step 6: SWE サブサイクリング更新
        // 高速な波の伝播を安定させるため、細かいタイムステップで複数回更新する
        // =========================================================
        sweCS.SetTexture(kernelSweUpdate, "TerrainHeightMap", terrainHeightMap);
        sweCS.SetTexture(kernelSweSublimate, "TerrainHeightMap", terrainHeightMap);
        
        if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
            sweCS.SetTexture(kernelSweUpdate, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
            sweCS.SetTexture(kernelSweUpdate, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
            sweCS.SetTexture(kernelSweUpdate, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
            sweCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
            sweCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
            sweCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
        }
        for (int i = 0; i < subSteps; i++) {
            sweCS.SetBuffer(kernelSweUpdate, "SWE_State_Read", sweStateBufferRead);
            sweCS.SetBuffer(kernelSweUpdate, "SWE_State_Write", sweStateBufferWrite);
            sweCS.Dispatch(kernelSweUpdate, tgSWE_X, tgSWE_Y, 1);
            SwapSWEBuffers();
        }

        // =========================================================
        // Step 7: SWE → APIC 昇華 (Sublimation)
        // 波が激しくなった領域のSWEの体積を削り、APIC粒子をスポーンさせる
        // =========================================================
        sweCS.SetFloat("fr_threshold", frThreshold);
        sweCS.SetFloat("grad_threshold", gradThreshold);
        sweCS.SetFloat("laplacian_threshold", laplacianThreshold);
        sweCS.SetFloat("conversion_rate_multiplier", conversionRateMultiplier);
        sweCS.SetFloat("push_z_multiplier", pushZMultiplier);
        sweCS.SetFloat("splash_velocity_multiplier", splashVelocityMultiplier);
        sweCS.SetInt("max_particles", maxParticles);
        sweCS.SetBuffer(kernelSweSublimate, "SWE_State_Read", sweStateBufferRead);
        sweCS.SetBuffer(kernelSweSublimate, "SWE_State_Write", sweStateBufferWrite);
        sweCS.SetBuffer(kernelSweSublimate, "APIC_Particle_Buffer", apicParticleBuffer);
        sweCS.SetBuffer(kernelSweSublimate, "ParticleCounter", particleCounterBuffer);
        sweCS.Dispatch(kernelSweSublimate, tgSWE_X, tgSWE_Y, 1);
        SwapSWEBuffers();

        // =========================================================
        // Step 8: APIC P2G (Particle to Grid)
        // 粒子の持つ質量と運動量を、圧力計算用の3Dグリッドに転写する
        // =========================================================
        apicCS.SetBuffer(kernelApicP2G, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(kernelApicP2G, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(kernelApicP2G, "_ApicMassInt", apicGridMassBuffer);
        apicCS.SetBuffer(kernelApicP2G, "_ApicVelIntX", apicGridVelXBuffer);
        apicCS.SetBuffer(kernelApicP2G, "_ApicVelIntY", apicGridVelYBuffer);
        apicCS.SetBuffer(kernelApicP2G, "_ApicVelIntZ", apicGridVelZBuffer);
        apicCS.SetBuffer(kernelApicP2G, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.DispatchIndirect(kernelApicP2G, particleDispatchArgsBuffer);

        // =========================================================
        // Step 9: APIC 速度の正規化と発散(Divergence)計算
        // グリッドの速度を質量で割り、非圧縮性流体のための発散を計算する
        // =========================================================
        apicCS.SetBuffer(kernelApicNormVel, "_ApicMassInt", apicGridMassBuffer);
        apicCS.SetBuffer(kernelApicNormVel, "_ApicVelIntX", apicGridVelXBuffer);
        apicCS.SetBuffer(kernelApicNormVel, "_ApicVelIntY", apicGridVelYBuffer);
        apicCS.SetBuffer(kernelApicNormVel, "_ApicVelIntZ", apicGridVelZBuffer);
        apicCS.Dispatch(kernelApicNormVel, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetTexture(kernelApicDiv, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(kernelApicDiv, "_ApicVelIntX", apicGridVelXBuffer);
        apicCS.SetBuffer(kernelApicDiv, "_ApicVelIntY", apicGridVelYBuffer);
        apicCS.SetBuffer(kernelApicDiv, "_ApicVelIntZ", apicGridVelZBuffer);
        apicCS.SetBuffer(kernelApicDiv, "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(kernelApicDiv, "_ApicMassInt", apicGridMassBuffer);
        apicCS.SetBuffer(kernelApicDiv, "SWE_State_Read", sweStateBufferRead);
        apicCS.Dispatch(kernelApicDiv, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // =========================================================
        // Step 10: APIC 圧力計算(Jacobi-PCG法)の初期化
        // =========================================================
        pcgDotResultBuffer.SetData(new uint[] { 0 });
        apicCS.SetBuffer(kernelApicBuildDiag, "_ApicMassInt", apicGridMassBuffer);
        apicCS.SetBuffer(kernelApicBuildDiag, "PCG_Precon", pcgPreconBuffer);
        apicCS.SetTexture(kernelApicBuildDiag, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(kernelApicBuildDiag, "SWE_State_Read", sweStateBufferRead);
        apicCS.Dispatch(kernelApicBuildDiag, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetTexture(kernelApicInitCG, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(kernelApicInitCG, "_ApicMassInt", apicGridMassBuffer);
        apicCS.SetBuffer(kernelApicInitCG, "APIC_Divergence", apicDivergenceBuffer);
        apicCS.SetBuffer(kernelApicInitCG, "APIC_Pressure_Write", apicPressureBufferWrite); 
        apicCS.SetBuffer(kernelApicInitCG, "PCG_R", pcgRBuffer);
        apicCS.SetBuffer(kernelApicInitCG, "PCG_P", pcgPBuffer);
        apicCS.SetBuffer(kernelApicInitCG, "PCG_Q", pcgQBuffer);
        apicCS.SetBuffer(kernelApicInitCG, "PCG_Precon", pcgPreconBuffer);
        apicCS.Dispatch(kernelApicInitCG, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetTexture(kernelApicDotPre, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(kernelApicDotPre, "_ApicMassInt", apicGridMassBuffer);
        apicCS.SetBuffer(kernelApicDotPre, "PCG_R", pcgRBuffer);
        apicCS.SetBuffer(kernelApicDotPre, "PCG_Precon", pcgPreconBuffer);
        apicCS.SetBuffer(kernelApicDotPre, "PCG_DotResult", pcgDotResultBuffer);
        apicCS.Dispatch(kernelApicDotPre, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetBuffer(kernelApicInitRTr, "PCG_DotResult", pcgDotResultBuffer);
        apicCS.SetBuffer(kernelApicInitRTr, "PCG_Scalars", pcgScalarsBuffer);
        apicCS.Dispatch(kernelApicInitRTr, 1, 1, 1);

        // =========================================================
        // Step 11: APIC 圧力ポアソン方程式の反復計算 (PCGループ)
        // 質量保存を満たすための圧力を求める(最も負荷が高いステップ)
        // =========================================================
        for (int i = 0; i < pcgIterations; i++)
        {
            apicCS.SetBuffer(kernelApicApplyA, "_ApicMassInt", apicGridMassBuffer);
            apicCS.SetBuffer(kernelApicApplyA, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kernelApicApplyA, "PCG_Q", pcgQBuffer);
            apicCS.SetTexture(kernelApicApplyA, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(kernelApicApplyA, "SWE_State_Read", sweStateBufferRead);
            apicCS.Dispatch(kernelApicApplyA, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetTexture(kernelApicDotGen, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(kernelApicDotGen, "_ApicMassInt", apicGridMassBuffer);
            apicCS.SetBuffer(kernelApicDotGen, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kernelApicDotGen, "PCG_Q", pcgQBuffer);
            apicCS.SetBuffer(kernelApicDotGen, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.Dispatch(kernelApicDotGen, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(kernelApicCalcAlpha, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.SetBuffer(kernelApicCalcAlpha, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kernelApicCalcAlpha, 1, 1, 1);

            apicCS.SetTexture(kernelApicUpdatePR, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(kernelApicUpdatePR, "_ApicMassInt", apicGridMassBuffer);
            apicCS.SetBuffer(kernelApicUpdatePR, "APIC_Pressure_Write", apicPressureBufferWrite);
            apicCS.SetBuffer(kernelApicUpdatePR, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kernelApicUpdatePR, "PCG_Q", pcgQBuffer);
            apicCS.SetBuffer(kernelApicUpdatePR, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kernelApicUpdatePR, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kernelApicUpdatePR, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(kernelApicDotPre, "_ApicMassInt", apicGridMassBuffer);
            apicCS.SetBuffer(kernelApicDotPre, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kernelApicDotPre, "PCG_Precon", pcgPreconBuffer);
            apicCS.SetBuffer(kernelApicDotPre, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.Dispatch(kernelApicDotPre, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(kernelApicCalcBeta, "PCG_DotResult", pcgDotResultBuffer);
            apicCS.SetBuffer(kernelApicCalcBeta, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kernelApicCalcBeta, 1, 1, 1);

            apicCS.SetTexture(kernelApicUpdateD, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(kernelApicUpdateD, "_ApicMassInt", apicGridMassBuffer);
            apicCS.SetBuffer(kernelApicUpdateD, "PCG_P", pcgPBuffer);
            apicCS.SetBuffer(kernelApicUpdateD, "PCG_R", pcgRBuffer);
            apicCS.SetBuffer(kernelApicUpdateD, "PCG_Precon", pcgPreconBuffer);
            apicCS.SetBuffer(kernelApicUpdateD, "PCG_Scalars", pcgScalarsBuffer);
            apicCS.Dispatch(kernelApicUpdateD, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);
        }

        // =========================================================
        // Step 12: APIC G2P (Grid to Particle)
        // 計算された圧力から速度場を修正し、粒子の速度とアフィン行列を更新する
        // =========================================================
        apicCS.SetBuffer(kernelApicG2P, "ActiveParticleList_Read", activeParticleListBuffer);
        apicCS.SetBuffer(kernelApicG2P, "ActiveParticleCount", activeParticleCountBuffer);
        apicCS.SetBuffer(kernelApicG2P, "_ApicVelIntX", apicGridVelXBuffer);
        apicCS.SetBuffer(kernelApicG2P, "_ApicVelIntY", apicGridVelYBuffer);
        apicCS.SetBuffer(kernelApicG2P, "_ApicVelIntZ", apicGridVelZBuffer);
        apicCS.SetBuffer(kernelApicG2P, "APIC_Pressure_Write", apicPressureBufferWrite);
        apicCS.SetBuffer(kernelApicG2P, "APIC_Particle_Buffer", apicParticleBuffer);
        apicCS.SetTexture(kernelApicG2P, "TerrainHeightMap", terrainHeightMap);
        apicCS.DispatchIndirect(kernelApicG2P, particleDispatchArgsBuffer);

        // =========================================================
        // Step 13: ボクセル化と描画処理へ
        // =========================================================
        DispatchAndRenderVoxelizer();
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
        particleCounterBuffer?.Release(); pcgRBuffer?.Release();
        pcgPBuffer?.Release(); pcgQBuffer?.Release();
        pcgPreconBuffer?.Release(); pcgDotResultBuffer?.Release();
        pcgScalarsBuffer?.Release(); activeParticleListBuffer?.Release();
        activeParticleCountBuffer?.Release(); particleDispatchArgsBuffer?.Release();

        ReleaseTerrain();
        ReleaseVoxelizer();
    }
}