using UnityEngine;

/// <summary>
/// Flat quad primitive for level blockout. A unit 1x1 quad in the local XZ plane,
/// centered on the pivot, facing local +Y. Size comes from the transform scale.
///
/// Adaptive subdivision follows the Greybox rules: a GreyboxManager anywhere above
/// in the hierarchy supplies a vertex density (verts/meter), multiplied by this
/// primitive's SubdivisionMultiplier, and the quad is gridded per axis using its
/// world-space edge lengths.
/// </summary>
public class Greyquad : GreyPrimitive
{
    // ─── UV ─────────────────────────────────────────────────────

    [SerializeField]
    [Tooltip("UV tiling scale. 1 = the texture stretches exactly once across the quad. " +
             "Higher values tile it more.")]
    float _uvTileScale = 1f;

    public float UvTileScale => _uvTileScale;

    // ─── GreyPrimitive overrides ─────────────────────────────────

    protected override void ResetToDefaults()
    {
        _uvTileScale = 1f;
    }

    protected override void GenerateMesh(Mesh mesh)
    {
        float effective = ComputeEffectiveDensity();
        ComputeCuts(effective, out int sCuts, out int tCuts);

        // s runs along local Z, t along local X — same parametrization as the Greybox +Y face.
        int rows = sCuts + 2;
        int cols = tCuts + 2;

        var verts   = new Vector3[rows * cols];
        var normals = new Vector3[rows * cols];
        var uvs     = new Vector2[rows * cols];
        var tris    = new int[(rows - 1) * (cols - 1) * 6];

        for (int j = 0; j < rows; j++)
        {
            float s = j / (float)(rows - 1);
            for (int k = 0; k < cols; k++)
            {
                float t  = k / (float)(cols - 1);
                int   vi = j * cols + k;
                verts[vi]   = new Vector3(t - 0.5f, 0f, s - 0.5f);
                normals[vi] = Vector3.up;
                uvs[vi]     = new Vector2(t * _uvTileScale, s * _uvTileScale);
            }
        }

        int tBase = 0;
        for (int j = 0; j < rows - 1; j++)
        {
            for (int k = 0; k < cols - 1; k++)
            {
                int v0 = j       * cols + k;
                int v1 = (j + 1) * cols + k;
                int v2 = (j + 1) * cols + (k + 1);
                int v3 = j       * cols + (k + 1);

                tris[tBase]     = v0;
                tris[tBase + 1] = v1;
                tris[tBase + 2] = v2;
                tris[tBase + 3] = v0;
                tris[tBase + 4] = v2;
                tris[tBase + 5] = v3;
                tBase += 6;
            }
        }

        mesh.Clear();
        mesh.vertices  = verts;
        mesh.normals   = normals;
        mesh.uv        = uvs;
        mesh.triangles = tris;
    }

    // ─── Subdivision ─────────────────────────────────────────────

    void ComputeCuts(float effective, out int sCuts, out int tCuts)
    {
        if (effective <= 0f) { sCuts = 0; tCuts = 0; return; }

        float sLen = transform.TransformVector(Vector3.forward).magnitude;
        float tLen = transform.TransformVector(Vector3.right).magnitude;

        sCuts = Mathf.Max(1, Mathf.RoundToInt(sLen * effective)) - 1;
        tCuts = Mathf.Max(1, Mathf.RoundToInt(tLen * effective)) - 1;
    }
}
