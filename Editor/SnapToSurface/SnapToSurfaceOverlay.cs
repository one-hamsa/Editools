#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Floating Scene View panel shown while Snap To Surface placement is active (Alt+A).
/// Mirrors the placement settings — Align Z and Surface Offset — and adds Flip Axis,
/// so they can be changed mid-placement without leaving snap mode.
///
/// Because it's a UIElements overlay, clicks on it are consumed by the overlay and
/// never reach the scene-view handler that confirms/cancels the snap — so tweaking a
/// setting here can't end placement; only a click (or right-click) in the viewport
/// does. Content is drawn with IMGUI to match the rest of Editools. Each SceneView
/// gets its own instance; visibility tracks <see cref="SnapToSurface.IsSnapping"/> via
/// <see cref="SnapToSurface.SnapModeChanged"/>.
/// </summary>
[Overlay(typeof(SceneView), k_Id, "Snap To Surface")]
class SnapToSurfaceOverlay : Overlay
{
    public const string k_Id = "snap-to-surface-window";

    static readonly GUIContent k_AlignZ = new GUIContent("Align Z",
        "Align the object's Z+ (forward) axis to the surface normal instead of Y+ (up).");
    static readonly GUIContent k_Offset = new GUIContent("Surface Offset",
        "Distance to push the object off the surface along its normal. 0 sits flush; positive lifts away, negative sinks in.");
    static readonly GUIContent k_Flip = new GUIContent("Flip Axis",
        "Point the aligned axis into the surface instead of out of it (flips 180° about the surface tangent).");

    public override VisualElement CreatePanelContent()
    {
        var root = new VisualElement { style = { minWidth = 200 } };
        root.Add(new IMGUIContainer(DrawGUI));
        return root;
    }

    public override void OnCreated()
    {
        SnapToSurface.SnapModeChanged += UpdateVisibility;
        UpdateVisibility();
    }

    public override void OnWillBeDestroyed()
    {
        SnapToSurface.SnapModeChanged -= UpdateVisibility;
    }

    void UpdateVisibility() => displayed = SnapToSurface.IsSnapping;

    static void DrawGUI()
    {
        if (!SnapToSurface.IsSnapping) return;

        EditorGUIUtility.labelWidth = 96f;

        EditorGUI.BeginChangeCheck();
        bool  alignZ = EditorGUILayout.Toggle(k_AlignZ, SnapToSurface.AlignZToSurface);
        float offset = EditorGUILayout.FloatField(k_Offset, SnapToSurface.SurfaceOffset);
        bool  flip   = EditorGUILayout.Toggle(k_Flip, SnapToSurface.FlipAxis);
        if (EditorGUI.EndChangeCheck())
        {
            SnapToSurface.AlignZToSurface = alignZ;
            SnapToSurface.SurfaceOffset   = offset;
            SnapToSurface.FlipAxis        = flip;
            SnapToSurface.ReapplyPlacement();
        }
    }
}
#endif
