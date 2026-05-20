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

    [SerializeField, HideInInspector]
    Mesh _mesh;
    [SerializeField, HideInInspector]
    int _meshOwnerId;
    protected Mesh SharedMesh => _mesh;

    // ─── Edit-time dirty tracking ────────────────────────────────

#if UNITY_EDITOR
    int  _cachedSignature = int.MinValue;
    bool _rebuildPending;
    [System.NonSerialized] bool _meshIsLive;
    public bool MeshIsLive => _meshIsLive;

    protected void SetRebuildPending() => _rebuildPending = true;

    /// <summary>
    /// Push hook for GreyboxManager: when manager-level settings change, the manager calls
    /// this on each child primitive to trigger a rebuild on the next editor Update tick.
    /// Skipped when the serialized mesh is already in sync with the current signature so
    /// that the manager's OnValidate firing on scene load doesn't trigger spurious rebuilds.
    /// </summary>
    public void MarkRebuildPending()
    {
        if (_mesh != null && _mesh.vertexCount > 0 && _cachedSignature == ComputeRebuildSignature())
            return;
        _rebuildPending = true;
    }

    /// <summary>
    /// Called by the scene-save hook. The on-disk mesh now matches the in-memory mesh,
    /// so the indicator should flip back to "Serialized".
    /// </summary>
    public void NotifyMeshSaved() => _meshIsLive = false;
#endif

    // ─── Unity lifecycle ─────────────────────────────────────────

    protected virtual void OnEnable()
    {
        if (_mesh != null && _mesh.vertexCount > 0)
        {
            var mf = GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = _mesh;
            var mc = GetComponent<MeshCollider>();
            if (mc != null) mc.sharedMesh = _mesh;
#if UNITY_EDITOR
            _cachedSignature = ComputeRebuildSignature();
#endif
            return;
        }
        EnsureMesh();
        RebuildMesh();
    }

    protected virtual void OnValidate()
    {
#if UNITY_EDITOR
        if (_mesh != null && _mesh.vertexCount > 0 && _cachedSignature == ComputeRebuildSignature())
            return;
        _rebuildPending = true;
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
