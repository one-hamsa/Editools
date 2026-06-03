using UnityEngine;

/// <summary>
/// Place anywhere above grey primitives (Greybox, Greypipe, Greyroad) in the hierarchy to
/// control their adaptive subdivision density. All primitives under this object poll these
/// values and subdivide to match. Changes to this component push <see cref="GreyPrimitive.RebuildMesh"/>
/// to every descendant primitive via <see cref="PushRebuildToChildren"/>, called from the custom
/// inspector's change-check, and from its undo handler only when an undo/redo actually changed one of
/// this manager's values (detected via <see cref="ComputeDensitySignature"/>).
/// </summary>
public class GreyboxManager : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Enable or disable adaptive subdivision for all grey primitives in this hierarchy.")]
    bool _subdivisionEnabled = true;

    [SerializeField]
    [Tooltip("Target vertex density in vertices per meter. Applied to all grey primitives " +
             "in this hierarchy when subdivision is enabled.")]
    float _vertexDensity = 1f;

    [SerializeField]
    [Tooltip("Global multiplier for length-axis subdivisions on Greypipes (rings along the spline). " +
             "Stacks with each pipe's local Length Subdiv Multiplier. 1 = no extra change.")]
    float _greypipeLengthSubdivMultiplier = 1f;

    [SerializeField]
    [Tooltip("Global multiplier for circumference subdivisions on Greypipes (sides of the tube). " +
             "Stacks with each pipe's local Girth Subdiv Multiplier. 1 = no extra change.")]
    float _greypipeGirthSubdivMultiplier = 1f;

    [SerializeField]
    [Tooltip("Global multiplier for length-axis subdivisions on Greyroads (rings along the spline). " +
             "Stacks with each road's local Length Subdiv Multiplier. 1 = no extra change.")]
    float _greyroadLengthSubdivMultiplier = 1f;

    [SerializeField]
    [Tooltip("Global multiplier for top/bottom-edge subdivisions on Greyroads. " +
             "Stacks with each road's local Width Subdiv Multiplier. 1 = no extra change.")]
    float _greyroadWidthSubdivMultiplier = 1f;

    [SerializeField]
    [Tooltip("Global multiplier for left/right side-edge subdivisions on Greyroads. " +
             "Stacks with each road's local Side Subdiv Multiplier. 1 = no extra change.")]
    float _greyroadSideSubdivMultiplier = 1f;

    [SerializeField]
    [Range(0f, 1f)]
    [Tooltip("Global edge smoothing for Greyroads — relaxes how banking, width and height " +
             "transition between handles. 0 = stiff (each transition eases to a flat stop at " +
             "every handle), 1 = fully relaxed flow through handles. Never overshoots, and the " +
             "value is always exact at each handle. Stacks (multiplies) with each road's local " +
             "Edge Smoothing.")]
    float _greyroadEdgeSmoothing = 1f;

    public float VertexDensity => _subdivisionEnabled ? Mathf.Max(0f, _vertexDensity) : 0f;

    public float GreypipeLengthSubdivMultiplier => Mathf.Max(0.01f, _greypipeLengthSubdivMultiplier);
    public float GreypipeGirthSubdivMultiplier  => Mathf.Max(0.01f, _greypipeGirthSubdivMultiplier);

    public float GreyroadLengthSubdivMultiplier => Mathf.Max(0.01f, _greyroadLengthSubdivMultiplier);
    public float GreyroadWidthSubdivMultiplier  => Mathf.Max(0.01f, _greyroadWidthSubdivMultiplier);
    public float GreyroadSideSubdivMultiplier   => Mathf.Max(0.01f, _greyroadSideSubdivMultiplier);

    public float GreyroadEdgeSmoothing => Mathf.Clamp01(_greyroadEdgeSmoothing);

#if UNITY_EDITOR
    public void PushRebuildToChildren()
    {
        foreach (var prim in GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
            prim.RebuildMesh();
    }

    /// <summary>
    /// Hash of the serialized values that drive descendant subdivision. The manager's editor caches
    /// this and, on undo/redo, only re-pushes a rebuild to children when it changed — so an undo
    /// unrelated to the manager costs one hash instead of rebuilding every descendant.
    /// </summary>
    public int ComputeDensitySignature()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + _subdivisionEnabled.GetHashCode();
            h = h * 31 + _vertexDensity.GetHashCode();
            h = h * 31 + _greypipeLengthSubdivMultiplier.GetHashCode();
            h = h * 31 + _greypipeGirthSubdivMultiplier.GetHashCode();
            h = h * 31 + _greyroadLengthSubdivMultiplier.GetHashCode();
            h = h * 31 + _greyroadWidthSubdivMultiplier.GetHashCode();
            h = h * 31 + _greyroadSideSubdivMultiplier.GetHashCode();
            h = h * 31 + _greyroadEdgeSmoothing.GetHashCode();
            return h;
        }
    }
#endif
}
