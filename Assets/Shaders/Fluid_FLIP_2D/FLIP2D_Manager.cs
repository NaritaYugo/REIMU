using UnityEngine;

public class FLIP2DManager : MonoBehaviour
{
    public ComputeShader compute;
    public Material displayMaterial;

    const int RES = 256;
    const int PARTICLES = 10000;

    struct Particle
    {
        public Vector2 position;
        public Vector2 velocity;
    }

    ComputeBuffer particleBuffer, weightBuffer, velXBuffer, velYBuffer;

    RenderTexture velocity, velocityOld;
    RenderTexture weight;
    RenderTexture pressure, pressureTemp;
    RenderTexture divergence;

    void Start()
    {
        // 16バイト (float2 pos + float2 vel = 4byte * 4) を明示
        particleBuffer = new ComputeBuffer(PARTICLES, sizeof(float) * 4); 
        weightBuffer = new ComputeBuffer(RES * RES, sizeof(int));
        velXBuffer = new ComputeBuffer(RES * RES, sizeof(int));
        velYBuffer = new ComputeBuffer(RES * RES, sizeof(int));

        Particle[] p = new Particle[PARTICLES];
        for (int i = 0; i < PARTICLES; i++)
        {
            // 0~1の範囲でランダムに。少し散らして固まらないようにします
            p[i].position = new Vector2(Random.value*0.1f+0.45f, Random.value*0.1f+0.45f);
            p[i].velocity = Vector2.zero;
        }
        particleBuffer.SetData(p);

        velocity = CreateRT(RenderTextureFormat.RGFloat);
        velocityOld = CreateRT(RenderTextureFormat.RGFloat);
        weight = CreateRT(RenderTextureFormat.RFloat);
        pressure = CreateRT(RenderTextureFormat.RFloat);
        pressureTemp = CreateRT(RenderTextureFormat.RFloat);
        divergence = CreateRT(RenderTextureFormat.RFloat);

        displayMaterial.SetBuffer("_Particles", particleBuffer);
    }

    RenderTexture CreateRT(RenderTextureFormat format)
    {
        RenderTexture rt = new RenderTexture(RES, RES, 0, format);
        rt.enableRandomWrite = true;
        rt.Create();
        return rt;
    }

    void Update()
    {
        weightBuffer.SetData(new int[RES * RES]);
        int threadGroups = Mathf.CeilToInt((float)PARTICLES / 64f);
        int gridGroups = Mathf.CeilToInt((float)RES / 8f);

        compute.SetInt("particleCount", PARTICLES);
        compute.SetFloat("dt", 0.01f);
        compute.SetFloat("dx", 1f / RES);
        compute.SetFloat("gravity", 9.8f);
        compute.SetFloat("flipRatio", 0.1f);

        //Clear
        int kCla = compute.FindKernel("ClearGrid");
        compute.SetBuffer(kCla, "_Particles", particleBuffer);
        compute.SetTexture(kCla, "_Velocity", velocity);
        compute.SetTexture(kCla, "_Velocity", velocity);
        compute.SetTexture(kCla, "_Weight", weight);
        compute.Dispatch(kCla, gridGroups, gridGroups, 1);

        //Particle to Grid
        int kP2G = compute.FindKernel("P2G");
        compute.SetBuffer(kP2G, "_Particles", particleBuffer);
        compute.SetTexture(kP2G, "_Velocity", velocity);
        compute.SetTexture(kP2G, "_Weight", weight);
        compute.SetBuffer(kP2G, "_VelXBuffer", velXBuffer);
        compute.SetBuffer(kP2G, "_VelYBuffer", velYBuffer);
        compute.SetBuffer(kP2G, "_WeightBuffer", weightBuffer);
        compute.Dispatch(kP2G, threadGroups, 1, 1);

        //Weight to Texture
        int kW2Tex = compute.FindKernel("WeightToTexture");
        compute.SetTexture(kW2Tex, "_Weight", weight);
        compute.SetBuffer(kW2Tex, "_WeightBuffer", weightBuffer);
        compute.Dispatch(kW2Tex, RES / 8, RES / 8, 1);

        //Normalize
        int kNorm = compute.FindKernel("Normalize");
        compute.SetTexture(kNorm, "_Velocity", velocity);
        compute.SetTexture(kNorm, "_VelocityOld", velocityOld);
        compute.SetTexture(kNorm, "_Weight", weight);
        compute.SetBuffer(kNorm, "_VelXBuffer", velXBuffer);
        compute.SetBuffer(kNorm, "_VelYBuffer", velYBuffer);
        compute.Dispatch(kNorm, RES / 8, RES / 8, 1);

        // Add gravity
        int kGrav = compute.FindKernel("AddGravity");
        compute.SetTexture(kGrav, "_Velocity", velocity);
        compute.SetTexture(kGrav, "_Weight", weight);
        compute.Dispatch(kGrav, RES / 8, RES / 8, 1);

        //Divergence
        int kDiv = compute.FindKernel("Divergence");
        compute.SetTexture(kDiv, "_Velocity", velocity);
        compute.SetTexture(kDiv, "_Weight", weight);
        compute.SetBuffer(kDiv, "_WeightBuffer", weightBuffer);
        compute.SetTexture(kDiv, "_Divergence", divergence);
        compute.Dispatch(kDiv, RES / 8, RES / 8, 1);

        //Pressure Solve
        int kPres = compute.FindKernel("PressureSolve");
        for (int i = 0; i < 40; i++)
        {
            compute.SetTexture(kPres, "_Pressure", pressure);
            compute.SetTexture(kPres, "_PressureTemp", pressureTemp);
            compute.SetTexture(kPres, "_Divergence", divergence);
            compute.SetTexture(kPres, "_Weight", weight);
            compute.Dispatch(kPres, RES / 8, RES / 8, 1);

            Swap(ref pressure, ref pressureTemp);
        }

        //Project Velocity
        int kProj = compute.FindKernel("Project");
        compute.SetTexture(kProj, "_Pressure", pressure);
        compute.SetTexture(kProj, "_Velocity", velocity);
        compute.Dispatch(kProj, RES / 8, RES / 8, 1);

        //Grid to Particle
        int kG2P = compute.FindKernel("G2P");
        compute.SetBuffer(kG2P, "_Particles", particleBuffer);
        compute.SetTexture(kG2P, "_Velocity", velocity);
        compute.SetTexture(kG2P, "_VelocityOld", velocityOld);
        compute.Dispatch(kG2P, threadGroups, 1, 1);

        Graphics.Blit(velocity, velocityOld);
    }

    void Swap(ref RenderTexture a, ref RenderTexture b)
    {
        RenderTexture t = a;
        a = b;
        b = t;
    }

    void OnRenderObject()
    {
        // オブジェクトの現在の位置とスケールを直接渡す
        displayMaterial.SetVector("_ObjPos", transform.position);
        displayMaterial.SetVector("_ObjScale", transform.localScale);
        
        displayMaterial.SetPass(0);
        displayMaterial.SetBuffer("_Particles", particleBuffer);
        Graphics.DrawProceduralNow(MeshTopology.Points, PARTICLES);
    }
}
