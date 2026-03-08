using UnityEngine;

/// <summary>
/// Per-project configuration for the QuickTransform editor tool.
/// Create via Assets → Create → BlockBuster → Quick Transform Config.
/// If no asset exists in the project, QuickTransform uses built-in defaults.
/// </summary>
[CreateAssetMenu(fileName = "QuickTransformConfig", menuName = "BlockBuster/Quick Transform Config")]
public class QuickTransformConfig : ScriptableObject
{
    [Header("Axes")]
    [Tooltip("World-space up axis. Defines the movement plane (perpendicular to this) and the rotation axis.")]
    public Vector3 upAxis = Vector3.up;

    [Header("Interaction")]
    [Tooltip("When true, holding W/E/R and moving the mouse begins dragging immediately — no click required. " +
             "Releasing the key commits the transform.")]
    public bool clickFreeDrag = false;
}
