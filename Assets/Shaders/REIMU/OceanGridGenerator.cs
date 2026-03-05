using UnityEngine;

public class ClipmapOceanManager : MonoBehaviour
{
    [Header("Clipmap Settings")]
    public Material oceanMaterial;
    public int resolution = 128; // 4の倍数を推奨 (64, 128など)
    public float baseSize = 100.0f; // LOD0（一番細かい中心部分）のサイズ

    void Start()
    {
        // LOD0: 中心
        CreateLODMesh("LOD0_Center", baseSize, 0f);

        // LOD1: 中間
        CreateLODMesh("LOD1_Ring", baseSize * 2f, baseSize);

        // LOD2: 遠景
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

        Mesh mesh = new Mesh();
        mesh.name = name;
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        int numVertices = (resolution + 1) * (resolution + 1);
        Vector3[] vertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        // 穴を空けるため、配列リストで動的に三角形を構築
        System.Collections.Generic.List<int> triangles = new System.Collections.Generic.List<int>();

        int v = 0;
        float step = size / resolution;
        float holeRadius = holeSize / 2.0f;

        for (int z = 0; z <= resolution; z++)
        {
            for (int x = 0; x <= resolution; x++)
            {
                // 頂点の配置
                float px = x * step - size / 2;
                float pz = z * step - size / 2;
                vertices[v] = new Vector3(px, 0, pz);
                uvs[v] = new Vector2((float)x / resolution, (float)z / resolution);

                // 面（ポリゴン）を張る処理
                if (x < resolution && z < resolution)
                {
                    // この四角形（Quad）の中心座標を計算
                    float quadCenterX = px + step / 2;
                    float quadCenterZ = pz + step / 2;

                    // 四角形が「穴（holeSize）」の内側に入っているか判定
                    bool insideHole = (Mathf.Abs(quadCenterX) < holeRadius) && (Mathf.Abs(quadCenterZ) < holeRadius);

                    // 穴の外側の場合だけ、三角形を追加（これがドーナツ化の魔法）
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

        MaterialPropertyBlock block = new MaterialPropertyBlock();
        block.SetFloat("_MeshSize", size);
        block.SetFloat("_CurrentGridSize", step);
        mr.SetPropertyBlock(block);
    }
}