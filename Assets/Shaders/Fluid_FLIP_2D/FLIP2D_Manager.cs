using UnityEngine;

public class FLIP2DManager : MonoBehaviour
{
    public ComputeShader compute;
    public Material displayMaterial;

    const float dt = 0.01f;
    float width,height;
    int nx,ny;
    const int RES = 32; // 長さ1.0fあたりのセル数。
    const float h = 1.0f / (float)RES; // 1セルの幅
    const float gravity = -9.8f;
    const float flipRatio = 0.99f;
    const float rho = 1.0f; //密度
    const int PARTICLES = 100000;
    const int maxIter = 24;
    const float tolerance = 1e-9f;
    const int iteration = 32;

    struct Particle
    {
        public Vector2 position;
        public Vector2 velocity;
    }

    struct MGLevel {
        public RenderTexture pressure;
        public RenderTexture divergence;
        public RenderTexture type;
        public RenderTexture residual;
        public int width;
        public int height;
    }


    ComputeBuffer particleBuf,velXBuf,velYBuf,weightXBuf,weightYBuf,dotBuf,cgVarsBuf;
    RenderTexture velXTex,velYTex,velXOldTex,velYOldTex,presTex,divTex,resTex,dirTex,apTex,preconTex,typeTex;

    void Start()
    {
        width = transform.localScale.x;
        height = transform.localScale.y;

        nx = Mathf.RoundToInt(width * RES);
        ny = Mathf.RoundToInt(height * RES);

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

    RenderTexture CreateRT(int w, int h, RenderTextureFormat fmt) {
        RenderTexture rt = new RenderTexture(w, h, 0, fmt);
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Point; // 圧力量なので補完しない
        rt.Create();
        return rt;
    }

    void InitParticles()
    {
        Particle[] p = new Particle[PARTICLES];
        for (int i = 0; i < PARTICLES; i++)
        {
            p[i].position = new Vector2(width/2, height/2) + Random.insideUnitCircle * width * 0.4f;
            p[i].velocity = Vector2.zero;
        }
        particleBuf.SetData(p);
    }

    void Update()
    {

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
        compute.SetInt("particleCount", PARTICLES);
        compute.SetFloat("width", width);
        compute.SetFloat("height", height);
        compute.SetFloat("rho", rho);
        compute.SetFloat("gravity", gravity);
        compute.SetFloat("flipRatio", flipRatio);
        compute.SetInt("maxIter", maxIter);
        compute.SetFloat("tolerance", tolerance);

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
        displayMaterial.SetFloat("_width", width);
        displayMaterial.SetFloat("_height", height);
        displayMaterial.SetInt("_nx", nx);
        displayMaterial.SetInt("_ny", ny);
        if (particleBuf == null) return;

        // 1. マテリアルにデータをセット
        displayMaterial.SetBuffer("_Particles", particleBuf);
        displayMaterial.SetVector("_ObjPos", transform.position);
        displayMaterial.SetVector("_ObjScale", transform.localScale);

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
