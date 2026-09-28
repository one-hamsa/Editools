using System.Collections.Generic;
using UnityEngine;

/// <summary>An explicit union of editable geometry inputs, independent of their welds.</summary>
public class GreyboxCompound : GreyPrimitive
{
    [SerializeField, HideInInspector]
    [Tooltip("The primitives, linked boxes, or Boolean results making up this geometry input.")]
    List<GreyPrimitive> _parts = new List<GreyPrimitive>();

    public List<GreyPrimitive> Parts => _parts;
    public override bool UsesColliderMesh => true;

    protected override void ResetToDefaults() => _parts.Clear();

    protected override void GenerateMesh(Mesh mesh)
    {
        var pieces = new List<CombineInstance>();
        foreach (var part in _parts)
        {
            // Deleted pieces no longer contribute to the input.
            if (part == null) continue;
            var source = SubdivisionSuppressed && part.UsesColliderMesh
                ? part.ColliderMesh : part.GetComponent<MeshFilter>().sharedMesh;
            if (source == null) continue;
            pieces.Add(new CombineInstance { mesh = source,
                transform = transform.worldToLocalMatrix * part.transform.localToWorldMatrix });
        }
        mesh.Clear();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.CombineMeshes(pieces.ToArray(), true, true);
    }
}

/// <summary>Geometry ownership follows explicit input references, never an arbitrary ancestor.</summary>
public static class GreyGeometry
{
    public static bool Owns(GreyPrimitive owner, GreyPrimitive input)
        => owner is GreyBooleanResult result ? result.Subject == input || result.Operator == input
            : owner is GreyboxCompound compound && compound.Parts.Contains(input);

    public static GreyPrimitive Owner(GreyPrimitive input)
    {
        if (input == null) return null; // Deleted inputs have no owner.
        if (input.GeometryOwner != null && Owns(input.GeometryOwner, input)) return input.GeometryOwner;
        // Existing scenes predate serialized ownership. Resolve only verified input relationships.
        for (var parent = input.transform.parent; parent != null; parent = parent.parent)
            if (parent.TryGetComponent<GreyPrimitive>(out var candidate) && Owns(candidate, input)) return candidate;
        return null;
    }

    public static GreyPrimitive Root(GreyPrimitive input)
    {
        using var scope = UnityEngine.Pool.HashSetPool<int>.Get(out var visited);
        while (input != null && visited.Add(input.GetInstanceID()))
        {
            var owner = Owner(input);
            if (owner == null) return input;
            input = owner;
        }
        Debug.LogError("[GreyBoolean] Geometry ownership contains a cycle.");
        return null;
    }

    public static bool Contains(GreyPrimitive root, GreyPrimitive input)
    {
        using var visitedScope = UnityEngine.Pool.HashSetPool<int>.Get(out var visited);
        using var scope = UnityEngine.Pool.ListPool<GreyPrimitive>.Get(out var pending);
        pending.Add(root);
        for (int i = 0; i < pending.Count; i++)
        {
            var node = pending[i];
            if (node == null || !visited.Add(node.GetInstanceID())) continue;
            if (node == input) return true;
            if (node is GreyBooleanResult result) { pending.Add(result.Subject); pending.Add(result.Operator); }
            else if (node is GreyboxCompound compound) pending.AddRange(compound.Parts);
        }
        return false;
    }
}
