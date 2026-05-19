#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreyroadSettings
{
    const string k_WidthPref  = "Editools_Greyroad_DefaultWidth";
    const string k_HeightPref = "Editools_Greyroad_DefaultHeight";
    const string k_LengthPref = "Editools_Greyroad_DefaultLength";

    internal static float DefaultWidth
    {
        get => EditorPrefs.GetFloat(k_WidthPref, 10f);
        set => EditorPrefs.SetFloat(k_WidthPref, Mathf.Max(0.001f, value));
    }

    internal static float DefaultHeight
    {
        get => EditorPrefs.GetFloat(k_HeightPref, 2f);
        set => EditorPrefs.SetFloat(k_HeightPref, Mathf.Max(0.001f, value));
    }

    internal static float DefaultLength
    {
        get => EditorPrefs.GetFloat(k_LengthPref, 5f);
        set => EditorPrefs.SetFloat(k_LengthPref, Mathf.Max(0.01f, value));
    }

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greyroad", false, 12)]
    static void CreateGreyroad(MenuCommand cmd)
    {
        var parent = cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null;
        var go = PlaceGreyroad(Vector3.zero, Quaternion.identity, parent);
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greyroad", true)]
    static bool CreateGreyroadValidate() => true;

    internal static GameObject PlaceGreyroad(Vector3 worldPos, Quaternion worldRot, Transform parent)
    {
        var go = GreyPrimitiveSettings.PlacePrimitive<Greyroad>("Greyroad", worldPos, worldRot, parent);

        var road = go.GetComponent<Greyroad>();
        if (road != null)
        {
            road.BaseWidth  = DefaultWidth;
            road.BaseHeight = DefaultHeight;
            road.Vertices.Clear();
            foreach (var v in Greyroad.CreateDefaultVertices(DefaultLength))
                road.Vertices.Add(v);
            road.RebuildMesh();
        }

        return go;
    }
}

static class GreyroadSettingsGUI
{
    static readonly GUIContent k_WidthLabel = new GUIContent(
        "Default Width",
        "Base width of the road's box cross-section for new Greyroads.");

    static readonly GUIContent k_HeightLabel = new GUIContent(
        "Default Height",
        "Base height (thickness) of the road's box cross-section for new Greyroads.");

    static readonly GUIContent k_LengthLabel = new GUIContent(
        "Default Length",
        "Total length (between the two edge vertices) of newly created Greyroads, in local units.");

    internal static void DrawGUI()
    {
        float curWidth  = GreyroadSettings.DefaultWidth;
        float nextWidth = EditorGUILayout.FloatField(k_WidthLabel, curWidth);
        if (!Mathf.Approximately(nextWidth, curWidth))
            GreyroadSettings.DefaultWidth = nextWidth;

        EditorGUILayout.Space(4);

        float curHeight  = GreyroadSettings.DefaultHeight;
        float nextHeight = EditorGUILayout.FloatField(k_HeightLabel, curHeight);
        if (!Mathf.Approximately(nextHeight, curHeight))
            GreyroadSettings.DefaultHeight = nextHeight;

        EditorGUILayout.Space(4);

        float curLength  = GreyroadSettings.DefaultLength;
        float nextLength = EditorGUILayout.FloatField(k_LengthLabel, curLength);
        if (!Mathf.Approximately(nextLength, curLength))
            GreyroadSettings.DefaultLength = nextLength;
    }
}

static class GreyroadCreationShortcut
{
    [Shortcut("Editools/Create Greyroad", typeof(SceneView), KeyCode.R, ShortcutModifiers.Control)]
    static void Execute()
    {
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = GreyroadSettings.PlaceGreyroad(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
