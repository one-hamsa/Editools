using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Keeps linked greybox seams welded under ANY edit. A child created by QuickTransform's RMB
/// extrude binds its seam face 1:1 to the source face it grew from (see <see cref="Greybox.SetSeamLink"/>),
/// purely by reference — the boxes are independent siblings, so each one moves/rotates/scales
/// exactly like a standalone object. This solver maintains the bond in WORLD space.
///
/// Model: when a box is manipulated it is authoritative and rigid; every box welded to one of its
/// (now moved) corners conforms to follow — its link parent and any boxes welded to it, recursing
/// along the chain. Conforming edits only CORNERS, never a transform, so it can never re-trigger a
/// transform-driven pass: the manipulated box stays rigid (no double-deform) and nothing compounds.
/// Corner edits (QuickTransform edge-drag / reset) call <see cref="SyncCorner"/> directly.
///
/// Trigger is push-based: it runs off scene-view interaction (duringSceneGui) and only inspects the
/// current selection, comparing each linked box's world matrix to the last seen value — no
/// [ExecuteAlways], no idle per-frame work — and it stays live mid-drag for every tool.
/// </summary>
[InitializeOnLoad]
static class GreyboxSeamSolver
{
    static GreyboxSeamSolver()
    {
        SceneView.duringSceneGui += OnSceneGui;
        Selection.selectionChanged += OnSelectionChanged;
        OnSelectionChanged();
    }

    // ─── Transform-driven conform (push-based, tool-agnostic) ───────

    static readonly Dictionary<Greybox, Matrix4x4> s_lastMatrix = new Dictionary<Greybox, Matrix4x4>();
    // World scale of each selected grey primitive captured when an interaction began, to detect a
    // scale-apply on release and refresh size-derived subdivision then.
    static readonly Dictionary<GreyPrimitive, Vector3> s_scaleAtGrab = new Dictionary<GreyPrimitive, Vector3>();
    static int s_lastHot;

    static bool s_expandingSelection;
    static bool s_selectionExpansionScheduled;

    static void OnSelectionChanged()
    {
        s_lastMatrix.Clear();
        s_scaleAtGrab.Clear();
        RequestLinkedSelectionExpansion();
    }

    internal static void RequestLinkedSelectionExpansion()
    {
        if (!GPEdit.Enabled || s_expandingSelection || s_selectionExpansionScheduled) return;
        s_selectionExpansionScheduled = true;
        EditorApplication.delayCall += ExpandLinkedSelection;
    }

    static void ExpandLinkedSelection()
    {
        s_selectionExpansionScheduled = false;
        if (!GPEdit.Enabled) return;
        // Apply after the originating selection operation, using its final selection.
        using var objectsScope = ListPool<Object>.Get(out var objects);
        using var boxesScope = ListPool<Greybox>.Get(out var boxes);
        using var visitedScope = HashSetPool<int>.Get(out var visited);
        objects.AddRange(Selection.objects);
        foreach (var go in Selection.gameObjects)
            if (!EditorUtility.IsPersistent(go) && go.TryGetComponent<Greybox>(out var box)
                && visited.Add(box.GetInstanceID())) boxes.Add(box);
        for (int i = 0; i < boxes.Count; i++)
        {
            using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
            boxes[i].GetSeams(seams);
            foreach (var seam in seams)
                if (!EditorUtility.IsPersistent(seam.other) && visited.Add(seam.other.GetInstanceID()))
                {
                    boxes.Add(seam.other);
                    if (!objects.Contains(seam.other.gameObject)) objects.Add(seam.other.gameObject);
                }
        }
        if (objects.Count != Selection.objects.Length)
        {
            var active = Selection.activeObject;
            int activeIndex = objects.IndexOf(active);
            if (activeIndex > 0) (objects[0], objects[activeIndex]) = (objects[activeIndex], objects[0]);
            s_expandingSelection = true;
            try { Selection.objects = objects.ToArray(); }
            finally { s_expandingSelection = false; }
            SceneView.RepaintAll();
        }
    }

    static void OnSceneGui(SceneView sv)
    {
        // hotControl flips 0 -> non-zero on a tool grab and back on release. On grab: open a fresh
        // undo batch (so a drag's conform edits collapse into the move's own undo step) and snapshot
        // scales. On release: rebuild any grey primitive whose scale changed, refreshing size-derived
        // subdivision on scale-apply — push-based, so no per-frame rebuilds mid-drag.
        int hot = GUIUtility.hotControl;
        if (s_lastHot == 0 && hot != 0)
        {
            s_undoRegistered.Clear();
            SnapshotScales();
        }
        else if (s_lastHot != 0 && hot == 0)
        {
            RebuildScaledSelection();
        }
        s_lastHot = hot;

        Event e = Event.current;
        if (e.type != EventType.Repaint && e.type != EventType.MouseDrag
            && e.type != EventType.MouseUp && e.type != EventType.MouseMove)
            return;

        Transform[] selection = Selection.transforms;
        if (selection == null || selection.Length == 0)
            return;

        bool any = false;
        foreach (Transform t in selection)
        {
            if (t == null) continue;
            Greybox gb = t.GetComponent<Greybox>();
            if (gb == null || !gb.HasSeam) continue;

            Matrix4x4 m = t.localToWorldMatrix;
            bool known = s_lastMatrix.TryGetValue(gb, out Matrix4x4 prev);
            if (known && Same(prev, m)) continue;   // not moved since last seen
            s_lastMatrix[gb] = m;
            if (!known) continue;                    // first sighting: record baseline, don't conform

            // Manipulated box is authoritative and rigid; every welded partner follows in world
            // space. A non-seam corner simply finds no partner, so sweeping all eight is harmless.
            s_undoName = "Move Linked Greybox";
            for (int i = 0; i < 8; i++)
                SyncCorner(gb, i);
            any = true;
        }

        if (any) sv.Repaint();
    }

    static bool Same(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 16; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // ─── Scale-apply rebuild ───────────────────────────────────────
    //
    // A grey primitive's subdivision density is derived from its world-space size, but that's only
    // recomputed inside RebuildMesh — and a plain Move/Rotate/Scale (native gizmo or QuickTransform)
    // never calls one. So we snapshot scales when an interaction starts and, when it ends, rebuild
    // any grey primitive whose scale actually changed. Move/rotate leave size unchanged, so they
    // cost nothing; only a real scale triggers the single rebuild.

    static void SnapshotScales()
    {
        s_scaleAtGrab.Clear();
        var selection = Selection.transforms;
        if (selection == null) return;
        foreach (Transform t in selection)
        {
            if (t == null) continue;
            var gp = t.GetComponent<GreyPrimitive>();
            if (gp != null) s_scaleAtGrab[gp] = t.lossyScale;
        }
    }

    static void RebuildScaledSelection()
    {
        foreach (var kv in s_scaleAtGrab)
        {
            GreyPrimitive gp = kv.Key;
            if (gp == null) continue;
            if (gp.transform.lossyScale != kv.Value)   // scale actually changed during the drag
            {
                gp.RebuildMesh();
                EditorUtility.SetDirty(gp);
            }
        }
        s_scaleAtGrab.Clear();
    }

    // ─── Shared seam machinery (corner edits call in from QuickTransform) ───

    // Recursion guard: (box, cornerIndex) pairs already snapped this pass. A box corner is shared
    // by three faces, so one moved corner can belong to several seams (a child seam AND this box's
    // own seam with its parent); propagation follows all of them, never bouncing back to one done.
    static readonly HashSet<(Greybox, int)> s_visited = new HashSet<(Greybox, int)>();

    // Undo bookkeeping for one seam operation. A partner box is registered the first time it is
    // mutated and never again, so the whole snapped seam reverts as one step. The set is cleared at
    // an interaction's start (hotControl edge above) or by BeginUndoScope for discrete edits.
    static readonly HashSet<Greybox> s_undoRegistered = new HashSet<Greybox>();
    static string s_undoName;

    /// <summary>Open a fresh undo batch + label for a discrete seam edit (QuickTransform corner edits).</summary>
    public static void BeginUndoScope(string name)
    {
        s_undoRegistered.Clear();
        s_undoName = name;
    }

    static void RegisterUndo(Greybox box)
    {
        if (s_undoRegistered.Add(box))
            Undo.RegisterCompleteObjectUndo(box, s_undoName);
    }

    /// <summary>
    /// Rebuild the meshes of every greybox seam-welded to <paramref name="origin"/> — its link parent
    /// and seam children, recursively. Used on undo/redo: the partners' corners are already restored by
    /// the undo, but their in-memory meshes are stale. Edits no corners (so it never re-triggers the
    /// solver) — a pure mesh refresh of the welded neighbours; <paramref name="origin"/> itself is left
    /// to the caller.
    /// </summary>
    public static void RebuildSeamPartners(Greybox origin)
    {
        if (origin == null || !origin.HasSeam) return;

        var visited = new HashSet<Greybox> { origin };
        var stack = new Stack<Greybox>();
        stack.Push(origin);
        while (stack.Count > 0)
        {
            Greybox box = stack.Pop();

            using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
            box.GetSeams(seams);
            foreach (var seam in seams)
                if (visited.Add(seam.other))
                {
                    seam.other.RebuildMesh();
                    EditorUtility.SetDirty(seam.other);
                    stack.Push(seam.other);
                }
        }
    }

    /// <summary>
    /// Snap every box seam welded to a just-moved corner of <paramref name="box"/>, keeping linked
    /// greyboxes coincident. Bidirectional — drives the paired corner on the link parent and on any
    /// boxes welded to this box — and recurses, since one corner can feed several seams.
    /// </summary>
    public static void SyncCorner(Greybox box, int movedCorner)
    {
        s_visited.Clear();
        s_visited.Add((box, movedCorner));
        Propagate(box, movedCorner);
    }

    static void Propagate(Greybox box, int movedCorner)
    {
        Vector3 world = box.transform.TransformPoint(box.Corners[movedCorner]);

        using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
        box.GetSeams(seams);
        foreach (var seam in seams)
            for (int k = 0; k < 4; k++)
                if (seam.corners[k] == movedCorner && s_visited.Add((seam.other, seam.otherCorners[k])))
                {
                    var partner = seam.other;
                    int corner = seam.otherCorners[k];
                    RegisterUndo(partner);
                    partner.Corners[corner] = partner.transform.InverseTransformPoint(world);
                    partner.RebuildMesh();
                    EditorUtility.SetDirty(partner);
                    Propagate(partner, corner);
                }
    }

    // ─── Manual linking (inspector "Link" field) ───────────────────
    //
    // Weld two pre-existing greyboxes that weren't made with linked-extrude. Mirrors the seam an RMB
    // extrude produces: find the most likely facing face pair, snap CHILD's face onto PARENT's
    // (child conforms, parent is the anchor), hide both seam faces, and record the weld so the solver
    // maintains it thereafter.

    // Face index -> its 4 corner indices (winding), matching the box convention used everywhere.
    static readonly int[][] FaceCornerIndices =
    {
        new[] { 1, 3, 7, 5 }, // 0 +X
        new[] { 0, 4, 6, 2 }, // 1 -X
        new[] { 2, 6, 7, 3 }, // 2 +Y
        new[] { 0, 1, 5, 4 }, // 3 -Y
        new[] { 4, 5, 7, 6 }, // 4 +Z
        new[] { 0, 2, 3, 1 }, // 5 -Z
    };

    static readonly int[][] s_perms4 = BuildPerms4();

    /// <summary>
    /// Weld <paramref name="child"/> to <paramref name="parent"/>: auto-pick the facing face pair,
    /// snap the child's face onto the parent's (parent stays put), hide both faces, and record the
    /// seam. From then on <see cref="SyncCorner"/> keeps it welded and the Unlink button severs it.
    /// </summary>
    public static void LinkBoxes(Greybox child, Greybox parent)
    {
        if (child == null || parent == null || child == parent) return;
        if (!GreyboxLinkHierarchy.CanLink(child, parent)) return;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Link Greyboxes");
        using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
        child.GetSeams(seams);
        foreach (var seam in seams)
            if (seam.other == parent)
            {
                GreyboxLinkHierarchy.Organize(parent, "Link Greyboxes");
                Undo.CollapseUndoOperations(undoGroup);
                return;
            }
        FindBestFacePair(child, parent, out int fChild, out int fParent);
        int[] cFace   = FaceCornerIndices[fChild];
        int[] pPaired = MatchCorners(child, cFace, parent, FaceCornerIndices[fParent]);

        Undo.RegisterCompleteObjectUndo(child, "Link Greybox Seam");
        Undo.RegisterCompleteObjectUndo(parent, "Link Greybox Seam");


        // Snap: the child's seam corners move onto the matched parent corners (parent is the anchor).
        for (int k = 0; k < 4; k++)
        {
            Vector3 world = parent.transform.TransformPoint(parent.Corners[pPaired[k]]);
            child.Corners[cFace[k]] = child.transform.InverseTransformPoint(world);
        }

        child.ActiveFaces[fChild]   = false;     // hide the now-coincident seam on both boxes
        parent.ActiveFaces[fParent] = false;

        child.AddSeamLink(parent, (int[])cFace.Clone(), pPaired);
        child.RebuildMesh();
        parent.RebuildMesh();
        EditorUtility.SetDirty(child);
        EditorUtility.SetDirty(parent);

        // The child's face just moved; re-weld anything already bound to those corners (e.g. a chain
        // whose top we just linked) so the whole assembly stays coincident.
        BeginUndoScope("Link Greybox Seam");
        for (int k = 0; k < 4; k++)
            SyncCorner(child, cFace[k]);
        GreyboxLinkHierarchy.Organize(parent, "Link Greyboxes");
        Undo.CollapseUndoOperations(undoGroup);
    }

    /// <summary>Pick the face pair most likely meant to join: closest centers, most anti-parallel normals.</summary>
    static void FindBestFacePair(Greybox a, Greybox b, out int faceA, out int faceB)
    {
        faceA = 0; faceB = 0;
        float best = float.MaxValue;
        for (int fa = 0; fa < 6; fa++)
        {
            FaceWorld(a, fa, out Vector3 cA, out Vector3 nA);
            for (int fb = 0; fb < 6; fb++)
            {
                FaceWorld(b, fb, out Vector3 cB, out Vector3 nB);
                float facing = -Vector3.Dot(nA, nB);                 // 1 = faces point at each other
                float cost   = Vector3.Distance(cA, cB) / Mathf.Max(facing, 0.05f);
                if (cost < best) { best = cost; faceA = fa; faceB = fb; }
            }
        }
    }

    /// <summary>World-space center and outward normal of a greybox face (robust for deformed boxes).</summary>
    static void FaceWorld(Greybox gb, int face, out Vector3 center, out Vector3 normal)
    {
        int[] ci = FaceCornerIndices[face];
        Vector3 c0 = gb.transform.TransformPoint(gb.Corners[ci[0]]);
        Vector3 c1 = gb.transform.TransformPoint(gb.Corners[ci[1]]);
        Vector3 c2 = gb.transform.TransformPoint(gb.Corners[ci[2]]);
        Vector3 c3 = gb.transform.TransformPoint(gb.Corners[ci[3]]);
        center = (c0 + c1 + c2 + c3) * 0.25f;
        normal = Vector3.Cross(c1 - c0, c3 - c0).normalized;

        // Sign-correct to point outward, using the face's axis convention.
        int   axis = face / 2;
        float sign = (face % 2 == 0) ? 1f : -1f;
        Vector3 localOut = axis == 0 ? new Vector3(sign, 0, 0)
                         : axis == 1 ? new Vector3(0, sign, 0)
                         :             new Vector3(0, 0, sign);
        if (Vector3.Dot(normal, gb.transform.TransformDirection(localOut)) < 0f) normal = -normal;
    }

    /// <summary>
    /// For each child-face corner slot, the parent-face corner index it should weld to — the
    /// bijection minimising total world distance over the 4!=24 pairings.
    /// </summary>
    static int[] MatchCorners(Greybox child, int[] cFace, Greybox parent, int[] pFace)
    {
        Vector3[] cw = new Vector3[4];
        Vector3[] pw = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            cw[i] = child.transform.TransformPoint(child.Corners[cFace[i]]);
            pw[i] = parent.transform.TransformPoint(parent.Corners[pFace[i]]);
        }

        int[] best = s_perms4[0];
        float bestCost = float.MaxValue;
        foreach (int[] perm in s_perms4)
        {
            float cost = 0f;
            for (int i = 0; i < 4; i++) cost += (cw[i] - pw[perm[i]]).sqrMagnitude;
            if (cost < bestCost) { bestCost = cost; best = perm; }
        }

        int[] result = new int[4];
        for (int i = 0; i < 4; i++) result[i] = pFace[best[i]];
        return result;
    }

    static int[][] BuildPerms4()
    {
        var list = new List<int[]>(24);
        Permute(new[] { 0, 1, 2, 3 }, 0, list);
        return list.ToArray();
    }

    static void Permute(int[] a, int k, List<int[]> outList)
    {
        if (k == a.Length) { outList.Add((int[])a.Clone()); return; }
        for (int i = k; i < a.Length; i++)
        {
            (a[k], a[i]) = (a[i], a[k]);
            Permute(a, k + 1, outList);
            (a[k], a[i]) = (a[i], a[k]);
        }
    }
}
