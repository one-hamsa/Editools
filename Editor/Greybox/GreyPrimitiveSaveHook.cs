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
    }

    static void OnSceneSaved(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
            foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
                prim.NotifyMeshSaved();
    }
}
