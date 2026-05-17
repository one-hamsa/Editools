#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreypipeSettings
{
    const string k_GirthPref = "Editools_Greypipe_DefaultGirth";
    const string k_SidesPref = "Editools_Greypipe_DefaultSides";

    internal static float DefaultGirth
    {
        get => EditorPrefs.GetFloat(k_GirthPref, 0.5f);
        set => EditorPrefs.SetFloat(k_GirthPref, Mathf.Max(0.001f, value));
    }

    internal static int DefaultSides
    {
        get => EditorPrefs.GetInt(k_SidesPref, 8);
        set => EditorPrefs.SetInt(k_SidesPref, Mathf.Max(3, value));
    }

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greypipe", false, 11)]
    static void CreateGreypipe(MenuCommand cmd)
    {
        var parent = cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null;
        var go = PlaceGreypipe(Vector3.zero, Quaternion.identity, parent);
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greypipe", true)]
    static bool CreateGreypipeValidate() => true;

    internal static GameObject PlaceGreypipe(Vector3 worldPos, Quaternion worldRot, Transform parent)
    {
        var go = GreyPrimitiveSettings.PlacePrimitive<Greypipe>("Greypipe", worldPos, worldRot, parent);

        var pipe = go.GetComponent<Greypipe>();
        if (pipe != null)
        {
            pipe.BaseGirth = DefaultGirth;
            pipe.Sides     = DefaultSides;
            pipe.RebuildMesh();
        }

        return go;
    }
}

static class GreypipeSettingsGUI
{
    static readonly GUIContent k_GirthLabel = new GUIContent(
        "Default Girth",
        "Base radius of the pipe's cross-section for new Greypipes.");

    static readonly GUIContent k_SidesLabel = new GUIContent(
        "Default Sides",
        "Number of polygon sides for the circular cross-section on new Greypipes.");

    internal static void DrawGUI()
    {
        float curGirth  = GreypipeSettings.DefaultGirth;
        float nextGirth = EditorGUILayout.FloatField(k_GirthLabel, curGirth);
        if (!Mathf.Approximately(nextGirth, curGirth))
            GreypipeSettings.DefaultGirth = nextGirth;

        EditorGUILayout.Space(4);

        int curSides  = GreypipeSettings.DefaultSides;
        int nextSides = EditorGUILayout.IntField(k_SidesLabel, curSides);
        if (nextSides != curSides)
            GreypipeSettings.DefaultSides = nextSides;
    }
}

static class GreypipeCreationShortcut
{
    [Shortcut("Editools/Create Greypipe", typeof(SceneView), KeyCode.T, ShortcutModifiers.Control)]
    static void Execute()
    {
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = GreypipeSettings.PlaceGreypipe(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
