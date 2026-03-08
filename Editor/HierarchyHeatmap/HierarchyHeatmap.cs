using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class HierarchyHeatmap
{
    public const int DefaultMaxRecent = 5;
    public const int DefaultMaxMarked = 10;
    public static int maxRecent = DefaultMaxRecent; // Now configurable
    public static int maxMarked = DefaultMaxMarked; // Configurable mark limit
    private static List<int> recentHierarchySelections = new List<int>(); // Index 0: oldest, last: newest
    private static List<string> recentProjectSelections = new List<string>(); // Index 0: oldest, last: newest
    private static bool enabled; // Opt-in: loaded from EditorPrefs

    public static Color lowHeatColor;
    public static Color highHeatColor;
    public static float heatAlpha;
    public static float gradientStrength;

    // Mark functionality
    private static List<int> markedHierarchyItems = new List<int>(); // Changed to List to maintain order
    private static List<string> markedProjectItems = new List<string>(); // Changed to List to maintain order
    public static Color markColor;
    public static float markAlpha;

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

    static HierarchyHeatmap() {
        // Load enabled state from EditorPrefs
        enabled = EditorPrefs.GetBool("HierarchyHeatmapEnabled", false);

        // Load maxRecent and maxMarked
        maxRecent = EditorPrefs.GetInt("HierarchyHeatmap_MaxRecent", DefaultMaxRecent);
        maxMarked = EditorPrefs.GetInt("HierarchyHeatmap_MaxMarked", DefaultMaxMarked);

        // Load colors from EditorPrefs
        LoadSettings();

        // Load marked items
        LoadMarkedItems();

        // Hook into selection changed event
        Selection.selectionChanged += OnSelectionChanged;

        // Hook into hierarchy item GUI drawing
        EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGUI;

        // Hook into project item GUI drawing
        EditorApplication.projectWindowItemOnGUI += OnProjectItemGUI;

        // Hook into update for polling active folder
        EditorApplication.update += OnUpdate;

        // Setup reflection for active folder path
        var assembly = typeof(UnityEditor.Editor).Assembly;
        var projectWindowUtilType = assembly.GetType("UnityEditor.ProjectWindowUtil");
        if (projectWindowUtilType != null) {
            getActiveFolderPathMethod = projectWindowUtilType.GetMethod("GetActiveFolderPath", BindingFlags.Static | BindingFlags.NonPublic);
        }

        // Pre-load types
        hierarchyWindowType = assembly.GetType("UnityEditor.SceneHierarchyWindow");
        projectBrowserType = assembly.GetType("UnityEditor.ProjectBrowser");

        // Reset on play mode change or scene change
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void LoadSettings() {
        lowHeatColor = new Color(
            EditorPrefs.GetFloat("HierarchyHeatmap_LowR", 0.9f),
            EditorPrefs.GetFloat("HierarchyHeatmap_LowG", 0.9f),
            EditorPrefs.GetFloat("HierarchyHeatmap_LowB", 1f),
            EditorPrefs.GetFloat("HierarchyHeatmap_LowA", 1f)
        );

        highHeatColor = new Color(
            EditorPrefs.GetFloat("HierarchyHeatmap_HighR", 1f),
            EditorPrefs.GetFloat("HierarchyHeatmap_HighG", 0.2f),
            EditorPrefs.GetFloat("HierarchyHeatmap_HighB", 0.2f),
            EditorPrefs.GetFloat("HierarchyHeatmap_HighA", 1f)
        );

        heatAlpha = EditorPrefs.GetFloat("HierarchyHeatmap_Alpha", 0.3f);
        gradientStrength = EditorPrefs.GetFloat("HierarchyHeatmap_GradientStrength", 0.5f);

        // Load mark color settings
        markColor = new Color(
            EditorPrefs.GetFloat("HierarchyHeatmap_MarkR", 1f),
            EditorPrefs.GetFloat("HierarchyHeatmap_MarkG", 0.8f),
            EditorPrefs.GetFloat("HierarchyHeatmap_MarkB", 0f),
            EditorPrefs.GetFloat("HierarchyHeatmap_MarkA", 1f)
        );

        markAlpha = EditorPrefs.GetFloat("HierarchyHeatmap_MarkAlpha", 0.4f);
    }

    public static void SaveSettings() {
        EditorPrefs.SetFloat("HierarchyHeatmap_LowR", lowHeatColor.r);
        EditorPrefs.SetFloat("HierarchyHeatmap_LowG", lowHeatColor.g);
        EditorPrefs.SetFloat("HierarchyHeatmap_LowB", lowHeatColor.b);
        EditorPrefs.SetFloat("HierarchyHeatmap_LowA", lowHeatColor.a);

        EditorPrefs.SetFloat("HierarchyHeatmap_HighR", highHeatColor.r);
        EditorPrefs.SetFloat("HierarchyHeatmap_HighG", highHeatColor.g);
        EditorPrefs.SetFloat("HierarchyHeatmap_HighB", highHeatColor.b);
        EditorPrefs.SetFloat("HierarchyHeatmap_HighA", highHeatColor.a);

        EditorPrefs.SetFloat("HierarchyHeatmap_Alpha", heatAlpha);
        EditorPrefs.SetFloat("HierarchyHeatmap_GradientStrength", gradientStrength);

        EditorPrefs.SetInt("HierarchyHeatmap_MaxRecent", maxRecent);
        EditorPrefs.SetInt("HierarchyHeatmap_MaxMarked", maxMarked);

        // Save mark color settings
        EditorPrefs.SetFloat("HierarchyHeatmap_MarkR", markColor.r);
        EditorPrefs.SetFloat("HierarchyHeatmap_MarkG", markColor.g);
        EditorPrefs.SetFloat("HierarchyHeatmap_MarkB", markColor.b);
        EditorPrefs.SetFloat("HierarchyHeatmap_MarkA", markColor.a);

        EditorPrefs.SetFloat("HierarchyHeatmap_MarkAlpha", markAlpha);

        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
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
    }

    private static void AddMarkedProjectItem(string guid) {
        markedProjectItems.Remove(guid); // Remove if exists to re-add at end
        markedProjectItems.Add(guid);
        if (markedProjectItems.Count > maxMarked) {
            markedProjectItems.RemoveAt(0); // Remove oldest
        }
    }

    [Shortcut("Editools/Clear All Heatmap Marks", KeyCode.D, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
    private static void ClearAllMarks() {
        markedHierarchyItems.Clear();
        markedProjectItems.Clear();
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
        if (!enabled) return;

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
        if (!enabled) return;

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
    }

    private static void AddOrUpdateRecentProject(string guid) {
        recentProjectSelections.Remove(guid);
        recentProjectSelections.Add(guid);
        if (recentProjectSelections.Count > maxRecent) {
            recentProjectSelections.RemoveAt(0);
        }
    }

    private static void OnHierarchyItemGUI(int instanceID, Rect selectionRect) {
        GameObject go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
        if (go == null) return;

        if (!hierarchyReflectionSetup) {
            try {
                SetupHierarchyReflection();
            } catch (Exception e) {
                Debug.LogWarning("HierarchyHeatmap: Failed to setup hierarchy reflection: " + e.Message);
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
                Debug.LogWarning("HierarchyHeatmap: Failed to check IsExpanded for hierarchy item: " + e.Message);
                isExpanded = true;
            }
        }

        // Check for marked children in collapsed parents
        if (hasChildren && !isExpanded && !isMarked) {
            foreach (int markedID in markedHierarchyItems) {
                if (markedID == instanceID) continue; // Skip self
                GameObject markedGO = EditorUtility.InstanceIDToObject(markedID) as GameObject;
                if (markedGO != null && markedGO.transform.IsChildOf(go.transform)) {
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

        if (!enabled) return;

        int tier = GetHierarchyRecencyTier(instanceID);

        if (hasChildren && !isExpanded) {
            int subtreeMax = 0;
            for (int i = recentHierarchySelections.Count - 1; i >= 0; i--) // Iterate from most recent
            {
                int recentID = recentHierarchySelections[i];
                if (recentID == instanceID) continue; // Skip self
                GameObject recentGO = EditorUtility.InstanceIDToObject(recentID) as GameObject;
                if (recentGO != null && recentGO.transform.IsChildOf(go.transform)) {
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
                Debug.LogWarning("HierarchyHeatmap: Failed to setup project reflection: " + e.Message);
            }
            projectReflectionSetup = true;
        }

        bool isFolder = AssetDatabase.IsValidFolder(path);
        bool isMarked = markedProjectItems.Contains(guid);

        if (isFolder) {
            UnityEngine.Object folderObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (folderObj == null) return;
            int folderInstanceID = folderObj.GetInstanceID();

            bool isExpanded = true; // Fallback to true
            if (projectIsExpandedMethod != null && projectTreeViewController != null) {
                try {
                    isExpanded = (bool)projectIsExpandedMethod.Invoke(projectTreeViewController, new object[] { folderInstanceID });
                } catch (Exception e) {
                    Debug.LogWarning("HierarchyHeatmap: Failed to check IsExpanded for project item: " + e.Message);
                    isExpanded = true;
                }
            }

            // Check for marked children in collapsed folders
            if (!isExpanded && !isMarked) {
                foreach (string markedGuid in markedProjectItems) {
                    if (markedGuid == guid) continue; // Skip self
                    string markedPath = AssetDatabase.GUIDToAssetPath(markedGuid);
                    if (markedPath.StartsWith(path + "/")) {
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

        if (!enabled) return;

        int tier = GetProjectRecencyTier(guid);

        if (isFolder) {
            UnityEngine.Object folderObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (folderObj == null) return;
            int folderInstanceID = folderObj.GetInstanceID();

            bool isExpanded = true; // Fallback to true
            if (projectIsExpandedMethod != null && projectTreeViewController != null) {
                try {
                    isExpanded = (bool)projectIsExpandedMethod.Invoke(projectTreeViewController, new object[] { folderInstanceID });
                } catch (Exception e) {
                    Debug.LogWarning("HierarchyHeatmap: Failed to check IsExpanded for project item: " + e.Message);
                    isExpanded = true;
                }
            }

            if (!isExpanded) {
                int subtreeMax = 0;
                for (int i = recentProjectSelections.Count - 1; i >= 0; i--) // Iterate from most recent
                {
                    string recentGuid = recentProjectSelections[i];
                    if (recentGuid == guid) continue; // Skip self
                    string recentPath = AssetDatabase.GUIDToAssetPath(recentGuid);
                    if (recentPath.StartsWith(path + "/")) {
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

    public static void SetEnabled(bool value) {
        enabled = value;
        EditorPrefs.SetBool("HierarchyHeatmapEnabled", enabled);
        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
    }

    public static void ResetRecent() {
        recentHierarchySelections.Clear();
        recentProjectSelections.Clear();
        EditorApplication.RepaintHierarchyWindow();
        EditorApplication.RepaintProjectWindow();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state) {
        if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.ExitingPlayMode) {
            ResetRecent();
        }
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        ResetRecent();
    }
}

public class HeatmapSettingsWindow : EditorWindow
{
    public static void ShowWindow() {
        var window = GetWindow<HeatmapSettingsWindow>();
        window.titleContent = new GUIContent("Heatmap Settings");
        window.Show();
    }

    private void OnGUI() {
        EditorGUI.BeginChangeCheck();

        GUILayout.Label("Heatmap Settings", EditorStyles.boldLabel);
        HierarchyHeatmap.lowHeatColor = EditorGUILayout.ColorField("Low Heat Color", HierarchyHeatmap.lowHeatColor);
        HierarchyHeatmap.highHeatColor = EditorGUILayout.ColorField("High Heat Color", HierarchyHeatmap.highHeatColor);
        HierarchyHeatmap.heatAlpha = EditorGUILayout.Slider("Heat Alpha", HierarchyHeatmap.heatAlpha, 0f, 1f);
        HierarchyHeatmap.gradientStrength = EditorGUILayout.Slider("Gradient Strength", HierarchyHeatmap.gradientStrength, 0f, 1f);
        HierarchyHeatmap.maxRecent = EditorGUILayout.IntSlider("Max Recent Items", HierarchyHeatmap.maxRecent, 1, 20);

        EditorGUILayout.Space();
        GUILayout.Label("Mark Settings", EditorStyles.boldLabel);
        HierarchyHeatmap.markColor = EditorGUILayout.ColorField("Mark Color", HierarchyHeatmap.markColor);
        HierarchyHeatmap.markAlpha = EditorGUILayout.Slider("Mark Alpha", HierarchyHeatmap.markAlpha, 0f, 1f);
        HierarchyHeatmap.maxMarked = EditorGUILayout.IntSlider("Max Marked Items", HierarchyHeatmap.maxMarked, 1, 50);

        if (EditorGUI.EndChangeCheck()) {
            HierarchyHeatmap.SaveSettings();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Reset to Defaults")) {
            HierarchyHeatmap.lowHeatColor = new Color(0.9f, 0.9f, 1f, 1f);
            HierarchyHeatmap.highHeatColor = new Color(1f, 0.2f, 0.2f, 1f);
            HierarchyHeatmap.heatAlpha = 0.3f;
            HierarchyHeatmap.gradientStrength = 0.5f;
            HierarchyHeatmap.maxRecent = HierarchyHeatmap.DefaultMaxRecent;
            HierarchyHeatmap.markColor = new Color(1f, 0.8f, 0f, 1f);
            HierarchyHeatmap.markAlpha = 0.4f;
            HierarchyHeatmap.maxMarked = HierarchyHeatmap.DefaultMaxMarked;
            HierarchyHeatmap.SaveSettings();
        }
    }
}
