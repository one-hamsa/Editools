using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
static class GreyPrimitiveSaveHook
{
    static GreyPrimitiveSaveHook()
    {
        EditorSceneManager.sceneSaved += OnSceneSaved;
        PrefabStage.prefabSaved += OnPrefabSaved;
    }

    static void OnSceneSaved(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
            NotifySaved(root);
    }

    static void OnPrefabSaved(GameObject prefabRoot) => NotifySaved(prefabRoot);

    static void NotifySaved(GameObject root)
    {
        foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
            prim.NotifyMeshSaved();
    }
}
