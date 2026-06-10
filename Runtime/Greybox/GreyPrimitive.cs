using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Base class for grey primitives. Owns the serialized mesh and exposes <see cref="RebuildMesh"/>;
/// rebuilds run only when explicitly triggered — inspector change-check, scene tools, undo/redo,
/// the "Rebuild Mesh" button, or <see cref="Reset"/>. Nothing rebuilds on load, domain reload, or
/// any other Unity callback.
///
/// Mesh persistence: a primitive on a plain scene object keeps its meshes scene-embedded (serialized
/// into the scene file). A primitive that belongs to a prefab bakes into a mesh .asset file beside
/// the owning prefab instead — a scene-embedded mesh can't be carried by a prefab file, and the
/// shared asset means the prefab and every scene instance reference the same bake, so rebaking from
/// anywhere updates all of them.
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

    // GUID of the prefab asset whose mesh .asset file owns the baked meshes; empty when the meshes
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

    /// <summary>Called by the save hook after a scene/prefab save. Writes the prefab-owned mesh
    /// asset to disk if dirty — the scene/prefab save itself doesn't touch that separate .asset
    /// file. Scene-embedded meshes are persisted by the scene save, so they need nothing here.</summary>
    public void NotifyMeshSaved()
    {
        if (!string.IsNullOrEmpty(_meshOwnerGuid) && _mesh != null)
            AssetDatabase.SaveAssetIfDirty(_mesh);
        _meshIsLive = false;
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
        PrepareBakeTargets();
#else
        EnsureMesh();
#endif
        GenerateMesh(_mesh);
        _mesh.RecalculateBounds();

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
        PersistBakedMeshes();
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
    // ─── Mesh persistence (editor) ───────────────────────────────

    // Bake state computed by PrepareBakeTargets, consumed by PersistBakedMeshes after generation.
    [System.NonSerialized] string _bakeContainerPath;
    [System.NonSerialized] bool _bakeCreateAsset;

    /// <summary>
    /// Decides, before generation, which Mesh objects this bake writes into.
    /// Plain scene object: scene-embedded meshes, recreated when the instance id doesn't match (a
    /// duplicated object splits off its own mesh) or when the current mesh is a prefab-owned asset
    /// (an unpacked/detached object must never write into the prefab's bake).
    /// Prefab-contained: writes in place into the existing mesh asset when this component owns it —
    /// no serialized change at all, so the prefab and every instance update together. Otherwise
    /// (first bake, mesh trapped in a scene file, duplicated object or duplicated prefab) fresh
    /// meshes are created here and PersistBakedMeshes saves them to a new asset file.
    /// </summary>
    void PrepareBakeTargets()
    {
        _bakeContainerPath = GetContainingPrefabPath();
        _bakeCreateAsset = false;

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
        bool writeInPlace = _mesh != null
                            && AssetDatabase.Contains(_mesh)
                            && _meshOwnerGuid == containerGuid
                            && !MeshSharedWithinContainer();
        if (!writeInPlace)
        {
            _mesh = new Mesh { name = $"{GetType().Name} Mesh" };
            _meshOwnerId = 0;
            if (UsesColliderMesh)
            {
                _colliderMesh = new Mesh { name = $"{GetType().Name} Collider" };
                _colliderMeshOwnerId = 0;
            }
            _meshOwnerGuid = containerGuid;
            _bakeCreateAsset = true;
        }
        else if (UsesColliderMesh && (_colliderMesh == null || !AssetDatabase.Contains(_colliderMesh)))
        {
            // The asset file exists but has no collider twin yet — bake one; PersistBakedMeshes
            // adds it to the same file.
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
    /// file carries them. Prefab-owned meshes live in "&lt;prefab name&gt; GreyMeshes/&lt;object
    /// name&gt;.asset" beside the prefab (collider twin as a sub-asset): created here on a fresh
    /// bake, or just marked dirty on an in-place bake — the save hook writes dirty mesh assets to
    /// disk on scene/prefab save, so inspector drags never hit the disk per tick.
    /// </summary>
    void PersistBakedMeshes()
    {
        if (_bakeContainerPath == null) return;

        if (_bakeCreateAsset)
        {
            string dir = System.IO.Path.GetDirectoryName(_bakeContainerPath).Replace('\\', '/');
            string folderName = $"{System.IO.Path.GetFileNameWithoutExtension(_bakeContainerPath)} GreyMeshes";
            string folder = $"{dir}/{folderName}";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(dir, folderName);

            string fileName = string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars()));
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}.asset");
            AssetDatabase.CreateAsset(_mesh, path);
            if (UsesColliderMesh && _colliderMesh != null)
            {
                AssetDatabase.AddObjectToAsset(_colliderMesh, _mesh);
                AssetDatabase.ImportAsset(path);
            }

            EditorUtility.SetDirty(this);
            ApplyMeshRefsToPrefab();
        }
        else
        {
            EditorUtility.SetDirty(_mesh);
            if (UsesColliderMesh && _colliderMesh != null)
            {
                if (!AssetDatabase.Contains(_colliderMesh))
                {
                    AssetDatabase.AddObjectToAsset(_colliderMesh, _mesh);
                    AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(_mesh));
                    EditorUtility.SetDirty(this);
                    ApplyMeshRefsToPrefab();
                }
                EditorUtility.SetDirty(_colliderMesh);
            }
        }
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
