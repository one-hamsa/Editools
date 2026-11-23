using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Keeps prefab bakes intact when instance overrides are applied. An instance bake is
/// scene-embedded, so applying its mesh references pushes scene-object references into the prefab
/// file — Unity nulls them, breaking every instance that follows the prefab. When the apply (or an
/// undo/redo of one) has finished, any grey primitive in the prefab left without a valid embedded
/// bake is re-baked directly on the asset (fresh sub-assets, saved), and grey bake meshes orphaned
/// by the apply are swept from the file. The instance applied with Apply All then drops its local
/// bake and shows the prefab's.
/// Net effect: applying a local bake means "re-bake the prefab with the applied parameters".
/// </summary>
[InitializeOnLoad]
static class GreyPrimitiveApplyGuard
{
    // Prefab-asset primitives reported without a valid bake during the current apply or undo/redo.
    // Filled by prefabInstanceUpdated, re-baked when the operation ends; entries left by any other
    // instance-update flow are dropped on the next editor update.
    static readonly HashSet<GreyPrimitive> s_invalidSources = new HashSet<GreyPrimitive>();

    static GreyPrimitiveApplyGuard()
    {
        PrefabUtility.prefabInstanceUpdated -= OnPrefabInstanceUpdated;
        PrefabUtility.prefabInstanceUpdated += OnPrefabInstanceUpdated;
        PrefabUtility.prefabInstanceApplied -= OnPrefabInstanceApplied;
        PrefabUtility.prefabInstanceApplied += OnPrefabInstanceApplied;
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
    }

    // Fires for every instance of the prefab while Unity propagates an apply, and for the prefab
    // asset root itself on a component or property apply. Only records here: re-baking the asset
    // while Unity is still updating instances makes it record the instances' stale (nulled) mesh
    // references as overrides against the re-baked prefab. Skipped while the editor is internally
    // rewriting state (import, compile, play-mode transition, build).
    static void OnPrefabInstanceUpdated(GameObject root)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying
            || EditorApplication.isCompiling
            || EditorApplication.isUpdating
            || BuildPipeline.isBuildingPlayer)
            return;

        foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
        {
            var source = PrefabUtility.IsPartOfPrefabAsset(prim)
                ? prim
                : PrefabUtility.GetCorrespondingObjectFromSource(prim);
            // Legitimate filter: an object added on top of the instance has no source — nothing
            // of it was applied.
            if (source == null) continue;
            if (source.PrefabAssetBakeIsValid()) continue;

            if (s_invalidSources.Count == 0)
            {
                EditorApplication.update -= DropUnhandledSources;
                EditorApplication.update += DropUnhandledSources;
            }
            s_invalidSources.Add(source);
        }
    }

    // Fires once after all instances were updated. The root is the applied instance for Apply All,
    // the prefab asset root for a component or property apply.
    static void OnPrefabInstanceApplied(GameObject root)
    {
        using var rebakedScope = HashSetPool<GreyPrimitive>.Get(out var rebaked);
        RebakeInvalidSources(rebaked);
        if (rebaked.Count == 0 || PrefabUtility.IsPartOfPrefabAsset(root)) return;

        // Apply All leaves the instance overriding only the mesh references Unity could not apply;
        // reverting them makes it follow the prefab bake that now holds the same geometry.
        foreach (var prim in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
            if (rebaked.Contains(PrefabUtility.GetCorrespondingObjectFromSource(prim)))
                prim.RevertLocalBakeIfUnmodified();
    }

    // Undoing an apply restores the prefab's references to the bake the apply replaced, which was
    // swept from the file — the prefab needs a fresh bake of the restored parameters.
    static void OnUndoRedoPerformed()
    {
        using var rebakedScope = HashSetPool<GreyPrimitive>.Get(out var rebaked);
        RebakeInvalidSources(rebaked);
    }

    static void DropUnhandledSources()
    {
        EditorApplication.update -= DropUnhandledSources;
        s_invalidSources.Clear();
    }

    // Re-bakes each recorded source still invalid and sweeps its prefab file. Re-baking fires
    // prefabInstanceUpdated again, so the recorded set is copied out first.
    static void RebakeInvalidSources(HashSet<GreyPrimitive> rebaked)
    {
        if (s_invalidSources.Count == 0) return;
        using var sourcesScope = ListPool<GreyPrimitive>.Get(out var sources);
        sources.AddRange(s_invalidSources);
        s_invalidSources.Clear();

        using var pathsScope = HashSetPool<string>.Get(out var rebakedPaths);
        foreach (var source in sources)
        {
            if (source.PrefabAssetBakeIsValid()) continue;
            source.RebuildMesh(); // embeds fresh sub-assets into the prefab and writes the file
            rebaked.Add(source);
            rebakedPaths.Add(AssetDatabase.GetAssetPath(source));
        }

        foreach (var path in rebakedPaths)
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
