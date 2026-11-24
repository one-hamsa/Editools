using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural box-lofted road primitive. Shares Greypipe's spline and handle behavior,
/// but lofts a box cross-section (width x height) rather than a
/// circle, adds per-vertex Banking (twist around the tangent), and exposes 6 face-visibility
/// flags so the user can hide individual sides (top / bottom / left / right / start cap / end cap)
/// in the Grey Primitive panel (a ledge: MMB on its face handles in Edit Mode).
///
/// Default visible faces at spawn: Top + the two lateral sides. Bottom and end caps are hidden.
///
/// A road can be a ledge: it follows one edge of a source road over a range of the source's
/// length and is rebuilt whenever the source is (see the Ledge link section). A road can also
/// be a branch: its first vertex stays on another road's spline (see the Branch anchor section).
/// </summary>
public class Greyroad : GreyPrimitive
{
    // ─── Face index constants ───────────────────────────────────

    public const int FaceTop       = 0;
    public const int FaceBottom    = 1;
    public const int FaceLeftSide  = 2;
    public const int FaceRightSide = 3;
    public const int FaceStartCap  = 4;
    public const int FaceEndCap    = 5;

    // ─── Vertex data ────────────────────────────────────────────

    [Serializable]
    public struct RoadVertex
    {
        [Tooltip("Local-space position of this spline control point.")]
        public Vector3 position;

        [Tooltip("Bezier handle direction in absolute local space. " +
                 "Identity = handle points along local +Z. Both handles share this direction.")]
        public Quaternion handleRotation;

        [Tooltip("Length of both symmetric Bezier handles in local units.")]
        public float handleLength;

        [Tooltip("Local width multiplier relative to the road's base width. 1 = default.")]
        public float widthMultiplier;

        [Tooltip("Local height multiplier relative to the road's base height. 1 = default.")]
        public float heightMultiplier;

        [Tooltip("Banking (roll) of the road's cross-section at this vertex, in radians, " +
                 "around the spline tangent. 0 = horizontal (cross-section up matches world up).")]
        public float bankingAngle;
    }

    [SerializeField]
    [Tooltip("Ordered list of spline control points defining the road's path. Minimum 2.")]
    List<RoadVertex> _vertices = DefaultVertices();

    // ─── Road parameters ────────────────────────────────────────

    [SerializeField]
    [Tooltip("Base width of the road's box cross-section in local units.")]
    float _baseWidth = 10f;

    [SerializeField]
    [Tooltip("Base height (thickness) of the road's box cross-section in local units.")]
    float _baseHeight = 2f;

    [SerializeField]
    [Tooltip("Multiplier for ring density along the road's length. Higher = more rings.")]
    float _lengthSubdivMultiplier = 1f;

    [SerializeField]
    [Tooltip("Multiplier for subdivisions along the top/bottom edges of the road's cross-section. " +
             "Cut count is auto-derived from width and vertex density; this multiplier tweaks it.")]
    float _widthSubdivMultiplier = 1f;

    [SerializeField]
    [Tooltip("Multiplier for subdivisions along the left/right side edges of the cross-section. " +
             "At the default height of 2 this typically produces no cuts; raise the multiplier or " +
             "the road's height (or the manager's density) to add subdivisions on the sides.")]
    float _sideSubdivMultiplier = 1f;

    [SerializeField]
    [Range(0f, 1f)]
    [Tooltip("Smooths the road's path while passing through every vertex and keeping its width. " +
             "0 follows the authored Beziers; 1 uses a natural cubic through the vertices. " +
             "Intermediate values blend their tangents. Stored symmetric handles are unchanged. " +
             "Very tight vertex layouts can still make the inside edge overlap.")]
    float _splineSmoothing = 0.5f;

    [SerializeField]
    [Range(0f, 1f)]
    [Tooltip("Smooths banking and deliberately authored width/height changes between vertices, without overshoot. " +
             "Constant width values stay constant. Multiplies with the Grey Primitive Manager's Greyroad Edge Smoothing. " +
             "Use Spline Smoothing to smooth the road's path.")]
    float _edgeSmoothing = 1f;

    [SerializeField]
    [Range(0f, 1f)]
    [Tooltip("Relaxes (smooths) the banking/twist across the whole road. Unlike Edge Smoothing, " +
             "this lets the twist STRAY from the banking handles: 0 = banking passes exactly " +
             "through each handle's angle; higher values pull the in-between angles toward a smooth, " +
             "gentle twist so the outer rims flow instead of snapping to each handle; 1 = fully " +
             "relaxed (an even twist straight from the first handle to the last). Affects banking " +
             "only — width and height are untouched. The two end points keep their angle.")]
    float _bankingRelaxation = 0f;

    [SerializeField]
    [Tooltip("Tiling multiplier for the U texture coordinate. U spans 0-1 across the road's " +
             "full width (top-down projection), regardless of the actual width. 1 = one texture " +
             "repeat across the width.")]
    float _uvUMultiplier = 1f;

    [SerializeField]
    [Tooltip("Tiling multiplier for the V texture coordinate. V follows the spline and repeats " +
             "once per local unit of road length, so longer roads tile more. This multiplier " +
             "scales that repeat density.")]
    float _uvVMultiplier = 1f;

    [SerializeField]
    [Tooltip("6 flags controlling which faces are included in the mesh. " +
             "Toggled in the Grey Primitive panel; a ledge's by MMB on its face handles. " +
             "Indices: 0=Top, 1=Bottom, 2=LeftSide, 3=RightSide, 4=StartCap, 5=EndCap.")]
    bool[] _activeFaces = DefaultActiveFaces();

    // ─── Ledge link ─────────────────────────────────────────────

    [SerializeField]
    [HideInInspector]
    [Tooltip("Source road this road follows as a ledge. While set, the path is derived from one edge " +
             "of the source and the stored vertices are overwritten on every rebuild. Empty = a normal road.")]
    Greyroad _ledgeSource;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Edge of the source road the ledge follows: +1 = right edge, -1 = left edge.")]
    int _ledgeSide = 1;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Where the ledge starts along the source, as segment index + fraction of that segment's length.")]
    float _ledgeStart;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Where the ledge ends along the source, as segment index + fraction of that segment's length.")]
    float _ledgeEnd = 1f;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Ledge only: how far the ledge's inner side sits inside the source road's edge, in source " +
             "local units. 0 = flush with the edge; higher values overlap the road. Set by dragging the inner face.")]
    float _ledgeInset;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Ledge only: height of the ledge's top above the source road's edge, in source local " +
             "units. 0 = level with the road. Set by dragging the top face.")]
    float _ledgeRise;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Ledge only: lean of the ledge around its inner top edge, in degrees. 0 = vertical; " +
             "positive leans the top away from the road. Set by Ctrl-dragging the top face.")]
    float _ledgeTilt;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Reverse index: ledges that follow this road. They are rebuilt whenever this road is.")]
    List<Greyroad> _ledges = new List<Greyroad>();

    // ─── Branch anchor ──────────────────────────────────────────

    [SerializeField]
    [HideInInspector]
    [Tooltip("Road this road branches off. While set, the first vertex is kept on that road's spline " +
             "at the branch position. Empty = a free road.")]
    Greyroad _branchSource;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Where the first vertex sits along the branch source's spline, as segment index + " +
             "fraction of that segment's length.")]
    float _branchParam;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Sideways offset of the first vertex from the branch source's spline, along the source's " +
             "cross-section, in source local units. Positive = toward the source's right edge.")]
    float _branchOffset;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Reverse index: roads that branch off this road. Their first vertex follows it whenever " +
             "this road is rebuilt.")]
    List<Greyroad> _branches = new List<Greyroad>();

    // ─── Public accessors ───────────────────────────────────────

    public List<RoadVertex> Vertices => _vertices;

    public float BaseWidth
    {
        get => _baseWidth;
        set { _baseWidth = Mathf.Max(0.001f, value); }
    }

    public float BaseHeight
    {
        get => _baseHeight;
        set { _baseHeight = Mathf.Max(0.001f, value); }
    }

    public float LengthSubdivMultiplier
    {
        get => _lengthSubdivMultiplier;
        set { _lengthSubdivMultiplier = value; }
    }

    public float WidthSubdivMultiplier
    {
        get => _widthSubdivMultiplier;
        set { _widthSubdivMultiplier = value; }
    }

    public float SideSubdivMultiplier
    {
        get => _sideSubdivMultiplier;
        set { _sideSubdivMultiplier = value; }
    }

    public float SplineSmoothing
    {
        get => _splineSmoothing;
        set => _splineSmoothing = Mathf.Clamp01(value);
    }

    public float EdgeSmoothing
    {
        get => _edgeSmoothing;
        set { _edgeSmoothing = Mathf.Clamp01(value); }
    }

    public float BankingRelaxation
    {
        get => _bankingRelaxation;
        set { _bankingRelaxation = Mathf.Clamp01(value); }
    }

    public float UvUMultiplier
    {
        get => _uvUMultiplier;
        set { _uvUMultiplier = value; }
    }

    public float UvVMultiplier
    {
        get => _uvVMultiplier;
        set { _uvVMultiplier = value; }
    }

    public bool[] ActiveFaces => _activeFaces;

    public int SegmentCount => _vertices != null ? Mathf.Max(0, _vertices.Count - 1) : 0;

    // ─── Ledge link API ─────────────────────────────────────────

    /// <summary>Shortest ledge range, in path parameter units (segment index + length fraction).</summary>
    public const float MinLedgeSpan = 0.01f;

    /// <summary>True while this road follows an edge of a source road.</summary>
    public bool IsLedge => _ledgeSource != null && _ledgeSource != this;

    public Greyroad LedgeSource => _ledgeSource;

    /// <summary>+1 = the ledge follows the source's right edge, -1 = its left edge.</summary>
    public int LedgeSide => _ledgeSide;

    public float LedgeStart => _ledgeStart;
    public float LedgeEnd => _ledgeEnd;

    public float LedgeInset
    {
        get => _ledgeInset;
        set => _ledgeInset = value;
    }

    public float LedgeRise
    {
        get => _ledgeRise;
        set => _ledgeRise = value;
    }

    /// <summary>Largest ledge lean either way, in degrees.</summary>
    public const float MaxLedgeTilt = 85f;

    /// <summary>Lean around the inner top edge in degrees: 0 = vertical, positive = top away from the road.</summary>
    public float LedgeTilt
    {
        get => _ledgeTilt;
        set => _ledgeTilt = Mathf.Clamp(value, -MaxLedgeTilt, MaxLedgeTilt);
    }

    /// <summary>Makes this road a ledge of <paramref name="source"/>. Rebuild afterwards.</summary>
    public void LinkAsLedge(Greyroad source, int side, float start, float end)
    {
        _ledgeSource = source;
        _ledgeSide = side >= 0 ? 1 : -1;
        SetLedgeRange(start, end);
        source.AddLedge(this);
    }

    /// <summary>Sets the range of the source's length the ledge covers, clamped to the source's path.</summary>
    public void SetLedgeRange(float start, float end)
    {
        float max = Mathf.Max(MinLedgeSpan, _ledgeSource.SegmentCount);
        _ledgeStart = Mathf.Clamp(start, 0f, max - MinLedgeSpan);
        _ledgeEnd = Mathf.Clamp(end, _ledgeStart + MinLedgeSpan, max);
    }

    /// <summary>Severs the ledge link. The road keeps its last derived vertices and becomes a normal road.</summary>
    public void UnlinkLedge()
    {
        // Legitimate filter: the source may have been deleted already.
        if (_ledgeSource != null) _ledgeSource._ledges.Remove(this);
        _ledgeSource = null;
    }

    /// <summary>The live ledges that follow this road.</summary>
    public void GetLedges(List<Greyroad> ledges)
    {
        ledges.Clear();
        foreach (var ledge in _ledges)
            if (ledge != null && ledge._ledgeSource == this) ledges.Add(ledge);
    }

    void AddLedge(Greyroad ledge)
    {
        if (!_ledges.Contains(ledge)) _ledges.Add(ledge);
    }

    // ─── Branch anchor API ──────────────────────────────────────

    /// <summary>True while this road's first vertex is kept on another road's spline.</summary>
    public bool IsBranch => _branchSource != null && _branchSource != this;

    public Greyroad BranchSource => _branchSource;

    /// <summary>Moves the anchored first vertex along the source road. Rebuild afterwards.</summary>
    public void SetBranchParam(float param) => _branchParam = Mathf.Clamp(param, 0f, _branchSource.SegmentCount);

    /// <summary>Sideways offset of the anchored first vertex from the source spline, in source local units (+ = right). Rebuild afterwards.</summary>
    public float BranchOffset
    {
        get => _branchOffset;
        set => _branchOffset = value;
    }

    /// <summary>
    /// World position of the anchored first vertex and the source's sideways axis there, as the
    /// world vector of one source local unit. False when this is not a branch or the source has no path.
    /// </summary>
    public bool GetBranchAnchorWorld(out Vector3 point, out Vector3 sideways)
    {
        point = sideways = default;
        if (!IsBranch || !_branchSource.GetPathFrameLocal(_branchParam, out Vector3 position, out _, out _, out Vector3 right))
            return false;
        Transform from = _branchSource.transform;
        point = from.TransformPoint(position + right * _branchOffset);
        sideways = from.TransformVector(right);
        return true;
    }

    /// <summary>Frees the first vertex. The road keeps its current shape and becomes a normal road.</summary>
    public void UnlinkBranch()
    {
        // Legitimate filter: the source may have been deleted already.
        if (_branchSource != null) _branchSource._branches.Remove(this);
        _branchSource = null;
    }

    /// <summary>Anchors this road's first vertex to <paramref name="source"/>'s spline at a path parameter. Rebuild afterwards.</summary>
    public void LinkAsBranch(Greyroad source, float param)
    {
        _branchSource = source;
        _branchParam = Mathf.Clamp(param, 0f, source.SegmentCount);
        source.AddBranch(this);
    }

    /// <summary>The live roads that branch off this road.</summary>
    public void GetBranches(List<Greyroad> branches)
    {
        branches.Clear();
        foreach (var branch in _branches)
            if (branch != null && branch._branchSource == this) branches.Add(branch);
    }

    void AddBranch(Greyroad branch)
    {
        if (!_branches.Contains(branch)) _branches.Add(branch);
    }

    // ─── Path parameter queries ─────────────────────────────────

    const int k_PathLutSteps = 32;

    /// <summary>Path parameter (segment index + fraction of that segment's length) of a Bezier parameter on a segment.</summary>
    public float PathParamFromBezier(int segment, float t)
    {
        Span<float> lengths = stackalloc float[k_PathLutSteps + 1];
        float total = SegmentLengthTable(segment, lengths);
        if (total < 1e-6f) return segment;
        float k = Mathf.Clamp01(t) * k_PathLutSteps;
        int i = Mathf.Min(Mathf.FloorToInt(k), k_PathLutSteps - 1);
        return segment + Mathf.Lerp(lengths[i], lengths[i + 1], k - i) / total;
    }

    /// <summary>Local position on the spline at a path parameter (segment index + fraction of that segment's length).</summary>
    public Vector3 GetPathPointLocal(float param)
    {
        int segment = Mathf.Clamp(Mathf.FloorToInt(param), 0, SegmentCount - 1);
        Span<float> lengths = stackalloc float[k_PathLutSteps + 1];
        float target = Mathf.Clamp01(param - segment) * SegmentLengthTable(segment, lengths);
        float t = 1f;
        for (int i = 1; i <= k_PathLutSteps; i++)
        {
            if (lengths[i] < target) continue;
            float span = lengths[i] - lengths[i - 1];
            t = (i - 1 + (span > 1e-6f ? (target - lengths[i - 1]) / span : 0f)) / k_PathLutSteps;
            break;
        }
        GetSegmentControlPoints(segment, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
        return EvaluateBezier(p0, p1, p2, p3, t);
    }

    /// <summary>
    /// Local position, unit tangent, cross-section up and cross-section right of the mesh path at a
    /// path parameter, the same values the mesh ring there is built from. False when the road has
    /// no path to sample.
    /// </summary>
    public bool GetPathFrameLocal(float param, out Vector3 position, out Vector3 tangent, out Vector3 up, out Vector3 right)
    {
        var frames = BuildFrames(ComputeEffectiveDensity(), param, param);
        if (frames.Length == 0)
        {
            position = tangent = up = right = default;
            return false;
        }
        position = frames[0].position;
        tangent = frames[0].tangent;
        up = frames[0].up;
        right = frames[0].right;
        return true;
    }

    /// <summary>Cumulative local length at evenly spaced Bezier parameters of a segment; returns the segment length.</summary>
    float SegmentLengthTable(int segment, Span<float> lengths)
    {
        GetSegmentControlPoints(segment, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
        int last = lengths.Length - 1;
        Vector3 previous = p0;
        lengths[0] = 0f;
        for (int i = 1; i <= last; i++)
        {
            Vector3 point = EvaluateBezier(p0, p1, p2, p3, i / (float)last);
            lengths[i] = lengths[i - 1] + Vector3.Distance(previous, point);
            previous = point;
        }
        return lengths[last];
    }

    // ─── Main Axis & Frame ──────────────────────────────────────

    public Vector3 MainAxisLocal
    {
        get
        {
            if (_vertices == null || _vertices.Count < 2)
                return Vector3.forward;
            Vector3 dir = _vertices[_vertices.Count - 1].position - _vertices[0].position;
            return dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;
        }
    }

    public Vector3 MainAxisWorld => transform.TransformDirection(MainAxisLocal);

    public Vector3 GetVertexHandleDirLocal(int index)
        => _vertices[index].handleRotation * Vector3.forward;

    public Vector3 GetVertexHandleDirWorld(int index)
        => transform.TransformDirection(GetVertexHandleDirLocal(index));

    // ─── World-space accessors ──────────────────────────────────

    public Vector3 GetWorldVertexPosition(int index)
        => transform.TransformPoint(_vertices[index].position);

    public void GetWorldVertexPositions(Vector3[] dst)
    {
        for (int i = 0; i < _vertices.Count && i < dst.Length; i++)
            dst[i] = transform.TransformPoint(_vertices[i].position);
    }

    // ─── Handle helpers ─────────────────────────────────────────

    public void ResetVertexHandle(int index)
    {
        Vector3 point = _vertices[index].position;
        Vector3 before = index > 0 ? _vertices[index - 1].position : point;
        Vector3 after = index + 1 < _vertices.Count ? _vertices[index + 1].position : point;
        GreySplineMath.AdaptiveHandle(before, point, after, out Vector3 direction,
            out float length);
        SetVertexHandleDirLocal(index, direction);
        var v = _vertices[index];
        v.handleLength = length;
        _vertices[index] = v;
    }

    public void SetVertexHandleLength(int index, float length)
    {
        var v = _vertices[index];
        v.handleLength = length;
        _vertices[index] = v;
    }

    public void SetVertexHandleDirLocal(int index, Vector3 localDir)
    {
        if (localDir.sqrMagnitude < 0.0001f) return;
        var v = _vertices[index];
        v.handleRotation = Quaternion.LookRotation(localDir.normalized, Vector3.up);
        _vertices[index] = v;
    }

    // ─── Banking helpers ────────────────────────────────────────

    public float GetVertexBankingAngle(int index) => _vertices[index].bankingAngle;

    public void SetVertexBankingAngle(int index, float radians)
    {
        var v = _vertices[index];
        v.bankingAngle = radians;
        _vertices[index] = v;
    }

    public void ResetVertexBanking(int index) => SetVertexBankingAngle(index, 0f);

    /// <summary>
    /// World-space position of a Banking handle endpoint at a vertex. The two banking handles
    /// at each vertex sit perpendicular to the spline tangent, are tilted by the vertex's
    /// bankingAngle, and extend baseWidth/2 in local units to either side.
    /// </summary>
    public Vector3 GetBankingHandleWorld(int index, int side)
    {
        if (_vertices == null || index < 0 || index >= _vertices.Count) return Vector3.zero;
        Vector3 vertexWorld = GetWorldVertexPosition(index);
        ComputeBankingHandleAxisWorld(index, out _, out Vector3 rightWorld);
        float halfW = _baseWidth * Mathf.Max(0.001f, _vertices[index].widthMultiplier) * 0.5f;
        Vector3 localRight = transform.InverseTransformVector(rightWorld);
        float scale = localRight.magnitude > 1e-6f ? 1f / localRight.magnitude : 1f;
        return vertexWorld + rightWorld * (halfW * scale * (side >= 0 ? 1f : -1f));
    }

    /// <summary>
    /// Computes world-space banking up & right axes at a vertex — the cross-section's local
    /// frame after Banking is applied. up is along the box's "height" direction (normally
    /// world-up at banking 0), right is along the box's "width" direction.
    /// </summary>
    public void ComputeBankingHandleAxisWorld(int index, out Vector3 upWorld, out Vector3 rightWorld)
    {
        Vector3 tangentLocal;
        if (_vertices.Count < 2) tangentLocal = Vector3.forward;
        else if (index == 0)
        {
            GetSegmentControlPoints(0, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
            tangentLocal = EvaluateBezierTangent(p0, p1, p2, p3, 0f);
        }
        else if (index == _vertices.Count - 1)
        {
            GetSegmentControlPoints(index - 1, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
            tangentLocal = EvaluateBezierTangent(p0, p1, p2, p3, 1f);
        }
        else
        {
            GetSegmentControlPoints(index - 1, out Vector3 a0, out Vector3 a1, out Vector3 a2, out Vector3 a3);
            GetSegmentControlPoints(index,     out Vector3 b0, out Vector3 b1, out Vector3 b2, out Vector3 b3);
            tangentLocal = (EvaluateBezierTangent(a0, a1, a2, a3, 1f)
                          + EvaluateBezierTangent(b0, b1, b2, b3, 0f)).normalized;
        }

        if (tangentLocal.sqrMagnitude < 1e-8f)
            tangentLocal = _vertices[Mathf.Min(index + 1, _vertices.Count - 1)].position
                         - _vertices[Mathf.Max(0, index - 1)].position;
        Vector3 refUp = transform.InverseTransformDirection(Vector3.up).normalized;
        ReferenceFrame(tangentLocal, refUp, out Vector3 right, out Vector3 up);
        Vector3 forward = Vector3.Cross(right, up).normalized;
        Quaternion bank = Quaternion.AngleAxis(_vertices[index].bankingAngle * Mathf.Rad2Deg, forward);
        rightWorld = transform.TransformVector(bank * right).normalized;
        upWorld = transform.TransformVector(bank * up).normalized;
    }

    static void ReferenceFrame(Vector3 tangent, Vector3 refUp, out Vector3 right, out Vector3 up)
    {
        Vector3 forward = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : Vector3.forward;
        right = Vector3.Cross(refUp, forward);
        if (right.sqrMagnitude < 1e-8f)
            right = Vector3.Cross(Mathf.Abs(forward.x) < 0.9f ? Vector3.right : Vector3.forward, forward);
        right.Normalize();
        up = Vector3.Cross(forward, right).normalized;
    }

    // ─── Pivot recenter ─────────────────────────────────────────

    public void RecenterPivot()
    {
        if (_vertices == null || _vertices.Count < 2) return;

        var worldPositions  = new Vector3[_vertices.Count];
        var worldHandleDirs = new Vector3[_vertices.Count];
        for (int i = 0; i < _vertices.Count; i++)
        {
            worldPositions[i]  = transform.TransformPoint(_vertices[i].position);
            worldHandleDirs[i] = transform.TransformVector(GetVertexHandleDirLocal(i));
        }

        Vector3 newWorldPivot = Vector3.zero;
        for (int i = 0; i < _vertices.Count; i++)
            newWorldPivot += worldPositions[i];
        newWorldPivot /= _vertices.Count;

        Vector3 worldMainAxis = (worldPositions[worldPositions.Length - 1] - worldPositions[0]);
        if (worldMainAxis.sqrMagnitude < 0.0001f) return;
        worldMainAxis.Normalize();

        Vector3 worldUp = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(worldMainAxis, worldUp)) > 0.99f)
            worldUp = Vector3.right;
        Quaternion newWorldRot = Quaternion.LookRotation(worldMainAxis, worldUp);

        transform.position = newWorldPivot;
        transform.rotation = newWorldRot;

        for (int i = 0; i < _vertices.Count; i++)
        {
            var v = _vertices[i];
            v.position = transform.InverseTransformPoint(worldPositions[i]);
            _vertices[i] = v;
        }

        for (int i = 0; i < _vertices.Count; i++)
        {
            Vector3 localHandle = transform.InverseTransformVector(worldHandleDirs[i]);
            Vector3 localDir = localHandle.normalized;
            if (localDir.sqrMagnitude < 0.0001f) continue;
            var v = _vertices[i];
            v.handleLength *= localHandle.magnitude;
            v.handleRotation = Quaternion.LookRotation(localDir, Vector3.up);
            _vertices[i] = v;
        }
    }

    // ─── Vertex manipulation ────────────────────────────────────

    public void InsertVertex(int segmentIndex, float t)
    {
        if (segmentIndex < 0 || segmentIndex >= _vertices.Count - 1) return;
        if (t <= 0.0001f || t >= 0.9999f) return; // An endpoint already has a vertex.

        var a = _vertices[segmentIndex];
        var b = _vertices[segmentIndex + 1];
        GetSegmentControlPoints(segmentIndex, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
        GreySplineMath.FitInsertedHandle(p0, p1, p2, p3, t,
            out Vector3 position, out Vector3 direction, out float length);
        Vector3 localDirection = direction;

        _vertices.Insert(segmentIndex + 1, new RoadVertex
        {
            position = position,
            handleRotation = Quaternion.LookRotation(localDirection.normalized, Vector3.up),
            handleLength = length,
            widthMultiplier = Mathf.Lerp(a.widthMultiplier, b.widthMultiplier, t),
            heightMultiplier = Mathf.Lerp(a.heightMultiplier, b.heightMultiplier, t),
            bankingAngle = Mathf.LerpAngle(a.bankingAngle * Mathf.Rad2Deg,
                b.bankingAngle * Mathf.Rad2Deg, t) * Mathf.Deg2Rad,
        });
        ShiftDependentParams(p => ParamAfterInsert(p, segmentIndex, t));
    }

    public void ExtendFromEdge(int edgeIndex, Vector3 localPosition)
    {
        var edge = _vertices[edgeIndex];
        float dist = Vector3.Distance(localPosition, edge.position);
        float handleLen = Mathf.Max(0.01f, dist / 3f);

        Vector3 toNew = (localPosition - edge.position);
        if (toNew.sqrMagnitude < 0.0001f) toNew = MainAxisLocal * (edgeIndex == 0 ? -1f : 1f);
        toNew.Normalize();

        // Handle points in the spline's forward direction (toward higher indices).
        // Prepend (index 0): forward = toward old first vertex = -toNew.
        // Append  (last):    forward = continuation away from road = toNew.
        Vector3 handleDir = edgeIndex == 0 ? -toNew : toNew;
        Quaternion handleRot = handleDir.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(handleDir, Vector3.up)
            : Quaternion.identity;

        var newVert = new RoadVertex
        {
            position         = localPosition,
            handleRotation   = handleRot,
            handleLength     = handleLen,
            widthMultiplier  = edge.widthMultiplier,
            heightMultiplier = edge.heightMultiplier,
            bankingAngle     = edge.bankingAngle,
        };

        if (edgeIndex == 0)
        {
            _vertices.Insert(0, newVert);
            ShiftDependentParams(p => p + 1f);
        }
        else
            _vertices.Add(newVert);
    }

    public bool RemoveVertex(int index)
    {
        if (_vertices.Count <= 2) return false;
        _vertices.RemoveAt(index);
        ShiftDependentParams(p => ParamAfterRemove(p, index));
        return true;
    }

    /// <summary>Keeps every ledge and branch on the same stretch of road after a vertex was added or removed.</summary>
    void ShiftDependentParams(Func<float, float> shift)
    {
        foreach (var ledge in _ledges)
            if (ledge != null && ledge._ledgeSource == this)
                ledge.SetLedgeRange(shift(ledge._ledgeStart), shift(ledge._ledgeEnd));
        foreach (var branch in _branches)
            if (branch != null && branch._branchSource == this)
                branch._branchParam = Mathf.Clamp(shift(branch._branchParam), 0f, SegmentCount);
    }

    static float ParamAfterInsert(float param, int segment, float t)
    {
        if (param >= segment + 1) return param + 1f;
        if (param <= segment) return param;
        float local = param - segment;
        return local <= t ? segment + local / t : segment + 1f + (local - t) / (1f - t);
    }

    // The two segments around the removed vertex merge into one.
    static float ParamAfterRemove(float param, int index)
    {
        if (param >= index + 1) return param - 1f;
        if (param <= index - 1) return param;
        return index - 1 + (param - (index - 1)) * 0.5f;
    }

    // ─── GreyPrimitive overrides ─────────────────────────────────

    protected override void OnBeforeRebuild()
    {
        if (_vertices == null || _vertices.Count < 2)
            _vertices = DefaultVertices();
        if (_activeFaces == null || _activeFaces.Length != 6)
            _activeFaces = DefaultActiveFaces();
        // A duplicated ledge or branch is not in its source's reverse index yet.
        if (IsLedge) _ledgeSource.AddLedge(this);
        if (IsBranch)
        {
            _branchSource.AddBranch(this);
            AnchorBranchStart();
        }
    }

    /// <summary>
    /// Puts the first vertex on the source road's path and matches the source's surface there, so
    /// the two meshes meet flush: the cross-section up follows the source's up (banking), and the
    /// handle keeps only its direction within the source's surface plane.
    /// </summary>
    void AnchorBranchStart()
    {
        var source = _branchSource;
        Transform from = source.transform, to = transform;
        var first = _vertices[0];
        if (!source.GetPathFrameLocal(_branchParam, out Vector3 position, out _, out Vector3 sourceUp, out Vector3 sourceRight))
        {
            // A degenerate source path has no surface to match; keep the start on its spline.
            first.position = to.InverseTransformPoint(from.TransformPoint(source.GetPathPointLocal(_branchParam)));
            _vertices[0] = first;
            return;
        }
        first.position = to.InverseTransformPoint(from.TransformPoint(position + sourceRight * _branchOffset));
        Vector3 up =to.InverseTransformVector(from.TransformVector(sourceUp)).normalized;

        Vector3 handle = Vector3.ProjectOnPlane(first.handleRotation * Vector3.forward, up);
        if (handle.sqrMagnitude < 1e-8f && _vertices.Count > 1)
            handle = Vector3.ProjectOnPlane(_vertices[1].position - first.position, up);
        if (handle.sqrMagnitude > 1e-8f)
        {
            handle.Normalize();
            first.handleRotation = Quaternion.LookRotation(handle, Vector3.up);
            Vector3 refUp = to.InverseTransformDirection(Vector3.up).normalized;
            ReferenceFrame(handle, refUp, out _, out Vector3 flatUp);
            first.bankingAngle = Vector3.SignedAngle(flatUp, up, handle) * Mathf.Deg2Rad;
        }
        _vertices[0] = first;
    }

    // Set while this road rebuilds its ledges and branches; ends reference cycles.
    [NonSerialized] bool _rebuildingDependents;

    protected override void OnAfterRebuild()
    {
        if (_rebuildingDependents) return;
        _rebuildingDependents = true;
        try
        {
            RebuildDependents(_ledges, ledges: true);
            RebuildDependents(_branches, ledges: false);
        }
        finally
        {
            _rebuildingDependents = false;
        }
    }

    void RebuildDependents(List<Greyroad> dependents, bool ledges)
    {
        for (int i = dependents.Count - 1; i >= 0; i--)
        {
            var road = dependents[i];
            // Deleted roads, roads linked elsewhere and the entries a duplicated source copied are dropped.
            if (road == null || road == this || (ledges ? road._ledgeSource : road._branchSource) != this)
                dependents.RemoveAt(i);
            else road.RebuildMesh();
        }
    }

    protected override void ResetToDefaults()
    {
        _vertices    = DefaultVertices();
        _baseWidth   = 10f;
        _baseHeight  = 2f;
        _splineSmoothing = 0.5f;
        _activeFaces = DefaultActiveFaces();
    }

    // ─── Mesh generation ────────────────────────────────────────

    struct SplineSample
    {
        public Vector3 position;
        public Vector3 tangent;
        public float widthMul;
        public float heightMul;
        public float bankingAngle;
        public float arcLen; // cumulative distance along the spline, in local units
        public float param;  // segment index + fraction of that segment's length
    }

    /// <summary>
    /// Compact per-ring frame: position, tangent + rotated (right, up) basis already
    /// adjusted for the vertex's Banking angle. Cross-section is built in this basis.
    /// </summary>
    struct RingFrame
    {
        public Vector3 position;
        public Vector3 tangent;
        public Vector3 right; // along +width
        public Vector3 up;    // along +height
        public float halfWidth;
        public float halfHeight;
        public float arcLen;  // cumulative distance along the spline, in local units
        public float param;   // segment index + fraction of that segment's length
    }

    protected override void GenerateMesh(Mesh mesh)
    {
        int vertCount = _vertices.Count;
        if (vertCount < 2) { mesh.Clear(); return; }

        float density = ComputeEffectiveDensity();

        RingFrame[] frames = IsLedge ? BuildLedgeFrames() : BuildFrames(density, 0f, SegmentCount);
        int ringCount = frames.Length;
        if (ringCount < 2) { mesh.Clear(); return; }
        if (IsLedge) SyncLedgeVertices(frames);

        int widthSubs = ComputeWidthSubdivisions(density);
        int sideSubs  = ComputeSideSubdivisions(density);

        // Per-edge vert counts (cross-section samples along each face edge).
        // Top/Bottom edges → widthSubs + 1 verts; Left/Right side edges → sideSubs + 1 verts.
        int wN = widthSubs + 1;
        int sN = sideSubs  + 1;

        // Accumulators
        var verts = new List<Vector3>(ringCount * 8);
        var norms = new List<Vector3>(ringCount * 8);
        var uvs   = new List<Vector2>(ringCount * 8);
        var tris  = new List<int>(ringCount * 24);

        // Helper: corner positions of one ring's cross-section (CCW looking down the tangent).
        // TR = +w +h, TL = -w +h, BL = -w -h, BR = +w -h
        // Local-2D coords (w, h) of the four corners:
        // For each side strip we emit a quad strip between ring i and ring i+1, with
        // wN or sN slices across the cross-section edge and (ringCount-1) slices along length.

        if (_activeFaces[FaceTop])      EmitFaceStrip(frames, ringCount, wN, FaceTop,      verts, norms, uvs, tris);
        if (_activeFaces[FaceBottom])   EmitFaceStrip(frames, ringCount, wN, FaceBottom,   verts, norms, uvs, tris);
        if (_activeFaces[FaceLeftSide]) EmitFaceStrip(frames, ringCount, sN, FaceLeftSide, verts, norms, uvs, tris);
        if (_activeFaces[FaceRightSide])EmitFaceStrip(frames, ringCount, sN, FaceRightSide,verts, norms, uvs, tris);
        if (_activeFaces[FaceStartCap]) EmitCapQuad(frames[0],              wN, sN, isStart: true,  verts, norms, uvs, tris);
        if (_activeFaces[FaceEndCap])   EmitCapQuad(frames[ringCount - 1], wN, sN, isStart: false, verts, norms, uvs, tris);

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
    }

    /// <summary>Ring frames of this road's own spline between two path parameters, in local space.</summary>
    RingFrame[] BuildFrames(float density, float from, float to)
    {
        var samples = SampleSpline(density, from, to);
        var frames = new RingFrame[samples.Count];
        Vector3 refUp = transform.InverseTransformDirection(Vector3.up).normalized;
        ComputeRingFrames(samples, frames, refUp);
        return frames;
    }

    /// <summary>
    /// Ring frames of a ledge in its own local space: the source's rings over the ledge range,
    /// moved onto the followed edge. Using the source's rings keeps the two meshes aligned. The
    /// cross-section stands vertical whatever the source's banking, leaned by the tilt around
    /// the ledge's inner top edge, which sits on the source edge (moved by inset and rise).
    /// </summary>
    RingFrame[] BuildLedgeFrames()
    {
        var source = _ledgeSource;
        var frames = source.BuildFrames(source.ComputeEffectiveDensity(), _ledgeStart, _ledgeEnd);
        Transform from = source.transform, to = transform;
        Vector3 refUp = from.InverseTransformDirection(Vector3.up).normalized;
        for (int i = 0; i < frames.Length; i++)
        {
            RingFrame f = frames[i];
            Vector3 edge = f.position + f.right * (_ledgeSide * source._baseWidth * f.halfWidth);
            ReferenceFrame(f.tangent, refUp, out Vector3 flatRight, out Vector3 flatUp);
            Vector3 innerTop = edge - flatRight * (_ledgeSide * _ledgeInset) + flatUp * _ledgeRise;
            // A positive tilt turns the up axis toward the outward side.
            Quaternion tilt = Quaternion.AngleAxis(-_ledgeSide * _ledgeTilt, f.tangent);
            Vector3 right = tilt * flatRight;
            Vector3 up = tilt * flatUp;
            Vector3 position = innerTop + right * (_ledgeSide * _baseWidth * 0.5f);
            f.position = to.InverseTransformPoint(from.TransformPoint(position));
            f.tangent = to.InverseTransformDirection(from.TransformDirection(f.tangent));
            f.right = to.InverseTransformDirection(from.TransformDirection(right));
            f.up = to.InverseTransformDirection(from.TransformDirection(up));
            f.halfWidth = 0.5f;
            f.halfHeight = 0.5f;
            frames[i] = f;
        }
        return frames;
    }

    /// <summary>
    /// Overwrites a ledge's stored vertices with points on its derived path (both ends plus one per
    /// source vertex crossed), so code that reads the vertices and a later unlink see the real path.
    /// </summary>
    void SyncLedgeVertices(RingFrame[] frames)
    {
        _vertices.Clear();
        Vector3 refUp = transform.InverseTransformDirection(Vector3.up).normalized;
        int last = frames.Length - 1;
        for (int i = 0; i <= last; i++)
        {
            RingFrame f = frames[i];
            bool end = i == 0 || i == last;
            bool onSourceVertex = Mathf.Abs(f.param - Mathf.Round(f.param)) < 1e-4f;
            if (!end && !onSourceVertex) continue;
            if (!end && (f.position - _vertices[_vertices.Count - 1].position).sqrMagnitude < 1e-4f) continue;

            ReferenceFrame(f.tangent, refUp, out _, out Vector3 unbankedUp);
            _vertices.Add(new RoadVertex
            {
                position         = f.position,
                handleRotation   = Quaternion.LookRotation(f.tangent, Vector3.up),
                widthMultiplier  = 1f,
                heightMultiplier = 1f,
                bankingAngle     = Vector3.SignedAngle(unbankedUp, f.up, f.tangent) * Mathf.Deg2Rad,
            });
        }

        for (int i = 0; i < _vertices.Count; i++)
        {
            var v = _vertices[i];
            float span = float.MaxValue;
            if (i > 0) span = Mathf.Min(span, Vector3.Distance(v.position, _vertices[i - 1].position));
            if (i < _vertices.Count - 1) span = Mathf.Min(span, Vector3.Distance(v.position, _vertices[i + 1].position));
            v.handleLength = Mathf.Max(0.01f, span / 3f);
            _vertices[i] = v;
        }
    }

    /// <summary>One cross-section of the path the mesh is built from, in world space.</summary>
    public struct PathFrame
    {
        public Vector3 position;  // top centre
        public Vector3 tangent;   // unit, toward higher path parameters
        public Vector3 halfRight; // top centre to the right top edge
        public Vector3 drop;      // top to bottom
        public float param;       // segment index + length fraction; a ledge reports its source's parameter
    }

    /// <summary>
    /// World-space cross-sections along the path the mesh is built from, in path order. For a
    /// ledge this is its derived path, with <see cref="PathFrame.param"/> on the source road.
    /// </summary>
    public void GetPathFramesWorld(List<PathFrame> result)
    {
        result.Clear();
        RingFrame[] frames = IsLedge ? BuildLedgeFrames() : BuildFrames(ComputeEffectiveDensity(), 0f, SegmentCount);
        foreach (var f in frames)
            result.Add(new PathFrame
            {
                position  = transform.TransformPoint(f.position),
                tangent   = transform.TransformDirection(f.tangent),
                halfRight = transform.TransformVector(f.right * (_baseWidth * f.halfWidth)),
                drop      = transform.TransformVector(f.up * -(_baseHeight * f.halfHeight * 2f)),
                param     = f.param,
            });
    }

    static void ComputeRingFrames(List<SplineSample> samples, RingFrame[] frames, Vector3 refUp)
    {
        for (int ring = 0; ring < samples.Count; ring++)
        {
            var s = samples[ring];
            Vector3 forward = s.tangent.normalized;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;

            // Cross-sections stay perpendicular to the path; orientation cannot narrow the road.
            ReferenceFrame(forward, refUp, out Vector3 right, out Vector3 up);

            float bank = s.bankingAngle;
            if (Mathf.Abs(bank) > 0.00001f)
            {
                Quaternion q = Quaternion.AngleAxis(bank * Mathf.Rad2Deg, forward);
                right = q * right;
                up    = q * up;
            }

            frames[ring].position   = s.position;
            frames[ring].tangent    = forward;
            frames[ring].right      = right;
            frames[ring].up         = up;
            frames[ring].halfWidth  = 0.5f * Mathf.Max(0f, s.widthMul);
            frames[ring].halfHeight = 0.5f * Mathf.Max(0f, s.heightMul);
            frames[ring].arcLen     = s.arcLen;
            frames[ring].param      = s.param;
        }
    }

    // Inset from the 0/1 ends of the U span. Faces sitting at exactly U=0 or U=1 (the side
    // walls) would otherwise sample a repeating texture right on its wrap seam — at exactly
    // 1.0 the sampler wraps to the opposite edge of the texture.
    const float k_UvEdgeInset = 0.01f;

    /// <summary>
    /// Position of a point on the cross-section edge of one face, parameterized by u in [0,1].
    /// u directions are chosen so that — with rings progressing along +tangent — the cross product
    /// (tangent × u_dir) equals the face's outward normal, allowing a unified winding for all four
    /// side faces in EmitFaceStrip.
    /// uvU is the texture U coordinate as a top-down projection: 0 at the left edge (-width),
    /// 1 at the right edge (+width), regardless of the actual width. Side faces sit at a constant
    /// width position, so they get a constant uvU (the texture's edge column stretches down them).
    /// </summary>
    void SampleFaceEdge(in RingFrame f, int face, float u, float baseWidth, float baseHeight,
        out Vector3 pos, out Vector3 normal, out float uvU)
    {
        // Spline runs along the TOP of the road. Top h = 0, bottom h = -fullH.
        // Increasing baseHeight extends the body DOWNWARD; the top stays at the spline.
        float halfW = baseWidth * f.halfWidth;
        float fullH = baseHeight * (f.halfHeight * 2f);
        float w, h;
        switch (face)
        {
            case FaceTop:    // u: -w → +w at h=0 → u_dir = +right, tangent×right = up ✓
                w = Mathf.Lerp(-halfW, +halfW, u); h = 0f;
                normal = f.up;
                break;
            case FaceBottom: // u: +w → -w at h=-fullH → u_dir = -right, tangent×(-right) = -up ✓
                w = Mathf.Lerp(+halfW, -halfW, u); h = -fullH;
                normal = -f.up;
                break;
            case FaceLeftSide:  // u: -fullH → 0 at -w → u_dir = +up, tangent×up = -right ✓
                w = -halfW; h = Mathf.Lerp(-fullH, 0f, u);
                normal = -f.right;
                break;
            case FaceRightSide: // u: 0 → -fullH at +w → u_dir = -up, tangent×(-up) = +right ✓
                w = +halfW; h = Mathf.Lerp(0f, -fullH, u);
                normal = f.right;
                break;
            default:
                w = 0f; h = 0f; normal = f.tangent;
                break;
        }
        pos = f.position + f.right * w + f.up * h;
        uvU = halfW > 1e-5f ? (w + halfW) / (2f * halfW) : 0.5f;
        uvU = Mathf.Clamp(uvU, k_UvEdgeInset, 1f - k_UvEdgeInset);
    }

    void EmitFaceStrip(RingFrame[] frames, int ringCount, int edgeVerts, int face,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris)
    {
        int baseIndex = verts.Count;
        int slices = Mathf.Max(2, edgeVerts);

        for (int ring = 0; ring < ringCount; ring++)
        {
            for (int i = 0; i < slices; i++)
            {
                float u = i / (float)(slices - 1);
                SampleFaceEdge(frames[ring], face, u, _baseWidth, _baseHeight, out Vector3 p, out Vector3 n, out float uvU);
                verts.Add(p);
                norms.Add(n);
                // Top-down projected UV: U spans 0-1 across the width, V repeats by arc length.
                uvs.Add(new Vector2(uvU * _uvUMultiplier, frames[ring].arcLen * _uvVMultiplier));
            }
        }

        for (int ring = 0; ring < ringCount - 1; ring++)
        {
            for (int i = 0; i < slices - 1; i++)
            {
                int v00 = baseIndex + ring       * slices + i;
                int v01 = baseIndex + ring       * slices + i + 1;
                int v10 = baseIndex + (ring + 1) * slices + i;
                int v11 = baseIndex + (ring + 1) * slices + i + 1;

                // Unified winding: (ring_dir, u_dir) chosen per face so this CCW order
                // produces the desired outward normal. See SampleFaceEdge for u-direction logic.
                tris.Add(v00); tris.Add(v10); tris.Add(v11);
                tris.Add(v00); tris.Add(v11); tris.Add(v01);
            }
        }
    }

    void EmitCapQuad(in RingFrame f, int wN, int sN, bool isStart,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris)
    {
        float halfW = _baseWidth  * f.halfWidth;
        float fullH = _baseHeight * (f.halfHeight * 2f);
        // Cap normal points away from the spline interior.
        Vector3 normal = isStart ? -f.tangent : f.tangent;

        int baseIndex = verts.Count;
        int rows = Mathf.Max(2, sN);
        int cols = Mathf.Max(2, wN);

        for (int j = 0; j < rows; j++)
        {
            float sv = j / (float)(rows - 1);            // 0 → top, 1 → bottom (along height)
            float h  = Mathf.Lerp(0f, -fullH, sv);       // top at spline, bottom hangs below
            for (int i = 0; i < cols; i++)
            {
                float su = i / (float)(cols - 1);        // 0 → left, 1 → right (along width)
                float w  = Mathf.Lerp(-halfW, +halfW, su);
                verts.Add(f.position + f.right * w + f.up * h);
                norms.Add(normal);
                // Caps are vertical, so the top-down projection collapses their height axis:
                // U spans the width, V is the cap's constant arc-length position.
                float capU = Mathf.Clamp(su, k_UvEdgeInset, 1f - k_UvEdgeInset);
                uvs.Add(new Vector2(capU * _uvUMultiplier, f.arcLen * _uvVMultiplier));
            }
        }

        for (int j = 0; j < rows - 1; j++)
        {
            for (int i = 0; i < cols - 1; i++)
            {
                int v00 = baseIndex + j       * cols + i;
                int v01 = baseIndex + j       * cols + i + 1;
                int v10 = baseIndex + (j + 1) * cols + i;
                int v11 = baseIndex + (j + 1) * cols + i + 1;

                // Winding chosen so the visible face matches `normal`. With our cap loop
                // (j down → -height, i right → +width) the natural (v00,v10,v11)+(v00,v11,v01)
                // winding produces +tangent (end-cap outward). Start cap reverses for -tangent.
                if (isStart)
                {
                    tris.Add(v00); tris.Add(v11); tris.Add(v10);
                    tris.Add(v00); tris.Add(v01); tris.Add(v11);
                }
                else
                {
                    tris.Add(v00); tris.Add(v10); tris.Add(v11);
                    tris.Add(v00); tris.Add(v11); tris.Add(v01);
                }
            }
        }
    }

    // ─── Spline sampling ────────────────────────────────────────

    /// <summary>
    /// Samples the spline between two path parameters (segment index + fraction of that segment's
    /// length). The interior rings are the ones the whole road gets, so a partial range lines up
    /// with the full road; exact samples are added at <paramref name="from"/> and <paramref name="to"/>.
    /// </summary>
    List<SplineSample> SampleSpline(float density, float from, float to)
    {
        var samples = new List<SplineSample>(64);
        int vertCount = _vertices.Count;
        int segCount = vertCount - 1;
        if (segCount < 1) return samples;

        // Per-vertex property values. Banking is unwrapped into a continuous degree sequence so
        // the smoothing math sees no ±180° jumps.
        var widthVals  = new float[vertCount];
        var heightVals = new float[vertCount];
        var bankVals   = new float[vertCount];
        for (int i = 0; i < vertCount; i++)
        {
            widthVals[i]  = _vertices[i].widthMultiplier;
            heightVals[i] = _vertices[i].heightMultiplier;
            float deg = _vertices[i].bankingAngle * Mathf.Rad2Deg;
            bankVals[i] = i == 0 ? deg : bankVals[i - 1] + Mathf.DeltaAngle(bankVals[i - 1], deg);
        }

        // Cache each segment's bezier control points (reused for the length table and emission).
        var ctrl0 = new Vector3[segCount];
        var ctrl1 = new Vector3[segCount];
        var ctrl2 = new Vector3[segCount];
        var ctrl3 = new Vector3[segCount];
        for (int seg = 0; seg < segCount; seg++)
            GetSegmentControlPoints(seg, out ctrl0[seg], out ctrl1[seg], out ctrl2[seg], out ctrl3[seg]);

        // ── Arc-length table over the whole spline ──
        // Rings are placed by even DISTANCE, not by even parameter t. Uniform t bunches rings up
        // in curves and stretches them on straights (uneven, clumped tessellation); even
        // arc-length keeps the loft uniform. A global parameter g = segment + t maps a distance
        // back to a (segment, t) pair; vertexLen[i] is the cumulative distance to each control
        // point, used to ease banking / width / height by distance too (not by parameter).
        const int lutPerSeg = 64;
        var lutLen = new float[segCount * lutPerSeg + 1];
        var lutG   = new float[segCount * lutPerSeg + 1];
        var vertexLen = new float[vertCount];

        int w = 0;
        float accum = 0f;
        Vector3 prev = ctrl0[0];
        for (int seg = 0; seg < segCount; seg++)
        {
            int kStart = seg == 0 ? 0 : 1; // shared endpoint already recorded by the previous segment
            for (int k = kStart; k <= lutPerSeg; k++)
            {
                float t = k / (float)lutPerSeg;
                Vector3 pos = EvaluateBezier(ctrl0[seg], ctrl1[seg], ctrl2[seg], ctrl3[seg], t);
                if (w > 0) accum += Vector3.Distance(prev, pos);
                lutLen[w] = accum;
                lutG[w]   = seg + t;
                prev = pos;
                w++;
            }
            vertexLen[seg + 1] = accum;
        }
        int lutN = w;
        float totalLen = lutLen[lutN - 1];
        if (totalLen < 1e-5f) return samples;

        // Banking relaxation (banking only): pull interior banking angles toward an even twist
        // running straight from the first handle to the last (by distance). This lets the twist
        // stray from the authored handle angles so the outer rims flow smoothly; the two end
        // points keep their angle. 0 = exact (no straying), 1 = fully relaxed even twist.
        float bankRelax = Mathf.Clamp01(_bankingRelaxation);
        if (bankRelax > 0f && vertCount >= 3)
        {
            float startBank = bankVals[0];
            float endBank   = bankVals[vertCount - 1];
            for (int i = 1; i < vertCount - 1; i++)
            {
                float evenTwist = Mathf.Lerp(startBank, endBank, vertexLen[i] / totalLen);
                bankVals[i] = Mathf.Lerp(bankVals[i], evenTwist, bankRelax);
            }
        }

        // Monotone (overshoot-free) tangents measured over arc length, scaled by Edge Smoothing,
        // so the twist eases evenly by distance regardless of bezier handle lengths.
        float smoothing = GetEffectiveEdgeSmoothing();
        var widthTan  = new float[vertCount];
        var heightTan = new float[vertCount];
        var bankTan   = new float[vertCount];
        ComputeMonotoneTangents(widthVals,  vertexLen, widthTan,  smoothing);
        ComputeMonotoneTangents(heightVals, vertexLen, heightTan, smoothing);
        ComputeMonotoneTangents(bankVals,   vertexLen, bankTan,   smoothing);

        float lengthMultiplier = Mathf.Max(0.1f, _lengthSubdivMultiplier) * GetManagerLengthMultiplier();
        float densityFactor = density > 0f ? density : 1f;
        int cursor = 0;
        int firstSeg = Mathf.Clamp(Mathf.FloorToInt(from), 0, segCount - 1);
        int lastSeg = Mathf.Clamp(Mathf.CeilToInt(to) - 1, firstSeg, segCount - 1);
        for (int seg = firstSeg; seg <= lastSeg; seg++)
        {
            float segLen = vertexLen[seg + 1] - vertexLen[seg];
            if (segLen < 1e-6f) continue;
            Vector3 a = ctrl1[seg] - ctrl0[seg], b = ctrl2[seg] - ctrl1[seg], c = ctrl3[seg] - ctrl2[seg];
            float turning = Vector3.Angle(a, b) + Vector3.Angle(b, c);
            turning += Mathf.Abs(bankVals[seg + 1] - bankVals[seg]);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(segLen * densityFactor * lengthMultiplier,
                turning / 5f)), 2, 4096);
            float uFrom = seg == firstSeg ? Mathf.Clamp01(from - seg) : 0f;
            float uTo = seg == lastSeg ? Mathf.Clamp01(to - seg) : 1f;
            // Range ends first and last; between them only the whole road's own ring positions.
            for (int r = samples.Count == 0 ? 0 : 1; r <= steps; r++)
            {
                float u = r == 0 ? uFrom : r == steps ? uTo : r / (float)steps;
                if (r > 0 && r < steps && (u <= uFrom + 1e-5f || u >= uTo - 1e-5f)) continue;
                float target = Mathf.Lerp(vertexLen[seg], vertexLen[seg + 1], u);
                while (cursor < lutN - 2 && lutLen[cursor + 1] < target) cursor++;
                float spanLen = lutLen[cursor + 1] - lutLen[cursor];
                float frac = spanLen > 1e-6f ? Mathf.Clamp01((target - lutLen[cursor]) / spanLen) : 0f;
                float t = Mathf.Clamp01(Mathf.Lerp(lutG[cursor], lutG[cursor + 1], frac) - seg);
                Vector3 tangent = EvaluateBezierTangent(ctrl0[seg], ctrl1[seg], ctrl2[seg], ctrl3[seg], t);
                if (tangent.sqrMagnitude < 1e-8f) tangent = ctrl3[seg] - ctrl0[seg];
                samples.Add(new SplineSample
                {
                    position = EvaluateBezier(ctrl0[seg], ctrl1[seg], ctrl2[seg], ctrl3[seg], t),
                    tangent = tangent,
                    widthMul = EvaluateHermite(widthVals[seg], widthTan[seg], widthVals[seg + 1], widthTan[seg + 1], u, segLen),
                    heightMul = EvaluateHermite(heightVals[seg], heightTan[seg], heightVals[seg + 1], heightTan[seg + 1], u, segLen),
                    bankingAngle = EvaluateHermite(bankVals[seg], bankTan[seg], bankVals[seg + 1], bankTan[seg + 1], u, segLen) * Mathf.Deg2Rad,
                    arcLen = target,
                    param = seg + u,
                });
            }
        }

        return samples;
    }

    /// <summary>Cross-section cut count along the top/bottom edges. ~20% more sensitive than
    /// raw width × density so slightly narrower roads still pick up a subdivision or two.</summary>
    int ComputeWidthSubdivisions(float density)
    {
        float densityFactor = density > 0f ? density : 0f;  // 0 manager density → no cuts
        float mul = Mathf.Max(0f, _widthSubdivMultiplier) * GetManagerWidthMultiplier();
        return Mathf.Max(0, Mathf.RoundToInt(_baseWidth * densityFactor * mul * 1.2f) - 1);
    }

    /// <summary>Cross-section cut count along the left/right side edges.</summary>
    int ComputeSideSubdivisions(float density)
    {
        float densityFactor = density > 0f ? density : 0f;
        float mul = Mathf.Max(0f, _sideSubdivMultiplier) * GetManagerSideMultiplier();
        // Side cuts derive from height — at default height=2 and density=1 with mul=1 this
        // produces round(2)-1 = 1 cut, then we clamp at 0 to honor the spec "usually no subdivision".
        // Subtracting another 1 puts the default at zero cuts; raising height or multipliers re-enables.
        int cuts = Mathf.RoundToInt(_baseHeight * densityFactor * mul) - 2;
        return Mathf.Max(0, cuts);
    }

    float GetManagerLengthMultiplier()
    {
        var manager = GetComponentInParent<GreyPrimitiveManager>();
        return manager != null ? manager.GreyroadLengthSubdivMultiplier : 1f;
    }

    float GetManagerWidthMultiplier()
    {
        var manager = GetComponentInParent<GreyPrimitiveManager>();
        return manager != null ? manager.GreyroadWidthSubdivMultiplier : 1f;
    }

    float GetManagerSideMultiplier()
    {
        var manager = GetComponentInParent<GreyPrimitiveManager>();
        return manager != null ? manager.GreyroadSideSubdivMultiplier : 1f;
    }

    float GetEffectiveEdgeSmoothing()
    {
        return Mathf.Clamp01(_edgeSmoothing) * GetManagerEdgeSmoothing();
    }

    float GetManagerEdgeSmoothing()
    {
        var manager = GetComponentInParent<GreyPrimitiveManager>();
        return manager != null ? manager.GreyroadEdgeSmoothing : 1f;
    }

    // ─── Bezier math ────────────────────────────────────────────

    static Vector3 EvaluateBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
    }

    static Vector3 EvaluateBezierTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
    }

    // ─── Scalar property smoothing ──────────────────────────────

    /// <summary>
    /// Cubic Hermite interpolation between two scalar values across an interval of length
    /// <paramref name="dx"/>, with endpoint tangents given as slopes per unit distance.
    /// <paramref name="u"/> is the fraction (0..1) along the interval. Passes exactly through v0
    /// at u=0 and v1 at u=1, so control-point values are always preserved.
    /// </summary>
    static float EvaluateHermite(float v0, float m0, float v1, float m1, float u, float dx)
    {
        float u2 = u * u;
        float u3 = u2 * u;
        float h00 =  2f * u3 - 3f * u2 + 1f;
        float h10 =       u3 - 2f * u2 + u;
        float h01 = -2f * u3 + 3f * u2;
        float h11 =       u3 -      u2;
        // m0/m1 are slopes per unit distance, so the tangent terms scale by the interval length.
        return h00 * v0 + h10 * dx * m0 + h01 * v1 + h11 * dx * m1;
    }

    /// <summary>
    /// Monotone (overshoot-free) cubic tangents for a value track sampled at positions
    /// <paramref name="x"/> (cumulative arc length of each control point), scaled by
    /// <paramref name="smoothing"/>. Tangents are slopes per unit distance, so transitions ease
    /// evenly by distance. At smoothing 0 every tangent is zero and Hermite interpolation collapses
    /// to smoothstep (the stiff look that eases to a flat stop at each handle). At 1 the
    /// Fritsch–Carlson tangents give the most relaxed flow through the handles that still never
    /// overshoots between them. Scaling toward 0 stays inside the monotone region, so every value
    /// in between is also overshoot-free.
    /// </summary>
    static void ComputeMonotoneTangents(float[] v, float[] x, float[] m, float smoothing)
    {
        int n = v.Length;
        if (n == 0) return;
        if (n == 1) { m[0] = 0f; return; }

        // Secant slopes per unit distance between consecutive control points.
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            float dx = x[i + 1] - x[i];
            d[i] = dx > 1e-6f ? (v[i + 1] - v[i]) / dx : 0f;
        }

        m[0]     = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            float left = x[i] - x[i - 1], right = x[i + 1] - x[i];
            if (d[i - 1] * d[i] <= 0f || left <= 1e-6f || right <= 1e-6f)
                m[i] = 0f;
            else
            {
                float w1 = 2f * right + left, w2 = right + 2f * left;
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
        }

        // Fritsch–Carlson clamp: keep each interval monotone so the value never bulges past a handle.
        for (int i = 0; i < n - 1; i++)
        {
            if (Mathf.Abs(d[i]) < 1e-6f) { m[i] = 0f; m[i + 1] = 0f; continue; }
            float alpha = m[i]     / d[i];
            float beta  = m[i + 1] / d[i];
            float s = alpha * alpha + beta * beta;
            if (s > 9f)
            {
                float tau = 3f / Mathf.Sqrt(s);
                m[i]     = tau * alpha * d[i];
                m[i + 1] = tau * beta  * d[i];
            }
        }

        float k = Mathf.Clamp01(smoothing);
        for (int i = 0; i < n; i++) m[i] *= k;
    }

    // ─── Spline queries ─────────────────────────────────────────

    public Vector3 EvaluateSplineWorld(int segmentIndex, float t)
    {
        GetSegmentControlPoints(segmentIndex, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
        return transform.TransformPoint(EvaluateBezier(p0, p1, p2, p3, t));
    }

    public Vector3 EvaluateSplineTangentWorld(int segmentIndex, float t)
    {
        GetSegmentControlPoints(segmentIndex, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
        return transform.TransformVector(EvaluateBezierTangent(p0, p1, p2, p3, t));
    }

    public void GetSegmentControlPoints(int segmentIndex, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3)
    {
        var a = _vertices[segmentIndex];
        var b = _vertices[segmentIndex + 1];
        Vector3 aDir = a.handleRotation * Vector3.forward;
        Vector3 bDir = b.handleRotation * Vector3.forward;
        p0 = a.position;
        p1 = a.position + aDir * a.handleLength;
        p2 = b.position - bDir * b.handleLength;
        p3 = b.position;
        if (_splineSmoothing <= 0f) return;
        EnsureSmoothedSpline();
        float length = _smoothingLengths[segmentIndex];
        p1 = p0 + SmoothedHandleOffset(segmentIndex, length);
        p2 = p3 - SmoothedHandleOffset(segmentIndex + 1, length);
    }

    [NonSerialized] Vector3[] _smoothingPositions;
    [NonSerialized] Vector3[] _smoothingDerivatives;
    [NonSerialized] float[] _smoothingLengths;

    Vector3 SmoothedHandleOffset(int index, float segmentLength)
    {
        var vertex = _vertices[index];
        Vector3 authored = transform.TransformVector(GetVertexHandleDirLocal(index) * vertex.handleLength);
        Vector3 smooth = _smoothingDerivatives[index];
        // A branch start keeps its locked handle so the path leaves the source road flush.
        float amount = index == 0 && IsBranch ? 0f : Mathf.Clamp01(_splineSmoothing);
        Vector3 direction = authored.sqrMagnitude < 1e-12f ? smooth.normalized
            : smooth.sqrMagnitude < 1e-12f ? authored.normalized
            : Vector3.Slerp(authored.normalized, smooth.normalized, amount).normalized;
        float length = Mathf.Lerp(authored.magnitude, smooth.magnitude * segmentLength / 3f, amount);
        return transform.InverseTransformVector(direction * length);
    }

    void EnsureSmoothedSpline()
    {
        int count = _vertices.Count;
        bool changed = _smoothingPositions == null || _smoothingPositions.Length != count;
        for (int i = 0; !changed && i < count; i++)
            changed = !_smoothingPositions[i].Equals(transform.TransformPoint(_vertices[i].position));
        if (!changed) return;

        _smoothingPositions = new Vector3[count];
        _smoothingDerivatives = new Vector3[count];
        _smoothingLengths = new float[count - 1];
        var points = new Vector3[count];
        var indices = new int[count];
        int unique = 0;
        for (int i = 0; i < count; i++)
        {
            Vector3 point = transform.TransformPoint(_vertices[i].position);
            _smoothingPositions[i] = point;
            if (unique == 0 || !point.Equals(points[unique - 1])) points[unique++] = point;
            indices[i] = unique - 1;
            if (i > 0) _smoothingLengths[i - 1] = Vector3.Distance(_smoothingPositions[i - 1], point);
        }
        if (unique < 2) return; // Coincident knots describe no path to smooth.

        var spans = new float[unique - 1];
        for (int i = 0; i < spans.Length; i++) spans[i] = Mathf.Max(1e-6f, Vector3.Distance(points[i], points[i + 1]));
        var second = new Vector3[unique];
        var upper = new float[unique];
        // Natural cubic interpolation: shared second derivatives, with zero curvature at the ends.
        for (int i = 1; i < unique - 1; i++)
        {
            float left = spans[i - 1], right = spans[i];
            float diagonal = 2f * (left + right) - left * upper[i - 1];
            upper[i] = right / diagonal;
            Vector3 slopes = (points[i + 1] - points[i]) / right - (points[i] - points[i - 1]) / left;
            second[i] = (6f * slopes - left * second[i - 1]) / diagonal;
        }
        for (int i = unique - 2; i > 0; i--) second[i] -= upper[i] * second[i + 1];
        for (int i = 0; i < count; i++)
        {
            int knot = indices[i];
            if (knot < unique - 1)
            {
                float span = spans[knot];
                _smoothingDerivatives[i] = (points[knot + 1] - points[knot]) / span
                    - span * (2f * second[knot] + second[knot + 1]) / 6f;
            }
            else
            {
                float span = spans[knot - 1];
                _smoothingDerivatives[i] = (points[knot] - points[knot - 1]) / span
                    + span * (second[knot - 1] + 2f * second[knot]) / 6f;
            }
        }
    }

    // ─── Editor warning gizmo ───────────────────────────────────

    // Flags corners too tight for the road's width. A constant-width ribbon physically can't turn
    // sharper than its own half-width without the inner wall folding through itself — even spacing
    // can't fix that, it's geometry. We mark those spots in red so the turn can be widened or the
    // road narrowed. Only drawn while selected, so there's no cost otherwise.
    void OnDrawGizmosSelected()
    {
        if (_vertices == null || _vertices.Count < 2) return;
        int segCount = _vertices.Count - 1;
        Gizmos.color = new Color(1f, 0.18f, 0.1f, 1f);
        float worldScale = Mathf.Abs(transform.lossyScale.x);
        float markerSize = Mathf.Max(0.25f, _baseWidth * 0.12f) * (worldScale > 0.0001f ? worldScale : 1f);

        for (int seg = 0; seg < segCount; seg++)
        {
            GetSegmentControlPoints(seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
            const int N = 32;
            for (int k = 0; k <= N; k++)
            {
                float t = k / (float)N;
                float u = 1f - t;
                Vector3 d1 = 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
                Vector3 d2 = 6f * u * (p2 - 2f * p1 + p0) + 6f * t * (p3 - 2f * p2 + p1);
                float speed = d1.magnitude;
                if (speed < 1e-4f) continue;
                float curvature = Vector3.Cross(d1, d2).magnitude / (speed * speed * speed);
                if (curvature < 1e-6f) continue;

                float radius = 1f / curvature;
                float wMul = Mathf.Lerp(_vertices[seg].widthMultiplier, _vertices[seg + 1].widthMultiplier, t);
                float halfW = _baseWidth * 0.5f * Mathf.Max(0.001f, wMul);
                if (radius < halfW)
                {
                    Vector3 world = transform.TransformPoint(EvaluateBezier(p0, p1, p2, p3, t));
                    Gizmos.DrawSphere(world, markerSize);
                }
            }
        }
    }

    // ─── Defaults ────────────────────────────────────────────────

    public static List<RoadVertex> CreateDefaultVertices(float length)
    {
        float half = Mathf.Max(0.001f, length) * 0.5f;
        float hLen = half * (2f / 3f);
        return new List<RoadVertex>
        {
            new RoadVertex
            {
                position         = new Vector3(0f, 0f, -half),
                handleRotation   = Quaternion.identity,
                handleLength     = hLen,
                widthMultiplier  = 1f,
                heightMultiplier = 1f,
                bankingAngle     = 0f,
            },
            new RoadVertex
            {
                position         = new Vector3(0f, 0f,  half),
                handleRotation   = Quaternion.identity,
                handleLength     = hLen,
                widthMultiplier  = 1f,
                heightMultiplier = 1f,
                bankingAngle     = 0f,
            },
        };
    }

    const float k_DefaultLength = 5f;
    static List<RoadVertex> DefaultVertices() => CreateDefaultVertices(k_DefaultLength);

    /// <summary>
    /// Default visible faces at spawn: Top + the two lateral sides (perpendicular to the
    /// spline's main axis). Bottom and end caps are hidden.
    /// </summary>
    public static bool[] DefaultActiveFaces() => new bool[]
    {
        true,   // 0 Top
        false,  // 1 Bottom
        true,   // 2 LeftSide
        true,   // 3 RightSide
        false,  // 4 StartCap
        false,  // 5 EndCap
    };
}
