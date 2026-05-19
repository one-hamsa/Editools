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
    int  _cachedSignature = int.MinValue;
    bool _rebuildPending;

    protected void SetRebuildPending() => _rebuildPending = true;

    /// <summary>
    /// Push hook for GreyboxManager: when manager-level settings change, the manager calls
    /// this on each child primitive to trigger a rebuild on the next editor Update tick.
    /// </summary>
    public void MarkRebuildPending() => _rebuildPending = true;
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
        int sig = ComputeRebuildSignature();
        if (_rebuildPending || sig != _cachedSignature)
        {
            _rebuildPending = false;
            _cachedSignature = sig;
            EnsureMesh();
            RebuildMesh();
        }
    }

    /// <summary>
    /// Hash of every input that affects mesh output. The base mixes in the manager state,
    /// transform scale, and subdivision multiplier. Subclasses override to mix in their own
    /// shape data so any inspector change triggers a rebuild on the next editor tick.
    /// </summary>
    int ComputeRebuildSignature()
    {
        var manager = GetComponentInParent<GreyboxManager>();
        int sig = 17;
        unchecked
        {
            sig = sig * 31 + (manager != null ? manager.GetInstanceID() : 0);
            sig = sig * 31 + (manager != null ? manager.VertexDensity.GetHashCode() : 0);
            sig = sig * 31 + (manager != null ? manager.GreypipeLengthSubdivMultiplier.GetHashCode() : 0);
            sig = sig * 31 + (manager != null ? manager.GreypipeGirthSubdivMultiplier.GetHashCode() : 0);
            sig = sig * 31 + (manager != null ? manager.GreyroadLengthSubdivMultiplier.GetHashCode() : 0);
            sig = sig * 31 + (manager != null ? manager.GreyroadWidthSubdivMultiplier.GetHashCode() : 0);
            sig = sig * 31 + (manager != null ? manager.GreyroadSideSubdivMultiplier.GetHashCode() : 0);
            sig = sig * 31 + transform.lossyScale.GetHashCode();
            sig = sig * 31 + _subdivisionMultiplier.GetHashCode();
            sig = sig * 31 + GetSubclassRebuildSignature();
        }
        return sig;
    }
#endif

    /// <summary>
    /// Subclasses mix in any local field that affects mesh output so that inspector edits
    /// are reflected without requiring an explicit OnValidate call site.
    /// </summary>
    protected virtual int GetSubclassRebuildSignature() => 0;

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
