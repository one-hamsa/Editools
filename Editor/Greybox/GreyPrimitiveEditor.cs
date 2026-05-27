using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GreyPrimitive), true)]
[CanEditMultipleObjects]
public class GreyPrimitiveEditor : Editor
{
    // Rebuilds are push-only: every path that mutates the primitive's state explicitly triggers
    // RebuildMesh. Inspector edits go through the change-check below. Undo/redo restores serialized
    // fields but leaves the in-memory mesh built from the pre-undo state, so we rebuild explicitly.
    protected virtual void OnEnable()  => Undo.undoRedoPerformed += OnUndoRedo;
    protected virtual void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

    void OnUndoRedo()
    {
        foreach (var t in targets)
            if (t is GreyPrimitive p) p.RebuildMesh();
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

        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var t in targets)
                if (t is GreyPrimitive p) p.RebuildMesh();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Rebuild Mesh"))
        {
            foreach (var t in targets)
                if (t is GreyPrimitive p) p.RebuildMesh();
        }
    }
}
