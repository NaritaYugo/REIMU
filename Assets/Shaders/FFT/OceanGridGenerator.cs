using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class OceanGridGenerator : MonoBehaviour
{
    public int resolution = 100; // 100x100分割
    public float size = 10.0f;   // 全体のサイズ

    void Start()
    {
        GenerateGrid();
    }

    void GenerateGrid()
    {
        Mesh mesh = new Mesh();
        mesh.name = "OceanGrid";
        // 頂点数が65535を超える可能性があるため32bitインデックスに変更
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; 

        int numVertices = (resolution + 1) * (resolution + 1);
        Vector3[] vertices = new Vector3[numVertices];
        Vector2[] uvs = new Vector2[numVertices];
        int[] triangles = new int[resolution * resolution * 6];

        int v = 0;
        int t = 0;
        float step = size / resolution;

        for (int z = 0; z <= resolution; z++)
        {
            for (int x = 0; x <= resolution; x++)
            {
                // 中心を(0,0,0)にして頂点を配置
                vertices[v] = new Vector3(x * step - size / 2, 0, z * step - size / 2);
                // UV座標を 0.0 ~ 1.0 にマッピング
                uvs[v] = new Vector2((float)x / resolution, (float)z / resolution);

                if (x < resolution && z < resolution)
                {
                    triangles[t] = v;
                    triangles[t + 1] = v + resolution + 1;
                    triangles[t + 2] = v + 1;
                    triangles[t + 3] = v + 1;
                    triangles[t + 4] = v + resolution + 1;
                    triangles[t + 5] = v + resolution + 2;
                    t += 6;
                }
                v++;
            }
        }

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();

        GetComponent<MeshFilter>().mesh = mesh;
    }
}