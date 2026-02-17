using UnityEngine;

public class LOD2DManager : MonoBehaviour
{
    public ComputeShader compute;
    public Material displayMaterial;
    public MeshRenderer backgroundRenderer;

    const float dt = 0.01f;
    float width, height; //テクスチャのスケールを取得する
    const int RES = 32; // 長さ1.0fあたりのセル数
    int nx, ny; // セル数 nx = (int)(RES * width)
    float aspect; // アス比 (float)ny / (float)nx
    const float h = 1.0f / (float)RES; // 1セルの幅
    const float gravity = -9.8f;
    const float flipRatio = 0.99f; //最大値
    const float rho = 1.0f; //密度
    const int PARTICLES = 100000; //平衡時の粒子数
    const int maxIter = 15;
    const float tolerance = 1e-9f;
    const int iteration = 20;
    const float flipPos = 0.2f; //画面中央から画面端までの長さを1としたときの割合
    const float picPos = 0.4f;
    const float eulerPos = 0.6f; 
    const float swePos = 0.8f; 
    const float fftPos = 0.9f; 
    int[] countArray;
    int currentCount;
    float recoveryFactor; //粒子の過不足　-1.0～1.0

    // 0～flipPos: FLIP
    // flipPos～picPos: FLIP/PIC
    // picPos～eulerPos: PIC/Euler
    // eulerPos～swePos: Euler/SWE
    // swePos～fftPos: SWE/FFT
    // fftPos～: FFT

    struct Particle
    {
        public Vector2 position;
        public Vector2 velocity;
        public int active; // boolの代わりにintを使用 (0: false, 1: true)
        public int padding;         // 4 bytes (パディング)
    }

    ComputeBuffer particleBuf,velXBuf,velYBuf,weightXBuf,weightYBuf,dotBuf,cgVarsBuf,countBuf,activeListBuf,deadPoolBuf;
    RenderTexture velXTex,velYTex,velXOldTex,velYOldTex,presTex,divTex,resTex,dirTex,apTex,preconTex,typeTex;

    void Start()
    {
        // 1. パラメータ計算
        width = transform.localScale.x;
        height = transform.localScale.y;
        nx = Mathf.RoundToInt(width * RES);
        ny = Mathf.RoundToInt(height * RES);
        aspect = (float)ny / (float)nx;

        // 2. バッファ生成
        int maxParticles = PARTICLES * 2;

        particleBuf = new ComputeBuffer(maxParticles, 24, ComputeBufferType.Structured);

        activeListBuf = new ComputeBuffer(maxParticles, sizeof(int), ComputeBufferType.Append);
        deadPoolBuf = new ComputeBuffer(maxParticles, sizeof(int), ComputeBufferType.Append);
        countBuf = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        countArray = new int[1];

        // グリッド用バッファ生成
        velXBuf = new ComputeBuffer((nx + 1) * ny, sizeof(int));
        velYBuf = new ComputeBuffer(nx * (ny + 1), sizeof(int));
        weightXBuf = new ComputeBuffer((nx + 1) * ny, sizeof(int));
        weightYBuf = new ComputeBuffer(nx * (ny + 1), sizeof(int));
        dotBuf = new ComputeBuffer(1, sizeof(int));
        cgVarsBuf = new ComputeBuffer(5, sizeof(float));

        // RenderTexture (Gridデータ) の生成
        RenderTexture CreateGridTex(int w, int h, RenderTextureFormat format) {
            RenderTexture tex = new RenderTexture(w, h, 0, format);
            tex.enableRandomWrite = true;
            tex.filterMode = FilterMode.Point;
            tex.Create();
            return tex;
        }

        // 3. RenderTexture生成
        velXTex = CreateGridTex(nx + 1, ny, RenderTextureFormat.RFloat);
        velYTex = CreateGridTex(nx, ny + 1, RenderTextureFormat.RFloat);
        velXOldTex = CreateGridTex(nx + 1, ny, RenderTextureFormat.RFloat);
        velYOldTex = CreateGridTex(nx, ny + 1, RenderTextureFormat.RFloat);
        presTex = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        divTex  = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        resTex  = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        dirTex  = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        apTex   = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        preconTex = CreateGridTex(nx, ny, RenderTextureFormat.RFloat);
        typeTex = CreateGridTex(nx, ny, RenderTextureFormat.RInt);

        displayMaterial = backgroundRenderer.material;

        // DeadPoolのカウンタをリセット
        deadPoolBuf.SetCounterValue(0);

        // カーネル検索と実行
        int kInitAll = compute.FindKernel("InitAllParticles");
        compute.SetBuffer(kInitAll, "_Particles", particleBuf);
        compute.SetBuffer(kInitAll, "_DeadPoolAppend", deadPoolBuf);

        // maxParticles 分回す
        int threadGroups = Mathf.CeilToInt((float)maxParticles / 64f);
        compute.Dispatch(kInitAll, threadGroups, 1, 1);

        currentCount = PARTICLES;
    }

    void Update()
    {
        recoveryFactor = ((float)PARTICLES - (float)currentCount) / (float)PARTICLES;

        // グループ数の計算
        int threadGroups = Mathf.CeilToInt((float)PARTICLES / 64f);
        int groupX = Mathf.CeilToInt((float)nx / 8f);
        int groupY = Mathf.CeilToInt((float)ny / 8f);
        int groupVelX = Mathf.CeilToInt((float)(nx + 1) / 8f);
        int groupVelY = Mathf.CeilToInt((float)(ny + 1) / 8f);

        // 定数セット
        compute.SetFloat("dt", dt);
        compute.SetFloat("h", h);
        compute.SetInt("nx", nx);
        compute.SetInt("ny", ny);
        compute.SetInt("maxParticles", PARTICLES * 2);
        compute.SetInt("particleCount", PARTICLES);
        compute.SetFloat("width", width);
        compute.SetFloat("height", height);
        compute.SetFloat("rho", rho);
        compute.SetFloat("gravity", gravity);
        compute.SetFloat("flipRatio", flipRatio);
        compute.SetInt("maxIter", maxIter);
        compute.SetFloat("tolerance", tolerance);
        compute.SetFloat("_flipPos", flipPos);
        compute.SetFloat("_picPos", picPos);
        compute.SetFloat("_eulerPos", eulerPos);
        compute.SetFloat("_swePos", swePos);
        compute.SetFloat("_fftPos", fftPos);
        compute.SetInt("_TargetCount",PARTICLES);
        compute.SetFloat("_aspectRatio", aspect);
        compute.SetFloat("_Time", Time.time);
        compute.SetFloat("_RecoveryFactor", recoveryFactor);

        activeListBuf.SetCounterValue(0);
        compute.SetInt("_CurrentCount", currentCount);

        // 1. Grid Clear & Mark Cell Types
        int kCla = compute.FindKernel("ClearGrid");
        compute.SetBuffer(kCla, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kCla, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kCla, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kCla, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kCla, "_Pressure", presTex);
        compute.SetTexture(kCla, "_Divergence", divTex);
        compute.SetTexture(kCla, "_GridType", typeTex);
        compute.Dispatch(kCla, groupX, groupY, 1);

        // 2. P2G
        int kP2G = compute.FindKernel("P2G");
        compute.SetBuffer(kP2G, "_Particles", particleBuf);
        compute.SetBuffer(kP2G, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kP2G, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kP2G, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kP2G, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kP2G, "_GridType", typeTex);
        compute.Dispatch(kP2G, threadGroups, 1, 1);

        // 3. Normalize
        int kNorm = compute.FindKernel("NormalizeVel");
        compute.SetBuffer(kNorm, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kNorm, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kNorm, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kNorm, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kNorm, "_VelX", velXTex);
        compute.SetTexture(kNorm, "_VelY", velYTex);
        compute.Dispatch(kNorm, groupVelX, groupVelY, 1);

        // 4. Backup Velocity
        Graphics.CopyTexture(velXTex, velXOldTex);
        Graphics.CopyTexture(velYTex, velYOldTex);

        // 5. External Forces & Boundary
        int kGrav = compute.FindKernel("AddGravity");
        compute.SetTexture(kGrav, "_VelY", velYTex);
        compute.Dispatch(kGrav, groupVelX, groupVelY, 1);

        int kBound = compute.FindKernel("ApplyBoundCond");
        compute.SetTexture(kBound, "_VelX", velXTex);
        compute.SetTexture(kBound, "_VelY", velYTex);
        compute.SetTexture(kBound, "_GridType", typeTex);
        compute.Dispatch(kBound, groupVelX, groupVelY, 1);

        // 6.マウス判定
        if (Input.GetMouseButton(0) || Input.GetMouseButton(1))
            {
                
                Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit))
                {
                    
                    // hit.textureCoord は 0.0～1.0 なので、シミュレーションサイズを掛ける
                    Vector2 mousePos = new Vector2(hit.textureCoord.x * width, hit.textureCoord.y * height);
                    int kMouse = compute.FindKernel("MouseInteraction");
                    compute.SetInt("_MouseClick", Input.GetMouseButton(0) ? 1 : 2); // 1:左(吸い込み) 2:右(弾く)
                    compute.SetVector("_MousePos", mousePos);
                    compute.SetTexture(kMouse, "_VelX", velXTex);
                    compute.SetTexture(kMouse, "_VelY", velYTex);
                    compute.SetTexture(kMouse, "_GridType", typeTex);
                    compute.Dispatch(kMouse, groupVelX, groupVelY, 1);
                }
            }
            else
            {
                compute.SetInt("_MouseClick", 0);
            }

        // 7. Divergence & Build Diagonal
        int kDiv = compute.FindKernel("Divergence");
        compute.SetTexture(kDiv, "_VelX", velXTex);
        compute.SetTexture(kDiv, "_VelY", velYTex);
        compute.SetBuffer(kDiv, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kDiv, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kDiv, "_Divergence", divTex);
        compute.SetTexture(kDiv, "_GridType", typeTex);
        compute.Dispatch(kDiv, groupVelX, groupVelY, 1);

        int kDiag = compute.FindKernel("BuildDiag");
        compute.SetTexture(kDiag, "_GridType", typeTex);
        compute.SetTexture(kDiag, "_Precon", preconTex);
        compute.Dispatch(kDiag, groupX, groupY, 1);

        // 8. Conjugate Gradient Loop
        SolvePressure(groupX, groupY);

        // 9. Projection
        int kProj = compute.FindKernel("Projection");
        compute.SetTexture(kProj, "_Pressure", presTex);
        compute.SetTexture(kProj, "_GridType", typeTex);
        compute.SetTexture(kProj, "_VelX", velXTex);
        compute.SetTexture(kProj, "_VelY", velYTex);
        compute.Dispatch(kProj, groupVelX, groupVelY, 1);

        // 10. G2P, Advection
        int kG2PAdv = compute.FindKernel("G2P_Advection");
        compute.SetBuffer(kG2PAdv, "_Particles", particleBuf);
        compute.SetTexture(kG2PAdv, "_VelX", velXTex);
        compute.SetTexture(kG2PAdv, "_VelY", velYTex);
        compute.SetTexture(kG2PAdv, "_VelXOld", velXOldTex);
        compute.SetTexture(kG2PAdv, "_VelYOld", velYOldTex);
        compute.SetTexture(kG2PAdv, "_GridType", typeTex);
        compute.Dispatch(kG2PAdv, threadGroups, 1, 1);

        // 速度の遺品整理
        int kDeadVel = compute.FindKernel("TransferDeadVelocity");
        compute.SetBuffer(kDeadVel, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kDeadVel, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kDeadVel, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kDeadVel, "_WeightYBuffer", weightYBuf);
        compute.SetBuffer(kDeadVel, "_Particles", particleBuf);
        compute.SetBuffer(kDeadVel, "_DeadPoolAppend", deadPoolBuf);
        compute.Dispatch(kDeadVel, groupX, groupY, 1);
        
        // --- 11. 補充（Spawn） ---
        // 前フレームの currentCount に基づき、足りなければ補充
        int kSpawn = compute.FindKernel("SpawnParticles");
        compute.SetBuffer(kSpawn, "_Particles", particleBuf);
        compute.SetTexture(kSpawn, "_GridType", typeTex);
        compute.SetTexture(kSpawn, "_VelX", velXTex);
        compute.SetTexture(kSpawn, "_VelY", velYTex);
        compute.SetBuffer(kSpawn, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kSpawn, "_WeightYBuffer", weightYBuf);
        compute.SetBuffer(kSpawn, "_DeadPoolConsume", deadPoolBuf);
        compute.Dispatch(kSpawn, groupX, groupY, 1);

        // --- 12. アクティブリストの構築 (Collect) ---
        // 生き残った粒子と新しく生まれた粒子のIDをリスト化
        int kCollect = compute.FindKernel("CollectActiveList");
        compute.SetBuffer(kCollect, "_Particles", particleBuf);
        compute.SetTexture(kCollect, "_GridType", typeTex);
        compute.SetBuffer(kCollect, "_ActiveListAppend", activeListBuf);
        compute.Dispatch(kCollect, threadGroups, 1, 1);

        // --- 13. カウントの取得 (次フレーム用) ---
        ComputeBuffer.CopyCount(activeListBuf, countBuf, 0);
        countBuf.GetData(countArray);
        currentCount = countArray[0];
    }

    void SolvePressure(int groupX, int groupY)
    {
        // --- A. 初期化フェーズ ---
        // 1. dotResult と CGVars をリセット
        dotBuf.SetData(new int[] { 0 });
        // CGVars のリセット (rTr, dAd, alpha, beta, rTrOld すべて 0)
        cgVarsBuf.SetData(new float[5]); 

        // 2. InitCG
        int kInit = compute.FindKernel("InitCG");
        compute.SetTexture(kInit, "_Pressure", presTex);
        compute.SetTexture(kInit, "_Residual", resTex);
        compute.SetTexture(kInit, "_SearchDir", dirTex);
        compute.SetTexture(kInit, "_GridType", typeTex);
        compute.Dispatch(kInit, groupX, groupY, 1);

        // 2. 現在の圧力 p から Ap を計算 (Warm Start用)
        int kap = compute.FindKernel("ApplyA");
        compute.SetTexture(kap, "_SearchDir", presTex); // SearchDirの代わりにPressureを渡す
        compute.SetTexture(kap, "_Ap", apTex);
        compute.SetTexture(kap, "_GridType", typeTex);
        compute.Dispatch(kap, groupX, groupY, 1);

        // 3. 初期残差 r = b - Ap を計算
        int kRes = compute.FindKernel("ComputeInitialResidual");
        compute.SetTexture(kRes, "_Divergence", divTex);
        compute.SetTexture(kRes, "_Ap", apTex);
        compute.SetTexture(kRes, "_Residual", resTex);
        compute.SetTexture(kRes, "_SearchDir", dirTex);
        compute.SetTexture(kRes, "_Precon", preconTex);
        compute.SetTexture(kRes, "_GridType", typeTex);
        compute.Dispatch(kRes, groupX, groupY, 1);

        // 4. 初回の rTr 計算: precon*res*res
        int kDot = compute.FindKernel("DotProduct");
        compute.SetInt("_UsePrecon", 1);
        compute.SetTexture(kDot, "_TexA", resTex);
        compute.SetTexture(kDot, "_TexB", resTex);
        compute.SetTexture(kDot, "_Precon", preconTex); // 計算には使用しない
        compute.SetBuffer(kDot, "_DotResult", dotBuf);
        compute.SetTexture(kDot, "_GridType", typeTex);
        compute.Dispatch(kDot, groupX, groupY, 1);

        // 5. 計算されたドット積を CGVars[0] (rTr) に格納
        int kManage = compute.FindKernel("ManageCG");
        compute.SetInt("_CGPhase", 0);
        compute.SetBuffer(kManage, "_DotResult", dotBuf);
        compute.SetBuffer(kManage, "_CGVars", cgVarsBuf);
        compute.Dispatch(kManage, 1, 1, 1);

        // --- B. CG反復フェーズ ---
        for (int i = 0; i < iteration; i++)
        {
            // 1. Ap = A * d
            compute.SetTexture(kap, "_GridType", typeTex);
            compute.SetTexture(kap, "_Ap", apTex);
            compute.SetTexture(kap, "_SearchDir", dirTex);
            compute.SetTexture(kap, "_Precon", preconTex);
            compute.Dispatch(kap, groupX, groupY, 1);

            // 2. dAd (d * Ap) の計算準備
            compute.SetInt("_UsePrecon", 0);
            compute.SetTexture(kDot, "_TexA", dirTex);
            compute.SetTexture(kDot, "_TexB", apTex);
            compute.SetTexture(kDot, "_Precon", preconTex); // 計算には使用しない
            compute.SetBuffer(kDot, "_DotResult", dotBuf);
            compute.SetTexture(kDot, "_GridType", typeTex);
            compute.Dispatch(kDot, groupX, groupY, 1);

            // 3. alpha = rTr / dAd の計算
            compute.SetInt("_CGPhase", 1);
            compute.SetBuffer(kManage, "_DotResult", dotBuf);
            compute.SetBuffer(kManage, "_CGVars", cgVarsBuf);
            compute.Dispatch(kManage, 1, 1, 1);

            // 4. Update P, R (バッファからAlphaを直接読む)
            int kUpdatePR = compute.FindKernel("UpdatePR");
            compute.SetBuffer(kUpdatePR, "_CGVars", cgVarsBuf);
            compute.SetTexture(kUpdatePR, "_GridType", typeTex);
            compute.SetTexture(kUpdatePR, "_Pressure", presTex);
            compute.SetTexture(kUpdatePR, "_SearchDir", dirTex);
            compute.SetTexture(kUpdatePR, "_Residual", resTex);
            compute.SetTexture(kUpdatePR, "_Ap", apTex);
            compute.Dispatch(kUpdatePR, groupX, groupY, 1);

            // 5. 新しい rTr (rTr_new) の計算準備
            compute.SetInt("_UsePrecon", 1);
            compute.SetTexture(kDot, "_TexA", resTex);
            compute.SetTexture(kDot, "_TexB", resTex);
            compute.Dispatch(kDot, groupX, groupY, 1);

            // 6. beta = rTr_new / rTr_old の計算
            compute.SetInt("_CGPhase", 2);
            compute.SetBuffer(kManage, "_DotResult", dotBuf);
            compute.SetBuffer(kManage, "_CGVars", cgVarsBuf);
            compute.Dispatch(kManage, 1, 1, 1);

            // 7. Update D (バッファからBetaを直接読む)
            int kUpdateD = compute.FindKernel("UpdateD");
            compute.SetBuffer(kUpdateD, "_CGVars", cgVarsBuf);
            compute.SetTexture(kUpdateD, "_GridType", typeTex);
            compute.SetTexture(kUpdateD, "_SearchDir", dirTex);
            compute.SetTexture(kUpdateD, "_Residual", resTex);
            compute.SetTexture(kUpdateD, "_Precon", preconTex);
            compute.Dispatch(kUpdateD, groupX, groupY, 1);
        }
    }

    void OnRenderObject()
    {
        if (particleBuf == null || displayMaterial == null) return;

        // 値のセット（Pass0, Pass1共通）
        displayMaterial.SetFloat("_width", width);
        displayMaterial.SetFloat("_height", height);
        displayMaterial.SetFloat("_flipPos", flipPos);
        displayMaterial.SetFloat("_picPos", picPos);
        displayMaterial.SetFloat("_eulerPos", eulerPos);
        displayMaterial.SetFloat("_swePos", swePos);
        displayMaterial.SetFloat("_fftPos", fftPos);
        displayMaterial.SetInt("_nx", nx);
        displayMaterial.SetInt("_ny", ny);
        displayMaterial.SetBuffer("_Particles", particleBuf);
        displayMaterial.SetVector("_ObjPos", transform.position);
        displayMaterial.SetVector("_ObjScale", transform.localScale);
        displayMaterial.SetBuffer("_ActiveList", activeListBuf);
        displayMaterial.SetFloat("_aspectRatio", aspect);

        // 流体パーティクルの描画（Pass0: 背景, Pass1: 流体）
        displayMaterial.SetPass(1);
        // CopyCountしたcurrentCountを使って、生きている数だけ描画
        Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, currentCount);
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
        countBuf?.Release();
        activeListBuf?.Release();
        deadPoolBuf?.Release();


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
