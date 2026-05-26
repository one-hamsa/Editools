#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class StickerMenu
{
    [MenuItem("GameObject/3D Object/Sticker", false, 13)]
    static void CreateSticker(MenuCommand cmd)
    {
        var parent = cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null;
        var go = PlaceSticker(Vector3.zero, Quaternion.identity, parent);
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Sticker", true)]
    static bool CreateStickerValidate() => true;

    /// <summary>
    /// Creates a Sticker GameObject at the given pose with all Editools defaults applied.
    /// Stickers diverge from the shared GreyPrimitive create path on two points: they never
    /// get a MeshCollider, and they take shadow-casting from <see cref="StickerSettings"/>
    /// (which defaults to Off) rather than the shared GreyPrimitive default.
    /// </summary>
    internal static GameObject PlaceSticker(Vector3 worldPos, Quaternion worldRot, Transform parent)
    {
        var go = new GameObject("Sticker");
        if (parent != null)
            go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create Sticker");

        // Adding Sticker pulls in MeshFilter and MeshRenderer via RequireComponent. LocalMaterial
        // is added separately via reflection — Editools cannot hard-reference UD.Rendering.
        var sticker = go.AddComponent<Sticker>();
        LocalMaterialBridge.EnsureOn(go);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial    = StickerSettings.GetOrCreateDefaultMaterial();
            mr.shadowCastingMode = StickerSettings.DefaultShadowCastingMode;
        }

        go.transform.position = worldPos;
        go.transform.rotation = worldRot;
        go.isStatic = StickerSettings.DefaultStatic;
        go.layer    = StickerSettings.DefaultLayer;

        ApplyDefaultsToSticker(sticker);

        Selection.activeObject = go;
        return go;
    }

    static void ApplyDefaultsToSticker(Sticker sticker)
    {
        var so = new SerializedObject(sticker);
        SetIfPresent(so, "_columns",         StickerSettings.DefaultColumns);
        SetIfPresent(so, "_rows",            StickerSettings.DefaultRows);
        SetIfPresent(so, "_rayDistAbove",    StickerSettings.DefaultRayDistAbove);
        SetIfPresent(so, "_rayDistBelow",    StickerSettings.DefaultRayDistBelow);
        SetIfPresent(so, "_retryBendAngle",  StickerSettings.DefaultRetryBendAngle);
        SetIfPresent(so, "_relaxIterations", StickerSettings.DefaultRelaxIterations);
        SetIfPresent(so, "_relaxStrength",   StickerSettings.DefaultRelaxStrength);
        SetIfPresent(so, "_relaxRigidity",   StickerSettings.DefaultRelaxRigidity);
        SetIfPresent(so, "_surfaceOffset",   StickerSettings.DefaultSurfaceOffset);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetIfPresent(SerializedObject so, string propertyPath, int value)
    {
        var p = so.FindProperty(propertyPath);
        if (p != null) p.intValue = value;
    }

    static void SetIfPresent(SerializedObject so, string propertyPath, float value)
    {
        var p = so.FindProperty(propertyPath);
        if (p != null) p.floatValue = value;
    }
}

static class StickerCreationShortcut
{
    [Shortcut("Editools/Create Sticker", typeof(SceneView), KeyCode.K, ShortcutModifiers.Control | ShortcutModifiers.Shift)]
    static void Execute()
    {
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = StickerMenu.PlaceSticker(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
