using UnityEngine;

public class Grid2D_Manager : MonoBehaviour
{
    public ComputeShader computeShader;
    public Material displayMaterial;
    
    // 速度用
    RenderTexture velA, velB;
    // 圧力用
    RenderTexture pressureA, pressureB;
    // 発散
    RenderTexture divergenceBuffer;
    
    // パラメータ
    public float alpha = 0.3f;
    public float dx = 0.1f;
    public float dt = 0.03f;
    public Vector4 texSize = new Vector2(256f, 256f);
    public float sensitivity = 500;
    public Vector2 lastMousePos;
    public float brushSize = 0.08f;
    public bool addLeftEdgeFlow = false;

    void Start()
    {
        // 既存の速度バッファ作成
        velA = CreateRT(); velB = CreateRT();
        // 圧力バッファ作成
        pressureA = CreateRT(); pressureB = CreateRT();
        // 発散バッファ
        divergenceBuffer = CreateRT();

        Graphics.Blit(Texture2D.blackTexture, velA);
        Graphics.Blit(Texture2D.blackTexture, velB);
        displayMaterial.SetTexture("_MainTex", velA);

        Renderer renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            displayMaterial = renderer.sharedMaterial; 
        }
    }

    RenderTexture CreateRT() {
        RenderTexture rt = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBFloat);
        rt.enableRandomWrite = true;

        rt.filterMode = FilterMode.Bilinear;
        rt.wrapMode = TextureWrapMode.Clamp;
        
        rt.Create();
        return rt;
    }

    void Update()
    {
        computeShader.SetFloat("alpha", alpha);
        computeShader.SetFloat("dt", dt);
        computeShader.SetFloat("dx", dx);
        computeShader.SetVector("texSize", new Vector4(256, 256));
        computeShader.SetFloat("brushSize", brushSize);

        computeShader.SetFloat("time", Time.time);

        // 1. 熱源（速度）追加
        HandleMouseInput();

        // 2. 移流 (A → B)
        int advectionKernel = computeShader.FindKernel("Advection");
        computeShader.SetTexture(advectionKernel, "Input", velA);
        computeShader.SetTexture(advectionKernel, "Result", velB);
        computeShader.Dispatch(advectionKernel, 256 / 8, 256 / 8, 1);
        
        Swap(ref velA, ref velB); // 入れ替え

        // 3. 拡散 (A → B)
        int diffusionKernel = computeShader.FindKernel("Diffusion");
        computeShader.SetTexture(diffusionKernel, "Input", velA);
        computeShader.SetTexture(diffusionKernel, "Result", velB);
        computeShader.Dispatch(diffusionKernel, 256 / 8, 256 / 8, 1);
        
        Swap(ref velA, ref velB); // 入れ替え

        // 4. Divergence計算 (velA を読んで divergenceBuffer に書く)
        int divKernel = computeShader.FindKernel("Divergence");
        computeShader.SetTexture(divKernel, "VelIn", velA);
        computeShader.SetTexture(divKernel, "DivOut", divergenceBuffer);
        computeShader.Dispatch(divKernel, 256 / 8, 256 / 8, 1);

        // 5. PressureSolve (反復計算)
        int solveKernel = computeShader.FindKernel("PressureSolve");
        Graphics.Blit(Texture2D.blackTexture, pressureA); 
        Graphics.Blit(Texture2D.blackTexture, pressureB);
        
        for (int i = 0; i < 20; i++) {
            computeShader.SetTexture(solveKernel, "DivIn", divergenceBuffer);
            computeShader.SetTexture(solveKernel, "PresIn", pressureA);
            computeShader.SetTexture(solveKernel, "PresOut", pressureB);
            computeShader.Dispatch(solveKernel, 256 / 8, 256 / 8, 1);
            Swap(ref pressureA, ref pressureB);
        }

        // 6. ProjectVelocity (速度の補正)
        int projectKernel = computeShader.FindKernel("ProjectVelocity");
        computeShader.SetTexture(projectKernel, "PresIn", pressureA);
        computeShader.SetTexture(projectKernel, "VelIn", velA);
        computeShader.SetTexture(projectKernel, "VelOut", velB);
        computeShader.Dispatch(projectKernel, 256 / 8, 256 / 8, 1);
        Swap(ref velA, ref velB);

        // 7. 表示
        displayMaterial.SetTexture("_MainTex", velA);
    }


    void Swap(ref RenderTexture a, ref RenderTexture b) {
        RenderTexture temp = a;
        a = b;
        b = temp;
    }
    
    // ...
    void HandleMouseInput()
    {
        Vector2 currentMousePos = Vector2.zero;
        bool isHit = false;

        // カメラからマウス位置に向かってレイを飛ばす
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit))
        {
            if (hit.transform == transform)
            {
                currentMousePos = hit.textureCoord;
                isHit = true;
            }
        }

        if (Input.GetMouseButtonDown(0)) lastMousePos = currentMousePos;

        // 左端から流れを出すかどうか切り替える（例：Lキー）
        if (Input.GetKeyDown(KeyCode.L))
        {
            addLeftEdgeFlow = !addLeftEdgeFlow;
            Debug.Log("Left Edge Flow: " + (addLeftEdgeFlow ? "Enabled" : "Disabled"));
        }

        // AddHeat カーネルのディスパッチ
        int heatKernel = computeShader.FindKernel("AddHeat");
        
        // マウス入力
        if (Input.GetMouseButton(0) && isHit) 
        {
            float dt_safe = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector2 velocity = (currentMousePos - lastMousePos) * 256.0f / dt_safe;
            Vector2 mouseVelocity = velocity * sensitivity; // sensitivityで調整

            computeShader.SetVector("mousePos", currentMousePos);
            computeShader.SetVector("mouseVelocity", mouseVelocity);
            computeShader.SetBool("mouseDown", true);
        }
        else // マウスが押されていない時は false を送る
        {
            computeShader.SetBool("mouseDown", false);
        }

        // 左端からの波を出すかどうかのフラグを Compute Shader に送る
        computeShader.SetBool("addLeftEdgeFlow", addLeftEdgeFlow);

        // Compute Shader のディスパッチはここで一度だけ行う
        computeShader.SetTexture(heatKernel, "Result", velA);
        computeShader.Dispatch(heatKernel, 256 / 8, 256 / 8, 1);

        lastMousePos = currentMousePos;
    }
}