using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Pool;

// Rebuild only boxes whose visible faces changed when a weld endpoint disappeared or returned, and
// split newly created (duplicated or pasted) primitives off their source's meshes.
[InitializeOnLoad]
static class GreyboxSeamVisibility
{
    static readonly Dictionary<Greybox, int> s_faces = new Dictionary<Greybox, int>();
    static bool s_scheduled;

    static GreyboxSeamVisibility()
    {
        EditorApplication.delayCall += RegisterLoadedBoxes;
        EditorSceneManager.sceneOpened += OnSceneOpened;
        EditorSceneManager.sceneClosing += OnSceneClosing;
        ObjectChangeEvents.changesPublished += OnChanges;
        Undo.undoRedoPerformed += ScheduleRefresh;
    }

    static int FaceMask(Greybox box)
    {
        int mask = 0;
        for (int face = 0; face < 6; face++)
            if (box.IsFaceVisible(face)) mask |= 1 << face;
        return mask;
    }

    static void Register(Greybox box)
    {
        if (box == null || EditorUtility.IsPersistent(box) || !box.gameObject.scene.IsValid()) return;
        if (box.RememberSeamFaces()) EditorUtility.SetDirty(box);
        if (!s_faces.ContainsKey(box)) s_faces.Add(box, FaceMask(box));
    }

    static void RegisterRoot(GameObject root)
    {
        if (root == null) return;
        foreach (var manager in root.GetComponentsInChildren<GreyPrimitiveManager>(true)) GreyPrimitiveManagerEditor.Register(manager);
        foreach (var box in root.GetComponentsInChildren<Greybox>(true)) Register(box);
        foreach (var primitive in root.GetComponentsInChildren<GreyPrimitive>(true)) GreyBooleanOrchestrator.Register(primitive);
    }

    static void RegisterLoadedBoxes()
    {
        foreach (var manager in Resources.FindObjectsOfTypeAll<GreyPrimitiveManager>()) GreyPrimitiveManagerEditor.Register(manager);
        foreach (var box in Resources.FindObjectsOfTypeAll<Greybox>()) Register(box);
        foreach (var primitive in Resources.FindObjectsOfTypeAll<GreyPrimitive>()) GreyBooleanOrchestrator.Register(primitive);
        ScheduleRefresh();
    }

    static void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        foreach (var root in scene.GetRootGameObjects()) RegisterRoot(root);
        ScheduleRefresh();
    }

    static void OnSceneClosing(Scene scene, bool removingScene)
    {
        using var scope = ListPool<Greybox>.Get(out var removed);
        foreach (var box in s_faces.Keys)
            if (box == null || box.gameObject.scene == scene) removed.Add(box);
        foreach (var box in removed) s_faces.Remove(box);
    }

    // A duplicated or pasted hierarchy still references the source's scene-embedded meshes. Split it
    // off at creation so a later edit to either side can't write geometry into the other's mesh. The
    // whole change stream is one batch, so a multi-object paste or an undo that restores many
    // primitives scans the scene once.
    static void SplitDuplicatedMeshes(List<GameObject> roots)
    {
        if (roots.Count == 0 || EditorApplication.isPlayingOrWillChangePlaymode) return;
        using var createdScope = ListPool<GreyPrimitive>.Get(out var created);
        using var rootScope = ListPool<GreyPrimitive>.Get(out var rootPrimitives);
        foreach (var root in roots)
        {
            if (root == null || EditorUtility.IsPersistent(root) || !root.scene.IsValid()) continue;
            root.GetComponentsInChildren(true, rootPrimitives);
            created.AddRange(rootPrimitives);
        }
        GreyPrimitive.SplitSharedSceneEmbeddedMeshes(created);
    }

    static void OnChanges(ref ObjectChangeEventStream stream)
    {
        using var createdScope = ListPool<GameObject>.Get(out var createdRoots);
        for (int i = 0; i < stream.length; i++)
        {
            switch (stream.GetEventType(i))
            {
                case ObjectChangeKind.CreateGameObjectHierarchy:
                    stream.GetCreateGameObjectHierarchyEvent(i, out var created);
                    createdRoots.Add(EditorUtility.EntityIdToObject(created.instanceId) as GameObject);
                    break;
                case ObjectChangeKind.ChangeGameObjectStructure:
                    stream.GetChangeGameObjectStructureEvent(i, out var structure);
                    RegisterRoot(EditorUtility.EntityIdToObject(structure.instanceId) as GameObject);
                    break;
                case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                    Register(EditorUtility.EntityIdToObject(properties.instanceId) as Greybox);
                    GreyPrimitiveManagerEditor.Register(EditorUtility.EntityIdToObject(properties.instanceId) as GreyPrimitiveManager);
                    break;
            }
        }
        SplitDuplicatedMeshes(createdRoots);
        foreach (var root in createdRoots) RegisterRoot(root);
        ScheduleRefresh();
    }

    static void ScheduleRefresh()
    {
        if (s_scheduled) return;
        s_scheduled = true;
        EditorApplication.delayCall += Refresh;
    }

    static void Refresh()
    {
        s_scheduled = false;
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        GreyPrimitiveManagerEditor.NormalizePrimaryManagers();
        using var boxesScope = ListPool<Greybox>.Get(out var boxes);
        using var changedScope = ListPool<Greybox>.Get(out var changed);
        boxes.AddRange(s_faces.Keys);
        foreach (var box in boxes)
        {
            if (box == null) { s_faces.Remove(box); continue; }
            int mask = FaceMask(box);
            if (s_faces[box] == mask) continue;
            s_faces[box] = mask;
            changed.Add(box);
        }
        foreach (var box in changed) GreyPrimitiveEditor.RebuildPrimitiveAndDependents(box);
        GreyBooleanOrchestrator.RefreshStructure();
        if (changed.Count > 0) SceneView.RepaintAll();
    }
}
