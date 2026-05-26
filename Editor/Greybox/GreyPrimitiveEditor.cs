using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GreyPrimitive), true)]
[CanEditMultipleObjects]
public class GreyPrimitiveEditor : Editor
{
    // Inspector edits fire OnValidate (via DrawDefaultInspector's ApplyModifiedProperties), which
    // hash-gates a rebuild. Undo/redo restores serialized fields including _builtHash, so the hash
    // matches the restored state and OnValidate skips — leaving the in-memory mesh stale relative
    // to the restored params. Force-rebuild on undo/redo to keep the mesh in sync.
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

        DrawDefaultInspector();

        EditorGUILayout.Space();
        if (GUILayout.Button("Rebuild Mesh"))
        {
            foreach (var t in targets)
                if (t is GreyPrimitive p) p.RebuildMesh();
        }
    }
}
