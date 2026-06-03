using UnityEditor;

[CustomEditor(typeof(GreyboxManager))]
[CanEditMultipleObjects]
public class GreyboxManagerEditor : Editor
{
    // Push-only propagation: inspector edits on the manager push RebuildMesh to every descendant
    // primitive via the change-check below. Undo/redo restores serialized fields but the children's
    // meshes were built from pre-undo manager values — so we re-push, but only when the undo actually
    // changed this manager. Each selected manager's density signature is cached so an undo elsewhere
    // is a cheap O(1) comparison instead of a fan-out over every descendant.
    int[] _lastSignatures;

    void OnEnable()
    {
        Undo.undoRedoPerformed += OnUndoRedo;
        CaptureSignatures();
    }

    void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

    void CaptureSignatures()
    {
        _lastSignatures = new int[targets.Length];
        for (int i = 0; i < targets.Length; i++)
            _lastSignatures[i] = targets[i] is GreyboxManager mgr ? mgr.ComputeDensitySignature() : 0;
    }

    void OnUndoRedo()
    {
        if (_lastSignatures == null || _lastSignatures.Length != targets.Length)
            CaptureSignatures();

        for (int i = 0; i < targets.Length; i++)
        {
            if (!(targets[i] is GreyboxManager mgr)) continue;
            int sig = mgr.ComputeDensitySignature();
            if (sig == _lastSignatures[i]) continue; // undo didn't touch this manager — descendants unaffected
            _lastSignatures[i] = sig;
            mgr.PushRebuildToChildren();
        }
    }

    public override void OnInspectorGUI()
    {
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            if (_lastSignatures == null || _lastSignatures.Length != targets.Length)
                CaptureSignatures();

            for (int i = 0; i < targets.Length; i++)
                if (targets[i] is GreyboxManager mgr)
                {
                    mgr.PushRebuildToChildren();
                    _lastSignatures[i] = mgr.ComputeDensitySignature();
                }
        }
    }
}
