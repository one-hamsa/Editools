#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreypipeSettings
{
    internal static float DefaultGirth => EditoolsSettings.Current.greypipeDefaultGirth;
    internal static float DefaultLength => EditoolsSettings.Current.greypipeDefaultLength;

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greypipe", false, 11)]
    static void CreateGreypipe(MenuCommand cmd)
    {
        var parent = cmd.context as GameObject != null ? (cmd.context as GameObject).transform : null;
        var go = PlaceGreypipe(Vector3.zero, Quaternion.identity, parent);
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greypipe", true)]
    static bool CreateGreypipeValidate() => GreyPrimitiveSettings.Enabled;

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
static class GreypipeCreationShortcut
{
    [Shortcut("Editools/Create Greypipe", typeof(SceneView), KeyCode.T, ShortcutModifiers.Control)]
    static void Execute()
    {
        if (!GreyPrimitiveSettings.CanCreate) return;
        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        var go = GreypipeSettings.PlaceGreypipe(spawnPos, Quaternion.identity, selectedParent);
        SnapToSurface.BeginSnap(go);
    }
}
#endif
