#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Wraps the greybox being placed (Ctrl+G) around the renderers under a set of transforms, with as
/// little slack as the shape allows. The 6 faces stay planar but are free to tilt off the box axes,
/// so a slanted or tapered subject gets a tighter shell than an oriented bounding box can. Every
/// vertex of the subject stays inside the result - the fit never cuts into the geometry.
/// </summary>
static class GreyboxSelectionFit
{
    // Fraction of a face's axis extent that counts as "near the face" when fitting that face's plane.
    const float k_TiltBandFraction = 0.25f;
    // A tilted plane has to cut this fraction of the axis-aligned plane's slack to be worth taking.
    const float k_TiltGain = 0.98f;
    // Cosine of the widest tilt accepted (45 degrees). Beyond that the fit is following noise.
    const float k_MinTiltCos = 0.7071f;
    // Below this many points near a face there isn't enough of a surface to fit a plane to.
    const int k_MinTiltPoints = 16;
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

    /// <summary>
    /// Box orientation, picked the same way QuickTransform picks its multi-selection OBB: world axes
    /// are the baseline and a target's own rotation has to beat them by 1%, so an unrotated subject
    /// keeps a predictable world-aligned box.
    /// </summary>
    static Quaternion ChooseOrientation(Transform[] targets, List<Vector3> cloud)
    {
        Quaternion best = Quaternion.identity;
        float bestVolume = ProjectedVolume(Quaternion.identity, cloud) * 0.99f;

        foreach (var target in targets)
        {
            if (target == null)
                continue;

            Quaternion q = target.rotation;
            // Skip denormalized quaternions (can happen mid-undo).
            float sqrLen = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (sqrLen < 0.1f || sqrLen > 2f)
                continue;
            q = Quaternion.Normalize(q);

            float volume = ProjectedVolume(q, cloud);
            if (volume < bestVolume)
            {
                best = q;
                bestVolume = volume;
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
    /// tilted toward the geometry it faces wherever that leaves less slack.
    /// </summary>
    static void BuildFacePlanes(List<Vector3> framePoints)
    {
        FrameBounds(framePoints, out Vector3 min, out Vector3 max);
        s_frameDiagonal = (max - min).magnitude;

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
                TryTiltPlane(framePoints, axis, sign, extent, ref normal, ref offset);

            s_faceNormals[face] = normal;
            s_faceOffsets[face] = offset;
        }
    }

    /// <summary>
    /// Replace a face's axis-aligned plane with one fitted to the points nearest it, pushed back out
    /// until everything is inside again. Kept only when it is a tighter fit than the axis-aligned
    /// plane and the tilt stays within <see cref="k_MinTiltCos"/>.
    /// </summary>
    static void TryTiltPlane(List<Vector3> framePoints, int axis, float sign, float extent,
        ref Vector3 normal, ref float offset)
    {
        int b = (axis + 1) % 3;
        int c = (axis + 2) % 3;
        float nearThreshold = offset - extent * k_TiltBandFraction;

        int count = 0;
        double sumA = 0d, sumB = 0d, sumC = 0d;
        for (int i = 0; i < framePoints.Count; i++)
        {
            Vector3 p = framePoints[i];
            if (sign * p[axis] < nearThreshold)
                continue;
            count++;
            sumA += p[axis];
            sumB += p[b];
            sumC += p[c];
        }

        if (count < k_MinTiltPoints)
            return;

        float meanA = (float)(sumA / count);
        float meanB = (float)(sumB / count);
        float meanC = (float)(sumC / count);

        // Least squares over the near points: a = meanA + slopeB*(b - meanB) + slopeC*(c - meanC).
        double sBB = 0d, sBC = 0d, sCC = 0d, sAB = 0d, sAC = 0d;
        for (int i = 0; i < framePoints.Count; i++)
        {
            Vector3 p = framePoints[i];
            if (sign * p[axis] < nearThreshold)
                continue;
            double dA = p[axis] - meanA;
            double dB = p[b] - meanB;
            double dC = p[c] - meanC;
            sBB += dB * dB;
            sBC += dB * dC;
            sCC += dC * dC;
            sAB += dA * dB;
            sAC += dA * dC;
        }

        double det = sBB * sCC - sBC * sBC;
        // A near set spread along a single line (or bunched at a point) pins no plane.
        if (System.Math.Abs(det) < 1e-10d)
            return;

        float slopeB = (float)((sAB * sCC - sAC * sBC) / det);
        float slopeC = (float)((sAC * sBB - sAB * sBC) / det);

        Vector3 tilted = Vector3.zero;
        tilted[axis] = 1f;
        tilted[b]    = -slopeB;
        tilted[c]    = -slopeC;

        float length = tilted.magnitude;
        if (1f / length < k_MinTiltCos)
            return;

        tilted = tilted / length * sign;

        // Push the plane out until the whole subject is behind it again, then compare how much room
        // it leaves over the face's own points - the tilt only earns its place by leaving less.
        float tiltedOffset = float.MinValue;
        for (int i = 0; i < framePoints.Count; i++)
            tiltedOffset = Mathf.Max(tiltedOffset, Vector3.Dot(framePoints[i], tilted));

        float alignedSlack = 0f;
        float tiltedSlack  = 0f;
        for (int i = 0; i < framePoints.Count; i++)
        {
            Vector3 p = framePoints[i];
            if (sign * p[axis] < nearThreshold)
                continue;
            alignedSlack += offset - sign * p[axis];
            tiltedSlack  += tiltedOffset - Vector3.Dot(p, tilted);
        }

        if (tiltedSlack >= alignedSlack * k_TiltGain)
            return;

        normal = tilted;
        offset = tiltedOffset;
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
