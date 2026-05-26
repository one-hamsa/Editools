using UnityEditor;

[CustomEditor(typeof(GreyboxManager))]
[CanEditMultipleObjects]
public class GreyboxManagerEditor : Editor
{
    // Inspector edits flow through GreyboxManager.OnValidate (hash-gated push to children) —
    // no editor change-check needed. Undo/redo restores serialized fields including _pushHash,
    // so the manager's OnValidate skips the push and children are left stale. Force-push on
    // undo/redo to keep them in sync.
    void OnEnable()  => Undo.undoRedoPerformed += OnUndoRedo;
    void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

    void OnUndoRedo()
    {
        foreach (var t in targets)
            if (t is GreyboxManager mgr) mgr.PushRebuildToChildren();
    }
}
