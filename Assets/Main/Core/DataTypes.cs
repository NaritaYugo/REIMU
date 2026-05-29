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
        public float padding;   
    }

    // 3次元APICの粒子構造体
    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct APICParticle 
    {
        public Vector3 position; public float mass;
        public Vector3 velocity; public float age;
        
        //　アフィン行列(3x3)
        public Vector3 c1;       public float pad_c1;
        public Vector3 c2;       public float pad_c2;
        public Vector3 c3;       public float pad_c3;
    }
}