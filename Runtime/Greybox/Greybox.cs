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
///
/// Adaptive subdivision: a GreyboxManager anywhere above in the hierarchy
/// supplies a vertex density (verts/meter). The mesh is subdivided per local
/// axis to approach that density, using the world-space bounding-box extents.
/// Edit-time: rebuilt whenever scale or manager density changes.
/// Runtime: built once on OnEnable, never again.
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

    // ─── Subdivision ────────────────────────────────────────────

    [SerializeField]
    [Tooltip("Coefficient applied to the GreyboxManager's vertex density. " +
             "1 = use manager density as-is. 0 = disable subdivision on this Greybox.")]
    float _subdivisionMultiplier = 1f;

    // ─── UV ─────────────────────────────────────────────────────

    [SerializeField]
    [Tooltip("UV tiling scale. 1 = one texture repeat per meter of world-space face extent. " +
             "Higher values tile the texture more tightly.")]
    float _uvTileScale = 1f;

    // ─── Mesh ───────────────────────────────────────────────────

    Mesh _mesh;

    // Per face: [fixedComp, fixedVal, sComp, tComp]
    //   fixedComp: which uvw component is constant (0=u/X, 1=v/Y, 2=w/Z)
    //   fixedVal:  0 or 1
    //   sComp:     uvw component the row parameter maps to
    //   tComp:     uvw component the col parameter maps to
    //
    // Grid vertex [j][k]: uvw[fixedComp]=fixedVal, uvw[sComp]=j/(rows-1), uvw[tComp]=k/(cols-1)
    // Winding produces outward normals matching the face axis direction.
    static readonly int[,] s_faceParams = new int[6, 4]
    {
        { 0, 1, 1, 2 }, // face 0 (+X): u=1, s→v(Y), t→w(Z)
        { 0, 0, 2, 1 }, // face 1 (-X): u=0, s→w(Z), t→v(Y)
        { 1, 1, 2, 0 }, // face 2 (+Y): v=1, s→w(Z), t→u(X)
        { 1, 0, 0, 2 }, // face 3 (-Y): v=0, s→u(X), t→w(Z)
        { 2, 1, 0, 1 }, // face 4 (+Z): w=1, s→u(X), t→v(Y)
        { 2, 0, 1, 0 }, // face 5 (-Z): w=0, s→v(Y), t→u(X)
    };

    // ─── Edit-time dirty tracking ────────────────────────────────

#if UNITY_EDITOR
    GreyboxManager _cachedManager;
    float          _cachedDensity  = -1f;
    Vector3        _cachedScale;
    bool           _rebuildPending;
#endif

    // ─── Unity lifecycle ─────────────────────────────────────────

    void OnEnable()
    {
        ValidateArrays();
        EnsureMesh();
        RebuildMesh();
    }

    void OnValidate()
    {
        ValidateArrays();
#if UNITY_EDITOR
        // sharedMesh assignment triggers SendMessage which is forbidden inside OnValidate.
        // Defer to the next Update tick instead.
        _rebuildPending = true;
#else
        EnsureMesh();
        RebuildMesh();
#endif
    }

#if UNITY_EDITOR
    void Reset()
    {
        _corners     = DefaultCorners();
        _activeFaces = DefaultActiveFaces();
        EnsureMesh();
        RebuildMesh();
    }

    void Update()
    {
        if (_rebuildPending)
        {
            _rebuildPending = false;
            EnsureMesh();
            RebuildMesh();
            // Refresh cache so the density/scale check below doesn't fire again immediately
            _cachedManager = GetComponentInParent<GreyboxManager>();
            _cachedDensity = _cachedManager != null ? _cachedManager.VertexDensity : 0f;
            _cachedScale   = transform.lossyScale;
            return;
        }

        var   manager = GetComponentInParent<GreyboxManager>();
        float density = manager != null ? manager.VertexDensity : 0f;
        var   scale   = transform.lossyScale;

        if (manager == _cachedManager && density == _cachedDensity && scale == _cachedScale)
            return;

        _cachedManager = manager;
        _cachedDensity = density;
        _cachedScale   = scale;
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

    // ─── Public API ──────────────────────────────────────────────

    /// <summary>Rebuild the procedural mesh from current corner positions, active-face flags, and subdivision.</summary>
    public void RebuildMesh()
    {
        ValidateArrays();
        EnsureMesh();

        float effective = ComputeEffectiveDensity();

        // Pre-compute per-face grid sizes based on actual face edge lengths
        var faceRows = new int[6];
        var faceCols = new int[6];
        for (int face = 0; face < 6; face++)
        {
            if (!_activeFaces[face]) continue;
            ComputeFaceCuts(face, effective, out int sc, out int tc);
            faceRows[face] = sc + 2;
            faceCols[face] = tc + 2;
        }

        // Count total verts and tris across all active faces
        int totalVerts = 0, totalTris = 0;
        for (int face = 0; face < 6; face++)
        {
            if (!_activeFaces[face]) continue;
            totalVerts += faceRows[face] * faceCols[face];
            totalTris  += (faceRows[face] - 1) * (faceCols[face] - 1) * 2;
        }

        var verts   = new Vector3[totalVerts];
        var normals = new Vector3[totalVerts];
        var uvs     = new Vector2[totalVerts];
        var tris    = new int[totalTris * 3];

        int vBase = 0, tBase = 0;

        for (int face = 0; face < 6; face++)
        {
            if (!_activeFaces[face]) continue;

            int fixedComp = s_faceParams[face, 0];
            int fixedVal  = s_faceParams[face, 1];
            int sComp     = s_faceParams[face, 2];
            int tComp     = s_faceParams[face, 3];
            int rows      = faceRows[face];
            int cols      = faceCols[face];

            // Flat face normal from deformed corners at (s=0,t=0), (s=1,t=0), (s=0,t=1)
            Vector3 ca = SampleFace(fixedComp, fixedVal, sComp, tComp, 0f, 0f);
            Vector3 cb = SampleFace(fixedComp, fixedVal, sComp, tComp, 1f, 0f);
            Vector3 cd = SampleFace(fixedComp, fixedVal, sComp, tComp, 0f, 1f);
            Vector3 faceNormal = Vector3.Cross(cb - ca, cd - ca).normalized;

            // World-space extents along each face axis for UV projection
            Vector3 lossyScale = transform.lossyScale;
            float sScale = Mathf.Abs(lossyScale[sComp]);
            float tScale = Mathf.Abs(lossyScale[tComp]);

            // Vertex grid
            for (int j = 0; j < rows; j++)
            {
                float s = j / (float)(rows - 1);
                for (int k = 0; k < cols; k++)
                {
                    float t   = k / (float)(cols - 1);
                    int   vi  = vBase + j * cols + k;
                    Vector3 p = SampleFace(fixedComp, fixedVal, sComp, tComp, s, t);
                    verts[vi]   = p;
                    normals[vi] = faceNormal;
                    uvs[vi]     = new Vector2(p[sComp] * sScale * _uvTileScale,
                                             p[tComp] * tScale * _uvTileScale);
                }
            }

            // Quad triangles
            for (int j = 0; j < rows - 1; j++)
            {
                for (int k = 0; k < cols - 1; k++)
                {
                    int v0 = vBase + j       * cols + k;
                    int v1 = vBase + (j + 1) * cols + k;
                    int v2 = vBase + (j + 1) * cols + (k + 1);
                    int v3 = vBase + j       * cols + (k + 1);

                    tris[tBase]     = v0;
                    tris[tBase + 1] = v1;
                    tris[tBase + 2] = v2;
                    tris[tBase + 3] = v0;
                    tris[tBase + 4] = v2;
                    tris[tBase + 5] = v3;
                    tBase += 6;
                }
            }

            vBase += rows * cols;
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

    // ─── Subdivision ─────────────────────────────────────────────

    float ComputeEffectiveDensity()
    {
        float multiplier = Mathf.Max(0f, _subdivisionMultiplier);
        if (multiplier == 0f) return 0f;
        var manager = GetComponentInParent<GreyboxManager>();
        float density = manager != null ? manager.VertexDensity : 0f;
        return density * multiplier;
    }

    // Returns per-face edge-loop cuts based on the actual world-space edge lengths
    // of that face's two parametric directions. Takes the longer of the two opposite
    // edges in each direction so sheared faces don't get under-subdivided.
    void ComputeFaceCuts(int face, float effective, out int sCuts, out int tCuts)
    {
        if (effective <= 0f) { sCuts = 0; tCuts = 0; return; }

        int fixedComp = s_faceParams[face, 0];
        int fixedVal  = s_faceParams[face, 1];
        int sComp     = s_faceParams[face, 2];
        int tComp     = s_faceParams[face, 3];

        Vector3 p00 = SampleFace(fixedComp, fixedVal, sComp, tComp, 0f, 0f);
        Vector3 p10 = SampleFace(fixedComp, fixedVal, sComp, tComp, 1f, 0f);
        Vector3 p01 = SampleFace(fixedComp, fixedVal, sComp, tComp, 0f, 1f);
        Vector3 p11 = SampleFace(fixedComp, fixedVal, sComp, tComp, 1f, 1f);

        float sLen = (transform.TransformVector(p10 - p00).magnitude
                    + transform.TransformVector(p11 - p01).magnitude) * 0.5f;
        float tLen = (transform.TransformVector(p01 - p00).magnitude
                    + transform.TransformVector(p11 - p10).magnitude) * 0.5f;

        sCuts = Mathf.Max(1, Mathf.RoundToInt(sLen * effective)) - 1;
        tCuts = Mathf.Max(1, Mathf.RoundToInt(tLen * effective)) - 1;
    }

    // Trilinear interpolation of the 8 corners.
    // u = X param [0,1], v = Y param [0,1], w = Z param [0,1]
    Vector3 TriLerp(float u, float v, float w)
    {
        Vector3 c00 = Vector3.Lerp(_corners[0], _corners[1], u);
        Vector3 c10 = Vector3.Lerp(_corners[2], _corners[3], u);
        Vector3 c01 = Vector3.Lerp(_corners[4], _corners[5], u);
        Vector3 c11 = Vector3.Lerp(_corners[6], _corners[7], u);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, v), Vector3.Lerp(c01, c11, v), w);
    }

    // Samples a local-space position on a face at parametric (s, t).
    Vector3 SampleFace(int fixedComp, int fixedVal, int sComp, int tComp, float s, float t)
    {
        float[] uvw    = new float[3];
        uvw[fixedComp] = fixedVal;
        uvw[sComp]     = s;
        uvw[tComp]     = t;
        return TriLerp(uvw[0], uvw[1], uvw[2]);
    }

    // ─── Mesh lifecycle ───────────────────────────────────────────

    void EnsureMesh()
    {
        if (_mesh != null) return;
        _mesh = new Mesh { name = "Greybox Mesh", hideFlags = HideFlags.HideAndDontSave };
    }

    // ─── Defaults ────────────────────────────────────────────────

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
