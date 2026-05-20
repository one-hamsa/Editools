using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural box-lofted road primitive. Mirrors Greypipe for spline math, frame transport,
/// and pivot/handle behavior — but lofts a box cross-section (width × height) rather than a
/// circle, adds per-vertex Banking (twist around the tangent), and exposes 6 face-visibility
/// flags so the user can hide individual sides (top / bottom / left / right / start cap / end cap)
/// in QuickTransform Special mode.
///
/// Default visible faces at spawn: Top + the two lateral sides. Bottom and end caps are hidden.
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
                 "Identity = handle points along local +Z. Symmetric on both sides.")]
        public Quaternion handleRotation;

        [Tooltip("Length of each bezier handle (symmetric on both sides), in local units.")]
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
    [Tooltip("6 flags controlling which faces are included in the mesh. " +
             "Toggle via Q + LMB on a face in the Scene View. " +
             "Indices: 0=Top, 1=Bottom, 2=LeftSide, 3=RightSide, 4=StartCap, 5=EndCap.")]
    bool[] _activeFaces = DefaultActiveFaces();

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

    public bool[] ActiveFaces => _activeFaces;

    public int SegmentCount => _vertices != null ? Mathf.Max(0, _vertices.Count - 1) : 0;

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
        var v = _vertices[index];
        v.handleRotation = Quaternion.identity;
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
        float lossy = Mathf.Abs(transform.lossyScale.x);
        if (lossy < 0.0001f) lossy = 1f;
        return vertexWorld + rightWorld * (halfW * lossy * (side >= 0 ? 1f : -1f));
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

        Vector3 tangentWorld = transform.TransformDirection(tangentLocal);
        if (tangentWorld.sqrMagnitude < 0.0001f) tangentWorld = MainAxisWorld;
        tangentWorld.Normalize();

        Vector3 worldUp = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(tangentWorld, worldUp)) > 0.99f)
            worldUp = Vector3.right;

        Vector3 right0 = Vector3.Cross(worldUp, tangentWorld).normalized;
        Vector3 up0    = Vector3.Cross(tangentWorld, right0).normalized;

        // Apply banking — rotate (right, up) by bankingAngle around the tangent.
        float angle = _vertices[index].bankingAngle;
        Quaternion bank = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, tangentWorld);
        rightWorld = bank * right0;
        upWorld    = bank * up0;
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
            worldHandleDirs[i] = GetVertexHandleDirWorld(i);
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
            Vector3 localDir = transform.InverseTransformDirection(worldHandleDirs[i]).normalized;
            if (localDir.sqrMagnitude < 0.0001f) continue;
            var v = _vertices[i];
            v.handleRotation = Quaternion.LookRotation(localDir, Vector3.up);
            _vertices[i] = v;
        }
    }

    // ─── Vertex manipulation ────────────────────────────────────

    public void InsertVertex(int segmentIndex, float t)
    {
        if (segmentIndex < 0 || segmentIndex >= _vertices.Count - 1) return;

        var a = _vertices[segmentIndex];
        var b = _vertices[segmentIndex + 1];

        GetSegmentControlPoints(segmentIndex, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);

        Vector3 pos = EvaluateBezier(p0, p1, p2, p3, t);
        Vector3 tan = EvaluateBezierTangent(p0, p1, p2, p3, t).normalized;

        float st = t * t * (3f - 2f * t);
        float widthMul  = Mathf.Lerp(a.widthMultiplier,  b.widthMultiplier,  st);
        float heightMul = Mathf.Lerp(a.heightMultiplier, b.heightMultiplier, st);
        float banking   = Mathf.LerpAngle(a.bankingAngle * Mathf.Rad2Deg,
                                          b.bankingAngle * Mathf.Rad2Deg, st) * Mathf.Deg2Rad;

        float distA = Vector3.Distance(pos, a.position);
        float distB = Vector3.Distance(pos, b.position);
        float hLen  = (distA + distB) * 0.25f;

        Quaternion handleRot = tan.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(tan, Vector3.up)
            : Quaternion.identity;

        var newVert = new RoadVertex
        {
            position         = pos,
            handleRotation   = handleRot,
            handleLength     = hLen,
            widthMultiplier  = widthMul,
            heightMultiplier = heightMul,
            bankingAngle     = banking,
        };

        _vertices.Insert(segmentIndex + 1, newVert);
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
            _vertices.Insert(0, newVert);
        else
            _vertices.Add(newVert);
    }

    public bool RemoveVertex(int index)
    {
        if (_vertices.Count <= 2) return false;
        _vertices.RemoveAt(index);
        return true;
    }

    // ─── GreyPrimitive overrides ─────────────────────────────────

    protected override void OnBeforeRebuild()
    {
        if (_vertices == null || _vertices.Count < 2)
            _vertices = DefaultVertices();
        if (_activeFaces == null || _activeFaces.Length != 6)
            _activeFaces = DefaultActiveFaces();
    }

    protected override int GetSubclassRebuildSignature()
    {
        unchecked
        {
            int sig = 17;
            sig = sig * 31 + _baseWidth.GetHashCode();
            sig = sig * 31 + _baseHeight.GetHashCode();
            sig = sig * 31 + _lengthSubdivMultiplier.GetHashCode();
            sig = sig * 31 + _widthSubdivMultiplier.GetHashCode();
            sig = sig * 31 + _sideSubdivMultiplier.GetHashCode();
            sig = sig * 31 + (_vertices != null ? _vertices.Count : 0);
            if (_vertices != null)
            {
                for (int i = 0; i < _vertices.Count; i++)
                {
                    var v = _vertices[i];
                    sig = sig * 31 + v.position.GetHashCode();
                    sig = sig * 31 + v.handleLength.GetHashCode();
                    sig = sig * 31 + v.widthMultiplier.GetHashCode();
                    sig = sig * 31 + v.heightMultiplier.GetHashCode();
                    sig = sig * 31 + v.bankingAngle.GetHashCode();
                    // handleRotation: quaternion hash is stable enough for dirty detection
                    sig = sig * 31 + v.handleRotation.GetHashCode();
                }
            }
            if (_activeFaces != null)
            {
                for (int i = 0; i < _activeFaces.Length; i++)
                    sig = sig * 31 + (_activeFaces[i] ? 1 : 0);
            }
            return sig;
        }
    }

    protected override void ResetToDefaults()
    {
        _vertices    = DefaultVertices();
        _baseWidth   = 10f;
        _baseHeight  = 2f;
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
    }

    protected override void GenerateMesh(Mesh mesh)
    {
        int vertCount = _vertices.Count;
        if (vertCount < 2) { mesh.Clear(); return; }

        float density = ComputeEffectiveDensity();

        var samples = SampleSpline(density);
        int ringCount = samples.Count;
        if (ringCount < 2) { mesh.Clear(); return; }

        int widthSubs = ComputeWidthSubdivisions(density);
        int sideSubs  = ComputeSideSubdivisions(density);

        // Per-edge vert counts (cross-section samples along each face edge).
        // Top/Bottom edges → widthSubs + 1 verts; Left/Right side edges → sideSubs + 1 verts.
        int wN = widthSubs + 1;
        int sN = sideSubs  + 1;

        var frames = new RingFrame[ringCount];
        Vector3 refUp = transform.InverseTransformDirection(Vector3.up).normalized;
        ComputeRingFrames(samples, frames, refUp);

        // Accumulators
        var verts = new List<Vector3>(ringCount * 8);
        var norms = new List<Vector3>(ringCount * 8);
        var tris  = new List<int>(ringCount * 24);

        // Helper: corner positions of one ring's cross-section (CCW looking down the tangent).
        // TR = +w +h, TL = -w +h, BL = -w -h, BR = +w -h
        // Local-2D coords (w, h) of the four corners:
        // For each side strip we emit a quad strip between ring i and ring i+1, with
        // wN or sN slices across the cross-section edge and (ringCount-1) slices along length.

        if (_activeFaces[FaceTop])      EmitFaceStrip(frames, ringCount, wN, FaceTop,      verts, norms, tris);
        if (_activeFaces[FaceBottom])   EmitFaceStrip(frames, ringCount, wN, FaceBottom,   verts, norms, tris);
        if (_activeFaces[FaceLeftSide]) EmitFaceStrip(frames, ringCount, sN, FaceLeftSide, verts, norms, tris);
        if (_activeFaces[FaceRightSide])EmitFaceStrip(frames, ringCount, sN, FaceRightSide,verts, norms, tris);
        if (_activeFaces[FaceStartCap]) EmitCapQuad(frames[0],              wN, sN, isStart: true,  verts, norms, tris);
        if (_activeFaces[FaceEndCap])   EmitCapQuad(frames[ringCount - 1], wN, sN, isStart: false, verts, norms, tris);

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetTriangles(tris, 0);
    }

    static void ComputeRingFrames(List<SplineSample> samples, RingFrame[] frames, Vector3 refUp)
    {
        for (int ring = 0; ring < samples.Count; ring++)
        {
            var s = samples[ring];
            Vector3 forward = s.tangent.normalized;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;

            // Derive frame from tangent + world up (in local space) at every ring
            // independently. No transport — banking handles are the sole twist authority.
            Vector3 upRef = refUp;
            if (Mathf.Abs(Vector3.Dot(upRef, forward)) > 0.99f)
                upRef = Vector3.Cross(forward, Vector3.one).normalized;

            Vector3 right = Vector3.Cross(upRef, forward).normalized;
            Vector3 up    = Vector3.Cross(forward, right).normalized;

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
        }
    }

    /// <summary>
    /// Position of a point on the cross-section edge of one face, parameterized by u in [0,1].
    /// u directions are chosen so that — with rings progressing along +tangent — the cross product
    /// (tangent × u_dir) equals the face's outward normal, allowing a unified winding for all four
    /// side faces in EmitFaceStrip.
    /// </summary>
    void SampleFaceEdge(in RingFrame f, int face, float u, float baseWidth, float baseHeight,
        out Vector3 pos, out Vector3 normal)
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
    }

    void EmitFaceStrip(RingFrame[] frames, int ringCount, int edgeVerts, int face,
        List<Vector3> verts, List<Vector3> norms, List<int> tris)
    {
        int baseIndex = verts.Count;
        int slices = Mathf.Max(2, edgeVerts);

        for (int ring = 0; ring < ringCount; ring++)
        {
            for (int i = 0; i < slices; i++)
            {
                float u = i / (float)(slices - 1);
                SampleFaceEdge(frames[ring], face, u, _baseWidth, _baseHeight, out Vector3 p, out Vector3 n);
                verts.Add(p);
                norms.Add(n);
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
        List<Vector3> verts, List<Vector3> norms, List<int> tris)
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

    List<SplineSample> SampleSpline(float density)
    {
        var samples = new List<SplineSample>();
        int segCount = _vertices.Count - 1;

        for (int seg = 0; seg < segCount; seg++)
        {
            var a = _vertices[seg];
            var b = _vertices[seg + 1];

            GetSegmentControlPoints(seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);

            int subdivs = ComputeSegmentSubdivisions(p0, p1, p2, p3, density);

            int startT = (seg == 0) ? 0 : 1;
            for (int i = startT; i <= subdivs; i++)
            {
                float t = i / (float)subdivs;
                float st = t * t * (3f - 2f * t);
                samples.Add(new SplineSample
                {
                    position    = EvaluateBezier(p0, p1, p2, p3, t),
                    tangent     = EvaluateBezierTangent(p0, p1, p2, p3, t),
                    widthMul    = Mathf.Lerp(a.widthMultiplier,  b.widthMultiplier,  st),
                    heightMul   = Mathf.Lerp(a.heightMultiplier, b.heightMultiplier, st),
                    bankingAngle = Mathf.LerpAngle(a.bankingAngle * Mathf.Rad2Deg,
                                                   b.bankingAngle * Mathf.Rad2Deg, st) * Mathf.Deg2Rad,
                });
            }
        }

        return samples;
    }

    int ComputeSegmentSubdivisions(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float density)
    {
        float chordLen = Vector3.Distance(p0, p3);
        float densityFactor = density > 0f ? density : 1f;
        float lengthMultiplier = Mathf.Max(0.1f, _lengthSubdivMultiplier) * GetManagerLengthMultiplier();

        int baseSubdivs = Mathf.Max(2, Mathf.CeilToInt(chordLen * densityFactor * lengthMultiplier));

        float polyLen   = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
        float curveRatio = chordLen > 0.001f ? (polyLen - chordLen) / chordLen : 0f;
        int   curveBonus = Mathf.CeilToInt(baseSubdivs * Mathf.Clamp(curveRatio, 0f, 3f));

        return baseSubdivs + curveBonus;
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
        var manager = GetComponentInParent<GreyboxManager>();
        return manager != null ? manager.GreyroadLengthSubdivMultiplier : 1f;
    }

    float GetManagerWidthMultiplier()
    {
        var manager = GetComponentInParent<GreyboxManager>();
        return manager != null ? manager.GreyroadWidthSubdivMultiplier : 1f;
    }

    float GetManagerSideMultiplier()
    {
        var manager = GetComponentInParent<GreyboxManager>();
        return manager != null ? manager.GreyroadSideSubdivMultiplier : 1f;
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

    // ─── Spline queries ─────────────────────────────────────────

    public Vector3 EvaluateSplineWorld(int segmentIndex, float t)
    {
        GetSegmentControlPoints(segmentIndex, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
        return transform.TransformPoint(EvaluateBezier(p0, p1, p2, p3, t));
    }

    public Vector3 EvaluateSplineTangentWorld(int segmentIndex, float t)
    {
        GetSegmentControlPoints(segmentIndex, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
        return transform.TransformDirection(EvaluateBezierTangent(p0, p1, p2, p3, t));
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
    }

    // ─── Defaults ────────────────────────────────────────────────

    public static List<RoadVertex> CreateDefaultVertices(float length)
    {
        float half = Mathf.Max(0.001f, length) * 0.5f;
        float hLen = half;
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
