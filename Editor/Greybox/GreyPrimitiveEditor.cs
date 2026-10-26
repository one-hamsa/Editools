using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GreyPrimitive), true)]
[CanEditMultipleObjects]
public class GreyPrimitiveEditor : Editor
{
    static readonly GUIContent s_booleanLabel = new GUIContent(
        "Boolean",
        "Grey object to subtract from this one: a primitive, a linked group or a Boolean Result. Drag it here " +
        "or use Pick. Creates a Boolean Result that holds both inputs. An Operator already used by another " +
        "Boolean stays in place and is shared. Clear the field to undo this Boolean only.");

    static readonly GUIContent s_cutMaterialLabel = new GUIContent(
        "Cut Material",
        "Material for the faces the Operator cuts. Empty = the cut faces use the Subject's material. " +
        "Assigned = the Boolean Result gets an extra material slot for the cut faces only.");

    static readonly GUIContent s_pickLabel = new GUIContent(
        "Pick",
        "Click a Grey object in the Scene View or Hierarchy to use as the Operator. Esc cancels.");

    static readonly GUIContent s_pickingLabel = new GUIContent(
        "Picking…",
        "Waiting for a Grey object click in the Scene View or Hierarchy. Esc cancels.");

    static readonly GUIContent s_rebuildLabel = new GUIContent(
        "Rebuild Mesh",
        "Regenerate this object's mesh and every Boolean Result that uses it.");

    static readonly GUIContent s_setColliderLabel = new GUIContent(
        "Set Collider",
        "Assign the generated mesh to this object's MeshCollider.");

    static readonly GUIContent s_recenterLabel = new GUIContent(
        "Recenter Pivot",
        "Move the pivot to the center of the box's bounding box, shifting the transform so the geometry " +
        "stays put.");

    internal static readonly GUIContent ExplodeLabel = new GUIContent(
        "Explode Boolean",
        "Undo the whole Boolean this object belongs to, including nested Booleans and groups. Every input returns " +
        "to the outermost Boolean's parent as a separate, visible primitive. Linked boxes stay linked in a link " +
        "folder. Operators also used by other Booleans stay with them.");

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
            if (GUILayout.Button(s_rebuildLabel))
                RebuildAndSyncTargets();
            if (prim.UsesColliderMesh && GUILayout.Button(s_setColliderLabel))
                foreach (var t in targets)
                    if (t is GreyPrimitive p) p.ApplyColliderMesh();
        }

        if (prim is Greybox && GUILayout.Button(s_recenterLabel))
            foreach (var t in targets)
                if (t is Greybox g) RecenterGreyboxPivot(g);

        if ((prim is GreyBooleanResult || GreyGeometry.Owner(prim) != null) && GUILayout.Button(ExplodeLabel))
        {
            // One target can sit inside another target's tree, which the first Explode already destroyed.
            foreach (var t in targets)
                if (t != null) GreyBooleanOrchestrator.Explode((GreyPrimitive)t);
            GUIUtility.ExitGUI();
        }
    }

    // Re-origin a Greybox at its bounding-box center: shift every corner by -center in local space and
    // push the transform by +center in world space, so the geometry doesn't visibly move. Seam-welded
    // partners need no resync — nothing changes in world space.
    internal static void RecenterGreyboxPivot(Greybox gb, bool rebuild = true)
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
        Undo.RegisterCompleteObjectUndo(gb.transform, "Recenter Greybox Pivot");

        for (int i = 0; i < gb.transform.childCount; i++)
        {
            var child = gb.transform.GetChild(i);
            Undo.RegisterCompleteObjectUndo(child, "Recenter Greybox Pivot");
            child.localPosition -= center;
        }

        gb.transform.position += gb.transform.TransformVector(center);
        for (int i = 0; i < corners.Length; i++)
            corners[i] -= center;

        if (rebuild)
        {
            gb.RebuildMesh();
            GreyBooleanOrchestrator.ReBakeFrom(gb);
        }
        EditorUtility.SetDirty(gb);
    }

    // The Boolean field is exposed only on Greybox and Boolean Results (so it can be chained),
    // keeping Greypipe/Greyroad inspectors clean. Drawn with a Pick button beside it.
    void DrawBooleanRow()
    {
        if (!(target is Greybox) && !(target is GreyBooleanResult) && !(target is GreyboxCompound)) return;

        var subject = GreyBooleanOrchestrator.EditSubject((GreyPrimitive)target);
        var settings = subject == target ? serializedObject : new SerializedObject(subject);
        if (settings != serializedObject) settings.Update();
        var prop = settings.FindProperty("_booleanOperator");
        EditorGUI.BeginChangeCheck();
        DrawBooleanInput(settings, subject, s_booleanLabel);
        if (prop.objectReferenceValue != null)
            EditorGUILayout.PropertyField(settings.FindProperty("_booleanCutMaterial"), s_cutMaterialLabel);
        bool changed = EditorGUI.EndChangeCheck();
        if (settings != serializedObject && changed)
        {
            settings.ApplyModifiedProperties();
            GreyBooleanOrchestrator.Sync(subject);
        }
    }

    internal static bool DrawBooleanInput(SerializedObject settings, GreyPrimitive subject, GUIContent label)
    {
        var prop = settings.FindProperty("_booleanOperator");
        EditorGUI.BeginChangeCheck();
        using (new EditorGUILayout.HorizontalScope())
        {
            var current = prop.objectReferenceValue as GreyPrimitive;
            EditorGUI.showMixedValue = prop.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            var selected = EditorGUILayout.ObjectField(label, current != null ? current.gameObject : null,
                typeof(GameObject), true) as GameObject;
            if (EditorGUI.EndChangeCheck())
            {
                var picked = GreyBooleanOrchestrator.ResolveObject(selected);
                if (selected == null || picked != null) prop.objectReferenceValue = picked;
            }
            EditorGUI.showMixedValue = false;
            bool picking = GreyBooleanPicker.IsPicking && GreyBooleanPicker.PickingSubject == subject;
            if (GUILayout.Button(picking ? s_pickingLabel : s_pickLabel, GUILayout.Width(64f))) GreyBooleanPicker.Begin(subject);
        }
        return EditorGUI.EndChangeCheck();
    }

    void RebuildAndSyncTargets()
    {
        foreach (var t in targets)
            if (t is GreyPrimitive p)
            {
                if (p is Greybox gb && GreyPrimitiveSettings.AutoUpdatePivot)
                    RecenterGreyboxPivot(gb, rebuild: false);
                p.RebuildMesh();
                GreyBooleanOrchestrator.Sync(p);
                GreyBooleanOrchestrator.ReBakeFrom(p);
            }
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
