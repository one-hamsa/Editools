using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Owns the baked mesh for a Boolean (Subject − Operator) and is the PARENT of both: the Subject and
/// Operator live under it as movable inputs, so the result has its own free transform (the "final
/// object") while editing/moving either input just re-bakes. The Subject may itself be a
/// <see cref="GreyBooleanResult"/>, so booleans can be chained.
///
/// All geometry is authored in this result's local space. Each flat surface (a coplanar,
/// edge-connected group of CSG fragments) is meshed as a QUAD GRID built IN ITS SOURCE FACE'S OWN
/// edge directions (recovered from the per-fragment face tag) so the grid stays aligned even on
/// skewed/parallelogram faces — and the cut edge is clipped exactly (clean, not stair-stepped). Grid
/// density matches <see cref="GreyboxManager"/>. The boolean is computed only in the editor (the
/// result is serialized), so no CSG runs in a player build.
/// </summary>
public class GreyBooleanResult : GreyPrimitive
{
    // Polygon tags. Subject Greybox faces keep their index 0..5 (so per-face visibility is inherited
    // and the source frame is recoverable). Operator Greybox faces use 20+index. Non-Greybox bodies
    // use the body tags. Anything >= 6 is never dropped by the face-visibility filter.
    const int k_OperatorFaceBase = 20;
    const int k_SubjectBodyTag   = 100;
    const int k_OperatorBodyTag  = 101;

    const float k_QuantScale = 10000f; // position quantization for vertex/edge matching (0.1mm)
    const float k_CoordEps   = 1e-4f;  // merge tolerance for grid-line coordinates

    [SerializeField, HideInInspector]
    [Tooltip("The Subject this result is carved from. Set by the orchestrator; not user-edited.")]
    GreyPrimitive _subject;

    [SerializeField, HideInInspector]
    [Tooltip("The Operator subtracted from the Subject. Set by the orchestrator; not user-edited.")]
    GreyPrimitive _operator;

    public GreyPrimitive Subject  => _subject;
    public GreyPrimitive Operator => _operator;

    public override bool UsesColliderMesh => true; // bakes a low-poly collider twin

    public void Configure(GreyPrimitive subject, GreyPrimitive op)
    {
        _subject  = subject;
        _operator = op;
    }

    // Face index -> the 4 corner indices that form it, wound so the polygon normal points outward.
    static readonly int[,] s_faceCorners = new int[6, 4]
    {
        { 1, 3, 7, 5 }, // +X
        { 0, 4, 6, 2 }, // -X
        { 2, 6, 7, 3 }, // +Y
        { 0, 1, 5, 4 }, // -Y
        { 4, 5, 7, 6 }, // +Z
        { 0, 2, 3, 1 }, // -Z
    };

    protected override void ResetToDefaults()
    {
        _subject  = null;
        _operator = null;
    }

#if UNITY_EDITOR
    // Source corners in THIS result's local space (set per bake). Null when the input isn't a Greybox.
    [NonSerialized] Vector3[] _subjLocal;
    [NonSerialized] Vector3[] _opLocal;

    // Cached subtraction from the render pass, reused by the immediately-following collider pass so the
    // (expensive) CSG only runs once per RebuildMesh. _subjLocal/_opLocal stay valid across both passes.
    [NonSerialized] CsgSolid _bakedSolid;
    [NonSerialized] bool[]   _bakedActiveFaces;
    [NonSerialized] float    _bakedUvScale;
#endif

    protected override void GenerateMesh(Mesh mesh)
    {
#if UNITY_EDITOR
        if (_subject == null) return; // not configured yet (transient on AddComponent)

        CsgSolid baked;
        bool[] activeFaces;
        float uvScale;

        if (SubdivisionSuppressed && _bakedSolid != null)
        {
            // Collider pass: reuse the render pass's subtraction, just mesh it minimally (density 0).
            baked = _bakedSolid;
            activeFaces = _bakedActiveFaces;
            uvScale = _bakedUvScale;
        }
        else
        {
            var subjectPolys = new List<CsgPolygon>();
            activeFaces = null;
            uvScale = 1f;

            if (_subject is Greybox gb)
            {
                _subjLocal = ToLocal(gb.transform, gb.Corners);
                AddBoxPolygons(subjectPolys, _subjLocal, faceTagBase: 0);
                activeFaces = gb.ActiveFaces;
                uvScale = gb.UvTileScale;
            }
            else
            {
                _subjLocal = null;
                AddMeshPolygons(subjectPolys, _subject, k_SubjectBodyTag);
            }
            var subject = new CsgSolid(subjectPolys);

            baked = subject;
            if (_operator != null && _operator != _subject)
            {
                var opPolys = new List<CsgPolygon>();
                if (_operator is Greybox opBox)
                {
                    _opLocal = ToLocal(opBox.transform, opBox.Corners);
                    AddBoxPolygons(opPolys, _opLocal, faceTagBase: k_OperatorFaceBase);
                }
                else
                {
                    _opLocal = null;
                    AddMeshPolygons(opPolys, _operator, k_OperatorBodyTag);
                }

                if (opPolys.Count > 0)
                    baked = CsgSolid.Subtract(subject, new CsgSolid(opPolys));
            }

            _bakedSolid = baked;
            _bakedActiveFaces = activeFaces;
            _bakedUvScale = uvScale;
        }

        BuildMesh(mesh, baked, activeFaces, uvScale);
#endif
    }

#if UNITY_EDITOR
    // ─── Solid construction (everything in THIS result's local space) ─────────

    Vector3[] ToLocal(Transform src, Vector3[] localCorners)
    {
        var dst = new Vector3[localCorners.Length];
        for (int i = 0; i < localCorners.Length; i++)
            dst[i] = transform.InverseTransformPoint(src.TransformPoint(localCorners[i]));
        return dst;
    }

    void AddBoxPolygons(List<CsgPolygon> dst, Vector3[] corners, int faceTagBase)
    {
        for (int face = 0; face < 6; face++)
        {
            Vector3 q0 = corners[s_faceCorners[face, 0]];
            Vector3 q1 = corners[s_faceCorners[face, 1]];
            Vector3 q2 = corners[s_faceCorners[face, 2]];
            Vector3 q3 = corners[s_faceCorners[face, 3]];
            int tag = faceTagBase + face;
            AddTriangle(dst, q0, q1, q2, tag);
            AddTriangle(dst, q0, q2, q3, tag);
        }
    }

    void AddMeshPolygons(List<CsgPolygon> dst, GreyPrimitive src, int tag)
    {
        var mf = src.GetComponent<MeshFilter>();
        var mesh = mf != null ? mf.sharedMesh : null;
        if (mesh == null)
        {
            Debug.LogError($"[GreyBoolean] '{src.name}' has no mesh.");
            return;
        }

        var verts = mesh.vertices;
        var tris = mesh.triangles;
        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 a = transform.InverseTransformPoint(src.transform.TransformPoint(verts[tris[i]]));
            Vector3 b = transform.InverseTransformPoint(src.transform.TransformPoint(verts[tris[i + 1]]));
            Vector3 c = transform.InverseTransformPoint(src.transform.TransformPoint(verts[tris[i + 2]]));
            AddTriangle(dst, a, b, c, tag);
        }
    }

    static void AddTriangle(List<CsgPolygon> dst, Vector3 a, Vector3 b, Vector3 c, int tag)
    {
        if (CsgPlane.TryFromPoints(a, b, c, out var plane))
            dst.Add(new CsgPolygon(new List<Vector3>(3) { a, b, c }, tag, plane));
    }

    // ─── Mesh output: group into flat surfaces, mesh each as a face-aligned quad grid ──

    void BuildMesh(Mesh mesh, CsgSolid solid, bool[] activeFaces, float uvScale)
    {
        float effective = ComputeEffectiveDensity();
        float target = effective > 0f ? 1f / effective : 0f; // target edge length (m); 0 = quads only at seams

        var polys = new List<CsgPolygon>();
        foreach (var p in solid.polygons)
        {
            if (p.tag >= 0 && p.tag < 6 && activeFaces != null && !activeFaces[p.tag]) continue;
            polys.Add(p);
        }

        var posId = new Dictionary<VKey, int>();
        var polyIds = new List<int[]>(polys.Count);
        foreach (var p in polys)
        {
            var ids = new int[p.verts.Count];
            for (int k = 0; k < p.verts.Count; k++) ids[k] = GetPosId(posId, p.verts[k]);
            polyIds.Add(ids);
        }

        // Union coplanar + edge-sharing polygons into flat surfaces.
        var parent = new int[polys.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        var edgeFirst = new Dictionary<long, int>();
        for (int pi = 0; pi < polys.Count; pi++)
        {
            var ids = polyIds[pi];
            int n = ids.Length;
            for (int k = 0; k < n; k++)
            {
                long key = EdgeKey(ids[k], ids[(k + 1) % n]);
                if (edgeFirst.TryGetValue(key, out int other))
                {
                    if (Coplanar(polys[pi].plane, polys[other].plane)) Union(parent, pi, other);
                }
                else edgeFirst[key] = pi;
            }
        }
        var groups = new Dictionary<int, List<int>>();
        for (int pi = 0; pi < polys.Count; pi++)
        {
            int root = Find(parent, pi);
            if (!groups.TryGetValue(root, out var list)) { list = new List<int>(); groups[root] = list; }
            list.Add(pi);
        }

        var verts   = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs     = new List<Vector2>();
        var tris    = new List<int>();
        foreach (var group in groups.Values)
            EmitSurface(group, polys, polyIds, target, uvScale, verts, normals, uvs, tris);

        mesh.Clear();
        if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
    }

    void EmitSurface(List<int> group, List<CsgPolygon> polys, List<int[]> polyIds,
        float target, float uvScale,
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris)
    {
        Vector3 normal = polys[group[0]].plane.normal;
        ResolveFrame(group, polys, polyIds, normal, out Vector3 origin, out Vector3 uDir, out Vector3 vDir);

        // The grid winding follows uDir×vDir; flip it when that opposes the surface's true normal
        // (e.g. operator cut-walls, whose source-face frame points outward into the removed solid).
        bool flip = Vector3.Dot(Vector3.Cross(uDir, vDir), normal) < 0f;

        // Project fragments into the (a,b) face frame; collect seam coordinates.
        var frags = new List<Vector2[]>(group.Count);
        var uCoords = new List<float>();
        var vCoords = new List<float>();
        foreach (int pi in group)
        {
            var src = polys[pi].verts;
            var pts = new Vector2[src.Count];
            for (int k = 0; k < src.Count; k++)
            {
                Vector2 ab = ProjectAB(src[k], origin, uDir, vDir, normal);
                pts[k] = ab;
                uCoords.Add(ab.x);
                vCoords.Add(ab.y);
            }
            frags.Add(EnsureCcw(pts));
        }

        // Cut edges = boundary edges (appear in only one fragment), in (a,b).
        var cutEdges = CollectBoundaryEdges(group, polys, polyIds, origin, uDir, vDir, normal);

        float worldPerU = transform.TransformVector(uDir).magnitude;
        float worldPerV = transform.TransformVector(vDir).magnitude;
        var us = Infill(UniqueSorted(uCoords), target, worldPerU);
        var vs = Infill(UniqueSorted(vCoords), target, worldPerV);
        if (us.Count < 2 || vs.Count < 2) return;

        var gridVert = new Dictionary<long, int>();
        for (int i = 0; i < us.Count - 1; i++)
        {
            for (int j = 0; j < vs.Count - 1; j++)
            {
                float a0 = us[i], a1 = us[i + 1], b0 = vs[j], b1 = vs[j + 1];

                var c00 = new Vector2(a0, b0);
                var c10 = new Vector2(a1, b0);
                var c11 = new Vector2(a1, b1);
                var c01 = new Vector2(a0, b1);

                bool fullyInside = InsideAny(c00, frags) && InsideAny(c10, frags)
                                && InsideAny(c11, frags) && InsideAny(c01, frags)
                                && !CutCrossesCell(cutEdges, a0, a1, b0, b1);

                if (fullyInside)
                {
                    int va = GridVert(i,     j,     us, vs, origin, uDir, vDir, normal, uvScale, gridVert, verts, normals, uvs);
                    int vb = GridVert(i + 1, j,     us, vs, origin, uDir, vDir, normal, uvScale, gridVert, verts, normals, uvs);
                    int vc = GridVert(i + 1, j + 1, us, vs, origin, uDir, vDir, normal, uvScale, gridVert, verts, normals, uvs);
                    int vd = GridVert(i,     j + 1, us, vs, origin, uDir, vDir, normal, uvScale, gridVert, verts, normals, uvs);
                    if (!flip)
                    {
                        tris.Add(va); tris.Add(vb); tris.Add(vc);
                        tris.Add(va); tris.Add(vc); tris.Add(vd);
                    }
                    else
                    {
                        tris.Add(va); tris.Add(vc); tris.Add(vb);
                        tris.Add(va); tris.Add(vd); tris.Add(vc);
                    }
                    continue;
                }

                // Boundary cell: clip the cell against each fragment and emit the pieces (clean cut).
                var cell = new List<Vector2> { c00, c10, c11, c01 };
                foreach (var frag in frags)
                {
                    var clipped = ClipConvex(cell, frag);
                    if (clipped.Count < 3) continue;
                    EmitFan(clipped, origin, uDir, vDir, normal, uvScale, flip, verts, normals, uvs, tris);
                }
            }
        }
    }

    // ─── Frame resolution ─────────────────────────────────────────────────────

    void ResolveFrame(List<int> group, List<CsgPolygon> polys, List<int[]> polyIds, Vector3 normal,
        out Vector3 origin, out Vector3 uDir, out Vector3 vDir)
    {
        // Prefer the source face's own edge directions (recovered from the tag) — aligns the grid to
        // skewed/parallelogram faces exactly like Greybox.
        foreach (int pi in group)
        {
            int tag = polys[pi].tag;
            Vector3[] corners = null;
            int face = -1;
            if (tag >= 0 && tag < 6 && _subjLocal != null) { corners = _subjLocal; face = tag; }
            else if (tag >= k_OperatorFaceBase && tag < k_OperatorFaceBase + 6 && _opLocal != null)
            { corners = _opLocal; face = tag - k_OperatorFaceBase; }

            if (corners != null)
            {
                Vector3 c0 = corners[s_faceCorners[face, 0]];
                Vector3 c1 = corners[s_faceCorners[face, 1]];
                Vector3 c3 = corners[s_faceCorners[face, 3]];
                // Project the face's edge directions onto THIS surface's actual plane. A distorted
                // (non-planar) face is split by the CSG into triangles whose plane differs from the
                // face's corner span; using the raw corner edges would place grid verts off-plane.
                origin = c0;
                uDir = Vector3.ProjectOnPlane(c1 - c0, normal);
                vDir = Vector3.ProjectOnPlane(c3 - c0, normal);
                if (uDir.sqrMagnitude > 1e-10f && vDir.sqrMagnitude > 1e-10f)
                    return;
                break; // degenerate after projection — fall through to the boundary-edge frame
            }
        }

        // Fallback (mesh inputs): align to the longest boundary edge.
        origin = polys[group[0]].verts[0];
        uDir = LongestBoundaryEdge(group, polys, polyIds, normal);
        vDir = Vector3.Cross(normal, uDir).normalized;
    }

    Vector3 LongestBoundaryEdge(List<int> group, List<CsgPolygon> polys, List<int[]> polyIds, Vector3 normal)
    {
        var count = new Dictionary<long, (int a, int b, int n)>();
        var posOf = new Dictionary<int, Vector3>();
        foreach (int pi in group)
        {
            var poly = polys[pi];
            var ids = polyIds[pi];
            int n = ids.Length;
            for (int k = 0; k < n; k++)
            {
                int a = ids[k], b = ids[(k + 1) % n];
                posOf[a] = poly.verts[k];
                long key = EdgeKey(a, b);
                count[key] = count.TryGetValue(key, out var e) ? (e.a, e.b, e.n + 1) : (a, b, 1);
            }
        }

        Vector3 best = Vector3.zero;
        float bestLen = 0f;
        foreach (var e in count.Values)
        {
            if (e.n != 1) continue; // boundary edges only
            Vector3 dir = posOf[e.b] - posOf[e.a];
            float len = dir.sqrMagnitude;
            if (len > bestLen) { bestLen = len; best = dir; }
        }
        if (bestLen < 1e-10f)
        {
            Vector3 t = Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up;
            return Vector3.Cross(normal, t).normalized;
        }
        return best.normalized;
    }

    List<(Vector2 p, Vector2 q)> CollectBoundaryEdges(List<int> group, List<CsgPolygon> polys, List<int[]> polyIds,
        Vector3 origin, Vector3 uDir, Vector3 vDir, Vector3 normal)
    {
        var count = new Dictionary<long, int>();
        var endA = new Dictionary<long, Vector2>();
        var endB = new Dictionary<long, Vector2>();
        foreach (int pi in group)
        {
            var poly = polys[pi];
            var ids = polyIds[pi];
            int n = ids.Length;
            for (int k = 0; k < n; k++)
            {
                int a = ids[k], b = ids[(k + 1) % n];
                long key = EdgeKey(a, b);
                if (!count.ContainsKey(key))
                {
                    count[key] = 1;
                    endA[key] = ProjectAB(poly.verts[k], origin, uDir, vDir, normal);
                    endB[key] = ProjectAB(poly.verts[(k + 1) % n], origin, uDir, vDir, normal);
                }
                else count[key]++;
            }
        }

        var result = new List<(Vector2, Vector2)>();
        foreach (var kv in count)
            if (kv.Value == 1) result.Add((endA[kv.Key], endB[kv.Key]));
        return result;
    }

    // ─── (a,b) frame math & emission ──────────────────────────────────────────

    static Vector2 ProjectAB(Vector3 p, Vector3 origin, Vector3 uDir, Vector3 vDir, Vector3 n)
    {
        Vector3 d = p - origin;
        float denom = Vector3.Dot(Vector3.Cross(uDir, vDir), n);
        if (Mathf.Abs(denom) < 1e-12f) return Vector2.zero;
        float a = Vector3.Dot(Vector3.Cross(d, vDir), n) / denom;
        float b = Vector3.Dot(Vector3.Cross(uDir, d), n) / denom;
        return new Vector2(a, b);
    }

    static int GridVert(int i, int j, List<float> us, List<float> vs,
        Vector3 origin, Vector3 uDir, Vector3 vDir, Vector3 normal, float uvScale,
        Dictionary<long, int> cache, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs)
    {
        long key = ((long)i << 32) | (uint)j;
        if (cache.TryGetValue(key, out int idx)) return idx;
        idx = AddVert(origin + us[i] * uDir + vs[j] * vDir, normal, uvScale, verts, normals, uvs);
        cache[key] = idx;
        return idx;
    }

    static void EmitFan(List<Vector2> ab, Vector3 origin, Vector3 uDir, Vector3 vDir, Vector3 normal,
        float uvScale, bool flip, List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris)
    {
        int b0 = verts.Count;
        for (int k = 0; k < ab.Count; k++)
            AddVert(origin + ab[k].x * uDir + ab[k].y * vDir, normal, uvScale, verts, normals, uvs);
        for (int k = 1; k < ab.Count - 1; k++)
        {
            tris.Add(b0);
            tris.Add(flip ? b0 + k + 1 : b0 + k);
            tris.Add(flip ? b0 + k : b0 + k + 1);
        }
    }

    static int AddVert(Vector3 p, Vector3 normal, float uvScale,
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs)
    {
        int idx = verts.Count;
        verts.Add(p);
        normals.Add(normal);
        uvs.Add(PlanarUv(p, normal, uvScale));
        return idx;
    }

    // ─── 2D helpers (in the (a,b) frame) ──────────────────────────────────────

    static Vector2[] EnsureCcw(Vector2[] poly)
    {
        float area = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Length];
            area += a.x * b.y - b.x * a.y;
        }
        if (area < 0f) Array.Reverse(poly);
        return poly;
    }

    static bool InsideAny(Vector2 p, List<Vector2[]> frags)
    {
        foreach (var f in frags) if (PointInConvex(p, f)) return true;
        return false;
    }

    static bool PointInConvex(Vector2 p, Vector2[] poly)
    {
        bool pos = false, neg = false;
        int n = poly.Length;
        for (int k = 0; k < n; k++)
        {
            Vector2 a = poly[k], b = poly[(k + 1) % n];
            float cr = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            if (cr > 1e-6f) pos = true;
            else if (cr < -1e-6f) neg = true;
            if (pos && neg) return false;
        }
        return true;
    }

    static bool CutCrossesCell(List<(Vector2 p, Vector2 q)> edges, float a0, float a1, float b0, float b1)
    {
        foreach (var e in edges)
        {
            // Endpoint strictly inside the cell?
            if (StrictInside(e.p, a0, a1, b0, b1) || StrictInside(e.q, a0, a1, b0, b1)) return true;
            // Crosses a cell edge?
            if (SegSeg(e.p, e.q, new Vector2(a0, b0), new Vector2(a1, b0))) return true;
            if (SegSeg(e.p, e.q, new Vector2(a1, b0), new Vector2(a1, b1))) return true;
            if (SegSeg(e.p, e.q, new Vector2(a1, b1), new Vector2(a0, b1))) return true;
            if (SegSeg(e.p, e.q, new Vector2(a0, b1), new Vector2(a0, b0))) return true;
        }
        return false;
    }

    static bool StrictInside(Vector2 p, float a0, float a1, float b0, float b1)
    {
        const float e = 1e-5f;
        return p.x > a0 + e && p.x < a1 - e && p.y > b0 + e && p.y < b1 - e;
    }

    static bool SegSeg(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        float d1 = Cross(p4 - p3, p1 - p3);
        float d2 = Cross(p4 - p3, p2 - p3);
        float d3 = Cross(p2 - p1, p3 - p1);
        float d4 = Cross(p2 - p1, p4 - p1);
        return ((d1 > 1e-7f && d2 < -1e-7f) || (d1 < -1e-7f && d2 > 1e-7f))
            && ((d3 > 1e-7f && d4 < -1e-7f) || (d3 < -1e-7f && d4 > 1e-7f));
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    // Sutherland–Hodgman: clip convex 'subject' by convex CCW 'clip'.
    static List<Vector2> ClipConvex(List<Vector2> subject, Vector2[] clip)
    {
        var output = new List<Vector2>(subject);
        int cn = clip.Length;
        for (int c = 0; c < cn && output.Count > 0; c++)
        {
            Vector2 A = clip[c], B = clip[(c + 1) % cn];
            Vector2 edge = B - A;
            var input = output;
            output = new List<Vector2>(input.Count + 1);
            for (int i = 0; i < input.Count; i++)
            {
                Vector2 cur = input[i];
                Vector2 prev = input[(i - 1 + input.Count) % input.Count];
                bool curIn = Cross(edge, cur - A) >= -1e-7f;
                bool prevIn = Cross(edge, prev - A) >= -1e-7f;
                if (curIn)
                {
                    if (!prevIn) output.Add(LineIntersect(prev, cur, A, B));
                    output.Add(cur);
                }
                else if (prevIn)
                {
                    output.Add(LineIntersect(prev, cur, A, B));
                }
            }
        }
        return output;
    }

    static Vector2 LineIntersect(Vector2 p1, Vector2 p2, Vector2 a, Vector2 b)
    {
        Vector2 r = p2 - p1, s = b - a;
        float denom = Cross(r, s);
        if (Mathf.Abs(denom) < 1e-12f) return p1;
        float t = Cross(a - p1, s) / denom;
        return p1 + t * r;
    }

    static List<float> UniqueSorted(List<float> coords)
    {
        coords.Sort();
        var result = new List<float>(coords.Count);
        foreach (float c in coords)
            if (result.Count == 0 || c - result[result.Count - 1] > k_CoordEps)
                result.Add(c);
        return result;
    }

    static List<float> Infill(List<float> coords, float target, float worldPer)
    {
        if (coords.Count < 2 || target <= 0f || worldPer <= 0f) return coords;
        var result = new List<float>(coords.Count);
        for (int i = 0; i < coords.Count - 1; i++)
        {
            result.Add(coords[i]);
            float gap = coords[i + 1] - coords[i];
            int segs = Mathf.Max(1, Mathf.CeilToInt(gap * worldPer / target - 1e-4f));
            for (int s = 1; s < segs; s++)
                result.Add(coords[i] + gap * (s / (float)segs));
        }
        result.Add(coords[coords.Count - 1]);
        return result;
    }

    // ─── Surface grouping helpers ─────────────────────────────────────────────

    static int GetPosId(Dictionary<VKey, int> ids, Vector3 p)
    {
        var key = new VKey(p);
        if (!ids.TryGetValue(key, out int id)) { id = ids.Count; ids[key] = id; }
        return id;
    }

    static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    static bool Coplanar(CsgPlane a, CsgPlane b) =>
        Vector3.Dot(a.normal, b.normal) > 0.999f && Mathf.Abs(a.w - b.w) < 1e-3f;

    static int Find(int[] parent, int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb) parent[ra] = rb;
    }

    static Vector2 PlanarUv(Vector3 p, Vector3 n, float scale)
    {
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
        Vector2 uv;
        if (ax >= ay && ax >= az)      uv = new Vector2(p.z, p.y);
        else if (ay >= ax && ay >= az) uv = new Vector2(p.x, p.z);
        else                           uv = new Vector2(p.x, p.y);
        return uv * scale;
    }

    readonly struct VKey : IEquatable<VKey>
    {
        readonly int _x, _y, _z;

        public VKey(Vector3 p)
        {
            _x = Mathf.RoundToInt(p.x * k_QuantScale);
            _y = Mathf.RoundToInt(p.y * k_QuantScale);
            _z = Mathf.RoundToInt(p.z * k_QuantScale);
        }

        public bool Equals(VKey o) => _x == o._x && _y == o._y && _z == o._z;
        public override bool Equals(object o) => o is VKey k && Equals(k);
        public override int GetHashCode()
        {
            unchecked { int h = 17; h = h * 31 + _x; h = h * 31 + _y; h = h * 31 + _z; return h; }
        }
    }
#endif
}
