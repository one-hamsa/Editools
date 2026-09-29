#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

static partial class GPEdit
{
    sealed class BoxSplit
    {
        public Greybox box;
        public Greybox upper;
        public int axis;
        public float fraction;
    }

    struct SplitSeam
    {
        public Greybox a, b;
        public int[] aCorners, bCorners;
    }

    static float GreyboxSplitFraction(Greybox box, int edge, Vector2 mouse)
    {
        var pair = GPEditShared.EdgeCornerIndices[edge];
        Vector3 a = box.transform.TransformPoint(box.Corners[pair[0]]);
        Vector3 b = box.transform.TransformPoint(box.Corners[pair[1]]);
        Vector3 point = GPEditShared.ClosestPointOnSegmentToScreenPos(a, b, mouse);
        return (b - a).sqrMagnitude < 1e-10f ? 0f
            : Mathf.Clamp01(Vector3.Dot(point - a, b - a) / (b - a).sqrMagnitude);
    }

    static bool SeamCrossesCut(int[] corners, int axis)
    {
        int mask = 1 << axis;
        for (int i = 1; i < 4; i++)
            if ((corners[i] & mask) != (corners[0] & mask)) return true;
        return false;
    }

    static void DrawGreyboxSplitPreview(Greybox box, int edge, Vector2 mouse)
    {
        float fraction = GreyboxSplitFraction(box, edge, mouse);
        if (fraction <= 0.001f || fraction >= 0.999f) return;
        using var splitsScope = ListPool<BoxSplit>.Get(out var splits);
        using var mapScope = DictionaryPool<int, BoxSplit>.Get(out var byId);
        using var seamsScope = ListPool<SplitSeam>.Get(out var seams);
        if (!PlanGreyboxSplit(box, edge / 4, fraction, splits, byId, seams)) return;
        Handles.color = GPEditShared.CreateLoop;
        foreach (var split in splits)
        {
            int mask = 1 << split.axis;
            var corners = GPEditShared.FaceCornerIndices[split.axis * 2 + 1];
            var points = split.box.Corners;
            var tr = split.box.transform;
            for (int i = 0; i < 4; i++)
            {
                int a = corners[i], b = corners[(i + 1) % 4];
                Handles.DrawLine(tr.TransformPoint(Vector3.Lerp(points[a], points[a | mask], split.fraction)),
                    tr.TransformPoint(Vector3.Lerp(points[b], points[b | mask], split.fraction)), 1f);
            }
        }
    }

    static bool MapSeamCut(int[] corners, int[] otherCorners, int axis, float fraction,
        out int otherAxis, out float otherFraction)
    {
        int mask = 1 << axis;
        otherAxis = -1;
        otherFraction = 0f;
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                if ((corners[i] ^ corners[j]) == mask)
                {
                    int otherMask = otherCorners[i] ^ otherCorners[j];
                    if (otherMask != 1 && otherMask != 2 && otherMask != 4) return false;
                    int mappedAxis = otherMask == 1 ? 0 : otherMask == 2 ? 1 : 2;
                    bool reversed = ((corners[i] & mask) == 0) != ((otherCorners[i] & otherMask) == 0);
                    float mappedFraction = reversed ? 1f - fraction : fraction;
                    if (otherAxis >= 0 && (otherAxis != mappedAxis || Mathf.Abs(otherFraction - mappedFraction) > 1e-5f)) return false;
                    otherAxis = mappedAxis;
                    otherFraction = mappedFraction;
                }
        return otherAxis >= 0;
    }

    static bool PlanGreyboxSplit(Greybox root, int axis, float fraction, List<BoxSplit> splits,
        Dictionary<int, BoxSplit> byId, List<SplitSeam> seams)
    {
        var first = new BoxSplit { box = root, axis = axis, fraction = fraction };
        splits.Add(first);
        byId.Add(root.GetInstanceID(), first);
        for (int i = 0; i < splits.Count; i++)
        {
            var split = splits[i];
            using var linksScope = ListPool<Greybox.Seam>.Get(out var links);
            split.box.GetSeams(links);
            foreach (var seam in links)
            {
                if (!SeamCrossesCut(seam.corners, split.axis)) continue;
                if (!MapSeamCut(seam.corners, seam.otherCorners, split.axis, split.fraction,
                    out int mappedAxis, out float mappedFraction)) return false;
                int id = seam.other.GetInstanceID();
                if (byId.TryGetValue(id, out var existing))
                {
                    if (existing.axis != mappedAxis || Mathf.Abs(existing.fraction - mappedFraction) > 1e-4f) return false;
                }
                else
                {
                    var next = new BoxSplit { box = seam.other, axis = mappedAxis, fraction = mappedFraction };
                    splits.Add(next);
                    byId.Add(id, next);
                }
            }
        }
        foreach (var split in splits)
        {
            using var linksScope = ListPool<Greybox.Seam>.Get(out var links);
            split.box.GetSeams(links);
            foreach (var seam in links)
            {
                if (byId.ContainsKey(seam.other.GetInstanceID()) && split.box.GetInstanceID() > seam.other.GetInstanceID()) continue;
                seams.Add(new SplitSeam { a = split.box, b = seam.other, aCorners = seam.corners, bCorners = seam.otherCorners });
            }
        }
        return true;
    }

    static void SplitGreybox(Greybox box, int edge, float fraction)
    {
        if (!GreyboxLinkHierarchy.CanLink(box, box)) return;
        if (fraction <= 0.001f || fraction >= 0.999f) return;
        using var splitsScope = ListPool<BoxSplit>.Get(out var splits);
        using var mapScope = DictionaryPool<int, BoxSplit>.Get(out var byId);
        using var seamsScope = ListPool<SplitSeam>.Get(out var seams);
        if (!PlanGreyboxSplit(box, edge / 4, fraction, splits, byId, seams))
        {
            SceneView.lastActiveSceneView.ShowNotification(new GUIContent("This linked loop requires conflicting cuts."));
            return;
        }

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        const string label = "Split Linked Greyboxes";
        Undo.SetCurrentGroupName(label);
        GreyPrimitive.BeginDeferredPersist();
        try
        {
            GreyBooleanOrchestrator.EnsureManagedInput(box);
            foreach (var split in splits) Undo.RegisterCompleteObjectUndo(split.box, label);
            foreach (var seam in seams)
            {
                Undo.RegisterCompleteObjectUndo(seam.a, label);
                Undo.RegisterCompleteObjectUndo(seam.b, label);
            }
            foreach (var split in splits) split.box.UnlinkAll();
            foreach (var split in splits) ApplyGreyboxSplit(split);
            foreach (var seam in seams)
            {
                byId.TryGetValue(seam.a.GetInstanceID(), out var a);
                byId.TryGetValue(seam.b.GetInstanceID(), out var b);
                if (a != null && b != null && SeamCrossesCut(seam.aCorners, a.axis))
                {
                    bool reverse = ((seam.aCorners[0] & (1 << a.axis)) == 0)
                        != ((seam.bCorners[0] & (1 << b.axis)) == 0);
                    a.box.AddSeamLink(reverse ? b.upper : b.box, seam.aCorners, seam.bCorners);
                    a.upper.AddSeamLink(reverse ? b.box : b.upper, seam.aCorners, seam.bCorners);
                }
                else
                {
                    Greybox left = a != null && (seam.aCorners[0] & (1 << a.axis)) != 0 ? a.upper : seam.a;
                    Greybox right = b != null && (seam.bCorners[0] & (1 << b.axis)) != 0 ? b.upper : seam.b;
                    left.AddSeamLink(right, seam.aCorners, seam.bCorners);
                }
                EditorUtility.SetDirty(seam.a);
                EditorUtility.SetDirty(seam.b);
            }
            GreyboxLinkHierarchy.Organize(box, label);
            using var selectionScope = ListPool<Object>.Get(out var selection);
            selection.AddRange(Selection.objects);
            foreach (var split in splits)
            {
                if (GreyPrimitiveSettings.AutoUpdatePivot)
                {
                    GreyPrimitiveEditor.RecenterGreyboxPivot(split.box, rebuild: false);
                    GreyPrimitiveEditor.RecenterGreyboxPivot(split.upper, rebuild: false);
                }
                split.box.RebuildMesh();
                split.upper.RebuildMesh();
                GreyBooleanOrchestrator.ReBakeFrom(split.box);
                EditorUtility.SetDirty(split.box);
                EditorUtility.SetDirty(split.upper);
                if (split.box.GetComponentInParent<GreyBooleanResult>() == null)
                {
                    if (!selection.Contains(split.box.gameObject)) selection.Add(split.box.gameObject);
                    selection.Add(split.upper.gameObject);
                }
            }
            Selection.objects = selection.ToArray();
        }
        finally
        {
            GreyPrimitive.EndDeferredPersist();
            Undo.CollapseUndoOperations(group);
        }
    }

    static void ApplyGreyboxSplit(BoxSplit split)
    {
        var source = split.box;
        var st = source.transform;
        var go = GreyPrimitiveSettings.PlacePrimitive<Greybox>(source.name + " Split", st.position, st.rotation,
            st.parent, meshCollider: source.GetComponent<MeshCollider>() != null, select: false);
        go.transform.localPosition = st.localPosition;
        go.transform.localRotation = st.localRotation;
        go.transform.localScale = st.localScale;
        go.layer = source.gameObject.layer;
        go.tag = source.gameObject.tag;
        GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(source.gameObject));
        split.upper = go.GetComponent<Greybox>();
        split.upper.SubdivisionMultiplier = source.SubdivisionMultiplier;
        split.upper.PlanarUv = source.PlanarUv;
        split.upper.FlipFaces = source.FlipFaces;
        var settings = new SerializedObject(split.upper);
        settings.FindProperty("_uvTileScale").floatValue = source.UvTileScale;
        settings.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.CopySerialized(source.GetComponent<MeshRenderer>(), go.GetComponent<MeshRenderer>());
        var collider = source.GetComponent<MeshCollider>();
        if (collider != null) EditorUtility.CopySerialized(collider, go.GetComponent<MeshCollider>());
        for (int face = 0; face < 6; face++) split.upper.ActiveFaces[face] = source.IsFaceVisible(face);
        int mask = 1 << split.axis;
        SplitGreyboxCorners(source.Corners, split.axis, split.fraction, source.Corners, split.upper.Corners);
        var lowerCorners = new int[4];
        var upperCorners = new int[4];
        int index = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            if ((corner & mask) != 0) continue;
            int high = corner | mask;
            lowerCorners[index] = high;
            upperCorners[index++] = corner;
        }
        source.ActiveFaces[split.axis * 2] = false;
        split.upper.ActiveFaces[split.axis * 2 + 1] = false;
        source.AddSeamLink(split.upper, lowerCorners, upperCorners);
        KeepSplitInBoolean(source, split.upper);
    }

    static void SplitGreyboxCorners(Vector3[] original, int axis, float fraction, Vector3[] lower, Vector3[] upper)
    {
        int mask = 1 << axis;
        for (int corner = 0; corner < 8; corner++)
        {
            if ((corner & mask) != 0) continue;
            int high = corner | mask;
            Vector3 lowPoint = original[corner], highPoint = original[high];
            Vector3 point = Vector3.Lerp(lowPoint, highPoint, fraction);
            lower[corner] = lowPoint;
            lower[high] = point;
            upper[corner] = point;
            upper[high] = highPoint;
        }
    }

    static void KeepSplitInBoolean(Greybox source, Greybox upper)
        => GreyBooleanOrchestrator.IncludeLinkedBox(source, upper);
}
#endif
