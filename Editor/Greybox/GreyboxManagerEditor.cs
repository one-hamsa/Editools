using UnityEditor;

[CustomEditor(typeof(GreyboxManager))]
[CanEditMultipleObjects]
public class GreyboxManagerEditor : Editor
{
    // Push-only propagation: inspector edits on the manager push RebuildMesh to every descendant
    // primitive via the change-check below. Undo/redo restores serialized fields but the children's
    // meshes were built from pre-undo manager values, so we push again on undo.
    void OnEnable()  => Undo.undoRedoPerformed += OnUndoRedo;
    void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

    void OnUndoRedo()
    {
        foreach (var t in targets)
            if (t is GreyboxManager mgr) mgr.PushRebuildToChildren();
    }

    public override void OnInspectorGUI()
    {
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var t in targets)
                if (t is GreyboxManager mgr) mgr.PushRebuildToChildren();
        }
    }
}
