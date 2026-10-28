#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Editools.Geometry;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// The closed outline of one flat surface of a boolean result, rebuilt from its CSG fragments in a
/// 2D face frame, and its grid-clipped triangulation. Coordinates are integer multiples of
/// <see cref="Unit"/> (a local-space length chosen above the float noise of the fragments), so
/// fragment seams, T-junctions and duplicate split vertices snap together before the union.
/// </summary>
internal sealed class GreyFaceOutline
{
    // Contours narrower than this (in outline units) are CSG seam noise, not surface: hairline slits between
    // fragments whose split vertices disagree by float error, and slivers of the same origin.
    const double k_MinWidth = 16d;

    readonly PolyTree _faces;
    readonly double _tolerance;
    readonly double _area;

    public double Unit { get; }
    public IntRect Bounds { get; }

    GreyFaceOutline(double unit, PolyTree faces, IntRect bounds, double tolerance, double area)
    {
        Unit = unit;
        _faces = faces;
        Bounds = bounds;
        _tolerance = tolerance;
        _area = area;
    }

    public Vector2 ToVector(IntPoint p) => new Vector2((float)(p.X * Unit), (float)(p.Y * Unit));

    /// <summary>Unions the 2D fragments of one surface into its outline. False (with the reason) when the fragments do not form one.</summary>
    public static bool TryBuild(List<Vector2[]> fragments, double unit, out GreyFaceOutline outline, out string error)
    {
        outline = null;
        error = null;
        var paths = new List<List<IntPoint>>(fragments.Count);
        double inputArea = 0d, perimeter = 0d;
        foreach (var fragment in fragments)
        {
            var path = new List<IntPoint>(fragment.Length);
            foreach (var p in fragment)
                path.Add(new IntPoint((long)Math.Round(p.x / unit), (long)Math.Round(p.y / unit)));
            double pathArea = Clipper.Area(path);
            if (pathArea < 0d) { path.Reverse(); pathArea = -pathArea; }
            if (pathArea == 0d) continue; // thinner than the snap unit: CSG split noise, not surface
            inputArea += pathArea;
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i]; var b = path[(i + 1) % path.Count];
                perimeter += Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y);
            }
            paths.Add(path);
        }
        if (paths.Count == 0)
        {
            // Every fragment collapsed: the surface has no area to mesh.
            outline = new GreyFaceOutline(unit, new PolyTree(), new IntRect(0, 0, 0, 0), 0d, 0d);
            return true;
        }
        WeldSeams(paths, (long)(k_MinWidth / 2d));
        try
        {
            // Not StrictlySimple: this Clipper's strict post-pass splits a merged face along spurious seams.
            var union = new Clipper();
            union.AddPaths(paths, PolyTypes.ptSubject, true);
            var faces = new PolyTree();
            if (!union.Execute(ClipTypes.ctUnion, faces, PolyFillTypes.pftNonZero))
            { error = "Face outline union failed."; return false; }
            CleanContours(faces);
            RemoveSlivers(faces);
            double faceArea = TreeArea(faces);
            double tolerance = Math.Max(perimeter * k_MinWidth, Math.Abs(inputArea) * 1e-6d);
            if (Math.Abs(faceArea - inputArea) > tolerance)
            { error = "Face outline area does not match its source polygons."; return false; }
            var bounds = Clipper.GetBounds(AllContours(faces));
            outline = new GreyFaceOutline(unit, faces, bounds, tolerance, faceArea);
            return true;
        }
        catch (Exception exception)
        {
            // Clipper exceptions are contained at this authoring-time boundary.
            Debug.LogException(exception);
            error = "Face outline clipping failed.";
            return false;
        }
    }

    /// <summary>
    /// Clips the outline by a grid fitted to it and triangulates every piece. Grid lines pass through the
    /// outline's corners, the gaps between them are filled at the target cell size (outline units per axis;
    /// 0 = no interior lines) and grid points near a cut edge are pulled onto it, so no cell leaves a sliver
    /// along the outline. Output vertices are deduplicated.
    /// </summary>
    public bool TryTriangulate(double targetA, double targetB,
        out List<IntPoint> vertices, out List<int> triangles, out string error)
    {
        vertices = new List<IntPoint>(64);
        triangles = new List<int>(128);
        error = null;
        try
        {
            var contours = AllContours(_faces);
            var columns = FittedLines(contours, true, Bounds.left, Bounds.right, targetA);
            var rows = FittedLines(contours, false, Bounds.top, Bounds.bottom, targetB);
            var grid = FitGrid(columns, rows, contours, k_SnapFraction * Math.Min(targetA, targetB));
            using var vertexLease = DictionaryPool<IntPoint, int>.Get(out var vertexIds);
            if (!MeshFaces(_faces, grid, columns.Count, rows.Count, vertexIds, vertices, triangles))
            { error = "Face outline could not be completely triangulated."; return false; }
            double area = 0d;
            for (int i = 0; i < triangles.Count; i += 3)
            {
                double twice = Cross(vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]);
                if (twice <= 0d) { error = "Face triangulation contains a reversed or degenerate triangle."; return false; }
                area += twice * 0.5d;
            }
            if (Math.Abs(area - _area) > _tolerance)
            { error = "Face triangulation does not cover its complete outline."; return false; }
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            error = "Face outline clipping failed.";
            return false;
        }
    }

    // Grid lines closer than this fraction of the target cell size merge into one (the earlier one wins, the
    // bounds always win), and grid points move at most this far onto a nearby outline corner or edge.
    // Half the merge distance, so two neighbouring grid points cannot cross while snapping.
    const double k_MergeFraction = 0.5d;
    const double k_SnapFraction = 0.25d;

    /// <summary>
    /// Sorted grid lines along one axis: the bounds, every outline corner coordinate (merged when closer than
    /// half a cell) and evenly spaced fill lines in the gaps at <paramref name="target"/> cell size.
    /// </summary>
    static List<long> FittedLines(List<List<IntPoint>> contours, bool alongX, long min, long max, double target)
    {
        var lines = new List<long>(16) { min };
        if (target > 0d && max > min)
        {
            using var cornerLease = ListPool<long>.Get(out var corners);
            foreach (var contour in contours)
                foreach (var p in contour) corners.Add(alongX ? p.X : p.Y);
            corners.Sort();
            double merge = target * k_MergeFraction;
            foreach (long corner in corners)
            {
                if (corner - lines[lines.Count - 1] < merge) continue;
                if (max - corner < merge) break;
                lines.Add(corner);
            }
            lines.Add(max);
            // Fill every gap evenly at the target size (one cell when the gap is below 1.5 cells).
            var filled = new List<long>(lines.Count * 2) { min };
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                long from = lines[i], to = lines[i + 1];
                int segments = Math.Max(1, (int)Math.Round((to - from) / target));
                for (int s = 1; s < segments; s++)
                    filled.Add(from + (long)Math.Round((to - from) * (s / (double)segments)));
                filled.Add(to);
            }
            return filled;
        }
        lines.Add(max);
        return lines;
    }

    /// <summary>
    /// Grid points (row-major, <c>rows x columns</c>) at the line crossings, each pulled onto the nearest
    /// outline corner, else the nearest outline edge, within <paramref name="snap"/>. Points on the bounds
    /// only slide along their bound, so the cells still tile the whole outline. Any cell the snapping would
    /// fold or flatten gets its corners back on the lines.
    /// </summary>
    static IntPoint[] FitGrid(List<long> columns, List<long> rows, List<List<IntPoint>> contours, double snap)
    {
        int width = columns.Count, height = rows.Count;
        var grid = new IntPoint[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                grid[y * width + x] = new IntPoint(columns[x], rows[y]);
        if (snap <= 0d || width < 2 || height < 2) return grid;

        double snapSquared = snap * snap;
        var snapped = new bool[grid.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool onColumnBound = x == 0 || x == width - 1, onRowBound = y == 0 || y == height - 1;
                if (onColumnBound && onRowBound) continue;
                var p = grid[y * width + x];
                IntPoint best = p;
                double bestSquared = snapSquared;
                bool found = false;
                foreach (var contour in contours)
                    foreach (var corner in contour)
                    {
                        if (onColumnBound && corner.X != p.X || onRowBound && corner.Y != p.Y) continue;
                        double d = DistanceSquared(p, corner);
                        if (d > bestSquared) continue;
                        bestSquared = d; best = corner; found = true;
                    }
                if (!found && !onColumnBound && !onRowBound)
                    foreach (var contour in contours)
                        for (int i = 0; i < contour.Count; i++)
                        {
                            IntPoint a = contour[i], b = contour[(i + 1) % contour.Count];
                            double t = ProjectOnSegment(p, a, b, out double d);
                            if (t <= 0d || t >= 1d || d > bestSquared) continue;
                            bestSquared = d;
                            best = new IntPoint((long)Math.Round(a.X + (b.X - a.X) * t), (long)Math.Round(a.Y + (b.Y - a.Y) * t));
                            found = true;
                        }
                if (!found) continue;
                grid[y * width + x] = best;
                snapped[y * width + x] = true;
            }

        // Snapping a point onto the outline must not fold or flatten any of the cells around it.
        bool reverted = true;
        while (reverted)
        {
            reverted = false;
            for (int y = 0; y + 1 < height; y++)
                for (int x = 0; x + 1 < width; x++)
                {
                    int i00 = y * width + x, i10 = i00 + 1, i11 = i00 + width + 1, i01 = i00 + width;
                    if (ConvexCell(grid[i00], grid[i10], grid[i11], grid[i01])) continue;
                    foreach (int i in new[] { i00, i10, i11, i01 })
                    {
                        if (!snapped[i]) continue;
                        grid[i] = new IntPoint(columns[i % width], rows[i / width]);
                        snapped[i] = false;
                        reverted = true;
                    }
                }
        }
        return grid;
    }

    static bool ConvexCell(IntPoint a, IntPoint b, IntPoint c, IntPoint d) =>
        Cross(a, b, c) > 0d && Cross(b, c, d) > 0d && Cross(c, d, a) > 0d && Cross(d, a, b) > 0d;

    static void CleanContours(PolyNode node)
    {
        if (node.Contour.Count > 0)
        {
            var clean = Clipper.CleanPolygon(node.Contour, 4d);
            node.Contour.Clear();
            node.Contour.AddRange(clean);
        }
        // A sliver hole collapses to nothing after cleaning; its node would otherwise break the hole split.
        node.Childs.RemoveAll(child => child.Contour.Count < 3 && child.ChildCount == 0);
        foreach (var child in node.Childs) CleanContours(child);
    }

    // Fragments meeting along a CSG seam carry split vertices that disagree by float error, and a plane that
    // cut only one side leaves a T-junction. Welds vertices within the tolerance onto each other and inserts
    // every vertex into the other fragments' edges it lies on, so seam edges coincide exactly and the union
    // merges across them instead of leaving touching polygons.
    static void WeldSeams(List<List<IntPoint>> paths, long tolerance)
    {
        long toleranceSquared = tolerance * tolerance;
        using var pointLease = ListPool<IntPoint>.Get(out var points);
        foreach (var path in paths) points.AddRange(path);
        for (int i = 1; i < points.Count; i++)
            for (int j = 0; j < i; j++)
                if (DistanceSquared(points[i], points[j]) <= toleranceSquared) { points[i] = points[j]; break; }
        int next = 0;
        foreach (var path in paths)
            for (int k = 0; k < path.Count; k++) path[k] = points[next++];

        using var insertLease = ListPool<(double t, IntPoint p)>.Get(out var inserts);
        for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            var path = paths[pathIndex];
            var welded = new List<IntPoint>(path.Count + 4);
            for (int k = 0; k < path.Count; k++)
            {
                IntPoint a = path[k], b = path[(k + 1) % path.Count];
                welded.Add(a);
                inserts.Clear();
                foreach (var p in points)
                {
                    if (p.Equals(a) || p.Equals(b)) continue;
                    double t = ProjectOnSegment(p, a, b, out double distanceSquared);
                    if (t > 0d && t < 1d && distanceSquared <= toleranceSquared) inserts.Add((t, p));
                }
                inserts.Sort((x, y) => x.t.CompareTo(y.t));
                foreach (var insert in inserts)
                    if (!welded[welded.Count - 1].Equals(insert.p)) welded.Add(insert.p);
            }
            paths[pathIndex] = welded;
        }
    }

    /// <summary>Parameter of <paramref name="p"/>'s projection on segment ab (0 at a, 1 at b) and its squared distance from the segment.</summary>
    static double ProjectOnSegment(IntPoint p, IntPoint a, IntPoint b, out double distanceSquared)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y, apx = p.X - a.X, apy = p.Y - a.Y;
        double lengthSquared = abx * abx + aby * aby;
        if (lengthSquared == 0d) { distanceSquared = apx * apx + apy * apy; return 0d; }
        double t = (apx * abx + apy * aby) / lengthSquared;
        double cross = apx * aby - apy * abx;
        distanceSquared = cross * cross / lengthSquared;
        return t;
    }

    // Drops hairline holes (filling them) and sliver faces whose mean width is below k_MinWidth.
    static void RemoveSlivers(PolyNode node)
    {
        node.Childs.RemoveAll(child => MeanWidth(child.Contour) < k_MinWidth);
        foreach (var child in node.Childs) RemoveSlivers(child);
    }

    static double MeanWidth(List<IntPoint> contour)
    {
        double perimeter = 0d;
        for (int i = 0; i < contour.Count; i++)
        {
            var a = contour[i]; var b = contour[(i + 1) % contour.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            perimeter += Math.Sqrt(dx * dx + dy * dy);
        }
        return perimeter > 0d ? 2d * Math.Abs(Clipper.Area(contour)) / perimeter : 0d;
    }

    static double TreeArea(PolyNode node)
    {
        double area = node.Contour.Count == 0 ? 0d : Math.Abs(Clipper.Area(node.Contour)) * (node.IsHole ? -1d : 1d);
        foreach (var child in node.Childs) area += TreeArea(child);
        return area;
    }

    static List<List<IntPoint>> AllContours(PolyNode node)
    {
        var contours = new List<List<IntPoint>>(8);
        var pending = new Stack<PolyNode>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Contour.Count > 0) contours.Add(current.Contour);
            foreach (var child in current.Childs) pending.Push(child);
        }
        return contours;
    }

    static bool MeshFaces(PolyNode parent, IntPoint[] grid, int width, int height,
        Dictionary<IntPoint, int> ids, List<IntPoint> vertices, List<int> triangles)
    {
        var cell = new List<IntPoint>(4) { default, default, default, default };
        foreach (var face in parent.Childs)
        {
            if (!face.IsHole)
            {
                var paths = RegionPaths(face);
                var bounds = Clipper.GetBounds(paths);
                for (int y = 0; y + 1 < height; y++)
                    for (int x = 0; x + 1 < width; x++)
                    {
                        int i00 = y * width + x;
                        cell[0] = grid[i00]; cell[1] = grid[i00 + 1]; cell[2] = grid[i00 + width + 1]; cell[3] = grid[i00 + width];
                        if (Math.Max(Math.Max(cell[0].X, cell[1].X), Math.Max(cell[2].X, cell[3].X)) < bounds.left
                            || Math.Min(Math.Min(cell[0].X, cell[1].X), Math.Min(cell[2].X, cell[3].X)) > bounds.right
                            || Math.Max(Math.Max(cell[0].Y, cell[1].Y), Math.Max(cell[2].Y, cell[3].Y)) < bounds.top
                            || Math.Min(Math.Min(cell[0].Y, cell[1].Y), Math.Min(cell[2].Y, cell[3].Y)) > bounds.bottom) continue;
                        if (!Intersect(paths, cell, out var piece)
                            || !TriangulateRegions(piece, ids, vertices, triangles, 0)) return false;
                    }
            }
            if (!MeshFaces(face, grid, width, height, ids, vertices, triangles)) return false;
        }
        return true;
    }

    static List<List<IntPoint>> RegionPaths(PolyNode face)
    {
        var paths = new List<List<IntPoint>>(face.ChildCount + 1) { face.Contour };
        foreach (var hole in face.Childs) paths.Add(hole.Contour);
        return paths;
    }

    static bool Intersect(List<List<IntPoint>> paths, long x0, long y0, long x1, long y1, out PolyTree result) =>
        Intersect(paths, new List<IntPoint>(4) { new IntPoint(x0, y0), new IntPoint(x1, y0),
            new IntPoint(x1, y1), new IntPoint(x0, y1) }, out result);

    static bool Intersect(List<List<IntPoint>> paths, List<IntPoint> clip, out PolyTree result)
    {
        var clipper = new Clipper { StrictlySimple = true };
        clipper.AddPaths(paths, PolyTypes.ptSubject, true);
        clipper.AddPath(clip, PolyTypes.ptClip, true);
        result = new PolyTree();
        return clipper.Execute(ClipTypes.ctIntersection, result, PolyFillTypes.pftNonZero);
    }

    static bool TriangulateRegions(PolyNode parent, Dictionary<IntPoint, int> ids,
        List<IntPoint> vertices, List<int> triangles, int depth)
    {
        // Splitting through a hole makes simple contours without introducing a fictitious filled bridge.
        if (depth > 128) return false;
        foreach (var face in parent.Childs)
        {
            if (face.IsHole) continue;
            if (face.ChildCount == 0)
            {
                if (!TriangulateContour(face.Contour, ids, vertices, triangles)) return false;
                continue;
            }
            var paths = RegionPaths(face);
            var bounds = Clipper.GetBounds(paths);
            var holeBounds = Clipper.GetBounds(new List<List<IntPoint>>(1) { face.Childs[0].Contour });
            long split = holeBounds.left + (holeBounds.right - holeBounds.left) / 2;
            if (split <= bounds.left || split >= bounds.right) return false;
            if (!Intersect(paths, bounds.left, bounds.top, split, bounds.bottom, out var left)
                || !Intersect(paths, split, bounds.top, bounds.right, bounds.bottom, out var right)
                || !TriangulateRegions(left, ids, vertices, triangles, depth + 1)
                || !TriangulateRegions(right, ids, vertices, triangles, depth + 1)) return false;
            foreach (var hole in face.Childs)
                if (!TriangulateRegions(hole, ids, vertices, triangles, depth + 1)) return false;
        }
        return true;
    }

    static bool TriangulateContour(List<IntPoint> contour, Dictionary<IntPoint, int> ids,
        List<IntPoint> vertices, List<int> triangles)
    {
        using var lease = ListPool<IntPoint>.Get(out var polygon);
        polygon.AddRange(Clipper.CleanPolygon(contour, 4d));
        if (Clipper.Area(polygon) < 0d) polygon.Reverse();
        for (int i = polygon.Count - 1; i >= 0 && polygon.Count >= 3; i--)
        {
            if (Cross(polygon[(i + polygon.Count - 1) % polygon.Count], polygon[i], polygon[(i + 1) % polygon.Count]) != 0d) continue;
            polygon.RemoveAt(i);
        }
        while (polygon.Count > 3)
        {
            int ear = -1;
            double best = -1d;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[(i + polygon.Count - 1) % polygon.Count];
                var b = polygon[i];
                var c = polygon[(i + 1) % polygon.Count];
                double cross = Cross(a, b, c);
                if (cross <= 0d) continue;
                bool blocked = false;
                for (int j = 0; j < polygon.Count; j++)
                {
                    var p = polygon[j];
                    if (p == a || p == b || p == c) continue;
                    if (Cross(a, b, p) >= 0d && Cross(b, c, p) >= 0d && Cross(c, a, p) >= 0d)
                    { blocked = true; break; }
                }
                if (blocked) continue;
                double quality = cross / (DistanceSquared(a, b) + DistanceSquared(b, c) + DistanceSquared(c, a));
                if (quality <= best) continue;
                ear = i;
                best = quality;
            }
            if (ear < 0) return false;
            AddTriangle(polygon[(ear + polygon.Count - 1) % polygon.Count], polygon[ear], polygon[(ear + 1) % polygon.Count], ids, vertices, triangles);
            polygon.RemoveAt(ear);
        }
        var final = Clipper.CleanPolygon(polygon, 4d);
        if (final.Count == 3 && Cross(final[0], final[1], final[2]) > 0d)
            AddTriangle(final[0], final[1], final[2], ids, vertices, triangles);
        return true;
    }

    static void AddTriangle(IntPoint a, IntPoint b, IntPoint c, Dictionary<IntPoint, int> ids,
        List<IntPoint> vertices, List<int> triangles)
    {
        triangles.Add(Vertex(a, ids, vertices));
        triangles.Add(Vertex(b, ids, vertices));
        triangles.Add(Vertex(c, ids, vertices));
    }

    static int Vertex(IntPoint p, Dictionary<IntPoint, int> ids, List<IntPoint> vertices)
    {
        if (ids.TryGetValue(p, out int index)) return index;
        index = vertices.Count;
        ids.Add(p, index);
        vertices.Add(p);
        return index;
    }

    static double Cross(IntPoint a, IntPoint b, IntPoint c) =>
        ((double)b.X - a.X) * (c.Y - a.Y) - ((double)b.Y - a.Y) * (c.X - a.X);

    static double DistanceSquared(IntPoint a, IntPoint b)
    {
        double x = (double)b.X - a.X, y = (double)b.Y - a.Y;
        return x * x + y * y;
    }
}
#endif
