using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure port of <c>ProjectionPlacer</c>'s BFS sampling + relax pipeline, decoupled from
/// the paintshop cage so it can drive a <see cref="Sticker"/> grid in the editor.
///
/// Given a flat rectangular grid in the anchor's tangent plane, it conforms each cell
/// to the underlying surface via:
///   1. BFS from the center: predict cell from already-processed neighbors, raycast,
///      requeue on miss; on second miss, fall back to the prediction.
///   2. Relax iterations: Laplacian smooth + edge-length rigidity correction +
///      reproject every point onto the surface.
///
/// The raycast itself is supplied as a delegate so this solver has no editor or
/// physics dependency — the caller plugs in their own raycaster.
/// </summary>
public static class StickerGridSolver
{
    public struct Input
    {
        public float   Width;
        public float   Height;
        public int     Cols;
        public int     Rows;
        public Vector3 AnchorPos;
        public Vector3 AnchorNormal;
        public Vector3 AnchorUp;
        public float   RayDistAbove;
        public float   RayDistBelow;
        public float   RetryBendAngle;
        public int     RelaxIterations;
        public float   RelaxStrength;
        public float   RelaxRigidity;
    }

    public struct RaycastHit3
    {
        public bool    Hit;
        public Vector3 Point;
        public Vector3 Normal;
    }

    /// <summary>Closest-hit raycast against whatever the caller wants to project onto.</summary>
    public delegate RaycastHit3 RaycastFn(Ray ray, float maxDist);

    /// <summary>
    /// Accumulator for BFS debug-draw commands. Pass an instance into <see cref="Solve"/>
    /// to capture per-cell diagnostics (anchor point, hit/miss-coloured points + normals,
    /// the actual cast rays in dim colour, and neighbor connection lines) — same visual
    /// vocabulary as ProjectionPlacer. The caller is responsible for replaying the list
    /// from <c>OnDrawGizmos</c>.
    /// </summary>
    public sealed class Viz
    {
        public enum CmdKind : byte { Sphere, Ray, Line }
        public struct Cmd
        {
            public CmdKind Kind;
            public Vector3 A;       // center (Sphere) / origin (Ray) / start (Line)
            public Vector3 B;       // direction (Ray) / end (Line) — unused for Sphere
            public Color   Color;
            public float   Radius;  // Sphere only
        }
        public readonly List<Cmd> Commands = new();
        public void Clear() => Commands.Clear();
        public void Sphere(Vector3 c, Color col, float r)            => Commands.Add(new Cmd { Kind = CmdKind.Sphere, A = c, Color = col, Radius = r });
        public void Ray   (Vector3 origin, Vector3 dir, Color col)   => Commands.Add(new Cmd { Kind = CmdKind.Ray,    A = origin, B = dir, Color = col });
        public void Line  (Vector3 a, Vector3 b, Color col)          => Commands.Add(new Cmd { Kind = CmdKind.Line,   A = a, B = b, Color = col });
    }

    static Color MulRGB(Color c, float s) => new Color(c.r * s, c.g * s, c.b * s, c.a);

    /// <summary>Builds an orientation matching the legacy ProjectionPlacer convention.</summary>
    public static Quaternion SurfaceRotation(Vector3 normal, Vector3 upRef)
    {
        var surfaceUp = Vector3.ProjectOnPlane(upRef, normal);
        if (surfaceUp.sqrMagnitude < 0.001f)
            surfaceUp = Vector3.ProjectOnPlane(Vector3.forward, normal);
        return Quaternion.LookRotation(normal, surfaceUp.normalized);
    }

    public static void Solve(in Input input, RaycastFn raycast,
                             out Vector3[] points, out Vector3[] normals,
                             Viz viz = null)
    {
        int cols  = Mathf.Max(2, input.Cols);
        int rows  = Mathf.Max(2, input.Rows);
        int total = cols * rows;

        float stepX   = input.Width  / (cols - 1);
        float stepY   = input.Height / (rows - 1);
        float rayDist = input.RayDistAbove + input.RayDistBelow;

        points  = new Vector3[total];
        normals = new Vector3[total];

        viz?.Clear();

        // No surface to project onto — leave a flat grid in the tangent plane.
        if (raycast == null)
        {
            BuildFlatGrid(in input, cols, rows, points, normals);
            return;
        }

        var processed  = new bool[total];
        var inFrontier = new bool[total];
        var frontier   = new Queue<int>(total);
        var secondPass = new Queue<int>(total);

        BfsSampleGrid(in input, cols, rows, stepX, stepY, rayDist, raycast,
                      points, normals, processed, inFrontier, frontier, secondPass, viz);
        RelaxGrid    (in input, cols, rows, stepX, stepY, rayDist, raycast,
                      points, normals);
    }

    // ── Flat grid ────────────────────────────────────────────────────────

    static void BuildFlatGrid(in Input input, int cols, int rows,
                              Vector3[] points, Vector3[] normals)
    {
        var rot   = SurfaceRotation(input.AnchorNormal, input.AnchorUp);
        var right = rot * Vector3.right;
        var up    = rot * Vector3.up;
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            float u = c / (float)(cols - 1) - 0.5f;
            float v = r / (float)(rows - 1) - 0.5f;
            int i = r * cols + c;
            points [i] = input.AnchorPos + right * (u * input.Width) + up * (v * input.Height);
            normals[i] = input.AnchorNormal;
        }
    }

    // ── BFS sampling ─────────────────────────────────────────────────────

    static void BfsSampleGrid(in Input input, int cols, int rows,
                              float stepX, float stepY, float rayDist, RaycastFn raycast,
                              Vector3[] points, Vector3[] normals,
                              bool[] processed, bool[] inFrontier,
                              Queue<int> frontier, Queue<int> secondPass,
                              Viz viz)
    {
        var anchor  = input.AnchorPos;
        var forward = input.AnchorNormal;
        var origin  = anchor + forward * input.RayDistAbove;

        int centerCol = (cols - 1) / 2;
        int centerRow = (rows - 1) / 2;
        int centerIdx = centerRow * cols + centerCol;

        float seedOffsetX = ((cols - 1) * 0.5f - centerCol) * stepX;
        float seedOffsetY = ((rows - 1) * 0.5f - centerRow) * stepY;
        var   seedOrigin  = origin + (SurfaceRotation(forward, input.AnchorUp) * Vector3.right) * seedOffsetX
                                   + (SurfaceRotation(forward, input.AnchorUp) * Vector3.up)    * seedOffsetY;

        if (viz != null)
        {
            viz.Sphere(anchor, Color.yellow, 0.02f);
            viz.Ray(anchor, 0.1f * forward, Color.yellow);
        }

        frontier.Enqueue(centerIdx);
        inFrontier[centerIdx] = true;

        while (frontier.Count > 0 || secondPass.Count > 0)
        {
            bool isFirstPass = frontier.Count > 0;
            int idx = frontier.Count > 0 ? frontier.Dequeue() : secondPass.Dequeue();
            int col = idx % cols;
            int row = idx / cols;

            PredictFromNeighbors(col, row, cols, rows, stepX, stepY, forward, input.AnchorUp,
                                 seedOrigin, input.RayDistAbove, points, normals, processed,
                                 out var predPos, out var predNormal);

            // Center cell uses the anchor normal — neighbor estimation may skew it on curvy surfaces.
            if (Mathf.Abs((cols - 1f) / 2 - col) <= 0.5f && Mathf.Abs((rows - 1f) / 2 - row) <= 0.5f)
                predNormal = forward;

            predNormal.Normalize();

            var rayOrigin = predPos + predNormal * rayDist / 2f;
            var ray = new Ray(rayOrigin, -predNormal);
            var hit = raycast(ray, rayDist);

            if (!hit.Hit && input.RetryBendAngle > 0f)
            {
                var toCenter = (anchor - rayOrigin).normalized;
                var bentDir  = Vector3.RotateTowards(-predNormal, toCenter,
                                                     input.RetryBendAngle * Mathf.Deg2Rad, 0f);
                ray = new Ray(rayOrigin, bentDir);
                hit = raycast(ray, rayDist);
            }

            Vector3 targetPos, targetNormal;
            if (hit.Hit)
            {
                targetPos    = hit.Point;
                targetNormal = hit.Normal;
            }
            else
            {
                if (isFirstPass)
                {
                    secondPass.Enqueue(idx);
                    continue;
                }
                targetPos    = predPos;
                targetNormal = predNormal;
            }

            points [idx] = targetPos;
            normals[idx] = targetNormal;
            processed[idx] = true;

            if (viz != null)
            {
                Color debugColor = hit.Hit ? (isFirstPass ? Color.green : Color.cyan) : Color.red;
                if (idx == centerIdx) debugColor = Color.yellow;
                viz.Sphere(targetPos, debugColor, 0.01f);
                viz.Ray   (targetPos, 0.1f * targetNormal, debugColor);
                viz.Ray   (ray.origin, ray.direction, MulRGB(debugColor, 0.3f));
            }

            for (int d = 0; d < 4; d++)
            {
                int dc = d == 0 ? -1 : d == 1 ? 1 : 0;
                int dr = d == 2 ? -1 : d == 3 ? 1 : 0;
                int nc = col + dc, nr = row + dr;
                if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
                int ni = nr * cols + nc;

                if (viz != null && processed[ni])
                    viz.Line(targetPos, points[ni], Color.gray);

                if (inFrontier[ni]) continue;
                frontier.Enqueue(ni);
                inFrontier[ni] = true;
            }
        }
    }

    static void PredictFromNeighbors(int col, int row, int cols, int rows,
                                     float stepX, float stepY, Vector3 forward, Vector3 upRef,
                                     Vector3 seedOrigin, float depthAbove,
                                     Vector3[] points, Vector3[] normals, bool[] processed,
                                     out Vector3 predPos, out Vector3 predNormal)
    {
        predPos    = Vector3.zero;
        predNormal = Vector3.zero;
        int nCount = 0;

        for (int dc = -1; dc <= 1; dc++)
        for (int dr = -1; dr <= 1; dr++)
        {
            if (dc == 0 && dr == 0) continue;
            int nc = col + dc, nr = row + dr;
            if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
            int ni = nr * cols + nc;
            if (!processed[ni]) continue;

            var nNorm  = normals[ni].normalized;
            var nRot   = SurfaceRotation(nNorm, upRef);
            var nRight = nRot * Vector3.right;
            var nUp    = nRot * Vector3.up;
            var nPos   = points[ni] + nRight * (dc * stepX) + nUp * (dr * stepY);
            predPos    += nPos;
            predNormal += nNorm;
            nCount++;
        }

        if (nCount == 0)
        {
            // seedOrigin sits the above-surface margin above the anchor — drop back to the anchor plane.
            predPos    = seedOrigin - depthAbove * forward;
            predNormal = forward;
        }
        else
        {
            predPos    /= nCount;
            predNormal  = predNormal.normalized;
        }
    }

    // ── Relax ────────────────────────────────────────────────────────────

    static Vector3[] s_relaxPts;
    static Vector3[] s_relaxNrm;

    static void RelaxGrid(in Input input, int cols, int rows,
                          float stepX, float stepY, float rayDist, RaycastFn raycast,
                          Vector3[] points, Vector3[] normals)
    {
        if (input.RelaxIterations <= 0) return;

        int total = cols * rows;
        if (s_relaxPts == null || s_relaxPts.Length < total)
        {
            s_relaxPts = new Vector3[total];
            s_relaxNrm = new Vector3[total];
        }
        var relaxPts = s_relaxPts;
        var relaxNrm = s_relaxNrm;

        for (int iter = 0; iter < input.RelaxIterations; iter++)
        {
            int centerIdx = ((rows - 1) / 2) * cols + (cols - 1) / 2;
            var preSmoothCenter = points[centerIdx];

            for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                int i  = row * cols + col;
                int iL = row * cols + (col - 1);
                int iR = row * cols + (col + 1);
                int iD = (row - 1) * cols + col;
                int iU = (row + 1) * cols + col;

                var p = points[i];
                var n = normals[i];

                // Mirror ghost neighbors across the boundary so edge cells smooth correctly.
                Vector3 pL, pR, pD, pU, nL, nR, nD, nU;
                if (col > 0)        { pL = points[iL]; nL = normals[iL]; } else { pL = 2f * p - points[iR]; nL = Vector3.Reflect(normals[iR], p - points[iR]); }
                if (col < cols - 1) { pR = points[iR]; nR = normals[iR]; } else { pR = 2f * p - points[iL]; nR = Vector3.Reflect(normals[iL], p - points[iL]); }
                if (row > 0)        { pD = points[iD]; nD = normals[iD]; } else { pD = 2f * p - points[iU]; nD = Vector3.Reflect(normals[iU], p - points[iU]); }
                if (row < rows - 1) { pU = points[iU]; nU = normals[iU]; } else { pU = 2f * p - points[iD]; nU = Vector3.Reflect(normals[iD], p - points[iD]); }

                relaxPts[i] = Vector3.Lerp(p, (pL + pR + pD + pU) * 0.25f, input.RelaxStrength);
                relaxNrm[i] = Vector3.Slerp(n, (nL + nR + nD + nU).normalized, input.RelaxStrength);
            }

            System.Array.Copy(relaxPts, points,  total);
            System.Array.Copy(relaxNrm, normals, total);

            // Edge-length + diagonal rigidity correction — restores quad shape after the smooth.
            float stepDiag = Mathf.Sqrt(stepX * stepX + stepY * stepY);
            float rigidity = input.RelaxRigidity;
            for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                int i   = row * cols + col;
                int iL  = row * cols + (col - 1);
                int iD  = (row - 1) * cols + col;
                int iDL = (row - 1) * cols + (col - 1);
                int iDR = (row - 1) * cols + (col + 1);
                var p   = relaxPts[i];

                if (col > 0)
                {
                    var delta = p - relaxPts[iL];
                    var off   = 0.5f * rigidity * (stepX - delta.magnitude) * delta.normalized;
                    points[i]  += off;
                    points[iL] -= off;
                }
                if (row > 0)
                {
                    var delta = p - relaxPts[iD];
                    var off   = 0.5f * rigidity * (stepY - delta.magnitude) * delta.normalized;
                    points[i]  += off;
                    points[iD] -= off;
                }
                if (col > 0 && row > 0)
                {
                    var delta = p - relaxPts[iDL];
                    var off   = 0.5f * rigidity * (stepDiag - delta.magnitude) * delta.normalized;
                    points[i]   += off;
                    points[iDL] -= off;
                }
                if (col < cols - 1 && row > 0)
                {
                    var delta = p - relaxPts[iDR];
                    var off   = 0.5f * rigidity * (stepDiag - delta.magnitude) * delta.normalized;
                    points[i]   += off;
                    points[iDR] -= off;
                }
            }

            // Re-anchor: subtract net center drift so the grid stays centered on the anchor.
            var centerDrift = points[centerIdx] - preSmoothCenter;
            for (int i = 0; i < total; i++)
                points[i] -= centerDrift;
        }

        // Final reproject — snap every relaxed point back onto the surface.
        for (int i = 0; i < total; i++)
        {
            var relaxRay = new Ray(points[i] + normals[i] * rayDist, -normals[i]);
            var hit = raycast(relaxRay, rayDist + rayDist);
            if (hit.Hit)
            {
                points[i]  = hit.Point;
                normals[i] = hit.Normal;
            }
        }
    }
}
