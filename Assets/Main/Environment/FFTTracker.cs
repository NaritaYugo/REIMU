using UnityEngine;

// =========================================================================
// FFTオーシャンのメッシュをプレイヤー(カメラ)に追従させるスクリプト
// =========================================================================
public class FFTTracker : MonoBehaviour
{
    public Transform target;
    public float waterHeight = 0.0f; // 初期値を0.0fに（必要に応じてインスペクタで変更）
    
    [Header("References")]
    [Tooltip("同じオブジェクト、またはシーン内のGridGeneratorをアタッチ")]
    public OceanGridGenerator gridGenerator;

    void Start()
    {
        // アタッチし忘れていた場合は自動で取得
        if (gridGenerator == null) {
            gridGenerator = GetComponent<OceanGridGenerator>();
        }
    }

    void LateUpdate()
    {
        if (target != null && gridGenerator != null)
        {
            // LOD0（一番細かい中心部分）の1マスのサイズを計算
            float step = gridGenerator.baseSize / gridGenerator.resolution;

            // プレイヤーの位置を「1マス単位」にスナップ（切り捨て/四捨五入）させる
            // これにより、メッシュが滑らかに移動するのではなく、カクカクとセル単位で移動する。
            // 見た目上は波の頂点が同じ場所に留まるため、メッシュが移動していることをプレイヤーに悟らせない。
            float snappedX = Mathf.Round(target.position.x / step) * step;
            float snappedZ = Mathf.Round(target.position.z / step) * step;

            // スナップした座標をメッシュに適用
            transform.position = new Vector3(snappedX, waterHeight, snappedZ);
            transform.rotation = Quaternion.identity;
        }
    }
}