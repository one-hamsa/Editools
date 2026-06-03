using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GreyPrimitive), true)]
[CanEditMultipleObjects]
public class GreyPrimitiveEditor : Editor
{
    static readonly GUIContent s_booleanLabel = new GUIContent(
        "Boolean",
        "Optional Operator to subtract from this object. Drag a Grey object here, or use Pick to " +
        "click one in the scene. Creates a baked 'Boolean Result' child (Subject minus Operator).");

    // Rebuilds are push-only: every path that mutates the primitive's state explicitly triggers
    // RebuildMesh. Inspector edits go through the change-check below. Undo/redo restores serialized
    // fields but leaves the in-memory mesh built from the pre-undo state, so we rebuild explicitly.
    protected virtual void OnEnable()
    {
        Undo.undoRedoPerformed += OnUndoRedo;
        GreyBooleanLiveWatcher.Acquire();
    }

    protected virtual void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
        GreyBooleanLiveWatcher.Release();
    }

    void OnUndoRedo() => RebuildTargetsWithDependents();

    public override void OnInspectorGUI()
    {
        var prim = (GreyPrimitive)target;
        bool isLive = prim.MeshIsLive;

        var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
        var label = isLive ? "Mesh: Live" : "Mesh: Serialized";
        var color = isLive ? new Color(1f, 0.75f, 0.3f) : new Color(0.4f, 0.8f, 0.4f);

        var style = new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = color }
        };
        EditorGUI.LabelField(rect, label, style);

        serializedObject.Update();
        EditorGUI.BeginChangeCheck();
        DrawPropertiesExcluding(serializedObject, "m_Script", "_booleanOperator");
        DrawBooleanRow();
        bool changed = EditorGUI.EndChangeCheck();
        serializedObject.ApplyModifiedProperties();

        if (changed) RebuildAndSyncTargets();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Rebuild Mesh"))
                RebuildAndSyncTargets();
            if (prim.UsesColliderMesh && GUILayout.Button("Set Collider"))
                foreach (var t in targets)
                    if (t is GreyPrimitive p) p.ApplyColliderMesh();
        }
    }

    // The Boolean field is exposed only on Greybox and Boolean Results (so it can be chained),
    // keeping Greypipe/Greyroad inspectors clean. Drawn with a Pick button beside it.
    void DrawBooleanRow()
    {
        if (!(target is Greybox) && !(target is GreyBooleanResult)) return;

        var prop = serializedObject.FindProperty("_booleanOperator");
        if (prop == null) return;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PropertyField(prop, s_booleanLabel);

            bool picking = GreyBooleanPicker.IsPicking
                           && GreyBooleanPicker.PickingSubject == (GreyPrimitive)target;
            if (GUILayout.Button(picking ? "Picking…" : "Pick", GUILayout.Width(64f)))
                GreyBooleanPicker.Begin((GreyPrimitive)target);
        }
    }

    void RebuildAndSyncTargets()
    {
        foreach (var t in targets)
            if (t is GreyPrimitive p) { p.RebuildMesh(); GreyBooleanOrchestrator.Sync(p); }
    }

    // Undo/redo restored serialized fields and seam/boolean links, but the in-memory meshes are still
    // built from the pre-undo state. Rebuild each selected primitive together with everything derived
    // from it, so dependent geometry doesn't go stale when its source is undone.
    void RebuildTargetsWithDependents()
    {
        foreach (var t in targets)
            if (t is GreyPrimitive p) RebuildWithDependents(p);
    }

    // Rebuild root, every grey primitive beneath it, and — for each — the boolean results and
    // seam-welded boxes that reference it. A Boolean Result is the parent of its subject/operator, so
    // GetComponentsInChildren also reaches the inputs nested under a result.
    static void RebuildWithDependents(GreyPrimitive root)
    {
        if (root == null) return;

        foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
        {
            if (prim == null) continue;
            prim.RebuildMesh();
            GreyBooleanOrchestrator.Sync(prim);       // reconcile prim's own boolean (a changed/cleared operator)
            GreyBooleanOrchestrator.ReBakeFrom(prim); // re-bake any result that references prim, up the chain
            if (prim is Greybox gb)
                GreyboxSeamSolver.RebuildSeamPartners(gb);
        }
    }
}
