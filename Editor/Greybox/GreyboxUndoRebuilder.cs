using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Global, selection-independent rebuild of grey geometry after an undo/redo.
//
// Undo restores serialized fields but leaves in-memory meshes built from the pre-undo state.
// This hook is the undo/redo rebuild path for grey primitives — selection-independent, and it
// rebuilds ONLY what the undo actually touched:
//
//   • a primitive whose own properties were undone,
//   • every primitive on (or under) an object whose SCALE was undone — mesh density is baked
//     from world scale, so position/rotation undos are deliberately ignored,
//   • the descendants of a manager whose properties were undone.
//
// It learns "what changed" from ObjectChangeEvents.changesPublished (which carries the affected
// instance ids) and only acts when Undo.undoRedoPerformed confirms the change came from undo/redo.
// changesPublished fires for normal edits too — those are already rebuilt by their edit paths, so
// this hook stays silent for them and never rebuilds during a drag.
[InitializeOnLoad]
static class GreyboxUndoRebuilder
{
    // The most recent changesPublished batch, classified by trigger. Cleared and refilled on every
    // batch, so at the moment an undo/redo finishes these hold exactly that operation's targets.
    static readonly HashSet<GreyPrimitive>  s_changedPrims    = new HashSet<GreyPrimitive>();
    static readonly HashSet<Transform>      s_scaledRoots     = new HashSet<Transform>();
    static readonly HashSet<GreyboxManager> s_changedManagers = new HashSet<GreyboxManager>();

    // Last-known state, so a transform / manager change can be classified as "actually scale" or
    // "actually density" rather than any property edit. Keyed by instance id; populated lazily.
    static readonly Dictionary<int, Vector3> s_scaleByInstance     = new Dictionary<int, Vector3>();
    static readonly Dictionary<int, int>     s_signatureByInstance = new Dictionary<int, int>();

    // Reused across undos to dedupe the final rebuild set without per-undo allocation.
    static readonly HashSet<GreyPrimitive> s_rebuild = new HashSet<GreyPrimitive>();

    // The undo's own changesPublished batch and Undo.undoRedoPerformed both fire within the same
    // editor tick; their relative order isn't guaranteed, so we defer the rebuild one tick to read
    // the batch after both have run. One flush per tick regardless of how many undos coalesce.
    static bool s_flushScheduled;

    static GreyboxUndoRebuilder()
    {
        ObjectChangeEvents.changesPublished += OnChangesPublished;
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    static void OnChangesPublished(ref ObjectChangeEventStream stream)
    {
        s_changedPrims.Clear();
        s_scaledRoots.Clear();
        s_changedManagers.Clear();

        for (int i = 0; i < stream.length; i++)
        {
            if (stream.GetEventType(i) != ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                continue;

            stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var data);
            var obj = EditorUtility.InstanceIDToObject(data.instanceId);
            if (obj == null) continue;

            switch (obj)
            {
                case GreyPrimitive prim:                       // a primitive's own fields changed
                    s_changedPrims.Add(prim);
                    break;
                case GreyboxManager mgr:                       // a manager's fields changed
                    RecordManagerIfDensityChanged(mgr);
                    break;
                case Transform tr:                             // a transform changed — scale only
                    RecordRootIfScaleChanged(tr);
                    break;
                case GameObject go:                            // property reported on the GO itself
                    RecordRootIfScaleChanged(go.transform);
                    break;
            }
        }
    }

    // Track scale per object that carries grey geometry, so an undo of a move/rotate (same field
    // event, unchanged scale) is skipped while an undo of a scale is caught. Unknown previous scale
    // is treated as changed — a stale mesh is worse than a redundant rebuild.
    static void RecordRootIfScaleChanged(Transform tr)
    {
        if (tr == null) return;
        if (tr.GetComponentInChildren<GreyPrimitive>(includeInactive: true) == null) return;

        int id = tr.GetInstanceID();
        Vector3 scale = tr.localScale;
        bool changed = !s_scaleByInstance.TryGetValue(id, out var prev) || prev != scale;
        s_scaleByInstance[id] = scale;
        if (changed) s_scaledRoots.Add(tr);
    }

    // Mirror GreyboxManagerEditor: only a change to a density-affecting field should re-push to
    // children, so compare the same signature it uses instead of reacting to any manager edit.
    static void RecordManagerIfDensityChanged(GreyboxManager mgr)
    {
        if (mgr == null) return;
        int id = mgr.GetInstanceID();
        int sig = mgr.ComputeDensitySignature();
        bool changed = !s_signatureByInstance.TryGetValue(id, out var prev) || prev != sig;
        s_signatureByInstance[id] = sig;
        if (changed) s_changedManagers.Add(mgr);
    }

    static void OnUndoRedo()
    {
        if (s_flushScheduled) return;
        s_flushScheduled = true;
        EditorApplication.delayCall += Flush;
    }

    static void Flush()
    {
        s_flushScheduled = false;

        s_rebuild.Clear();

        foreach (var prim in s_changedPrims)
            if (prim != null) s_rebuild.Add(prim);

        foreach (var root in s_scaledRoots)
        {
            if (root == null) continue;
            foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
                if (prim != null) s_rebuild.Add(prim);
        }

        foreach (var prim in s_rebuild)
            GreyPrimitiveEditor.RebuildPrimitiveAndDependents(prim);

        foreach (var mgr in s_changedManagers)
            if (mgr != null) mgr.PushRebuildToChildren();

        s_changedPrims.Clear();
        s_scaledRoots.Clear();
        s_changedManagers.Clear();
        s_rebuild.Clear();
    }
}
