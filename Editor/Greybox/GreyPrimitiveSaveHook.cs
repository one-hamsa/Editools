using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
static class GreyPrimitiveSaveHook
{
    static GreyPrimitiveSaveHook()
    {
        EditorSceneManager.sceneSaving += OnSceneSaving;
        EditorSceneManager.sceneSaved += OnSceneSaved;
        PrefabStage.prefabSaving += OnPrefabSaving;
        PrefabStage.prefabSaved += OnPrefabSaved;
    }

    // Pre-save: drop stale collider twins on types that reuse the render mesh (Greypipe, Greyroad)
    // so the cleared state is what gets written, not re-bloated next save.
    static void OnSceneSaving(Scene scene, string path)
    {
        foreach (var root in scene.GetRootGameObjects())
            ClearStaleColliders(root);
    }

    static void OnPrefabSaving(GameObject prefabRoot) => ClearStaleColliders(prefabRoot);

    static void OnSceneSaved(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
            NotifySaved(root);
    }

    static void OnPrefabSaved(GameObject prefabRoot) => NotifySaved(prefabRoot);

    static void ClearStaleColliders(GameObject root)
    {
        foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
            prim.ClearStaleColliderMesh();
    }

    static void NotifySaved(GameObject root)
    {
        foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
            prim.NotifyMeshSaved();
    }
}
