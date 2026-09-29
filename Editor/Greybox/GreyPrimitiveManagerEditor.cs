using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Pool;

[CustomEditor(typeof(GreyPrimitiveManager))]
[CanEditMultipleObjects]
public class GreyPrimitiveManagerEditor : Editor
{
    // Push-only propagation: inspector edits on the manager push RebuildMesh to every descendant
    // primitive via the change-check below. Undo/redo restores serialized fields but the children's
    // meshes were built from pre-undo manager values — so we re-push, but only when the undo actually
    // changed this manager. Each selected manager's density signature is cached so an undo elsewhere
    // is a cheap O(1) comparison instead of a fan-out over every descendant.
    int[] _lastSignatures;
    static readonly Dictionary<int, GreyPrimitiveManager> s_managers = new Dictionary<int, GreyPrimitiveManager>();
    static readonly Dictionary<int, GreyPrimitiveManager> s_primaries = new Dictionary<int, GreyPrimitiveManager>();

    internal static void Register(GreyPrimitiveManager manager)
    {
        if (manager == null || EditorUtility.IsPersistent(manager) || !manager.gameObject.scene.IsValid()) return;
        s_managers[manager.GetInstanceID()] = manager;
    }

    static void MakePrimary(GreyPrimitiveManager manager)
    {
        var scene = manager.gameObject.scene;
        if (!scene.IsValid() || !scene.isLoaded) return;
        Register(manager);
        s_primaries[scene.handle] = manager;
        foreach (var root in scene.GetRootGameObjects())
            foreach (var other in root.GetComponentsInChildren<GreyPrimitiveManager>(true))
            {
                Register(other);
                if (other != manager && other.Primary) ClearPrimary(other, recordUndo: true);
            }
    }

    static void ClearPrimary(GreyPrimitiveManager manager, bool recordUndo)
    {
        var settings = new SerializedObject(manager);
        settings.FindProperty("_primary").boolValue = false;
        if (recordUndo) settings.ApplyModifiedProperties();
        else
        {
            settings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(manager);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        }
    }

    internal static void NormalizePrimaryManagers()
    {
        using var scope = ListPool<int>.Get(out var removed);
        foreach (var pair in s_primaries)
            if (pair.Value == null || !pair.Value.Primary || pair.Value.gameObject.scene.handle != pair.Key
                || !pair.Value.gameObject.scene.isLoaded) removed.Add(pair.Key);
        foreach (int id in removed) s_primaries.Remove(id);
        removed.Clear();
        foreach (var pair in s_managers)
        {
            var manager = pair.Value;
            if (manager == null || !manager.gameObject.scene.isLoaded) { removed.Add(pair.Key); continue; }
            if (!manager.Primary) continue;
            int scene = manager.gameObject.scene.handle;
            if (!s_primaries.TryGetValue(scene, out var primary)) s_primaries[scene] = manager;
            else if (primary != manager) ClearPrimary(manager, recordUndo: false);
        }
        foreach (int id in removed) s_managers.Remove(id);
    }

    void OnEnable()
    {
        foreach (var item in targets) Register(item as GreyPrimitiveManager);
        Undo.undoRedoPerformed += OnUndoRedo;
        CaptureSignatures();
    }

    void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

    void CaptureSignatures()
    {
        _lastSignatures = new int[targets.Length];
        for (int i = 0; i < targets.Length; i++)
            _lastSignatures[i] = targets[i] is GreyPrimitiveManager mgr ? mgr.ComputeDensitySignature() : 0;
    }

    void OnUndoRedo()
    {
        if (_lastSignatures == null || _lastSignatures.Length != targets.Length)
            CaptureSignatures();

        for (int i = 0; i < targets.Length; i++)
        {
            if (!(targets[i] is GreyPrimitiveManager mgr)) continue;
            int sig = mgr.ComputeDensitySignature();
            if (sig == _lastSignatures[i]) continue; // undo didn't touch this manager — descendants unaffected
            _lastSignatures[i] = sig;
            mgr.PushRebuildToChildren();
        }
    }

    public override void OnInspectorGUI()
    {
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            if (_lastSignatures == null || _lastSignatures.Length != targets.Length)
                CaptureSignatures();

            for (int i = 0; i < targets.Length; i++)
                if (targets[i] is GreyPrimitiveManager mgr)
                {
                    if (mgr.Primary) MakePrimary(mgr);
                    int signature = mgr.ComputeDensitySignature();
                    if (_lastSignatures[i] != signature) mgr.PushRebuildToChildren();
                    _lastSignatures[i] = signature;
                }
        }
    }
}
