#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreypipeSettings
{
    const string k_GirthPref  = "Editools_Greypipe_DefaultGirth";
    const string k_LengthPref = "Editools_Greypipe_DefaultLength";

    internal static float DefaultGirth
    {
        get => EditorPrefs.GetFloat(k_GirthPref, 0.5f);
        set => EditorPrefs.SetFloat(k_GirthPref, Mathf.Max(0.001f, value));
    }

    internal static float DefaultLength
    {
        get => EditorPrefs.GetFloat(k_LengthPref, 5f);
        set => EditorPrefs.SetFloat(k_LengthPref, Mathf.Max(0.01f, value));
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
            // Replace the default vertex list with one sized to the user's preferred length.
            pipe.Vertices.Clear();
            foreach (var v in Greypipe.CreateDefaultVertices(DefaultLength))
                pipe.Vertices.Add(v);
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

    static readonly GUIContent k_LengthLabel = new GUIContent(
        "Default Length",
        "Total length (between the two edge vertices) of newly created Greypipes, in local units.");

    internal static void DrawGUI()
    {
        float curGirth  = GreypipeSettings.DefaultGirth;
        float nextGirth = EditorGUILayout.FloatField(k_GirthLabel, curGirth);
        if (!Mathf.Approximately(nextGirth, curGirth))
            GreypipeSettings.DefaultGirth = nextGirth;

        EditorGUILayout.Space(4);

        float curLength  = GreypipeSettings.DefaultLength;
        float nextLength = EditorGUILayout.FloatField(k_LengthLabel, curLength);
        if (!Mathf.Approximately(nextLength, curLength))
            GreypipeSettings.DefaultLength = nextLength;
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
