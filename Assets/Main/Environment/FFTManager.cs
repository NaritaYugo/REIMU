using UnityEngine;

// =========================================================================
// JONSWAPスペクトルを用いたFFT (高速フーリエ変換) による海洋波シミュレーション
// APICやSWE領域の外側に広がる、無限の海（背景）の波形テクスチャを生成する。
// =========================================================================
public class FFTManager : MonoBehaviour
{
    [Header("FFT Settings")]
    public int resolution = 256;
    public int lodCount = 3;
    public float[] domainSizes = new float[] { 20.0f, 80.0f, 320.0f }; 
    
    [Header("JONSWAP Parameters")]
    public float windSpeed = 12.0f;
    public float windDirection = 0.0f;
    public float fetch = 100000.0f;
    public float peakEnhancement = 3.3f;
    public float spreadStrength = 2.0f;
    public float[] lodAmplitudes = new float[] { 0.5f, 1.0f, 1.0f };

    [Header("Compute Shader")]
    public ComputeShader fftShader; 

    [Header("Debug Rendering")]
    public Material fftOceanMaterial;

    private RenderTexture[] h0Textures;
    private RenderTexture[] spectrumH, spectrumDx, spectrumDz;
    private RenderTexture[] pingPongH, pingPongDx, pingPongDz;
    public RenderTexture[] displacementMaps;
    public RenderTexture mergedDisplacementMap;
    
    private Texture2D butterflyTexture;
    private int stages;

    private int kernelInit, kernelUpdate;
    private int kernelIFFTHorizontal, kernelIFFTVertical, kernelFinalize;

    void Start()
    {
        stages = (int)Mathf.Log(resolution, 2);
        
        CacheKernels();
        InitializeTextures();
        CreateButterflyTexture();
        GenerateInitialSpectrum();
    }

    private void CacheKernels()
    {
        kernelInit = fftShader.FindKernel("InitJonswap");
        kernelUpdate = fftShader.FindKernel("UpdateTimeSpectrum");
        kernelIFFTHorizontal = fftShader.FindKernel("IFFT_Horizontal");
        kernelIFFTVertical = fftShader.FindKernel("IFFT_Vertical");
        kernelFinalize = fftShader.FindKernel("IFFT_Finalize");
    }

    void Update()
    {
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
        rt.wrapMode = TextureWrapMode.Repeat; 
        rt.Create();
        return rt;
    }

    // バタフライ演算用テクスチャの生成
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

                // 回転子 e^(-i * 2PI * k / N)
                float angle = 2.0f * Mathf.PI * (k % halfWidth) / butterflyWidth;
                float twiddleRe = Mathf.Cos(angle);
                float twiddleIm = Mathf.Sin(angle);

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

                int pixelIndex = stage + i * stages; 
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
        for (int i = 0; i < lodCount; i++)
        {
            fftShader.SetTexture(kernelInit, "H0_Texture", h0Textures[i]);
            fftShader.SetInt("N", resolution);
            fftShader.SetFloat("Length", domainSizes[i]);
            fftShader.SetFloat("Gravity", 9.81f);
            fftShader.SetFloat("WindSpeed", windSpeed);
            fftShader.SetFloat("WindDirection", windDirection * Mathf.Deg2Rad);
            fftShader.SetFloat("Fetch", fetch);
            fftShader.SetFloat("PeakEnhancement", peakEnhancement);
            fftShader.SetFloat("SpreadStrength", spreadStrength);

            Dispatch(fftShader, kernelInit);
        }
    }

    void UpdateTimeDependentSpectrum(int lodIndex)
    {
        fftShader.SetTexture(kernelUpdate, "H0_Texture", h0Textures[lodIndex]);
        fftShader.SetTexture(kernelUpdate, "OutH_Spectrum", spectrumH[lodIndex]);
        fftShader.SetTexture(kernelUpdate, "OutDx_Spectrum", spectrumDx[lodIndex]);
        fftShader.SetTexture(kernelUpdate, "OutDz_Spectrum", spectrumDz[lodIndex]);
        
        fftShader.SetInt("N", resolution);
        fftShader.SetFloat("Length", domainSizes[lodIndex]);
        fftShader.SetFloat("Gravity", 9.81f);
        fftShader.SetFloat("Time", Time.time);

        Dispatch(fftShader, kernelUpdate);
    }

    // =========================================================
    // IFFT (逆高速フーリエ変換) の実行
    // =========================================================
    void RunIFFT(int lodIndex)
    {
        bool pingPong = true;

        for (int i = 0; i < stages; i++)
        {
            fftShader.SetInt("Stage", i);
            fftShader.SetTexture(kernelIFFTHorizontal, "ButterflyTex", butterflyTexture);
            SetIFFTBuffers(kernelIFFTHorizontal, pingPong, lodIndex);
            Dispatch(fftShader, kernelIFFTHorizontal);
            pingPong = !pingPong;
        }

        for (int i = 0; i < stages; i++)
        {
            fftShader.SetInt("Stage", i);
            fftShader.SetTexture(kernelIFFTVertical, "ButterflyTex", butterflyTexture);
            SetIFFTBuffers(kernelIFFTVertical, pingPong, lodIndex);
            Dispatch(fftShader, kernelIFFTVertical);
            pingPong = !pingPong;
        }

        if (pingPong) {
            fftShader.SetTexture(kernelFinalize, "Input_H", spectrumH[lodIndex]);
            fftShader.SetTexture(kernelFinalize, "Input_Dx", spectrumDx[lodIndex]);
            fftShader.SetTexture(kernelFinalize, "Input_Dz", spectrumDz[lodIndex]);
        } else {
            fftShader.SetTexture(kernelFinalize, "Input_H", pingPongH[lodIndex]);
            fftShader.SetTexture(kernelFinalize, "Input_Dx", pingPongDx[lodIndex]);
            fftShader.SetTexture(kernelFinalize, "Input_Dz", pingPongDz[lodIndex]);
        }
        
        fftShader.SetFloat("LodAmplitude", lodAmplitudes[lodIndex]);
        fftShader.SetTexture(kernelFinalize, "DisplacementMap", displacementMaps[lodIndex]);
        Dispatch(fftShader, kernelFinalize);
    }

    void SetIFFTBuffers(int kernel, bool pingPong, int lodIndex)
    {
        if (pingPong) {
            fftShader.SetTexture(kernel, "Input_H", spectrumH[lodIndex]);
            fftShader.SetTexture(kernel, "Input_Dx", spectrumDx[lodIndex]);
            fftShader.SetTexture(kernel, "Input_Dz", spectrumDz[lodIndex]);
            fftShader.SetTexture(kernel, "Output_H", pingPongH[lodIndex]);
            fftShader.SetTexture(kernel, "Output_Dx", pingPongDx[lodIndex]);
            fftShader.SetTexture(kernel, "Output_Dz", pingPongDz[lodIndex]);
        } else {
            fftShader.SetTexture(kernel, "Input_H", pingPongH[lodIndex]);
            fftShader.SetTexture(kernel, "Input_Dx", pingPongDx[lodIndex]);
            fftShader.SetTexture(kernel, "Input_Dz", pingPongDz[lodIndex]);
            fftShader.SetTexture(kernel, "Output_H", spectrumH[lodIndex]);
            fftShader.SetTexture(kernel, "Output_Dx", spectrumDx[lodIndex]);
            fftShader.SetTexture(kernel, "Output_Dz", spectrumDz[lodIndex]);
        }
    }

    void Dispatch(ComputeShader shader, int kernelId)
    {
        int threadGroups = (resolution + 7) / 8;
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
        if (butterflyTexture != null) Destroy(butterflyTexture); // メモリリーク防止
    }
}