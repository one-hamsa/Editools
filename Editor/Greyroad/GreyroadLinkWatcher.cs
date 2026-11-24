#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Keeps linked roads attached while Transforms move, with any tool: when a selected object
/// moves, the ledges and branch roads of every Greyroad in it are rebuilt from the new pose, and a
/// moved ledge or branch road is rebuilt back onto its source. Driven from the Scene View by
/// <see cref="GreyboxSeamSolver"/>; it does work only on a frame where a selected Transform moved.
/// </summary>
static class GreyroadLinkWatcher
{
    static readonly Dictionary<int, Matrix4x4> s_lastMatrix = new Dictionary<int, Matrix4x4>();
    static bool s_ownsDeferredPersist;

    internal static void OnSelectionChanged()
    {
        FinishDeferredPersist();
        s_lastMatrix.Clear();
    }

    internal static void OnSceneGui(SceneView sv)
    {
        Event e = Event.current;
        bool released = e.rawType == EventType.MouseUp || GUIUtility.hotControl == 0;
        if (e.type != EventType.Repaint && e.type != EventType.MouseDrag && e.rawType != EventType.MouseUp)
        {
            if (released) FinishDeferredPersist();
            return;
        }

        using var dependentsScope = ListPool<Greyroad>.Get(out var dependents);
        CollectMovedDependents(dependents);
        try
        {
            if (dependents.Count == 0) return;
            // Mid-drag rebuilds update the live mesh only; another tool may already own that scope.
            if (!released && !GreyPrimitive.IsPersistenceDeferred)
            {
                GreyPrimitive.BeginDeferredPersist();
                s_ownsDeferredPersist = true;
            }
            foreach (var road in dependents)
            {
                road.RebuildMesh();
                EditorUtility.SetDirty(road);
            }
            sv.Repaint();
        }
        finally
        {
            if (released) FinishDeferredPersist();
        }
    }

    /// <summary>Roads to rebuild because a selected Transform moved since the last check.</summary>
    static void CollectMovedDependents(List<Greyroad> dependents)
    {
        Transform[] selection = Selection.transforms;
        if (selection == null || selection.Length == 0) return;

        using var roadsScope = ListPool<Greyroad>.Get(out var roads);
        using var linkedScope = ListPool<Greyroad>.Get(out var linked);
        using var addedScope = HashSetPool<int>.Get(out var added);
        foreach (Transform t in selection)
        {
            if (t == null) continue; // Selection can contain objects deleted during the event.
            Matrix4x4 matrix = t.localToWorldMatrix;
            int id = t.GetInstanceID();
            bool known = s_lastMatrix.TryGetValue(id, out Matrix4x4 previous);
            if (known && previous == matrix) continue;
            s_lastMatrix[id] = matrix;
            if (!known) continue; // first sighting: record the baseline only

            t.GetComponentsInChildren(true, roads);
            foreach (var road in roads)
            {
                if (road.IsLedge || road.IsBranch) Add(road, dependents, added);
                road.GetLedges(linked);
                foreach (var ledge in linked) Add(ledge, dependents, added);
                road.GetBranches(linked);
                foreach (var branch in linked) Add(branch, dependents, added);
            }
        }
    }

    static void Add(Greyroad road, List<Greyroad> dependents, HashSet<int> added)
    {
        if (added.Add(road.GetInstanceID())) dependents.Add(road);
    }

    static void FinishDeferredPersist()
    {
        if (!s_ownsDeferredPersist) return;
        s_ownsDeferredPersist = false;
        GreyPrimitive.EndDeferredPersist();
    }
}
#endif
