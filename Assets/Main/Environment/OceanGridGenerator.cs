using UnityEngine;
using System.Collections.Generic;

// =========================================================================
// 海洋描画用のクリップマップ(同心円状・ドーナツ状のLODメッシュ)を動的に生成する
// =========================================================================
public class OceanGridGenerator : MonoBehaviour
{
    [Header("Clipmap Settings")]
    public Material oceanMaterial;
    [Tooltip("LODの解像度。4の倍数を推奨")]
    public int resolution = 128; 
    [Tooltip("LOD0（一番細かい中心部分）の全体のサイズ")]
    public float baseSize = 100.0f;

    void Start()
    {
        // LOD0: 中心 (穴なし)
        CreateLODMesh("LOD0_Center", baseSize, 0f);

        // LOD1: 中間 (ドーナツ状)
        CreateLODMesh("LOD1_Ring", baseSize * 2f, baseSize);

        // LOD2: 遠景 (ドーナツ状)
        CreateLODMesh("LOD2_Ring", baseSize * 4f, baseSize * 2f);
    }

    void CreateLODMesh(string name, float size, float holeSize)
    {
        GameObject lodObj = new GameObject(name);
        lodObj.transform.SetParent(this.transform);
        lodObj.transform.localPosition = Vector3.zero;

        MeshFilter mf = lodObj.AddComponent<MeshFilter>();
        MeshRenderer mr = lodObj.AddComponent<MeshRenderer>();
        if (oceanMaterial != null) mr.material = oceanMaterial;

        Mesh mesh = new Mesh { name = name };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        int numVertices = (resolution + 1) * (resolution + 1);
        Vector3[] vertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        List<int> triangles = new List<int>();

        int v = 0;
        float step = size / resolution;
        float holeRadius = holeSize / 2.0f;

        for (int z = 0; z <= resolution; z++)
        {
            for (int x = 0; x <= resolution; x++)
            {
                // 頂点の配置
                float px = x * step - size / 2f;
                float pz = z * step - size / 2f;
                vertices[v] = new Vector3(px, 0, pz);
                uvs[v] = new Vector2((float)x / resolution, (float)z / resolution);

                // 面（ポリゴン）を張る処理
                if (x < resolution && z < resolution)
                {
                    float quadCenterX = px + step / 2f;
                    float quadCenterZ = pz + step / 2f;

                    // 四角形が「穴」の内側に入っているか判定
                    bool insideHole = (Mathf.Abs(quadCenterX) < holeRadius) && (Mathf.Abs(quadCenterZ) < holeRadius);

                    // 穴の外側の場合だけ、三角形を追加
                    if (!insideHole)
                    {
                        triangles.Add(v);
                        triangles.Add(v + resolution + 1);
                        triangles.Add(v + 1);
                        triangles.Add(v + 1);
                        triangles.Add(v + resolution + 1);
                        triangles.Add(v + resolution + 2);
                    }
                }
                v++;
            }
        }

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        
        // 巨大なメッシュがカメラ外で消えないように境界を拡張
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(size * 2, 1000f, size * 2));

        mf.mesh = mesh;

        // シェーダー側でLODの継ぎ目を綺麗に補間するための情報を渡す
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        block.SetFloat("_MeshSize", size);
        block.SetFloat("_CurrentGridSize", step);
        mr.SetPropertyBlock(block);
    }
}