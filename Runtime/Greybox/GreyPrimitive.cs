using UnityEngine;

/// <summary>
/// Base class for grey primitives. Owns the serialized mesh and exposes <see cref="RebuildMesh"/>;
/// rebuilds run only when explicitly triggered — inspector change-check, scene tools, undo/redo,
/// the "Rebuild Mesh" button, or <see cref="Reset"/>. Nothing rebuilds on load, domain reload, or
/// any other Unity callback.
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

    /// <summary>Called by the scene-save hook after the in-memory mesh is persisted.</summary>
    public void NotifyMeshSaved() => _meshIsLive = false;

    protected virtual void Reset()
    {
        ResetToDefaults();
        EnsureMesh();
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

        EnsureMesh();
        GenerateMesh(_mesh);
        _mesh.RecalculateBounds();

        // Types that opt in (Greybox, GreyBooleanResult) bake an unsubdivided collider twin from the
        // same inputs and feed it to the MeshCollider. Others (Greypipe, Greyroad) just point the
        // collider at the render mesh — no separate bake.
        Mesh colliderMesh = _mesh;
        if (UsesColliderMesh)
        {
            EnsureColliderMesh();
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
    }

#if UNITY_EDITOR
    /// <summary>Injects the (freshly baked) collider mesh into a MeshCollider on this object — the
    /// "Set Collider" button, for when a collider is added manually after the mesh was built.</summary>
    public void ApplyColliderMesh()
    {
        if (_colliderMesh == null || _colliderMeshOwnerId != GetInstanceID())
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

    // ─── Mesh lifecycle ───────────────────────────────────────────

    void EnsureMesh()
    {
        if (_mesh != null && _meshOwnerId == GetInstanceID()) return;
        _mesh = new Mesh { name = $"{GetType().Name} Mesh" };
        _meshOwnerId = GetInstanceID();
    }

    void EnsureColliderMesh()
    {
        if (_colliderMesh != null && _colliderMeshOwnerId == GetInstanceID()) return;
        _colliderMesh = new Mesh { name = $"{GetType().Name} Collider" };
        _colliderMeshOwnerId = GetInstanceID();
    }
}
