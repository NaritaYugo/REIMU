using UnityEngine;

public class FFTTracker : MonoBehaviour
{
    public Transform target;
    public float waterHeight = 0.0f;
    
    [Header("References")]
    [Tooltip("同じオブジェクト、またはシーン内のGridGeneratorをアタッチ")]
    public OceanGridGenerator gridGenerator;

    void Start()
    {
        if (gridGenerator == null) {
            gridGenerator = GetComponent<OceanGridGenerator>();
        }
    }

    void LateUpdate()
    {
        if (target != null && gridGenerator != null)
        {
            float step = gridGenerator.baseSize / gridGenerator.resolution;

            // プレイヤーの位置をスナップ
            float snappedX = Mathf.Round(target.position.x / step) * step;
            float snappedZ = Mathf.Round(target.position.z / step) * step;

            transform.position = new Vector3(snappedX, waterHeight, snappedZ);
            transform.rotation = Quaternion.identity;
        }
    }
}