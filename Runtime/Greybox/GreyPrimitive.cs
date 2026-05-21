using UnityEngine;

/// <summary>
/// Base class for grey primitives. The MonoBehaviour is inert: it owns the serialized mesh
/// and exposes <see cref="RebuildMesh"/>, but no Unity lifecycle callback ever regenerates.
/// Rebuilds are triggered exclusively from editor code (custom inspectors, scene tools).
/// </summary>
[ExecuteAlways]
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

    // ─── Mesh ───────────────────────────────────────────────────

    [SerializeField, HideInInspector]
    Mesh _mesh;
    [SerializeField, HideInInspector]
    int _meshOwnerId;
    protected Mesh SharedMesh => _mesh;

#if UNITY_EDITOR
    [System.NonSerialized] bool _meshIsLive;
    public bool MeshIsLive => _meshIsLive;

    /// <summary>Called by the scene-save hook after the in-memory mesh is persisted.</summary>
    public void NotifyMeshSaved() => _meshIsLive = false;
#endif

    // ─── Unity lifecycle ─────────────────────────────────────────
    //
    // Intentionally inert. We do NOT rebuild in OnEnable, OnValidate, or Update. The only
    // thing OnEnable does is re-bind the serialized mesh to MeshFilter / MeshCollider if
    // the references were dropped — assignment only, never regeneration.

    protected virtual void OnEnable()
    {
        if (_mesh == null) return;
        var mf = GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != _mesh) mf.sharedMesh = _mesh;
        var mc = GetComponent<MeshCollider>();
        if (mc != null && mc.sharedMesh != _mesh) mc.sharedMesh = _mesh;
    }

#if UNITY_EDITOR
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

        var mf = GetComponent<MeshFilter>();
        if (mf != null) mf.sharedMesh = _mesh;

        var mc = GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = _mesh;
    }

    // ─── Subdivision ─────────────────────────────────────────────

    protected float ComputeEffectiveDensity()
    {
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
}
