using UnityEngine;

/// <summary>
/// Place anywhere above Greybox objects in the hierarchy to control their
/// adaptive subdivision density. All Greybox components under this object
/// poll this value and subdivide to match the target density.
/// </summary>
[ExecuteAlways]
public class GreyboxManager : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Enable or disable adaptive subdivision for all Greybox components in this hierarchy.")]
    bool _subdivisionEnabled = true;

    [SerializeField]
    [Tooltip("Target vertex density in vertices per meter. Applied to all Greybox components " +
             "in this hierarchy when subdivision is enabled.")]
    float _vertexDensity = 1f;

    public float VertexDensity => _subdivisionEnabled ? Mathf.Max(0f, _vertexDensity) : 0f;
}
