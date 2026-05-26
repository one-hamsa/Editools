#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Edit-time raycaster used by <see cref="Sticker"/> to conform its grid against
/// nearby scene geometry. Built once per Sticker rebuild — caller constructs with
/// a slab AABB, runs many <see cref="Raycast"/> calls, then discards.
///
/// Candidate filter (applied at construction):
///   • <see cref="MeshRenderer"/> enabled, present on the same GameObject as the filter.
///   • GameObject active in hierarchy.
///   • GameObject marked static.
///   • At least one shared material writes Z (<c>_ZWrite > 0</c> if the property exists; assumed true otherwise).
///   • MeshFilter.sharedMesh is non-null and has triangles.
///   • Renderer's world-space AABB intersects the sticker's <see cref="_slabBounds"/>.
/// The Sticker's own GameObject is NOT excluded — the Sticker clears its mesh before
/// the rebuild starts, so its renderer holds zero triangles during raycasting.
///
/// Per-ray, each surviving candidate gets an AABB-ray pre-test before the actual
/// triangle intersection — drops irrelevant candidates fast when the slab is large
/// but a single ray is narrow.
///
/// Triangle intersection uses <c>UnityEditor.HandleUtility.IntersectRayMesh</c>
/// (internal Unity API) reached via reflection.
/// </summary>
public sealed class StickerRaycaster
{
    struct Candidate
    {
        public Mesh      Mesh;
        public Matrix4x4 LocalToWorld;
        public Bounds    WorldBounds;
    }

    readonly List<Candidate> _candidates = new();
    readonly Bounds          _slabBounds;

    public int CandidateCount => _candidates.Count;

    /// <param name="slabBounds">
    /// World-space AABB the sticker may sample inside. Candidates whose renderer
    /// bounds don't intersect this are dropped at construction.
    /// </param>
    public StickerRaycaster(Bounds slabBounds)
    {
        _slabBounds = slabBounds;
        BuildCandidateList();
    }

    void BuildCandidateList()
    {
        var filters = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        _candidates.Capacity = Mathf.Max(_candidates.Capacity, 64);

        for (int i = 0; i < filters.Length; i++)
        {
            var mf = filters[i];
            if (mf == null) continue;
            var go = mf.gameObject;
            if (!go.activeInHierarchy) continue;
            if (!go.isStatic) continue;

            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled) continue;

            // Cheap reject by bounds before paying for material introspection.
            var bounds = mr.bounds;
            if (!_slabBounds.Intersects(bounds)) continue;

            if (!AnyMaterialWritesZ(mr.sharedMaterials)) continue;

            var mesh = mf.sharedMesh;
            if (mesh == null || mesh.vertexCount == 0) continue;

            _candidates.Add(new Candidate
            {
                Mesh         = mesh,
                LocalToWorld = mf.transform.localToWorldMatrix,
                WorldBounds  = bounds,
            });
        }
    }

    static bool AnyMaterialWritesZ(Material[] mats)
    {
        if (mats == null) return false;
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            if (m == null) continue;
            // Most opaque shaders don't expose the _ZWrite toggle — treat absence as "writes Z".
            if (!m.HasProperty("_ZWrite")) return true;
            if (m.GetFloat("_ZWrite") > 0.5f) return true;
        }
        return false;
    }

    /// <summary>
    /// Closest-hit raycast across the candidate set. Returns the world-space hit point
    /// and geometric normal of the closest triangle, or <c>Hit=false</c> if nothing was hit.
    /// </summary>
    public StickerGridSolver.RaycastHit3 Raycast(Ray ray, float maxDist)
    {
        var result = new StickerGridSolver.RaycastHit3();
        float closest = maxDist;
        for (int i = 0; i < _candidates.Count; i++)
        {
            var c = _candidates[i];
            // AABB pre-test — skips candidates the ray can't possibly hit within maxDist.
            if (!c.WorldBounds.IntersectRay(ray, out float boundsDist)) continue;
            if (boundsDist >= closest) continue;

            if (!TryIntersectRayMesh(ray, c.Mesh, c.LocalToWorld, out var hit)) continue;
            if (hit.distance >= closest) continue;

            closest       = hit.distance;
            result.Hit    = true;
            result.Point  = hit.point;
            result.Normal = hit.normal;
        }
        return result;
    }

    // ── UnityEditor.HandleUtility.IntersectRayMesh via reflection ────────

    static MethodInfo s_intersectRayMesh;
    static bool       s_lookupAttempted;

    static bool TryIntersectRayMesh(Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit)
    {
        hit = default;
        if (!s_lookupAttempted)
        {
            s_lookupAttempted = true;
            var t = System.Type.GetType("UnityEditor.HandleUtility, UnityEditor");
            if (t != null)
            {
                s_intersectRayMesh = t.GetMethod(
                    "IntersectRayMesh",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4), typeof(RaycastHit).MakeByRefType() },
                    null);
            }
            if (s_intersectRayMesh == null)
                Debug.LogError("[Sticker] UnityEditor.HandleUtility.IntersectRayMesh(Ray, Mesh, Matrix4x4, out RaycastHit) not found via reflection — Sticker conforming is disabled.");
        }
        if (s_intersectRayMesh == null) return false;

        var args = new object[] { ray, mesh, matrix, null };
        bool ok = (bool)s_intersectRayMesh.Invoke(null, args);
        if (ok) hit = (RaycastHit)args[3];
        return ok;
    }
}
#endif
