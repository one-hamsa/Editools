using UnityEditor;
using UnityEngine;

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

        // Already a linked child: show the weld + an Unlink button (one parent per box).
        if (gb.IsLinkAlive)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                string parentName = gb.LinkedParent != null ? gb.LinkedParent.name : "(missing)";
                EditorGUILayout.LabelField($"Seam welded to: {parentName}", EditorStyles.miniLabel);
                if (GUILayout.Button("Unlink", GUILayout.Width(70f)))
                {
                    Undo.RegisterCompleteObjectUndo(gb, "Unlink Greybox Seam");
                    if (gb.LinkedParent != null)
                        Undo.RegisterCompleteObjectUndo(gb.LinkedParent, "Unlink Greybox Seam");
                    gb.Unlink();
                    EditorUtility.SetDirty(gb);
                }
            }
            return;
        }

        // Not a linked child yet — offer a drop target (one-shot: drop a box, it welds, the field
        // clears next repaint) plus a Pick button to click the target in the scene, like Boolean.
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
