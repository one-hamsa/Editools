using UnityEngine;

/// <summary>
/// Edit-time greybox primitive that generates a planar grid mesh which conforms
/// to nearby static geometry via BFS sampling + relax (the same algorithm used
/// by ProjectionPlacer's paintshop cage, ported to drive a mesh directly).
///
/// At play time the component is inert — the mesh is serialized inline on the
/// GameObject and renders through the standard <see cref="MeshFilter"/> /
/// <see cref="MeshRenderer"/> path with no Sticker code running.
///
/// Sizing is driven by <see cref="MainTex"/>: the longer image side maps to
/// <see cref="k_BaseSideMeters"/>, the shorter scales by aspect ratio.
/// The texture itself is also pushed into the sibling <c>LocalMaterial</c>'s
/// <c>_MainTex</c> override (reached via reflection — Editools must not gain
/// a hard reference to <c>UD.Rendering</c>, which already references Editools).
/// </summary>
public class Sticker : GreyPrimitive
{
    public const float k_BaseSideMeters = 5f;

    // ─── Texture / sizing ────────────────────────────────────────

    [SerializeField]
    [Tooltip("Texture to display on the sticker. Drives the mesh dimensions " +
             "(longer side = 5 m, shorter scales by aspect ratio) AND is injected " +
             "into the sibling LocalMaterial's _MainTex slot.")]
    Texture2D _mainTex;

    public Texture2D MainTex => _mainTex;

    // ─── Subdivision ─────────────────────────────────────────────

    [SerializeField, Min(2)]
    [Tooltip("Grid subdivisions along the texture's width (U axis). " +
             "Higher = finer conformation to underlying geometry, more verts.")]
    int _columns = 16;

    [SerializeField, Min(2)]
    [Tooltip("Grid subdivisions along the texture's height (V axis). " +
             "Higher = finer conformation to underlying geometry, more verts.")]
    int _rows = 16;

    // ─── Raycast slab ────────────────────────────────────────────

    [SerializeField, Min(0f)]
    [Tooltip("Distance the conforming rays travel ABOVE the sticker's tangent plane " +
             "(along the sticker's +forward) before giving up. In meters.")]
    float _rayDistAbove = 0.25f;

    [SerializeField, Min(0f)]
    [Tooltip("Distance the conforming rays travel BELOW the sticker's tangent plane " +
             "(along the sticker's -forward) before giving up. In meters.")]
    float _rayDistBelow = 0.25f;

    [SerializeField, Range(0f, 89f)]
    [Tooltip("If a vertex's straight raycast misses, retry once with the ray bent this many " +
             "degrees toward the sticker's center. Helps catch grazing hits on curved surfaces.")]
    float _retryBendAngle = 30f;

    // ─── Surface offset ──────────────────────────────────────────

    [SerializeField]
    [Tooltip("Offset pushed along each vertex's normal AFTER conformation, in meters. " +
             "Positive values lift the sticker slightly off the surface to avoid z-fighting / intersection. " +
             "Typical values 0.001–0.01.")]
    float _surfaceOffset = 0.005f;

    // ─── Relax ───────────────────────────────────────────────────

    [SerializeField, Range(0, 20)]
    [Tooltip("Number of relax passes after BFS sampling. Each pass smooths positions " +
             "toward neighbor averages then restores edge lengths. Higher = smoother, more cost.")]
    int _relaxIterations = 5;

    [SerializeField, Range(0f, 1f)]
    [Tooltip("How aggressively each relax pass averages a vertex toward its neighbors. " +
             "0 = no smoothing, 1 = snap to neighbor average each pass.")]
    float _relaxStrength = 0.5f;

    [SerializeField, Range(0f, 1f)]
    [Tooltip("How strongly each relax pass restores original edge lengths between neighbors. " +
             "Higher = grid resists stretching, preserves quad shape.")]
    float _relaxRigidity = 0.5f;

    // ─── Debug ───────────────────────────────────────────────────

    [SerializeField]
    [Tooltip("Draw the full ProjectionPlacer-style debug gizmos when selected: sampling rectangle, " +
             "each cage point with its normal and slab depth, and the BFS sampling diagnostic " +
             "(hit/miss-coloured points, cast rays, neighbor connection lines).")]
    bool _drawCageOutline = true;

    // Cached world-space cage from the last rebuild — used by OnDrawGizmosSelected.
    // NonSerialized: rebuilds on next OnValidate after a domain reload.
    [System.NonSerialized] Vector3[] _cageWorld;
    [System.NonSerialized] Vector3[] _cageNormalsWorld;
    [System.NonSerialized] int       _cageCols;
    [System.NonSerialized] int       _cageRows;
    [System.NonSerialized] StickerGridSolver.Viz _bfsViz;

    // ─── Sizing helper ───────────────────────────────────────────

    /// <summary>Effective sticker size in meters. Longer side = 5 m, shorter scales by aspect.</summary>
    public Vector2 ComputeSize()
    {
        if (_mainTex == null)
            return new Vector2(k_BaseSideMeters, k_BaseSideMeters);
        float tw = Mathf.Max(1, _mainTex.width);
        float th = Mathf.Max(1, _mainTex.height);
        float longest = Mathf.Max(tw, th);
        return new Vector2(k_BaseSideMeters * (tw / longest),
                           k_BaseSideMeters * (th / longest));
    }

    // ─── GreyPrimitive overrides ─────────────────────────────────

    protected override void ResetToDefaults()
    {
        // All defaults live on the serialized field initializers above. Nothing extra to clear.
    }

    // Runs ahead of every RebuildMesh — inspector change-check, undo, scene tools, Reset, button.
    // Mirrors the texture into the sibling LocalMaterial's _MainTex slot in the same push tick
    // that rebuilds the mesh (texture drives mesh dimensions anyway).
    protected override void OnBeforeRebuild() => SyncMainTexToLocalMaterial();

    protected override void GenerateMesh(Mesh mesh)
    {
        // Empty the mesh first so the Sticker's own renderer holds no triangles
        // during the raycast pass — avoids the BFS hitting its previous build.
        mesh.Clear();

#if UNITY_EDITOR
        var size = ComputeSize();
        int cols = Mathf.Max(2, _columns);
        int rows = Mathf.Max(2, _rows);

        var raycaster = new StickerRaycaster(ComputeSlabBounds(size));

        var input = new StickerGridSolver.Input
        {
            Width           = size.x,
            Height          = size.y,
            Cols            = cols,
            Rows            = rows,
            AnchorPos       = transform.position,
            AnchorNormal    = transform.forward,
            AnchorUp        = transform.up,
            RayDistAbove    = _rayDistAbove,
            RayDistBelow    = _rayDistBelow,
            RetryBendAngle  = _retryBendAngle,
            RelaxIterations = _relaxIterations,
            RelaxStrength   = _relaxStrength,
            RelaxRigidity   = _relaxRigidity,
        };
        _bfsViz ??= new StickerGridSolver.Viz();
        StickerGridSolver.Solve(input, raycaster.Raycast, out var pts, out var nrms, _bfsViz);

        // Lift each vertex along its surface normal so the sticker hovers slightly above
        // the geometry it conformed to. Both the mesh and the cage gizmo use the lifted points.
        if (_surfaceOffset != 0f)
        {
            for (int i = 0; i < pts.Length; i++)
                pts[i] += nrms[i] * _surfaceOffset;
        }

        _cageWorld        = pts;
        _cageNormalsWorld = nrms;
        _cageCols         = cols;
        _cageRows         = rows;

        int total = cols * rows;
        var verts = new Vector3[total];
        var uvs   = new Vector2[total];
        var tris  = new int[(cols - 1) * (rows - 1) * 6];

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            int i = r * cols + c;
            verts[i] = transform.InverseTransformPoint(pts[i]);
            // Planar UVs assigned on the undeformed grid so the texture stretches with the deformation.
            uvs  [i] = new Vector2(c / (float)(cols - 1), r / (float)(rows - 1));
        }

        // Winding: front face on the sticker's +forward side (the viewer side, away from the surface).
        // With rows running +Y and cols running +X in local tangent space, that's v0→v3→v2 / v0→v2→v1
        // — clockwise as seen from +forward, which Unity treats as front-facing.
        int t = 0;
        for (int r = 0; r < rows - 1; r++)
        for (int c = 0; c < cols - 1; c++)
        {
            int v0 = r * cols + c;
            int v1 = (r + 1) * cols + c;
            int v2 = (r + 1) * cols + (c + 1);
            int v3 = r * cols + (c + 1);
            tris[t++] = v0; tris[t++] = v3; tris[t++] = v2;
            tris[t++] = v0; tris[t++] = v2; tris[t++] = v1;
        }

        mesh.vertices  = verts;
        mesh.uv        = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
#endif
    }

#if UNITY_EDITOR
    // ─── Slab bounds ─────────────────────────────────────────────

    /// <summary>
    /// World-space AABB covering everywhere the BFS rays may sample. Used to pre-filter
    /// the raycaster's candidate list so large scenes don't pay per-triangle costs for
    /// renderers nowhere near the sticker. 20% padding accounts for predicted-cell drift
    /// during the BFS expansion.
    /// </summary>
    Bounds ComputeSlabBounds(Vector2 size)
    {
        float hw = size.x * 0.6f;
        float hh = size.y * 0.6f;
        float hz = (_rayDistAbove + _rayDistBelow) * 0.6f;

        var b = new Bounds(transform.position, Vector3.zero);
        for (int dx = -1; dx <= 1; dx += 2)
        for (int dy = -1; dy <= 1; dy += 2)
        for (int dz = -1; dz <= 1; dz += 2)
            b.Encapsulate(transform.TransformPoint(new Vector3(dx * hw, dy * hh, dz * hz)));
        return b;
    }
#endif

#if UNITY_EDITOR
    // ─── LocalMaterial binding ───────────────────────────────────

    void SyncMainTexToLocalMaterial() => LocalMaterialBridge.PushMainTex(gameObject, _mainTex);

    protected override void Reset()
    {
        LocalMaterialBridge.EnsureOn(gameObject);
        base.Reset();
    }

    // ─── Gizmos (matches ProjectionPlacer's viz layers) ──────────

    void OnDrawGizmosSelected()
    {
        if (!_drawCageOutline) return;

        DrawSamplingRectangle();
        DrawCagePoints();
        ReplayBfsViz();
    }

    void DrawSamplingRectangle()
    {
        var size = ComputeSize();
        float hw = size.x * 0.5f;
        float hh = size.y * 0.5f;
        var r = transform.right;
        var u = transform.up;
        var o = transform.position;

        Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.4f);
        Gizmos.DrawLine(o - r * hw - u * hh, o + r * hw - u * hh);
        Gizmos.DrawLine(o + r * hw - u * hh, o + r * hw + u * hh);
        Gizmos.DrawLine(o + r * hw + u * hh, o - r * hw + u * hh);
        Gizmos.DrawLine(o - r * hw + u * hh, o - r * hw - u * hh);
    }

    void DrawCagePoints()
    {
        if (_cageWorld == null || _cageNormalsWorld == null) return;
        if (_cageCols < 2 || _cageRows < 2) return;
        if (_cageWorld.Length != _cageCols * _cageRows) return;

        float dist = _rayDistAbove + _rayDistBelow;
        var solid = new Color(0.6f, 1f, 0.4f, 1f);
        var faded = new Color(0.6f, 1f, 0.4f, 0.5f);

        for (int i = 0; i < _cageWorld.Length; i++)
        {
            var p = _cageWorld[i];
            var n = _cageNormalsWorld[i];
            Gizmos.color = solid;
            Gizmos.DrawSphere(p, 0.01f);
            Gizmos.DrawRay(p, n * 0.1f);
            if (dist > 0f)
            {
                Gizmos.color = faded;
                Gizmos.DrawRay(p, -n * dist);
            }
        }
    }

    void ReplayBfsViz()
    {
        if (_bfsViz == null) return;
        var cmds = _bfsViz.Commands;
        for (int i = 0; i < cmds.Count; i++)
        {
            var cmd = cmds[i];
            Gizmos.color = cmd.Color;
            switch (cmd.Kind)
            {
                case StickerGridSolver.Viz.CmdKind.Sphere: Gizmos.DrawSphere(cmd.A, cmd.Radius); break;
                case StickerGridSolver.Viz.CmdKind.Line:   Gizmos.DrawLine  (cmd.A, cmd.B);      break;
                case StickerGridSolver.Viz.CmdKind.Ray:    Gizmos.DrawRay   (cmd.A, cmd.B);      break;
            }
        }
    }
#endif
}
