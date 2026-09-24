#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Wraps the greybox being placed (Ctrl+G) around the renderers under a set of transforms, with as
/// little slack as the shape allows: the box frame is searched for minimum bounding volume, then the
/// 6 faces, still planar, are free to tilt off the box axes so a slanted or tapered subject gets a
/// tighter shell than an oriented bounding box can. Every vertex of the subject stays inside the
/// result - the fit never cuts into the geometry.
/// </summary>
static class GreyboxSelectionFit
{
    // A tilted plane has to cut this fraction of the axis-aligned plane's slack to be worth taking.
    const float k_TiltGain = 0.98f;
    // Cosine of the widest tilt accepted off the box axis (60 degrees). Beyond that the corner solve
    // gets unstable and the face is really a different face.
    const float k_MinTiltCos = 0.5f;
    // Thickness given to an axis the subject is flat on, so the box never collapses to a plane.
    const float k_MinExtent = 0.01f;

    // Corner index bits match Greybox: bit0 = +X, bit1 = +Y, bit2 = +Z.
    // Face indices match Greybox too: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z.
    static readonly Vector3[] s_faceNormals  = new Vector3[6];
    static readonly float[]   s_faceOffsets  = new float[6];
    static readonly Vector3[] s_framePoints  = new Vector3[8];
    static readonly Vector3[] s_worldCorners = new Vector3[8];
    // Diagonal of the axis-aligned start box, the scale the corner validity check is measured against.
    static float s_frameDiagonal;
    // Frame-space extreme point per reduction direction - the hull sample the face planes are fitted to.
    static readonly Vector3[] s_extremePoints  = new Vector3[k_ReductionDirections];
    static readonly int[]     s_extremeIndices = new int[k_ReductionDirections];

    /// <summary>
    /// Fit <paramref name="box"/> to the renderers under <paramref name="targets"/> (children
    /// included) and place it there. False when there was nothing to fit - the box is left alone.
    /// </summary>
    internal static bool FitToTargets(Greybox box, Transform[] targets)
    {
        if (box == null)
            return false;

        if (targets == null || targets.Length == 0)
        {
            Debug.LogWarning("[Greybox] Nothing to fit - no objects were selected when the box was created.");
            return false;
        }

        using var cloudScope = ListPool<Vector3>.Get(out var cloud);
        GatherWorldVertices(targets, box.transform, cloud);
        if (cloud.Count == 0)
        {
            Debug.LogWarning("[Greybox] Nothing to fit - the selection has no active mesh renderers.");
            return false;
        }

        Quaternion rotation = ChooseOrientation(targets, cloud);
        Vector3 axisX = rotation * Vector3.right;
        Vector3 axisY = rotation * Vector3.up;
        Vector3 axisZ = rotation * Vector3.forward;

        // Everything below works in the box frame: a point becomes its distances along the frame
        // axes, so a face is just a normal plus one offset and the plane maths stays 3-component.
        using var frameScope = ListPool<Vector3>.Get(out var framePoints);
        for (int i = 0; i < cloud.Count; i++)
        {
            Vector3 v = cloud[i];
            framePoints.Add(new Vector3(Vector3.Dot(v, axisX), Vector3.Dot(v, axisY), Vector3.Dot(v, axisZ)));
        }

        BuildFacePlanes(framePoints);
        if (!TrySolveCorners(s_framePoints))
            BuildAxisAlignedCorners(framePoints, s_framePoints);

        for (int i = 0; i < 8; i++)
        {
            Vector3 p = s_framePoints[i];
            s_worldCorners[i] = axisX * p.x + axisY * p.y + axisZ * p.z;
        }

        // Greybox's pivot sits at the centre of the bottom face - corners 0,1,4,5 (bit1 clear).
        Vector3 bottomCenter = (s_worldCorners[0] + s_worldCorners[1] + s_worldCorners[4] + s_worldCorners[5]) * 0.25f;

        box.transform.rotation   = rotation;
        box.transform.position   = bottomCenter;
        box.transform.localScale = Vector3.one; // the corners carry the size, so scale stays neutral

        for (int i = 0; i < 8; i++)
            box.Corners[i] = box.transform.InverseTransformPoint(s_worldCorners[i]);

        box.RebuildMesh();
        EditorUtility.SetDirty(box);
        return true;
    }

    /// <summary>Return the box to the shape a fresh Ctrl+G gives it: default corners, default scale.</summary>
    internal static void ResetToCreationShape(Greybox box)
    {
        if (box == null)
            return;

        Vector3[] defaults = Greybox.DefaultCorners();
        for (int i = 0; i < 8; i++)
            box.Corners[i] = defaults[i];

        box.transform.rotation   = Quaternion.identity;
        box.transform.localScale = GreyboxSettings.DefaultScale;

        box.RebuildMesh();
        EditorUtility.SetDirty(box);
    }

    // ─── Vertex cloud ───────────────────────────────────────────

    /// <summary>
    /// World-space vertices of every active renderer under the targets. Real vertices, not renderer
    /// bounds - the tilted planes need the actual surface to fit to.
    /// </summary>
    static void GatherWorldVertices(Transform[] targets, Transform exclude, List<Vector3> cloud)
    {
        using var vertScope = ListPool<Vector3>.Get(out var verts);

        foreach (var target in targets)
        {
            if (target == null)
                continue;

            foreach (var mf in target.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled || !mf.gameObject.activeInHierarchy)
                    continue;
                if (mf.sharedMesh == null || mf.transform.IsChildOf(exclude))
                    continue;
                AppendWorldVertices(mf.transform, mf.sharedMesh, verts, cloud);
            }

            foreach (var smr in target.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null)
                    continue;
                if (smr.transform.IsChildOf(exclude))
                    continue;

                // Bake the posed mesh so the fit wraps what the artist sees, not the bind pose.
                // Baked verts are in the renderer's local space, so the same transform applies.
                Mesh baked = new Mesh();
                smr.BakeMesh(baked);
                AppendWorldVertices(smr.transform, baked, verts, cloud);
                Object.DestroyImmediate(baked);
            }
        }
    }

    static void AppendWorldVertices(Transform tf, Mesh mesh, List<Vector3> verts, List<Vector3> cloud)
    {
        verts.Clear();
        mesh.GetVertices(verts);

        Matrix4x4 localToWorld = tf.localToWorldMatrix;
        for (int i = 0; i < verts.Count; i++)
            cloud.Add(localToWorld.MultiplyPoint3x4(verts[i]));
    }

    // ─── Orientation ────────────────────────────────────────────

    // Directions the cloud is reduced along before the orientation search. Only extreme points can
    // move a bounding box, so a bounded sample of them keeps dense meshes cheap.
    const int k_ReductionDirections = 128;
    // Clouds at or below this size are searched as-is.
    const int k_ReductionThreshold = 256;
    // Hill-climb angular steps, degrees: coarse enough to leave a seed's basin, fine enough to settle.
    const float k_RefineStartStep = 30f;
    const float k_RefineEndStep   = 0.05f;
    // A rotated frame has to beat the world frame by this fraction of volume, so an unrotated
    // subject keeps a predictable world-aligned box.
    const float k_WorldFramePreference = 0.99f;

    static readonly Vector3[] s_reductionDirections = BuildReductionDirections();

    /// <summary>
    /// Box orientation minimising the cloud's bounding volume. Seeds (world axes, principal axes,
    /// each target's rotation) are each hill-climbed to a local minimum and the smallest wins;
    /// world axes are kept unless a rotated frame beats them by <see cref="k_WorldFramePreference"/>.
    /// </summary>
    static Quaternion ChooseOrientation(Transform[] targets, List<Vector3> cloud)
    {
        using var pointsScope = ListPool<Vector3>.Get(out var points);
        ReduceToExtremePoints(cloud, points);

        using var seedScope = ListPool<Quaternion>.Get(out var seeds);
        seeds.Add(Quaternion.identity);
        seeds.Add(PrincipalAxes(points));
        foreach (var target in targets)
        {
            if (target == null)
                continue;

            Quaternion q = target.rotation;
            // Skip denormalized quaternions (can happen mid-undo).
            float sqrLen = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (sqrLen < 0.1f || sqrLen > 2f)
                continue;
            seeds.Add(Quaternion.Normalize(q));
        }

        float worldVolume = ProjectedVolume(Quaternion.identity, points);
        Quaternion best = Quaternion.identity;
        float bestVolume = float.MaxValue;

        foreach (var seed in seeds)
        {
            Quaternion refined = RefineOrientation(seed, points, out float volume);
            if (volume < bestVolume)
            {
                best = refined;
                bestVolume = volume;
            }
        }

        if (bestVolume >= worldVolume * k_WorldFramePreference)
            return Quaternion.identity;

        return CanonicalizeAxes(best);
    }

    /// <summary>
    /// Local search from <paramref name="seed"/>: rotate about each box axis by a shrinking step,
    /// keeping any rotation that lowers the volume, until the step is below <see cref="k_RefineEndStep"/>.
    /// </summary>
    static Quaternion RefineOrientation(Quaternion seed, List<Vector3> points, out float volume)
    {
        Quaternion q = seed;
        volume = ProjectedVolume(q, points);

        float step = k_RefineStartStep;
        while (step >= k_RefineEndStep)
        {
            bool improved = false;
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 boxAxis = q * AxisVector(axis);
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Quaternion candidate = Quaternion.AngleAxis(sign * step, boxAxis) * q;
                    float candidateVolume = ProjectedVolume(candidate, points);
                    if (candidateVolume < volume * (1f - 1e-6f))
                    {
                        q = candidate;
                        volume = candidateVolume;
                        improved = true;
                    }
                }
            }

            if (!improved)
                step *= 0.5f;
        }

        return Quaternion.Normalize(q);
    }

    /// <summary>
    /// Extreme point of the cloud along each of <see cref="s_reductionDirections"/>, deduplicated.
    /// Small clouds are copied whole.
    /// </summary>
    static void ReduceToExtremePoints(List<Vector3> cloud, List<Vector3> points)
    {
        if (cloud.Count <= k_ReductionThreshold)
        {
            points.AddRange(cloud);
            return;
        }

        using var pickedScope = HashSetPool<int>.Get(out var picked);
        foreach (var dir in s_reductionDirections)
        {
            int bestIndex = 0;
            float bestDot = float.MinValue;
            for (int i = 0; i < cloud.Count; i++)
            {
                float d = Vector3.Dot(cloud[i], dir);
                if (d > bestDot)
                {
                    bestDot = d;
                    bestIndex = i;
                }
            }

            if (picked.Add(bestIndex))
                points.Add(cloud[bestIndex]);
        }
    }

    static Vector3[] BuildReductionDirections()
    {
        // Fibonacci sphere: evenly spread, deterministic.
        var dirs = new Vector3[k_ReductionDirections];
        float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int i = 0; i < dirs.Length; i++)
        {
            float y = 1f - (i + 0.5f) * 2f / dirs.Length;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float theta = golden * i;
            dirs[i] = new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r);
        }
        return dirs;
    }

    /// <summary>Frame of the cloud's covariance eigenvectors (Jacobi), right-handed.</summary>
    static Quaternion PrincipalAxes(List<Vector3> points)
    {
        if (points.Count < 2)
            return Quaternion.identity;

        Vector3 mean = Vector3.zero;
        for (int i = 0; i < points.Count; i++)
            mean += points[i];
        mean /= points.Count;

        var a = new double[3, 3];
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 d = points[i] - mean;
            a[0, 0] += d.x * d.x; a[0, 1] += d.x * d.y; a[0, 2] += d.x * d.z;
            a[1, 1] += d.y * d.y; a[1, 2] += d.y * d.z;
            a[2, 2] += d.z * d.z;
        }
        a[1, 0] = a[0, 1]; a[2, 0] = a[0, 2]; a[2, 1] = a[1, 2];

        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            if (off < 1e-20d)
                break;

            for (int p = 0; p < 2; p++)
            for (int q = p + 1; q < 3; q++)
            {
                double apq = a[p, q];
                if (System.Math.Abs(apq) < 1e-30d)
                    continue;

                double theta = (a[q, q] - a[p, p]) / (2d * apq);
                double t = (theta >= 0d ? 1d : -1d) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1d));
                double c = 1d / System.Math.Sqrt(t * t + 1d);
                double s = t * c;

                a[p, p] -= t * apq;
                a[q, q] += t * apq;
                a[p, q] = a[q, p] = 0d;
                for (int r = 0; r < 3; r++)
                {
                    if (r == p || r == q)
                        continue;
                    double arp = a[r, p], arq = a[r, q];
                    a[r, p] = a[p, r] = c * arp - s * arq;
                    a[r, q] = a[q, r] = s * arp + c * arq;
                }
                for (int r = 0; r < 3; r++)
                {
                    double vrp = v[r, p], vrq = v[r, q];
                    v[r, p] = c * vrp - s * vrq;
                    v[r, q] = s * vrp + c * vrq;
                }
            }
        }

        Vector3 x = new Vector3((float)v[0, 0], (float)v[1, 0], (float)v[2, 0]).normalized;
        Vector3 y = new Vector3((float)v[0, 1], (float)v[1, 1], (float)v[2, 1]).normalized;
        Vector3 z = Vector3.Cross(x, y);
        if (z.sqrMagnitude < 1e-6f)
            return Quaternion.identity;
        return Quaternion.LookRotation(z, y);
    }

    /// <summary>
    /// Relabel the box axes (a box has 24 equivalent frames) so its +Y is the axis nearest world up
    /// and its +X the remaining axis nearest world right - the pivot lands on the face the user
    /// would call the bottom.
    /// </summary>
    static Quaternion CanonicalizeAxes(Quaternion q)
    {
        Vector3 up    = ClosestBoxAxis(q, Vector3.up, Vector3.zero);
        Vector3 right = ClosestBoxAxis(q, Vector3.right, up);
        return Quaternion.LookRotation(Vector3.Cross(right, up), up);
    }

    static Vector3 ClosestBoxAxis(Quaternion q, Vector3 reference, Vector3 exclude)
    {
        Vector3 best = Vector3.up;
        float bestDot = float.MinValue;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 dir = q * AxisVector(axis);
            if (Mathf.Abs(Vector3.Dot(dir, exclude)) > 0.5f)
                continue;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float d = Vector3.Dot(dir * sign, reference);
                if (d > bestDot)
                {
                    bestDot = d;
                    best = dir * sign;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Volume of the cloud's bounding box in the given frame, with flat axes given a floor so a flat
    /// subject still compares orientations by its other two extents.
    /// </summary>
    static float ProjectedVolume(Quaternion rotation, List<Vector3> cloud)
    {
        Vector3 axisX = rotation * Vector3.right;
        Vector3 axisY = rotation * Vector3.up;
        Vector3 axisZ = rotation * Vector3.forward;

        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int i = 0; i < cloud.Count; i++)
        {
            Vector3 v = cloud[i];
            Vector3 p = new Vector3(Vector3.Dot(v, axisX), Vector3.Dot(v, axisY), Vector3.Dot(v, axisZ));
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return Mathf.Max(max.x - min.x, k_MinExtent)
             * Mathf.Max(max.y - min.y, k_MinExtent)
             * Mathf.Max(max.z - min.z, k_MinExtent);
    }

    // ─── Face planes ────────────────────────────────────────────

    /// <summary>
    /// One enclosing plane per face: the frame's bounding box to start with, then each face plane
    /// replaced by a supporting plane of the geometry it faces wherever that leaves less slack.
    /// </summary>
    static void BuildFacePlanes(List<Vector3> framePoints)
    {
        FrameBounds(framePoints, out Vector3 min, out Vector3 max);
        s_frameDiagonal = (max - min).magnitude;
        FindExtremePoints(framePoints);

        for (int face = 0; face < 6; face++)
        {
            int axis    = face / 2;
            float sign  = (face % 2 == 0) ? 1f : -1f;
            float extent = max[axis] - min[axis];

            Vector3 normal = AxisVector(axis) * sign;
            float offset   = sign > 0f ? max[axis] : -min[axis];

            // An axis the subject is flat on carries no surface to fit a plane to, and its two faces
            // are only as far apart as the min-extent floor put them - leave both axis-aligned.
            if (extent > k_MinExtent)
                TryTiltPlane(framePoints, ref normal, ref offset);

            s_faceNormals[face] = normal;
            s_faceOffsets[face] = offset;
        }
    }

    /// <summary>Extreme frame point along each of <see cref="s_reductionDirections"/>.</summary>
    static void FindExtremePoints(List<Vector3> framePoints)
    {
        for (int d = 0; d < s_reductionDirections.Length; d++)
        {
            Vector3 dir = s_reductionDirections[d];
            int bestIndex = 0;
            float bestDot = float.MinValue;
            for (int i = 0; i < framePoints.Count; i++)
            {
                float dot = Vector3.Dot(framePoints[i], dir);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestIndex = i;
                }
            }
            s_extremeIndices[d] = bestIndex;
            s_extremePoints[d]  = framePoints[bestIndex];
        }
    }

    /// <summary>
    /// Replace a face's axis-aligned plane with the supporting plane through three of the extreme
    /// points facing it that leaves the least slack over those points, pushed out until every vertex
    /// is inside. Kept only when it beats the axis-aligned plane by <see cref="k_TiltGain"/> and its
    /// tilt stays within <see cref="k_MinTiltCos"/>.
    /// </summary>
    static void TryTiltPlane(List<Vector3> framePoints, ref Vector3 normal, ref float offset)
    {
        Vector3 faceAxis = normal;

        // Points extreme along a direction within the tilt cone are the only ones a plane of this
        // face can rest on. Coincident verts (a Greybox duplicates corners per face) collapse via index.
        using var candidateScope = ListPool<Vector3>.Get(out var candidates);
        using var seenScope      = HashSetPool<int>.Get(out var seen);
        for (int d = 0; d < s_reductionDirections.Length; d++)
        {
            if (Vector3.Dot(s_reductionDirections[d], faceAxis) < k_MinTiltCos)
                continue;
            if (seen.Add(s_extremeIndices[d]))
                candidates.Add(s_extremePoints[d]);
        }
        if (candidates.Count < 3)
            return;

        float degenerateArea = 1e-6f * s_frameDiagonal * s_frameDiagonal;

        Vector3 bestNormal = normal;
        float bestSlack = PlaneSlack(candidates, normal, offset) * k_TiltGain;

        for (int a = 0; a < candidates.Count - 2; a++)
        for (int b = a + 1; b < candidates.Count - 1; b++)
        for (int c = b + 1; c < candidates.Count; c++)
        {
            Vector3 n = Vector3.Cross(candidates[b] - candidates[a], candidates[c] - candidates[a]);
            float area = n.magnitude;
            if (area < degenerateArea)
                continue;
            n /= area;

            float alignment = Vector3.Dot(n, faceAxis);
            if (alignment < 0f)
            {
                n = -n;
                alignment = -alignment;
            }
            if (alignment < k_MinTiltCos)
                continue;

            float candidateOffset = float.MinValue;
            for (int i = 0; i < s_extremePoints.Length; i++)
                candidateOffset = Mathf.Max(candidateOffset, Vector3.Dot(s_extremePoints[i], n));

            float slack = PlaneSlack(candidates, n, candidateOffset);
            if (slack < bestSlack)
            {
                bestSlack  = slack;
                bestNormal = n;
            }
        }

        if (bestNormal == normal)
            return;

        // The extreme points are a hull sample, not the hull - push out over every vertex.
        float finalOffset = float.MinValue;
        for (int i = 0; i < framePoints.Count; i++)
            finalOffset = Mathf.Max(finalOffset, Vector3.Dot(framePoints[i], bestNormal));

        normal = bestNormal;
        offset = finalOffset;
    }

    /// <summary>Total distance the plane sits above the given points.</summary>
    static float PlaneSlack(List<Vector3> points, Vector3 normal, float offset)
    {
        float slack = 0f;
        for (int i = 0; i < points.Count; i++)
            slack += offset - Vector3.Dot(points[i], normal);
        return slack;
    }

    // ─── Corners ────────────────────────────────────────────────

    /// <summary>
    /// Each corner is where its three face planes meet. False when the planes don't form a solid box
    /// - a corner outside one of the opposing planes means the tilts fold the shape inside out.
    /// </summary>
    static bool TrySolveCorners(Vector3[] corners)
    {
        for (int i = 0; i < 8; i++)
        {
            int faceX = (i & 1) != 0 ? 0 : 1;
            int faceY = (i & 2) != 0 ? 2 : 3;
            int faceZ = (i & 4) != 0 ? 4 : 5;

            if (!TryIntersectPlanes(faceX, faceY, faceZ, out corners[i]))
                return false;
        }

        // Scaled off the box itself, plus a term for the float error a subject far from the world
        // origin carries into the plane solve.
        float distance = 0f;
        for (int face = 0; face < 6; face++)
            distance = Mathf.Max(distance, Mathf.Abs(s_faceOffsets[face]));
        float tolerance = Mathf.Max(s_frameDiagonal * 1e-3f, distance * 1e-5f);

        for (int i = 0; i < 8; i++)
        {
            for (int face = 0; face < 6; face++)
            {
                if (Vector3.Dot(corners[i], s_faceNormals[face]) > s_faceOffsets[face] + tolerance)
                    return false;
            }
        }

        return true;
    }

    static bool TryIntersectPlanes(int faceA, int faceB, int faceC, out Vector3 point)
    {
        Vector3 nA = s_faceNormals[faceA], nB = s_faceNormals[faceB], nC = s_faceNormals[faceC];
        Vector3 crossBC = Vector3.Cross(nB, nC);

        float det = Vector3.Dot(nA, crossBC);
        if (Mathf.Abs(det) < 1e-6f)
        {
            point = Vector3.zero;
            return false;
        }

        point = (s_faceOffsets[faceA] * crossBC
               + s_faceOffsets[faceB] * Vector3.Cross(nC, nA)
               + s_faceOffsets[faceC] * Vector3.Cross(nA, nB)) / det;
        return true;
    }

    /// <summary>Fallback shape: the frame's bounding box, right angles and all.</summary>
    static void BuildAxisAlignedCorners(List<Vector3> framePoints, Vector3[] corners)
    {
        FrameBounds(framePoints, out Vector3 min, out Vector3 max);

        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vector3(
                (i & 1) != 0 ? max.x : min.x,
                (i & 2) != 0 ? max.y : min.y,
                (i & 4) != 0 ? max.z : min.z);
        }
    }

    /// <summary>
    /// Bounding box of the frame points, with any axis the subject is flat on opened up to
    /// <see cref="k_MinExtent"/> so the box keeps a solid volume.
    /// </summary>
    static void FrameBounds(List<Vector3> framePoints, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int i = 0; i < framePoints.Count; i++)
        {
            min = Vector3.Min(min, framePoints[i]);
            max = Vector3.Max(max, framePoints[i]);
        }

        for (int axis = 0; axis < 3; axis++)
        {
            float missing = k_MinExtent - (max[axis] - min[axis]);
            if (missing <= 0f)
                continue;
            min[axis] -= missing * 0.5f;
            max[axis] += missing * 0.5f;
        }
    }

    static Vector3 AxisVector(int axis) =>
        axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
}
#endif
