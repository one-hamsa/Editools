using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Removes grey bake meshes that nothing references from prefab files. Used by the apply guard after
/// it re-bakes a prefab, and by the "Remove Unreferenced Grey Meshes" command for selected prefabs
/// (or every prefab under selected folders), which also keeps meshes other assets reference.
/// Only meshes named like a grey bake are candidates; other mesh sub-assets in the same file
/// (legacy Kanbass embeds, hand-added meshes) are never touched.
/// </summary>
static class GreyMeshOrphanSweep
{
    const string MenuPath = "One Hamsa/Editools/Greybox/Remove Unreferenced Grey Meshes";
    const string AssetsMenuPath = "Assets/One Hamsa/Editools/Remove Unreferenced Grey Meshes";

    /// <summary>Adds to <paramref name="result"/> every grey mesh sub-asset of the prefab that no
    /// MeshFilter, MeshCollider or grey primitive under the prefab root references.</summary>
    internal static void CollectUnreferencedGreyMeshes(string prefabPath, List<Mesh> result)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        using var referencedScope = HashSetPool<Mesh>.Get(out var referenced);
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(includeInactive: true))
            if (mf.sharedMesh != null) referenced.Add(mf.sharedMesh);
        foreach (var mc in root.GetComponentsInChildren<MeshCollider>(includeInactive: true))
            if (mc.sharedMesh != null) referenced.Add(mc.sharedMesh);
        foreach (var gp in root.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
        {
            if (gp.BakedRenderMesh != null) referenced.Add(gp.BakedRenderMesh);
            if (gp.ColliderMesh != null) referenced.Add(gp.ColliderMesh);
        }

        foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(prefabPath))
        {
            // Every grey bake mesh is named "<Type> Mesh"/"<Type> Collider" and all grey types
            // start with "Grey" - the name gate is what keeps this sweep off foreign meshes.
            if (rep is not Mesh mesh) continue;
            if (!mesh.name.StartsWith("Grey")) continue;
            if (referenced.Contains(mesh)) continue;
            result.Add(mesh);
        }
    }

    /// <summary>Removes <paramref name="meshes"/> (sub-assets of the prefab) from the prefab file and
    /// writes it.</summary>
    internal static void RemoveAndSave(string prefabPath, List<Mesh> meshes)
    {
        if (meshes.Count == 0) return;
        foreach (var mesh in meshes)
            AssetDatabase.RemoveObjectFromAsset(mesh);
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        EditorUtility.SetDirty(root);
        AssetDatabase.SaveAssetIfDirty(root);
    }

    [MenuItem(MenuPath)]
    [MenuItem(AssetsMenuPath)]
    static void RemoveFromSelection()
    {
        using var pathsScope = ListPool<string>.Get(out var prefabPaths);
        CollectSelectedPrefabPaths(prefabPaths);

        var openStage = PrefabStageUtility.GetCurrentPrefabStage();
        var candidates = new Dictionary<string, List<Mesh>>(prefabPaths.Count);
        int candidateCount = 0;
        foreach (var path in prefabPaths)
        {
            // The open stage writes the whole file on its next save, from its own copy of it.
            if (openStage != null && openStage.assetPath == path)
            {
                Debug.LogError($"[Greybox] Skipped '{path}': it is open in Prefab Mode. Close it and run the command again.");
                continue;
            }
            var meshes = new List<Mesh>();
            CollectUnreferencedGreyMeshes(path, meshes);
            if (meshes.Count == 0) continue;
            candidates.Add(path, meshes);
            candidateCount += meshes.Count;
        }

        if (candidateCount == 0)
        {
            EditorUtility.DisplayDialog("Remove Unreferenced Grey Meshes",
                $"No unreferenced grey meshes in {prefabPaths.Count} prefab(s).", "OK");
            return;
        }

        if (!KeepExternallyReferenced(candidates, out int keptCount)) return;

        var report = new StringBuilder();
        int removeCount = 0;
        foreach (var pair in candidates)
        {
            if (pair.Value.Count == 0) continue;
            removeCount += pair.Value.Count;
            report.AppendLine($"{pair.Key}: {pair.Value.Count}");
        }
        Debug.Log($"[Greybox] Unreferenced grey meshes ({removeCount}, {keptCount} kept because other assets reference them):\n{report}");

        if (removeCount == 0)
        {
            EditorUtility.DisplayDialog("Remove Unreferenced Grey Meshes",
                $"Every unreferenced grey mesh ({keptCount}) is referenced by another asset. Nothing removed.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("Remove Unreferenced Grey Meshes",
                $"Remove {removeCount} grey mesh sub-asset(s) from {CountNonEmpty(candidates)} prefab(s)?\n\n{report}\n" +
                $"{keptCount} kept because other assets reference them. This cannot be undone.",
                "Remove", "Cancel"))
            return;

        foreach (var pair in candidates)
            RemoveAndSave(pair.Key, pair.Value);
        Debug.Log($"[Greybox] Removed {removeCount} unreferenced grey mesh(es) from {CountNonEmpty(candidates)} prefab(s).");
    }

    [MenuItem(MenuPath, isValidateFunction: true)]
    [MenuItem(AssetsMenuPath, isValidateFunction: true)]
    static bool RemoveFromSelectionValidate() => Selection.assetGUIDs.Length > 0;

    // Selected prefabs, plus every prefab under selected folders.
    static void CollectSelectedPrefabPaths(List<string> result)
    {
        using var foldersScope = ListPool<string>.Get(out var folders);
        foreach (var guid in Selection.assetGUIDs)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.IsValidFolder(path)) folders.Add(path);
            else if (path.EndsWith(".prefab")) result.Add(path);
        }
        if (folders.Count == 0) return;
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", folders.ToArray()))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!result.Contains(path)) result.Add(path);
        }
    }

    // A scene or another prefab can reference a prefab's mesh sub-asset directly (an object pasted
    // out of the prefab, an instance override), which the in-file scan does not see. Removes such
    // meshes from the candidates by scanning every scene, prefab and asset file for
    // "fileID: <local id>, guid: <prefab guid>". Returns false when the user cancelled the scan.
    static bool KeepExternallyReferenced(Dictionary<string, List<Mesh>> candidates, out int keptCount)
    {
        keptCount = 0;
        var byGuid = new Dictionary<string, (string path, Dictionary<long, Mesh> meshes)>(candidates.Count);
        foreach (var pair in candidates)
        {
            var meshes = new Dictionary<long, Mesh>(pair.Value.Count);
            string prefabGuid = null;
            foreach (var mesh in pair.Value)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out prefabGuid, out long localId);
                meshes.Add(localId, mesh);
            }
            byGuid.Add(prefabGuid, (pair.Key, meshes));
        }

        var files = new List<string>(4096);
        foreach (var file in Directory.EnumerateFiles(Application.dataPath, "*", SearchOption.AllDirectories))
            if (file.EndsWith(".unity") || file.EndsWith(".prefab") || file.EndsWith(".asset"))
                files.Add(file);

        using var referencedScope = HashSetPool<Mesh>.Get(out var externallyReferenced);
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Remove Unreferenced Grey Meshes",
                        "Scanning assets for references", (float)i / files.Count))
                    return false;

                string text = File.ReadAllText(files[i]);
                foreach (var pair in byGuid)
                {
                    if (text.IndexOf(pair.Key, System.StringComparison.Ordinal) < 0) continue;
                    // Unity wraps long references, so the guid can follow on the next line.
                    foreach (Match match in Regex.Matches(text, @"fileID: (-?\d+),\s+guid: " + pair.Key))
                        if (pair.Value.meshes.TryGetValue(long.Parse(match.Groups[1].Value), out var mesh))
                            externallyReferenced.Add(mesh);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        foreach (var pair in candidates)
            keptCount += pair.Value.RemoveAll(externallyReferenced.Contains);
        return true;
    }

    static int CountNonEmpty(Dictionary<string, List<Mesh>> candidates)
    {
        int count = 0;
        foreach (var pair in candidates)
            if (pair.Value.Count > 0) count++;
        return count;
    }
}
