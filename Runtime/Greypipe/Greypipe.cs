using System;
using System.Collections.Generic;
using UnityEngine;

public class Greypipe : GreyPrimitive
{
    // ─── Vertex data ────────────────────────────────────────────

    [Serializable]
    public struct SplineVertex
    {
        [Tooltip("Local-space position of this spline control point.")]
        public Vector3 position;

        [Tooltip("Bezier handle rotation expressed relative to the spline's Main Axis frame. " +
                 "Identity = handle aligned with the Main Axis. Symmetric on both sides.")]
        public Quaternion handleRotation;

        [Tooltip("Length of each bezier handle (symmetric on both sides), in local units.")]
        public float handleLength;

        [Tooltip("Local girth multiplier relative to the pipe's base girth. 1 = default.")]
        public float girthMultiplier;
    }

    [SerializeField]
    [Tooltip("Ordered list of spline control points defining the pipe's path. Minimum 2.")]
    List<SplineVertex> _vertices = DefaultVertices();

    // ─── Pipe parameters ────────────────────────────────────────

    [SerializeField]
    [Tooltip("Base radius of the pipe's circular cross-section in local units.")]
    float _baseGirth = 0.5f;

    [SerializeField]
    [Tooltip("Number of polygon sides for the circular cross-section. Higher = smoother.")]
    int _sides = 8;

    [SerializeField]
    [Tooltip("Multiplier for ring density along the pipe's length. Higher = more rings at curves.")]
    float _lengthSubdivMultiplier = 1f;

    [SerializeField]
    [Tooltip("Multiplier for circumference subdivision density. Higher = more segments around the tube.")]
    float _girthSubdivMultiplier = 1f;

    // ─── Public accessors ───────────────────────────────────────

    public List<SplineVertex> Vertices => _vertices;

    public float BaseGirth
    {
        get => _baseGirth;
        set { _baseGirth = Mathf.Max(0.001f, value); }
    }

    public int Sides
    {
        get => _sides;
        set { _sides = Mathf.Max(3, value); }
    }

    public float LengthSubdivMultiplier
    {
        get => _lengthSubdivMultiplier;
        set { _lengthSubdivMultiplier = value; }
    }

    public float GirthSubdivMultiplier
    {
        get => _girthSubdivMultiplier;
        set { _girthSubdivMultiplier = value; }
    }

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

    /// <summary>
    /// Local-space rotation that maps Vector3.forward to the Main Axis,
    /// with up matched to world up. Handle rotations are stored relative
    /// to this frame, so the spline shape follows the Main Axis when vertices move.
    /// </summary>
    public Quaternion MainAxisFrameLocal
    {
        get
        {
            Vector3 axis = MainAxisLocal;
            Vector3 upRef = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(axis, upRef)) > 0.99f)
                upRef = Vector3.right;
            return Quaternion.LookRotation(axis, upRef);
        }
    }

    /// <summary>Local-space handle forward direction for a vertex (Main Axis frame applied).</summary>
    public Vector3 GetVertexHandleDirLocal(int index)
        => MainAxisFrameLocal * _vertices[index].handleRotation * Vector3.forward;

    /// <summary>World-space handle forward direction for a vertex.</summary>
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

    /// <summary>Resets a vertex's handle to align with the Main Axis (identity relative rotation).</summary>
    public void ResetVertexHandle(int index)
    {
        var v = _vertices[index];
        v.handleRotation = Quaternion.identity;
        _vertices[index] = v;
    }

    /// <summary>Sets a vertex's handle rotation from a local-space forward direction.</summary>
    public void SetVertexHandleDirLocal(int index, Vector3 localDir)
    {
        if (localDir.sqrMagnitude < 0.0001f) return;
        Vector3 relDir = Quaternion.Inverse(MainAxisFrameLocal) * localDir.normalized;
        if (relDir.sqrMagnitude < 0.0001f) return;
        var v = _vertices[index];
        v.handleRotation = Quaternion.LookRotation(relDir, Vector3.up);
        _vertices[index] = v;
    }

    // ─── Edge-driven reshape ────────────────────────────────────

    /// <summary>
    /// Captures each vertex's position relative to the edge-axis frame at the moment of capture.
    /// Used in tandem with ApplyEdgeAxisFrame to keep interior vertices following the edges
    /// when an edge vertex is moved. Returns a flat array: for vertex i, snapshot[i] = (perpX, perpY, fractionAlongAxis).
    /// </summary>
    public Vector3[] CaptureEdgeAxisRelativePositions()
    {
        var result = new Vector3[_vertices.Count];
        Vector3 origin   = _vertices[0].position;
        Vector3 axis     = _vertices[_vertices.Count - 1].position - origin;
        float   axisLen  = axis.magnitude;
        if (axisLen < 0.0001f) return result;
        Vector3 forward = axis / axisLen;
        Quaternion frameInv = Quaternion.Inverse(BuildEdgeFrame(forward));

        for (int i = 0; i < _vertices.Count; i++)
        {
            Vector3 delta = _vertices[i].position - origin;
            Vector3 framed = frameInv * delta;  // x,y = perpendicular; z = along axis
            float fraction = framed.z / axisLen;
            result[i] = new Vector3(framed.x, framed.y, fraction);
        }
        return result;
    }

    /// <summary>
    /// Re-applies a previously captured edge-axis-relative snapshot to interior vertices,
    /// using the current edge positions as the new frame. Skips edges (i=0 and i=Count-1).
    /// </summary>
    public void ApplyEdgeAxisRelativePositions(Vector3[] snapshot)
    {
        if (snapshot == null || snapshot.Length != _vertices.Count) return;

        Vector3 origin   = _vertices[0].position;
        Vector3 axis     = _vertices[_vertices.Count - 1].position - origin;
        float   axisLen  = axis.magnitude;
        if (axisLen < 0.0001f) return;
        Vector3 forward = axis / axisLen;
        Quaternion frame = BuildEdgeFrame(forward);

        for (int i = 1; i < _vertices.Count - 1; i++)
        {
            Vector3 rel = snapshot[i];
            Vector3 framed = new Vector3(rel.x, rel.y, rel.z * axisLen);
            Vector3 worldDelta = frame * framed;
            var v = _vertices[i];
            v.position = origin + worldDelta;
            _vertices[i] = v;
        }
    }

    static Quaternion BuildEdgeFrame(Vector3 forward)
    {
        Vector3 up = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.99f) up = Vector3.right;
        return Quaternion.LookRotation(forward, up);
    }

    // ─── Pivot recenter ─────────────────────────────────────────

    /// <summary>
    /// Recenters the object's transform pivot to the centroid of the vertices,
    /// orients the local Z axis to the Main Axis, and remaps all vertex positions
    /// so the world-space geometry stays put. Should be called after edits that
    /// change the spline's extent (move/extend/insert/delete).
    /// </summary>
    public void RecenterPivot()
    {
        if (_vertices == null || _vertices.Count < 2) return;

        var worldPositions = new Vector3[_vertices.Count];
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

        // Remap vertex local positions; handle rotations are stored relative
        // to MainAxisFrameLocal which is derived from positions, so we need to
        // re-express each handle so its world direction is preserved.
        for (int i = 0; i < _vertices.Count; i++)
        {
            var v = _vertices[i];
            v.position = transform.InverseTransformPoint(worldPositions[i]);
            _vertices[i] = v;
        }

        // Now positions are set; MainAxisFrameLocal is updated. Restore handle directions.
        Quaternion frameInv = Quaternion.Inverse(MainAxisFrameLocal);
        for (int i = 0; i < _vertices.Count; i++)
        {
            Vector3 localDir = transform.InverseTransformDirection(worldHandleDirs[i]).normalized;
            if (localDir.sqrMagnitude < 0.0001f) continue;
            Vector3 relDir = frameInv * localDir;
            if (relDir.sqrMagnitude < 0.0001f) continue;
            var v = _vertices[i];
            v.handleRotation = Quaternion.LookRotation(relDir, Vector3.up);
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

        float girth = Mathf.Lerp(a.girthMultiplier, b.girthMultiplier, t);

        // Handle length: half of the average distance to the two neighbors
        float distA = Vector3.Distance(pos, a.position);
        float distB = Vector3.Distance(pos, b.position);
        float hLen  = (distA + distB) * 0.25f;

        // Handle rotation: tangent direction, expressed relative to Main Axis frame
        Quaternion frameInv = Quaternion.Inverse(MainAxisFrameLocal);
        Vector3 relTan = frameInv * tan;
        Quaternion handleRot = relTan.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(relTan, Vector3.up)
            : Quaternion.identity;

        var newVert = new SplineVertex
        {
            position        = pos,
            handleRotation  = handleRot,
            handleLength    = hLen,
            girthMultiplier = girth,
        };

        _vertices.Insert(segmentIndex + 1, newVert);
    }

    public void ExtendFromEdge(int edgeIndex, Vector3 localPosition)
    {
        var edge = _vertices[edgeIndex];
        float dist = Vector3.Distance(localPosition, edge.position);

        Vector3 dir = (localPosition - edge.position);
        if (dir.sqrMagnitude < 0.0001f) dir = MainAxisLocal * (edgeIndex == 0 ? -1f : 1f);
        dir.Normalize();

        var newVert = new SplineVertex
        {
            position        = localPosition,
            handleRotation  = Quaternion.identity,  // will be set after insertion (depends on new main axis)
            handleLength    = Mathf.Max(0.01f, dist * 0.5f),
            girthMultiplier = edge.girthMultiplier,
        };

        int insertedIdx;
        if (edgeIndex == 0)
        {
            _vertices.Insert(0, newVert);
            insertedIdx = 0;
        }
        else
        {
            _vertices.Add(newVert);
            insertedIdx = _vertices.Count - 1;
        }

        // Now set the new vertex's handle to point along the extension direction (main-axis-relative)
        SetVertexHandleDirLocal(insertedIdx, dir);
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
    }

    protected override void ResetToDefaults()
    {
        _vertices = DefaultVertices();
        _baseGirth = 0.5f;
        _sides = 8;
    }

    protected override void GenerateMesh(Mesh mesh)
    {
        int vertCount = _vertices.Count;
        if (vertCount < 2) return;

        float density = ComputeEffectiveDensity();

        var samples = SampleSpline(density);
        int ringCount = samples.Count;
        int circleSegs = ComputeCircleSegments(density);

        int vertsPerRing = circleSegs + 1;
        int totalVerts   = ringCount * vertsPerRing;
        int totalTris    = (ringCount - 1) * circleSegs * 2;

        var verts   = new Vector3[totalVerts];
        var normals = new Vector3[totalVerts];
        var tris    = new int[totalTris * 3];

        Vector3 mainAxis = MainAxisLocal;
        Vector3 refUp = DeriveUp(mainAxis);

        for (int ring = 0; ring < ringCount; ring++)
        {
            var s = samples[ring];
            Vector3 forward = s.tangent.normalized;
            if (forward.sqrMagnitude < 0.0001f) forward = mainAxis;

            Vector3 right = Vector3.Cross(refUp, forward).normalized;
            Vector3 up = Vector3.Cross(forward, right).normalized;

            float radius = _baseGirth * s.girth;

            for (int seg = 0; seg <= circleSegs; seg++)
            {
                float angle = (seg % circleSegs) * (2f * Mathf.PI / circleSegs);
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                Vector3 offset = right * cos + up * sin;
                int vi = ring * vertsPerRing + seg;
                verts[vi]   = s.position + offset * radius;
                normals[vi] = offset;
            }
        }

        int tBase = 0;
        for (int ring = 0; ring < ringCount - 1; ring++)
        {
            for (int seg = 0; seg < circleSegs; seg++)
            {
                int curr = ring * vertsPerRing + seg;
                int next = curr + vertsPerRing;

                tris[tBase]     = curr;
                tris[tBase + 1] = next + 1;
                tris[tBase + 2] = next;
                tris[tBase + 3] = curr;
                tris[tBase + 4] = curr + 1;
                tris[tBase + 5] = next + 1;
                tBase += 6;
            }
        }

        mesh.Clear();
        mesh.vertices  = verts;
        mesh.normals   = normals;
        mesh.triangles = tris;
    }

    // ─── Spline sampling ────────────────────────────────────────

    struct SplineSample
    {
        public Vector3 position;
        public Vector3 tangent;
        public float girth;
    }

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
                samples.Add(new SplineSample
                {
                    position = EvaluateBezier(p0, p1, p2, p3, t),
                    tangent  = EvaluateBezierTangent(p0, p1, p2, p3, t),
                    girth    = Mathf.Lerp(a.girthMultiplier, b.girthMultiplier, t),
                });
            }
        }

        return samples;
    }

    int ComputeSegmentSubdivisions(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float density)
    {
        float chordLen = Vector3.Distance(p0, p3);
        float polyLen  = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
        float curvature = Mathf.Max(0f, polyLen - chordLen);

        int baseDivs = Mathf.Max(2, Mathf.RoundToInt(chordLen * 0.5f));
        int curveDivs = Mathf.RoundToInt(curvature * 1.5f);

        float multiplier = Mathf.Max(0.1f, _lengthSubdivMultiplier * (density > 0f ? density : 1f));
        return Mathf.Max(2, Mathf.RoundToInt((baseDivs + curveDivs) * multiplier));
    }

    int ComputeCircleSegments(float density)
    {
        float multiplier = Mathf.Max(0.1f, _girthSubdivMultiplier * (density > 0f ? density : 1f));
        return Mathf.Max(3, Mathf.RoundToInt(_sides * multiplier));
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
        Quaternion frame = MainAxisFrameLocal;
        Vector3 aDir = frame * a.handleRotation * Vector3.forward;
        Vector3 bDir = frame * b.handleRotation * Vector3.forward;
        p0 = a.position;
        p1 = a.position + aDir * a.handleLength;
        p2 = b.position - bDir * b.handleLength;
        p3 = b.position;
    }

    // ─── Helpers ────────────────────────────────────────────────

    Vector3 DeriveUp(Vector3 forward)
    {
        Vector3 worldUp = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(forward.normalized, worldUp)) > 0.99f)
            worldUp = Vector3.right;
        return Vector3.Cross(forward, Vector3.Cross(worldUp, forward)).normalized;
    }

    // ─── Defaults ────────────────────────────────────────────────

    static List<SplineVertex> DefaultVertices()
    {
        return new List<SplineVertex>
        {
            new SplineVertex
            {
                position        = new Vector3(0f, 0f, -0.5f),
                handleRotation  = Quaternion.identity,
                handleLength    = 0.5f,
                girthMultiplier = 1f,
            },
            new SplineVertex
            {
                position        = new Vector3(0f, 0f, 0.5f),
                handleRotation  = Quaternion.identity,
                handleLength    = 0.5f,
                girthMultiplier = 1f,
            },
        };
    }
}
