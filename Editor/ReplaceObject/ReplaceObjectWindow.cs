using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Replace Object: swaps every currently selected Hierarchy object with a duplicate
/// of a single reference object. Each replacement inherits its original's transform
/// (parent, world position/rotation, local scale, sibling index) and name.
///
/// The reference may be a prefab asset (replacements become linked prefab instances)
/// or a scene object (replacements are plain duplicates of its current state).
///
/// Opened from the Editools Settings popup.
/// </summary>
public class ReplaceObjectWindow : EditorWindow
{
    static readonly GUIContent k_ReferenceLabel = new GUIContent(
        "Replace With",
        "Prefab asset or scene object to duplicate. Selected objects become copies of this.");

    GameObject _reference;

    public static void ShowWindow()
    {
        var window = GetWindow<ReplaceObjectWindow>(true, "Replace Object", true);
        window.minSize = new Vector2(320, 120);
        window.maxSize = new Vector2(320, 120);
        window.Show();
    }

    void OnSelectionChange() => Repaint();

    void OnGUI()
    {
        EditorGUILayout.Space(8);

        int selectionCount = CountSceneSelection();
        EditorGUILayout.HelpBox(
            selectionCount == 0
                ? "Select one or more objects in the Hierarchy to replace."
                : $"{selectionCount} selected object(s) will be replaced.",
            selectionCount == 0 ? MessageType.Warning : MessageType.Info);

        EditorGUILayout.Space(6);
        _reference = (GameObject)EditorGUILayout.ObjectField(
            k_ReferenceLabel, _reference, typeof(GameObject), true);

        EditorGUILayout.Space(10);
        using (new EditorGUI.DisabledScope(_reference == null || selectionCount == 0))
        {
            if (GUILayout.Button("Replace", GUILayout.Height(30)))
                DoReplace();
        }
    }

    static int CountSceneSelection()
    {
        int count = 0;
        foreach (var go in Selection.gameObjects)
            if (go != null && !EditorUtility.IsPersistent(go))
                count++;
        return count;
    }

    void DoReplace()
    {
        if (_reference == null) return;

        // Snapshot the targets: scene objects only, and never the reference itself
        // (replacing it would destroy the source mid-loop).
        var targets = new List<GameObject>();
        foreach (var go in Selection.gameObjects)
        {
            if (go == null || EditorUtility.IsPersistent(go)) continue;
            if (go == _reference) continue;
            targets.Add(go);
        }

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Nothing To Replace",
                "No scene objects are selected (the reference itself is skipped).",
                "OK");
            return;
        }

        Undo.SetCurrentGroupName($"Replace {targets.Count} object(s) with '{_reference.name}'");
        int undoGroup = Undo.GetCurrentGroup();

        var replacements = new List<GameObject>(targets.Count);
        foreach (var target in targets)
        {
            var tf = target.transform;
            var parent = tf.parent;
            var position = tf.position;
            var rotation = tf.rotation;
            var localScale = tf.localScale;
            var siblingIndex = tf.GetSiblingIndex();
            var targetName = target.name;

            var replacement = CreateDuplicate(_reference, parent);
            Undo.RegisterCreatedObjectUndo(replacement, "Replace Object");

            var rt = replacement.transform;
            rt.position = position;
            rt.rotation = rotation;
            rt.localScale = localScale;
            rt.SetSiblingIndex(siblingIndex);
            replacement.name = targetName;

            Undo.DestroyObjectImmediate(target);
            replacements.Add(replacement);
        }

        Undo.CollapseUndoOperations(undoGroup);
        Selection.objects = replacements.ToArray();

        Debug.Log($"[Editools] Replace Object: replaced {replacements.Count} object(s) with '{_reference.name}'.");
    }

    /// <summary>
    /// Prefab asset reference → a linked prefab instance. Scene object reference →
    /// a plain duplicate of its current state.
    /// </summary>
    static GameObject CreateDuplicate(GameObject reference, Transform parent)
    {
        if (EditorUtility.IsPersistent(reference))
            return (GameObject)PrefabUtility.InstantiatePrefab(reference, parent);

        var dup = Object.Instantiate(reference, parent);
        return dup;
    }
}
