using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;

public static class SceneCameraUndo
{
    struct CameraState
    {
        public Vector3    pivot;
        public Quaternion rotation;
        public float      size;
        public bool       orthographic;

        public CameraState(SceneView view)
        {
            pivot        = view.pivot;
            rotation     = view.rotation;
            size         = view.size;
            orthographic = view.orthographic;
        }

        public void Apply(SceneView view)
        {
            view.pivot        = pivot;
            view.rotation     = rotation;
            view.size         = size;
            view.orthographic = orthographic;
            view.Repaint();
        }

        public bool Equals(CameraState other)
        {
            return pivot == other.pivot
                && rotation == other.rotation
                && Mathf.Approximately(size, other.size)
                && orthographic == other.orthographic;
        }
    }

    class ViewHistory
    {
        public readonly Stack<CameraState> undoStack = new Stack<CameraState>();
        public readonly Stack<CameraState> redoStack = new Stack<CameraState>();
        public CameraState lastCommitted;
        public CameraState lastFrame;
        public bool        hasPending;
        public double      lastMoveTime;
    }

    private static readonly Dictionary<int, ViewHistory> s_histories = new Dictionary<int, ViewHistory>();

    private const int    k_maxHistory     = 32;
    private const double k_settleDelay    = 0.15;

    private static bool s_isRestoring;
    private static bool s_initialized;

    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        if (s_initialized) return;
        s_initialized = true;
        EditorApplication.update += OnUpdate;
    }

    private static void OnUpdate()
    {
        if (s_isRestoring) return;

        foreach (var view in SceneView.sceneViews)
        {
            var sceneView = view as SceneView;
            if (sceneView == null) continue;
            UpdateView(sceneView);
        }
    }

    private static ViewHistory GetHistory(SceneView view)
    {
        int id = view.GetInstanceID();
        if (!s_histories.TryGetValue(id, out var history))
        {
            history = new ViewHistory();
            s_histories[id] = history;
        }
        return history;
    }

    private static void UpdateView(SceneView view)
    {
        var h = GetHistory(view);
        CameraState current = new CameraState(view);

        // First frame for this view — seed the baseline.
        if (h.undoStack.Count == 0 && !h.hasPending)
        {
            h.lastCommitted = current;
            h.lastFrame     = current;
            h.undoStack.Push(current);
            return;
        }

        bool movedThisFrame = !current.Equals(h.lastFrame);
        h.lastFrame = current;

        if (movedThisFrame)
        {
            h.lastMoveTime = EditorApplication.timeSinceStartup;
            h.hasPending   = true;
        }
        else if (h.hasPending && EditorApplication.timeSinceStartup - h.lastMoveTime >= k_settleDelay)
        {
            if (!current.Equals(h.lastCommitted))
                PushState(h, current);
            h.hasPending = false;
        }
    }

    private static void PushState(ViewHistory h, CameraState state)
    {
        if (h.undoStack.Count > 0 && h.undoStack.Peek().Equals(state))
            return;

        if (h.undoStack.Count >= k_maxHistory)
        {
            var temp = new CameraState[h.undoStack.Count];
            h.undoStack.CopyTo(temp, 0);
            h.undoStack.Clear();
            for (int i = temp.Length - 2; i >= 0; i--)
                h.undoStack.Push(temp[i]);
        }

        h.undoStack.Push(state);
        h.lastCommitted = state;
        h.redoStack.Clear();
    }

    [Shortcut("Editools/Scene Camera Undo", KeyCode.Z, ShortcutModifiers.Shift)]
    private static void UndoCameraMove()
    {
        SceneView view = SceneView.lastActiveSceneView;
        if (view == null) return;

        var h = GetHistory(view);
        if (h.undoStack.Count <= 1) return;

        s_isRestoring = true;

        h.redoStack.Push(h.undoStack.Pop());

        CameraState target = h.undoStack.Peek();
        target.Apply(view);
        h.lastCommitted = target;
        h.lastFrame     = target;

        EditorApplication.delayCall += () => s_isRestoring = false;
    }

    [Shortcut("Editools/Scene Camera Redo", KeyCode.Y, ShortcutModifiers.Shift)]
    private static void RedoCameraMove()
    {
        SceneView view = SceneView.lastActiveSceneView;
        if (view == null) return;

        var h = GetHistory(view);
        if (h.redoStack.Count == 0) return;

        s_isRestoring = true;

        CameraState target = h.redoStack.Pop();
        h.undoStack.Push(target);
        target.Apply(view);
        h.lastCommitted = target;
        h.lastFrame     = target;

        EditorApplication.delayCall += () => s_isRestoring = false;
    }
}
