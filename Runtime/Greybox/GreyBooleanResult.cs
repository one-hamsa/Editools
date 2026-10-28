using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Pool;
#if UNITY_EDITOR
using Editools.Geometry;
#endif

/// <summary>
/// Owns the baked mesh for a Boolean (Subject − Operator) and is the PARENT of both: the Subject and
/// Operator live under it as movable inputs, so the result has its own free transform (the "final
/// object") while editing/moving either input just re-bakes. The Subject may itself be a
/// <see cref="GreyBooleanResult"/>, so booleans can be chained.
///
/// All geometry is authored in this result's local space. Every CSG fragment is tagged with the source
/// box face it came from (Subject, Operator, or a Greybox part of a compound), so the mesh pass can
/// rebuild each face as one surface: its fragments are unioned into an outline in the face's own
/// bilinear (a, b) parameter space and meshed with the SAME grid a Greybox gives that face, so uncut
/// regions match the source box exactly (even on distorted, non-planar faces) and the cut edge is
/// clipped exactly. Surfaces without a box face (mesh-based inputs, frozen inner cuts) are meshed as a
/// planar grid aligned to their longest outline edge. The boolean is computed only in the editor (the
/// result is serialized), so no CSG runs in a player build.
/// </summary>
public class GreyBooleanResult : GreyPrimitive
{
    // Polygon tags. Subject Greybox faces keep their index 0..5 (so per-face visibility is inherited
    // and the source frame is recoverable) — including through chained results, where they refer to
    // the innermost Greybox subject. Operator Greybox faces use 20+index. Greybox parts of a compound
    // input use the part-face ranges (base + part index * 6 + face). Other bodies use the body tags.
    // Anything >= 6 is never dropped by the face-visibility filter.
    const int k_OperatorFaceBase = 20;
    const int k_SubjectBodyTag   = 100;
    const int k_OperatorBodyTag  = 101;
    const int k_HiddenSubjectFace = -1;
    // A cut's faces, once an outer boolean nests it, are frozen to k_CutLevelBase + level so each
    // boolean level keeps a distinct identity (and its own material slot) all the way up the chain.
    const int k_CutLevelBase     = 200;
    const int k_SubjectPartFaceBase  = 1000;
    const int k_OperatorPartFaceBase = 2000;
    const int k_PartFaceRange        = 1000; // parts beyond range / 6 fall back to the body tag

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
#if UNITY_EDITOR
        _bakedSolid = null;
#endif
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
    // Per compound part (indexed like Parts); null entries for parts that aren't a Greybox.
    [NonSerialized] Vector3[][] _subjPartLocal;
    [NonSerialized] Vector3[][] _opPartLocal;

    // Cached subtraction from the render pass, reused by the immediately-following collider pass so the
    // (expensive) CSG only runs once per RebuildMesh. The source frames stay valid across both passes.
    [NonSerialized] CsgSolid _bakedSolid;
    [NonSerialized] bool[]   _bakedActiveFaces;
    [NonSerialized] float    _bakedUvScale;
#endif

    protected override void GenerateMesh(Mesh mesh)
    {
#if UNITY_EDITOR
        if (_subject == null)
        {
            mesh.Clear();
            _bakedSolid = null;
            return; // Deleted or not yet configured subject contributes no geometry.
        }

        // Collider pass (SubdivisionSuppressed) reuses the render pass's subtraction, just meshes it
        // minimally (density 0). The (expensive) CSG only runs once per RebuildMesh.
        if (!(SubdivisionSuppressed && _bakedSolid != null))
            _bakedSolid = ComputeSolid(out _bakedActiveFaces, out _bakedUvScale);

        BuildMesh(mesh, _bakedSolid, _bakedActiveFaces, _bakedUvScale);
#endif
    }

#if UNITY_EDITOR
    // ─── Solid construction (everything in THIS result's local space) ─────────

    /// <summary>
    /// Computes the Subject − Operator solid in this result's local space, along with the
    /// face-visibility mask and UV scale the mesh pass applies to it. Also refreshes the source
    /// box frames (the face corners the surface grids are built in).
    /// </summary>
    CsgSolid ComputeSolid(out bool[] activeFaces, out float uvScale)
    {
        var subjectPolys = new List<CsgPolygon>();
        activeFaces = null;
        uvScale = 1f;
        _subjLocal = null;
        _opLocal = null;
        _subjPartLocal = null;
        _opPartLocal = null;

        if (_subject is Greybox gb)
        {
            _subjLocal = ToLocal(gb.transform, gb.Corners);
            AddBoxPolygons(subjectPolys, _subjLocal, faceTagBase: 0);
            activeFaces = gb.VisibleFaces;
            uvScale = gb.UvTileScale;
        }
        else if (_subject is GreyBooleanResult chained)
        {
            // A chained boolean consumes the inner result's exact CSG polygons, NOT its baked render
            // mesh — the quad-grid remesh is full of T-vertices whose jittery per-triangle planes
            // leak through the BSP classification, keeping inside-out operator faces in regions an
            // earlier cut already hollowed.
            AddResultPolygons(subjectPolys, chained, keepSubjectFaceTags: true, bodyTag: k_SubjectBodyTag);

            // Subject face tags survive the whole chain, so the innermost input remains the source of
            // face visibility, grid frames and UV scale — the outer bake renders those faces exactly
            // as the inner one did.
            var innermost = InnermostSubject(chained);
            if (innermost is Greybox box)
            {
                _subjLocal = ToLocal(box.transform, box.Corners);
                activeFaces = box.VisibleFaces;
                uvScale = box.UvTileScale;
            }
            else if (innermost is GreyboxCompound compound)
                _subjPartLocal = PartFrames(compound);
        }
        else if (_subject is GreyboxCompound compound)
        {
            _subjPartLocal = new Vector3[compound.Parts.Count][];
            AddCompoundPolygons(subjectPolys, compound, k_SubjectBodyTag, 0, _subjPartLocal);
        }
        else
            AddMeshPolygons(subjectPolys, _subject, k_SubjectBodyTag);
        var subject = new CsgSolid(subjectPolys);

        var baked = subject;
        if (_operator != null && _operator != _subject)
        {
            var opPolys = new List<CsgPolygon>();
            if (_operator is Greybox opBox)
            {
                _opLocal = ToLocal(opBox.transform, opBox.Corners);
                AddBoxPolygons(opPolys, _opLocal, faceTagBase: k_OperatorFaceBase);
            }
            else if (_operator is GreyBooleanResult chainedOp)
                AddResultPolygons(opPolys, chainedOp, keepSubjectFaceTags: false, bodyTag: k_OperatorBodyTag);
            else if (_operator is GreyboxCompound opCompound)
            {
                _opPartLocal = new Vector3[opCompound.Parts.Count][];
                AddCompoundPolygons(opPolys, opCompound, k_OperatorBodyTag, 0, _opPartLocal);
            }
            else
                AddMeshPolygons(opPolys, _operator, k_OperatorBodyTag);

            if (opPolys.Count > 0)
                baked = CsgSolid.Subtract(subject, new CsgSolid(opPolys));
        }
        return baked;
    }

    /// <summary>
    /// The subtraction solid of this result's last bake, computed on demand when no bake has run
    /// since load. Consumed by an outer <see cref="GreyBooleanResult"/> that uses this result as an
    /// input (the chain re-bakes inner-to-outer, so a fresh outer bake sees a fresh inner solid).
    /// </summary>
    CsgSolid GetOrComputeSolid()
    {
        if (_subject == null) return new CsgSolid(new List<CsgPolygon>()); // not configured yet

        if (_bakedSolid == null)
            _bakedSolid = ComputeSolid(out _bakedActiveFaces, out _bakedUvScale);
        return _bakedSolid;
    }

    Vector3[] ToLocal(Transform src, Vector3[] localCorners)
    {
        var dst = new Vector3[localCorners.Length];
        for (int i = 0; i < localCorners.Length; i++)
            dst[i] = transform.InverseTransformPoint(src.TransformPoint(localCorners[i]));
        return dst;
    }

    Vector3[][] PartFrames(GreyboxCompound compound)
    {
        var frames = new Vector3[compound.Parts.Count][];
        for (int i = 0; i < frames.Length && i * 6 < k_PartFaceRange; i++)
            if (compound.Parts[i] is Greybox box) frames[i] = ToLocal(box.transform, box.Corners);
        return frames;
    }

    void AddBoxPolygons(List<CsgPolygon> dst, Vector3[] corners, int faceTagBase, bool uniformTag = false,
        bool[] visibility = null)
    {
        // A reflected transform or reparented corner array must still describe an outward solid.
        float volume = 0f;
        Vector3 origin = corners[0];
        for (int face = 0; face < 6; face++)
        {
            Vector3 a = corners[s_faceCorners[face, 0]] - origin;
            Vector3 b = corners[s_faceCorners[face, 1]] - origin;
            Vector3 c = corners[s_faceCorners[face, 2]] - origin;
            Vector3 d = corners[s_faceCorners[face, 3]] - origin;
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) + Vector3.Dot(a, Vector3.Cross(c, d));
        }
        bool reverse = volume < 0f;
        for (int face = 0; face < 6; face++)
        {
            Vector3 q0 = corners[s_faceCorners[face, 0]];
            Vector3 q1 = corners[s_faceCorners[face, 1]];
            Vector3 q2 = corners[s_faceCorners[face, 2]];
            Vector3 q3 = corners[s_faceCorners[face, 3]];
            int tag = uniformTag ? faceTagBase : faceTagBase + face;
            if (visibility != null && !visibility[face]) tag = k_HiddenSubjectFace;
            AddTriangle(dst, q0, reverse ? q2 : q1, reverse ? q1 : q2, tag);
            AddTriangle(dst, q0, reverse ? q3 : q2, reverse ? q2 : q3, tag);
        }
    }

    void AddMeshPolygons(List<CsgPolygon> dst, GreyPrimitive src, int tag, int levelOffset = 0)
    {
        if (src is Greybox box)
        {
            AddBoxPolygons(dst, ToLocal(box.transform, box.Corners), tag, uniformTag: true,
                visibility: tag == k_SubjectBodyTag ? box.VisibleFaces : null);
            return;
        }
        if (src is GreyBooleanResult result)
        {
            AddResultPolygons(dst, result, tag == k_SubjectBodyTag, tag, levelOffset, keepBoxFaceTags: false);
            return;
        }
        if (src is GreyboxCompound compound)
        {
            AddCompoundPolygons(dst, compound, tag, levelOffset, partFrames: null);
            return;
        }
        var mf = src.GetComponent<MeshFilter>();
        var mesh = mf != null ? mf.sharedMesh : null;
        if (mesh == null)
        {
            Debug.LogError($"[GreyBoolean] '{src.name}' has no mesh.");
            return;
        }

        var verts = mesh.vertices;
        var tris = mesh.triangles;
        bool reverse = ReversesWinding(src.transform);
        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 a = transform.InverseTransformPoint(src.transform.TransformPoint(verts[tris[i]]));
            Vector3 b = transform.InverseTransformPoint(src.transform.TransformPoint(verts[tris[i + 1]]));
            Vector3 c = transform.InverseTransformPoint(src.transform.TransformPoint(verts[tris[i + 2]]));
            AddTriangle(dst, a, reverse ? c : b, reverse ? b : c, tag);
        }
    }

    /// <summary>
    /// Unions a compound's parts. With <paramref name="partFrames"/> (the compound is a direct input of
    /// this result), each Greybox part keeps per-face tags and its corners are stored at its part index so
    /// the mesh pass can rebuild its faces in their own frames; nested compounds and non-box parts use
    /// the body <paramref name="tag"/>.
    /// </summary>
    void AddCompoundPolygons(List<CsgPolygon> dst, GreyboxCompound compound, int tag, int levelOffset, Vector3[][] partFrames)
    {
        var solid = new CsgSolid(new List<CsgPolygon>());
        int partFaceBase = tag == k_SubjectBodyTag ? k_SubjectPartFaceBase : k_OperatorPartFaceBase;
        for (int i = 0; i < compound.Parts.Count; i++)
        {
            var part = compound.Parts[i];
            if (part == null) continue; // Deleted pieces no longer contribute to the input.
            var polygons = new List<CsgPolygon>();
            if (partFrames != null && part is Greybox box && i * 6 < k_PartFaceRange)
            {
                var corners = ToLocal(box.transform, box.Corners);
                partFrames[i] = corners;
                AddBoxPolygons(polygons, corners, partFaceBase + i * 6,
                    visibility: tag == k_SubjectBodyTag ? box.VisibleFaces : null);
            }
            else
                AddMeshPolygons(polygons, part, tag, levelOffset);
            solid = CsgSolid.Union(solid, new CsgSolid(polygons));
            if (tag == k_SubjectBodyTag) levelOffset += CutCount(part);
        }
        dst.AddRange(solid.polygons);
    }

    bool ReversesWinding(Transform source)
    {
        Vector3 x = transform.InverseTransformVector(source.TransformVector(Vector3.right));
        Vector3 y = transform.InverseTransformVector(source.TransformVector(Vector3.up));
        Vector3 z = transform.InverseTransformVector(source.TransformVector(Vector3.forward));
        return Vector3.Dot(x, Vector3.Cross(y, z)) < 0f;
    }

    static void AddTriangle(List<CsgPolygon> dst, Vector3 a, Vector3 b, Vector3 c, int tag)
    {
        if (CsgPlane.TryFromPoints(a, b, c, out var plane))
            dst.Add(new CsgPolygon(new List<Vector3>(3) { a, b, c }, tag, plane));
    }

    static bool IsSubjectFaceTag(int tag) =>
        (tag >= 0 && tag < 6) || (tag >= k_SubjectPartFaceBase && tag < k_SubjectPartFaceBase + k_PartFaceRange);

    static bool IsOperatorTag(int tag) =>
        (tag >= k_OperatorFaceBase && tag < k_OperatorFaceBase + 6) || tag == k_OperatorBodyTag
        || (tag >= k_OperatorPartFaceBase && tag < k_OperatorPartFaceBase + k_PartFaceRange);

    static bool IsCutLevelTag(int tag) => tag >= k_CutLevelBase && tag < k_SubjectPartFaceBase;

    /// <summary>
    /// Adds the exact CSG polygons of another result's solid, mapped into this result's local space.
    /// When <paramref name="keepSubjectFaceTags"/> (the src is our Subject), the level identity of every
    /// fragment is preserved so materials inherit through the chain: Subject faces and Subject body stay
    /// level 0, already-frozen inner cuts (>= k_CutLevelBase) keep their level, and the src's own
    /// Operator faces are frozen to <c>k_CutLevelBase + CutCount(src)</c>. When src is our Operator,
    /// every fragment collapses to <paramref name="bodyTag"/> — one new cut level.
    /// </summary>
    void AddResultPolygons(List<CsgPolygon> dst, GreyBooleanResult src, bool keepSubjectFaceTags, int bodyTag,
        int levelOffset = 0, bool keepBoxFaceTags = true)
    {
        int frozenLevel = keepSubjectFaceTags ? k_CutLevelBase + CutCount(src) + levelOffset : 0;
        var solid = src.GetOrComputeSolid();
        bool reverse = ReversesWinding(src.transform);
        foreach (var p in solid.polygons)
        {
            var verts = new List<Vector3>(p.verts.Count);
            for (int i = 0; i < p.verts.Count; i++)
                verts.Add(transform.InverseTransformPoint(src.transform.TransformPoint(p.verts[i])));
            if (reverse) verts.Reverse();

            int tag;
            if (!keepSubjectFaceTags)
                tag = bodyTag;                                        // src is the Operator: one new cut
            else if (IsSubjectFaceTag(p.tag) && !keepBoxFaceTags)
                tag = p.tag < 6 && src._bakedActiveFaces != null && !src._bakedActiveFaces[p.tag] ? k_HiddenSubjectFace : bodyTag;
            else if (IsSubjectFaceTag(p.tag) || p.tag == k_SubjectBodyTag || p.tag == k_HiddenSubjectFace)
                tag = p.tag;                                          // Subject-derived (level 0)
            else if (IsCutLevelTag(p.tag))
                tag = p.tag + levelOffset;                            // inner cut within this subject group
            else
                tag = frozenLevel;                                    // src's own Operator -> freeze its level

            if (TryPlaneFromVerts(verts, out var plane))
                dst.Add(new CsgPolygon(verts, tag, plane));
        }
    }

    static int CutCount(GreyPrimitive node)
    {
        if (node is GreyBooleanResult result) return CutCount(result.Subject) + 1;
        int count = 0;
        if (node is GreyboxCompound compound)
            foreach (var part in compound.Parts) if (part != null) count += CutCount(part);
        return count;
    }

    /// <summary>Subject cut materials in geometry traversal order, including nested compound parts.</summary>
    public List<Material> CollectCutMaterials()
    {
        var materials = new List<Material>(4);
        CollectCutMaterials(this, materials);
        return materials;
    }

    static void CollectCutMaterials(GreyPrimitive node, List<Material> materials)
    {
        if (node is GreyBooleanResult result)
        {
            CollectCutMaterials(result.Subject, materials);
            materials.Add(result.Subject != null ? result.Subject.BooleanCutMaterial : null);
        }
        else if (node is GreyboxCompound compound)
            foreach (var part in compound.Parts) if (part != null) CollectCutMaterials(part, materials);
    }

    // Recomputes a polygon's plane after a space change; false for a fully degenerate sliver
    // (skipped — it contributes nothing to the solid).
    static bool TryPlaneFromVerts(List<Vector3> verts, out CsgPlane plane)
    {
        for (int i = 1; i < verts.Count - 1; i++)
            if (CsgPlane.TryFromPoints(verts[0], verts[i], verts[i + 1], out plane))
                return true;
        plane = null;
        return false;
    }

    // The input at the bottom of a Subject chain (Result -> Result -> ... -> input).
    static GreyPrimitive InnermostSubject(GreyBooleanResult result)
    {
        GreyPrimitive s = result;
        while (s is GreyBooleanResult r) s = r.Subject;
        return s;
    }

    // ─── Mesh output: group fragments per source face, mesh each as a grid in its own frame ──

    void BuildMesh(Mesh mesh, CsgSolid solid, bool[] activeFaces, float uvScale)
    {
        float density = ComputeEffectiveDensity(); // vertices per world meter; 0 = outline vertices only

        var polys = new List<CsgPolygon>();
        foreach (var p in solid.polygons)
        {
            if (p.tag == k_HiddenSubjectFace || (p.tag >= 0 && p.tag < 6 && activeFaces != null && !activeFaces[p.tag])) continue;
            polys.Add(p);
        }

        var groups = GroupSurfaces(polys);

        // Each cut level (the faces one Operator carved) can carry its own material, preserved through
        // further booleans. Every level that has an assigned material gets its own submesh/slot, in
        // level order (innermost first); the Subject and any unassigned cut levels share slot 0. With
        // no cut materials anywhere this is a single submesh, exactly as before. The collider pass
        // (SubdivisionSuppressed) never splits — collision doesn't care about slots.
        var cutMats = CollectCutMaterials();       // index 0 => level 1 .. up to the outermost cut
        int levels  = cutMats.Count;               // == CutCount(this)

        var slotForLevel = new int[levels + 1];    // cut level (0..N) -> submesh slot
        int slotCount = 1;                         // slot 0 = Subject + unassigned cuts
        for (int lvl = 1; lvl <= levels; lvl++)
            slotForLevel[lvl] = cutMats[lvl - 1] != null ? slotCount++ : 0;

        bool split = !SubdivisionSuppressed && slotCount > 1;

        var verts   = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs     = new List<Vector2>();
        var slotTris = new List<int>[split ? slotCount : 1];
        for (int i = 0; i < slotTris.Length; i++) slotTris[i] = new List<int>();

        var localToWorld = transform.localToWorldMatrix;
        foreach (var group in groups)
        {
            int slot = split ? slotForLevel[GroupLevel(group, polys, levels)] : 0;
            EmitSurface(group, polys, localToWorld, density, uvScale, verts, normals, uvs, slotTris[slot]);
        }

        mesh.Clear();
        if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = slotTris.Length;
        for (int i = 0; i < slotTris.Length; i++)
            mesh.SetTriangles(slotTris[i], i);
    }

    // The cut level a surface belongs to: 0 = Subject-derived, N (== immediateLevel) = this result's
    // own Operator, 1..N-1 = an inner Operator frozen into the chain. A group shares one source, so
    // the first decisive tag classifies the whole group.
    static int GroupLevel(List<int> group, List<CsgPolygon> polys, int immediateLevel)
    {
        foreach (int pi in group)
        {
            int tag = polys[pi].tag;
            if (IsSubjectFaceTag(tag) || tag == k_SubjectBodyTag) return 0;
            if (IsOperatorTag(tag)) return immediateLevel;
            if (IsCutLevelTag(tag)) return tag - k_CutLevelBase;
        }
        return 0;
    }

    void EmitSurface(List<int> group, List<CsgPolygon> polys, Matrix4x4 localToWorld, float density, float uvScale,
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris)
    {
        double unit = SnapUnit(group, polys);
        string error;
        bool meshed = TryGetFaceFrame(polys[group[0]].tag, out var frame)
            ? TryMeshBoxFace(group, polys, frame, localToWorld, unit, density, uvScale, verts, normals, uvs, tris, out error)
            : TryMeshPlanarSurface(group, polys, localToWorld, unit, density, uvScale, verts, normals, uvs, tris, out error);
        if (meshed) return;

        // Keep the exact solid intact if an authoring outline cannot be remeshed.
        Debug.LogError($"[GreyBoolean] '{name}': {error} Using the exact source polygons for this face.", this);
        foreach (int index in group)
        {
            var polygon = polys[index];
            int first = verts.Count;
            foreach (var vertex in polygon.verts) AddVert(vertex, polygon.plane.normal, uvScale, verts, normals, uvs);
            for (int k = 1; k + 1 < polygon.verts.Count; k++)
            { tris.Add(first); tris.Add(first + k); tris.Add(first + k + 1); }
        }
    }

    // Outline snap length (local units): above the float noise of CSG split vertices at the surface's
    // distance from the origin. Seam vertices that still disagree by a few units are welded by the outline.
    static double SnapUnit(List<int> group, List<CsgPolygon> polys)
    {
        float magnitude = 0f;
        foreach (int index in group)
            foreach (var vertex in polys[index].verts)
                magnitude = Mathf.Max(magnitude, Mathf.Abs(vertex.x), Mathf.Abs(vertex.y), Mathf.Abs(vertex.z));
        return Math.Max(2e-5d, magnitude * 2e-6d);
    }

    static int Segments(float worldLength, float density) =>
        density > 0f ? Mathf.Max(1, Mathf.RoundToInt(worldLength * density)) : 1;

    /// <summary>
    /// Meshes one source box face: fragments are mapped into the face's (a, b) parameter space (each
    /// CSG triangle half affinely, so straight cut edges stay straight), unioned, clipped by the face's
    /// Greybox grid and mapped back onto the CSG surface. The whole face shares the Greybox face normal,
    /// so the diagonal of a distorted face is not visible in flat shading.
    /// </summary>
    static bool TryMeshBoxFace(List<int> group, List<CsgPolygon> polys, FaceFrame frame, Matrix4x4 localToWorld,
        double unit, float density, float uvScale,
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris, out string error)
    {
        var fragments = new List<Vector2[]>(group.Count);
        using var sourceLease = DictionaryPool<IntPoint, Vector3>.Get(out var sourceVertices);
        foreach (int index in group)
        {
            var polygon = polys[index];
            bool firstHalf = frame.InFirstHalf(Centroid(polygon.verts));
            var points = new Vector2[polygon.verts.Count];
            for (int k = 0; k < points.Length; k++)
            {
                Vector3 vertex = polygon.verts[k];
                points[k] = frame.Project(vertex, firstHalf);
                sourceVertices.TryAdd(Snap(points[k], unit), vertex);
            }
            fragments.Add(points);
        }
        if (!GreyFaceOutline.TryBuild(fragments, unit, out var outline, out error)) return false;

        // Cell size of the face's Greybox subdivision, so an uncut face keeps the source box grid exactly.
        double targetA = density > 0f ? frame.lengthA / unit / Segments(frame.WorldLengthA(localToWorld), density) : 0d;
        double targetB = density > 0f ? frame.lengthB / unit / Segments(frame.WorldLengthB(localToWorld), density) : 0d;
        if (!outline.TryTriangulate(targetA, targetB, out var points2D, out var indices, out error)) return false;

        Vector3 normal = frame.Normal;
        bool flip = Vector3.Dot(normal, polys[group[0]].plane.normal) < 0f;
        if (flip) normal = -normal;
        int first = verts.Count;
        for (int i = 0; i < points2D.Count; i++)
        {
            if (!sourceVertices.TryGetValue(points2D[i], out var position))
            {
                Vector2 uv = outline.ToVector(points2D[i]);
                position = frame.OnCsgSurface(uv);
            }
            AddVert(position, normal, uvScale, verts, normals, uvs);
        }
        AddTriangles(indices, first, flip, tris);
        return true;
    }

    /// <summary>
    /// Meshes a surface with no source box face (mesh-based inputs, frozen inner cuts): fragments are
    /// unioned in a planar frame aligned to the outline's longest edge and clipped by a grid at the
    /// target density.
    /// </summary>
    static bool TryMeshPlanarSurface(List<int> group, List<CsgPolygon> polys, Matrix4x4 localToWorld,
        double unit, float density, float uvScale,
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris, out string error)
    {
        Vector3 normal = polys[group[0]].plane.normal;
        Vector3 origin = polys[group[0]].verts[0];
        Vector3 tangent = Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up;
        Vector3 uDir = Vector3.Cross(normal, tangent).normalized;
        Vector3 vDir = Vector3.Cross(normal, uDir).normalized;

        var fragments = new List<Vector2[]>(group.Count);
        using var sourceLease = DictionaryPool<IntPoint, Vector3>.Get(out var sourceVertices);
        Project(group, polys, origin, uDir, vDir, unit, fragments, sourceVertices);
        if (TryLongestBoundaryEdge(fragments, unit, out Vector2 direction))
        {
            uDir = (direction.x * uDir + direction.y * vDir).normalized;
            vDir = Vector3.Cross(normal, uDir).normalized;
            Project(group, polys, origin, uDir, vDir, unit, fragments, sourceVertices);
        }
        if (!GreyFaceOutline.TryBuild(fragments, unit, out var outline, out error)) return false;

        // Target cell size (outline units per axis) from the density (vertices per world meter).
        float worldPerU = localToWorld.MultiplyVector(uDir).magnitude;
        float worldPerV = localToWorld.MultiplyVector(vDir).magnitude;
        double targetA = density > 0f && worldPerU > 0f ? 1d / (density * worldPerU * unit) : 0d;
        double targetB = density > 0f && worldPerV > 0f ? 1d / (density * worldPerV * unit) : 0d;
        if (!outline.TryTriangulate(targetA, targetB, out var points2D, out var indices, out error)) return false;

        bool flip = Vector3.Dot(Vector3.Cross(uDir, vDir), normal) < 0f;
        int first = verts.Count;
        foreach (var point in points2D)
        {
            if (!sourceVertices.TryGetValue(point, out var position))
            {
                Vector2 uv = outline.ToVector(point);
                position = origin + uv.x * uDir + uv.y * vDir;
            }
            AddVert(position, normal, uvScale, verts, normals, uvs);
        }
        AddTriangles(indices, first, flip, tris);
        return true;
    }

    static void Project(List<int> group, List<CsgPolygon> polys, Vector3 origin, Vector3 uDir, Vector3 vDir, double unit,
        List<Vector2[]> fragments, Dictionary<IntPoint, Vector3> sourceVertices)
    {
        fragments.Clear();
        sourceVertices.Clear();
        foreach (int index in group)
        {
            var polygon = polys[index];
            var points = new Vector2[polygon.verts.Count];
            for (int k = 0; k < points.Length; k++)
            {
                Vector3 vertex = polygon.verts[k];
                points[k] = ProjectAB(vertex, origin, uDir, vDir);
                sourceVertices.TryAdd(Snap(points[k], unit), vertex);
            }
            fragments.Add(points);
        }
    }

    // Direction of the longest edge used by exactly one fragment (a surface boundary, not a CSG seam).
    static bool TryLongestBoundaryEdge(List<Vector2[]> fragments, double unit, out Vector2 direction)
    {
        using var countLease = DictionaryPool<(IntPoint, IntPoint), int>.Get(out var edgeCounts);
        foreach (var fragment in fragments)
        {
            for (int i = 0; i < fragment.Length; i++)
            {
                var key = EdgeKey(Snap(fragment[i], unit), Snap(fragment[(i + 1) % fragment.Length], unit));
                edgeCounts[key] = edgeCounts.TryGetValue(key, out int count) ? count + 1 : 1;
            }
        }
        direction = Vector2.zero;
        double longest = 0d;
        foreach (var (key, count) in edgeCounts)
        {
            if (count != 1) continue;
            double dx = (double)key.Item2.X - key.Item1.X, dy = (double)key.Item2.Y - key.Item1.Y;
            double length = dx * dx + dy * dy;
            if (length <= longest) continue;
            longest = length;
            direction = new Vector2((float)dx, (float)dy);
        }
        return longest > 0d;
    }

    static (IntPoint, IntPoint) EdgeKey(IntPoint a, IntPoint b) =>
        a.X < b.X || (a.X == b.X && a.Y < b.Y) ? (a, b) : (b, a);

    static IntPoint Snap(Vector2 p, double unit) =>
        new IntPoint((long)Math.Round(p.x / unit), (long)Math.Round(p.y / unit));

    static void AddTriangles(List<int> indices, int first, bool flip, List<int> tris)
    {
        for (int i = 0; i < indices.Count; i += 3)
        {
            tris.Add(first + indices[i]);
            tris.Add(first + indices[i + (flip ? 2 : 1)]);
            tris.Add(first + indices[i + (flip ? 1 : 2)]);
        }
    }

    static Vector3 Centroid(List<Vector3> verts)
    {
        Vector3 sum = Vector3.zero;
        foreach (var v in verts) sum += v;
        return sum / verts.Count;
    }

    bool TryGetFaceFrame(int tag, out FaceFrame frame)
    {
        Vector3[] corners = null;
        int face = 0;
        if (tag >= 0 && tag < 6) { corners = _subjLocal; face = tag; }
        else if (tag >= k_OperatorFaceBase && tag < k_OperatorFaceBase + 6) { corners = _opLocal; face = tag - k_OperatorFaceBase; }
        else if (tag >= k_SubjectPartFaceBase && tag < k_SubjectPartFaceBase + k_PartFaceRange)
            corners = PartCorners(_subjPartLocal, tag - k_SubjectPartFaceBase, out face);
        else if (tag >= k_OperatorPartFaceBase && tag < k_OperatorPartFaceBase + k_PartFaceRange)
            corners = PartCorners(_opPartLocal, tag - k_OperatorPartFaceBase, out face);
        if (corners == null) { frame = default; return false; }
        return FaceFrame.TryCreate(corners, face, out frame);
    }

    static Vector3[] PartCorners(Vector3[][] parts, int partFace, out int face)
    {
        face = partFace % 6;
        int part = partFace / 6;
        return parts != null && part < parts.Length ? parts[part] : null;
    }

    /// <summary>Solves p = origin + a * uDir + b * vDir in the least-squares sense (the off-plane component is dropped).</summary>
    static Vector2 ProjectAB(Vector3 p, Vector3 origin, Vector3 uDir, Vector3 vDir)
    {
        Vector3 d = p - origin;
        double uu = Dot(uDir, uDir), vv = Dot(vDir, vDir), uv = Dot(uDir, vDir);
        double du = Dot(d, uDir), dv = Dot(d, vDir);
        double denominator = uu * vv - uv * uv;
        return new Vector2((float)((du * vv - dv * uv) / denominator), (float)((dv * uu - du * uv) / denominator));
    }

    static double Dot(Vector3 a, Vector3 b) => (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z;

    /// <summary>
    /// One source box face as a bilinear patch over (a, b): q0 at (0, 0), q1 at (1, 0), q2 at (1, 1), q3 at (0, 1),
    /// matching the (s, t) grid <see cref="Greybox"/> renders. The CSG sees the face as the two triangles
    /// (q0, q1, q2) and (q0, q2, q3); each half maps affinely onto its parameter triangle, and both maps
    /// agree on the diagonal. 2D outline coordinates are (a * lengthA, b * lengthB) so they are metric.
    /// </summary>
    readonly struct FaceFrame
    {
        public readonly Vector3 q0, q1, q2, q3;
        public readonly float lengthA, lengthB; // mean edge lengths along a and b (local units)
        readonly Vector3 _diagonalSide;         // normal of the plane through q0-q2 that separates the two halves
        readonly float _sideOfQ1;

        FaceFrame(Vector3 q0, Vector3 q1, Vector3 q2, Vector3 q3, float lengthA, float lengthB, Vector3 diagonalSide, float sideOfQ1)
        {
            this.q0 = q0; this.q1 = q1; this.q2 = q2; this.q3 = q3;
            this.lengthA = lengthA; this.lengthB = lengthB;
            _diagonalSide = diagonalSide;
            _sideOfQ1 = sideOfQ1;
        }

        /// <summary>False for a collapsed face (a zero-length edge, or a corner folded past the diagonal or over the face).</summary>
        public static bool TryCreate(Vector3[] corners, int face, out FaceFrame frame)
        {
            Vector3 q0 = corners[s_faceCorners[face, 0]], q1 = corners[s_faceCorners[face, 1]];
            Vector3 q2 = corners[s_faceCorners[face, 2]], q3 = corners[s_faceCorners[face, 3]];
            float lengthA = ((q1 - q0).magnitude + (q2 - q3).magnitude) * 0.5f;
            float lengthB = ((q3 - q0).magnitude + (q2 - q1).magnitude) * 0.5f;
            Vector3 raw = Vector3.Cross(q1 - q0, q3 - q0);
            Vector3 diagonalSide = Vector3.Cross(q2 - q0, raw);
            float sideOfQ1 = Vector3.Dot(q1 - q0, diagonalSide), sideOfQ3 = Vector3.Dot(q3 - q0, diagonalSide);
            float minArea = 1e-6f * lengthA * lengthB;
            frame = new FaceFrame(q0, q1, q2, q3, lengthA, lengthB, diagonalSide, sideOfQ1);
            // Both CSG halves must face the way the whole face does, or a grid triangle spanning the diagonal could fold.
            return lengthA > 1e-6f && lengthB > 1e-6f
                && Vector3.Dot(Vector3.Cross(q1 - q0, q2 - q0), raw) > minArea * raw.magnitude
                && Vector3.Dot(Vector3.Cross(q2 - q0, q3 - q0), raw) > minArea * raw.magnitude
                && sideOfQ1 * sideOfQ3 < 0f;
        }

        public Vector3 Normal => Vector3.Cross(q1 - q0, q3 - q0).normalized; // the Greybox face normal, unoriented

        public float WorldLengthA(Matrix4x4 localToWorld) =>
            (localToWorld.MultiplyVector(q1 - q0).magnitude + localToWorld.MultiplyVector(q2 - q3).magnitude) * 0.5f;

        public float WorldLengthB(Matrix4x4 localToWorld) =>
            (localToWorld.MultiplyVector(q3 - q0).magnitude + localToWorld.MultiplyVector(q2 - q1).magnitude) * 0.5f;

        /// <summary>True when <paramref name="p"/> lies on the (q0, q1, q2) triangle's side of the diagonal.</summary>
        public bool InFirstHalf(Vector3 p) => Vector3.Dot(p - q0, _diagonalSide) * _sideOfQ1 >= 0f;

        public Vector2 Project(Vector3 p, bool firstHalf)
        {
            Vector2 ab = firstHalf ? ProjectAB(p, q0, q1 - q0, q2 - q1) : ProjectAB(p, q0, q2 - q3, q3 - q0);
            return new Vector2(ab.x * lengthA, ab.y * lengthB);
        }

        /// <summary>The point on the CSG's two-triangle surface; exact for anything on a straight fragment edge.</summary>
        public Vector3 OnCsgSurface(Vector2 uv)
        {
            float a = uv.x / lengthA, b = uv.y / lengthB;
            return a >= b ? q0 + a * (q1 - q0) + b * (q2 - q1) : q0 + a * (q2 - q3) + b * (q3 - q0);
        }
    }

    List<List<int>> GroupSurfaces(List<CsgPolygon> polygons)
    {
        var groups = new List<List<int>>(16);
        for (int i = 0; i < polygons.Count; i++)
        {
            List<int> match = null;
            foreach (var group in groups)
            {
                if (!SameSurface(polygons[group[0]], polygons[i])) continue;
                match = group;
                break;
            }
            if (match == null) { match = new List<int>(8); groups.Add(match); }
            match.Add(i);
        }
        return groups;
    }

    // Fragments of one source box face form one surface whatever their planes (a distorted face is two
    // CSG triangles); anything else is grouped per tag and plane.
    bool SameSurface(CsgPolygon a, CsgPolygon b)
    {
        if (a.tag != b.tag) return false;
        if (TryGetFaceFrame(a.tag, out _)) return true;
        if (Vector3.Dot(a.plane.normal, b.plane.normal) < 0.999999f) return false;
        Vector3 origin = a.verts[0];
        foreach (var p in b.verts)
            if (Mathf.Abs(Vector3.Dot(a.plane.normal, p - origin)) > CsgPlane.Epsilon) return false;
        return true;
    }

    static int AddVert(Vector3 p, Vector3 normal, float uvScale,
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs)
    {
        int index = verts.Count;
        verts.Add(p);
        normals.Add(normal);
        uvs.Add(BoxProjectUv(p, normal, uvScale));
        return index;
    }

    static Vector2 BoxProjectUv(Vector3 p, Vector3 n, float scale)
    {
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
        Vector2 uv;
        if (ax >= ay && ax >= az)      uv = new Vector2(p.z, p.y);
        else if (ay >= ax && ay >= az) uv = new Vector2(p.x, p.z);
        else                           uv = new Vector2(p.x, p.y);
        return uv * scale;
    }

#endif
}
