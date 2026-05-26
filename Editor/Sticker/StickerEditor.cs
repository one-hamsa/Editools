#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Extends <see cref="GreyPrimitiveEditor"/> for Sticker so the artist gets the
/// shared Live/Serialized indicator, Rebuild button, and undo/redo handling.
///
/// Additional behaviour: polls the transform via <see cref="EditorApplication.update"/>
/// while the sticker is selected and triggers a rebuild whenever position, rotation,
/// or scale changes — so dragging the move/rotate/scale handle reconforms live.
/// <see cref="MonoBehaviour.OnValidate"/> doesn't fire on transform drags and
/// <see cref="Editor.OnSceneGUI"/> only runs on scene-view repaints (which can be sparse),
/// so the update tick is the reliable signal.
/// </summary>
[CustomEditor(typeof(Sticker))]
[CanEditMultipleObjects]
class StickerEditor : GreyPrimitiveEditor
{
    Vector3    _lastPos;
    Quaternion _lastRot;
    Vector3    _lastScale;
    bool       _poseInitialized;

    protected override void OnEnable()
    {
        base.OnEnable();
        SnapshotPose();
        EditorApplication.update += OnEditorUpdate;
    }

    protected override void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        base.OnDisable();
    }

    void SnapshotPose()
    {
        if (target is not Sticker s) return;
        var tr = s.transform;
        _lastPos         = tr.position;
        _lastRot         = tr.rotation;
        _lastScale       = tr.localScale;
        _poseInitialized = true;
    }

    void OnEditorUpdate()
    {
        if (target == null) return;       // selection destroyed
        if (target is not Sticker s) return;
        var tr = s.transform;

        if (!_poseInitialized)
        {
            SnapshotPose();
            return;
        }

        if (tr.position   == _lastPos &&
            tr.rotation   == _lastRot &&
            tr.localScale == _lastScale)
            return;

        _lastPos   = tr.position;
        _lastRot   = tr.rotation;
        _lastScale = tr.localScale;
        s.RebuildMesh();
        // Force the scene view to redraw so the gizmos pick up the new cage/BFS-viz state.
        SceneView.RepaintAll();
    }
}
#endif
