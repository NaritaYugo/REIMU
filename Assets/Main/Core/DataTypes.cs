using UnityEngine;
using System.Runtime.InteropServices;

namespace FluidSimulation
{
    // 2次元SWEのセル構造体
    public struct SWECell
    {
        public float h;         // 水深
        public float hu;        // x方向の運動量
        public float hv;        // y方向の運動量
        // HLSLのStructuredBufferにおける16バイト(float4)アライメントの仕様に
        // メモリレイアウトを合わせるためのパディング
        public float padding;   
    }

    // 3次元APICの粒子構造体
    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct APICParticle 
    {
        public Vector3 position; public float mass;
        public Vector3 velocity; public float age;
        
        // APIC特有の速度の空間微分（アフィン行列）を保持する変数
        // 16バイトアライメントのためのパディング(pad_c)を含める
        public Vector3 c1;       public float pad_c1;
        public Vector3 c2;       public float pad_c2;
        public Vector3 c3;       public float pad_c3;
    }
}