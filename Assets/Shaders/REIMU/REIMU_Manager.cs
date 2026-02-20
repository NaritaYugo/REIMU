using UnityEngine;
using System.Runtime.InteropServices;

public class ReimuSimulation : MonoBehaviour
{
    // --- デバッグ用 ---
    private SWEGrid[] debugSweArray;
    private int[] debugSourceArray;

    // --- シェーダー ---
    public ComputeShader reimuCompute, sweSolverCompute, apicSolverCompute;

    // --- 変数 ---
    public int maxParticles = 100000;
    public int sweGridResolution = 1024;
    public float fixedPointMultiplier = 1e6f;
    public float sweTimeStep = 0.0002f; // クーラン条件(CFL)を満たすよう小さく設定
    public float gravity = 9.81f;

    public int apicGridSizeX = 1024;
    public int apicGridSizeY = 64;
    public float apicTimeStep = 0.01f;
    public int jacobiIterations = 40; // 圧力計算の精度（高いほど硬い水になる）

    public float sublimateThreshold = 3.0f; // 飛沫になる波の急激さの閾値
    public int hFlipSmoothIterations = 5; // 平滑化をかける回数

    // 描画用
    public Material sweMaterial;
    public Material particleMaterial;
    public float gridSpacing = 0.1f;
    public float particleSize = 0.05f;

    // バッファ群
    private ComputeBuffer particleBuffer, poolBuffer, 
        sweBuffer, sweMassSourceIntBuffer, sweMomentumSourceIntBuffer,
        sweBufferRK1, sweBufferRK2, sweFluxBuffer, sweBufferOriginal, 
        csArgsBuffer,
        gridMassIntBuffer, gridUIntBuffer, gridWIntBuffer, gridUBuffer, gridWBuffer,
        gridDivergenceBuffer, gridPressureBuffer, gridPressureTempBuffer, gridCellTypeBuffer,
        hFlipBuffer, hFlipBufferTemp, particleHeightIntBuffer;

    // カーネルID
    private int initParticlesKernel, dummySetupKernel, computeFluxKernel, 
        updateStateKernel, copyBufferKernel,combineRK2Kernel,
        clearGridKernel, p2gKernel, updateGridVelKernel, g2pAdvectKernel,
        markDivKernel, jacobiKernel, copyPressKernel, applyPressKernel,
        condenseKernel, addSourceKernel, sublimateKernel, 
        clearGridBuffersKernel, applyViscosityKernel, addSourceSubcycledKernel,
        clearHFlipKernel, addParticleHeightKernel, initHFlipKernel, smoothHFlipKernel, copyHFlipKernel;

    struct Particle {
        public Vector2 position;
        public Vector2 velocity;
        public float mass;
        public Vector4 C;
    }

    struct SWEGrid {
        public float h;
        public float u;
    }

    void Start()
    {
        InitializeBuffers();
        InitializeComputeShader();
        RunDummySetup(); // テストデータの生成
    }

    void InitializeBuffers()
    {
        int particleStride = 40;
        particleBuffer = new ComputeBuffer(maxParticles, particleStride);
        poolBuffer = new ComputeBuffer(maxParticles, sizeof(uint), ComputeBufferType.Append);
        
        sweBuffer = new ComputeBuffer(sweGridResolution, Marshal.SizeOf(typeof(SWEGrid)));
        sweMassSourceIntBuffer = new ComputeBuffer(sweGridResolution, sizeof(int));
        sweMomentumSourceIntBuffer = new ComputeBuffer(sweGridResolution, sizeof(int));

        sweBufferRK1 = new ComputeBuffer(sweGridResolution, Marshal.SizeOf(typeof(SWEGrid)));
        sweFluxBuffer = new ComputeBuffer(sweGridResolution + 1, sizeof(float) * 2);
        sweBufferOriginal = new ComputeBuffer(sweGridResolution, Marshal.SizeOf(typeof(SWEGrid)));
        sweBufferRK2 = new ComputeBuffer(sweGridResolution, Marshal.SizeOf(typeof(SWEGrid)));

        int totalGridCells = apicGridSizeX * apicGridSizeY;
        gridMassIntBuffer = new ComputeBuffer(totalGridCells, sizeof(int));
        gridUIntBuffer = new ComputeBuffer(totalGridCells, sizeof(int));
        gridWIntBuffer = new ComputeBuffer(totalGridCells, sizeof(int));
        gridUBuffer = new ComputeBuffer(totalGridCells, sizeof(float));
        gridWBuffer = new ComputeBuffer(totalGridCells, sizeof(float));
        gridDivergenceBuffer = new ComputeBuffer(totalGridCells, sizeof(float));
        gridPressureBuffer = new ComputeBuffer(totalGridCells, sizeof(float));
        gridPressureTempBuffer = new ComputeBuffer(totalGridCells, sizeof(float));
        gridCellTypeBuffer = new ComputeBuffer(totalGridCells, sizeof(int));

        condenseKernel = reimuCompute.FindKernel("CondenseToSWE");
        addSourceKernel = sweSolverCompute.FindKernel("AddSourceTerms");

        sweSolverCompute.SetFloat("_FixedPointMultiplier", fixedPointMultiplier);
        sublimateKernel = reimuCompute.FindKernel("SublimateToAPIC");

        hFlipBuffer = new ComputeBuffer(sweGridResolution, sizeof(float));
        hFlipBufferTemp = new ComputeBuffer(sweGridResolution, sizeof(float));
        particleHeightIntBuffer = new ComputeBuffer(sweGridResolution, sizeof(int));

        debugSweArray = new SWEGrid[sweGridResolution];
        debugSourceArray = new int[sweGridResolution];
    }

    void InitializeComputeShader()
    {
        initParticlesKernel = reimuCompute.FindKernel("InitParticles");
        dummySetupKernel = reimuCompute.FindKernel("DummySetup");

        reimuCompute.SetInt("_MaxParticles", maxParticles);
        reimuCompute.SetInt("_SWEGridResolution", sweGridResolution);
        reimuCompute.SetFloat("_FixedPointMultiplier", fixedPointMultiplier);

        reimuCompute.SetBuffer(initParticlesKernel, "_ParticleBuffer", particleBuffer);
        reimuCompute.SetBuffer(initParticlesKernel, "_PoolAppendBuffer", poolBuffer);
        
        poolBuffer.SetCounterValue(0); 
        
        int threadGroups = Mathf.CeilToInt(maxParticles / 64f);
        reimuCompute.Dispatch(initParticlesKernel, threadGroups, 1, 1);

        computeFluxKernel = sweSolverCompute.FindKernel("ComputeFlux");
        updateStateKernel = sweSolverCompute.FindKernel("UpdateState");

        sweSolverCompute.SetInt("_SWEGridResolution", sweGridResolution);
        sweSolverCompute.SetFloat("_GridSpacing", gridSpacing);
        sweSolverCompute.SetFloat("_Gravity", gravity);
        combineRK2Kernel = sweSolverCompute.FindKernel("CombineRK2");
        copyBufferKernel = sweSolverCompute.FindKernel("CopyBufferOriginal");

        clearGridKernel = apicSolverCompute.FindKernel("ClearGrid");
        p2gKernel = apicSolverCompute.FindKernel("P2G");
        updateGridVelKernel = apicSolverCompute.FindKernel("UpdateGridVelocity");
        g2pAdvectKernel = apicSolverCompute.FindKernel("G2P_Advect");
        markDivKernel = apicSolverCompute.FindKernel("MarkAndDivergence");
        jacobiKernel = apicSolverCompute.FindKernel("JacobiIteration");
        copyPressKernel = apicSolverCompute.FindKernel("CopyPressure");
        applyPressKernel = apicSolverCompute.FindKernel("ApplyPressure");

        clearGridBuffersKernel = reimuCompute.FindKernel("ClearGridBuffers");
        applyViscosityKernel = sweSolverCompute.FindKernel("ApplyArtificialViscosity");
        addSourceSubcycledKernel = sweSolverCompute.FindKernel("AddSourceTermsSubcycled");

        clearHFlipKernel = reimuCompute.FindKernel("ClearHFlip");
        addParticleHeightKernel = reimuCompute.FindKernel("AddParticleHeight");
        initHFlipKernel = reimuCompute.FindKernel("InitHFlip");
        smoothHFlipKernel = reimuCompute.FindKernel("SmoothHFlip");
        copyHFlipKernel = reimuCompute.FindKernel("CopyHFlip");

        reimuCompute.SetInt("_MaxParticles", maxParticles);
        apicSolverCompute.SetInt("_MaxParticles", maxParticles);
    }

    // テストデータの生成処理
    void RunDummySetup()
    {
        reimuCompute.SetBuffer(dummySetupKernel, "_SWEBuffer", sweBuffer);
        reimuCompute.SetBuffer(dummySetupKernel, "_ParticleBuffer", particleBuffer);
        // Consumeはプールから取り出し、AppendはActiveに入れる
        reimuCompute.SetBuffer(dummySetupKernel, "_PoolConsumeBuffer", poolBuffer);

        int threadGroups = Mathf.CeilToInt(Mathf.Max(sweGridResolution, 1000) / 64f);
        reimuCompute.Dispatch(dummySetupKernel, threadGroups, 1, 1);
    }

    void Update()
    {
        // 0. フレーム開始時にソースバッファ(湧き出し量)をリセット
        int sweGroups = Mathf.CeilToInt(sweGridResolution / 64f);
        reimuCompute.SetBuffer(clearGridBuffersKernel, "_SWEMassSourceInt", sweMassSourceIntBuffer);
        reimuCompute.SetBuffer(clearGridBuffersKernel, "_SWEMomentumSourceInt", sweMomentumSourceIntBuffer);
        reimuCompute.Dispatch(clearGridBuffersKernel, sweGroups, 1, 1);

        // ================================================
        // 1. APICの更新 (飛沫の移動と圧力計算)
        // ================================================
        UpdateAPIC();

        // ================================================
        // 2. 凝縮 (APIC to SWE) の実行
        // ================================================
            reimuCompute.SetFloat("_SWEGridSpacing", gridSpacing);
            reimuCompute.SetBuffer(condenseKernel, "_ParticleBuffer", particleBuffer);
            reimuCompute.SetBuffer(condenseKernel, "_SWEBuffer", sweBuffer);
            reimuCompute.SetBuffer(condenseKernel, "_SWEMassSourceInt", sweMassSourceIntBuffer);
            reimuCompute.SetBuffer(condenseKernel, "_SWEMomentumSourceInt", sweMomentumSourceIntBuffer);
            reimuCompute.SetBuffer(condenseKernel, "_PoolAppendBuffer", poolBuffer);
            reimuCompute.SetBuffer(condenseKernel, "_HFlipBuffer", hFlipBuffer);

            int groupMaxP = Mathf.CeilToInt(maxParticles / 64f);
            reimuCompute.Dispatch(condenseKernel, groupMaxP, 1, 1);

        // ================================================
        // 3. 場の分析と遷移判定 (人工粘性による前処理)
        // ================================================
        sweSolverCompute.SetBuffer(applyViscosityKernel, "_SWEBuffer", sweBuffer);
        sweSolverCompute.SetBuffer(applyViscosityKernel, "_SWEBufferOut", sweBufferRK1);
        sweSolverCompute.SetBuffer(applyViscosityKernel, "_HFlipBuffer", hFlipBuffer);
        sweSolverCompute.Dispatch(applyViscosityKernel, sweGroups, 1, 1);
        
        // 平滑化されたデータを元のバッファに戻す
        sweSolverCompute.SetBuffer(copyBufferKernel, "_SWEBuffer", sweBufferRK1);
        sweSolverCompute.SetBuffer(copyBufferKernel, "_SWEBufferOriginal", sweBuffer);
        sweSolverCompute.Dispatch(copyBufferKernel, sweGroups, 1, 1);

        // ================================================
        // 4. 昇華 (SWE to APIC) の実行
        // ================================================
        reimuCompute.SetFloat("_SublimateThreshold", sublimateThreshold);
        reimuCompute.SetFloat("_ParticleMass", 0.01f);
        reimuCompute.SetFloat("_SWEGridSpacing_Sub", gridSpacing);
        
        reimuCompute.SetBuffer(sublimateKernel, "_SWEBuffer", sweBuffer);
        reimuCompute.SetBuffer(sublimateKernel, "_ParticleBuffer", particleBuffer);
        reimuCompute.SetBuffer(sublimateKernel, "_PoolConsumeBuffer", poolBuffer);
        reimuCompute.SetBuffer(sublimateKernel, "_SWEMassSourceInt", sweMassSourceIntBuffer);
        reimuCompute.SetBuffer(sublimateKernel, "_SWEMomentumSourceInt", sweMomentumSourceIntBuffer);
        reimuCompute.SetBuffer(sublimateKernel, "_HFlipBuffer", hFlipBuffer);

        reimuCompute.Dispatch(sublimateKernel, groupMaxP, 1, 1);

        // ================================================
        // 5. 仮想水面 h_FLIP の計算と平滑化
        // ================================================
        reimuCompute.SetBuffer(clearHFlipKernel, "_ParticleHeightInt", particleHeightIntBuffer);
        reimuCompute.Dispatch(clearHFlipKernel, sweGroups, 1, 1);

        reimuCompute.SetBuffer(addParticleHeightKernel, "_ParticleBuffer", particleBuffer);
        reimuCompute.SetBuffer(addParticleHeightKernel, "_ParticleHeightInt", particleHeightIntBuffer);
        
        int groupsHFlip = Mathf.CeilToInt(maxParticles / 64f);
        reimuCompute.Dispatch(addParticleHeightKernel, groupsHFlip, 1, 1);

        reimuCompute.SetBuffer(initHFlipKernel, "_SWEBuffer", sweBuffer);
        reimuCompute.SetBuffer(initHFlipKernel, "_ParticleHeightInt", particleHeightIntBuffer);
        reimuCompute.SetBuffer(initHFlipKernel, "_HFlipBuffer", hFlipBuffer);
        reimuCompute.Dispatch(initHFlipKernel, sweGroups, 1, 1);

        reimuCompute.SetBuffer(smoothHFlipKernel, "_HFlipBuffer", hFlipBuffer);
        reimuCompute.SetBuffer(smoothHFlipKernel, "_HFlipBufferTemp", hFlipBufferTemp);
        reimuCompute.SetBuffer(copyHFlipKernel, "_HFlipBuffer", hFlipBuffer);
        reimuCompute.SetBuffer(copyHFlipKernel, "_HFlipBufferTemp", hFlipBufferTemp);

        for (int i = 0; i < hFlipSmoothIterations; i++) {
            reimuCompute.Dispatch(smoothHFlipKernel, sweGroups, 1, 1);
            reimuCompute.Dispatch(copyHFlipKernel, sweGroups, 1, 1);
        }

        // ================================================
        // 6. SWEソルバの更新 (サブサイクリング)
        // ================================================
        int subSteps = Mathf.CeilToInt(apicTimeStep / sweTimeStep);
        sweSolverCompute.SetFloat("_SubSteps", (float)subSteps);
        sweSolverCompute.SetFloat("_DeltaTime", sweTimeStep);

        int groupsFlux = Mathf.CeilToInt((sweGridResolution + 1) / 64f);

        for (int step = 0; step < subSteps; step++)
        {
            // 6.1 ソース項（湧き出し）として系に加える
            sweSolverCompute.SetBuffer(addSourceSubcycledKernel, "_SWEBuffer", sweBuffer);
            sweSolverCompute.SetBuffer(addSourceSubcycledKernel, "_SWEMassSourceInt", sweMassSourceIntBuffer);
            sweSolverCompute.SetBuffer(addSourceSubcycledKernel, "_SWEMomentumSourceInt", sweMomentumSourceIntBuffer);
            sweSolverCompute.Dispatch(addSourceSubcycledKernel, sweGroups, 1, 1);

            // 6.2 - 6.4 TVD-RK2 による時間積分
            sweSolverCompute.SetBuffer(copyBufferKernel, "_SWEBuffer", sweBuffer);
            sweSolverCompute.SetBuffer(copyBufferKernel, "_SWEBufferOriginal", sweBufferOriginal);
            sweSolverCompute.Dispatch(copyBufferKernel, sweGroups, 1, 1);

            sweSolverCompute.SetBuffer(computeFluxKernel, "_SWEBuffer", sweBuffer);
            sweSolverCompute.SetBuffer(computeFluxKernel, "_FluxBuffer", sweFluxBuffer);
            sweSolverCompute.Dispatch(computeFluxKernel, groupsFlux, 1, 1);

            sweSolverCompute.SetBuffer(updateStateKernel, "_SWEBuffer", sweBuffer);
            sweSolverCompute.SetBuffer(updateStateKernel, "_FluxBuffer", sweFluxBuffer);
            sweSolverCompute.SetBuffer(updateStateKernel, "_SWEBufferOut", sweBufferRK1);
            sweSolverCompute.Dispatch(updateStateKernel, sweGroups, 1, 1);

            sweSolverCompute.SetBuffer(computeFluxKernel, "_SWEBuffer", sweBufferRK1);
            sweSolverCompute.SetBuffer(computeFluxKernel, "_FluxBuffer", sweFluxBuffer);
            sweSolverCompute.Dispatch(computeFluxKernel, groupsFlux, 1, 1);

            sweSolverCompute.SetBuffer(updateStateKernel, "_SWEBuffer", sweBufferRK1);
            sweSolverCompute.SetBuffer(updateStateKernel, "_FluxBuffer", sweFluxBuffer);
            sweSolverCompute.SetBuffer(updateStateKernel, "_SWEBufferOut", sweBufferRK2); 
            sweSolverCompute.Dispatch(updateStateKernel, sweGroups, 1, 1);

            sweSolverCompute.SetBuffer(combineRK2Kernel, "_SWEBufferOriginal", sweBufferOriginal);
            sweSolverCompute.SetBuffer(combineRK2Kernel, "_SWEBufferOut", sweBufferRK2); 
            sweSolverCompute.SetBuffer(combineRK2Kernel, "_SWEBuffer", sweBuffer);      
            sweSolverCompute.Dispatch(combineRK2Kernel, sweGroups, 1, 1);
        }

        // --- ここからデバッグ用ログ出力 ---
        // 震源地付近（X=5.0あたり、インデックスでいうと50付近）のデータを取得
        int targetIndex = 50; 

        // GPUからメインメモリへデータを読み戻す（※デバッグ用なので重いです）
        sweBuffer.GetData(debugSweArray);
        sweMassSourceIntBuffer.GetData(debugSourceArray);

        float h = debugSweArray[targetIndex].h;
        float u = debugSweArray[targetIndex].u;
        
        // アトミック加算された整数値を元の浮動小数点(質量)に戻す
        float sourceMass = (float)debugSourceArray[targetIndex] / fixedPointMultiplier;

        // 異常値（波が10メートルを超える、または流速が異常、またはNaN）を検知したらログを出す
        if (h > 10.0f || Mathf.Abs(u) > 10.0f || float.IsNaN(h) || float.IsNaN(u)) {
            Debug.LogWarning($"【爆発検知】Index: {targetIndex} | h: {h:F4} | u: {u:F4} | 投入予定の質量(Source): {sourceMass:F6}");
            
            // エディタを一時停止させて状態を確認しやすくする
            Debug.Break(); 
        }
    }

    void UpdateAPIC()
    {
        apicSolverCompute.SetInt("_GridSizeX", apicGridSizeX);
        apicSolverCompute.SetInt("_GridSizeY", apicGridSizeY);
        apicSolverCompute.SetFloat("_GridSpacing", gridSpacing);
        apicSolverCompute.SetFloat("_DeltaTime", apicTimeStep);
        apicSolverCompute.SetFloat("_FixedPointMultiplier", fixedPointMultiplier);
        apicSolverCompute.SetFloat("_Gravity", gravity);

        int gridGroups = Mathf.CeilToInt((apicGridSizeX * apicGridSizeY) / 64f);
        int particleGroups = Mathf.CeilToInt(maxParticles / 64f);

        // 1. Clear Grid
        apicSolverCompute.SetBuffer(clearGridKernel, "_GridMassInt", gridMassIntBuffer);
        apicSolverCompute.SetBuffer(clearGridKernel, "_GridUInt", gridUIntBuffer);
        apicSolverCompute.SetBuffer(clearGridKernel, "_GridWInt", gridWIntBuffer);
        apicSolverCompute.SetBuffer(clearGridKernel, "_GridU", gridUBuffer);
        apicSolverCompute.SetBuffer(clearGridKernel, "_GridW", gridWBuffer);
        apicSolverCompute.Dispatch(clearGridKernel, gridGroups, 1, 1);

        // 2. P2G
        apicSolverCompute.SetBuffer(p2gKernel, "_ParticleBuffer", particleBuffer);
        apicSolverCompute.SetBuffer(p2gKernel, "_GridMassInt", gridMassIntBuffer);
        apicSolverCompute.SetBuffer(p2gKernel, "_GridUInt", gridUIntBuffer);
        apicSolverCompute.SetBuffer(p2gKernel, "_GridWInt", gridWIntBuffer);
        apicSolverCompute.Dispatch(p2gKernel, particleGroups, 1, 1);

        // 3. Update Grid Velocity
        apicSolverCompute.SetBuffer(updateGridVelKernel, "_GridMassInt", gridMassIntBuffer);
        apicSolverCompute.SetBuffer(updateGridVelKernel, "_GridUInt", gridUIntBuffer);
        apicSolverCompute.SetBuffer(updateGridVelKernel, "_GridWInt", gridWIntBuffer);
        apicSolverCompute.SetBuffer(updateGridVelKernel, "_GridU", gridUBuffer);
        apicSolverCompute.SetBuffer(updateGridVelKernel, "_GridW", gridWBuffer);
        apicSolverCompute.Dispatch(updateGridVelKernel, gridGroups, 1, 1);

        // 3.1 セル判定と発散(Divergence)計算
        apicSolverCompute.SetBuffer(markDivKernel, "_HFlipBuffer", hFlipBuffer);

        apicSolverCompute.SetBuffer(markDivKernel, "_GridMassInt", gridMassIntBuffer);
        apicSolverCompute.SetBuffer(markDivKernel, "_GridU", gridUBuffer);
        apicSolverCompute.SetBuffer(markDivKernel, "_GridW", gridWBuffer);
        apicSolverCompute.SetBuffer(markDivKernel, "_GridDivergence", gridDivergenceBuffer);
        apicSolverCompute.SetBuffer(markDivKernel, "_GridPressure", gridPressureBuffer);
        apicSolverCompute.SetBuffer(markDivKernel, "_GridPressureTemp", gridPressureTempBuffer);
        apicSolverCompute.SetBuffer(markDivKernel, "_GridCellType", gridCellTypeBuffer);
        apicSolverCompute.Dispatch(markDivKernel, gridGroups, 1, 1);

        // 3.2 圧力の計算（ヤコビ反復法を複数回まわす）
        apicSolverCompute.SetBuffer(jacobiKernel, "_GridCellType", gridCellTypeBuffer);
        apicSolverCompute.SetBuffer(jacobiKernel, "_GridDivergence", gridDivergenceBuffer);
        apicSolverCompute.SetBuffer(jacobiKernel, "_GridPressure", gridPressureBuffer);
        apicSolverCompute.SetBuffer(jacobiKernel, "_GridPressureTemp", gridPressureTempBuffer);

        apicSolverCompute.SetBuffer(copyPressKernel, "_GridPressure", gridPressureBuffer);
        apicSolverCompute.SetBuffer(copyPressKernel, "_GridPressureTemp", gridPressureTempBuffer);

        for (int i = 0; i < jacobiIterations; i++) {
            apicSolverCompute.Dispatch(jacobiKernel, gridGroups, 1, 1);
            apicSolverCompute.Dispatch(copyPressKernel, gridGroups, 1, 1);
        }

        // 3.3 圧力を速度に適用（非圧縮性の担保）
        apicSolverCompute.SetBuffer(applyPressKernel, "_GridCellType", gridCellTypeBuffer);
        apicSolverCompute.SetBuffer(applyPressKernel, "_GridPressure", gridPressureBuffer);
        apicSolverCompute.SetBuffer(applyPressKernel, "_GridU", gridUBuffer);
        apicSolverCompute.SetBuffer(applyPressKernel, "_GridW", gridWBuffer);
        apicSolverCompute.Dispatch(applyPressKernel, gridGroups, 1, 1);

        // 4. G2P & Advect
        apicSolverCompute.SetBuffer(g2pAdvectKernel, "_ParticleBuffer", particleBuffer);
        apicSolverCompute.SetBuffer(g2pAdvectKernel, "_GridU", gridUBuffer);
        apicSolverCompute.SetBuffer(g2pAdvectKernel, "_GridW", gridWBuffer);
        apicSolverCompute.Dispatch(g2pAdvectKernel, particleGroups, 1, 1);
    }

    // カメラのレンダリング後に描画を実行
    void OnRenderObject()
    {
        // SWE描画
        sweMaterial.SetBuffer("_HFlipBuffer", hFlipBuffer);
        sweMaterial.SetFloat("_GridSpacing", gridSpacing);
        sweMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, sweGridResolution);

        // APIC描画 (DrawProceduralIndirectNow をやめて直描きに)
        particleMaterial.SetBuffer("_ParticleBuffer", particleBuffer);
        particleMaterial.SetFloat("_ParticleSize", particleSize);
        particleMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, maxParticles);
    }

    

    void OnDestroy()
    {
        particleBuffer?.Release();
        poolBuffer?.Release();
        sweBuffer?.Release();
        sweMassSourceIntBuffer?.Release();
        sweMomentumSourceIntBuffer?.Release();
        csArgsBuffer?.Release();
        sweBufferRK1?.Release();
        sweBufferRK2?.Release();
        sweFluxBuffer?.Release();
        sweBufferOriginal?.Release();
        gridMassIntBuffer?.Release();
        gridUIntBuffer?.Release();
        gridWIntBuffer?.Release();
        gridUBuffer?.Release();
        gridWBuffer?.Release();
        gridDivergenceBuffer?.Release();
        gridPressureBuffer?.Release();
        gridPressureTempBuffer?.Release();
        gridCellTypeBuffer?.Release();
        hFlipBuffer?.Release();
        hFlipBufferTemp?.Release();
        particleHeightIntBuffer?.Release();
    }
}