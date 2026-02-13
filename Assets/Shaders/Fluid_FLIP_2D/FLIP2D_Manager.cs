using UnityEngine;

public class FLIP2DManager : MonoBehaviour
{
    public ComputeShader compute;
    public Material displayMaterial;

    const int RES = 256;
    const int PARTICLES = 10000;
    const int maxIter = 100;
    const float tolerance = 1e-9f;

    struct Particle
    {
        public Vector2 position;
        public Vector2 velocity;
    }

    ComputeBuffer particleBuf,velXBuf,velYBuf,weightBuf,dotBuf;
    RenderTexture velXTex,velYTex,velXOldTex,velYOldTex,presTex,divTex,resTex,dirTex,apTex,preconTex,typeTex;

    void Start()
    {
        // グリッド解像度の設定
        // RES: セル数 (例: 128)
        int nx = RES;
        int ny = RES;

        // --- 1. StructuredBuffer の生成 ---
        // 粒子バッファ
        particleBuf = new ComputeBuffer(PARTICLES, sizeof(float) * 4); // position(2) + velocity(2)
        
        // int型のアトミック加算用バッファ (SCALE倍して保存するため)
        // VelX は (nx + 1) * ny
        velXBuf = new ComputeBuffer((nx + 1) * ny, sizeof(int));
        // VelY は nx * (ny + 1)
        velYBuf = new ComputeBuffer(nx * (ny + 1), sizeof(int));
        // Weight / DotResult
        weightBuf = new ComputeBuffer(nx * ny, sizeof(int));
        dotBuf = new ComputeBuffer(1, sizeof(int));

        // --- 2. RenderTexture (Gridデータ) の生成 ---
        // 汎用的な設定関数 (使い回し)
        RenderTexture CreateGridTex(int w, int h, RenderTextureFormat format) {
            RenderTexture tex = new RenderTexture(w, h, 0, format);
            tex.enableRandomWrite = true;
            tex.filterMode = FilterMode.Point; // 流体計算ではPointが基本
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
        compute.SetFloat("dt", 0.005f);
        compute.SetFloat("h", 1.0f / (float)RES);
        compute.SetInt("nx", RES);
        compute.SetInt("ny", RES);
        compute.SetInt("particleCount", PARTICLES);
        compute.SetFloat("width", 1.0f);
        compute.SetFloat("height", 1.0f);
        compute.SetFloat("rho", 1);
        compute.SetFloat("gravity", -9.8f);
        compute.SetFloat("flipRatio", 0.9f);
        compute.SetInt("maxIter", maxIter);
        compute.SetFloat("tolerance", tolerance);

        // 1. Grid Clear
        int kCla = compute.FindKernel("ClearGrid");
        compute.SetBuffer(kCla, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kCla, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kCla, "_WeightBuffer", weightBuf);
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
        compute.SetBuffer(kP2G, "_WeightBuffer", weightBuf);
        compute.SetTexture(kP2G, "_GridType", typeTex);
        compute.Dispatch(kP2G, threadGroups, 1, 1);

        // 4. Normalize
        int kNormX = compute.FindKernel("NormalizeVelX");
        compute.SetBuffer(kNormX, "_VelXBuffer", velXBuf);
        compute.SetBuffer(kNormX, "_WeightBuffer", weightBuf);
        compute.SetTexture(kNormX, "_VelX", velXTex);
        compute.Dispatch(kNormX, gridGroupsX, gridGroups, 1);

        int kNormY = compute.FindKernel("NormalizeVelY");
        compute.SetBuffer(kNormY, "_VelYBuffer", velYBuf);
        compute.SetBuffer(kNormY, "_WeightBuffer", weightBuf);
        compute.SetTexture(kNormY, "_VelY", velYTex);
        compute.Dispatch(kNormY, gridGroups, gridGroupsY, 1);

        // 5. External Forces & Boundary
        int kGrav = compute.FindKernel("AddGravity");
        compute.SetTexture(kGrav, "_VelY", velYTex);
        compute.Dispatch(kGrav, RES / 8, RES / 8, 1);

        int kBound = compute.FindKernel("ApplyBoundCond");
        compute.SetTexture(kBound, "_VelX", velXTex);
        compute.SetTexture(kBound, "_VelY", velYTex);
        compute.SetTexture(kBound, "_GridType", typeTex);
        compute.Dispatch(kBound, RES / 8, RES / 8, 1);

        // 6. Backup Velocity (FLIPの肝：差分をとるためコピー)
        Graphics.CopyTexture(velXTex, velXOldTex);
        Graphics.CopyTexture(velYTex, velYOldTex);

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

        // 10. G2P
        int kG2P = compute.FindKernel("G2P");
        compute.SetBuffer(kG2P, "_Particles", particleBuf);
        compute.SetTexture(kG2P, "_VelX", velXTex);
        compute.SetTexture(kG2P, "_VelY", velYTex);
        compute.SetTexture(kG2P, "_VelXOld", velXOldTex);
        compute.SetTexture(kG2P, "_VelYOld", velYOldTex);
        compute.Dispatch(kG2P, threadGroups, 1, 1);

        // 11. Advection
        int kAdv = compute.FindKernel("Advection");
        compute.SetBuffer(kAdv, "_Particles", particleBuf);
        compute.SetTexture(kAdv, "_GridType", typeTex);
        compute.Dispatch(kAdv, threadGroups, 1, 1);
    }

    void SolvePressure(int groups)
    {
        int kInit = compute.FindKernel("InitCG");
        compute.SetTexture(kInit, "_Pressure", presTex);
        compute.SetTexture(kInit, "_Divergence", divTex);
        compute.SetTexture(kInit, "_Residual", resTex);
        compute.SetTexture(kInit, "_SearchDir", dirTex);
        compute.SetTexture(kInit, "_Precon", preconTex);
        compute.SetTexture(kInit, "_Ap", apTex);
        compute.SetTexture(kInit, "_GridType", typeTex); // GridTypeも忘れずに
        compute.Dispatch(kInit, groups, groups, 1);
        float rTr = GetDotProduct(resTex, resTex, groups); // 補助関数

        for (int i = 0; i < maxIter; i++)
        {
            if (rTr < tolerance) break;

            // Ap = A * d
            int kap = compute.FindKernel("ApplyA");
            compute.SetTexture(kap, "_GridType", typeTex);
            compute.SetTexture(kap, "_Ap", apTex);
            compute.SetTexture(kap, "_SearchDir", dirTex);
            compute.SetTexture(kap, "_Precon", preconTex);
            compute.Dispatch(kap, groups, groups, 1);

            // alpha = rTr / (d * Ap)
            float dAd = GetDotProduct(dirTex, apTex, groups);
            float alpha = rTr / Mathf.Max(dAd, 1e-10f);

            // Update P, R
            compute.SetFloat("_Alpha", alpha);
            int kUpdatePR = compute.FindKernel("UpdatePR");
            compute.SetTexture(kUpdatePR, "_GridType", typeTex);
            compute.SetTexture(kUpdatePR, "_Pressure", presTex);
            compute.SetTexture(kUpdatePR, "_SearchDir", dirTex);
            compute.SetTexture(kUpdatePR, "_Residual", resTex);
            compute.SetTexture(kUpdatePR, "_Ap", apTex);
            compute.Dispatch(kUpdatePR, RES / 8, RES / 8, 1);


            // beta = rTr_new / rTr_old
            float rTrNew = GetDotProduct(resTex, resTex, groups);
            float beta = rTrNew / Mathf.Max(rTr, 1e-10f);

            // Update D
            compute.SetFloat("_Beta", beta);
            int kUpdateD = compute.FindKernel("UpdateD");
            compute.SetTexture(kUpdateD, "_GridType", typeTex);
            compute.SetTexture(kUpdateD, "_Pressure", presTex);
            compute.SetTexture(kUpdateD, "_SearchDir", dirTex);
            compute.SetTexture(kUpdateD, "_Residual", resTex);
            compute.SetTexture(kUpdateD, "_Precon", preconTex);
            compute.Dispatch(kUpdateD, groups, groups, 1);

            rTr = rTrNew;
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
        // グリッドサイズなども必要なら送る
        // displayMaterial.SetInt("_Res", RES);

        // 2. 描画実行 (Pass 0 を使用)
        displayMaterial.SetPass(0);
        
        // 1粒子あたり6頂点（三角形2枚）で Quad を作る
        Graphics.DrawProceduralNow(MeshTopology.Triangles, PARTICLES * 6);
    }

    int texA_ID = Shader.PropertyToID("_TexA");
    int texB_ID = Shader.PropertyToID("_TexB");

    float GetDotProduct(RenderTexture texA, RenderTexture texB, int groups)
    {
        // 1. バッファをリセット
        dotBuf.SetData(new int[] { 0 });

        // 2. カーネルとテクスチャの設定
        int k = compute.FindKernel("DotProductGeneric");
        compute.SetTexture(k, texA_ID, texA);
        compute.SetTexture(k, texB_ID, texB);
        compute.SetBuffer(k, "_DotResult", dotBuf);
        compute.SetTexture(k, "_GridType", typeTex);

        // 3. 実行
        compute.Dispatch(k, groups, groups, 1);

        // 4. 結果をCPUへ（デバッグ中はこれでOK）
        int[] res = new int[1];
        dotBuf.GetData(res); 
        
        return (float)res[0] / 1000000.0f; 
    }

    void OnDestroy()
    {
        particleBuf?.Release();
        velXBuf?.Release();
        velYBuf?.Release();
        weightBuf?.Release();
        dotBuf?.Release();

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
