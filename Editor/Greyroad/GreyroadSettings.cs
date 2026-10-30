#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreyroadSettings
{
    internal static float DefaultWidth => EditoolsSettings.Current.greyroadDefaultWidth;
    internal static float DefaultHeight => EditoolsSettings.Current.greyroadDefaultHeight;
    internal static float DefaultLength => EditoolsSettings.Current.greyroadDefaultLength;

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greyroad", false, 12)]
    static void CreateGreyroad(MenuCommand cmd)
    {
        var parent = cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null;
        var go = PlaceGreyroad(Vector3.zero, Quaternion.identity, parent);
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greyroad", true)]
    static bool CreateGreyroadValidate() => GreyPrimitiveSettings.Enabled;

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
static class GreyroadCreationShortcut
{
    [Shortcut("Editools/Create Greyroad", typeof(SceneView), KeyCode.R, ShortcutModifiers.Control)]
    static void Execute()
    {
        if (!GreyPrimitiveSettings.CanCreate) return;
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = GreyroadSettings.PlaceGreyroad(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
