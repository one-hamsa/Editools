using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;

// Organizational folders never own geometry or change Boolean input membership.
static class GreyboxLinkHierarchy
{
    internal static void Collect(Greybox seed, List<Greybox> boxes)
    {
        using var visitedScope = HashSetPool<int>.Get(out var visited);
        foreach (var box in boxes) visited.Add(box.GetInstanceID());
        if (visited.Add(seed.GetInstanceID())) boxes.Add(seed);
        using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
        for (int i = 0; i < boxes.Count; i++)
        {
            boxes[i].GetSeams(seams);
            foreach (var seam in seams)
                if (visited.Add(seam.other.GetInstanceID())) boxes.Add(seam.other);
        }
    }

    static Transform BooleanScope(Greybox box)
    {
        var scope = GreyBooleanOrchestrator.OperandScope(box);
        return scope != null ? scope.transform : null;
    }

    internal static Transform GroupParent(List<Greybox> boxes) => FindDestination(boxes);

    internal static bool CanLink(Greybox first, Greybox second)
    {
        using var scope = ListPool<Greybox>.Get(out var boxes);
        Collect(first, boxes);
        Collect(second, boxes);
        return Validate(boxes);
    }

    internal static bool Validate(List<Greybox> boxes, bool requireMovement = true)
    {
        var scene = boxes[0].gameObject.scene;
        GreyPrimitive booleanScope = null;
        foreach (var member in boxes)
        {
            var scope = GreyBooleanOrchestrator.OperandScope(member);
            if (scope != null && GreyGeometry.Owner(scope) != null) { booleanScope = scope; break; }
        }
        var manager = boxes[0].GetComponentInParent<GreyPrimitiveManager>();
        foreach (var box in boxes)
        {
            string reason = null;
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(box.gameObject);
            if (EditorUtility.IsPersistent(box) || !scene.IsValid() || box.gameObject.scene != scene)
                reason = "Linked boxes must be in the same scene or prefab stage.";
            else if (requireMovement && stage != null && stage.prefabContentsRoot == box.gameObject)
                reason = "A prefab contents root cannot be moved into a linked group. Put its boxes below the root first.";
            else if (requireMovement && PrefabUtility.IsPartOfNonAssetPrefabInstance(box)
                && !PrefabUtility.IsOutermostPrefabInstanceRoot(box.gameObject))
                reason = "Open the prefab to link its internal boxes, or unpack it first.";
            else if (GreyBooleanOrchestrator.OperandScope(box) is GreyPrimitive scope
                && GreyGeometry.Owner(scope) != null && scope != booleanScope)
                reason = "Boxes belonging to different Boolean inputs cannot share a linked group.";
            else if (box.GetComponentInParent<GreyPrimitiveManager>() != manager)
                reason = "Linked boxes must use the same Grey Primitive Manager.";
            if (reason == null) continue;
            Debug.LogWarning("[Greybox] " + reason, box);
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.ShowNotification(new GUIContent(reason));
            return false;
        }
        if (!requireMovement) return true;
        var destination = booleanScope != null ? booleanScope.transform : FindDestination(boxes);
        if (destination == null) destination = CommonParent(boxes);
        foreach (var box in boxes)
        {
            if (CanPreserveAttachments(box, destination, boxes)) continue;
            const string reason = "Grouping would distort attached content under these scaled parents. Move the attachment or remove the parent shear first.";
            Debug.LogWarning("[Greybox] " + reason, box);
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.ShowNotification(new GUIContent(reason));
            return false;
        }
        return true;
    }

    internal static bool CanPreserveAttachments(Greybox box, Transform destination, List<Greybox> boxes)
    {
        if (box.transform.parent == destination || !HasAttachedContent(box, boxes)) return true;
        Vector3 x = box.transform.TransformVector(Vector3.right);
        Vector3 y = box.transform.TransformVector(Vector3.up);
        Vector3 z = box.transform.TransformVector(Vector3.forward);
        if (destination != null)
        {
            x = destination.InverseTransformVector(x);
            y = destination.InverseTransformVector(y);
            z = destination.InverseTransformVector(z);
        }
        return Mathf.Abs(Vector3.Dot(x.normalized, y.normalized)) < 0.0001f
            && Mathf.Abs(Vector3.Dot(x.normalized, z.normalized)) < 0.0001f
            && Mathf.Abs(Vector3.Dot(y.normalized, z.normalized)) < 0.0001f;
    }

    static bool HasAttachedContent(Greybox box, List<Greybox> boxes)
    {
        foreach (Transform child in box.transform)
            if (!child.TryGetComponent<Greybox>(out var member) || !boxes.Contains(member)) return true;
        foreach (var component in box.GetComponents<Component>())
            if (!(component is Transform || component is Greybox || component is MeshFilter
                || component is MeshRenderer || component is MeshCollider)) return true;
        return false;
    }

    static Transform FindDestination(List<Greybox> boxes)
    {
        GreyboxCompound destinationGroup = null;
        foreach (var box in boxes)
        {
            var operand = GreyBooleanOrchestrator.OperandScope(box);
            if (operand is GreyboxCompound compound
                && (destinationGroup == null || GreyGeometry.Owner(compound) != null)) destinationGroup = compound;
        }
        if (destinationGroup != null) return destinationGroup.transform;

        var common = CommonParent(boxes);
        if (Reusable(common, boxes)) return common;
        using var scope = ListPool<Transform>.Get(out var folders);
        CollectFolders(boxes, folders);
        Transform destination = null;
        foreach (var folder in folders)
        {
            if (!Reusable(folder, boxes)) continue;
            if (destination == null || Depth(folder) < Depth(destination)
                || (Depth(folder) == Depth(destination) && folder.GetInstanceID() < destination.GetInstanceID()))
                destination = folder;
        }
        return destination;
    }

    internal static void CollectFolders(List<Greybox> boxes, List<Transform> folders)
    {
        var common = CommonParent(boxes);
        foreach (var box in boxes)
            for (var parent = box.transform.parent; parent != null; parent = parent.parent)
            {
                if (!PlainFolder(parent) || !ExclusiveParent(parent, boxes)) break;
                if (!folders.Contains(parent)) folders.Add(parent);
                if (parent == common) break;
            }
    }

    static void CollectNestedFolders(Transform parent, List<Greybox> boxes, List<Transform> folders)
    {
        foreach (Transform child in parent)
        {
            if (!PlainFolder(child) || CountExclusiveMembers(child, boxes) < 0) continue;
            if (!folders.Contains(child)) folders.Add(child);
            CollectNestedFolders(child, boxes, folders);
        }
    }

    static bool PlainFolder(Transform parent)
    {
        if (parent == null || PrefabUtility.IsPartOfAnyPrefab(parent.gameObject)) return false;
        var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(parent.gameObject);
        if (stage != null && stage.prefabContentsRoot == parent.gameObject) return false;
        var components = parent.GetComponents<Component>();
        return components.Length == 1 && components[0] is Transform;
    }

    internal static bool ExclusiveParent(Transform parent, List<Greybox> boxes)
        => parent != null && CountExclusiveMembers(parent, boxes) > 0;

    static int CountExclusiveMembers(Transform parent, List<Greybox> boxes)
    {
        int count = 0;
        foreach (Transform child in parent)
        {
            if (child.TryGetComponent<Greybox>(out var box))
            {
                if (!boxes.Contains(box)) return -1;
                count++;
            }
            else
            {
                if (!PlainFolder(child)) return -1;
                int nested = CountExclusiveMembers(child, boxes);
                if (nested < 0) return -1;
                count += nested;
            }
        }
        return count;
    }

    static bool Reusable(Transform parent, List<Greybox> boxes)
    {
        if (!PlainFolder(parent) || !ExclusiveParent(parent, boxes)) return false;
        foreach (var box in boxes)
        {
            if (parent.IsChildOf(box.transform)) return false;
            if (box.gameObject.activeInHierarchy && !parent.gameObject.activeInHierarchy) return false;
            if (!CanPreserveAttachments(box, parent, boxes)) return false;
        }
        return true;
    }

    static Transform CommonParent(List<Greybox> boxes)
    {
        Transform parent = boxes[0].transform.parent;
        while (parent != null)
        {
            bool common = true;
            foreach (var box in boxes)
                if (parent == box.transform || !box.transform.IsChildOf(parent)) { common = false; break; }
            if (common) return parent;
            parent = parent.parent;
        }
        return null;
    }

    internal static Transform LinkedExtrusionParent(Greybox source)
    {
        using var scope = ListPool<Greybox>.Get(out var boxes);
        Collect(source, boxes);
        var destination = FindDestination(boxes);
        return destination != null ? destination : source.transform.parent;
    }

    internal static Transform UnlinkedExtrusionParent(Greybox source)
    {
        Transform outsideBoolean = null;
        bool boolean = false;
        for (var ancestor = source.transform.parent; ancestor != null; ancestor = ancestor.parent)
            if (ancestor.TryGetComponent<GreyBooleanResult>(out _) || ancestor.TryGetComponent<GreyboxCompound>(out _))
            {
                boolean = true;
                outsideBoolean = ancestor.parent;
            }
        if (boolean) return outsideBoolean;
        var parent = source.transform.parent;
        using var scope = ListPool<Greybox>.Get(out var boxes);
        Collect(source, boxes);
        var group = FindDestination(boxes);
        return boxes.Count > 1 && group != null && PlainFolder(group) ? group.parent : parent;
    }

    internal static void Organize(Greybox seed, string label)
    {
        using var scope = ListPool<Greybox>.Get(out var boxes);
        Collect(seed, boxes);
        if (boxes.Count < 2 || !Validate(boxes)) return;
        using var foldersScope = ListPool<Transform>.Get(out var folders);
        Transform destination = FindDestination(boxes);
        CollectFolders(boxes, folders);
        foreach (var box in boxes)
            if (GreyBooleanOrchestrator.OperandScope(box) != null)
            {
                destination = GreyBooleanOrchestrator.EnsureLinkedGroup(boxes).transform;
                break;
            }
        if (destination == null)
        {
            var host = CommonParent(boxes);
            var go = new GameObject("Linked Greyboxes");
            SceneManager.MoveGameObjectToScene(go, seed.gameObject.scene);
            go.transform.SetParent(host, false);
            go.layer = seed.gameObject.layer;
            Undo.RegisterCreatedObjectUndo(go, label);
            destination = go.transform;
        }
        CollectNestedFolders(destination, boxes, folders);
        for (int i = 0; i < folders.Count; i++) CollectNestedFolders(folders[i], boxes, folders);
        // Move nested members first so moving an ancestor cannot change their world geometry.
        boxes.Sort((a, b) => Depth(b.transform).CompareTo(Depth(a.transform)));
        foreach (var box in boxes) MoveBox(box, destination, label);
        RemoveEmptyFolders(folders, destination);
        foreach (var box in boxes) GreyPrimitiveEditor.RebuildPrimitiveAndDependents(box);
    }

    internal static void RemoveEmptyFolders(List<Transform> folders, Transform destination)
    {
        folders.RemoveAll(folder => folder == null);
        folders.Sort((a, b) => Depth(b).CompareTo(Depth(a)));
        foreach (var folder in folders)
            if (folder != null && folder != destination && folder.childCount == 0)
                Undo.DestroyObjectImmediate(folder.gameObject);
    }

    internal static int Depth(Transform transform)
    {
        int depth = 0;
        while (transform.parent != null) { depth++; transform = transform.parent; }
        return depth;
    }

    internal static void MoveBox(Greybox box, Transform destination, string label)
    {
        var transform = box.transform;
        if (transform.parent == destination) return;
        var worldCorners = box.GetWorldCorners();
        bool active = box.gameObject.activeInHierarchy;
        Undo.RegisterCompleteObjectUndo(box, label);
        Undo.SetTransformParent(transform, destination, label);
        // Unity cannot preserve shear in a reparented TRS. Preserve the actual box instead.
        for (int i = 0; i < 8; i++) box.Corners[i] = transform.InverseTransformPoint(worldCorners[i]);
        if (!active && box.gameObject.activeInHierarchy)
        {
            Undo.RecordObject(box.gameObject, label);
            box.gameObject.SetActive(false);
        }
        PrefabUtility.RecordPrefabInstancePropertyModifications(box);
        PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
        EditorUtility.SetDirty(box);
    }

    internal static void AfterUnlink(List<Greybox> previousGroup, Transform previousParent, string label)
    {
        // Unlink changes welds, not the membership of an authored geometry input.
        foreach (var box in previousGroup)
            if (GreyBooleanOrchestrator.OperandScope(box) != null)
            {
                GreyBooleanOrchestrator.ReBakeFrom(box);
                return;
            }
        using var foldersScope = ListPool<Transform>.Get(out var folders);
        CollectFolders(previousGroup, folders);
        for (int i = 0; i < folders.Count; i++) CollectNestedFolders(folders[i], previousGroup, folders);
        var formerGroup = FindDestination(previousGroup);
        Transform releasedParent = PlainFolder(formerGroup) ? formerGroup.parent : previousParent;
        using var visitedScope = HashSetPool<int>.Get(out var visited);
        using var componentScope = ListPool<Greybox>.Get(out var component);
        foreach (var box in previousGroup)
        {
            if (!visited.Add(box.GetInstanceID())) continue;
            component.Clear();
            Collect(box, component);
            foreach (var member in component) visited.Add(member.GetInstanceID());
            if (component.Count > 1) Organize(box, label);
            else if (PlainFolder(formerGroup) && folders.Contains(box.transform.parent)
                && CanPreserveAttachments(box, releasedParent, component))
            {
                MoveBox(box, releasedParent, label);
                GreyPrimitiveEditor.RebuildPrimitiveAndDependents(box);
            }
        }
        RemoveEmptyFolders(folders, null);
    }
}
