using UnityEngine;

/// <summary>
/// Base class for grey primitives. Owns the serialized mesh and exposes <see cref="RebuildMesh"/>;
/// no runtime Unity callback regenerates. In edit mode, <see cref="OnValidate"/> rebuilds when the
/// serialized hash of mesh-defining params (see <see cref="ComputeMeshHash"/>) no longer matches
/// — so changing an inspector field on the primitive, or on the parent <see cref="GreyboxManager"/>
/// (whose multipliers feed into the hash), triggers exactly one rebuild on the next OnValidate tick.
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

    // ─── Mesh ───────────────────────────────────────────────────

    [SerializeField, HideInInspector]
    Mesh _mesh;
    [SerializeField, HideInInspector]
    int _meshOwnerId;
    [SerializeField, HideInInspector]
    int _builtHash;
    protected Mesh SharedMesh => _mesh;

#if UNITY_EDITOR
    [System.NonSerialized] bool _meshIsLive;
    public bool MeshIsLive => _meshIsLive;

    /// <summary>Called by the scene-save hook after the in-memory mesh is persisted.</summary>
    public void NotifyMeshSaved() => _meshIsLive = false;
#endif

    // ─── Unity lifecycle ─────────────────────────────────────────

#if UNITY_EDITOR
    protected virtual void Reset()
    {
        ResetToDefaults();
        EnsureMesh();
        RebuildMesh();
    }

    /// <summary>
    /// Rebinds the serialized mesh to MeshFilter/MeshCollider if references drifted, then
    /// rebuilds when mesh-defining params changed since the last build. Both branches are
    /// no-ops in the steady state (no ref drift, hash matches).
    /// </summary>
    protected virtual void OnValidate()
    {
        if (_mesh != null)
        {
            var mf = GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != _mesh) mf.sharedMesh = _mesh;
            var mc = GetComponent<MeshCollider>();
            if (mc != null && mc.sharedMesh != _mesh) mc.sharedMesh = _mesh;
        }

        if (ComputeMeshHash() != _builtHash)
            RebuildMesh();
    }

    /// <summary>
    /// Hash of every input that influences <see cref="GenerateMesh"/>. Subclasses MUST override
    /// and fold in their own serialized fields and any inherited <see cref="GreyboxManager"/>
    /// multipliers they read. <see cref="OnValidate"/> uses this to skip rebuilds when nothing
    /// has changed since the last build.
    /// </summary>
    protected virtual int ComputeMeshHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + _subdivisionMultiplier.GetHashCode();
            h = h * 31 + ComputeEffectiveDensity().GetHashCode();
            return h;
        }
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

#if UNITY_EDITOR
        _builtHash = ComputeMeshHash();
#endif
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
