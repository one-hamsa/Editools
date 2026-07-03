#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Floating Scene View panel with X / Y / Z buttons that reorient the selected objects:
/// each click rotates an object 90° clockwise around its own local axis and swaps the
/// localScale of the two other axes, so the object keeps its world-space proportions
/// while the mesh turns "inside" them. Shown/hidden via the Editools Settings toggle.
/// </summary>
[Overlay(typeof(SceneView), k_Id, "Change Orientation")]
class ChangeOrientationOverlay : Overlay
{
    public const string k_Id = "change-orientation";

    const string k_EnabledPref = "Editools_ChangeOrientation_Enabled";

    // notifies live overlay instances when the Settings toggle flips
    static event System.Action s_onEnabledChanged;

    internal static bool Enabled
    {
        get => EditorPrefs.GetBool(k_EnabledPref, false);
        set
        {
            if (value == Enabled) return;
            EditorPrefs.SetBool(k_EnabledPref, value);
            s_onEnabledChanged?.Invoke();
        }
    }

    static readonly GUIContent k_XAxis = new GUIContent("X Axis",
        "Rotate the selection 90° clockwise around each object's local X axis (swaps Y/Z scale).");
    static readonly GUIContent k_YAxis = new GUIContent("Y Axis",
        "Rotate the selection 90° clockwise around each object's local Y axis (swaps X/Z scale).");
    static readonly GUIContent k_ZAxis = new GUIContent("Z Axis",
        "Rotate the selection 90° clockwise around each object's local Z axis (swaps X/Y scale).");

    IMGUIContainer _content;

    // ─── Lifecycle ──────────────────────────────────────────────

    public override VisualElement CreatePanelContent()
    {
        var root = new VisualElement { style = { minWidth = 168 } };
        _content = new IMGUIContainer(DrawGUI);
        root.Add(_content);
        return root;
    }

    public override void OnCreated()
    {
        s_onEnabledChanged += UpdateVisibility;
        Selection.selectionChanged += OnSelectionChanged;
        UpdateVisibility();
    }

    public override void OnWillBeDestroyed()
    {
        Selection.selectionChanged -= OnSelectionChanged;
        s_onEnabledChanged -= UpdateVisibility;
    }

    void UpdateVisibility() => displayed = Enabled;

    void OnSelectionChanged() => _content?.MarkDirtyRepaint();

    // ─── GUI ────────────────────────────────────────────────────

    static void DrawGUI()
    {
        using (new EditorGUI.DisabledScope(Selection.transforms.Length == 0))
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawAxisButton(k_XAxis, 0, new Color(0.9f, 0.45f, 0.45f, 1f));
            DrawAxisButton(k_YAxis, 1, new Color(0.5f, 0.85f, 0.5f, 1f));
            DrawAxisButton(k_ZAxis, 2, new Color(0.5f, 0.65f, 0.95f, 1f));
        }
    }

    static void DrawAxisButton(GUIContent label, int axis, Color tint)
    {
        var bg = GUI.backgroundColor;
        GUI.backgroundColor = tint;
        if (GUILayout.Button(label, GUILayout.Height(24)))
            Reorient(axis);
        GUI.backgroundColor = bg;
    }

    // ─── Reorient ───────────────────────────────────────────────

    /// <summary>
    /// Rotate each selected transform 90° clockwise around its local axis (0=X, 1=Y, 2=Z)
    /// and swap the localScale of the two other axes. A 90° turn exchanges the roles of
    /// those axes, so the swap keeps the object's world-space envelope unchanged.
    /// </summary>
    static void Reorient(int axis)
    {
        Vector3 axisVec = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
        (int a, int b) = axis == 0 ? (1, 2) : axis == 1 ? (0, 2) : (0, 1);

        foreach (var t in Selection.transforms)
        {
            Undo.RecordObject(t, "Change Orientation");
            t.localRotation *= Quaternion.AngleAxis(90f, axisVec);
            Vector3 s = t.localScale;
            (s[a], s[b]) = (s[b], s[a]);
            t.localScale = s;
        }
    }
}
#endif
