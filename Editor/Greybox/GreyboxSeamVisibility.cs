using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Pool;

// Rebuild only boxes whose visible faces changed when a weld endpoint disappeared or returned.
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

    static int FaceMask(Greybox box, bool effective)
    {
        int mask = 0;
        for (int face = 0; face < 6; face++)
            if (effective ? box.IsFaceVisible(face) : box.ActiveFaces[face]) mask |= 1 << face;
        return mask;
    }

    static void Register(Greybox box)
    {
        if (box == null || EditorUtility.IsPersistent(box) || !box.gameObject.scene.IsValid()) return;
        if (box.RememberSeamFaces()) EditorUtility.SetDirty(box);
        if (!s_faces.ContainsKey(box)) s_faces.Add(box, FaceMask(box, false));
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

    static void OnChanges(ref ObjectChangeEventStream stream)
    {
        for (int i = 0; i < stream.length; i++)
        {
            switch (stream.GetEventType(i))
            {
                case ObjectChangeKind.CreateGameObjectHierarchy:
                    stream.GetCreateGameObjectHierarchyEvent(i, out var created);
                    RegisterRoot(EditorUtility.InstanceIDToObject(created.instanceId) as GameObject);
                    break;
                case ObjectChangeKind.ChangeGameObjectStructure:
                    stream.GetChangeGameObjectStructureEvent(i, out var structure);
                    RegisterRoot(EditorUtility.InstanceIDToObject(structure.instanceId) as GameObject);
                    break;
                case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                    Register(EditorUtility.InstanceIDToObject(properties.instanceId) as Greybox);
                    GreyPrimitiveManagerEditor.Register(EditorUtility.InstanceIDToObject(properties.instanceId) as GreyPrimitiveManager);
                    break;
            }
        }
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
            int mask = FaceMask(box, true);
            if (s_faces[box] == mask) continue;
            s_faces[box] = mask;
            changed.Add(box);
        }
        foreach (var box in changed) GreyPrimitiveEditor.RebuildPrimitiveAndDependents(box);
        GreyBooleanOrchestrator.RefreshStructure();
        if (changed.Count > 0) SceneView.RepaintAll();
    }
}
