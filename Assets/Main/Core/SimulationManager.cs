using UnityEngine;
using System.Reflection;
using System.Runtime.InteropServices;
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
        public ComputeBuffer apicParticle, deltaH, deltaHU, deltaHV;
        public ComputeBuffer sweStateRead, sweStateWrite;
        public ComputeBuffer apicGridMass, apicGridVelX, apicGridVelY, apicGridVelZ;
        public ComputeBuffer apicDivergence, apicPressureWrite, particleCounter;
        
        // Jacobi-PCG用のバッファ
        public ComputeBuffer pcgR, pcgP, pcgQ, pcgPrecon, pcgDotResult, pcgScalars;
        
        // 計算負荷削減のための、生存粒子リストと間接ディスパッチ(DispatchIndirect)用バッファ
        public ComputeBuffer activeParticleList, activeParticleCount, particleDispatchArgs;

        public void ReleaseAll()
        {
            FieldInfo[] fields = this.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
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
            sweWorldOffset = new Vector2(targetBaseX, targetBaseZ);
            apicWorldOffset = new Vector3(sweWorldOffset.x, sweWorldOffset.y, seaBottomHeight);
        }

        M_ratio = (int)(dxApic / dxSwe);

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
        buffers.apicParticle = new ComputeBuffer(maxParticles, Marshal.SizeOf(typeof(APICParticle)), ComputeBufferType.Default);
        buffers.deltaH = new ComputeBuffer(sweTotalCells, sizeof(uint));
        buffers.deltaHU = new ComputeBuffer(sweTotalCells, sizeof(uint));
        buffers.deltaHV = new ComputeBuffer(sweTotalCells, sizeof(uint));

        int apicTotalCells = apicGridRes.x * apicGridRes.y * apicGridRes.z;
        buffers.apicGridMass = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicGridVelX = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicGridVelY = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicGridVelZ = new ComputeBuffer(apicTotalCells, sizeof(uint));
        buffers.apicDivergence = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.apicPressureWrite = new ComputeBuffer(apicTotalCells, sizeof(float));
        buffers.particleCounter = new ComputeBuffer(1, sizeof(uint));
        buffers.particleCounter.SetData(new uint[] { 0 });

        APICParticle[] emptyParticles = new APICParticle[maxParticles];
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

        buffers.activeParticleList = new ComputeBuffer(maxParticles, sizeof(uint), ComputeBufferType.Append);
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
            cs.SetInt("M_ratio", M_ratio);  
            cs.SetFloat("base_mass", baseMass);
            cs.SetFloat("sea_bottom_z", seaBottomHeight);
        }
        
        sweCS.SetBuffer(sweKernels.ClearIntermediates, "Delta_H_Buffer", buffers.deltaH);
        sweCS.SetBuffer(sweKernels.ClearIntermediates, "Delta_HU_Buffer", buffers.deltaHU);
        sweCS.SetBuffer(sweKernels.ClearIntermediates, "Delta_HV_Buffer", buffers.deltaHV);
    }

    void Update()
    {
        if (buffers.sweStateRead == null || trackTarget == null) return;

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
        sweCS.Dispatch(sweKernels.ClearIntermediates, tgSWE_X, tgSWE_Y, 1);
        
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "APIC_Divergence", buffers.apicDivergence);
        apicCS.SetBuffer(apicKernels.ClearApicGrid, "APIC_Pressure_Write", buffers.apicPressureWrite);
        apicCS.Dispatch(apicKernels.ClearApicGrid, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // =========================================================
        // Step 2: 生存粒子のリスト構築と間接ディスパッチ(DispatchIndirect)の準備
        // 無駄な計算を省くため、生きているAPIC粒子だけを抽出する
        // =========================================================
        buffers.activeParticleList.SetCounterValue(0); 
        apicCS.SetBuffer(apicKernels.BuildActiveList, "APIC_Particle_Buffer", buffers.apicParticle);
        apicCS.SetBuffer(apicKernels.BuildActiveList, "ActiveParticleList_Write", buffers.activeParticleList);
        apicCS.SetInt("max_particles", maxParticles);
        apicCS.Dispatch(apicKernels.BuildActiveList, tgParticles, 1, 1);

        ComputeBuffer.CopyCount(buffers.activeParticleList, buffers.activeParticleCount, 0);
        ComputeBuffer.CopyCount(buffers.activeParticleList, buffers.particleDispatchArgs, 0);
        apicCS.SetBuffer(apicKernels.SetupDispatchArgs, "ParticleDispatchArgs", buffers.particleDispatchArgs);
        apicCS.Dispatch(apicKernels.SetupDispatchArgs, 1, 1, 1);
        
        // =========================================================
        // Step 3: APIC粒子の移流 (Advect)
        // 粒子を速度に従って移動させ、地形等との衝突判定を行う
        // =========================================================
        apicCS.SetBuffer(apicKernels.Advect, "ActiveParticleList_Read", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.Advect, "ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.Advect, "APIC_Particle_Buffer", buffers.apicParticle);
        apicCS.SetTexture(apicKernels.Advect, "TerrainHeightMap", terrainHeightMap);
        apicCS.DispatchIndirect(apicKernels.Advect, buffers.particleDispatchArgs);

        // =========================================================
        // Step 4: APIC → SWE 凝縮 (Condensation)
        // 水面に落下したAPIC粒子を消滅させ、運動量をSWEの中間バッファに書き込む
        // =========================================================
        apicCS.SetTexture(apicKernels.Condense, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.Condense, "ActiveParticleList_Read", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.Condense, "ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.Condense, "SWE_State_Read", buffers.sweStateRead);
        apicCS.SetBuffer(apicKernels.Condense, "Delta_H_Buffer", buffers.deltaH);
        apicCS.SetBuffer(apicKernels.Condense, "Delta_HU_Buffer", buffers.deltaHU);
        apicCS.SetBuffer(apicKernels.Condense, "Delta_HV_Buffer", buffers.deltaHV);
        apicCS.SetBuffer(apicKernels.Condense, "APIC_Particle_Buffer", buffers.apicParticle);
        apicCS.SetFloat("horizontal_momentum_transfer", horizontalMomentumTransfer);
        apicCS.SetFloat("vertical_momentum_to_wave", verticalMomentumToWave);
        apicCS.DispatchIndirect(apicKernels.Condense, buffers.particleDispatchArgs);

        // =========================================================
        // Step 5: SWE 凝縮・マウスインタラクションの適用
        // 中間バッファに溜まった凝縮の衝撃やマウスの力をSWE本体のステートに反映する
        // =========================================================
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "SWE_State_Read", buffers.sweStateRead);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "SWE_State_Write", buffers.sweStateWrite);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "Delta_H_Buffer", buffers.deltaH);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "Delta_HU_Buffer", buffers.deltaHU);
        sweCS.SetBuffer(sweKernels.ApplyCondensation, "Delta_HV_Buffer", buffers.deltaHV);
        sweCS.Dispatch(sweKernels.ApplyCondensation, tgSWE_X, tgSWE_Y, 1);
        SwapSWEBuffers();

        if (mouseActive == 1) {
            sweCS.SetBuffer(sweKernels.InteractSwe, "SWE_State_Read", buffers.sweStateRead);
            sweCS.SetBuffer(sweKernels.InteractSwe, "SWE_State_Write", buffers.sweStateWrite);
            sweCS.Dispatch(sweKernels.InteractSwe, tgSWE_X, tgSWE_Y, 1);
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
            sweCS.SetBuffer(sweKernels.UpdateSwe, "SWE_State_Read", buffers.sweStateRead);
            sweCS.SetBuffer(sweKernels.UpdateSwe, "SWE_State_Write", buffers.sweStateWrite);
            sweCS.Dispatch(sweKernels.UpdateSwe, tgSWE_X, tgSWE_Y, 1);
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
        sweCS.SetBuffer(sweKernels.Sublimate, "SWE_State_Read", buffers.sweStateRead);
        sweCS.SetBuffer(sweKernels.Sublimate, "SWE_State_Write", buffers.sweStateWrite);
        sweCS.SetBuffer(sweKernels.Sublimate, "APIC_Particle_Buffer", buffers.apicParticle);
        sweCS.SetBuffer(sweKernels.Sublimate, "ParticleCounter", buffers.particleCounter);
        sweCS.Dispatch(sweKernels.Sublimate, tgSWE_X, tgSWE_Y, 1);
        SwapSWEBuffers();

        // =========================================================
        // Step 8: APIC P2G (Particle to Grid)
        // 粒子の持つ質量と運動量を、圧力計算用の3Dグリッドに転写する
        // =========================================================
        apicCS.SetBuffer(apicKernels.P2G, "ActiveParticleList_Read", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.P2G, "ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.P2G, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.P2G, "APIC_Particle_Buffer", buffers.apicParticle);
        apicCS.DispatchIndirect(apicKernels.P2G, buffers.particleDispatchArgs);

        // =========================================================
        // Step 9: APIC 速度の正規化と発散(Divergence)計算
        // グリッドの速度を質量で割り、非圧縮性流体のための発散を計算する
        // =========================================================
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.NormalizeVel, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.Dispatch(apicKernels.NormalizeVel, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetTexture(apicKernels.Divergence, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.Divergence, "APIC_Divergence", buffers.apicDivergence);
        apicCS.SetBuffer(apicKernels.Divergence, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.Divergence, "SWE_State_Read", buffers.sweStateRead);
        apicCS.Dispatch(apicKernels.Divergence, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        // =========================================================
        // Step 10: APIC 圧力計算(Jacobi-PCG法)の初期化
        // =========================================================
        buffers.pcgDotResult.SetData(new uint[] { 0 });
        apicCS.SetBuffer(apicKernels.BuildDiag, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.BuildDiag, "PCG_Precon", buffers.pcgPrecon);
        apicCS.SetTexture(apicKernels.BuildDiag, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.BuildDiag, "SWE_State_Read", buffers.sweStateRead);
        apicCS.Dispatch(apicKernels.BuildDiag, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetTexture(apicKernels.InitCG, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.InitCG, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.InitCG, "APIC_Divergence", buffers.apicDivergence);
        apicCS.SetBuffer(apicKernels.InitCG, "APIC_Pressure_Write", buffers.apicPressureWrite); 
        apicCS.SetBuffer(apicKernels.InitCG, "PCG_R", buffers.pcgR);
        apicCS.SetBuffer(apicKernels.InitCG, "PCG_P", buffers.pcgP);
        apicCS.SetBuffer(apicKernels.InitCG, "PCG_Q", buffers.pcgQ);
        apicCS.SetBuffer(apicKernels.InitCG, "PCG_Precon", buffers.pcgPrecon);
        apicCS.Dispatch(apicKernels.InitCG, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetTexture(apicKernels.DotProductPrecon, "TerrainHeightMap", terrainHeightMap);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "_ApicMassInt", buffers.apicGridMass);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "PCG_R", buffers.pcgR);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "PCG_Precon", buffers.pcgPrecon);
        apicCS.SetBuffer(apicKernels.DotProductPrecon, "PCG_DotResult", buffers.pcgDotResult);
        apicCS.Dispatch(apicKernels.DotProductPrecon, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

        apicCS.SetBuffer(apicKernels.InitRTr, "PCG_DotResult", buffers.pcgDotResult);
        apicCS.SetBuffer(apicKernels.InitRTr, "PCG_Scalars", buffers.pcgScalars);
        apicCS.Dispatch(apicKernels.InitRTr, 1, 1, 1);

        // =========================================================
        // Step 11: APIC 圧力ポアソン方程式の反復計算 (PCGループ)
        // 質量保存を満たすための圧力を求める(最も負荷が高いステップ)
        // =========================================================
        for (int i = 0; i < pcgIterations; i++)
        {
            apicCS.SetBuffer(apicKernels.ApplyA, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.ApplyA, "PCG_P", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.ApplyA, "PCG_Q", buffers.pcgQ);
            apicCS.SetTexture(apicKernels.ApplyA, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.ApplyA, "SWE_State_Read", buffers.sweStateRead);
            apicCS.Dispatch(apicKernels.ApplyA, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetTexture(apicKernels.DotProductGeneric, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "PCG_P", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "PCG_Q", buffers.pcgQ);
            apicCS.SetBuffer(apicKernels.DotProductGeneric, "PCG_DotResult", buffers.pcgDotResult);
            apicCS.Dispatch(apicKernels.DotProductGeneric, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(apicKernels.CalculateAlpha, "PCG_DotResult", buffers.pcgDotResult);
            apicCS.SetBuffer(apicKernels.CalculateAlpha, "PCG_Scalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.CalculateAlpha, 1, 1, 1);

            apicCS.SetTexture(apicKernels.UpdatePR, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.UpdatePR, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.UpdatePR, "APIC_Pressure_Write", buffers.apicPressureWrite);
            apicCS.SetBuffer(apicKernels.UpdatePR, "PCG_P", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.UpdatePR, "PCG_Q", buffers.pcgQ);
            apicCS.SetBuffer(apicKernels.UpdatePR, "PCG_R", buffers.pcgR);
            apicCS.SetBuffer(apicKernels.UpdatePR, "PCG_Scalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.UpdatePR, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(apicKernels.DotProductPrecon, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.DotProductPrecon, "PCG_R", buffers.pcgR);
            apicCS.SetBuffer(apicKernels.DotProductPrecon, "PCG_Precon", buffers.pcgPrecon);
            apicCS.SetBuffer(apicKernels.DotProductPrecon, "PCG_DotResult", buffers.pcgDotResult);
            apicCS.Dispatch(apicKernels.DotProductPrecon, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);

            apicCS.SetBuffer(apicKernels.CalculateBeta, "PCG_DotResult", buffers.pcgDotResult);
            apicCS.SetBuffer(apicKernels.CalculateBeta, "PCG_Scalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.CalculateBeta, 1, 1, 1);

            apicCS.SetTexture(apicKernels.UpdateD, "TerrainHeightMap", terrainHeightMap);
            apicCS.SetBuffer(apicKernels.UpdateD, "_ApicMassInt", buffers.apicGridMass);
            apicCS.SetBuffer(apicKernels.UpdateD, "PCG_P", buffers.pcgP);
            apicCS.SetBuffer(apicKernels.UpdateD, "PCG_R", buffers.pcgR);
            apicCS.SetBuffer(apicKernels.UpdateD, "PCG_Precon", buffers.pcgPrecon);
            apicCS.SetBuffer(apicKernels.UpdateD, "PCG_Scalars", buffers.pcgScalars);
            apicCS.Dispatch(apicKernels.UpdateD, tgAPIC_X, tgAPIC_Y, tgAPIC_Z);
        }

        // =========================================================
        // Step 12: APIC G2P (Grid to Particle)
        // 計算された圧力から速度場を修正し、粒子の速度とアフィン行列を更新する
        // =========================================================
        apicCS.SetBuffer(apicKernels.G2P, "ActiveParticleList_Read", buffers.activeParticleList);
        apicCS.SetBuffer(apicKernels.G2P, "ActiveParticleCount", buffers.activeParticleCount);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicVelIntX", buffers.apicGridVelX);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicVelIntY", buffers.apicGridVelY);
        apicCS.SetBuffer(apicKernels.G2P, "_ApicVelIntZ", buffers.apicGridVelZ);
        apicCS.SetBuffer(apicKernels.G2P, "APIC_Pressure_Write", buffers.apicPressureWrite);
        apicCS.SetBuffer(apicKernels.G2P, "APIC_Particle_Buffer", buffers.apicParticle);
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