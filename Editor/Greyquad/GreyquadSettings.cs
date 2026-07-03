#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreyquadSettings
{
    const string k_SizeXPref = "Editools_Greyquad_DefaultSizeX";
    const string k_SizeZPref = "Editools_Greyquad_DefaultSizeZ";

    internal static Vector2 DefaultSize
    {
        get => new Vector2(
            EditorPrefs.GetFloat(k_SizeXPref, 10f),
            EditorPrefs.GetFloat(k_SizeZPref, 10f));
        set
        {
            EditorPrefs.SetFloat(k_SizeXPref, value.x);
            EditorPrefs.SetFloat(k_SizeZPref, value.y);
        }
    }

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greyquad", false, 10)]
    static void CreateGreyquad(MenuCommand cmd)
    {
        var go = PlaceGreyquad(Vector3.zero, Quaternion.identity,
            cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null);
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greyquad", true)]
    static bool CreateGreyquadValidate() => true;

    // A Greyquad never casts shadows and never gets a collider — the shared
    // DefaultMeshCollider/DefaultShadowCasting prefs don't apply to it.
    internal static GameObject PlaceGreyquad(Vector3 worldPos, Quaternion worldRot, Transform parent)
    {
        var go = GreyPrimitiveSettings.PlacePrimitive<Greyquad>("Greyquad", worldPos, worldRot, parent,
            meshCollider: false, castShadows: false);
        var size = DefaultSize;
        go.transform.localScale = new Vector3(size.x, 1f, size.y);
        return go;
    }
}

static class GreyquadSettingsGUI
{
    static readonly GUIContent k_SizeLabel = new GUIContent(
        "Default Size",
        "Local scale (X, Z) applied to new Greyquads on creation. " +
        "The mesh is a 1x1 unit quad, so a size of 10 creates a 10x10 quad.");

    internal static void DrawGUI()
    {
        var curSize  = GreyquadSettings.DefaultSize;
        var nextSize = EditorGUILayout.Vector2Field(k_SizeLabel, curSize);
        if (nextSize != curSize)
            GreyquadSettings.DefaultSize = nextSize;
    }
}

static class GreyquadCreationShortcut
{
    [Shortcut("Editools/Create Greyquad", typeof(SceneView), KeyCode.Q, ShortcutModifiers.Control)]
    static void Execute()
    {
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = GreyquadSettings.PlaceGreyquad(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
