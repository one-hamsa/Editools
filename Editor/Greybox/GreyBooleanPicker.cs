#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot scene-view picker for the Boolean "Pick" button. After Pick is pressed, the next
/// left-click in the scene on a Grey object assigns it as the Subject's Operator, then exits.
/// This complements the drag-and-drop reference field — it does not replace it.
/// </summary>
static class GreyBooleanPicker
{
    static GreyPrimitive s_subject;

    public static bool IsPicking => s_subject != null;
    public static GreyPrimitive PickingSubject => s_subject;

    public static void Begin(GreyPrimitive subject)
    {
        s_subject = subject;
        SceneView.duringSceneGui -= OnSceneGui;
        SceneView.duringSceneGui += OnSceneGui;
        SceneView.RepaintAll();
    }

    static void Cancel()
    {
        SceneView.duringSceneGui -= OnSceneGui;
        s_subject = null;
        SceneView.RepaintAll();
    }

    static void OnSceneGui(SceneView sv)
    {
        if (s_subject == null) { Cancel(); return; }

        Handles.BeginGUI();
        GUI.Label(new Rect(8, 8, 460, 20),
            $"Pick an Operator for '{s_subject.name}'  —  click a Grey object, Esc to cancel",
            EditorStyles.whiteLargeLabel);
        Handles.EndGUI();

        Event e = Event.current;

        // Hold the default control so a click doesn't change the selection instead of picking.
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            Cancel();
            e.Use();
            return;
        }

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            var subject = s_subject;
            var go = HandleUtility.PickGameObject(e.mousePosition, false);
            var picked = go != null ? go.GetComponentInParent<GreyPrimitive>() : null;

            if (picked != null && picked != subject)
                AssignOperator(subject, picked);
            else if (picked == subject)
                Debug.LogWarning("[GreyBoolean] Can't use the Subject as its own Operator.");

            e.Use();
            Cancel();
        }
    }

    static void AssignOperator(GreyPrimitive subject, GreyPrimitive op)
    {
        var so = new SerializedObject(subject);
        so.Update();
        so.FindProperty("_booleanOperator").objectReferenceValue = op;
        so.ApplyModifiedProperties(); // records undo
        GreyBooleanOrchestrator.Sync(subject);
    }
}
#endif
