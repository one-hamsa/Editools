using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Color-codes recently selected items in the Hierarchy and Project windows and draws Alt+D marks.
/// Colors and limits live in <see cref="EditoolsSettings"/>; the window hooks are subscribed only
/// while the function is enabled and the Editools overlay is displayed (<see cref="Sync"/>).
/// </summary>
public static class HierarchyHeatmap
{
    static EditoolsSettings Settings => EditoolsSettings.Current;
    static int maxRecent => Settings.heatmapMaxRecent;
    static int maxMarked => Settings.heatmapMaxMarked;
    private static List<int> recentHierarchySelections = new List<int>(); // Index 0: oldest, last: newest
    private static List<string> recentProjectSelections = new List<string>(); // Index 0: oldest, last: newest
    static bool s_subscribed;

    static Color lowHeatColor => Settings.heatmapLowColor;
    static Color highHeatColor => Settings.heatmapHighColor;
    static float heatAlpha => Settings.heatmapAlpha;
    static float gradientStrength => Settings.heatmapGradientStrength;

    // Mark functionality
    private static List<int> markedHierarchyItems = new List<int>(); // Changed to List to maintain order
    private static List<string> markedProjectItems = new List<string>(); // Changed to List to maintain order
    static Color markColor => Settings.heatmapMarkColor;
    static float markAlpha => Settings.heatmapMarkAlpha;

    private static string lastFolderPath = string.Empty;
    private static MethodInfo getActiveFolderPathMethod;

    // Hierarchy reflection
    private static Type hierarchyWindowType;
    private static EditorWindow hierarchyWindow;
    private static object treeView;
    private static object hierarchyTreeViewController;
    private static MethodInfo isExpandedMethod;
    private static bool hierarchyReflectionSetup = false;

    // Project reflection
    private static Type projectBrowserType;
    private static EditorWindow projectWindow;
    private static object projectTreeViewController;
    private static MethodInfo projectIsExpandedMethod;
    private static bool projectReflectionSetup = false;

    // Resolved-object caches. Walking markedHierarchyItems / recentHierarchySelections
    // and calling EditorUtility.EntityIdToObject for every entry on every row redraw
    // (hundreds of rows * 5-10 entries) is what makes this expensive. We rebuild
    // these caches lazily when the source lists change, then per-row code just
    // walks the cached Transforms/strings.
    private static Transform[] s_markedHierarchyTransforms = System.Array.Empty<Transform>();
    private static Transform[] s_recentHierarchyTransforms = System.Array.Empty<Transform>();
    private static string[]    s_markedProjectPaths       = System.Array.Empty<string>();
    private static string[]    s_recentProjectPaths       = System.Array.Empty<string>();
    private static bool s_markedHierarchyDirty = true;
    private static bool s_recentHierarchyDirty = true;
    private static bool s_markedProjectDirty   = true;
    private static bool s_recentProjectDirty   = true;

    // Latches so reflection-failure warnings only fire once per session, not once per
    // hierarchy/project row every repaint.
    private static bool s_warnedHierarchyReflectionSetup;
    private static bool s_warnedHierarchyIsExpanded;
    private static bool s_warnedProjectReflectionSetup;
    private static bool s_warnedProjectIsExpanded;

    static HierarchyHeatmap() {
        LoadMarkedItems();

        // Setup reflection for active folder path
        var assembly = typeof(UnityEditor.Editor).Assembly;
        var projectWindowUtilType = assembly.GetType("UnityEditor.ProjectWindowUtil");
        if (projectWindowUtilType != null) {
            getActiveFolderPathMethod = projectWindowUtilType.GetMethod("GetActiveFolderPath", BindingFlags.Static | BindingFlags.NonPublic);
        }

        // Pre-load types
        hierarchyWindowType = assembly.GetType("UnityEditor.SceneHierarchyWindow");
        projectBrowserType = assembly.GetType("UnityEditor.ProjectBrowser");
    }

    internal static bool Enabled => Settings.hierarchyHeatmapEnabled;

    /// <summary>Hooks the Hierarchy / Project windows while enabled and the overlay is active; unhooks otherwise.</summary>
    internal static void Sync() {
        bool wanted = EditoolsOverlay.IsActive && Enabled;
        if (wanted == s_subscribed) return;
        s_subscribed = wanted;

        if (wanted) {
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGUI;
            EditorApplication.projectWindowItemOnGUI += OnProjectItemGUI;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
        } else {
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyItemGUI;
            EditorApplication.projectWindowItemOnGUI -= OnProjectItemGUI;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
    }

    // ─── Resolved-object caches (rebuilt lazily on dirty) ────────

    private static void EnsureMarkedHierarchyTransforms() {
        if (!s_markedHierarchyDirty) return;
        if (s_markedHierarchyTransforms.Length != markedHierarchyItems.Count)
            s_markedHierarchyTransforms = new Transform[markedHierarchyItems.Count];
        for (int i = 0; i < markedHierarchyItems.Count; i++) {
            var go = EditorUtility.EntityIdToObject(markedHierarchyItems[i]) as GameObject;
            s_markedHierarchyTransforms[i] = go != null ? go.transform : null;
        }
        s_markedHierarchyDirty = false;
    }

    private static void EnsureRecentHierarchyTransforms() {
        if (!s_recentHierarchyDirty) return;
        if (s_recentHierarchyTransforms.Length != recentHierarchySelections.Count)
            s_recentHierarchyTransforms = new Transform[recentHierarchySelections.Count];
        for (int i = 0; i < recentHierarchySelections.Count; i++) {
            var go = EditorUtility.EntityIdToObject(recentHierarchySelections[i]) as GameObject;
            s_recentHierarchyTransforms[i] = go != null ? go.transform : null;
        }
        s_recentHierarchyDirty = false;
    }

    private static void EnsureMarkedProjectPaths() {
        if (!s_markedProjectDirty) return;
        if (s_markedProjectPaths.Length != markedProjectItems.Count)
            s_markedProjectPaths = new string[markedProjectItems.Count];
        for (int i = 0; i < markedProjectItems.Count; i++)
            s_markedProjectPaths[i] = AssetDatabase.GUIDToAssetPath(markedProjectItems[i]);
        s_markedProjectDirty = false;
    }

    private static void EnsureRecentProjectPaths() {
        if (!s_recentProjectDirty) return;
        if (s_recentProjectPaths.Length != recentProjectSelections.Count)
            s_recentProjectPaths = new string[recentProjectSelections.Count];
        for (int i = 0; i < recentProjectSelections.Count; i++)
            s_recentProjectPaths[i] = AssetDatabase.GUIDToAssetPath(recentProjectSelections[i]);
        s_recentProjectDirty = false;
    }

    private static void LoadMarkedItems() {
        // Load marked hierarchy items
        string hierarchyData = EditorPrefs.GetString("HierarchyHeatmap_MarkedHierarchy", "");
        if (!string.IsNullOrEmpty(hierarchyData)) {
            string[] items = hierarchyData.Split(',');
            foreach (string item in items) {
                if (int.TryParse(item, out int id)) {
                    markedHierarchyItems.Add(id);
                }
            }
        }

        // Load marked project items
        string projectData = EditorPrefs.GetString("HierarchyHeatmap_MarkedProject", "");
        if (!string.IsNullOrEmpty(projectData)) {
            string[] items = projectData.Split(',');
            foreach (string item in items) {
                if (!string.IsNullOrEmpty(item)) {
                    markedProjectItems.Add(item);
                }
            }
        }

        s_markedHierarchyDirty = true;
        s_markedProjectDirty = true;
    }

    private static void SaveMarkedItems() {
        // Save marked hierarchy items
        List<string> hierarchyStrings = new List<string>();
        foreach (int id in markedHierarchyItems) {
            hierarchyStrings.Add(id.ToString());
        }
        EditorPrefs.SetString("HierarchyHeatmap_MarkedHierarchy", string.Join(",", hierarchyStrings));

        // Save marked project items
        List<string> projectStrings = new List<string>(markedProjectItems);
        EditorPrefs.SetString("HierarchyHeatmap_MarkedProject", string.Join(",", projectStrings));
    }

    [Shortcut("Editools/Toggle Heatmap Mark", KeyCode.D, ShortcutModifiers.Alt)]
    private static void ToggleMark() {
        if (!s_subscribed) return;
        // Handle active folder in project view (even if not explicitly selected)
        string activeFolderPath = GetActiveFolderPath();
        if (!string.IsNullOrEmpty(activeFolderPath)) {
            string guid = AssetDatabase.AssetPathToGUID(activeFolderPath);
            if (!string.IsNullOrEmpty(guid)) {
                if (markedProjectItems.Contains(guid)) {
                    markedProjectItems.Remove(guid);
                } else {
                    AddMarkedProjectItem(guid);
                }
            }
        }

        foreach (var obj in Selection.objects) {
            string path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path)) {
                // Project item
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (markedProjectItems.Contains(guid)) {
                    markedProjectItems.Remove(guid);
                } else {
                    AddMarkedProjectItem(guid);
                }
            } else if (obj is GameObject go) {
                // Hierarchy item
                int id = go.GetInstanceID();
                if (markedHierarchyItems.Contains(id)) {
                    markedHierarchyItems.Remove(id);
                } else {
                    AddMarkedHierarchyItem(id);
                }
            }
        }

        SaveMarkedItems();
        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
    }

    private static void AddMarkedHierarchyItem(int id) {
        markedHierarchyItems.Remove(id); // Remove if exists to re-add at end
        markedHierarchyItems.Add(id);
        if (markedHierarchyItems.Count > maxMarked) {
            markedHierarchyItems.RemoveAt(0); // Remove oldest
        }
        s_markedHierarchyDirty = true;
    }

    private static void AddMarkedProjectItem(string guid) {
        markedProjectItems.Remove(guid); // Remove if exists to re-add at end
        markedProjectItems.Add(guid);
        if (markedProjectItems.Count > maxMarked) {
            markedProjectItems.RemoveAt(0); // Remove oldest
        }
        s_markedProjectDirty = true;
    }

    [Shortcut("Editools/Clear All Heatmap Marks", KeyCode.D, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
    private static void ClearAllMarks() {
        if (!s_subscribed) return;
        markedHierarchyItems.Clear();
        markedProjectItems.Clear();
        s_markedHierarchyDirty = true;
        s_markedProjectDirty = true;
        SaveMarkedItems();
        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
    }

    private static void SetupHierarchyReflection() {
        if (hierarchyWindowType == null) return;

        var windows = Resources.FindObjectsOfTypeAll(hierarchyWindowType);
        if (windows.Length == 0) return;

        hierarchyWindow = windows[0] as EditorWindow; // Assumes first window; could extend to handle multiple
        if (hierarchyWindow == null) return;

        var sceneHierarchyField = hierarchyWindowType.GetField("m_SceneHierarchy", BindingFlags.Instance | BindingFlags.NonPublic);
        if (sceneHierarchyField == null) return;

        var sceneHierarchy = sceneHierarchyField.GetValue(hierarchyWindow);
        if (sceneHierarchy == null) return;

        var treeViewProperty = sceneHierarchy.GetType().GetProperty("treeView", BindingFlags.NonPublic | BindingFlags.Instance);
        if (treeViewProperty == null) return;

        treeView = treeViewProperty.GetValue(sceneHierarchy);
        if (treeView == null) return;

        // Try to get the 'data' property for the actual tree view controller
        object data = null;
        var dataProperty = treeView.GetType().GetProperty("data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (dataProperty != null) {
            data = dataProperty.GetValue(treeView);
        }

        hierarchyTreeViewController = data ?? treeView;

        isExpandedMethod = hierarchyTreeViewController.GetType().GetMethod("IsExpanded", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
    }

    private static void SetupProjectTreeReflection() {
        if (projectBrowserType == null) return;

        var windows = Resources.FindObjectsOfTypeAll(projectBrowserType);
        if (windows.Length == 0) return;

        projectWindow = windows[0] as EditorWindow; // Assumes first window; could extend to handle multiple
        if (projectWindow == null) return;

        var folderTreeField = projectBrowserType.GetField("m_FolderTree", BindingFlags.Instance | BindingFlags.NonPublic);
        if (folderTreeField == null) return;

        var folderTree = folderTreeField.GetValue(projectWindow);
        if (folderTree == null) return;

        // Get the 'data' property directly from folderTree
        object data = null;
        var dataProperty = folderTree.GetType().GetProperty("data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (dataProperty != null) {
            data = dataProperty.GetValue(folderTree);
        }

        if (data == null) return;

        projectTreeViewController = data;

        projectIsExpandedMethod = projectTreeViewController.GetType().GetMethod("IsExpanded", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
    }

    private static string GetActiveFolderPath() {
        if (getActiveFolderPathMethod != null) {
            return (string)getActiveFolderPathMethod.Invoke(null, null);
        }
        return string.Empty;
    }

    private static void OnUpdate() {
        string currentFolderPath = GetActiveFolderPath();
        if (!string.IsNullOrEmpty(currentFolderPath) && currentFolderPath != lastFolderPath) {
            lastFolderPath = currentFolderPath;
            string guid = AssetDatabase.AssetPathToGUID(currentFolderPath);
            if (!string.IsNullOrEmpty(guid)) {
                AddOrUpdateRecentProject(guid);
                EditorApplication.RepaintProjectWindow();
            }
        }
    }

    private static void OnSelectionChanged() {
        foreach (var obj in Selection.objects) {
            string path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path)) {
                string guid = AssetDatabase.AssetPathToGUID(path);
                AddOrUpdateRecentProject(guid);
            } else if (obj is GameObject go) {
                int id = go.GetInstanceID();
                AddOrUpdateRecentHierarchy(id);
            }
        }

        // Repaint both windows to update visuals
        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
    }

    private static void AddOrUpdateRecentHierarchy(int id) {
        recentHierarchySelections.Remove(id);
        recentHierarchySelections.Add(id);
        if (recentHierarchySelections.Count > maxRecent) {
            recentHierarchySelections.RemoveAt(0);
        }
        s_recentHierarchyDirty = true;
    }

    private static void AddOrUpdateRecentProject(string guid) {
        recentProjectSelections.Remove(guid);
        recentProjectSelections.Add(guid);
        if (recentProjectSelections.Count > maxRecent) {
            recentProjectSelections.RemoveAt(0);
        }
        s_recentProjectDirty = true;
    }

    private static void OnHierarchyItemGUI(int instanceID, Rect selectionRect) {
        GameObject go = EditorUtility.EntityIdToObject(instanceID) as GameObject;
        if (go == null) return;

        if (!hierarchyReflectionSetup) {
            try {
                SetupHierarchyReflection();
            } catch (Exception e) {
                if (!s_warnedHierarchyReflectionSetup) {
                    Debug.LogWarning("HierarchyHeatmap: Failed to setup hierarchy reflection: " + e.Message);
                    s_warnedHierarchyReflectionSetup = true;
                }
            }
            hierarchyReflectionSetup = true;
        }

        bool hasChildren = go.transform.childCount > 0;
        bool isMarked = markedHierarchyItems.Contains(instanceID);

        bool isExpanded = true; // Fallback to true to avoid propagation if reflection fails
        if (hasChildren && isExpandedMethod != null && hierarchyTreeViewController != null) {
            try {
                isExpanded = (bool)isExpandedMethod.Invoke(hierarchyTreeViewController, new object[] { instanceID });
            } catch (Exception e) {
                if (!s_warnedHierarchyIsExpanded) {
                    Debug.LogWarning("HierarchyHeatmap: Failed to check IsExpanded for hierarchy item: " + e.Message);
                    s_warnedHierarchyIsExpanded = true;
                }
                isExpanded = true;
            }
        }

        // Check for marked children in collapsed parents. Uses the cached
        // Transform array so we don't EntityIdToObject per row per repaint.
        if (hasChildren && !isExpanded && !isMarked) {
            EnsureMarkedHierarchyTransforms();
            var parentT = go.transform;
            for (int i = 0; i < markedHierarchyItems.Count; i++) {
                if (markedHierarchyItems[i] == instanceID) continue; // skip self
                var markedT = s_markedHierarchyTransforms[i];
                if (markedT != null && markedT.IsChildOf(parentT)) {
                    isMarked = true;
                    break;
                }
            }
        }

        // Check if marked (takes priority over heatmap)
        if (isMarked) {
            Color finalMarkColor = markColor;
            finalMarkColor.a = markAlpha;
            EditorGUI.DrawRect(selectionRect, finalMarkColor);
            return;
        }

        int tier = GetHierarchyRecencyTier(instanceID);

        if (hasChildren && !isExpanded) {
            EnsureRecentHierarchyTransforms();
            var parentT = go.transform;
            int subtreeMax = 0;
            for (int i = recentHierarchySelections.Count - 1; i >= 0; i--) // Iterate from most recent
            {
                int recentID = recentHierarchySelections[i];
                if (recentID == instanceID) continue;
                var recentT = s_recentHierarchyTransforms[i];
                if (recentT != null && recentT.IsChildOf(parentT)) {
                    int childTier = GetHierarchyRecencyTier(recentID);
                    if (childTier > subtreeMax) {
                        subtreeMax = childTier;
                    }
                }
            }
            if (subtreeMax > 0) {
                tier = Mathf.Max(tier, subtreeMax); // Use max from subtree, in case parent also has tier
            }
        }

        if (tier > 0) {
            float intensity = (float)tier / 5f * gradientStrength;
            Color heatColor = Color.Lerp(lowHeatColor, highHeatColor, intensity);
            heatColor.a = heatAlpha;

            EditorGUI.DrawRect(selectionRect, heatColor);
        }
    }

    private static int GetHierarchyRecencyTier(int id) {
        int index = recentHierarchySelections.IndexOf(id);
        if (index < 0) return 0;
        float normalized = (float)(index + 1) / recentHierarchySelections.Count;
        return Mathf.CeilToInt(normalized * 5); // 1 to 5
    }

    private static void OnProjectItemGUI(string guid, Rect selectionRect) {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path)) return;

        if (!projectReflectionSetup) {
            try {
                SetupProjectTreeReflection();
            } catch (Exception e) {
                if (!s_warnedProjectReflectionSetup) {
                    Debug.LogWarning("HierarchyHeatmap: Failed to setup project reflection: " + e.Message);
                    s_warnedProjectReflectionSetup = true;
                }
            }
            projectReflectionSetup = true;
        }

        bool isFolder = AssetDatabase.IsValidFolder(path);
        bool isMarked = markedProjectItems.Contains(guid);

        // Compute isExpanded once for folders; original code resolved the folder
        // and ran the IsExpanded reflection twice per row. Now it runs once.
        bool isExpanded = true;
        if (isFolder) {
            UnityEngine.Object folderObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (folderObj == null) return;
            int folderInstanceID = folderObj.GetInstanceID();

            if (projectIsExpandedMethod != null && projectTreeViewController != null) {
                try {
                    isExpanded = (bool)projectIsExpandedMethod.Invoke(projectTreeViewController, new object[] { folderInstanceID });
                } catch (Exception e) {
                    if (!s_warnedProjectIsExpanded) {
                        Debug.LogWarning("HierarchyHeatmap: Failed to check IsExpanded for project item: " + e.Message);
                        s_warnedProjectIsExpanded = true;
                    }
                    isExpanded = true;
                }
            }

            // Check for marked children in collapsed folders (uses cached paths).
            if (!isExpanded && !isMarked) {
                EnsureMarkedProjectPaths();
                string prefix = path + "/";
                for (int i = 0; i < markedProjectItems.Count; i++) {
                    if (markedProjectItems[i] == guid) continue; // Skip self
                    string markedPath = s_markedProjectPaths[i];
                    if (!string.IsNullOrEmpty(markedPath) && markedPath.StartsWith(prefix)) {
                        isMarked = true;
                        break;
                    }
                }
            }
        }

        // Check if marked (takes priority over heatmap)
        if (isMarked) {
            Color finalMarkColor = markColor;
            finalMarkColor.a = markAlpha;
            EditorGUI.DrawRect(selectionRect, finalMarkColor);
            return;
        }

        int tier = GetProjectRecencyTier(guid);

        if (isFolder && !isExpanded) {
            EnsureRecentProjectPaths();
            string prefix = path + "/";
            int subtreeMax = 0;
            for (int i = recentProjectSelections.Count - 1; i >= 0; i--) // Iterate from most recent
            {
                string recentGuid = recentProjectSelections[i];
                if (recentGuid == guid) continue; // Skip self
                string recentPath = s_recentProjectPaths[i];
                if (!string.IsNullOrEmpty(recentPath) && recentPath.StartsWith(prefix)) {
                    int childTier = GetProjectRecencyTier(recentGuid);
                    if (childTier > subtreeMax) {
                        subtreeMax = childTier;
                    }
                }
            }
            if (subtreeMax > 0) {
                tier = Mathf.Max(tier, subtreeMax); // Use max from subtree, in case folder also has tier
            }
        }

        if (tier > 0) {
            float intensity = (float)tier / 5f * gradientStrength;
            Color heatColor = Color.Lerp(lowHeatColor, highHeatColor, intensity);
            heatColor.a = heatAlpha;

            EditorGUI.DrawRect(selectionRect, heatColor);
        }
    }

    private static int GetProjectRecencyTier(string guid) {
        int index = recentProjectSelections.IndexOf(guid);
        if (index < 0) return 0;
        float normalized = (float)(index + 1) / recentProjectSelections.Count;
        return Mathf.CeilToInt(normalized * 5); // 1 to 5
    }

    public static void ResetRecent() {
        recentHierarchySelections.Clear();
        recentProjectSelections.Clear();
        s_recentHierarchyDirty = true;
        s_recentProjectDirty = true;
        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state) {
        if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.ExitingPlayMode) {
            ResetRecent();
        }
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        // Hierarchy InstanceIDs from before scene load are stale — drop caches
        // so the next per-row pass re-resolves against the new scene.
        s_markedHierarchyDirty = true;
        s_recentHierarchyDirty = true;
        ResetRecent();
    }
}

