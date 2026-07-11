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

    static readonly GUIContent s_cutMaterialLabel = new GUIContent(
        "Cut Material",
        "Material for the faces the Operator carves out. Empty = the result keeps one material slot. " +
        "Assign one to give the Boolean Result a second slot applied to the cut faces only.");

    static readonly GUIContent s_recenterLabel = new GUIContent(
        "Recenter Pivot",
        "Move the pivot to the center of the box's bounding box, shifting the transform so the geometry " +
        "stays put.");

    // Rebuilds are push-only: every path that mutates the primitive's state explicitly triggers
    // RebuildMesh. Inspector edits go through the change-check below. Undo/redo rebuilds are
    // handled by GreyboxUndoRebuilder, which rebuilds only what the undo actually touched.
    protected virtual void OnEnable()
    {
        GreyBooleanLiveWatcher.Acquire();
    }

    protected virtual void OnDisable()
    {
        GreyBooleanLiveWatcher.Release();
    }

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
        DrawPropertiesExcluding(serializedObject, "m_Script", "_booleanOperator", "_booleanCutMaterial");
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

        if (prim is Greybox && GUILayout.Button(s_recenterLabel))
            foreach (var t in targets)
                if (t is Greybox g) RecenterGreyboxPivot(g);
    }

    // Re-origin a Greybox at its bounding-box center: shift every corner by -center in local space and
    // push the transform by +center in world space, so the geometry doesn't visibly move. Seam-welded
    // partners need no resync — nothing changes in world space.
    static void RecenterGreyboxPivot(Greybox gb)
    {
        var corners = gb.Corners;
        Vector3 min = corners[0], max = corners[0];
        for (int i = 1; i < corners.Length; i++)
        {
            min = Vector3.Min(min, corners[i]);
            max = Vector3.Max(max, corners[i]);
        }
        Vector3 center = (min + max) * 0.5f;
        if (center.sqrMagnitude < 1e-12f) return; // already centered

        Undo.RegisterCompleteObjectUndo(gb, "Recenter Greybox Pivot");
        Undo.RecordObject(gb.transform, "Recenter Greybox Pivot");

        gb.transform.position += gb.transform.TransformVector(center);
        for (int i = 0; i < corners.Length; i++)
            corners[i] -= center;

        gb.RebuildMesh();
        GreyBooleanOrchestrator.Sync(gb);
        EditorUtility.SetDirty(gb);
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

        // The cut-material slot appears only once an Operator is set.
        if (prop.objectReferenceValue != null)
        {
            var matProp = serializedObject.FindProperty("_booleanCutMaterial");
            if (matProp != null)
                EditorGUILayout.PropertyField(matProp, s_cutMaterialLabel);
        }
    }

    void RebuildAndSyncTargets()
    {
        foreach (var t in targets)
            if (t is GreyPrimitive p) { p.RebuildMesh(); GreyBooleanOrchestrator.Sync(p); }
    }

    // Rebuild a single primitive together with everything derived from it — its own boolean,
    // any result that references it, and any seam-welded boxes. Called by GreyboxUndoRebuilder
    // for each primitive an undo/redo actually touched.
    internal static void RebuildPrimitiveAndDependents(GreyPrimitive prim)
    {
        if (prim == null) return;
        prim.RebuildMesh();
        GreyBooleanOrchestrator.Sync(prim);       // reconcile prim's own boolean (a changed/cleared operator)
        GreyBooleanOrchestrator.ReBakeFrom(prim); // re-bake any result that references prim, up the chain
        if (prim is Greybox gb)
            GreyboxSeamSolver.RebuildSeamPartners(gb);
    }
}
