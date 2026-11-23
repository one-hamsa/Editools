using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps scene and prefab saves writing the grey primitives' real baked meshes: re-points an opened
/// prefab stage at the meshes embedded in the prefab file, drops stale collider twins before a save,
/// and writes dirty embedded meshes after one.
/// </summary>
[InitializeOnLoad]
static class GreyPrimitiveSaveHook
{
    static GreyPrimitiveSaveHook()
    {
        EditorSceneManager.sceneSaving += OnSceneSaving;
        EditorSceneManager.sceneSaved += OnSceneSaved;
        PrefabStage.prefabSaving += OnPrefabSaving;
        PrefabStage.prefabSaved += OnPrefabSaved;
        PrefabStage.prefabStageOpened -= RelinkStageMeshes;
        PrefabStage.prefabStageOpened += RelinkStageMeshes;

        // A stage left open across a domain reload never raises prefabStageOpened again. First update,
        // not delayCall: delayCall waits for editor focus, update keeps ticking in the background.
        EditorApplication.update += RelinkCurrentStageOnFirstUpdate;
    }

    static void RelinkCurrentStageOnFirstUpdate()
    {
        EditorApplication.update -= RelinkCurrentStageOnFirstUpdate;
        RelinkCurrentStage();
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

    static void RelinkCurrentStage()
    {
        // Legitimate filter: no prefab is open in Prefab Mode.
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null) RelinkStageMeshes(stage);
    }

    // Prefab Mode loads the prefab's embedded Mesh sub-assets as stage-owned copies, on open and on
    // the reload after a save; a stage save writes references to them as null. Pair each stage
    // primitive with the asset's (same hierarchy, same order) and point it at the file's meshes.
    // Runs before any save, so save hooks that compare stage meshes with the file's see a match.
    static void RelinkStageMeshes(PrefabStage stage)
    {
        var stagePrims = stage.prefabContentsRoot.GetComponentsInChildren<GreyPrimitive>(includeInactive: true);
        // Legitimate filter: a prefab without grey primitives has nothing to relink.
        if (stagePrims.Length == 0) return;

        var assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
        var assetPrims = assetRoot.GetComponentsInChildren<GreyPrimitive>(includeInactive: true);
        if (assetPrims.Length != stagePrims.Length)
        {
            Debug.LogError($"[Greybox] {stage.assetPath}: Prefab Mode holds {stagePrims.Length} grey primitives, " +
                           $"the prefab asset {assetPrims.Length}; baked meshes not relinked. Rebuild them before saving.");
            return;
        }

        for (int i = 0; i < stagePrims.Length; i++)
        {
            var stagePrim = stagePrims[i];
            var assetPrim = assetPrims[i];
            if (stagePrim.GetType() != assetPrim.GetType() || stagePrim.name != assetPrim.name)
            {
                Debug.LogError($"[Greybox] {stage.assetPath}: Prefab Mode primitive '{stagePrim.name}' " +
                               $"({stagePrim.GetType().Name}) does not match the asset's '{assetPrim.name}' " +
                               $"({assetPrim.GetType().Name}) at index {i}; baked meshes not relinked.", stagePrim);
                return;
            }
        }

        for (int i = 0; i < stagePrims.Length; i++)
            stagePrims[i].RelinkStageMeshes(assetPrims[i]);
    }
}
