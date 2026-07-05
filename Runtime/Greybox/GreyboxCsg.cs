using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Minimal BSP-based mesh boolean used by the Greybox Boolean feature.
/// Port of the classic csg.js BSP algorithm (Evan Wallace, MIT). Edit-time bake only —
/// the result is serialized, so this never runs in a player build.
///
/// Polygons are convex (triangles or convex clip fragments). Vertices carry only
/// position; per-vertex normals and UVs are derived at mesh-build time from each
/// polygon's plane, matching Greybox's flat-shaded look.
///
/// A polygon's <see cref="CsgPolygon.tag"/> survives splits, so result faces can be
/// traced back to the Subject face (0..5) or the Operator (see GreyBooleanResult) they
/// came from — that's how Subject face-visibility is inherited.
/// </summary>
sealed class CsgPlane
{
    public const float Epsilon = 1e-5f;

    public Vector3 normal;
    public float w;

    public CsgPlane(Vector3 normal, float w)
    {
        this.normal = normal;
        this.w = w;
    }

    public static bool TryFromPoints(Vector3 a, Vector3 b, Vector3 c, out CsgPlane plane)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        float len = n.magnitude;
        if (len < Epsilon) { plane = null; return false; } // degenerate triangle -> skip
        n /= len;
        plane = new CsgPlane(n, Vector3.Dot(n, a));
        return true;
    }

    public CsgPlane Clone() => new CsgPlane(normal, w);

    public void Flip()
    {
        normal = -normal;
        w = -w;
    }

    const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

    /// <summary>
    /// Splits <paramref name="polygon"/> by this plane, appending the pieces to the
    /// matching output list. Mirrors csg.js Plane.splitPolygon.
    /// </summary>
    public void SplitPolygon(CsgPolygon polygon,
        List<CsgPolygon> coplanarFront, List<CsgPolygon> coplanarBack,
        List<CsgPolygon> front, List<CsgPolygon> back)
    {
        var verts = polygon.verts;
        int count = verts.Count;

        int polygonType = 0;
        var types = new int[count];
        for (int i = 0; i < count; i++)
        {
            float t = Vector3.Dot(normal, verts[i]) - w;
            int type = (t < -Epsilon) ? Back : (t > Epsilon) ? Front : Coplanar;
            polygonType |= type;
            types[i] = type;
        }

        switch (polygonType)
        {
            case Coplanar:
                (Vector3.Dot(normal, polygon.plane.normal) > 0 ? coplanarFront : coplanarBack).Add(polygon);
                break;
            case Front:
                front.Add(polygon);
                break;
            case Back:
                back.Add(polygon);
                break;
            default: // Spanning — clip into a front piece and a back piece
                var f = new List<Vector3>();
                var b = new List<Vector3>();
                for (int i = 0; i < count; i++)
                {
                    int j = (i + 1) % count;
                    int ti = types[i], tj = types[j];
                    Vector3 vi = verts[i], vj = verts[j];

                    if (ti != Back) f.Add(vi);
                    if (ti != Front) b.Add(vi);

                    if ((ti | tj) == Spanning)
                    {
                        float t = (w - Vector3.Dot(normal, vi)) / Vector3.Dot(normal, vj - vi);
                        Vector3 v = Vector3.Lerp(vi, vj, t);
                        f.Add(v);
                        b.Add(v);
                    }
                }
                if (f.Count >= 3) front.Add(new CsgPolygon(f, polygon.tag, polygon.plane.Clone()));
                if (b.Count >= 3) back.Add(new CsgPolygon(b, polygon.tag, polygon.plane.Clone()));
                break;
        }
    }
}

/// <summary>Convex polygon with a source <see cref="tag"/> (Subject face index 0..5, or Operator).</summary>
sealed class CsgPolygon
{
    public readonly List<Vector3> verts;
    public readonly CsgPlane plane;
    public readonly int tag;

    public CsgPolygon(List<Vector3> verts, int tag, CsgPlane plane)
    {
        this.verts = verts;
        this.tag = tag;
        this.plane = plane;
    }

    public void Flip()
    {
        verts.Reverse();
        plane.Flip();
    }
}

/// <summary>BSP tree node. Mirrors csg.js Node.</summary>
sealed class CsgNode
{
    CsgPlane _plane;
    CsgNode _front;
    CsgNode _back;
    readonly List<CsgPolygon> _polygons = new List<CsgPolygon>();

    // Runaway backstop for pathological/degenerate input — e.g. sliver polygons from chaining many
    // booleans that keep spanning-splitting. The tree is built on an explicit stack (no call-stack
    // recursion), so this bounds work/memory, not stack frames. Far above any legitimate mesh's BSP depth.
    internal const int MaxDepth = 4096;

    // Set by Build when MaxDepth is reached; CsgSolid.Subtract reads it to fail the bake loudly instead
    // of returning silently-wrong geometry. XXX: static is safe — the boolean bake is edit-time and
    // single-threaded, and Subtract clears it before each operation.
    internal static bool s_depthExceeded;

    public CsgNode() { }
    public CsgNode(List<CsgPolygon> polygons) => Build(polygons);

    // The traversals below are iterative (explicit stack) rather than the recursive csg.js originals:
    // a deep/degenerate BSP — which chaining booleans produces — would otherwise overflow the call
    // stack and hard-crash the editor.

    public void Invert()
    {
        var stack = new Stack<CsgNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            for (int i = 0; i < node._polygons.Count; i++) node._polygons[i].Flip();
            node._plane?.Flip();
            (node._front, node._back) = (node._back, node._front);
            if (node._front != null) stack.Push(node._front);
            if (node._back != null) stack.Push(node._back);
        }
    }

    public List<CsgPolygon> ClipPolygons(List<CsgPolygon> polygons)
    {
        var result = new List<CsgPolygon>();
        var stack = new Stack<(CsgNode node, List<CsgPolygon> polys)>();
        stack.Push((this, polygons));
        while (stack.Count > 0)
        {
            var (node, polys) = stack.Pop();
            if (node._plane == null)
            {
                result.AddRange(polys);
                continue;
            }

            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();
            for (int i = 0; i < polys.Count; i++)
                node._plane.SplitPolygon(polys[i], front, back, front, back);

            if (node._front != null) stack.Push((node._front, front));
            else result.AddRange(front);

            if (node._back != null) stack.Push((node._back, back));
            // no back child -> back polygons are inside the solid and dropped
        }
        return result;
    }

    public void ClipTo(CsgNode bsp)
    {
        var stack = new Stack<CsgNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var clipped = bsp.ClipPolygons(node._polygons);
            node._polygons.Clear();
            node._polygons.AddRange(clipped);
            if (node._front != null) stack.Push(node._front);
            if (node._back != null) stack.Push(node._back);
        }
    }

    public void AllPolygons(List<CsgPolygon> result)
    {
        var stack = new Stack<CsgNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            result.AddRange(node._polygons);
            // push back then front so front pops first — preserves the original self→front→back order
            if (node._back != null) stack.Push(node._back);
            if (node._front != null) stack.Push(node._front);
        }
    }

    public void Build(List<CsgPolygon> polygons)
    {
        if (polygons == null || polygons.Count == 0) return;

        var stack = new Stack<(CsgNode node, List<CsgPolygon> polys, int depth)>();
        stack.Push((this, polygons, 0));
        while (stack.Count > 0)
        {
            var (node, polys, depth) = stack.Pop();
            if (polys.Count == 0) continue;
            node._plane ??= polys[0].plane.Clone();

            // Pathological geometry: stop subdividing and keep the remaining polygons as a leaf rather
            // than splitting forever; flag it so Subtract fails loudly instead of the editor hanging.
            if (depth >= MaxDepth)
            {
                s_depthExceeded = true;
                node._polygons.AddRange(polys);
                continue;
            }

            var front = new List<CsgPolygon>();
            var back = new List<CsgPolygon>();
            for (int i = 0; i < polys.Count; i++)
                node._plane.SplitPolygon(polys[i], node._polygons, node._polygons, front, back);

            if (front.Count > 0)
            {
                node._front ??= new CsgNode();
                stack.Push((node._front, front, depth + 1));
            }
            if (back.Count > 0)
            {
                node._back ??= new CsgNode();
                stack.Push((node._back, back, depth + 1));
            }
        }
    }
}

/// <summary>A solid as a flat list of convex polygons, plus the boolean ops over them.</summary>
sealed class CsgSolid
{
    public readonly List<CsgPolygon> polygons;

    public CsgSolid(List<CsgPolygon> polygons) => this.polygons = polygons;

    /// <summary>Returns a − b (the part of <paramref name="a"/> outside <paramref name="b"/>).</summary>
    public static CsgSolid Subtract(CsgSolid a, CsgSolid b)
    {
        // An empty subject builds an empty BSP that clips nothing, so the operator would come back
        // whole and inverted (an inside-out copy of the cutter). Nothing minus anything is nothing.
        if (a.polygons.Count == 0) return new CsgSolid(new List<CsgPolygon>());

        CsgNode.s_depthExceeded = false;

        var na = new CsgNode(ClonePolygons(a.polygons));
        var nb = new CsgNode(ClonePolygons(b.polygons));

        na.Invert();
        na.ClipTo(nb);
        nb.ClipTo(na);
        nb.Invert();
        nb.ClipTo(na);
        nb.Invert();

        var bPolys = new List<CsgPolygon>();
        nb.AllPolygons(bPolys);
        na.Build(bPolys);
        na.Invert();

        if (CsgNode.s_depthExceeded)
        {
            Debug.LogError($"[GreyBoolean] CSG subtraction hit the max BSP depth ({CsgNode.MaxDepth}) — " +
                           "input geometry is too complex or degenerate (usually from chaining many " +
                           "booleans). Keeping the un-cut subject; flatten or simplify the chain.");
            return a;
        }

        var result = new List<CsgPolygon>();
        na.AllPolygons(result);
        return new CsgSolid(result);
    }

    static List<CsgPolygon> ClonePolygons(List<CsgPolygon> src)
    {
        var dst = new List<CsgPolygon>(src.Count);
        for (int i = 0; i < src.Count; i++)
        {
            var p = src[i];
            dst.Add(new CsgPolygon(new List<Vector3>(p.verts), p.tag, p.plane.Clone()));
        }
        return dst;
    }
}
