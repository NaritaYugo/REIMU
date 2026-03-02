using UnityEngine;

public class FFTOceanTracker : MonoBehaviour
{
    public Transform target;
    public float waterHeight = 15.0f;
    
    [Header("Grid Settings (Generatorと同じ値を入力)")]
    public float size = 1000.0f;   // OceanGridGeneratorの size
    public int resolution = 250;   // OceanGridGeneratorの resolution

    void LateUpdate()
    {
        if (target != null)
        {
            // 1マスのサイズ（ステップ幅）を計算
            float step = size / resolution;

            // プレイヤーの位置を「1マス単位」にスナップ（切り捨て/四捨五入）させる
            float snappedX = Mathf.Round(target.position.x / step) * step;
            float snappedZ = Mathf.Round(target.position.z / step) * step;

            // スナップした座標をメッシュに適用
            transform.position = new Vector3(snappedX, waterHeight, snappedZ);
            transform.rotation = Quaternion.identity;
        }
    }
}