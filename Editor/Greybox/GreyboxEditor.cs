using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Greybox inspector. Extends the shared <see cref="GreyPrimitiveEditor"/> (mesh-state label,
/// rebuild-on-change, Rebuild button, Boolean sync) and appends an Unlink button for seam-linked
/// boxes (created by QuickTransform's RMB extrude). The link is a plain reference with no hierarchy
/// dependence, so the only way to sever it is explicit — this button.
/// </summary>
[CustomEditor(typeof(Greybox))]
[CanEditMultipleObjects]
public class GreyboxEditor : GreyPrimitiveEditor
{
    static readonly GUIContent s_linkLabel = new GUIContent(
        "Link",
        "Drop a greybox here to weld this box's nearest face onto it: snaps the shared corners, " +
        "hides the seam, and keeps them welded as either box is edited. Use to link boxes that " +
        "weren't created with linked-extrude.");

    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();

        if (targets.Length != 1) return;   // linking/unlinking is a single-object action
        var gb = (Greybox)target;

        EditorGUILayout.Space();

        // Show existing welds while still allowing another group to be linked.
        if (gb.HasSeam)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
                gb.GetSeams(seams);
                EditorGUILayout.LabelField($"Linked faces: {seams.Count}", EditorStyles.miniLabel);
                if (GUILayout.Button("Unlink", GUILayout.Width(70f)))
                {
                    Undo.IncrementCurrentGroup();
                    int undoGroup = Undo.GetCurrentGroup();
                    using var groupScope = ListPool<Greybox>.Get(out var previousGroup);
                    GreyboxLinkHierarchy.Collect(gb, previousGroup);
                    var previousParent = gb.transform.parent;
                    Undo.RegisterCompleteObjectUndo(gb, "Unlink Greybox Seam");
                    foreach (var seam in seams)
                        Undo.RegisterCompleteObjectUndo(seam.other, "Unlink Greybox Seam");
                    gb.UnlinkAll();
                    GreyboxLinkHierarchy.AfterUnlink(previousGroup, previousParent, "Unlink Greybox Seam");
                    Undo.CollapseUndoOperations(undoGroup);
                    foreach (var seam in seams) EditorUtility.SetDirty(seam.other);
                    EditorUtility.SetDirty(gb);
                }
            }
        }

        // Add a weld or join two existing linked groups.
        using (new EditorGUILayout.HorizontalScope())
        {
            var picked = (Greybox)EditorGUILayout.ObjectField(s_linkLabel, null, typeof(Greybox), true);
            if (picked != null && picked != gb)
                GreyboxSeamSolver.LinkBoxes(gb, picked);

            bool picking = GreyboxSeamPicker.IsPicking && GreyboxSeamPicker.PickingSubject == gb;
            if (GUILayout.Button(picking ? "Picking…" : "Pick", GUILayout.Width(64f)))
                GreyboxSeamPicker.Begin(gb);
        }
    }
}
