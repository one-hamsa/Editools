#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

static class GreyPrimitiveSettings
{
    // Reuse legacy Greybox pref keys so existing per-project settings carry over
    const string k_MatGuidPref      = "Editools_Greybox_DefaultMatGUID";
    const string k_StaticPref       = "Editools_Greybox_DefaultStatic";
    const string k_LayerPref        = "Editools_Greybox_DefaultLayer";
    const string k_MeshColliderPref = "Editools_Greybox_DefaultMeshCollider";
    const string k_ShadowCastPref   = "Editools_Greybox_DefaultShadowCast";
    const string k_AutoUpdatePivotPref = "Editools_GreyPrimitive_AutoUpdatePivot";

    internal static bool AutoUpdatePivot
    {
        get => EditorPrefs.GetBool(k_AutoUpdatePivotPref, true);
        set => EditorPrefs.SetBool(k_AutoUpdatePivotPref, value);
    }

    internal static Material DefaultMaterial
    {
        get
        {
            string guid = EditorPrefs.GetString(k_MatGuidPref, "");
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }
        set
        {
            if (value != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long _))
                EditorPrefs.SetString(k_MatGuidPref, guid);
            else
                EditorPrefs.DeleteKey(k_MatGuidPref);
        }
    }

    internal static bool DefaultStatic
    {
        get => EditorPrefs.GetBool(k_StaticPref, true);
        set => EditorPrefs.SetBool(k_StaticPref, value);
    }

    internal static int DefaultLayer
    {
        get => EditorPrefs.GetInt(k_LayerPref, 0);
        set => EditorPrefs.SetInt(k_LayerPref, value);
    }

    internal static bool DefaultMeshCollider
    {
        get => EditorPrefs.GetBool(k_MeshColliderPref, true);
        set => EditorPrefs.SetBool(k_MeshColliderPref, value);
    }

    internal static bool DefaultShadowCasting
    {
        get => EditorPrefs.GetBool(k_ShadowCastPref, true);
        set => EditorPrefs.SetBool(k_ShadowCastPref, value);
    }

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

    // ─── Shared settings GUI (used by both GreyboxSettingsPopup and GreypipeSettingsPopup) ──

    static readonly GUIContent k_MatLabel = new GUIContent(
        "Default Material",
        "Material applied to new grey primitives on creation. " +
        "Leave empty to use the built-in grey fallback.");

    static readonly GUIContent k_StaticLabel = new GUIContent(
        "Static",
        "Mark new primitives as static. Enables lightmap baking and occlusion culling contributions.");

    static readonly GUIContent k_LayerLabel = new GUIContent(
        "Default Layer",
        "Layer assigned to new primitives on creation.");

    static readonly GUIContent k_MeshColliderLabel = new GUIContent(
        "Mesh Collider",
        "Add a MeshCollider component to new primitives. Disable for purely visual blockout pieces.");

    static readonly GUIContent k_ShadowCastLabel = new GUIContent(
        "Cast Shadows",
        "Whether new primitives cast shadows. Disable to save draw calls on background blockout geometry.");

    internal static void DrawSharedSettingsGUI()
    {
        var current = DefaultMaterial;
        var next = (Material)EditorGUILayout.ObjectField(k_MatLabel, current, typeof(Material), false);
        if (next != current)
            DefaultMaterial = next;

        EditorGUILayout.Space(4);

        bool curStatic = DefaultStatic;
        bool nextStatic = EditorGUILayout.Toggle(k_StaticLabel, curStatic);
        if (nextStatic != curStatic)
            DefaultStatic = nextStatic;

        EditorGUILayout.Space(4);

        int curLayer  = DefaultLayer;
        int nextLayer = EditorGUILayout.LayerField(k_LayerLabel, curLayer);
        if (nextLayer != curLayer)
            DefaultLayer = nextLayer;

        EditorGUILayout.Space(4);

        bool curCollider  = DefaultMeshCollider;
        bool nextCollider = EditorGUILayout.Toggle(k_MeshColliderLabel, curCollider);
        if (nextCollider != curCollider)
            DefaultMeshCollider = nextCollider;

        EditorGUILayout.Space(4);

        bool curShadow  = DefaultShadowCasting;
        bool nextShadow = EditorGUILayout.Toggle(k_ShadowCastLabel, curShadow);
        if (nextShadow != curShadow)
            DefaultShadowCasting = nextShadow;
    }
}

/// <summary>
/// Unified settings popup for all grey primitives (Greybox, Greypipe).
/// Shows shared settings first, then per-primitive sections.
/// </summary>
class GreyPrimitivesSettingsPopup : PopupWindowContent
{
    static GUIStyle s_sectionHeader;

    static readonly GUIContent k_TooltipsLabel = new GUIContent(
        "Scene View Tooltips",
        "Show the simplified action hints in the Scene View while Grey Primitive Edit Mode is on.");

    static readonly GUIContent k_AutoUpdatePivotLabel = new GUIContent(
        "Auto Update Pivot",
        "Recenter Greybox pivots after shape edits without moving the geometry or child objects.");

    public override Vector2 GetWindowSize() => new Vector2(280, 567);

    public override void OnGUI(Rect rect)
    {
        if (s_sectionHeader == null)
            s_sectionHeader = new GUIStyle(EditorStyles.boldLabel) { margin = new RectOffset(0, 0, 6, 2) };

        GreyPrimitiveSettings.DrawSharedSettingsGUI();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Greybox", s_sectionHeader);
        GreyboxSettingsGUI.DrawGUI();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Greypipe", s_sectionHeader);
        GreypipeSettingsGUI.DrawGUI();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Greyroad", s_sectionHeader);
        GreyroadSettingsGUI.DrawGUI();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Greyquad", s_sectionHeader);
        GreyquadSettingsGUI.DrawGUI();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Edit Mode", s_sectionHeader);
        bool tt = EditorGUILayout.Toggle(k_TooltipsLabel, GPEdit.ShowTooltips);
        if (tt != GPEdit.ShowTooltips) GPEdit.ShowTooltips = tt;
        bool autoPivot = EditorGUILayout.Toggle(k_AutoUpdatePivotLabel, GreyPrimitiveSettings.AutoUpdatePivot);
        if (autoPivot != GreyPrimitiveSettings.AutoUpdatePivot)
            GreyPrimitiveSettings.AutoUpdatePivot = autoPivot;
    }
}
#endif
