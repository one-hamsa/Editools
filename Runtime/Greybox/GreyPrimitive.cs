using UnityEngine;

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

    Mesh _mesh;
    protected Mesh SharedMesh => _mesh;

    // ─── Edit-time dirty tracking ────────────────────────────────

#if UNITY_EDITOR
    GreyboxManager _cachedManager;
    float          _cachedDensity  = -1f;
    Vector3        _cachedScale;
    bool           _rebuildPending;

    protected void SetRebuildPending() => _rebuildPending = true;
#endif

    // ─── Unity lifecycle ─────────────────────────────────────────

    protected virtual void OnEnable()
    {
        EnsureMesh();
        RebuildMesh();
    }

    protected virtual void OnValidate()
    {
#if UNITY_EDITOR
        _rebuildPending = true;
#else
        EnsureMesh();
        RebuildMesh();
#endif
    }

#if UNITY_EDITOR
    protected virtual void Reset()
    {
        ResetToDefaults();
        EnsureMesh();
        RebuildMesh();
    }

    protected virtual void Update()
    {
        if (_rebuildPending)
        {
            _rebuildPending = false;
            EnsureMesh();
            RebuildMesh();
            _cachedManager = GetComponentInParent<GreyboxManager>();
            _cachedDensity = _cachedManager != null ? _cachedManager.VertexDensity : 0f;
            _cachedScale   = transform.lossyScale;
            return;
        }

        var   manager = GetComponentInParent<GreyboxManager>();
        float density = manager != null ? manager.VertexDensity : 0f;
        var   scale   = transform.lossyScale;

        if (manager == _cachedManager && density == _cachedDensity && scale == _cachedScale)
            return;

        _cachedManager = manager;
        _cachedDensity = density;
        _cachedScale   = scale;
        RebuildMesh();
    }
#endif

    // ─── Public API ──────────────────────────────────────────────

    public void RebuildMesh()
    {
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
        if (_mesh != null) return;
        _mesh = new Mesh { name = $"{GetType().Name} Mesh", hideFlags = HideFlags.HideAndDontSave };
    }
}
