using UnityEngine;

/// <summary>
/// Procedural deformable box primitive. Stores 8 corner positions in local space
/// and rebuilds a mesh from them on demand. Pivot is at center-bottom.
///
/// Corner index encoding matches QuickTransform's box convention:
///   bit0 = X sign (0=negative, 1=positive)
///   bit1 = Y sign (0=bottom,   1=top)
///   bit2 = Z sign (0=negative, 1=positive)
///
/// Default shape: 1×1×1 unit cube, Y range [0,1], XZ range [-0.5, 0.5].
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Greybox : MonoBehaviour
{
    // ─── Corner data ────────────────────────────────────────────

    [SerializeField]
    [Tooltip("8 local-space corner positions. Index bits: bit0=+X, bit1=+Y, bit2=+Z.")]
    Vector3[] _corners = DefaultCorners();

    public Vector3[] Corners => _corners;

    // ─── Face visibility ────────────────────────────────────────

    [SerializeField]
    [Tooltip("6 flags controlling which faces are included in the mesh. " +
             "Toggle via W + middle-click on a face in the Scene View. " +
             "Indices match QuickTransform face convention: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z.")]
    bool[] _activeFaces = DefaultActiveFaces();

    public bool[] ActiveFaces => _activeFaces;

    // ─── Mesh ───────────────────────────────────────────────────

    Mesh _mesh;

    static readonly int[][] FaceCornerIndices =
    {
        new[] { 1, 3, 7, 5 }, // face 0: +X
        new[] { 0, 4, 6, 2 }, // face 1: -X
        new[] { 2, 6, 7, 3 }, // face 2: +Y
        new[] { 0, 1, 5, 4 }, // face 3: -Y
        new[] { 4, 5, 7, 6 }, // face 4: +Z
        new[] { 0, 2, 3, 1 }, // face 5: -Z
    };

    // ─── Unity lifecycle ────────────────────────────────────────

    void OnEnable()
    {
        ValidateArrays();
        EnsureMesh();
        RebuildMesh();
    }

    void OnValidate()
    {
        ValidateArrays();
        EnsureMesh();
        RebuildMesh();
    }

#if UNITY_EDITOR
    void Reset()
    {
        _corners     = DefaultCorners();
        _activeFaces = DefaultActiveFaces();
        EnsureMesh();
        RebuildMesh();
    }
#endif

    void ValidateArrays()
    {
        if (_corners == null || _corners.Length != 8)
            _corners = DefaultCorners();
        if (_activeFaces == null || _activeFaces.Length != 6)
            _activeFaces = DefaultActiveFaces();
    }

    // ─── Public API ─────────────────────────────────────────────

    /// <summary>Rebuild the procedural mesh from current corner positions and active-face flags.</summary>
    public void RebuildMesh()
    {
        ValidateArrays();
        EnsureMesh();

        int activeFaceCount = 0;
        for (int f = 0; f < 6; f++)
            if (_activeFaces[f]) activeFaceCount++;

        var verts   = new Vector3[activeFaceCount * 4];
        var normals = new Vector3[activeFaceCount * 4];
        var uvs     = new Vector2[activeFaceCount * 4];
        var tris    = new int[activeFaceCount * 6];

        int vBase = 0, tBase = 0;
        for (int face = 0; face < 6; face++)
        {
            if (!_activeFaces[face]) continue;

            int[] ci = FaceCornerIndices[face];
            Vector3 a = _corners[ci[0]];
            Vector3 b = _corners[ci[1]];
            Vector3 c = _corners[ci[2]];
            Vector3 d = _corners[ci[3]];

            verts[vBase]     = a;
            verts[vBase + 1] = b;
            verts[vBase + 2] = c;
            verts[vBase + 3] = d;

            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            normals[vBase]     = n;
            normals[vBase + 1] = n;
            normals[vBase + 2] = n;
            normals[vBase + 3] = n;

            uvs[vBase]     = new Vector2(0, 0);
            uvs[vBase + 1] = new Vector2(1, 0);
            uvs[vBase + 2] = new Vector2(1, 1);
            uvs[vBase + 3] = new Vector2(0, 1);

            tris[tBase]     = vBase;
            tris[tBase + 1] = vBase + 1;
            tris[tBase + 2] = vBase + 2;
            tris[tBase + 3] = vBase;
            tris[tBase + 4] = vBase + 2;
            tris[tBase + 5] = vBase + 3;

            vBase += 4;
            tBase += 6;
        }

        _mesh.Clear();
        _mesh.vertices  = verts;
        _mesh.normals   = normals;
        _mesh.uv        = uvs;
        _mesh.triangles = tris;
        _mesh.RecalculateBounds();

        var mf = GetComponent<MeshFilter>();
        if (mf != null) mf.sharedMesh = _mesh;

        var mc = GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = _mesh;
    }

    /// <summary>Returns the 8 corners transformed to world space.</summary>
    public Vector3[] GetWorldCorners()
    {
        var result = new Vector3[8];
        for (int i = 0; i < 8; i++)
            result[i] = transform.TransformPoint(_corners[i]);
        return result;
    }

    // ─── Helpers ─────────────────────────────────────────────────

    void EnsureMesh()
    {
        if (_mesh != null) return;
        _mesh = new Mesh { name = "Greybox Mesh", hideFlags = HideFlags.HideAndDontSave };
    }

    public static Vector3[] DefaultCorners()
    {
        // Pivot at center-bottom: Y in [0,1], XZ in [-0.5, 0.5]
        return new Vector3[]
        {
            new Vector3(-0.5f, 0f, -0.5f), // 0: -X, bot, -Z
            new Vector3( 0.5f, 0f, -0.5f), // 1: +X, bot, -Z
            new Vector3(-0.5f, 1f, -0.5f), // 2: -X, top, -Z
            new Vector3( 0.5f, 1f, -0.5f), // 3: +X, top, -Z
            new Vector3(-0.5f, 0f,  0.5f), // 4: -X, bot, +Z
            new Vector3( 0.5f, 0f,  0.5f), // 5: +X, bot, +Z
            new Vector3(-0.5f, 1f,  0.5f), // 6: -X, top, +Z
            new Vector3( 0.5f, 1f,  0.5f), // 7: +X, top, +Z
        };
    }

    static bool[] DefaultActiveFaces() =>
        new bool[] { true, true, true, true, true, true };
}
