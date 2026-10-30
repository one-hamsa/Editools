#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

static class GreyboxSettings
{
    internal static Vector3 DefaultScale => EditoolsSettings.Current.greyboxDefaultScale;

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
    static bool CreateGreyboxValidate() => GreyPrimitiveSettings.Enabled;

    internal static GameObject PlaceGreybox(Vector3 worldPos, Quaternion worldRot, Transform parent, bool select = true)
    {
        var go = GreyPrimitiveSettings.PlacePrimitive<Greybox>("Greybox", worldPos, worldRot, parent, select: select);
        go.transform.localScale = DefaultScale;
        return go;
    }
}
static class GreyboxCreationShortcut
{
    // The selection the create press was fired on, kept for a follow-up press to fit the box to:
    // creation moves Selection to the new box, so it can't be read back later.
    static Transform[] s_fitTargets;
    static Greybox     s_pendingBox;

    [Shortcut("Editools/Create Greybox", typeof(SceneView), KeyCode.G, ShortcutModifiers.Control)]
    static void Execute()
    {
        if (!GreyPrimitiveSettings.CanCreate) return;
        if (IsPlacingPendingBox())
        {
            ToggleSelectionFit();
            return;
        }

        var sv = SceneView.lastActiveSceneView;
        Vector3 spawnPos = sv != null ? sv.pivot : Vector3.zero;

        Transform selectedParent = Selection.activeTransform?.parent;
        s_fitTargets = Selection.transforms;

        var go = GreyboxSettings.PlaceGreybox(spawnPos, Quaternion.identity, selectedParent);
        s_pendingBox = go.GetComponent<Greybox>();
        SnapToSurface.BeginSnap(go);
    }

    /// <summary>True while the box from the last create press is still being placed.</summary>
    static bool IsPlacingPendingBox() =>
        s_pendingBox != null
        && SnapToSurface.IsSnapping
        && SnapToSurface.SnappingObject == s_pendingBox.gameObject;

    /// <summary>
    /// Wrap the box being placed around the selection the create press was fired on, and stop the
    /// cursor from moving it so the placement click confirms the fitted shape. Pressing again puts
    /// the default box back and hands placement to the cursor again.
    /// </summary>
    static void ToggleSelectionFit()
    {
        // This chord's Ctrl is still held, and snap mode reads a held Ctrl as "cancel".
        SnapToSurface.SuppressModifierCancelUntilRelease();

        if (SnapToSurface.PlacementFrozen)
        {
            GreyboxSelectionFit.ResetToCreationShape(s_pendingBox);
            SnapToSurface.SetPlacementFrozen(false);
            return;
        }

        if (GreyboxSelectionFit.FitToTargets(s_pendingBox, s_fitTargets))
            SnapToSurface.SetPlacementFrozen(true);
    }
}
#endif
