#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreyboxSettings
{
    const string k_ScaleXPref = "Editools_Greybox_DefaultScaleX";
    const string k_ScaleYPref = "Editools_Greybox_DefaultScaleY";
    const string k_ScaleZPref = "Editools_Greybox_DefaultScaleZ";

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

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greybox", false, 10)]
    static void CreateGreybox(MenuCommand cmd)
    {
        var go = GreyPrimitiveSettings.PlacePrimitive<Greybox>("Greybox", Vector3.zero, Quaternion.identity,
            cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null);
        go.transform.localScale = DefaultScale;
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greybox", true)]
    static bool CreateGreyboxValidate() => true;

    internal static GameObject PlaceGreybox(Vector3 worldPos, Quaternion worldRot, Transform parent)
    {
        var go = GreyPrimitiveSettings.PlacePrimitive<Greybox>("Greybox", worldPos, worldRot, parent);
        go.transform.localScale = DefaultScale;
        return go;
    }
}

static class GreyboxSettingsGUI
{
    static readonly GUIContent k_ScaleLabel = new GUIContent(
        "Default Scale",
        "Local scale applied to new Greyboxes on creation. " +
        "The mesh corners define a 1x1x1 unit cube, so a scale of 10 creates a 10x10x10 box.");

    internal static void DrawGUI()
    {
        var curScale  = GreyboxSettings.DefaultScale;
        var nextScale = EditorGUILayout.Vector3Field(k_ScaleLabel, curScale);
        if (nextScale != curScale)
            GreyboxSettings.DefaultScale = nextScale;
    }
}

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
