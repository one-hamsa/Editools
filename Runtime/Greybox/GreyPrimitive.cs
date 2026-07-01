using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Planar UV projection mode for a grey primitive. <see cref="None"/> keeps each type's own UVs
/// (per-face for Greybox, grid for the others). Any other value replaces the baked UVs with a flat
/// projection onto the plane whose normal is the named axis, normalized to fill 0–1 across the
/// object's bounding box on that plane. "Local" projects in the object's local space; "Global" in
/// world space (computed at bake time, so it doesn't follow later moves until the next rebake).
/// </summary>
public enum PlanarUvProjection
{
    None = 0,
    XLocal,
    YLocal,
    ZLocal,
    XGlobal,
    YGlobal,
    ZGlobal,
}

/// <summary>
/// Base class for grey primitives. Owns the serialized mesh and exposes <see cref="RebuildMesh"/>;
/// rebuilds run only when explicitly triggered — inspector change-check, scene tools, undo/redo,
/// the "Rebuild Mesh" button, or <see cref="Reset"/>. Nothing rebuilds on load, domain reload, or
/// any other Unity callback.
///
/// Mesh persistence: a primitive on a plain scene object keeps its meshes scene-embedded (serialized
/// into the scene file). A primitive that belongs to a prefab embeds its meshes inside the prefab
/// file itself, as sub-assets — no external mesh files. The prefab and every scene instance
/// reference the same embedded bake, so rebaking from anywhere updates all of them.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public abstract class GreyPrimitive : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Coefficient applied to the GreyboxManager's vertex density. " +
             "1 = use manager density as-is. 0 = disable subdivision on this primitive.")]
    float _subdivisionMultiplier = 1f;

    public float SubdivisionMultiplier
    {
        get => _subdivisionMultiplier;
        set => _subdivisionMultiplier = value;
    }

    // ─── Planar UV ───────────────────────────────────────────────

    [SerializeField]
    [Tooltip("Replace the baked UVs with a flat projection onto a plane. None keeps this type's own " +
             "UVs. The axis names the plane normal; Local projects in object space, Global in world " +
             "space (baked once — rebuild to refresh after moving). The projection is normalized to " +
             "fill 0–1 across the object's bounding box on that plane (the box is padded 1% per side " +
             "to avoid edge tiling). On a Greybox the UV tile scale further multiplies the result.")]
    PlanarUvProjection _planarUv = PlanarUvProjection.None;

    public PlanarUvProjection PlanarUv
    {
        get => _planarUv;
        set => _planarUv = value;
    }

    /// <summary>Extra multiplier applied to the normalized planar UVs. 1 for most types; Greybox
    /// overrides it with its UV tile scale so the existing field keeps tiling the projection.</summary>
    protected virtual float PlanarUvMultiplier => 1f;

    // ─── Flip Faces ──────────────────────────────────────────────

    [SerializeField]
    [Tooltip("Flip the mesh inside-out — reverses every triangle's winding and its normals so the " +
             "faces render from the other side. Useful for turning a box into a room. Applied to the " +
             "render mesh on each rebuild.")]
    bool _flipFaces;

    public bool FlipFaces
    {
        get => _flipFaces;
        set => _flipFaces = value;
    }

    // ─── Boolean ─────────────────────────────────────────────────
    // Optional CSG subtraction. When set, this primitive is the Subject, the reference is the
    // Operator, and a baked 'Boolean Result' child holds Subject−Operator (see GreyBooleanResult /
    // GreyBooleanOrchestrator). Lives on the base so any grey type — including a GreyBooleanResult —
    // can be a Subject and be chained/booleaned further.

    [SerializeField]
    [Tooltip("Optional Operator to subtract from this object. Drag any Grey object here (or use Pick). " +
             "When set, this object and the Operator stop rendering and a child 'Boolean Result' mesh " +
             "is baked as Subject minus Operator, inheriting this object's material, shadow/static " +
             "settings, and (for a Greybox) per-face visibility. Clear it to restore normal rendering.")]
    GreyPrimitive _booleanOperator;

    public GreyPrimitive BooleanOperator => _booleanOperator;

    // ─── Mesh ───────────────────────────────────────────────────

    [SerializeField, HideInInspector]
    Mesh _mesh;
    [SerializeField, HideInInspector]
    int _meshOwnerId;
    protected Mesh SharedMesh => _mesh;

    // GUID of the prefab asset the baked meshes are embedded in as sub-assets; empty when the meshes
    // are scene-embedded (plain scene object). When set, the owner-id fields are meaningless — the
    // asset itself is the identity, valid on the prefab and on every instance of it.
    [SerializeField, HideInInspector]
    string _meshOwnerGuid;

    // Unsubdivided twin of the render mesh, for collision. Same shape, far fewer triangles — owned and
    // serialized exactly like _mesh. Baked by re-running GenerateMesh with subdivision suppressed.
    [SerializeField, HideInInspector]
    Mesh _colliderMesh;
    [SerializeField, HideInInspector]
    int _colliderMeshOwnerId;
    public Mesh ColliderMesh => _colliderMesh;

    /// <summary>True for types that bake a separate unsubdivided collider mesh (Greybox, boolean
    /// result). False (the default) means the MeshCollider just uses the render mesh — Greypipe, Greyroad.</summary>
    public virtual bool UsesColliderMesh => false;

    // When true, ComputeEffectiveDensity returns 0 so GenerateMesh produces the minimal (collision) form.
    [System.NonSerialized] bool _suppressSubdivision;
    protected bool SubdivisionSuppressed => _suppressSubdivision;

#if UNITY_EDITOR
    [System.NonSerialized] bool _meshIsLive;
    public bool MeshIsLive => _meshIsLive;

    /// <summary>Called by the save hook after a scene/prefab save. Writes the prefab the meshes
    /// are embedded in if they're dirty — a scene save doesn't touch the prefab file (a prefab
    /// stage save does, making this a no-op there). Scene-embedded meshes are persisted by the
    /// scene save, so they need nothing here.</summary>
    public void NotifyMeshSaved()
    {
        if (!string.IsNullOrEmpty(_meshOwnerGuid) && _mesh != null)
            AssetDatabase.SaveAssetIfDirty(_mesh);
        _meshIsLive = false;
    }

    /// <summary>
    /// Drops a stale collider twin from a type that reuses the render mesh for collision
    /// (<see cref="UsesColliderMesh"/> == false, e.g. Greypipe, Greyroad). Such a twin only exists
    /// as leftover serialized data from an older bake; nothing rebuilds or references it, so it's
    /// dead weight in the scene/prefab. Called pre-save by the save hook. No-op for collider-mesh
    /// types and when the field is already clear. Returns true when it cleared something.
    /// A scene-embedded twin is dropped just by nulling the reference (the save won't serialize an
    /// unreferenced object); a twin embedded as this primitive's own prefab sub-asset is also
    /// removed from the asset, guarded so a mesh a sibling still shares is never yanked.
    /// </summary>
    public bool ClearStaleColliderMesh()
    {
        if (UsesColliderMesh) return false;
        if (_colliderMesh == null && _colliderMeshOwnerId == 0) return false;

        if (_colliderMesh != null
            && _colliderMesh.name == $"{GetType().Name} Collider"
            && AssetDatabase.Contains(_colliderMesh)
            && AssetDatabase.GetAssetPath(_colliderMesh) == GetContainingPrefabPath()
            && !MeshSharedWithinContainer())
        {
            AssetDatabase.RemoveObjectFromAsset(_colliderMesh);
        }

        _colliderMesh = null;
        _colliderMeshOwnerId = 0;
        return true;
    }

    protected virtual void Reset()
    {
        ResetToDefaults();
        RebuildMesh();
    }
#endif

    // ─── Public API ──────────────────────────────────────────────

    public void RebuildMesh()
    {
#if UNITY_EDITOR
        _meshIsLive = true;
#endif
        OnBeforeRebuild();

#if UNITY_EDITOR
        // During an interactive tool drag, regenerate the live mesh only — all AssetDatabase / prefab
        // persistence is deferred to drag end (see BeginDeferredPersist) so dragging a face doesn't
        // trigger a synchronous prefab reimport on every tick.
        if (s_deferPersist)
        {
            EnsureLiveMeshTargets();
            s_deferredPersist.Add(this);
        }
        else
        {
            PrepareBakeTargets();
        }
#else
        EnsureMesh();
#endif
        GenerateMesh(_mesh);
        _mesh.RecalculateBounds();
        ApplyPlanarUv(_mesh);
        ApplyFaceFlip(_mesh);

        // Types that opt in (Greybox, GreyBooleanResult) bake an unsubdivided collider twin from the
        // same inputs and feed it to the MeshCollider. Others (Greypipe, Greyroad) just point the
        // collider at the render mesh — no separate bake.
        Mesh colliderMesh = _mesh;
        if (UsesColliderMesh)
        {
#if !UNITY_EDITOR
            EnsureColliderMesh();
#endif
            _suppressSubdivision = true;
            GenerateMesh(_colliderMesh);
            _suppressSubdivision = false;
            _colliderMesh.RecalculateBounds();
            colliderMesh = _colliderMesh;
        }

        var mf = GetComponent<MeshFilter>();
        if (mf != null) mf.sharedMesh = _mesh;

        var mc = GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = colliderMesh;

#if UNITY_EDITOR
        if (!s_deferPersist) PersistBakedMeshes();
#endif
    }

#if UNITY_EDITOR
    /// <summary>Injects the (freshly baked) collider mesh into a MeshCollider on this object — the
    /// "Set Collider" button, for when a collider is added manually after the mesh was built.</summary>
    public void ApplyColliderMesh()
    {
        bool assetBacked = !string.IsNullOrEmpty(_meshOwnerGuid);
        if (_colliderMesh == null || (!assetBacked && _colliderMeshOwnerId != GetInstanceID()))
            RebuildMesh();

        var mc = GetComponent<MeshCollider>();
        if (mc == null)
        {
            Debug.LogError($"[Greybox] '{name}' has no MeshCollider to set — add one first.");
            return;
        }
        mc.sharedMesh = _colliderMesh;
    }
#endif

    // ─── Subdivision ─────────────────────────────────────────────

    protected float ComputeEffectiveDensity()
    {
        if (_suppressSubdivision) return 0f;
        float multiplier = Mathf.Max(0f, _subdivisionMultiplier);
        if (multiplier == 0f) return 0f;
        var manager = GetComponentInParent<GreyboxManager>();
        float density = manager != null ? manager.VertexDensity : 0f;
        return density * multiplier;
    }

    // ─── Planar UV ───────────────────────────────────────────────

    /// <summary>
    /// When <see cref="_planarUv"/> is set, overwrites the mesh's UV0 with a flat projection onto the
    /// plane whose normal is the chosen axis, in local or world space. The two in-plane components map
    /// to (u, v), normalized so the object's bounding box on that plane fills 0–1 — with the box padded
    /// 1% on each side so the texture never tiles right at the edges. <see cref="PlanarUvMultiplier"/>
    /// scales the result (Greybox feeds its UV tile scale through it). No-op when set to None.
    /// </summary>
    void ApplyPlanarUv(Mesh mesh)
    {
        if (_planarUv == PlanarUvProjection.None) return;

        int code  = (int)_planarUv - 1; // 0..5
        int axis   = code % 3;          // 0=X, 1=Y, 2=Z plane normal
        bool world = code >= 3;         // Local (0..2) vs Global (3..5)
        int uComp  = (axis + 1) % 3;    // the two in-plane components
        int vComp  = (axis + 2) % 3;

        var verts = mesh.vertices;
        int n = verts.Length;
        if (n == 0) return;

        // Project each vertex into the chosen space and find the plane-space bounds in one pass.
        var planar = new Vector2[n];
        float uMin = float.MaxValue, uMax = float.MinValue;
        float vMin = float.MaxValue, vMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = world ? transform.TransformPoint(verts[i]) : verts[i];
            float u = p[uComp];
            float v = p[vComp];
            planar[i] = new Vector2(u, v);
            if (u < uMin) uMin = u; if (u > uMax) uMax = u;
            if (v < vMin) vMin = v; if (v > vMax) vMax = v;
        }

        // Pad the box 1% per side, then normalize so the projection fills 0–1 across it.
        float uPad = (uMax - uMin) * 0.01f;
        float vPad = (vMax - vMin) * 0.01f;
        uMin -= uPad; uMax += uPad;
        vMin -= vPad; vMax += vPad;

        float uRange = uMax - uMin;
        float vRange = vMax - vMin;
        float uInv = uRange > 1e-6f ? 1f / uRange : 0f;
        float vInv = vRange > 1e-6f ? 1f / vRange : 0f;
        float mult = PlanarUvMultiplier;

        var uvs = new Vector2[n];
        for (int i = 0; i < n; i++)
            uvs[i] = new Vector2((planar[i].x - uMin) * uInv * mult,
                                 (planar[i].y - vMin) * vInv * mult);

        mesh.uv = uvs;
    }

    // ─── Flip Faces ──────────────────────────────────────────────

    /// <summary>
    /// When <see cref="_flipFaces"/> is set, turns the mesh inside-out: reverses each triangle's
    /// winding (per submesh) and negates every normal, so the surface renders from the opposite side.
    /// No-op when off.
    /// </summary>
    void ApplyFaceFlip(Mesh mesh)
    {
        if (!_flipFaces) return;

        for (int sm = 0; sm < mesh.subMeshCount; sm++)
        {
            var tris = mesh.GetTriangles(sm);
            for (int i = 0; i < tris.Length; i += 3)
                (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            mesh.SetTriangles(tris, sm);
        }

        var normals = mesh.normals;
        for (int i = 0; i < normals.Length; i++)
            normals[i] = -normals[i];
        mesh.normals = normals;
    }

    // ─── Abstract ────────────────────────────────────────────────

    protected abstract void GenerateMesh(Mesh mesh);
    protected abstract void ResetToDefaults();

    protected virtual void OnBeforeRebuild() { }

    // ─── Mesh lifecycle (player build) ───────────────────────────
    // In the editor PrepareBakeTargets supersedes these. An asset-backed mesh (prefab-owned bake) is
    // never replaced here — the asset is the identity, valid on any instance regardless of ids.

    void EnsureMesh()
    {
        if (_mesh != null && (!string.IsNullOrEmpty(_meshOwnerGuid) || _meshOwnerId == GetInstanceID()))
            return;
        _mesh = new Mesh { name = $"{GetType().Name} Mesh" };
        _meshOwnerId = GetInstanceID();
    }

    void EnsureColliderMesh()
    {
        if (_colliderMesh != null && (!string.IsNullOrEmpty(_meshOwnerGuid) || _colliderMeshOwnerId == GetInstanceID()))
            return;
        _colliderMesh = new Mesh { name = $"{GetType().Name} Collider" };
        _colliderMeshOwnerId = GetInstanceID();
    }

#if UNITY_EDITOR
    // ─── Interactive-drag persistence deferral (editor) ──────────
    // While an editor tool is mid-drag, RebuildMesh regenerates the live mesh but skips every
    // AssetDatabase / prefab write — otherwise each tick of the drag fires a synchronous prefab
    // reimport ("Importing…" bar) that interrupts the drag. Touched primitives queue here and bake
    // + persist once when the tool ends the scope.
    static bool s_deferPersist;
    static readonly System.Collections.Generic.HashSet<GreyPrimitive> s_deferredPersist =
        new System.Collections.Generic.HashSet<GreyPrimitive>();

    /// <summary>Opens a deferral scope: until <see cref="EndDeferredPersist"/>, RebuildMesh updates
    /// only the live mesh and skips persistence. Call from an editor tool when an interactive
    /// geometry drag begins.</summary>
    public static void BeginDeferredPersist() => s_deferPersist = true;

    /// <summary>Closes the deferral scope and bakes + persists every primitive touched during it,
    /// once each. Call when the drag finishes.</summary>
    public static void EndDeferredPersist()
    {
        s_deferPersist = false;
        foreach (var gp in s_deferredPersist)
            if (gp != null) gp.RebuildMesh();
        s_deferredPersist.Clear();
    }

    /// <summary>Ensures live mesh objects exist to write geometry into, with no AssetDatabase work —
    /// existing meshes this instance owns are reused as-is and only their contents are regenerated.
    /// A mesh this instance doesn't own (a fresh duplicate still shares the source's mesh) is split
    /// off first, so a drag doesn't write geometry into the original's mesh. Used while a drag is
    /// deferred; the real bake target selection runs in <see cref="PrepareBakeTargets"/> at drag end.</summary>
    void EnsureLiveMeshTargets()
    {
        if (_mesh == null || !OwnsMesh(_meshOwnerId))
        {
            _mesh = new Mesh { name = $"{GetType().Name} Mesh" };
            _meshOwnerId = GetInstanceID();
        }
        if (UsesColliderMesh && (_colliderMesh == null || !OwnsMesh(_colliderMeshOwnerId)))
        {
            _colliderMesh = new Mesh { name = $"{GetType().Name} Collider" };
            _colliderMeshOwnerId = GetInstanceID();
        }
    }

    // A duplicate serializes the source's owner id; when it doesn't match this instance the mesh still
    // belongs to the original and must be split off. Prefab-embedded meshes (owner guid set) are shared
    // across instances by design, so they always count as owned here.
    bool OwnsMesh(int ownerId) => !string.IsNullOrEmpty(_meshOwnerGuid) || ownerId == GetInstanceID();

    // ─── Mesh persistence (editor) ───────────────────────────────

    // Bake state computed by PrepareBakeTargets, consumed by PersistBakedMeshes after generation.
    [System.NonSerialized] string _bakeContainerPath;
    [System.NonSerialized] bool _bakeEmbedMeshes;

    /// <summary>
    /// Decides, before generation, which Mesh objects this bake writes into.
    /// Plain scene object: scene-embedded meshes, recreated when the instance id doesn't match (a
    /// duplicated object splits off its own mesh) or when the current mesh is a prefab-owned asset
    /// (an unpacked/detached object must never write into the prefab's bake).
    /// Prefab-contained: writes in place into the mesh embedded in this prefab when this component
    /// owns it — no serialized change at all, so the prefab and every instance update together.
    /// Otherwise (first bake, mesh trapped in a scene file or an external asset, duplicated object
    /// or duplicated prefab) fresh meshes are created here and PersistBakedMeshes embeds them into
    /// the prefab file as sub-assets.
    /// </summary>
    void PrepareBakeTargets()
    {
        _bakeContainerPath = GetContainingPrefabPath();
        _bakeEmbedMeshes = false;

        if (_bakeContainerPath == null)
        {
            bool detach = _mesh != null && AssetDatabase.Contains(_mesh);
            if (detach || _mesh == null || _meshOwnerId != GetInstanceID())
            {
                _mesh = new Mesh { name = $"{GetType().Name} Mesh" };
                _meshOwnerId = GetInstanceID();
            }
            if (UsesColliderMesh)
            {
                bool detachCollider = _colliderMesh != null && AssetDatabase.Contains(_colliderMesh);
                if (detachCollider || _colliderMesh == null || _colliderMeshOwnerId != GetInstanceID())
                {
                    _colliderMesh = new Mesh { name = $"{GetType().Name} Collider" };
                    _colliderMeshOwnerId = GetInstanceID();
                }
            }
            _meshOwnerGuid = string.Empty;
            return;
        }

        string containerGuid = AssetDatabase.AssetPathToGUID(_bakeContainerPath);
        bool meshShared = MeshSharedWithinContainer();
        bool writeInPlace = _mesh != null
                            && AssetDatabase.GetAssetPath(_mesh) == _bakeContainerPath
                            && _meshOwnerGuid == containerGuid
                            && !meshShared;
        if (!writeInPlace)
        {
            // A superseded bake embedded in this prefab is dead weight once replaced — remove it so
            // re-bakes don't accumulate sub-assets. A mesh shared with another primitive under the
            // same root (duplicated object) belongs to the original and stays.
            if (!meshShared)
            {
                if (_mesh != null && AssetDatabase.GetAssetPath(_mesh) == _bakeContainerPath)
                    AssetDatabase.RemoveObjectFromAsset(_mesh);
                if (_colliderMesh != null && AssetDatabase.GetAssetPath(_colliderMesh) == _bakeContainerPath)
                    AssetDatabase.RemoveObjectFromAsset(_colliderMesh);
            }
            _mesh = new Mesh { name = $"{GetType().Name} Mesh" };
            _meshOwnerId = 0;
            if (UsesColliderMesh)
            {
                _colliderMesh = new Mesh { name = $"{GetType().Name} Collider" };
                _colliderMeshOwnerId = 0;
            }
            _meshOwnerGuid = containerGuid;
            _bakeEmbedMeshes = true;
        }
        else if (UsesColliderMesh && (_colliderMesh == null || !AssetDatabase.Contains(_colliderMesh)))
        {
            // The render mesh is embedded but has no collider twin yet — bake one; PersistBakedMeshes
            // embeds it into the same prefab.
            _colliderMesh = new Mesh { name = $"{GetType().Name} Collider" };
        }
    }

    /// <summary>
    /// Path of the prefab asset that owns this primitive's serialized state — the open prefab stage,
    /// or the prefab this component is an instance of. Null for plain scene objects, including
    /// objects added on top of a prefab instance (their serialized state lives in the scene file).
    /// </summary>
    string GetContainingPrefabPath()
    {
        var stage = PrefabStageUtility.GetPrefabStage(gameObject);
        if (stage != null) return stage.assetPath;

        var source = PrefabUtility.GetCorrespondingObjectFromSource(this);
        if (source != null) return AssetDatabase.GetAssetPath(source);

        if (PrefabUtility.IsPartOfPrefabAsset(this))
            return AssetDatabase.GetAssetPath(this);

        return null;
    }

    /// <summary>
    /// True when another primitive under the same prefab root references this component's render
    /// mesh — i.e. this object is a duplicate still pointing at the original's bake, and must split
    /// off to its own asset instead of overwriting the original's. Two instances of the same prefab
    /// legitimately share a mesh but are never under the same root, so they don't trip this.
    /// </summary>
    bool MeshSharedWithinContainer()
    {
        var stage = PrefabStageUtility.GetPrefabStage(gameObject);
        GameObject root = stage != null
            ? stage.prefabContentsRoot
            : PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
        if (root == null) root = transform.root.gameObject;

        foreach (var other in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
            if (other != this && other._mesh == _mesh)
                return true;
        return false;
    }

    /// <summary>
    /// Saves freshly baked meshes after generation. Scene-embedded meshes need nothing — the scene
    /// file carries them. Prefab-owned meshes are embedded into the prefab file itself as sub-assets
    /// (render mesh + collider twin): added here on a fresh bake, or just marked dirty on an
    /// in-place bake. The actual disk write happens on save — a prefab stage save serializes the
    /// embedded meshes with the file, a scene save triggers the save hook — so inspector drags
    /// never hit the disk per tick.
    /// </summary>
    void PersistBakedMeshes()
    {
        if (_bakeContainerPath == null) return;

        if (_bakeEmbedMeshes)
        {
            AssetDatabase.AddObjectToAsset(_mesh, _bakeContainerPath);
            EditorUtility.SetDirty(_mesh);
            if (UsesColliderMesh && _colliderMesh != null)
            {
                AssetDatabase.AddObjectToAsset(_colliderMesh, _bakeContainerPath);
                EditorUtility.SetDirty(_colliderMesh);
            }

            EditorUtility.SetDirty(this);
            WritePrefabAndApplyRefs();
        }
        else
        {
            EditorUtility.SetDirty(_mesh);
            if (UsesColliderMesh && _colliderMesh != null)
            {
                if (!AssetDatabase.Contains(_colliderMesh))
                {
                    AssetDatabase.AddObjectToAsset(_colliderMesh, _bakeContainerPath);
                    EditorUtility.SetDirty(this);
                    WritePrefabAndApplyRefs();
                }
                EditorUtility.SetDirty(_colliderMesh);
            }
        }
    }

    /// <summary>
    /// Persists newly embedded meshes after a bake outside a prefab stage: writes the prefab file
    /// (so the sub-assets exist on disk) and pushes the mesh references into the prefab. Inside a
    /// prefab stage this is a no-op — writing the file under the open stage would conflict with it;
    /// the stage save serializes both the sub-assets and the references.
    /// </summary>
    void WritePrefabAndApplyRefs()
    {
        if (PrefabStageUtility.GetPrefabStage(gameObject) == null)
            AssetDatabase.SaveAssetIfDirty(_mesh);
        ApplyMeshRefsToPrefab();
    }

    /// <summary>
    /// After a bake on a prefab instance in a scene, pushes the changed mesh references (and the
    /// MeshFilter/MeshCollider assignments) into the prefab asset itself instead of leaving them as
    /// instance overrides — prefab mode and every other instance see the new bake immediately. No-op
    /// in a prefab stage (the stage save serializes the fields) and for objects added on top of an
    /// instance (their state belongs to the scene).
    /// </summary>
    void ApplyMeshRefsToPrefab()
    {
        if (PrefabStageUtility.GetPrefabStage(gameObject) != null) return;
        if (PrefabUtility.GetCorrespondingObjectFromSource(this) == null) return;

        ApplyOverrideToContainer(this, "_mesh");
        ApplyOverrideToContainer(this, "_colliderMesh");
        ApplyOverrideToContainer(this, "_meshOwnerGuid");

        var mf = GetComponent<MeshFilter>();
        if (mf != null && PrefabUtility.GetCorrespondingObjectFromSource(mf) != null)
            ApplyOverrideToContainer(mf, "m_Mesh");

        var mc = GetComponent<MeshCollider>();
        if (mc != null && PrefabUtility.GetCorrespondingObjectFromSource(mc) != null)
            ApplyOverrideToContainer(mc, "m_Mesh");
    }

    void ApplyOverrideToContainer(Object obj, string propertyName)
    {
        var so = new SerializedObject(obj);
        var prop = so.FindProperty(propertyName);
        if (prop == null)
        {
            Debug.LogError($"[Greybox] '{name}' — no serialized property '{propertyName}' on {obj.GetType().Name}.");
            return;
        }
        if (!prop.prefabOverride) return; // already matches the prefab — nothing to push
        PrefabUtility.ApplyPropertyOverride(prop, _bakeContainerPath, InteractionMode.AutomatedAction);
    }
#endif
}
