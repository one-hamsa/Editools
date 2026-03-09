using UnityEngine;

/// <summary>
/// Per-project configuration for the QuickTransform editor tool.
/// Create via Assets → Create → BlockBuster → Quick Transform Config.
/// If no asset exists in the project, QuickTransform uses built-in defaults.
///
/// Most settings are stored in EditorPrefs (per-user, git-ignored) and
/// editable via the Editools toolbar dropdown. This ScriptableObject only
/// holds the project-level up-axis setting.
/// </summary>
[CreateAssetMenu(fileName = "QuickTransformConfig", menuName = "BlockBuster/Quick Transform Config")]
public class QuickTransformConfig : ScriptableObject
{
    [Header("Axes")]
    [Tooltip("World-space up axis. Defines the movement plane (perpendicular to this) and the rotation axis.")]
    public Vector3 upAxis = Vector3.up;
}
