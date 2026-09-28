#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;

// Owns structural changes to the geometry graph. Welds and folders do not define Boolean inputs.
static class GreyBooleanOrchestrator
{
    const string k_Label = "Edit Boolean";
    static readonly Dictionary<int, GreyPrimitive> s_nodes = new Dictionary<int, GreyPrimitive>();
    static readonly Dictionary<int, long> s_structure = new Dictionary<int, long>();
    static readonly HashSet<int> s_pending = new HashSet<int>();

    public static void Sync(GreyPrimitive prim)
    {
        if (prim == null || !s_pending.Add(prim.GetInstanceID())) return;
        int id = prim.GetInstanceID();
        EditorApplication.delayCall += () =>
        {
            s_pending.Remove(id);
            if (prim != null) Reconcile(prim);
        };
    }

    internal static GreyPrimitive ResolveObject(GameObject go)
    {
        if (go == null) return null;
        var primitive = go.GetComponent<GreyPrimitive>();
        if (primitive != null) return primitive;
        var seed = go.GetComponentInChildren<Greybox>(true);
        if (seed != null)
        {
            using var scope = ListPool<Greybox>.Get(out var boxes);
            GreyboxLinkHierarchy.Collect(seed, boxes);
            if (GreyboxLinkHierarchy.ExclusiveParent(go.transform, boxes)) return seed;
        }
        return go.GetComponentInParent<GreyPrimitive>();
    }

    internal static GreyPrimitive EditSubject(GreyPrimitive input)
        => input is Greybox && GreyGeometry.Owner(input) is GreyboxCompound compound ? compound : input;

    static GreyBooleanResult FindResultFor(GreyPrimitive subject)
        => GreyGeometry.Owner(subject) is GreyBooleanResult result && result.Subject == subject ? result : null;

    static void Reconcile(GreyPrimitive prim)
    {
        var result = FindResultFor(prim);
        var op = prim.BooleanOperator;
        if (op == null)
        {
            if (result != null && result.Operator != null) Teardown(result);
            else ReBakeFrom(prim);
            return;
        }

        // A reference to a leaf of an already consumed input means its complete visible result.
        var opOwner = GreyGeometry.Owner(op);
        if (opOwner != null && opOwner != result)
        {
            // Editing an existing input must not promote its cutter to the common outer result.
            while (opOwner is GreyboxCompound)
            {
                op = opOwner;
                opOwner = GreyGeometry.Owner(op);
            }
            if (opOwner != null && opOwner != result) op = GreyGeometry.Root(op);
        }
        if (!CanOperate(EditSubject(prim), op) || !CanPrepare(prim) || !CanPrepare(op)) return;
        int undoGroup = Undo.GetCurrentGroup();
        var subject = ResolveLinkedInput(prim);
        op = ResolveLinkedInput(op);
        if (subject != prim)
        {
            SetBoolean(subject, op, prim.BooleanCutMaterial);
            SetBoolean(prim, null, prim.BooleanCutMaterial);
        }
        else if (op != subject.BooleanOperator) SetBoolean(subject, op, subject.BooleanCutMaterial);
        result = FindResultFor(subject);
        bool created = result == null;
        if (created) result = CreateResultWrapper(subject);
        else if (result.Operator != null && result.Operator != op)
            Release(result.Operator, GreyGeometry.Root(result).transform.parent);

        Undo.RegisterCompleteObjectUndo(result, k_Label);
        result.Configure(subject, op);
        SetOwner(subject, result);
        SetOwner(op, result);
        MoveInput(op, result.transform);
        CopySettings(subject, result, created);
        Register(result);
        RebuildTree(GreyGeometry.Root(result));
        if (created) Selection.activeObject = result.gameObject;
        Undo.CollapseUndoOperations(undoGroup);
    }

    static bool CanOperate(GreyPrimitive subject, GreyPrimitive op)
    {
        string reason = null;
        if (op == null || GreyGeometry.Contains(subject, op) || GreyGeometry.Contains(op, subject))
            reason = "A Boolean cannot subtract itself or one of its own geometry inputs.";
        else if (subject.transform.IsChildOf(op.transform) || op.transform.IsChildOf(subject.transform))
            reason = "Boolean inputs cannot contain one another in the hierarchy.";
        else if (subject.gameObject.scene != op.gameObject.scene || EditorUtility.IsPersistent(subject)
            || EditorUtility.IsPersistent(op)) reason = "Boolean inputs must belong to the same scene or prefab stage.";
        if (reason == null)
        {
            using var subjectScope = HashSetPool<int>.Get(out var subjectInputs);
            using var operatorScope = HashSetPool<int>.Get(out var operatorInputs);
            CollectProspectiveInputs(subject, subjectInputs);
            CollectProspectiveInputs(op, operatorInputs);
            if (subjectInputs.Overlaps(operatorInputs)) reason = "A Boolean's complete inputs must not share linked geometry.";
        }
        if (reason == null) return true;
        Debug.LogWarning("[GreyBoolean] " + reason, subject);
        return false;
    }

    static void CollectProspectiveInputs(GreyPrimitive root, HashSet<int> inputs)
    {
        using var nodesScope = ListPool<GreyPrimitive>.Get(out var nodes);
        using var boxesScope = ListPool<Greybox>.Get(out var boxes);
        nodes.Add(root);
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null || !inputs.Add(node.GetInstanceID())) continue;
            if (node is GreyBooleanResult result) { nodes.Add(result.Subject); nodes.Add(result.Operator); }
            else if (node is GreyboxCompound compound) nodes.AddRange(compound.Parts);
            else if (node is Greybox box)
            {
                boxes.Clear();
                GreyboxLinkHierarchy.Collect(box, boxes);
                foreach (var member in boxes)
                {
                    nodes.Add(member);
                    if (OperandScope(member) is GreyboxCompound group) nodes.Add(group);
                }
            }
        }
    }

    static bool CanPrepare(GreyPrimitive input)
    {
        var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(input.gameObject);
        if ((stage != null && stage.prefabContentsRoot == input.gameObject)
            || (PrefabUtility.IsPartOfNonAssetPrefabInstance(input)
                && !PrefabUtility.IsOutermostPrefabInstanceRoot(input.gameObject)))
        {
            Debug.LogWarning("[GreyBoolean] Open the prefab to edit its internal inputs; keep geometry below the prefab contents root.", input);
            return false;
        }
        if (!(input is Greybox box)) return true;
        using var scope = ListPool<Greybox>.Get(out var boxes);
        GreyboxLinkHierarchy.Collect(box, boxes);
        return boxes.Count < 2 || GreyboxLinkHierarchy.Validate(boxes);
    }

    static GreyPrimitive ResolveLinkedInput(GreyPrimitive input)
    {
        if (!(input is Greybox box)) return input;
        using var scope = ListPool<Greybox>.Get(out var boxes);
        GreyboxLinkHierarchy.Collect(box, boxes);
        if (boxes.Count < 2 && !(GreyGeometry.Owner(box) is GreyboxCompound)) return input;
        return EnsureLinkedGroup(boxes);
    }

    internal static GreyPrimitive OperandScope(Greybox box)
    {
        GreyPrimitive node = box;
        var owner = GreyGeometry.Owner(node);
        while (owner is GreyboxCompound) { node = owner; owner = GreyGeometry.Owner(node); }
        return owner is GreyBooleanResult || node is GreyboxCompound ? node : null;
    }

    // Promote a linked component into one explicit input, reusing its organizational folder.
    internal static GreyboxCompound EnsureLinkedGroup(List<Greybox> boxes)
    {
        using var foldersScope = ListPool<Transform>.Get(out var folders);
        GreyboxLinkHierarchy.CollectFolders(boxes, folders);
        GreyPrimitive scope = null;
        foreach (var box in boxes)
        {
            var candidate = OperandScope(box);
            if (candidate != null && (scope == null || GreyGeometry.Owner(candidate) != null)) scope = candidate;
        }
        GreyboxCompound compound = scope as GreyboxCompound;
        if (compound == null)
        {
            var source = scope != null ? scope : boxes[0];
            var owner = GreyGeometry.Owner(source);
            Transform folder = owner == null ? GreyboxLinkHierarchy.GroupParent(boxes) : null;
            GameObject go;
            if (folder != null) go = folder.gameObject;
            else
            {
                go = new GameObject("Linked Greyboxes");
                SceneManager.MoveGameObjectToScene(go, source.gameObject.scene);
                go.transform.SetParent(source.transform.parent, false);
                go.transform.localPosition = source.transform.localPosition;
                go.transform.localRotation = source.transform.localRotation;
                go.transform.localScale = source.transform.localScale;
                Undo.RegisterCreatedObjectUndo(go, k_Label);
            }
            compound = Undo.AddComponent<GreyboxCompound>(go);
            compound.SubdivisionMultiplier = source.SubdivisionMultiplier;
            compound.PlanarUv = source.PlanarUv;
            compound.FlipFaces = source.FlipFaces;
            go.layer = source.gameObject.layer;
            go.isStatic = source.gameObject.isStatic;
            compound.GetComponent<MeshRenderer>().sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
            if (owner != null) ReplaceInput(owner, source, compound);
            if (owner is GreyBooleanResult result && result.Subject == compound)
            {
                SetBoolean(compound, source.BooleanOperator, source.BooleanCutMaterial);
                SetBoolean(source, null, source.BooleanCutMaterial);
            }
        }
        Undo.RegisterCompleteObjectUndo(compound, k_Label);
        using var oldGroupsScope = ListPool<GreyboxCompound>.Get(out var oldGroups);
        boxes.Sort((a, b) => GreyboxLinkHierarchy.Depth(b.transform).CompareTo(GreyboxLinkHierarchy.Depth(a.transform)));
        foreach (var box in boxes)
        {
            var owner = GreyGeometry.Owner(box);
            if (owner is GreyboxCompound previous && previous != compound)
            {
                Undo.RegisterCompleteObjectUndo(previous, k_Label);
                previous.Parts.Remove(box);
                if (!oldGroups.Contains(previous)) oldGroups.Add(previous);
            }
            if (!compound.Parts.Contains(box)) compound.Parts.Add(box);
            SetOwner(box, compound);
            GreyboxLinkHierarchy.MoveBox(box, compound.transform, k_Label);
        }
        foreach (var old in oldGroups)
            if (old.Parts.Count == 0 && old.transform.childCount == 0 && GreyGeometry.Owner(old) == null)
                Undo.DestroyObjectImmediate(old.gameObject);
        GreyboxLinkHierarchy.RemoveEmptyFolders(folders, compound.transform);
        EditorUtility.SetDirty(compound);
        Register(compound);
        return compound;
    }

    internal static void IncludeLinkedBox(Greybox source, Greybox added)
    {
        if (OperandScope(source) == null) return;
        using var scope = ListPool<Greybox>.Get(out var boxes);
        GreyboxLinkHierarchy.Collect(source, boxes);
        if (!boxes.Contains(added)) boxes.Add(added);
        EnsureLinkedGroup(boxes);
        ReBakeFrom(source);
    }

    static GreyBooleanResult CreateResultWrapper(GreyPrimitive subject)
    {
        var owner = GreyGeometry.Owner(subject);
        var st = subject.transform;
        var go = new GameObject("Boolean Result");
        SceneManager.MoveGameObjectToScene(go, subject.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(go, k_Label);
        var rt = go.transform;
        rt.SetParent(st.parent, false);
        rt.localPosition = st.localPosition;
        rt.localRotation = st.localRotation;
        rt.localScale = st.localScale;
        rt.SetSiblingIndex(st.GetSiblingIndex());
        go.SetActive(subject.gameObject.activeSelf);
        var result = Undo.AddComponent<GreyBooleanResult>(go);
        Undo.AddComponent<MeshCollider>(go);
        if (owner != null) ReplaceInput(owner, subject, result);
        MoveInput(subject, rt);
        return result;
    }

    static void ReplaceInput(GreyPrimitive owner, GreyPrimitive previous, GreyPrimitive replacement)
    {
        Undo.RegisterCompleteObjectUndo(owner, k_Label);
        if (owner is GreyBooleanResult result)
        {
            if (result.Subject == previous)
            {
                result.Configure(replacement, result.Operator);
                SetBoolean(replacement, result.Operator, previous.BooleanCutMaterial);
            }
            else
            {
                result.Configure(result.Subject, replacement);
                if (result.Subject != null) SetBoolean(result.Subject, replacement, result.Subject.BooleanCutMaterial);
            }
        }
        else if (owner is GreyboxCompound compound)
        {
            int index = compound.Parts.IndexOf(previous);
            if (index >= 0) compound.Parts[index] = replacement;
        }
        SetOwner(replacement, owner);
        SetOwner(previous, null);
        EditorUtility.SetDirty(owner);
    }

    static void SetOwner(GreyPrimitive input, GreyPrimitive owner)
    {
        if (input == null || input.GeometryOwner == owner) return;
        Undo.RecordObject(input, k_Label);
        input.GeometryOwner = owner;
        EditorUtility.SetDirty(input);
    }

    static void SetBoolean(GreyPrimitive subject, GreyPrimitive op, Material material)
    {
        var settings = new SerializedObject(subject);
        settings.FindProperty("_booleanOperator").objectReferenceValue = op;
        settings.FindProperty("_booleanCutMaterial").objectReferenceValue = material;
        settings.ApplyModifiedProperties();
    }

    static void Teardown(GreyBooleanResult result)
    {
        var owner = GreyGeometry.Owner(result);
        var host = result.transform.parent;
        var subject = result.Subject;
        var op = result.Operator;
        if (subject != null)
        {
            MoveInput(subject, host);
            if (owner != null) ReplaceInput(owner, result, subject);
            else SetOwner(subject, null);
        }
        // Released cutters are independent, outside every consuming Boolean/compound.
        var root = owner != null ? GreyGeometry.Root(owner) : result;
        Release(op, root != null ? root.transform.parent : host);
        // Preserve unrelated children rather than deleting them with the wrapper.
        while (result.transform.childCount > 0)
            Undo.SetTransformParent(result.transform.GetChild(0), host, k_Label);
        Undo.DestroyObjectImmediate(result.gameObject);
        if (subject != null) RebuildTree(GreyGeometry.Root(subject));
    }

    static void MoveInput(GreyPrimitive input, Transform parent)
    {
        if (input.transform.parent == parent) return;
        var boxes = input.GetComponentsInChildren<Greybox>(true);
        var corners = new Vector3[boxes.Length][];
        for (int i = 0; i < boxes.Length; i++) corners[i] = boxes[i].GetWorldCorners();
        bool active = input.gameObject.activeInHierarchy;
        Undo.SetTransformParent(input.transform, parent, k_Label);
        if (!active && input.gameObject.activeInHierarchy)
        {
            Undo.RecordObject(input.gameObject, k_Label);
            input.gameObject.SetActive(false);
        }
        for (int i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            Undo.RegisterCompleteObjectUndo(box, k_Label);
            for (int corner = 0; corner < 8; corner++) box.Corners[corner] = box.transform.InverseTransformPoint(corners[i][corner]);
            box.RebuildMesh();
            EditorUtility.SetDirty(box);
            PrefabUtility.RecordPrefabInstancePropertyModifications(box);
        }
        PrefabUtility.RecordPrefabInstancePropertyModifications(input.transform);
    }

    static void Release(GreyPrimitive input, Transform host)
    {
        if (input == null) return;
        SetOwner(input, null);
        MoveInput(input, host);
        if (input is GreyboxCompound || input is GreyBooleanResult) RebuildTree(input);
        using var scope = HashSetPool<int>.Get(out var visited);
        RefreshVisibility(input, false, visited);
    }

    public static void ReBakeFrom(GreyPrimitive moved)
    {
        if (moved == null) return;
        var root = GreyGeometry.Root(moved);
        if (!(root is GreyboxCompound || root is GreyBooleanResult)) return;
        using var scope = HashSetPool<int>.Get(out var visited);
        var owner = GreyGeometry.Owner(moved);
        while (owner != null && visited.Add(owner.GetInstanceID()))
        {
            owner.RebuildMesh();
            if (owner is GreyBooleanResult result) ApplyResultMaterials(result);
            Register(owner);
            s_structure[owner.GetInstanceID()] = StructureSignature(owner);
            owner = GreyGeometry.Owner(owner);
        }
        visited.Clear();
        RefreshVisibility(root, false, visited);
    }

    internal static void RebuildTree(GreyPrimitive root)
    {
        using var visitedScope = HashSetPool<int>.Get(out var visited);
        using var activeScope = HashSetPool<int>.Get(out var active);
        if (!RebuildNode(root, visited, active)) return;
        visited.Clear();
        RefreshVisibility(root, false, visited);
    }

    static bool RebuildNode(GreyPrimitive node, HashSet<int> visited, HashSet<int> active)
    {
        if (node == null) return true; // Deleted inputs contribute an empty solid.
        int id = node.GetInstanceID();
        if (visited.Contains(id)) return true;
        if (!active.Add(id)) { Debug.LogError("[GreyBoolean] Geometry inputs contain a cycle.", node); return false; }
        if (node is GreyBooleanResult result)
        {
            if (!RebuildNode(result.Subject, visited, active) || !RebuildNode(result.Operator, visited, active)) return false;
            result.RebuildMesh();
            ApplyResultMaterials(result);
        }
        else if (node is GreyboxCompound compound)
        {
            foreach (var part in compound.Parts) if (!RebuildNode(part, visited, active)) return false;
            compound.RebuildMesh();
        }
        active.Remove(id);
        visited.Add(id);
        Register(node);
        if (s_nodes.ContainsKey(id)) s_structure[id] = StructureSignature(node);
        return true;
    }

    static void RefreshVisibility(GreyPrimitive node, bool consumed, HashSet<int> visited)
    {
        if (node == null || !visited.Add(node.GetInstanceID())) return;
        SetEnabled(node, !consumed && !(node is GreyboxCompound));
        if (node is GreyBooleanResult result)
        {
            RefreshVisibility(result.Subject, true, visited);
            RefreshVisibility(result.Operator, true, visited);
        }
        else if (node is GreyboxCompound compound)
            foreach (var part in compound.Parts) RefreshVisibility(part, consumed, visited);
    }

    static void SetEnabled(GreyPrimitive node, bool enabled)
    {
        var renderer = node.GetComponent<MeshRenderer>();
        if (renderer != null && renderer.enabled != enabled)
        {
            Undo.RecordObject(renderer, k_Label);
            renderer.enabled = enabled;
            EditorUtility.SetDirty(renderer);
        }
        var collider = node.GetComponent<MeshCollider>();
        if (collider != null && collider.enabled != enabled)
        {
            Undo.RecordObject(collider, k_Label);
            collider.enabled = enabled;
            EditorUtility.SetDirty(collider);
        }
    }

    internal static void Register(GreyPrimitive node)
    {
        if (node == null || EditorUtility.IsPersistent(node) || !node.gameObject.scene.IsValid()) return;
        if (!(node is GreyboxCompound || node is GreyBooleanResult)) return;
        int id = node.GetInstanceID();
        s_nodes[id] = node;
        if (!s_structure.ContainsKey(id)) s_structure[id] = StructureSignature(node);
    }

    static long StructureSignature(GreyPrimitive node)
    {
        long h = 17;
        if (node is GreyBooleanResult result)
        {
            h = h * 31 + (result.Subject != null ? result.Subject.GetInstanceID() : 0);
            h = h * 31 + (result.Operator != null ? result.Operator.GetInstanceID() : 0);
        }
        else if (node is GreyboxCompound compound)
            foreach (var part in compound.Parts) h = h * 31 + (part != null ? part.GetInstanceID() : 0);
        return h;
    }

    // Called by the existing scene/object-change hook, including deletion and Undo without a selection.
    internal static void RefreshStructure()
    {
        using var nodesScope = ListPool<GreyPrimitive>.Get(out var nodes);
        using var removedScope = ListPool<int>.Get(out var removed);
        using var rootsScope = HashSetPool<int>.Get(out var roots);
        foreach (var pair in s_nodes)
        {
            if (pair.Value == null) { removed.Add(pair.Key); continue; }
            if (s_structure[pair.Key] != StructureSignature(pair.Value)) nodes.Add(pair.Value);
        }
        foreach (int id in removed) { s_nodes.Remove(id); s_structure.Remove(id); }
        foreach (var node in nodes)
        {
            var root = GreyGeometry.Root(node);
            if (root != null && roots.Add(root.GetInstanceID())) RebuildTree(root);
        }
    }

    static void CopySettings(GreyPrimitive subject, GreyBooleanResult result, bool created)
    {
        var sr = subject.GetComponent<MeshRenderer>();
        var rr = result.GetComponent<MeshRenderer>();
        if (sr != null && rr != null)
        {
            // Slot 0 (Subject faces): seeded from the subject only when the result is first created;
            // afterwards the result owns it so an artist's override survives re-bakes.
            Material slot0 = created ? sr.sharedMaterial : rr.sharedMaterial;
            ApplyResultMaterials(result, slot0);
            Undo.RecordObject(rr, "Sync Boolean Result settings");
            rr.shadowCastingMode = sr.shadowCastingMode;
        }

        var go = result.gameObject;
        go.isStatic = subject.gameObject.isStatic;
        go.layer    = subject.gameObject.layer;
    }

    // Rebuild the result's material array to match its submesh split: slot 0 (its own Subject material)
    // followed by one slot per cut level that has a material, innermost first. Levels without a material
    // share slot 0, so an artist's assigned cut materials all survive further booleans.
    static void ApplyResultMaterials(GreyBooleanResult result, Material slot0)
    {
        var rr = result.GetComponent<MeshRenderer>();
        if (rr == null) return;
        using var scope = ListPool<Material>.Get(out var mats);
        mats.Add(slot0);
        foreach (var m in result.CollectCutMaterials())
            if (m != null) mats.Add(m);
        var current = rr.sharedMaterials;
        bool changed = current.Length != mats.Count;
        for (int i = 0; !changed && i < current.Length; i++) changed = current[i] != mats[i];
        if (!changed) return;
        Undo.RecordObject(rr, "Sync Boolean Result materials");
        rr.sharedMaterials = mats.ToArray();
    }

    static void ApplyResultMaterials(GreyBooleanResult result)
    {
        var rr = result.GetComponent<MeshRenderer>();
        if (rr != null) ApplyResultMaterials(result, rr.sharedMaterial);
    }

}
#endif
