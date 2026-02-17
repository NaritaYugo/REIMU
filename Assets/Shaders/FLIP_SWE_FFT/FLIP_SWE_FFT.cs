using UnityEngine;

public class FLIPSWEFFT : MonoBehaviour
{
    public ComputeShader compute;
    public Material displayMaterial;

    const int Count = 1024;
    int groups; // Startで計算

    int kInit, kComputeExplicit, kJacobiSolve;

    ComputeBuffer mainBuffer; // h, u (State)
    ComputeBuffer bBuffer;    // 右辺ベクトル b
    ComputeBuffer xBuffer;    // 解ベクトル x (現在のu)
    ComputeBuffer xNewBuffer; // 更新後の x

    struct State { public float h; public float u; }

    void Start()
    {
        groups = Mathf.CeilToInt(Count / 64f);

        kInit = compute.FindKernel("InitWave");
        kComputeExplicit = compute.FindKernel("ComputeExplicit");
        kJacobiSolve = compute.FindKernel("JacobiSolve");

        mainBuffer = new ComputeBuffer(Count, sizeof(float) * 2);
        bBuffer = new ComputeBuffer(Count, sizeof(float));
        xBuffer = new ComputeBuffer(Count, sizeof(float));
        xNewBuffer = new ComputeBuffer(Count, sizeof(float));

        // 初期化カーネル実行
        compute.SetBuffer(kInit, "_MainBuffer", mainBuffer);
        compute.SetBuffer(kInit, "_x", xBuffer);
        compute.SetInt("_Width", Count);
        compute.Dispatch(kInit, groups, 1, 1);

        // 表示用
        displayMaterial.SetBuffer("_MainBuffer", mainBuffer);
        displayMaterial.SetInt("_Count", Count);
    }

    void Update()
    {

            // --- マウス入力処理 ---
        if (Input.GetMouseButton(0)) // 左クリック中
        {
            // スクリーン座標を 0.0 ~ 1.0 に変換
            // カメラの設定に合わせて調整してください。以下は簡易的な例です。
            float mouseX = Input.mousePosition.x / Screen.width;

            int kMouse = compute.FindKernel("MouseInteract");
            compute.SetFloat("_MousePos", mouseX);
            compute.SetFloat("_MouseRadius", 0.02f);    // 影響範囲
            compute.SetFloat("_MouseStrength", 0.1f);  // 盛り上げる強さ
            compute.SetBuffer(kMouse, "_MainBuffer", mainBuffer);
            compute.Dispatch(kMouse, groups, 1, 1);
        }

        float dt = Time.deltaTime;

        compute.SetFloat("_DT", dt);
        compute.SetFloat("_DX", 0.1f);
        compute.SetInt("_Width", Count);
        
        // Boussinesq項の係数など
        compute.SetFloat("_Alpha", 0.1f); 

        // 1. 陽的ステップ & 右辺ベクトルbの作成
        compute.SetBuffer(kComputeExplicit, "_MainBuffer", mainBuffer);
        compute.SetBuffer(kComputeExplicit, "_b", bBuffer);
        compute.SetBuffer(kComputeExplicit, "_x", xBuffer); // 前フレームの解を初期値に
        compute.Dispatch(kComputeExplicit, groups, 1, 1);

        // 2. 陰解法（ヤコビ法）による反復
        // A * u_new = b を解く
        int iterations = 20;
        for (int i = 0; i < iterations; i++)
        {
            // x -> xNew
            compute.SetBuffer(kJacobiSolve, "_b", bBuffer);
            compute.SetBuffer(kJacobiSolve, "_x", xBuffer);
            compute.SetBuffer(kJacobiSolve, "_xNew", xNewBuffer);
            compute.Dispatch(kJacobiSolve, groups, 1, 1);

            // バッファのスワップ (xNew を次の x にする)
            var temp = xBuffer;
            xBuffer = xNewBuffer;
            xNewBuffer = temp;
        }

        // 3. 結果をMainBufferに書き戻す（可視化用）
        // UpdateStateカーネルを作るか、Jacobiの最後にMainBufferへ書き込む
        // ここでは簡略化のため可視化シェーダー側で _xBuffer を読む設計への変更もアリですが、
        // ひとまず値を戻す処理として記述します。
        int kUpdate = compute.FindKernel("UpdateState");
        compute.SetBuffer(kUpdate, "_MainBuffer", mainBuffer);
        compute.SetBuffer(kUpdate, "_x", xBuffer);
        compute.Dispatch(kUpdate, groups, 1, 1);
    }

    void OnRenderObject()
    {
        displayMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.LineStrip, Count);
    }

    void OnDestroy()
    {
        mainBuffer.Release();
        bBuffer.Release();
        xBuffer.Release();
        xNewBuffer.Release();
    }
}
