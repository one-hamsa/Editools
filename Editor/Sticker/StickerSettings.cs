#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

static class StickerSettings
{
    const string k_MatGuidPref         = "Editools_Sticker_DefaultMatGUID";
    const string k_StaticPref          = "Editools_Sticker_DefaultStatic";
    const string k_LayerPref           = "Editools_Sticker_DefaultLayer";
    const string k_ColumnsPref         = "Editools_Sticker_DefaultColumns";
    const string k_RowsPref            = "Editools_Sticker_DefaultRows";
    const string k_RayDistAbovePref    = "Editools_Sticker_DefaultRayDistAbove";
    const string k_RayDistBelowPref    = "Editools_Sticker_DefaultRayDistBelow";
    const string k_RetryBendAnglePref  = "Editools_Sticker_DefaultRetryBend";
    const string k_RelaxIterationsPref = "Editools_Sticker_DefaultRelaxIter";
    const string k_RelaxStrengthPref   = "Editools_Sticker_DefaultRelaxStrength";
    const string k_RelaxRigidityPref   = "Editools_Sticker_DefaultRelaxRigidity";
    const string k_SurfaceOffsetPref   = "Editools_Sticker_DefaultSurfaceOffset";
    const string k_ShadowCastPref      = "Editools_Sticker_DefaultShadowCast";

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

    internal static Material GetOrCreateDefaultMaterial()
    {
        var mat = DefaultMaterial;
        if (mat != null) return mat;
        return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
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

    internal static int DefaultColumns
    {
        get => Mathf.Max(2, EditorPrefs.GetInt(k_ColumnsPref, 16));
        set => EditorPrefs.SetInt(k_ColumnsPref, Mathf.Max(2, value));
    }

    internal static int DefaultRows
    {
        get => Mathf.Max(2, EditorPrefs.GetInt(k_RowsPref, 16));
        set => EditorPrefs.SetInt(k_RowsPref, Mathf.Max(2, value));
    }

    internal static float DefaultRayDistAbove
    {
        get => Mathf.Max(0f, EditorPrefs.GetFloat(k_RayDistAbovePref, 0.25f));
        set => EditorPrefs.SetFloat(k_RayDistAbovePref, Mathf.Max(0f, value));
    }

    internal static float DefaultRayDistBelow
    {
        get => Mathf.Max(0f, EditorPrefs.GetFloat(k_RayDistBelowPref, 0.25f));
        set => EditorPrefs.SetFloat(k_RayDistBelowPref, Mathf.Max(0f, value));
    }

    internal static float DefaultRetryBendAngle
    {
        get => Mathf.Clamp(EditorPrefs.GetFloat(k_RetryBendAnglePref, 30f), 0f, 89f);
        set => EditorPrefs.SetFloat(k_RetryBendAnglePref, Mathf.Clamp(value, 0f, 89f));
    }

    internal static int DefaultRelaxIterations
    {
        get => Mathf.Clamp(EditorPrefs.GetInt(k_RelaxIterationsPref, 5), 0, 20);
        set => EditorPrefs.SetInt(k_RelaxIterationsPref, Mathf.Clamp(value, 0, 20));
    }

    internal static float DefaultRelaxStrength
    {
        get => Mathf.Clamp01(EditorPrefs.GetFloat(k_RelaxStrengthPref, 0.5f));
        set => EditorPrefs.SetFloat(k_RelaxStrengthPref, Mathf.Clamp01(value));
    }

    internal static float DefaultRelaxRigidity
    {
        get => Mathf.Clamp01(EditorPrefs.GetFloat(k_RelaxRigidityPref, 0.5f));
        set => EditorPrefs.SetFloat(k_RelaxRigidityPref, Mathf.Clamp01(value));
    }

    internal static float DefaultSurfaceOffset
    {
        get => EditorPrefs.GetFloat(k_SurfaceOffsetPref, 0.005f);
        set => EditorPrefs.SetFloat(k_SurfaceOffsetPref, value);
    }

    /// <summary>Stickers default to NOT casting shadows — they're surface decals.</summary>
    internal static bool DefaultShadowCasting
    {
        get => EditorPrefs.GetBool(k_ShadowCastPref, false);
        set => EditorPrefs.SetBool(k_ShadowCastPref, value);
    }

    internal static ShadowCastingMode DefaultShadowCastingMode =>
        DefaultShadowCasting ? ShadowCastingMode.On : ShadowCastingMode.Off;
}

static class StickerSettingsGUI
{
    static readonly GUIContent k_MatLabel = new GUIContent(
        "Default Material",
        "Material applied to new Stickers on creation. Leave empty to use the built-in fallback. " +
        "Independent of other Grey Primitives.");

    static readonly GUIContent k_StaticLabel = new GUIContent(
        "Static",
        "Mark new Stickers as static. Enables lightmap baking and occlusion culling contributions.");

    static readonly GUIContent k_LayerLabel = new GUIContent(
        "Default Layer",
        "Layer assigned to new Stickers on creation.");

    static readonly GUIContent k_ColumnsLabel = new GUIContent(
        "Default Columns",
        "Grid subdivisions along width for new Stickers. Higher = finer surface conformation, more verts.");

    static readonly GUIContent k_RowsLabel = new GUIContent(
        "Default Rows",
        "Grid subdivisions along height for new Stickers. Higher = finer surface conformation, more verts.");

    static readonly GUIContent k_RayAboveLabel = new GUIContent(
        "Ray Distance Above",
        "How far new Stickers' conforming rays travel above the tangent plane before giving up (meters).");

    static readonly GUIContent k_RayBelowLabel = new GUIContent(
        "Ray Distance Below",
        "How far new Stickers' conforming rays travel below the tangent plane before giving up (meters).");

    static readonly GUIContent k_RetryBendLabel = new GUIContent(
        "Retry Bend Angle",
        "Degrees a missed ray bends toward the Sticker's center on its retry. Helps grazing edge cases.");

    static readonly GUIContent k_RelaxIterLabel = new GUIContent(
        "Relax Iterations",
        "Smoothing/rigidity passes after BFS sampling for new Stickers. Higher = smoother grid, more cost.");

    static readonly GUIContent k_RelaxStrengthLabel = new GUIContent(
        "Relax Strength",
        "Per-pass smoothing weight for new Stickers. 0 = no smoothing, 1 = snap to neighbor average.");

    static readonly GUIContent k_RelaxRigidityLabel = new GUIContent(
        "Relax Rigidity",
        "Per-pass edge-length restoration for new Stickers. Higher = preserves quad shape, resists stretching.");

    static readonly GUIContent k_SurfaceOffsetLabel = new GUIContent(
        "Surface Offset",
        "Distance each new Sticker's verts are lifted along their normal after conformation (meters). " +
        "Prevents intersection / z-fighting with the surface. Typical 0.001–0.01.");

    static readonly GUIContent k_ShadowLabel = new GUIContent(
        "Cast Shadows",
        "Whether new Stickers cast shadows. Off by default — Stickers are surface decals.");

    internal static void DrawGUI()
    {
        var curMat = StickerSettings.DefaultMaterial;
        var nxtMat = (Material)EditorGUILayout.ObjectField(k_MatLabel, curMat, typeof(Material), false);
        if (nxtMat != curMat) StickerSettings.DefaultMaterial = nxtMat;

        bool curStatic = StickerSettings.DefaultStatic;
        bool nxtStatic = EditorGUILayout.Toggle(k_StaticLabel, curStatic);
        if (nxtStatic != curStatic) StickerSettings.DefaultStatic = nxtStatic;

        int curLayer = StickerSettings.DefaultLayer;
        int nxtLayer = EditorGUILayout.LayerField(k_LayerLabel, curLayer);
        if (nxtLayer != curLayer) StickerSettings.DefaultLayer = nxtLayer;

        EditorGUILayout.Space(8);

        int curCols = StickerSettings.DefaultColumns;
        int nxtCols = EditorGUILayout.IntField(k_ColumnsLabel, curCols);
        if (nxtCols != curCols) StickerSettings.DefaultColumns = nxtCols;

        int curRows = StickerSettings.DefaultRows;
        int nxtRows = EditorGUILayout.IntField(k_RowsLabel, curRows);
        if (nxtRows != curRows) StickerSettings.DefaultRows = nxtRows;

        EditorGUILayout.Space(4);

        float curAbove = StickerSettings.DefaultRayDistAbove;
        float nxtAbove = EditorGUILayout.FloatField(k_RayAboveLabel, curAbove);
        if (!Mathf.Approximately(nxtAbove, curAbove)) StickerSettings.DefaultRayDistAbove = nxtAbove;

        float curBelow = StickerSettings.DefaultRayDistBelow;
        float nxtBelow = EditorGUILayout.FloatField(k_RayBelowLabel, curBelow);
        if (!Mathf.Approximately(nxtBelow, curBelow)) StickerSettings.DefaultRayDistBelow = nxtBelow;

        float curBend = StickerSettings.DefaultRetryBendAngle;
        float nxtBend = EditorGUILayout.Slider(k_RetryBendLabel, curBend, 0f, 89f);
        if (!Mathf.Approximately(nxtBend, curBend)) StickerSettings.DefaultRetryBendAngle = nxtBend;

        EditorGUILayout.Space(4);

        int curIter = StickerSettings.DefaultRelaxIterations;
        int nxtIter = EditorGUILayout.IntSlider(k_RelaxIterLabel, curIter, 0, 20);
        if (nxtIter != curIter) StickerSettings.DefaultRelaxIterations = nxtIter;

        float curStr = StickerSettings.DefaultRelaxStrength;
        float nxtStr = EditorGUILayout.Slider(k_RelaxStrengthLabel, curStr, 0f, 1f);
        if (!Mathf.Approximately(nxtStr, curStr)) StickerSettings.DefaultRelaxStrength = nxtStr;

        float curRig = StickerSettings.DefaultRelaxRigidity;
        float nxtRig = EditorGUILayout.Slider(k_RelaxRigidityLabel, curRig, 0f, 1f);
        if (!Mathf.Approximately(nxtRig, curRig)) StickerSettings.DefaultRelaxRigidity = nxtRig;

        EditorGUILayout.Space(4);

        float curOffset = StickerSettings.DefaultSurfaceOffset;
        float nxtOffset = EditorGUILayout.FloatField(k_SurfaceOffsetLabel, curOffset);
        if (!Mathf.Approximately(nxtOffset, curOffset)) StickerSettings.DefaultSurfaceOffset = nxtOffset;

        EditorGUILayout.Space(4);

        bool curShadow = StickerSettings.DefaultShadowCasting;
        bool nxtShadow = EditorGUILayout.Toggle(k_ShadowLabel, curShadow);
        if (nxtShadow != curShadow) StickerSettings.DefaultShadowCasting = nxtShadow;
    }
}

/// <summary>
/// Standalone Sticker settings popup, opened from the Editools settings overlay.
/// Independent from the shared Grey Primitives popup — Sticker owns its own
/// material/layer/static/shadow defaults.
/// </summary>
class StickerSettingsPopup : PopupWindowContent
{
    public override Vector2 GetWindowSize() => new Vector2(300, 470);

    public override void OnGUI(Rect rect)
    {
        EditorGUILayout.LabelField("Sticker Defaults", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);
        StickerSettingsGUI.DrawGUI();
    }
}
#endif
