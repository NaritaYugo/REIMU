using UnityEngine;

// 2次元SWE（低次モード）のセル構造体
public struct SWECell
{
    public float h;         // 水深
    public float hu;        // x方向の運動量
    public float hv;        // y方向の運動量
    public float padding;   // 16バイトアライメント用
}

// 3次元APIC（高次モード）の粒子構造体
[System.Serializable]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct APICParticle 
{
    public Vector3 position; public float mass;
    public Vector3 velocity; public float age;
    public Vector3 c1; public float pad_c1;
    public Vector3 c2; public float pad_c2;
    public Vector3 c3; public float pad_c3;
}