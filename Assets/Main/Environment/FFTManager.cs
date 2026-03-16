using UnityEngine;

// =========================================================================
// JONSWAPスペクトルを用いたFFT (高速フーリエ変換) による海洋波シミュレーション
// APICやSWE領域の外側に広がる、無限の海（背景）の波形テクスチャを生成する。
// =========================================================================
public class FFTManager : MonoBehaviour
{
    [Header("FFT Settings")]
    [Tooltip("FFTテクスチャの解像度。2の累乗(256, 512など)である必要がある")]
    public int resolution = 256;
    [Tooltip("生成するカスケード(LOD)の数")]
    public int lodCount = 3;
    [Tooltip("各LODがカバーする海域の物理的な広さ(m)")]
    public float[] domainSizes = new float[] { 20.0f, 80.0f, 320.0f }; 
    
    [Header("JONSWAP Parameters")]
    [Tooltip("風速 (m/s)。波の高さと長さに影響する")]
    public float windSpeed = 12.0f;
    [Tooltip("風の向き (度数法)")]
    public float windDirection = 0.0f;
    [Tooltip("吹送距離 (Fetch)。風が海面を吹き抜けた距離。波の成長限界を決める")]
    public float fetch = 100000.0f;
    [Range(1f, 10f), Tooltip("スペクトルのピーク強調度。波がどれくらい揃うか")]
    public float peakEnhancement = 3.3f;
    [Range(0f, 10f), Tooltip("波の広がり具合。風向きに対してどれだけ横波が混じるか")]
    public float spreadStrength = 2.0f;
    [Tooltip("各LODの波の高さの係数")]
    public float[] lodAmplitudes = new float[] { 0.5f, 1.0f, 1.0f };

    [Header("Compute Shader")]
    public ComputeShader fftShader; 

    [Header("Debug Rendering")]
    public Material fftOceanMaterial;

    // --- テクスチャ配列 ---
    // FFTの計算は周波数領域(Spectrum)から空間領域(Displacement)への逆変換(IFFT)を行う。
    // 計算過程の複素数を保持するため、RGFloatやARGBFloatの高精度フォーマットを使用する。
    private RenderTexture[] h0Textures;       // 初期スペクトル
    private RenderTexture[] spectrumH, spectrumDx, spectrumDz; // 時間発展後のスペクトル
    private RenderTexture[] pingPongH, pingPongDx, pingPongDz; // IFFT用のPingPong(入れ替え)バッファ
    public RenderTexture[] displacementMaps;  // 最終的な頂点変位マップ(XYZ)
    public RenderTexture mergedDisplacementMap;
    
    private Texture2D butterflyTexture; // バタフライ演算用のインデックス・重みテクスチャ
    private int stages; // FFTのステージ数 (log2(resolution))

    // --- カーネルIDのキャッシュ ---
    private int kernelInit, kernelUpdate;
    private int kernelIFFTHorizontal, kernelIFFTVertical, kernelFinalize;

    void Start()
    {
        stages = (int)Mathf.Log(resolution, 2);
        
        CacheKernels();
        InitializeTextures();
        CreateButterflyTexture(); // GPUでのFFT計算を高速化するための事前計算
        GenerateInitialSpectrum(); // JONSWAPモデルに基づく初期波面を生成
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
            // 時間経過に伴う波の位相変化を計算
            UpdateTimeDependentSpectrum(i);
            // 逆フーリエ変換(IFFT)を実行し、テクスチャ(空間領域)に戻す
            RunIFFT(i);
        }

        // 描画用のマテリアルに計算結果を渡す
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

    // =========================================================
    // バタフライ演算用テクスチャの生成
    // =========================================================
    // FFTアルゴリズムのキモである「どの要素とどの要素を足し引きするか」という
    // インデックス情報と、回転子(Twiddle Factor)の複素数情報をテクスチャに事前計算して焼く。
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

                // 回転子 (Twiddle factor) = e^(-i * 2PI * k / N)
                float angle = 2.0f * Mathf.PI * (k % halfWidth) / butterflyWidth;
                float twiddleRe = Mathf.Cos(angle);
                float twiddleIm = Mathf.Sin(angle);

                if (!isTop) {
                    twiddleRe = -twiddleRe;
                    twiddleIm = -twiddleIm;
                }

                int topIdx = isTop ? i : i - halfWidth;
                int botIdx = isTop ? i + halfWidth : i;

                // 最初のステージはビットリバース（インデックスのビット並びを反転）を行う
                if (stage == 0) {
                    topIdx = ReverseBits(topIdx, stages);
                    botIdx = ReverseBits(botIdx, stages);
                }

                int pixelIndex = stage + i * stages; 
                // RGには回転子の複素数、BAにはバタフライ演算で参照する2つのインデックスを格納
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
        // 小数点計算を省き、整数演算で最適化
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