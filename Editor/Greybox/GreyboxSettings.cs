#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Per-project EditorPrefs-backed settings for the Greybox tool.
/// Stores the default material assigned to newly created Greyboxes.
/// </summary>
static class GreyboxSettings
{
    const string k_MatGuidPref      = "Editools_Greybox_DefaultMatGUID";
    const string k_ScaleXPref       = "Editools_Greybox_DefaultScaleX";
    const string k_ScaleYPref       = "Editools_Greybox_DefaultScaleY";
    const string k_ScaleZPref       = "Editools_Greybox_DefaultScaleZ";
    const string k_StaticPref       = "Editools_Greybox_DefaultStatic";
    const string k_LayerPref        = "Editools_Greybox_DefaultLayer";
    const string k_MeshColliderPref = "Editools_Greybox_DefaultMeshCollider";
    const string k_ShadowCastPref   = "Editools_Greybox_DefaultShadowCast";

    /// <summary>Per-project default material for new Greyboxes. Null = built-in grey.</summary>
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

    /// <summary>Per-project default scale for new Greyboxes. Default 10×10×10.</summary>
    internal static Vector3 DefaultScale
    {
        get => new Vector3(
            EditorPrefs.GetFloat(k_ScaleXPref, 10f),
            EditorPrefs.GetFloat(k_ScaleYPref, 10f),
            EditorPrefs.GetFloat(k_ScaleZPref, 10f));
        set
        {
            EditorPrefs.SetFloat(k_ScaleXPref, value.x);
            EditorPrefs.SetFloat(k_ScaleYPref, value.y);
            EditorPrefs.SetFloat(k_ScaleZPref, value.z);
        }
    }

    /// <summary>Whether new Greyboxes are marked as static. Default true.</summary>
    internal static bool DefaultStatic
    {
        get => EditorPrefs.GetBool(k_StaticPref, true);
        set => EditorPrefs.SetBool(k_StaticPref, value);
    }

    /// <summary>Layer index applied to new Greyboxes. Default 0 (Default layer).</summary>
    internal static int DefaultLayer
    {
        get => EditorPrefs.GetInt(k_LayerPref, 0);
        set => EditorPrefs.SetInt(k_LayerPref, value);
    }

    /// <summary>Whether new Greyboxes get a MeshCollider component. Default true.</summary>
    internal static bool DefaultMeshCollider
    {
        get => EditorPrefs.GetBool(k_MeshColliderPref, true);
        set => EditorPrefs.SetBool(k_MeshColliderPref, value);
    }

    /// <summary>Whether new Greyboxes cast shadows. Default true.</summary>
    internal static bool DefaultShadowCasting
    {
        get => EditorPrefs.GetBool(k_ShadowCastPref, true);
        set => EditorPrefs.SetBool(k_ShadowCastPref, value);
    }

    /// <summary>
    /// Returns the configured default material, or a built-in grey Standard fallback.
    /// The fallback is not cached — only used at Greybox creation time.
    /// </summary>
    internal static Material GetOrCreateDefaultMaterial()
    {
        var mat = DefaultMaterial;
        if (mat != null) return mat;

        return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
    }

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greybox", false, 10)]
    static void CreateGreybox(MenuCommand cmd)
    {
        var go = new GameObject("Greybox");
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create Greybox");

        go.AddComponent<Greybox>();
        if (DefaultMeshCollider) go.AddComponent<MeshCollider>();

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial  = GetOrCreateDefaultMaterial();
            mr.shadowCastingMode = DefaultShadowCasting ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        go.transform.localScale = DefaultScale;
        go.isStatic   = DefaultStatic;
        go.layer      = DefaultLayer;

        Selection.activeObject = go;
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greybox", true)]
    static bool CreateGreyboxValidate() => true;

    // ─── Shared placement helper (used by menu item and shortcut) ──

    /// <summary>
    /// Creates a Greybox at the given world position/rotation, wires up material,
    /// static flag, and Ground layer. Registers full undo.
    /// </summary>
    internal static GameObject PlaceGreybox(Vector3 worldPos, Quaternion worldRot, Transform parent)
    {
        var go = new GameObject("Greybox");
        if (parent != null)
            go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create Greybox");

        go.AddComponent<Greybox>();
        if (DefaultMeshCollider) go.AddComponent<MeshCollider>();

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial    = GetOrCreateDefaultMaterial();
            mr.shadowCastingMode = DefaultShadowCasting ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        go.transform.position   = worldPos;
        go.transform.rotation   = worldRot;
        go.transform.localScale = DefaultScale;

        go.isStatic = DefaultStatic;
        go.layer    = DefaultLayer;

        Selection.activeObject = go;
        return go;
    }
}

// ─── Settings popup ──────────────────────────────────────────────────────────

/// <summary>
/// Popup for Greybox settings, accessible from the Editools Settings panel.
/// Currently exposes only the default material for new Greyboxes.
/// </summary>
class GreyboxSettingsPopup : PopupWindowContent
{
    static readonly GUIContent k_MatLabel = new GUIContent(
        "Default Material",
        "Material applied to new Greyboxes created via menu or Ctrl+G. " +
        "Leave empty to use the built-in grey fallback.");

    static readonly GUIContent k_ScaleLabel = new GUIContent(
        "Default Scale",
        "Local scale applied to new Greyboxes on creation. " +
        "The mesh corners define a 1×1×1 unit cube, so a scale of 10 creates a 10×10×10 box.");

    static readonly GUIContent k_StaticLabel = new GUIContent(
        "Static",
        "Mark new Greyboxes as static. Enables lightmap baking and occlusion culling contributions.");

    static readonly GUIContent k_LayerLabel = new GUIContent(
        "Default Layer",
        "Layer assigned to new Greyboxes on creation.");

    static readonly GUIContent k_MeshColliderLabel = new GUIContent(
        "Mesh Collider",
        "Add a MeshCollider component to new Greyboxes. Disable for purely visual blockout pieces.");

    static readonly GUIContent k_ShadowCastLabel = new GUIContent(
        "Cast Shadows",
        "Whether new Greyboxes cast shadows. Disable to save draw calls on background blockout geometry.");

    public override Vector2 GetWindowSize() => new Vector2(260, 172);

    public override void OnGUI(Rect rect)
    {
        var current = GreyboxSettings.DefaultMaterial;
        var next = (Material)EditorGUILayout.ObjectField(k_MatLabel, current, typeof(Material), false);
        if (next != current)
            GreyboxSettings.DefaultMaterial = next;

        EditorGUILayout.Space(4);

        var curScale  = GreyboxSettings.DefaultScale;
        var nextScale = EditorGUILayout.Vector3Field(k_ScaleLabel, curScale);
        if (nextScale != curScale)
            GreyboxSettings.DefaultScale = nextScale;

        EditorGUILayout.Space(4);

        bool curStatic = GreyboxSettings.DefaultStatic;
        bool nextStatic = EditorGUILayout.Toggle(k_StaticLabel, curStatic);
        if (nextStatic != curStatic)
            GreyboxSettings.DefaultStatic = nextStatic;

        EditorGUILayout.Space(4);

        int curLayer  = GreyboxSettings.DefaultLayer;
        int nextLayer = EditorGUILayout.LayerField(k_LayerLabel, curLayer);
        if (nextLayer != curLayer)
            GreyboxSettings.DefaultLayer = nextLayer;

        EditorGUILayout.Space(4);

        bool curCollider  = GreyboxSettings.DefaultMeshCollider;
        bool nextCollider = EditorGUILayout.Toggle(k_MeshColliderLabel, curCollider);
        if (nextCollider != curCollider)
            GreyboxSettings.DefaultMeshCollider = nextCollider;

        EditorGUILayout.Space(4);

        bool curShadow  = GreyboxSettings.DefaultShadowCasting;
        bool nextShadow = EditorGUILayout.Toggle(k_ShadowCastLabel, curShadow);
        if (nextShadow != curShadow)
            GreyboxSettings.DefaultShadowCasting = nextShadow;
    }
}

// ─── Ctrl+G shortcut — create Greybox and enter SnapToSurface placement mode ──

/// <summary>
/// Ctrl+G creates a Greybox at the view pivot, then immediately hands it to
/// SnapToSurface.BeginSnap so placement uses the exact same proven raycasting.
/// Left-click confirms; right-click cancels and destroys the object via undo.
/// </summary>
static class GreyboxCreationShortcut
{
    [Shortcut("Editools/Create Greybox", typeof(SceneView), KeyCode.G, ShortcutModifiers.Control)]
    static void Execute()
    {
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = GreyboxSettings.PlaceGreybox(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
