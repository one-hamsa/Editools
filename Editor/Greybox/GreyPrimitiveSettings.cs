#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Shared creation defaults for the Grey Primitive family and the Grey Primitives gate. Values live
/// in <see cref="EditoolsSettings"/> and are edited in its inspector.
/// </summary>
static class GreyPrimitiveSettings
{
    /// <summary>Gate for creation shortcuts, menu items and Edit Mode.</summary>
    internal static bool Enabled => EditoolsSettings.Current.greyPrimitivesEnabled;

    /// <summary>Gate check for the Scene View creation shortcuts.</summary>
    internal static bool CanCreate => EditoolsOverlay.IsActive && Enabled;

    internal static bool AutoUpdatePivot => EditoolsSettings.Current.greyAutoUpdatePivot;
    internal static Material DefaultMaterial => EditoolsSettings.Current.greyDefaultMaterial;
    internal static bool DefaultStatic => EditoolsSettings.Current.greyDefaultStatic;
    internal static int DefaultLayer => EditoolsSettings.Current.greyDefaultLayer;
    internal static bool DefaultMeshCollider => EditoolsSettings.Current.greyDefaultMeshCollider;
    internal static bool DefaultShadowCasting => EditoolsSettings.Current.greyDefaultShadowCasting;

    internal static Material GetOrCreateDefaultMaterial()
    {
        var mat = DefaultMaterial;
        if (mat != null) return mat;
        return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
    }

    // meshCollider / castShadows override the shared prefs when set; null falls back to the
    // DefaultMeshCollider / DefaultShadowCasting settings (Greyquad hardcodes both off).
    internal static GameObject PlacePrimitive<T>(string name, Vector3 worldPos, Quaternion worldRot, Transform parent,
        bool? meshCollider = null, bool? castShadows = null, bool select = true)
        where T : GreyPrimitive
    {
        int undoGroup = Undo.GetCurrentGroup();
        parent = ResolveParent(parent);
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, parent.gameObject.scene);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

        go.AddComponent<T>();
        if (meshCollider ?? DefaultMeshCollider) go.AddComponent<MeshCollider>();

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial     = GetOrCreateDefaultMaterial();
            mr.shadowCastingMode  = (castShadows ?? DefaultShadowCasting) ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        go.transform.position = worldPos;
        go.transform.rotation = worldRot;

        go.isStatic = DefaultStatic;
        go.layer    = DefaultLayer;

        if (select) Selection.activeObject = go;
        Undo.CollapseUndoOperations(undoGroup);
        return go;
    }

    internal static Transform ResolveParent(Transform parent, Scene scene = default, Transform excludedRoot = null)
    {
        if (parent != null && parent.GetComponentInParent<GreyPrimitiveManager>(true) != null)
            return parent;

        var stage = parent != null ? PrefabStageUtility.GetPrefabStage(parent.gameObject)
            : PrefabStageUtility.GetCurrentPrefabStage();
        if (parent != null) scene = parent.gameObject.scene;
        else if (!scene.IsValid())
            scene = stage != null ? stage.scene
                : Selection.activeGameObject != null ? Selection.activeGameObject.scene : SceneManager.GetActiveScene();
        if (!scene.IsValid()) scene = SceneManager.GetActiveScene();
        if (stage != null && stage.scene != scene) stage = null;

        Transform closest = null;
        int closestDistance = int.MaxValue;
        var roots = stage != null ? new[] { stage.prefabContentsRoot } : scene.GetRootGameObjects();
        foreach (var root in roots)
            foreach (var manager in root.GetComponentsInChildren<GreyPrimitiveManager>(true))
            {
                if (excludedRoot != null && manager.transform.IsChildOf(excludedRoot)) continue;
                if (manager.Primary) return manager.transform;
                int distance = HierarchyDistance(parent, manager.transform);
                if (distance >= closestDistance) continue;
                closest = manager.transform;
                closestDistance = distance;
            }
        if (closest != null) return closest;

        var geometry = new GameObject("Geometry");
        SceneManager.MoveGameObjectToScene(geometry, scene);
        if (stage != null) geometry.transform.SetParent(stage.prefabContentsRoot.transform, false);
        geometry.transform.position = Vector3.zero;
        geometry.transform.rotation = Quaternion.identity;
        Undo.RegisterCreatedObjectUndo(geometry, "Create Geometry");
        Undo.AddComponent<GreyPrimitiveManager>(geometry);
        return geometry.transform;
    }

    static int HierarchyDistance(Transform from, Transform to)
    {
        int up = 0;
        for (var ancestor = from; ancestor != null; ancestor = ancestor.parent, up++)
        {
            int down = 0;
            for (var candidate = to; candidate != null; candidate = candidate.parent, down++)
                if (candidate == ancestor) return up + down;
        }
        for (var candidate = to; candidate != null; candidate = candidate.parent) up++;
        return up;
    }

}
#endif
