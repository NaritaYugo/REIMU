using UnityEngine;

public class FFTOcean : MonoBehaviour
{
    [Header("FFT Settings")]
    public int resolution = 256;
    public int lodCount = 3;
    public float[] domainSizes = new float[] { 20.0f, 80.0f, 320.0f }; 
    
    [Header("JONSWAP Parameters")]
    public float windSpeed = 12.0f;
    public float windDirection = 0.0f;
    public float fetch = 100000.0f;
    [Range(1f, 10f)] public float peakEnhancement = 3.3f;
    [Range(0f, 10f)] public float spreadStrength = 2.0f;
    // 波の高さをLODごとに調整するためのスケール値（細かい波が荒れすぎるのを防ぐ）
    public float[] lodAmplitudes = new float[] { 0.5f, 1.0f, 1.0f };

    [Header("Compute Shaders")]
    public ComputeShader jonswapShader;
    public ComputeShader timeDependentShader;
    public ComputeShader ifftShader;

    [Header("Debug Rendering")]
    public Material fftOceanMaterial;

    // --- カスケードごとのテクスチャ配列 ---
    private RenderTexture[] h0Textures;
    private RenderTexture[] spectrumH, spectrumDx, spectrumDz;
    private RenderTexture[] pingPongH, pingPongDx, pingPongDz;
    public RenderTexture[] displacementMaps; // 各LODごとの変位

    // 最終合成マップ
    public RenderTexture mergedDisplacementMap;
    
    private Texture2D butterflyTexture;
    private int stages;

    void Start()
    {
        stages = (int)Mathf.Log(resolution, 2);
        
        InitializeTextures();
        CreateButterflyTexture();
        GenerateInitialSpectrum();
    }

    void Update()
    {
        // LODごとに時間発展とIFFTを回す
        for (int i = 0; i < lodCount; i++)
        {
            UpdateTimeDependentSpectrum(i);
            RunIFFT(i);
        }

        if (fftOceanMaterial != null)
{
            fftOceanMaterial.SetTexture("FFT_DispLOD0", displacementMaps[0]);
            fftOceanMaterial.SetTexture("FFT_DispLOD1", displacementMaps[1]);
            fftOceanMaterial.SetTexture("FFT_DispLOD2", displacementMaps[2]);
            fftOceanMaterial.SetFloat("FFT_Size0", domainSizes[0]);
            fftOceanMaterial.SetFloat("FFT_Size1", domainSizes[1]);
            fftOceanMaterial.SetFloat("FFT_Size2", domainSizes[2]);
        }
    }

    void InitializeTextures()
    {
        h0Textures = new RenderTexture[lodCount];
        spectrumH = new RenderTexture[lodCount];
        spectrumDx = new RenderTexture[lodCount];
        spectrumDz = new RenderTexture[lodCount];
        pingPongH = new RenderTexture[lodCount];
        pingPongDx = new RenderTexture[lodCount];
        pingPongDz = new RenderTexture[lodCount];
        displacementMaps = new RenderTexture[lodCount];

        for (int i = 0; i < lodCount; i++)
        {
            h0Textures[i] = CreateRenderTexture(resolution, RenderTextureFormat.ARGBFloat);
            spectrumH[i] = CreateRenderTexture(resolution, RenderTextureFormat.RGFloat);
            spectrumDx[i] = CreateRenderTexture(resolution, RenderTextureFormat.RGFloat);
            spectrumDz[i] = CreateRenderTexture(resolution, RenderTextureFormat.RGFloat);
            pingPongH[i] = CreateRenderTexture(resolution, RenderTextureFormat.RGFloat);
            pingPongDx[i] = CreateRenderTexture(resolution, RenderTextureFormat.RGFloat);
            pingPongDz[i] = CreateRenderTexture(resolution, RenderTextureFormat.RGFloat);
            displacementMaps[i] = CreateRenderTexture(resolution, RenderTextureFormat.ARGBFloat);
        }

        mergedDisplacementMap = CreateRenderTexture(resolution, RenderTextureFormat.ARGBFloat);
    }

    RenderTexture CreateRenderTexture(int size, RenderTextureFormat format)
    {
        RenderTexture rt = new RenderTexture(size, size, 0, format);
        rt.enableRandomWrite = true;
        rt.wrapMode = TextureWrapMode.Repeat; // ←【追加】これを設定しないと端が伸びて十字模様になります
        rt.Create();
        return rt;
    }

    // バタフライテクスチャの生成は変更なし(解像度が全LOD共通のため1つでOK)
    void CreateButterflyTexture()
    {
        butterflyTexture = new Texture2D(stages, resolution, TextureFormat.RGBAFloat, false, true);
        Color[] colors = new Color[resolution * stages];

        for (int i = 0; i < resolution; i++)
        {
            for (int stage = 0; stage < stages; stage++)
            {
                int butterflyWidth = 2 << stage;
                int halfWidth = butterflyWidth >> 1;

                int k = i % butterflyWidth;
                bool isTop = k < halfWidth;

                // IFFT用の回転因子 (正の指数)
                float angle = 2.0f * Mathf.PI * (k % halfWidth) / butterflyWidth;
                float twiddleRe = Mathf.Cos(angle);
                float twiddleIm = Mathf.Sin(angle);

                // シェーダーでの分岐を無くすため、Bottom Wingの符号反転をTwiddleに焼き込む
                if (!isTop) {
                    twiddleRe = -twiddleRe;
                    twiddleIm = -twiddleIm;
                }

                int topIdx = isTop ? i : i - halfWidth;
                int botIdx = isTop ? i + halfWidth : i;

                if (stage == 0) {
                    topIdx = ReverseBits(topIdx, stages);
                    botIdx = ReverseBits(botIdx, stages);
                }

                int pixelIndex = stage + i * stages; // x = stage, y = i
                colors[pixelIndex] = new Color(twiddleRe, twiddleIm, topIdx, botIdx);
            }
        }
        butterflyTexture.SetPixels(colors);
        butterflyTexture.Apply();
    }
    int ReverseBits(int index, int bits)
    {
        int rev = 0;
        for (int i = 0; i < bits; i++) {
            rev = (rev << 1) | ((index >> i) & 1);
        }
        return rev;
    }

    void GenerateInitialSpectrum()
    {
        int kernel = jonswapShader.FindKernel("CSMain");
        for (int i = 0; i < lodCount; i++)
        {
            jonswapShader.SetTexture(kernel, "H0_Texture", h0Textures[i]);
            jonswapShader.SetInt("N", resolution);
            jonswapShader.SetFloat("Length", domainSizes[i]);
            jonswapShader.SetFloat("Gravity", 9.81f);
            jonswapShader.SetFloat("WindSpeed", windSpeed);
            jonswapShader.SetFloat("WindDirection", windDirection * Mathf.Deg2Rad);
            jonswapShader.SetFloat("Fetch", fetch);
            jonswapShader.SetFloat("PeakEnhancement", peakEnhancement);
            jonswapShader.SetFloat("SpreadStrength", spreadStrength);

            // スペクトルの振幅を調整（高周波の波が暴れるのを抑える）
            // ※必要に応じてShader側に渡すか、このままC#側で制御する前提で進めます
            // （現状のJONSWAPシェーダーのままで問題なく動きます）

            Dispatch(jonswapShader, kernel);
        }
    }

    void UpdateTimeDependentSpectrum(int lodIndex)
    {
        int kernel = timeDependentShader.FindKernel("CSMain");
        timeDependentShader.SetTexture(kernel, "H0_Texture", h0Textures[lodIndex]);
        timeDependentShader.SetTexture(kernel, "OutH_Spectrum", spectrumH[lodIndex]);
        timeDependentShader.SetTexture(kernel, "OutDx_Spectrum", spectrumDx[lodIndex]);
        timeDependentShader.SetTexture(kernel, "OutDz_Spectrum", spectrumDz[lodIndex]);
        
        timeDependentShader.SetInt("N", resolution);
        timeDependentShader.SetFloat("Length", domainSizes[lodIndex]);
        timeDependentShader.SetFloat("Gravity", 9.81f);
        timeDependentShader.SetFloat("Time", Time.time);

        Dispatch(timeDependentShader, kernel);
    }

    void RunIFFT(int lodIndex)
    {
        int kernelH = ifftShader.FindKernel("CS_IFFT_Horizontal");
        int kernelV = ifftShader.FindKernel("CS_IFFT_Vertical");
        int kernelFinal = ifftShader.FindKernel("CS_Finalize");

        bool pingPong = true;

        for (int i = 0; i < stages; i++)
        {
            ifftShader.SetInt("Stage", i);
            ifftShader.SetTexture(kernelH, "ButterflyTex", butterflyTexture);
            SetIFFTBuffers(kernelH, pingPong, lodIndex);
            Dispatch(ifftShader, kernelH);
            pingPong = !pingPong;
        }

        for (int i = 0; i < stages; i++)
        {
            ifftShader.SetInt("Stage", i);
            ifftShader.SetTexture(kernelV, "ButterflyTex", butterflyTexture);
            SetIFFTBuffers(kernelV, pingPong, lodIndex);
            Dispatch(ifftShader, kernelV);
            pingPong = !pingPong;
        }

        if (pingPong) {
            ifftShader.SetTexture(kernelFinal, "Input_H", spectrumH[lodIndex]);
            ifftShader.SetTexture(kernelFinal, "Input_Dx", spectrumDx[lodIndex]);
            ifftShader.SetTexture(kernelFinal, "Input_Dz", spectrumDz[lodIndex]);
        } else {
            ifftShader.SetTexture(kernelFinal, "Input_H", pingPongH[lodIndex]);
            ifftShader.SetTexture(kernelFinal, "Input_Dx", pingPongDx[lodIndex]);
            ifftShader.SetTexture(kernelFinal, "Input_Dz", pingPongDz[lodIndex]);
        }
        ifftShader.SetFloat("LodAmplitude", lodAmplitudes[lodIndex]);

        ifftShader.SetTexture(kernelFinal, "DisplacementMap", displacementMaps[lodIndex]);
        Dispatch(ifftShader, kernelFinal);
    }

    void SetIFFTBuffers(int kernel, bool pingPong, int lodIndex)
    {
        if (pingPong) {
            ifftShader.SetTexture(kernel, "Input_H", spectrumH[lodIndex]);
            ifftShader.SetTexture(kernel, "Input_Dx", spectrumDx[lodIndex]);
            ifftShader.SetTexture(kernel, "Input_Dz", spectrumDz[lodIndex]);
            ifftShader.SetTexture(kernel, "Output_H", pingPongH[lodIndex]);
            ifftShader.SetTexture(kernel, "Output_Dx", pingPongDx[lodIndex]);
            ifftShader.SetTexture(kernel, "Output_Dz", pingPongDz[lodIndex]);
        } else {
            ifftShader.SetTexture(kernel, "Input_H", pingPongH[lodIndex]);
            ifftShader.SetTexture(kernel, "Input_Dx", pingPongDx[lodIndex]);
            ifftShader.SetTexture(kernel, "Input_Dz", pingPongDz[lodIndex]);
            ifftShader.SetTexture(kernel, "Output_H", spectrumH[lodIndex]);
            ifftShader.SetTexture(kernel, "Output_Dx", spectrumDx[lodIndex]);
            ifftShader.SetTexture(kernel, "Output_Dz", spectrumDz[lodIndex]);
        }
    }

    void Dispatch(ComputeShader shader, int kernelId)
    {
        int threadGroups = Mathf.CeilToInt(resolution / 8.0f);
        shader.Dispatch(kernelId, threadGroups, threadGroups, 1);
    }

    void OnDestroy()
    {
        for (int i = 0; i < lodCount; i++)
        {
            if (h0Textures != null && h0Textures[i] != null) h0Textures[i].Release();
            if (spectrumH != null && spectrumH[i] != null) spectrumH[i].Release();
            if (spectrumDx != null && spectrumDx[i] != null) spectrumDx[i].Release();
            if (spectrumDz != null && spectrumDz[i] != null) spectrumDz[i].Release();
            if (pingPongH != null && pingPongH[i] != null) pingPongH[i].Release();
            if (pingPongDx != null && pingPongDx[i] != null) pingPongDx[i].Release();
            if (pingPongDz != null && pingPongDz[i] != null) pingPongDz[i].Release();
            if (displacementMaps != null && displacementMaps[i] != null) displacementMaps[i].Release();
        }
        if (mergedDisplacementMap != null) mergedDisplacementMap.Release();
    }
}