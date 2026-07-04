using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Keeps prefab bakes intact when instance overrides are applied. An instance bake is
/// scene-embedded, so applying its mesh references pushes scene-object references into the prefab
/// file — Unity nulls or dangles them, breaking every other instance. After each apply, any grey
/// primitive in the prefab left without a valid embedded bake is re-baked directly on the asset
/// (fresh sub-assets, saved), and grey bake meshes orphaned by the apply are swept from the file.
/// Net effect: applying a local bake means "re-bake the prefab with the applied parameters".
/// </summary>
[InitializeOnLoad]
static class GreyPrimitiveApplyGuard
{
    static GreyPrimitiveApplyGuard()
    {
        PrefabUtility.prefabInstanceUpdated -= OnPrefabInstanceUpdated;
        PrefabUtility.prefabInstanceUpdated += OnPrefabInstanceUpdated;
    }

    // Fires after Apply All / Apply Component on an instance (and on other instance-update flows —
    // harmless: a healthy prefab no-ops below). Skipped while the editor is internally rewriting
    // state (import, compile, play-mode transition, build) — applies are user gestures and never
    // originate there.
    static void OnPrefabInstanceUpdated(GameObject instanceRoot)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying
            || EditorApplication.isCompiling
            || EditorApplication.isUpdating
            || BuildPipeline.isBuildingPlayer)
            return;

        HashSet<string> healedPrefabPaths = null;
        foreach (var prim in instanceRoot.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
        {
            // Legitimate filter: an object added on top of the instance has no source — nothing
            // of it was applied.
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prim);
            if (source == null) continue;
            if (source.PrefabAssetBakeIsValid()) continue;

            source.RebuildMesh(); // embeds fresh sub-assets into the prefab and writes the file
            healedPrefabPaths ??= new HashSet<string>();
            healedPrefabPaths.Add(AssetDatabase.GetAssetPath(source));
        }

        if (healedPrefabPaths == null) return;
        foreach (var path in healedPrefabPaths)
            SweepOrphanGreyMeshes(path);
    }

    // Removes grey bake meshes stranded in the prefab file by the apply — the prefab's previous
    // embedded bake becomes unreferenced the moment the applied fields overwrite the references.
    // Scoped to grey-named meshes that nothing under the prefab root references; other systems'
    // mesh sub-assets in the same file (legacy Kanbass embeds, hand-added meshes) are never touched.
    static void SweepOrphanGreyMeshes(string prefabPath)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        var referenced = new HashSet<Mesh>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(includeInactive: true))
            if (mf.sharedMesh != null) referenced.Add(mf.sharedMesh);
        foreach (var mc in root.GetComponentsInChildren<MeshCollider>(includeInactive: true))
            if (mc.sharedMesh != null) referenced.Add(mc.sharedMesh);
        foreach (var gp in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
        {
            if (gp.BakedRenderMesh != null) referenced.Add(gp.BakedRenderMesh);
            if (gp.ColliderMesh != null) referenced.Add(gp.ColliderMesh);
        }

        bool removedAny = false;
        foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(prefabPath))
        {
            // Every grey bake mesh is named "<Type> Mesh"/"<Type> Collider" and all grey types
            // start with "Grey" — the name gate is what keeps this sweep off foreign meshes.
            if (rep is not Mesh mesh) continue;
            if (!mesh.name.StartsWith("Grey")) continue;
            if (referenced.Contains(mesh)) continue;
            AssetDatabase.RemoveObjectFromAsset(mesh);
            removedAny = true;
        }

        if (removedAny)
        {
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
        }
    }
}
