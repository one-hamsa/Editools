using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

/// <summary>
/// Mass Rename: batch-rename multiple selected objects in the Hierarchy or assets
/// in the Project view. Triggered by F2 when multiple items are selected.
///
/// Settings (applied in order):
///   1. Replace [A] with [B] — glob wildcards (* = any characters) supported in A.
///   2. Remove Prefix [N] — trim N characters from the start.
///   3. Remove Suffix [N] — trim N characters from the end.
///   4. Add Numbering — appends zero-padded index (by new alphabetical order).
///
/// Steps 1–3 are applied per-item in original alphabetical order.
/// Step 4 is applied after re-sorting by the new names.
///
/// Toggle on/off via the Editools toolbar overlay.
/// </summary>
public class MassRename : EditorWindow
{
    // ─── EditorPrefs toggle ─────────────────────────────────────

    const string k_Pref = "MassRename_";
    const string k_EnabledPref = k_Pref + "Enabled";

    internal static bool Enabled
    {
        get => EditorPrefs.GetBool(k_EnabledPref, true);
        set => EditorPrefs.SetBool(k_EnabledPref, value);
    }

    // ─── State ──────────────────────────────────────────────────

    string _findString = "";
    string _replaceString = "";
    int _removePrefix;
    int _removeSuffix;
    bool _addNumbering;
    string _numberingSeparator = " ";

    // Targets — either GameObjects (hierarchy) or asset paths (project)
    List<UnityEngine.Object> _targets = new List<UnityEngine.Object>();
    bool _isAssetMode;

    // Preview
    List<(string original, string result)> _preview = new List<(string, string)>();
    Vector2 _previewScroll;

    // ─── Shortcut ───────────────────────────────────────────────

    [Shortcut("Editools/Mass Rename", KeyCode.F2)]
    static void OnShortcut()
    {
        if (!Enabled) return;

        // Need multiple items selected
        bool hasGameObjects = Selection.gameObjects != null && Selection.gameObjects.Length > 0;
        bool hasAssetGUIDs = Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0;

        // Asset mode: use Selection.assetGUIDs for reliable cross-folder selection.
        // Hierarchy mode: use Selection.gameObjects.
        bool isAssetMode = hasAssetGUIDs && !hasGameObjects;

        int count = isAssetMode ? Selection.assetGUIDs.Length
            : (Selection.gameObjects != null ? Selection.gameObjects.Length : 0);
        if (count < 2) return;

        var window = GetWindow<MassRename>(true, "Mass Rename", true);
        window.Init(isAssetMode);
        window.CenterAndSize();
        window.ShowUtility();
    }

    // ─── Init ───────────────────────────────────────────────────

    void Init(bool assetMode)
    {
        _isAssetMode = assetMode;
        _targets.Clear();

        if (_isAssetMode)
        {
            // Use assetGUIDs for reliable cross-folder selection in the Project view.
            // Selection.objects can miss items when selecting across multiple folders.
            foreach (string guid in Selection.assetGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                var obj = AssetDatabase.LoadMainAssetAtPath(path);
                if (obj != null)
                    _targets.Add(obj);
            }
        }
        else
        {
            foreach (var go in Selection.gameObjects)
                _targets.Add(go);
        }

        if (_targets.Count < 2)
        {
            Close();
            return;
        }

        // Sort alphabetically by name
        _targets.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        // Auto-detect shared prefix for find/replace defaults
        string prefix = FindSharedPrefix(_targets.Select(o => o.name).ToList());
        _findString = prefix;
        _replaceString = prefix;

        _removePrefix = 0;
        _removeSuffix = 0;
        _addNumbering = false;
        _numberingSeparator = " ";

        UpdatePreview();
    }

    // ─── Window Sizing ──────────────────────────────────────────

    const float k_SettingsHeight = 160f; // settings section + header + spacing
    const float k_PreviewRowHeight = 21f;
    const float k_BottomPadding = 44f;   // buttons + spacing
    const float k_MaxHeight = 600f;
    const float k_DefaultWidth = 420f;

    float CalculateHeight()
    {
        float previewHeight = _preview.Count * k_PreviewRowHeight;
        float totalHeight = k_SettingsHeight + previewHeight + k_BottomPadding;
        return Mathf.Clamp(totalHeight, 200f, k_MaxHeight);
    }

    /// <summary>
    /// Sets the window size based on content and centers it on the main editor window.
    /// Must be called before ShowUtility so position is set before the window appears.
    /// </summary>
    void CenterAndSize()
    {
        float height = CalculateHeight();
        float width = k_DefaultWidth;

        // Center on the main Unity editor window
        var main = EditorGUIUtility.GetMainWindowPosition();
        float x = main.x + (main.width - width) * 0.5f;
        float y = main.y + (main.height - height) * 0.5f;

        position = new Rect(x, y, width, height);
        minSize = new Vector2(width, 200f);
        maxSize = new Vector2(800, k_MaxHeight);
    }

    // ─── GUI ────────────────────────────────────────────────────

    void OnGUI()
    {
        if (_targets == null || _targets.Count == 0)
        {
            EditorGUILayout.HelpBox("No valid selection. Close and try again.", MessageType.Warning);
            return;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();

        // 1) Replace
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Replace", GUILayout.Width(55));
        _findString = EditorGUILayout.TextField(_findString);
        EditorGUILayout.LabelField("with", GUILayout.Width(30));
        _replaceString = EditorGUILayout.TextField(_replaceString);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(55 + 4); // align with fields above
        EditorGUILayout.LabelField("Use * as wildcard (e.g. obj*max)",
            EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);

        // 2) Remove prefix
        _removePrefix = Mathf.Max(0, EditorGUILayout.IntField(
            new GUIContent("Remove Prefix", "Number of characters to remove from the start of each name"),
            _removePrefix));

        // 3) Remove suffix
        _removeSuffix = Mathf.Max(0, EditorGUILayout.IntField(
            new GUIContent("Remove Suffix", "Number of characters to remove from the end of each name"),
            _removeSuffix));

        // 4) Add numbering — separator field appears inline to the right of the checkbox
        EditorGUILayout.BeginHorizontal();
        _addNumbering = EditorGUILayout.Toggle(
            new GUIContent("Add Numbering", "Appends a zero-padded number to each name based on alphabetical order"),
            _addNumbering);
        if (_addNumbering)
        {
            _numberingSeparator = EditorGUILayout.TextField(_numberingSeparator, GUILayout.Width(40));
            EditorGUILayout.LabelField("n", EditorStyles.miniLabel, GUILayout.Width(12));
        }
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        if (EditorGUI.EndChangeCheck())
            UpdatePreview();

        // ─── Preview ────────────────────────────────────────────
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

        _previewScroll = EditorGUILayout.BeginScrollView(_previewScroll,
            GUILayout.ExpandHeight(true));

        var defaultColor = GUI.color;
        foreach (var (original, result) in _preview)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(original, GUILayout.MinWidth(80));
            EditorGUILayout.LabelField("\u2192", GUILayout.Width(20)); // →

            // Highlight changed names
            if (original != result)
                GUI.color = new Color(0.5f, 1f, 0.5f);
            EditorGUILayout.LabelField(result, GUILayout.MinWidth(80));
            GUI.color = defaultColor;

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();

        // ─── Buttons ────────────────────────────────────────────
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            Close();

        EditorGUI.BeginDisabledGroup(!HasAnyChanges());
        if (GUILayout.Button("Rename", GUILayout.Width(80)))
        {
            ExecuteRename();
            Close();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);
    }

    // ─── Preview / Rename Logic ─────────────────────────────────

    void UpdatePreview()
    {
        _preview.Clear();

        // Build result names in original alphabetical order (steps 1-3)
        var items = new List<(UnityEngine.Object obj, string original, string renamed)>();

        foreach (var obj in _targets)
        {
            string name = obj.name;
            string result = ApplyFindReplace(name);
            result = ApplyRemovePrefix(result);
            result = ApplyRemoveSuffix(result);
            items.Add((obj, name, result));
        }

        // Step 4: numbering — re-sort by new name, then append numbers
        if (_addNumbering)
        {
            items.Sort((a, b) => string.Compare(a.renamed, b.renamed, StringComparison.OrdinalIgnoreCase));
            int digits = items.Count.ToString().Length;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                items[i] = (item.obj, item.original, item.renamed + _numberingSeparator + (i + 1).ToString().PadLeft(digits, '0'));
            }
        }

        foreach (var item in items)
            _preview.Add((item.original, item.renamed));
    }

    string ApplyFindReplace(string name)
    {
        if (string.IsNullOrEmpty(_findString))
            return name;

        // If wildcards present, anchor with ^...$ so globs match the full name
        // (prevents .* double-matching the empty string). Without wildcards, do
        // plain substring replacement.
        if (_findString.Contains("*"))
        {
            string pattern = "^" + Regex.Escape(_findString).Replace("\\*", ".*") + "$";
            return Regex.Replace(name, pattern, _replaceString, RegexOptions.IgnoreCase);
        }
        return name.Replace(_findString, _replaceString);
    }

    string ApplyRemovePrefix(string name)
    {
        if (_removePrefix <= 0 || _removePrefix >= name.Length)
            return _removePrefix >= name.Length ? "" : name;
        return name.Substring(_removePrefix);
    }

    string ApplyRemoveSuffix(string name)
    {
        if (_removeSuffix <= 0 || _removeSuffix >= name.Length)
            return _removeSuffix >= name.Length ? "" : name;
        return name.Substring(0, name.Length - _removeSuffix);
    }

    bool HasAnyChanges()
    {
        return _preview.Any(p => p.original != p.result);
    }

    // ─── Execute ────────────────────────────────────────────────

    void ExecuteRename()
    {
        // Build final names (same logic as preview)
        var items = new List<(UnityEngine.Object obj, string original, string renamed)>();
        foreach (var obj in _targets)
        {
            string name = obj.name;
            string result = ApplyFindReplace(name);
            result = ApplyRemovePrefix(result);
            result = ApplyRemoveSuffix(result);
            items.Add((obj, name, result));
        }

        if (_addNumbering)
        {
            items.Sort((a, b) => string.Compare(a.renamed, b.renamed, StringComparison.OrdinalIgnoreCase));
            int digits = items.Count.ToString().Length;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                items[i] = (item.obj, item.original, item.renamed + _numberingSeparator + (i + 1).ToString().PadLeft(digits, '0'));
            }
        }

        // Filter to only changed names
        var toRename = items.Where(i => i.original != i.renamed).ToList();
        if (toRename.Count == 0) return;

        // Register undo group
        Undo.SetCurrentGroupName("Mass Rename");
        int undoGroup = Undo.GetCurrentGroup();

        if (_isAssetMode)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (obj, original, renamed) in toRename)
                {
                    string assetPath = AssetDatabase.GetAssetPath(obj);
                    if (string.IsNullOrEmpty(assetPath)) continue;

                    // Undo for asset rename
                    Undo.RecordObject(obj, "Mass Rename");

                    string error = AssetDatabase.RenameAsset(assetPath, renamed);
                    if (!string.IsNullOrEmpty(error))
                        Debug.LogWarning($"[Mass Rename] Failed to rename '{original}' → '{renamed}': {error}");
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }
        else
        {
            // Hierarchy GameObjects
            foreach (var (obj, original, renamed) in toRename)
            {
                var go = obj as GameObject;
                if (go == null) continue;

                Undo.RecordObject(go, "Mass Rename");
                go.name = renamed;
                EditorUtility.SetDirty(go);
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
    }

    // ─── Utilities ──────────────────────────────────────────────

    /// <summary>
    /// Finds the longest common prefix among a list of names.
    /// Only considers whole "word" boundaries (stops at the last space or separator
    /// before the prefix diverges). Returns empty string if no meaningful prefix found.
    /// </summary>
    static string FindSharedPrefix(List<string> names)
    {
        if (names == null || names.Count < 2) return "";

        string first = names[0];
        int prefixLen = first.Length;

        for (int i = 1; i < names.Count; i++)
        {
            string other = names[i];
            int len = Mathf.Min(prefixLen, other.Length);
            int match = 0;
            while (match < len &&
                   char.ToLowerInvariant(first[match]) == char.ToLowerInvariant(other[match]))
                match++;
            prefixLen = match;
            if (prefixLen == 0) return "";
        }

        string rawPrefix = first.Substring(0, prefixLen);

        // Trim to last word boundary (space, underscore, dash) for cleaner defaults
        int lastSep = rawPrefix.LastIndexOfAny(new[] { ' ', '_', '-' });
        if (lastSep > 0 && lastSep < rawPrefix.Length - 1)
            rawPrefix = rawPrefix.Substring(0, lastSep + 1);

        return rawPrefix.TrimEnd();
    }
}
