using UnityEngine;

public class LOD2DManager : MonoBehaviour
{
    public ComputeShader compute;
    public Material displayMaterial;

    const int RES = 64;
    const int PARTICLES = 100000;
    const int maxIter = 30;
    const float tolerance = 1e-9f;
    const int iteration = 32;

    struct Particle
    {
        public Vector2 position;
        public Vector2 velocity;
    }

    ComputeBuffer particleBuf,velXBuf,velYBuf,weightXBuf,weightYBuf,dotBuf,cgVarsBuf;
    RenderTexture velXTex,velYTex,velXOldTex,velYOldTex,presTex,divTex,resTex,dirTex,apTex,preconTex,typeTex;

    void Start()
    {
        // グリッド解像度の設定
        int nx = RES;
        int ny = RES;

        // --- 1. StructuredBuffer の生成 ---
        // 粒子バッファ
        particleBuf = new ComputeBuffer(
            PARTICLES,
            16,
            ComputeBufferType.Structured
        );

        // int型のアトミック加算用バッファ (SCALE倍して保存するため)
        // VelX は (nx + 1) * ny
        velXBuf = new ComputeBuffer((nx + 1) * ny, sizeof(int));
        // VelY は nx * (ny + 1)
        velYBuf = new ComputeBuffer(nx * (ny + 1), sizeof(int));
        // Weight / DotResult
        weightXBuf = new ComputeBuffer(nx * ny, sizeof(int));
        weightYBuf = new ComputeBuffer(nx * ny, sizeof(int));
        dotBuf = new ComputeBuffer(1, sizeof(int));
        //alpha, beta, rTr, rTrOld, dAd
        cgVarsBuf = new ComputeBuffer(5, sizeof(float));

        // --- 2. RenderTexture (Gridデータ) の生成 ---
        RenderTexture CreateGridTex(int w, int h, RenderTextureFormat format) {
            RenderTexture tex = new RenderTexture(w, h, 0, format);
            tex.enableRandomWrite = true;
            tex.filterMode = FilterMode.Point;
            tex.Create();
            return tex;
        }

        // 速度 (float)
        velXTex = CreateGridTex(nx + 1, ny, RenderTextureFormat.RFloat);
        velYTex = CreateGridTex(nx, ny + 1, RenderTextureFormat.RFloat);
        velXOldTex = CreateGridTex(nx + 1, ny, RenderTextureFormat.RFloat);
        velYOldTex = CreateGridTex(nx, ny + 1, RenderTextureFormat.RFloat);

        // 圧力・発散・CG用バッファ (セル中心)
        presTex = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        divTex  = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        resTex  = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        dirTex  = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        apTex   = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        preconTex = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);

        // セルタイプ (int)
        typeTex = CreateGridTex(nx, ny, RenderTextureFormat.RInt);

        // --- 3. 粒子の初期配置 ---
        InitParticles();
    }

    void InitParticles()
    {
        Particle[] p = new Particle[PARTICLES];
        for (int i = 0; i < PARTICLES; i++)
        {
            // 0.1 ～ 0.9 の間にランダムに配置（0.5付近）
            p[i].position = new Vector2(0.5f, 0.5f) + Random.insideUnitCircle * 0.3f;
            p[i].velocity = Vector2.zero;
        }
        particleBuf.SetData(p);
    }

    void Update()
    {
        // グループ数の計算
        int threadGroups = Mathf.CeilToInt((float)PARTICLES / 64f);
        int gridGroups = Mathf.CeilToInt((float)RES / 8f);
        int gridGroupsX = Mathf.CeilToInt((float)(RES + 1) / 8f); // VelX用
        int gridGroupsY = Mathf.CeilToInt((float)(RES + 1) / 8f); // VelY用

        // 定数セット
        compute.SetFloat("dt", 0.002f);
        compute.SetFloat("h", 1.0f / (float)RES);
        compute.SetInt("nx", RES);
        compute.SetInt("ny", RES);
        compute.SetInt("particleCount", PARTICLES);
        compute.SetFloat("width", 1.0f);
        compute.SetFloat("height", 1.0f);
        compute.SetFloat("rho", 1);
        compute.SetFloat("gravity", -9.8f);
        compute.SetFloat("flipRatio", 0.99f);
        compute.SetInt("maxIter", maxIter);
        compute.SetFloat("tolerance", tolerance);

        // 1. Grid Clear
        int kCla = compute.FindKernel("ClearGrid");
        compute.SetBuffer(kCla, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kCla, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kCla, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kCla, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kCla, "_Pressure", presTex);
        compute.SetTexture(kCla, "_Divergence", divTex);
        compute.Dispatch(kCla, gridGroups, gridGroups, 1);

        // 2. Mark Cell Types
        int kCellType = compute.FindKernel("MarkCellTypes");
        compute.SetTexture(kCellType, "_GridType", typeTex);
        compute.Dispatch(kCellType, RES / 8, RES / 8, 1);

        // 3. P2G
        int kP2G = compute.FindKernel("P2G");
        compute.SetBuffer(kP2G, "_Particles", particleBuf);
        compute.SetBuffer(kP2G, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kP2G, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kP2G, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kP2G, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kP2G, "_GridType", typeTex);
        compute.Dispatch(kP2G, threadGroups, 1, 1);

        // 4. Normalize
        int kNormX = compute.FindKernel("NormalizeVelX");
        compute.SetBuffer(kNormX, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kNormX, "_WeightXBuffer", weightXBuf);
        compute.SetTexture(kNormX, "_VelX", velXTex);
        compute.Dispatch(kNormX, gridGroupsX, gridGroups, 1);

        int kNormY = compute.FindKernel("NormalizeVelY");
        compute.SetBuffer(kNormY, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kNormY, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kNormY, "_VelY", velYTex);
        compute.Dispatch(kNormY, gridGroups, gridGroupsY, 1);

        // 6. Backup Velocity
        Graphics.CopyTexture(velXTex, velXOldTex);
        Graphics.CopyTexture(velYTex, velYOldTex);

        // 5. External Forces & Boundary
        int kGrav = compute.FindKernel("AddGravity");
        compute.SetTexture(kGrav, "_VelY", velYTex);
        compute.Dispatch(kGrav, RES / 8, RES / 8, 1);

        int kBound = compute.FindKernel("ApplyBoundCond");
        compute.SetTexture(kBound, "_VelX", velXTex);
        compute.SetTexture(kBound, "_VelY", velYTex);
        compute.SetTexture(kBound, "_GridType", typeTex);
        compute.Dispatch(kBound, RES / 8, RES / 8, 1);

        // 7. Divergence & Build Diagonal
        int kDiv = compute.FindKernel("Divergence");
        compute.SetTexture(kDiv, "_VelX", velXTex);
        compute.SetTexture(kDiv, "_VelY", velYTex);
        compute.SetTexture(kDiv, "_Divergence", divTex);
        compute.SetTexture(kDiv, "_GridType", typeTex);
        compute.Dispatch(kDiv, RES / 8, RES / 8, 1);

        int kDiag = compute.FindKernel("BuildDiag");
        compute.SetTexture(kDiag, "_GridType", typeTex);
        compute.SetTexture(kDiag, "_Precon", preconTex);
        compute.Dispatch(kDiag, RES / 8, RES / 8, 1);

        // 8. Conjugate Gradient Loop
        SolvePressure(gridGroups);

        // 9. Projection
        int kProj = compute.FindKernel("Projection");
        compute.SetTexture(kProj, "_Pressure", presTex);
        compute.SetTexture(kProj, "_GridType", typeTex);
        compute.SetTexture(kProj, "_VelX", velXTex);
        compute.SetTexture(kProj, "_VelY", velYTex);
        compute.Dispatch(kProj, RES / 8, RES / 8, 1);

        // 10. G2P, Advection
        int kG2PAdv = compute.FindKernel("G2P_Advection");
        compute.SetBuffer(kG2PAdv, "_Particles", particleBuf);
        compute.SetTexture(kG2PAdv, "_VelX", velXTex);
        compute.SetTexture(kG2PAdv, "_VelY", velYTex);
        compute.SetTexture(kG2PAdv, "_VelXOld", velXOldTex);
        compute.SetTexture(kG2PAdv, "_VelYOld", velYOldTex);
        compute.SetTexture(kG2PAdv, "_GridType", typeTex);
        compute.Dispatch(kG2PAdv, threadGroups, 1, 1);
    }

    void SolvePressure(int groups)
    {
        // --- A. 初期化フェーズ ---
        // 1. dotResult と CGVars をリセット
        dotBuf.SetData(new int[] { 0 });
        // CGVars のリセット (rTr, dAd, alpha, beta, rTrOld すべて 0)
        cgVarsBuf.SetData(new float[5]); 

        // 2. InitCG
        int kInit = compute.FindKernel("InitCG");
        compute.SetTexture(kInit, "_Pressure", presTex);
        compute.SetTexture(kInit, "_Divergence", divTex);
        compute.SetTexture(kInit, "_Residual", resTex);
        compute.SetTexture(kInit, "_SearchDir", dirTex);
        compute.SetTexture(kInit, "_Precon", preconTex);
        compute.SetTexture(kInit, "_GridType", typeTex);
        compute.SetTexture(kInit, "_Ap", apTex);
        compute.Dispatch(kInit, groups, groups, 1);

        // 3. 初回の rTr 計算
        int kDotPrecon = compute.FindKernel("DotProductPreconditioned");
        compute.SetTexture(kDotPrecon, "_Residual", resTex);
        compute.SetTexture(kDotPrecon, "_Precon", preconTex);
        compute.SetBuffer(kDotPrecon, "_DotResult", dotBuf);
        compute.SetTexture(kDotPrecon, "_GridType", typeTex);
        compute.Dispatch(kDotPrecon, groups, groups, 1);

        // 4. 計算されたドット積を CGVars[0] (rTr) に格納
        int kStoreRTr = compute.FindKernel("ComputeInitialRTr");
        compute.SetBuffer(kStoreRTr, "_DotResult", dotBuf);
        compute.SetBuffer(kStoreRTr, "_CGVars", cgVarsBuf);
        compute.Dispatch(kStoreRTr, 1, 1, 1);

        // --- B. CG反復フェーズ ---
        for (int i = 0; i < iteration; i++)
        {
            // 1. Ap = A * d
            int kap = compute.FindKernel("ApplyA");
            compute.SetTexture(kap, "_GridType", typeTex);
            compute.SetTexture(kap, "_Ap", apTex);
            compute.SetTexture(kap, "_SearchDir", dirTex);
            compute.SetTexture(kap, "_Precon", preconTex);
            compute.Dispatch(kap, groups, groups, 1);

            // 2. dAd (d * Ap) の計算準備
            dotBuf.SetData(new int[] { 0 }); 
            int kDotGen = compute.FindKernel("DotProductGeneric");
            compute.SetTexture(kDotGen, "_TexA", dirTex);
            compute.SetTexture(kDotGen, "_TexB", apTex);
            compute.SetBuffer(kDotGen, "_DotResult", dotBuf);
            compute.SetTexture(kDotGen, "_GridType", typeTex);
            compute.Dispatch(kDotGen, groups, groups, 1);

            // 3. alpha = rTr / dAd の計算
            int kAlpha = compute.FindKernel("CalculateAlpha");
            compute.SetBuffer(kAlpha, "_DotResult", dotBuf);
            compute.SetBuffer(kAlpha, "_CGVars", cgVarsBuf);
            compute.Dispatch(kAlpha, 1, 1, 1);

            // 4. Update P, R (バッファからAlphaを直接読む)
            int kUpdatePR = compute.FindKernel("UpdatePR");
            compute.SetBuffer(kUpdatePR, "_CGVars", cgVarsBuf);
            compute.SetTexture(kUpdatePR, "_GridType", typeTex);
            compute.SetTexture(kUpdatePR, "_Pressure", presTex);
            compute.SetTexture(kUpdatePR, "_SearchDir", dirTex);
            compute.SetTexture(kUpdatePR, "_Residual", resTex);
            compute.SetTexture(kUpdatePR, "_Ap", apTex);
            compute.Dispatch(kUpdatePR, RES / 8, RES / 8, 1);

            // 5. 新しい rTr (rTr_new) の計算準備
            dotBuf.SetData(new int[] { 0 });
            compute.SetTexture(kDotPrecon, "_Residual", resTex);
            compute.SetTexture(kDotPrecon, "_Precon", preconTex);
            compute.Dispatch(kDotPrecon, groups, groups, 1);

            // 6. beta = rTr_new / rTr_old の計算
            int kBeta = compute.FindKernel("CalculateBeta");
            compute.SetBuffer(kBeta, "_DotResult", dotBuf);
            compute.SetBuffer(kBeta, "_CGVars", cgVarsBuf);
            compute.Dispatch(kBeta, 1, 1, 1);

            // 7. Update D (バッファからBetaを直接読む)
            int kUpdateD = compute.FindKernel("UpdateD");
            compute.SetBuffer(kUpdateD, "_CGVars", cgVarsBuf);
            compute.SetTexture(kUpdateD, "_GridType", typeTex);
            compute.SetTexture(kUpdateD, "_SearchDir", dirTex);
            compute.SetTexture(kUpdateD, "_Residual", resTex);
            compute.SetTexture(kUpdateD, "_Precon", preconTex);
            compute.Dispatch(kUpdateD, groups, groups, 1);
        }
    }

    void OnRenderObject()
    {
        displayMaterial.SetInt("_Res", RES);
        if (particleBuf == null) return;

        // 1. マテリアルにデータをセット
        displayMaterial.SetBuffer("_Particles", particleBuf);
        displayMaterial.SetVector("_ObjPos", transform.position);
        displayMaterial.SetVector("_ObjScale", transform.localScale);
        displayMaterial.SetInt("_Res", RES);

        // 2. 描画実行 (Pass 0 を使用)
        displayMaterial.SetPass(0);
        
        // 1粒子あたり6頂点（三角形2枚）で Quad を作る
        Graphics.DrawProceduralNow(MeshTopology.Triangles, PARTICLES * 6);
    }

    void OnDestroy()
    {
        particleBuf?.Release();
        velXBuf?.Release();
        velYBuf?.Release();
        weightXBuf?.Release();
        weightYBuf?.Release();
        dotBuf?.Release();
        cgVarsBuf?.Release();

        if (velXTex) velXTex.Release();
        if (velYTex) velYTex.Release();
        if (velXOldTex) velXOldTex.Release();
        if (velYOldTex) velYOldTex.Release();
        if (presTex) presTex.Release();
        if (divTex) divTex.Release();
        if (resTex) resTex.Release();
        if (dirTex) dirTex.Release();
        if (apTex) apTex.Release();
        if (preconTex) preconTex.Release();
        if (typeTex) typeTex.Release();

    }
}
