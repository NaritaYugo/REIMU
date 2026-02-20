using UnityEngine;
using System.Runtime.InteropServices; // Structサイズ計算用

public class TCSF : MonoBehaviour
{
    public ComputeShader compute;
    public Material displayMaterial;

    const float dt = 0.01f; // タイムステップ
    float width = 10.0f;
    float height = 10.0f;
    int nx, ny;
    const int RES = 32;
    const float cell_h = 1.0f / (float)RES; // 変数名 h は紛らわしいので cell_h とします
    const float gravity = -9.8f;
    const float flipRatio = 0.99f;
    const float rho = 1.0f;
    const int PARTICLES = 50000; // テスト用に少し減らしても良いが一旦維持
    const int maxIter = 100;    // CG法の収束用に少し増やす
    const float tolerance = 1e-5f; // 許容誤差

    // --- 連成用パラメータ ---
    float particleMass = 1.0f; // 便宜上 1.0 とする (実際は volume * rho)

    // 構造体を更新: activeフラグを追加
    struct Particle
    {
        public Vector2 position;
        public Vector2 velocity;
        public int active; // 1: Active, 0: Inactive (SWEに吸収された)
        public float padding; // アライメント調整用 (計24byte)
    }
    
    // SWEの状態構造体
    struct SWEState {
        public float h; // 水深
        public float u; // 水平流速
        public float h_new; // ダブルバッファ用
        public float u_new;
    }

    ComputeBuffer particleBuf, velXBuf, velYBuf, weightXBuf, weightYBuf, dotBuf, cgVarsBuf;
    // --- 新規追加バッファ ---
    ComputeBuffer sweBuf;     // SWEの状態 (サイズ: nx)
    ComputeBuffer fluxBuf;    // 質量・運動量交換用 (サイズ: nx * 2,  [0]=massFlux, [1]=momFlux)
    ComputeBuffer activeCountBuf; // 生存粒子数カウント用（デバッグ/Spawn用）
    ComputeBuffer poolBuf; 

    RenderTexture velXTex, velYTex, velXOldTex, velYOldTex, presTex, divTex, resTex, dirTex, apTex, preconTex, typeTex;

    void Start()
    {
        //width = transform.localScale.x;
        //height = transform.localScale.y;
        transform.localScale = new Vector3(width, height, 1);

        nx = Mathf.RoundToInt(width * RES);
        ny = Mathf.RoundToInt(height * RES);

        particleMass = 0.0002f;

        // --- バッファ生成 ---
        particleBuf = new ComputeBuffer(PARTICLES, Marshal.SizeOf(typeof(Particle)), ComputeBufferType.Structured);
        
        // 既存バッファ
        velXBuf = new ComputeBuffer((nx + 1) * ny, sizeof(int));
        velYBuf = new ComputeBuffer(nx * (ny + 1), sizeof(int));
        weightXBuf = new ComputeBuffer(nx * ny, sizeof(int));
        weightYBuf = new ComputeBuffer(nx * ny, sizeof(int));
        dotBuf = new ComputeBuffer(1, sizeof(int));
        cgVarsBuf = new ComputeBuffer(5, sizeof(float));

        // --- 新規: SWE & Flux バッファ ---
        sweBuf = new ComputeBuffer(nx, Marshal.SizeOf(typeof(SWEState)));
        // fluxBufは atomic int で処理するため、float2ではなく int x 2 のストライドで確保
        // [i*2 + 0] = MassFlux, [i*2 + 1] = MomFlux
        fluxBuf = new ComputeBuffer(nx * 2, sizeof(int)); 
        activeCountBuf = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw); // Counter

        poolBuf = new ComputeBuffer(PARTICLES, sizeof(uint), ComputeBufferType.Append);
        // カウンタをリセット (初期値 = 全粒子が死亡状態 = PARTICLES)
        poolBuf.SetCounterValue((uint)PARTICLES);

        // --- Texture生成 (変更なし) ---
        RenderTexture CreateGridTex(int w, int h, RenderTextureFormat format) {
            RenderTexture tex = new RenderTexture(w, h, 0, format);
            tex.enableRandomWrite = true;
            tex.filterMode = FilterMode.Point;
            tex.Create();
            return tex;
        }

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

        InitData();
    }

    void InitData()
        {
            // 1. 粒子配列の初期化
            // ★変更: 最初は「全粒子を非アクティブ(active=0)」にする
            // 水面はSWEだけで表現し、粒子は0個からスタートします。
            Particle[] p = new Particle[PARTICLES];
            for (int i = 0; i < PARTICLES; i++)
            {
                p[i].position = new Vector2(-100, -100); // 画面外
                p[i].velocity = Vector2.zero;
                p[i].active = 0; // 死んでいる
            }
            particleBuf.SetData(p);

            // 2. プールの初期化
            // 全てのID (0 ~ PARTICLES-1) をプールに入れる
            int[] poolIndices = new int[PARTICLES];
            for (int i = 0; i < PARTICLES; i++) poolIndices[i] = i;
            poolBuf.SetData(poolIndices);
            poolBuf.SetCounterValue((uint)PARTICLES); // 重要: カウンタをセット

            // 3. SWE初期化 (ここは前回のまま)
            SWEState[] swe = new SWEState[nx];
            float initialWaterHeight = height * 0.25f; 
            for(int i=0; i<nx; i++) {
                swe[i].h = initialWaterHeight;
                swe[i].u = 0;
                swe[i].h_new = initialWaterHeight;
                swe[i].u_new = 0;
            }
            sweBuf.SetData(swe);
            
            // Fluxクリア
            int[] clearFlux = new int[nx * 2];
            fluxBuf.SetData(clearFlux);
        }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R)) {
            InitData(); // 強制再初期化
            Debug.Log("Simulation Reset");
        }

        // グループ数計算
        int threadGroups = Mathf.CeilToInt((float)PARTICLES / 64f);
        int groupX = Mathf.CeilToInt((float)nx / 8f);
        int groupY = Mathf.CeilToInt((float)ny / 8f);
        int groupVelX = Mathf.CeilToInt((float)(nx + 1) / 8f);
        int groupVelY = Mathf.CeilToInt((float)(ny + 1) / 8f);
        int groupSWE = Mathf.CeilToInt((float)nx / 64f); // 1D

        // 定数セット
        compute.SetFloat("dt", dt);
        compute.SetFloat("h", cell_h); // ComputeShader側変数名に合わせる
        compute.SetInt("nx", nx);
        compute.SetInt("ny", ny);
        compute.SetInt("particleCount", PARTICLES);
        compute.SetFloat("rho", rho);
        compute.SetFloat("gravity", gravity);
        compute.SetFloat("flipRatio", flipRatio);
        compute.SetFloat("particleMass", particleMass); // 新規

        bool isMouseActive = false;
        Vector2 mousePos = Vector2.zero;
        // MeshCollider/BoxColliderへのRaycast
        if (Input.GetMouseButton(0) || Input.GetMouseButton(1))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                mousePos = new Vector2(hit.textureCoord.x * width, hit.textureCoord.y * height);
                isMouseActive = true;
            }
            // ★救済措置: Collider判定が失敗する場合の「強制平面判定」
            else 
            {
                // Z=0平面と仮定して計算 (Colliderなしでも動くようにする)
                Plane plane = new Plane(Vector3.back, Vector3.zero); // Z=0, 法線は手前
                float enter;
                if (plane.Raycast(ray, out enter))
                {
                    Vector3 worldPos = ray.GetPoint(enter);
                    // ワールド座標をローカル(シミュレーション空間)に変換
                    // ※オブジェクトが(0,0,0)にあり、Scaleが(width, height, 1)である前提
                    Vector3 localPos = transform.InverseTransformPoint(worldPos);
                    
                    // InverseTransformPointすると 0.0~1.0 ではなく -0.5~0.5 になることが多い
                    // Pivotが中心(Center)の場合: -0.5 ~ 0.5 -> +0.5 して width倍
                    // Pivotが左下(BottomLeft)の場合: 0.0 ~ 1.0 -> そのまま width倍
                    
                    // 安全策: transform.position (左下) からの差分をとる
                    float localX = worldPos.x - transform.position.x; 
                    // ※ transform.positionが左下ならこれで正解。中心なら +width/2 が必要。
                    // ここでは一旦「RaycastHitが取れない場合の予備」として、
                    // 前回のFLIPで動いていたならRaycastHitが取れているはずなので、ここはスキップでも良い。
                }
            }
        }
        if (isMouseActive)
        {
            int kMouseSWE = compute.FindKernel("MouseInteractionSWE");
            compute.SetInt("_MouseClick", Input.GetMouseButton(0) ? 1 : 2);
            compute.SetVector("_MousePos", mousePos);
            // floatのdtなどを渡す必要はないが、バッファは必須
            compute.SetBuffer(kMouseSWE, "_SWEBuffer", sweBuf);
            
            // 重要: 変数セット (width/height/h/nx など)
            // Updateの最初でセットしているはずですが、念のため
            compute.SetInt("nx", nx);
            compute.SetFloat("h", 1.0f / RES); // h
            compute.SetFloat("width", width);

            compute.Dispatch(kMouseSWE, Mathf.CeilToInt((float)nx / 64f), 1, 1);
        }
        else
        {
             compute.SetInt("_MouseClick", 0);
        }
        int sweSubSteps = 8; // 8回刻む
        float sweDt = dt / sweSubSteps; // 時間刻みを小さく
        // シェーダーにサブステップ用のdtを渡す
        compute.SetFloat("sweDt", sweDt);
        
        // ループ実行
        for (int i = 0; i < sweSubSteps; i++)
        {
            // 1. マウス操作 (サブステップごとに適用すると強力になるので、適宜調整)
            // ここでは「押しっぱなし」の感覚をよくするため毎回適用
            if (isMouseActive)
            {
                int kMouseSWE = compute.FindKernel("MouseInteractionSWE");
                compute.SetInt("_MouseClick", Input.GetMouseButton(0) ? 1 : 2);
                compute.SetVector("_MousePos", mousePos);
                compute.SetBuffer(kMouseSWE, "_SWEBuffer", sweBuf);
                // マウス強度をdtに合わせて弱める調整をShaderでするか、ここでする
                compute.Dispatch(kMouseSWE, Mathf.CeilToInt((float)nx / 64f), 1, 1);
            }

            // 2. UpdateSWE (波の進行と粒子生成)
            int kUpdateSWE = compute.FindKernel("UpdateSWE");
            compute.SetBuffer(kUpdateSWE, "_SWEBuffer", sweBuf);
            compute.SetBuffer(kUpdateSWE, "_FluxBuffer", fluxBuf);
            compute.SetBuffer(kUpdateSWE, "_Particles", particleBuf); 
            compute.SetBuffer(kUpdateSWE, "_ParticlePoolConsume", poolBuf); 
            
            compute.Dispatch(kUpdateSWE, Mathf.CeilToInt((float)nx / 64f), 1, 1);
        }
        // ===========================================
        // Phase 2: Standard FLIP Steps
        // ===========================================

        // 1. Grid Clear
        int kCla = compute.FindKernel("ClearGrid");
        compute.SetBuffer(kCla, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kCla, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kCla, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kCla, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kCla, "_Pressure", presTex);
        compute.SetTexture(kCla, "_Divergence", divTex);
        compute.SetTexture(kCla, "_GridType", typeTex);
        compute.SetBuffer(kCla, "_SWEBuffer", sweBuf); // 壁判定のためにSWEが必要
        compute.Dispatch(kCla, groupX, groupY, 1);

        // 2. P2G (activeな粒子のみ)
        int kP2G = compute.FindKernel("P2G");
        compute.SetBuffer(kP2G, "_Particles", particleBuf);
        compute.SetBuffer(kP2G, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kP2G, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kP2G, "_WeightXBuffer", weightXBuf);
        compute.SetBuffer(kP2G, "_WeightYBuffer", weightYBuf);
        compute.SetTexture(kP2G, "_GridType", typeTex);
        compute.SetBuffer(kP2G, "_SWEBuffer", sweBuf); // 水面下判定用
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
        compute.SetBuffer(kBound, "_SWEBuffer", sweBuf); // SWE水面を境界とする
        compute.Dispatch(kBound, groupVelX, groupVelY, 1);

        /* 6.マウス判定
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
        */

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

        // 8. Pressure Solve
        SolvePressure(groupX, groupY);

        // 9. Projection
        int kProj = compute.FindKernel("Projection");
        compute.SetTexture(kProj, "_Pressure", presTex);
        compute.SetTexture(kProj, "_GridType", typeTex);
        compute.SetTexture(kProj, "_VelX", velXTex);
        compute.SetTexture(kProj, "_VelY", velYTex);
        compute.Dispatch(kProj, groupVelX, groupVelY, 1);

        // 10. G2P, Advection & Condensation (ここが重要)
        int kG2PAdv = compute.FindKernel("G2P_Advection");
        compute.SetBuffer(kG2PAdv, "_Particles", particleBuf);
        
        // 書き込み用(RW)としてではなく、読み込み用(_Read...)にセットする
        compute.SetTexture(kG2PAdv, "_ReadVelX", velXTex);
        compute.SetTexture(kG2PAdv, "_ReadVelY", velYTex);
        compute.SetTexture(kG2PAdv, "_ReadVelXOld", velXOldTex);
        compute.SetTexture(kG2PAdv, "_ReadVelYOld", velYOldTex);
        compute.SetTexture(kG2PAdv, "_ReadGridType", typeTex);
        compute.SetTexture(kG2PAdv, "_GridType", typeTex);
        compute.SetBuffer(kG2PAdv, "_SWEBuffer", sweBuf);
        compute.SetBuffer(kG2PAdv, "_FluxBuffer", fluxBuf);
        compute.SetBuffer(kG2PAdv, "_ParticlePoolAppend", poolBuf);
        
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
        for (int i = 0; i < maxIter; i++)
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

        displayMaterial.SetBuffer("_Particles", particleBuf);
        displayMaterial.SetBuffer("_SWEBuffer", sweBuf); // 追加: 可視化用
        displayMaterial.SetVector("_ObjPos", transform.position);
        displayMaterial.SetVector("_ObjScale", transform.localScale);

        // Pass 0: 粒子描画
        displayMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, PARTICLES * 6);

        // Pass 1: SWE水面描画 (Quadを描画して、シェーダーで水面形状にする)
        displayMaterial.SetPass(1);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, nx * 6);
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
        // 新規バッファ解放
        sweBuf?.Release();
        fluxBuf?.Release();
        activeCountBuf?.Release();
        poolBuf?.Release();

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