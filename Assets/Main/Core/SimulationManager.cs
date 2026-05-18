using System.Reflection;
using System.Runtime.InteropServices;

using UnityEngine;
using UnityEngine.InputSystem;

using FluidSimulation;

public partial class SimulationManager : MonoBehaviour
{
    // キャッシュの一括取得用
    public class ShaderKernels
    {
        public void Initialize(ComputeShader cs)
        {
            var fields = this.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType == typeof(int))
                {
                    if (cs.HasKernel(field.Name))
                        field.SetValue(this, cs.FindKernel(field.Name));
                    else
                        Debug.LogError($"Kernel '{field.Name}' not found in {cs.name}");
                }
            }
        }
    }

    public class ApicKernels : ShaderKernels
    {
        public int Advect, ClearApicGrid , P2G, NormalizeVel, Divergence, G2P, Condense, 
            BuildDiag, InitCG, DotProductPrecon, InitRTr, ApplyA, DotProductGeneric, CalculateAlpha, 
            UpdatePR, CalculateBeta, UpdateD, BuildActiveList, SetupDispatchArgs, SetupMeshDrawArgs;
    }

    public class SweKernels : ShaderKernels
    {
        public int ClearIntermediates, InitSwe, UpdateSwe, Sublimate, ApplyCondensation, InteractSwe, ShiftSweGrid;
    }

    public class VoxelizerKernels : ShaderKernels
    {
        public int ClearVoxelGrid, SplatParticles, SplatSWE, BlurX, BlurY, BlurZ, MarchingCubes;
    }

    // バッファの一括解放用
    public class BufferSet
    {
        public ComputeBuffer apicParticle, deltaH, deltaHu, deltaHv;
        public ComputeBuffer sweStateRead, sweStateWrite;
        public ComputeBuffer apicGridMass, apicGridVelX, apicGridVelY, apicGridVelZ;
        public ComputeBuffer apicDivergence, apicPressureWrite, particleCounter;
        
        // Jacobi-PCG用のバッファ
        public ComputeBuffer pcgR, pcgP, pcgQ, pcgPrecon, pcgDotResult, pcgScalars;
        
        // 計算負荷削減のための、生存粒子リストと間接ディスパッチ(DispatchIndirect)用バッファ
        public ComputeBuffer activeParticleList, activeParticleCount, particleDispatchArgs;

        // ボクセル化
        public ComputeBuffer voxelGrid, voxelMomX, voxelMomY, voxelMomZ, edgeToVertexTable, 
            triangle, drawArgs, triTable, edgeTable, voxelBlurA, voxelBlurB, voxelFinalDensity;

        public void ReleaseAll()
        {
            FieldInfo[] fields = GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.GetValue(this) is ComputeBuffer cb)
                {
                    cb.Release();
                    cb.Dispose();
                }
            }
        }
    }

    [Header("Simulation Size")]
    [SerializeField] private Vector3Int m_DomainLength = new(64, 64, 64);
    [SerializeField] private int m_SweCellsPerMeter = 2;
    [SerializeField] private int m_ApicCellsPerMeter = 1;
    [SerializeField] private int m_MaxParticles = 1000000;
    [SerializeField] private int m_PcgIterations = 8;

    [Header("Sublimation")]
    [SerializeField] private float m_ToApicFroudeTH = 1.0f; 
    [SerializeField] private float m_ToApicGradTH = 1.5f; 
    [SerializeField] private float m_ToApicLaplacianTH = -2.5f; 
    
    [SerializeField] private float m_SublimatePerSecond = 20.0f;
    [SerializeField] private float m_SublimateVerticalMulti = 1.0f; 
    [SerializeField] private float m_SublimateSplashMulti = 1.0f;

    [Header("Condensation")]
    [SerializeField] private float m_ToSweHorizontalTransfer = 0.3f;
    [SerializeField] private float m_ToSweVerticalMomTransfer = 0.1f;

    [Header("Mouse Interaction")]
    [SerializeField] private float mouseRadius = 3.0f;
    [SerializeField] private float mouseForce = 3.0f;

    [Header("Compute Shaders")]
    [SerializeField] private ComputeShader sweCS;
    [SerializeField] private ComputeShader apicCS;

    [Header("Tracking")]
    public FFTManager fftOcean;
    public Transform trackTarget;

    [HideInInspector] public Vector2 worldOffset = Vector2.zero; 

    private float dxSwe, dxApic;
    private Vector2Int sweGridRes;
    private Vector3Int apicGridRes;

    private Vector3 prevMousePos;
    private bool wasMouseDown = false;
    
    private readonly ApicKernels apicKernels = new();
    private readonly SweKernels sweKernels = new();
    private readonly VoxelizerKernels voxKernels = new();
    private readonly BufferSet buffers = new();

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
            worldOffset = new Vector2(targetBaseX, targetBaseZ);
        }
        
        sweKernels.Initialize(sweCS);
        apicKernels.Initialize(apicCS);
        
        InitializeBuffers();
        InitializeTerrain();
        InitializeVoxelizer();
        BindBuffers();
    }

    private void InitializeBuffers()
    {
        int sweTotalCells = sweGridRes.x * sweGridRes.y;
        buffers.sweStateRead = new ComputeBuffer(sweTotalCells, Marshal.SizeOf(typeof(SWECell)));
        buffers.sweStateWrite = new ComputeBuffer(sweTotalCells, Marshal.SizeOf(typeof(SWECell)));
        buffers.apicParticle = new ComputeBuffer(m_MaxParticles, Marshal.SizeOf(typeof(APICParticle)), ComputeBufferType.Default);
        buffers.deltaH = new ComputeBuffer(sweTotalCells, sizeof(uint));
        buffers.deltaHu = new ComputeBuffer(sweTotalCells, sizeof(uint));
        buffers.deltaHv = new ComputeBuffer(sweTotalCells, sizeof(uint));

        int apicTotalCells = apicGridRes.x * apicGridRes.y * apicGridRes.z;
        buffers.apicGridMass = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicGridVelX = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicGridVelY = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicGridVelZ = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicDivergence = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.apicPressureWrite = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.particleCounter = new ComputeBuffer(1, sizeof(uint));
        buffers.particleCounter.SetData(new uint[] { 0 });

        APICParticle[] emptyParticles = new APICParticle[m_MaxParticles];
        buffers.apicParticle.SetData(emptyParticles);
        
        SWECell[] initialSWE = new SWECell[sweTotalCells];
        for (int i = 0; i < sweTotalCells; i++)
        {
            initialSWE[i] = new SWECell { h = 0f, hu = 0f, hv = 0f, padding = 0f };
        }
        buffers.sweStateRead.SetData(initialSWE);
        buffers.sweStateWrite.SetData(initialSWE);

        buffers.pcgR = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.pcgP = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.pcgQ = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.pcgPrecon = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.pcgDotResult = new ComputeBuffer(1, sizeof(uint));
        buffers.pcgScalars = new ComputeBuffer(5, sizeof(float));

        buffers.activeParticleList = new ComputeBuffer(m_MaxParticles, sizeof(uint), ComputeBufferType.Append);
        buffers.activeParticleCount = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        buffers.particleDispatchArgs = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
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
        }
        
        sweCS.SetBuffer(sweKernels.ClearIntermediates, "_DeltaHInt", buffers.deltaH);
        sweCS.SetBuffer(sweKernels.ClearIntermediates, "_DeltaHuInt", buffers.deltaHu);
        sweCS.SetBuffer(sweKernels.ClearIntermediates, "_DeltaHvInt", buffers.deltaHv);
    }

    void Update()
    {
        if (buffers.sweStateRead == null || trackTarget == null) return;

        // 動的タイムステップの計算
        float dtApic = Mathf.Min(Time.deltaTime, 0.0333f);
        float expected_max_depth = 50.0f;
        float expected_wave_speed = Mathf.Sqrt(9.81f * expected_max_depth);
        float expected_max_velocity = 15.0f; 
        
        // 波の速度がセルを飛び越えない安全なSWEの最大タイムステップを算出
        float dtSwe_max = 0.20f * dxSwe / (expected_wave_speed + expected_max_velocity); 
        int subSteps = Mathf.CeilToInt(dtApic / dtSwe_max);
        float dtSwe = dtApic / subSteps;

        // 追従・地形処理
        UpdateTrackingAndTerrain();

        ComputeShader[] shaders = { sweCS, apicCS };
        foreach (var cs in shaders)
        {
            cs.SetFloat("_dtSwe", dtSwe);
            cs.SetFloat("_dtApic", dtApic);
            cs.SetVector("_WorldOffset", worldOffset);
        }

        // --- マウス入力 ---
        Vector2 mousePosSWE = Vector2.zero;
        Vector2 mouseDirSWE = Vector2.zero;
        int mouseActive = 0;

        var mouse = Mouse.current;

        if (mouse != null && mouse.leftButton.isPressed)
        {
            Plane waterPlane = new Plane(Vector3.up, Vector3.zero);
            Vector2 currentMousePos = mouse.position.ReadValue();
            Ray ray = Camera.main.ScreenPointToRay(currentMousePos);

            if (waterPlane.Raycast(ray, out float enter))
            {
                Vector3 hitPoint = ray.GetPoint(enter);
                if (wasMouseDown)
                {
                    // 3D空間内での距離(mouseDeltaは使えない)
                    Vector3 delta = hitPoint - prevMousePos;
                    mouseDirSWE = new Vector2(delta.x, delta.z) / dtApic;
                    if (mouseDirSWE.sqrMagnitude > 0.01f) mouseActive = 1;
                }
                prevMousePos = hitPoint;
                mousePosSWE = new Vector2(hitPoint.x, hitPoint.z);
            }
        }
        // マウスの押下状態を更新
        wasMouseDown = mouse != null && mouse.leftButton.isPressed;

        sweCS.SetVector("_MousePos", mousePosSWE);
        sweCS.SetVector("_MouseDir", mouseDirSWE);
        sweCS.SetFloat("_MouseRadius", mouseRadius);
        sweCS.SetFloat("_MouseForce", mouseForce);
        sweCS.SetInt("_MouseActive", mouseActive);

        // --- スレッドグループ数の計算 ---
        Vector2Int tgSwe = (sweGridRes + new Vector2Int(7,7)) / 8;
        Vector3Int tgApic = (apicGridRes + new Vector3Int(7,7,7)) / 8;
        int tgParticles = (m_MaxParticles + 63) / 64;

        if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
            int[] apicFFTKernels = { apicKernels.BuildActiveList, apicKernels.Condense, apicKernels.Divergence, apicKernels.BuildDiag };
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
        sweCS.Dispatch(sweKernels.ClearIntermediates, tgSwe.x, tgSwe.y, 1);
        
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicDivergence", buffers.apicDivergence);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicPressure_W", buffers.apicPressureWrite);
        apicCS.Dispatch(apicKernels.ClearApicGrid, tgApic.x, tgApic.y, tgApic.z);

        // =========================================================
        // Step 2: 生存粒子のリスト構築と間接ディスパッチ(DispatchIndirect)の準備
        // 無駄な計算を省くため、生きているAPIC粒子だけを抽出する
        // =========================================================
        buffers.activeParticleList.SetCounterValue(0); 
        apicCS.SetBuffer(apicKernels.BuildActiveList, "_ApicParticle", buffers.apicParticle);
        apicCS.SetBuffer(apicKernels.BuildActiveList, "_ActiveParticleList_W", buffers.activeParticleList);
        apicCS.SetInt("max_particles", m_MaxParticles);
        apicCS.Dispatch(apicKernels.BuildActiveList, tgParticles, 1, 1);

        ComputeBuffer.CopyCount(buffers.activeParticleList, buffers.activeParticleCount, 0);
        ComputeBuffer.CopyCount(buffers.activeParticleList, buffers.particleDispatchArgs, 0);
        apicCS.SetBuffer(apicKernels.SetupDispatchArgs, "_ParticleDispatchArgs", buffers.particleDispatchArgs);
        apicCS.Dispatch(apicKernels.SetupDispatchArgs, 1, 1, 1);
        
        // =========================================================
        // Step 3: APIC粒子の移流 (Advect)
        // 粒子を速度に従って移動させ、地形等との衝突判定を行う
        // =========================================================
        apicCS.SetBuffer(apicKernels.Advect, "_ActiveParticleList_R", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.Advect, "_ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.Advect, "_ApicParticle", buffers.apicParticle);
        apicCS.SetTexture(apicKernels.Advect, "_TerrainHeightMap", terrainHeightMap);
        apicCS.DispatchIndirect(apicKernels.Advect, buffers.particleDispatchArgs);

        // =========================================================
        // Step 4: APIC → SWE 凝縮 (Condensation)
        // 水面に落下したAPIC粒子を消滅させ、運動量をSWEの中間バッファに書き込む
        // =========================================================
        apicCS.SetTexture(apicKernels.Condense, "_TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.Condense, "_ActiveParticleList_R", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.Condense, "_ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.Condense, "_SweState_R", buffers.sweStateRead);
        apicCS.SetBuffer(apicKernels.Condense, "_DeltaHInt", buffers.deltaH);
        apicCS.SetBuffer(apicKernels.Condense, "_DeltaHuInt", buffers.deltaHu);
        apicCS.SetBuffer(apicKernels.Condense, "_DeltaHvInt", buffers.deltaHv);
        apicCS.SetBuffer(apicKernels.Condense, "_ApicParticle", buffers.apicParticle);
        apicCS.SetFloat("horizontal_momentum_transfer", m_ToSweHorizontalTransfer);
        apicCS.SetFloat("vertical_momentum_to_wave", m_ToSweVerticalMomTransfer);
        apicCS.DispatchIndirect(apicKernels.Condense, buffers.particleDispatchArgs);

        // =========================================================
        // Step 5: SWE 凝縮・マウスインタラクションの適用
        // 中間バッファに溜まった凝縮の衝撃やマウスの力をSWE本体のステートに反映する
        // =========================================================
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "_SweState_R", buffers.sweStateRead);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "_SweState_W", buffers.sweStateWrite);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "_DeltaHInt", buffers.deltaH);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "_DeltaHuInt", buffers.deltaHu);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "_DeltaHvInt", buffers.deltaHv);
        sweCS.Dispatch(sweKernels.ApplyCondensation, tgSwe.x, tgSwe.y, 1);
        SwapSWEBuffers();

        if (mouseActive == 1) {
            sweCS.SetBuffer(sweKernels.InteractSwe, "_SweState_R", buffers.sweStateRead);
            sweCS.SetBuffer(sweKernels.InteractSwe, "_SweState_W", buffers.sweStateWrite);
            sweCS.Dispatch(sweKernels.InteractSwe, tgSwe.x, tgSwe.y, 1);
            SwapSWEBuffers();
        }

        // =========================================================
        // Step 6: SWE サブサイクリング更新
        // 高速な波の伝播を安定させるため、細かいタイムステップで複数回更新する
        // =========================================================
        sweCS.SetTexture(sweKernels.UpdateSwe, "TerrainHeightMap", terrainHeightMap);
        sweCS.SetTexture(sweKernels.Sublimate, "TerrainHeightMap", terrainHeightMap);
        
        if (fftOcean != null && fftOcean.displacementMaps.Length >= 3) {
            sweCS.SetTexture(sweKernels.UpdateSwe, "FFT_DispLOD0", fftOcean.displacementMaps[0]);
            sweCS.SetTexture(sweKernels.UpdateSwe, "FFT_DispLOD1", fftOcean.displacementMaps[1]);
            sweCS.SetTexture(sweKernels.UpdateSwe, "FFT_DispLOD2", fftOcean.displacementMaps[2]);
            sweCS.SetFloat("FFT_Size0", fftOcean.domainSizes[0]);
            sweCS.SetFloat("FFT_Size1", fftOcean.domainSizes[1]);
            sweCS.SetFloat("FFT_Size2", fftOcean.domainSizes[2]);
        }
        for (int i = 0; i < subSteps; i++) {
            sweCS.SetBuffer(sweKernels.UpdateSwe, "_SweState_R", buffers.sweStateRead);
            sweCS.SetBuffer(sweKernels.UpdateSwe, "_SweState_W", buffers.sweStateWrite);
            sweCS.Dispatch(sweKernels.UpdateSwe, tgSwe.x, tgSwe.y, 1);
            SwapSWEBuffers();
        }

        // =========================================================
        // Step 7: SWE → APIC 昇華 (Sublimation)
        // 波が激しくなった領域のSWEの体積を削り、APIC粒子をスポーンさせる
        // =========================================================
        sweCS.SetFloat("fr_threshold", m_ToApicFroudeTH);
        sweCS.SetFloat("grad_threshold", m_ToApicGradTH);
        sweCS.SetFloat("laplacian_threshold", m_ToApicLaplacianTH);
        sweCS.SetFloat("conversion_rate_multiplier", m_SublimatePerSecond);
        sweCS.SetFloat("push_z_multiplier", m_SublimateVerticalMulti);
        sweCS.SetFloat("splash_velocity_multiplier", m_SublimateSplashMulti);
        sweCS.SetInt("max_particles", m_MaxParticles);
        sweCS.SetBuffer(sweKernels.Sublimate, "_SweState_R", buffers.sweStateRead);
        sweCS.SetBuffer(sweKernels.Sublimate, "_SweState_W", buffers.sweStateWrite);
        sweCS.SetBuffer(sweKernels.Sublimate, "_ApicParticle", buffers.apicParticle);
        sweCS.SetBuffer(sweKernels.Sublimate, "_ParticleCounter", buffers.particleCounter);
        sweCS.Dispatch(sweKernels.Sublimate, tgSwe.x, tgSwe.y, 1);
        SwapSWEBuffers();

        // =========================================================
        // Step 8: APIC P2G (Particle to Grid)
        // 粒子の持つ質量と運動量を、圧力計算用の3Dグリッドに転写する
        // =========================================================
        apicCS.SetBuffer(apicKernels.P2G, "_ActiveParticleList_R", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.P2G, "_ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicParticle", buffers.apicParticle);
        apicCS.DispatchIndirect(apicKernels.P2G, buffers.particleDispatchArgs);

        // =========================================================
        // Step 9: APIC 速度の正規化と発散(Divergence)計算
        // グリッドの速度を質量で割り、非圧縮性流体のための発散を計算する
        // =========================================================
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.Dispatch(apicKernels.NormalizeVel, tgApic.x, tgApic.y, tgApic.z);

        apicCS.SetTexture(apicKernels.Divergence, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicDivergence", buffers.apicDivergence);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.Divergence, "_SweState_R", buffers.sweStateRead);
        apicCS.Dispatch(apicKernels.Divergence, tgApic.x, tgApic.y, tgApic.z);

        // =========================================================
        // Step 10: APIC 圧力計算(Jacobi-PCG法)の初期化
        // =========================================================
        buffers.pcgDotResult.SetData(new uint[] { 0 });
        apicCS.SetBuffer(apicKernels.BuildDiag, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.BuildDiag, "_PcgPrecon", buffers.pcgPrecon);
        apicCS.SetTexture(apicKernels.BuildDiag, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.BuildDiag, "_SweState_R", buffers.sweStateRead);
        apicCS.Dispatch(apicKernels.BuildDiag, tgApic.x, tgApic.y, tgApic.z);

        apicCS.SetTexture(apicKernels.InitCG, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.InitCG, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.InitCG, "_ApicDivergence", buffers.apicDivergence);
        apicCS.SetBuffer(apicKernels.InitCG, "_ApicPressure_W", buffers.apicPressureWrite); 
        apicCS.SetBuffer(apicKernels.InitCG, "_PcgR", buffers.pcgR);
        apicCS.SetBuffer(apicKernels.InitCG, "_PcgP", buffers.pcgP);
        apicCS.SetBuffer(apicKernels.InitCG, "_PcgQ", buffers.pcgQ);
        apicCS.SetBuffer(apicKernels.InitCG, "_PcgPrecon", buffers.pcgPrecon);
        apicCS.Dispatch(apicKernels.InitCG, tgApic.x, tgApic.y, tgApic.z);

        apicCS.SetTexture(apicKernels.DotProductPrecon, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "_PcgR", buffers.pcgR);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "_PcgPrecon", buffers.pcgPrecon);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "_PcgDotResult", buffers.pcgDotResult);
        apicCS.Dispatch(apicKernels.DotProductPrecon, tgApic.x, tgApic.y, tgApic.z);

        apicCS.SetBuffer(apicKernels.InitRTr, "_PcgDotResult", buffers.pcgDotResult);
        apicCS.SetBuffer(apicKernels.InitRTr, "_PcgScalars", buffers.pcgScalars);
        apicCS.Dispatch(apicKernels.InitRTr, 1, 1, 1);

        // =========================================================
        // Step 11: APIC 圧力ポアソン方程式の反復計算 (PCGループ)
        // 質量保存を満たすための圧力を求める(最も負荷が高いステップ)
        // =========================================================
        for (int i = 0; i < m_PcgIterations; i++)
        {
            apicCS.SetBuffer(apicKernels.ApplyA, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.ApplyA, "_PcgP", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.ApplyA, "_PcgQ", buffers.pcgQ);
            apicCS.SetTexture(apicKernels.ApplyA, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.ApplyA, "_SweState_R", buffers.sweStateRead);
            apicCS.Dispatch(apicKernels.ApplyA, tgApic.x, tgApic.y, tgApic.z);

            apicCS.SetTexture(apicKernels.DotProductGeneric, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "_PcgP", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "_PcgQ", buffers.pcgQ);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "_PcgDotResult", buffers.pcgDotResult);
            apicCS.Dispatch(apicKernels.DotProductGeneric, tgApic.x, tgApic.y, tgApic.z);

            apicCS.SetBuffer(apicKernels.CalculateAlpha, "_PcgDotResult", buffers.pcgDotResult);
            apicCS.SetBuffer(apicKernels.CalculateAlpha, "_PcgScalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.CalculateAlpha, 1, 1, 1);

            apicCS.SetTexture(apicKernels.UpdatePR, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.UpdatePR, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.UpdatePR, "_ApicPressure_W", buffers.apicPressureWrite);
            apicCS.SetBuffer(apicKernels.UpdatePR, "_PcgP", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.UpdatePR, "_PcgQ", buffers.pcgQ);
            apicCS.SetBuffer(apicKernels.UpdatePR, "_PcgR", buffers.pcgR);
            apicCS.SetBuffer(apicKernels.UpdatePR, "_PcgScalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.UpdatePR, tgApic.x, tgApic.y, tgApic.z);

            apicCS.SetBuffer(apicKernels.DotProductPrecon, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.DotProductPrecon, "_PcgR", buffers.pcgR);
            apicCS.SetBuffer(apicKernels.DotProductPrecon, "_PcgPrecon", buffers.pcgPrecon);
            apicCS.SetBuffer(apicKernels.DotProductPrecon, "_PcgDotResult", buffers.pcgDotResult);
            apicCS.Dispatch(apicKernels.DotProductPrecon, tgApic.x, tgApic.y, tgApic.z);

            apicCS.SetBuffer(apicKernels.CalculateBeta, "_PcgDotResult", buffers.pcgDotResult);
            apicCS.SetBuffer(apicKernels.CalculateBeta, "_PcgScalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.CalculateBeta, 1, 1, 1);

            apicCS.SetTexture(apicKernels.UpdateD, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.UpdateD, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.UpdateD, "_PcgP", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.UpdateD, "_PcgR", buffers.pcgR);
            apicCS.SetBuffer(apicKernels.UpdateD, "_PcgPrecon", buffers.pcgPrecon);
            apicCS.SetBuffer(apicKernels.UpdateD, "_PcgScalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.UpdateD, tgApic.x, tgApic.y, tgApic.z);
        }

        // =========================================================
        // Step 12: APIC G2P (Grid to Particle)
        // 計算された圧力から速度場を修正し、粒子の速度とアフィン行列を更新する
        // =========================================================
        apicCS.SetBuffer(apicKernels.G2P, "_ActiveParticleList_R", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.G2P, "_ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicPressure_W", buffers.apicPressureWrite);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicParticle", buffers.apicParticle);
        apicCS.SetTexture(apicKernels.G2P, "TerrainHeightMap", terrainHeightMap);
        apicCS.DispatchIndirect(apicKernels.G2P, buffers.particleDispatchArgs);

        // =========================================================
        // Step 13: ボクセル化と描画処理へ
        // =========================================================
        DispatchAndRenderVoxelizer();
    }

    private void SwapSWEBuffers()
    {
        ComputeBuffer temp = buffers.sweStateRead;
        buffers.sweStateRead = buffers.sweStateWrite;
        buffers.sweStateWrite = temp;
    }

    void OnDisable()
    {
        buffers.ReleaseAll();
        ReleaseTerrain();
    }
}