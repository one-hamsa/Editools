#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Updates selected Boolean geometry while inputs change. Common movement of a complete result
/// preserves its solid; changes to world-space scale still refresh mesh density.
/// </summary>
static class GreyBooleanLiveWatcher
{
    struct State
    {
        public long geometry;
        public Matrix4x4 relativeTransform;
        public Matrix4x4 worldMetric;
    }

    static int s_refCount;
    static bool s_ownsDeferredPersist;
    static readonly Dictionary<(int input, int consumer), State> s_lastState = new Dictionary<(int, int), State>();

    public static void Acquire()
    {
        if (s_refCount++ == 0)
        {
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += OnSelectionChanged;
        }
    }

    public static void Release()
    {
        if (--s_refCount > 0) return;
        s_refCount = 0;
        SceneView.duringSceneGui -= OnSceneGui;
        Selection.selectionChanged -= OnSelectionChanged;
        OnSelectionChanged();
    }

    static void OnSelectionChanged()
    {
        FinishDeferredPersist();
        s_lastState.Clear();
    }

    static void FinishDeferredPersist()
    {
        if (!s_ownsDeferredPersist) return;
        s_ownsDeferredPersist = false;
        GreyPrimitive.EndDeferredPersist();
    }

    static void OnSceneGui(SceneView sv)
    {
        Event e = Event.current;
        bool released = e.rawType == EventType.MouseUp || GUIUtility.hotControl == 0;
        if (e.type != EventType.Repaint && e.type != EventType.MouseDrag && e.rawType != EventType.MouseUp)
        {
            if (released) FinishDeferredPersist();
            return;
        }

        using var visitedScope = HashSetPool<int>.Get(out var visited);
        using var changedScope = ListPool<GreyPrimitive>.Get(out var changed);
        foreach (var t in Selection.transforms)
        {
            if (t == null) continue; // Selection can contain objects deleted during the event.
            WatchPrimitive(t.GetComponent<GreyPrimitive>(), visited, changed);
        }

        try
        {
            if (changed.Count == 0) return;
            // Custom geometry tools may already own the persistence scope.
            if (!released && !GreyPrimitive.IsPersistenceDeferred)
            {
                GreyPrimitive.BeginDeferredPersist();
                s_ownsDeferredPersist = true;
            }
            GreyBooleanOrchestrator.RebuildChanged(changed);
            sv.Repaint();
        }
        finally
        {
            if (released) FinishDeferredPersist();
        }
    }

    static void WatchPrimitive(GreyPrimitive prim, HashSet<int> visited, List<GreyPrimitive> changed)
    {
        // Non-primitive selections and temporarily missing Boolean inputs have nothing to watch.
        if (prim == null || !visited.Add(prim.GetInstanceID())) return;
        if (prim is GreyBooleanResult result)
        {
            WatchPrimitive(result.Subject, visited, changed);
            WatchPrimitive(result.Operator, visited, changed);
        }
        else if (prim is GreyboxCompound compound)
            foreach (var part in compound.Parts) WatchPrimitive(part, visited, changed);

        using var consumersScope = ListPool<GreyPrimitive>.Get(out var consumers);
        GreyBooleanOrchestrator.CollectConsumers(prim, consumers);
        if (consumers.Count == 0) WatchInput(prim, null, changed);
        else foreach (var consumer in consumers) WatchInput(prim, consumer, changed);
    }

    static void WatchInput(GreyPrimitive prim, GreyPrimitive owner, List<GreyPrimitive> changed)
    {
        Matrix4x4 world = prim.transform.localToWorldMatrix;
        var state = new State
        {
            geometry = GeometrySignature(prim, owner),
            relativeTransform = RelativeTransform(prim.transform, owner),
            worldMetric = world.transpose * world,
        };
        var key = (prim.GetInstanceID(), owner != null ? owner.GetInstanceID() : 0);
        if (!s_lastState.TryGetValue(key, out var previous))
        {
            s_lastState[key] = state;
            return;
        }

        bool geometryChanged = state.geometry != previous.geometry
            || !SameMatrix(state.relativeTransform, previous.relativeTransform, 4);
        bool scaleChanged = !SameMatrix(state.worldMetric, previous.worldMetric, 3);
        if (!geometryChanged && !scaleChanged) return;
        s_lastState[key] = state;

        if (geometryChanged && owner != null) changed.Add(owner);
        if (scaleChanged && (owner != null || prim is GreyBooleanResult || prim is GreyboxCompound))
            changed.Add(prim);
    }

    static Matrix4x4 RelativeTransform(Transform input, GreyPrimitive owner)
    {
        if (owner == null) return Matrix4x4.identity; // A root's pose does not change its local solid.
        Matrix4x4 relative = Matrix4x4.identity;
        var current = input;
        while (current != null && current != owner.transform)
        {
            relative = Matrix4x4.TRS(current.localPosition, current.localRotation, current.localScale) * relative;
            current = current.parent;
        }
        // Explicit geometry ownership can cross organizational hierarchies.
        return current != null ? relative : owner.transform.worldToLocalMatrix * input.localToWorldMatrix;
    }

    static bool SameMatrix(Matrix4x4 a, Matrix4x4 b, int dimensions)
    {
        for (int row = 0; row < dimensions; row++)
            for (int column = 0; column < dimensions; column++)
            {
                float tolerance = dimensions == 4 ? 1e-6f
                    : 1e-5f * Mathf.Max(1f, Mathf.Max(Mathf.Abs(a[row, column]), Mathf.Abs(b[row, column])));
                if (Mathf.Abs(a[row, column] - b[row, column]) > tolerance) return false;
            }
        return true;
    }

    static long GeometrySignature(GreyPrimitive prim, GreyPrimitive owner)
    {
        long h = owner != null ? owner.GetInstanceID() : 0;
        if (prim is Greybox gb)
        {
            var corners = gb.Corners;
            for (int i = 0; i < corners.Length; i++) h = h * 31 + corners[i].GetHashCode();
            for (int i = 0; i < 6; i++) h = h * 31 + (gb.IsFaceVisible(i) ? 1 : 0);
        }
        return h;
    }
}
#endif
