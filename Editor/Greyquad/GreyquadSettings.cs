#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreyquadSettings
{
    internal static Vector2 DefaultSize => EditoolsSettings.Current.greyquadDefaultSize;

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greyquad", false, 10)]
    static void CreateGreyquad(MenuCommand cmd)
    {
        var go = PlaceGreyquad(Vector3.zero, Quaternion.identity,
            cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null);
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greyquad", true)]
    static bool CreateGreyquadValidate() => GreyPrimitiveSettings.Enabled;

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
static class GreyquadCreationShortcut
{
    [Shortcut("Editools/Create Greyquad", typeof(SceneView), KeyCode.Q, ShortcutModifiers.Control)]
    static void Execute()
    {
        if (!GreyPrimitiveSettings.CanCreate) return;
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = GreyquadSettings.PlaceGreyquad(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
