#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot scene-view picker for the Greybox "Link" Pick button. After Pick is pressed, the next
/// left-click in the scene on a greybox welds the subject to it (the subject's nearest face snaps
/// onto the clicked box — see <see cref="GreyboxSeamSolver.LinkBoxes"/>), then exits. Complements
/// the drag-and-drop Link field; mirrors <c>GreyBooleanPicker</c>.
/// </summary>
static class GreyboxSeamPicker
{
    static Greybox s_subject;

    public static bool IsPicking => s_subject != null;
    public static Greybox PickingSubject => s_subject;

    public static void Begin(Greybox subject)
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
        GUI.Label(new Rect(8, 8, 480, 20),
            $"Pick a box to link '{s_subject.name}' to  —  click a greybox, Esc to cancel",
            EditorStyles.whiteLargeLabel);
        Handles.EndGUI();

        Event e = Event.current;

        // Hold the default control so a click picks instead of changing the selection.
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
            var picked = go != null ? go.GetComponentInParent<Greybox>() : null;

            if (picked != null && picked != subject)
                GreyboxSeamSolver.LinkBoxes(subject, picked);
            else if (picked == subject)
                Debug.LogWarning("[Greybox] Can't link a box to itself.");

            e.Use();
            Cancel();
        }
    }
}
#endif
