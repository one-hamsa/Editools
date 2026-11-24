#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>
/// Greybox editing in the coordinate frame shown by the hovered control. A drag is led by the
/// pressed face or edge; when that element is part of the sub-selection, every selected element
/// of its kind takes part, on any number of boxes.
/// </summary>
static partial class GPEdit
{
    enum GbDrag { None, Edge, FaceNormal, Extrude }

    /// <summary>One box taking part in a face or edge move.</summary>
    sealed class GbDragBox
    {
        public Greybox box;
        public Vector3[] start;                           // corners at drag start
        public readonly Vector3[] offsets = new Vector3[8]; // this event's offset per corner, in box local space
        public int direct;    // corner bits a dragged element moves this event
        public int mirrored;  // corner bits only symmetry moves this event
        public int displaced; // corner bits the previous event left away from their start position
    }

    /// <summary>One face or edge taking part in a move. Element 0 of the list is the lead: the pressed one.</summary>
    struct GbDragElement
    {
        public GbDragBox box;
        public int index;                   // face or edge index
        public CoordinateFollower follower; // movement of a non-lead element
        public Vector3 local;               // this event's movement in box local space
        public int mirrorAxes;              // edge symmetry: local axis bits of the partner edge; 0 = not picked
    }

    /// <summary>One face being extruded. Element 0 of the list is the lead.</summary>
    sealed class GbExtrusion
    {
        public Greybox source;
        public int face;
        public Vector3 normal;
        public Greybox created;
        public Vector3[] baseCorners;
        public Greybox mirror; // symmetric extrusion from the opposite face; unset while symmetry is off
        public Vector3 mirrorNormal;
        public Vector3[] mirrorBase;
    }

    static GbDrag    s_gbDrag;
    static Greybox   s_gbTarget; // lead box
    static int       s_gbButton;
    static int       s_gbControlId;
    static int       s_gbUndoGroup;

    static int s_gbEdge; // lead edge
    static int s_gbFace; // lead face
    static CoordinateDrag s_gbCoordinates;

    static readonly List<GbDragBox> s_gbDragBoxes = new List<GbDragBox>(8);
    static readonly List<GbDragElement> s_gbDragElements = new List<GbDragElement>(16);

    static readonly List<GbExtrusion> s_gbExtrusions = new List<GbExtrusion>(8);
    static bool s_gbExtrudeLinked;
    static bool s_gbMirrorExtruded; // the symmetric extrusions exist (Alt held during an extrude)
    static int  s_gbMirrorExtrudeUndoGroup;

    // New-box corner that sits on source face corner k of an extrusion.
    static readonly int[] k_ExtrusionBaseCorners = { 0, 4, 5, 1 };

    static readonly Vector3[] s_gbWc = new Vector3[8];
    static int s_gbHoverTint = -1;

    static partial void OnGreyboxSceneGUI(SceneView sv, Event e, Greybox gb)
    {
        if (s_gbDrag != GbDrag.None) { HandleGreyboxDrag(e, sv); return; }

        gb.GetWorldCorners(s_gbWc);
        int hoverFace = HitGreyboxFaceHandle(gb, e.mousePosition);
        int hoverEdge = hoverFace >= 0 ? -1 : HitGreyboxOutlineEdge(gb, e.mousePosition);

        if (e.type == EventType.MouseDown && (!e.alt || (e.button == 1 && hoverFace >= 0))
            && (e.button == 0 || e.button == 1 || e.button == 2))
            BeginGreybox(sv, e, gb, hoverFace, hoverEdge);

        if (e.type == EventType.Repaint)
            DrawGreybox(gb, hoverFace, hoverEdge, s_gbHoverTint);

        if (e.type == EventType.MouseMove || e.type == EventType.KeyDown || e.type == EventType.KeyUp)
            sv.Repaint();
    }

    // ─── Hover ──────────────────────────────────────────────────

    static int HitGreyboxFaceHandle(Greybox gb, Vector2 mousePos)
    {
        int best = -1;
        float bestDist = GPEditShared.HandlePx;
        int seamFaces = GreyboxSeamFaceMask(gb);
        for (int face = 0; face < 6; face++)
        {
            if ((seamFaces & (1 << face)) != 0) continue;
            Vector2 s = HandleUtility.WorldToGUIPoint(GreyboxFaceCenter(face));
            float dist = Vector2.Distance(mousePos, s);
            if (dist < bestDist) { bestDist = dist; best = face; }
        }
        return best;
    }

    static int GreyboxSeamFaceMask(Greybox gb)
    {
        using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
        gb.GetSeams(seams);
        int mask = 0;
        foreach (var seam in seams)
        {
            int seamCorners = 0;
            foreach (int corner in seam.corners) seamCorners |= 1 << corner;
            for (int face = 0; face < 6; face++)
            {
                if (gb.IsFaceVisible(face)) continue;
                int faceCorners = 0;
                foreach (int corner in GPEditShared.FaceCornerIndices[face]) faceCorners |= 1 << corner;
                if (seamCorners == faceCorners) mask |= 1 << face;
            }
        }
        return mask;
    }

    static int HitGreyboxOutlineEdge(Greybox gb, Vector2 mousePos)
    {
        int best = -1;
        float bestDist = GPEditShared.HoverPx;
        // Every edge is hoverable, including edges between hidden faces (own or welded), so
        // internal welds stay editable; a drag propagates to the welded partners.
        for (int ei = 0; ei < 12; ei++)
        {
            int[] ec = GPEditShared.EdgeCornerIndices[ei];
            float dist = GPEditShared.DistToSegment(s_gbWc[ec[0]], s_gbWc[ec[1]], mousePos);
            if (dist < bestDist) { bestDist = dist; best = ei; }
        }
        return best;
    }

    /// <summary>True when at least one of the edge's two faces is visible (drawn full-strength; edges of only-hidden faces are dimmed).</summary>
    static bool IsOutlineEdge(Greybox gb, int edge)
    {
        int[] adj = GPEditShared.EdgeFaceAdjacency[edge];
        return gb.IsFaceVisible(adj[0]) || gb.IsFaceVisible(adj[1]);
    }

    static Vector3 GreyboxFaceCenter(int face)
    {
        int[] ci = GPEditShared.FaceCornerIndices[face];
        return (s_gbWc[ci[0]] + s_gbWc[ci[1]] + s_gbWc[ci[2]] + s_gbWc[ci[3]]) * 0.25f;
    }

    /// <summary>Outward normal of the current face surface.</summary>
    static Vector3 GreyboxFaceNormal(Greybox gb, int face)
    {
        return GreyboxFaceCoordinates(gb, face, true).a;
    }

    /// <summary>Face index whose four corners are exactly <paramref name="corners"/>, or -1.</summary>
    static int GreyboxFaceOfCorners(int[] corners)
    {
        int mask = 0;
        foreach (int corner in corners) mask |= 1 << corner;
        for (int face = 0; face < 6; face++)
        {
            int faceMask = 0;
            foreach (int corner in GPEditShared.FaceCornerIndices[face]) faceMask |= 1 << corner;
            if (faceMask == mask) return face;
        }
        return -1;
    }

    // ─── Begin ──────────────────────────────────────────────────

    static void BeginGreybox(SceneView sv, Event e, Greybox gb, int hoverFace, int hoverEdge)
    {
        if (hoverFace >= 0)
        {
            if (e.button == 2) { BeginPrimitiveClick(sv, e, gb, ClickAction.ToggleFace, hoverFace); return; }
            // LMB: a click selects the face, a drag moves it (GPEditSubSelection.cs).
            if (e.button == 0) { BeginSubPress(e, gb, SubKind.BoxFace, hoverFace); return; }
            if (e.button != 1) return;
            StartGreyboxExtrude(gb, hoverFace, e.mousePosition, linked: !e.alt);
        }
        else if (hoverEdge >= 0)
        {
            if (e.button == 1) BeginPrimitiveClick(sv, e, gb, ClickAction.SplitEdge, hoverEdge, GreyboxSplitFraction(gb, hoverEdge, e.mousePosition));
            else if (e.button == 0) BeginSubPress(e, gb, SubKind.BoxEdge, hoverEdge);
            return;
        }
        else return;

        if (s_gbDrag == GbDrag.None) return;
        s_gbTarget = gb;
        s_gbButton = e.button;
        s_gbControlId = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(s_gbControlId);
        GUIUtility.hotControl = s_gbControlId;
        e.Use();
    }

    /// <summary>Hides or shows the clicked face; when it is selected, every selected face gets the clicked face's new state.</summary>
    static void ToggleGreyboxFace(Greybox gb, int face)
    {
        const string label = "Toggle Greybox Face";
        bool visible = !gb.IsFaceVisible(face);
        if (!IsSubSelected(gb, SubKind.BoxFace, face))
        {
            SetGreyboxFaceVisible(gb, face, visible, label);
            return;
        }
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        foreach (var element in s_subSelection)
        {
            var box = (Greybox)element.target;
            // Legitimate filter: a face welded after it was selected has no surface to show.
            if ((GreyboxSeamFaceMask(box) & (1 << element.index)) != 0) continue;
            SetGreyboxFaceVisible(box, element.index, visible, label);
        }
        Undo.CollapseUndoOperations(group);
    }

    static void SetGreyboxFaceVisible(Greybox gb, int face, bool visible, string label)
    {
        Undo.RegisterCompleteObjectUndo(gb, label);
        gb.SetFaceVisible(face, visible);
        gb.RebuildMesh();
        EditorUtility.SetDirty(gb);
    }

    static Vector3 GreyboxAxisWorldDir(Transform t, int axis)
        => t.TransformVector(axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward).normalized;

    static EditCoordinates GreyboxElementCoordinates(Greybox gb, bool face, int index, bool local)
        => face ? GreyboxFaceCoordinates(gb, index, local) : GreyboxEdgeCoordinates(gb, index, local);

    /// <summary>
    /// Starts a face or edge move led by the pressed element. When that element is selected,
    /// every selected element of the same kind moves with it. Expects s_gbWc to hold the lead
    /// box's world corners; the caller binds the control.
    /// </summary>
    static void StartGreyboxMove(Greybox gb, GbDrag drag, int index, Vector2 mousePos)
    {
        bool face = drag == GbDrag.FaceNormal;
        string name = face ? "Greybox Move Face" : "Greybox Edge Move";
        BeginGreyboxUndo(gb, name);
        s_gbDrag = drag;
        if (face) s_gbFace = index;
        else s_gbEdge = index;

        Vector3 anchor;
        if (face) anchor = GreyboxFaceCenter(index);
        else
        {
            int[] corners = GPEditShared.EdgeCornerIndices[index];
            anchor = GPEditShared.ClosestPointOnSegmentToScreenPos(s_gbWc[corners[0]], s_gbWc[corners[1]], mousePos);
        }
        s_gbCoordinates.Begin(anchor, mousePos,
            GreyboxElementCoordinates(gb, face, index, false), GreyboxElementCoordinates(gb, face, index, true));

        s_gbDragBoxes.Clear();
        s_gbDragElements.Clear();
        AddGreyboxDragElement(gb, face, index, true, name);
        SubKind kind = face ? SubKind.BoxFace : SubKind.BoxEdge;
        if (IsSubSelected(gb, kind, index))
            foreach (var element in s_subSelection)
                if (!element.Matches(gb, kind, index))
                    AddGreyboxDragElement((Greybox)element.target, face, element.index, false, name);
        gb.GetWorldCorners(s_gbWc);
    }

    static void AddGreyboxDragElement(Greybox gb, bool face, int index, bool lead, string undoName)
    {
        GbDragBox dragBox = null;
        foreach (var known in s_gbDragBoxes)
            if (ReferenceEquals(known.box, gb)) { dragBox = known; break; }
        if (dragBox == null)
        {
            // The lead box is registered by BeginGreyboxUndo.
            if (!lead) Undo.RegisterCompleteObjectUndo(gb, undoName);
            dragBox = new GbDragBox { box = gb, start = (Vector3[])gb.Corners.Clone() };
            s_gbDragBoxes.Add(dragBox);
        }

        var element = new GbDragElement { box = dragBox, index = index };
        if (!lead)
        {
            // Local coordinates are read from the box's world corners.
            gb.GetWorldCorners(s_gbWc);
            element.follower.Begin(GreyboxElementCoordinates(gb, face, index, true), ref s_gbCoordinates);
        }
        s_gbDragElements.Add(element);
    }

    static void BeginGreyboxUndo(Greybox gb, string name)
    {
        Undo.IncrementCurrentGroup();
        s_gbUndoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(gb, name);
        GreyboxSeamSolver.BeginUndoScope(name);
        Undo.SetCurrentGroupName(name);
        GreyPrimitive.BeginDeferredPersist();
    }

    // ─── Drag ───────────────────────────────────────────────────

    static void HandleGreyboxDrag(Event e, SceneView sv, int operandIndex = -1)
    {
        if (s_gbTarget == null) { ResetGreyboxDrag(); return; }

        // Events outside the window arrive as Ignore with the real type in rawType — honoring it
        // keeps the drag updating off-window and lets the release register wherever it happens.
        EventType type = e.type == EventType.Ignore ? e.rawType : e.type;

        if ((type == EventType.MouseDrag && e.button == s_gbButton)
            || type == EventType.KeyDown || type == EventType.KeyUp)
        {
            ApplyGreyboxDrag(e.mousePosition);
            sv.Repaint();
            e.Use();
            return;
        }

        if ((type == EventType.MouseUp && e.button == s_gbButton) || !Enabled)
        {
            FinishGreyboxDrag();
            e.Use();
            return;
        }

        if (e.type == EventType.Repaint)
        {
            s_gbTarget.GetWorldCorners(s_gbWc);
            int face = (s_gbDrag == GbDrag.FaceNormal || s_gbDrag == GbDrag.Extrude) ? s_gbFace : -1;
            int edge = s_gbDrag == GbDrag.Edge ? s_gbEdge : -1;
            DrawGreybox(s_gbTarget, face, edge, operandIndex);
            DrawGreyboxSymmetryHighlight();
        }
    }

    static void ApplyGreyboxDrag(Vector2 mousePos)
    {
        switch (s_gbDrag)
        {
            case GbDrag.Edge:
            case GbDrag.FaceNormal: ApplyGreyboxMove(mousePos);    break;
            case GbDrag.Extrude:    ApplyGreyboxExtrude(mousePos); break;
        }
    }

    /// <summary>
    /// Moves every dragged face or edge. The lead follows the cursor; in Local the others move
    /// along their own axes by the same distances and offsets add up on shared corners, in
    /// Global they all move by the lead's world offset. A symmetric drag (Alt) also moves each
    /// element's opposite: the opposite face by the reversed offset, or the partner edge.
    /// </summary>
    static void ApplyGreyboxMove(Vector2 mousePos)
    {
        Vector3 leadWorld = s_gbCoordinates.Update(mousePos);
        bool face = s_gbDrag == GbDrag.FaceNormal;
        bool sum = s_gbCoordinates.LocalMode;
        foreach (var dragBox in s_gbDragBoxes)
        {
            dragBox.direct = 0;
            dragBox.mirrored = 0;
        }

        for (int i = 0; i < s_gbDragElements.Count; i++)
        {
            var element = s_gbDragElements[i];
            Vector3 world = i == 0 ? leadWorld : element.follower.Update(ref s_gbCoordinates);
            element.local = element.box.box.transform.InverseTransformVector(world);
            int[] corners = face ? GPEditShared.FaceCornerIndices[element.index] : GPEditShared.EdgeCornerIndices[element.index];
            foreach (int corner in corners) AddGreyboxDragOffset(element.box, corner, element.local, true, sum);
            s_gbDragElements[i] = element;
        }

        // Symmetry runs after every direct move, so it never overrides a corner a dragged element moves.
        for (int i = 0; i < s_gbDragElements.Count; i++)
        {
            var element = s_gbDragElements[i];
            if (!s_gbCoordinates.symmetric) element.mirrorAxes = 0;
            else if (face)
            {
                foreach (int corner in GPEditShared.FaceCornerIndices[element.index ^ 1])
                    AddGreyboxDragOffset(element.box, corner, -element.local, false, sum);
            }
            else
            {
                if (element.mirrorAxes == 0) element.mirrorAxes = PickEdgeMirrorAxes(element.box.box, element.index, element.local);
                if (element.mirrorAxes != 0)
                {
                    Vector3 mirrored = MirrorEdgeDelta(element.local, element.mirrorAxes);
                    foreach (int corner in GPEditShared.EdgeCornerIndices[element.index])
                        AddGreyboxDragOffset(element.box, corner ^ element.mirrorAxes, mirrored, false, sum);
                }
            }
            s_gbDragElements[i] = element;
        }

        foreach (var dragBox in s_gbDragBoxes) WriteGreyboxDragCorners(dragBox);
    }

    static void AddGreyboxDragOffset(GbDragBox dragBox, int corner, Vector3 local, bool direct, bool sum)
    {
        int bit = 1 << corner;
        if (direct)
        {
            dragBox.offsets[corner] = sum && (dragBox.direct & bit) != 0 ? dragBox.offsets[corner] + local : local;
            dragBox.direct |= bit;
        }
        else if ((dragBox.direct & bit) == 0)
        {
            dragBox.offsets[corner] = sum && (dragBox.mirrored & bit) != 0 ? dragBox.offsets[corner] + local : local;
            dragBox.mirrored |= bit;
        }
    }

    /// <summary>
    /// Local axis bits (matching the corner index bits) a symmetric edge drag is mirrored across,
    /// picked from the drag direction: across the box along the dominant movement axis, or both
    /// axes (the diagonally opposite edge) when the edge slides along itself. 0 while the drag is
    /// too short to tell.
    /// </summary>
    static int PickEdgeMirrorAxes(Greybox box, int edge, Vector3 localDelta)
    {
        int axis = edge / 4, u = (axis + 1) % 3, v = (axis + 2) % 3;
        Vector3 scaled = Vector3.Scale(localDelta, box.transform.lossyScale);
        float along = Mathf.Abs(scaled[axis]), acrossU = Mathf.Abs(scaled[u]), acrossV = Mathf.Abs(scaled[v]);
        float threshold = HandleUtility.GetHandleSize(s_gbCoordinates.anchor) * 0.02f;
        if (Mathf.Max(along, Mathf.Max(acrossU, acrossV)) < threshold) return 0;
        if (along > acrossU && along > acrossV) return (1 << u) | (1 << v);
        return acrossU >= acrossV ? 1 << u : 1 << v;
    }

    /// <summary>
    /// Delta of the partner edge: reflected across the one mirror axis, or fully reversed for the
    /// diagonally opposite edge so the box centre stays in place.
    /// </summary>
    static Vector3 MirrorEdgeDelta(Vector3 localDelta, int mirrorAxes)
    {
        bool diagonal = (mirrorAxes & (mirrorAxes - 1)) != 0;
        if (diagonal) return -localDelta;
        for (int k = 0; k < 3; k++)
            if ((mirrorAxes & (1 << k)) != 0) localDelta[k] = -localDelta[k];
        return localDelta;
    }

    /// <summary>
    /// Sets each corner moved this event to its start position plus its offset, and returns
    /// corners an earlier event of this drag displaced (symmetry switched off, another partner
    /// edge picked) to their start position.
    /// </summary>
    static void WriteGreyboxDragCorners(GbDragBox dragBox)
    {
        int moved = dragBox.direct | dragBox.mirrored;
        int touched = moved | dragBox.displaced;
        var box = dragBox.box;
        for (int corner = 0; corner < 8; corner++)
        {
            if ((touched & (1 << corner)) == 0) continue;
            box.Corners[corner] = (moved & (1 << corner)) != 0
                ? dragBox.start[corner] + dragBox.offsets[corner]
                : dragBox.start[corner];
        }
        dragBox.displaced = moved;

        box.RebuildMesh();
        EditorUtility.SetDirty(box);
        for (int corner = 0; corner < 8; corner++)
            if ((touched & (1 << corner)) != 0)
                GreyboxSeamSolver.SyncCorner(box, corner);
        GreyBooleanOrchestrator.ReBakeFrom(box);
    }

    static void FinishGreyboxDrag()
    {
        bool extruding = s_gbDrag == GbDrag.Extrude;
        using var createdScope = ListPool<GameObject>.Get(out var created);
        if (extruding) FinishGreyboxExtrusions(created);
        else if (GreyPrimitiveSettings.AutoUpdatePivot)
            foreach (var dragBox in s_gbDragBoxes)
                GreyPrimitiveEditor.RecenterGreyboxPivot(dragBox.box);

        GreyPrimitive.EndDeferredPersist();
        Undo.CollapseUndoOperations(s_gbUndoGroup);
        GUIUtility.hotControl = 0;
        ResetGreyboxDrag();
        if (!extruding) return;

        // The extruded faces are welded and hidden now.
        ClearSubSelection();
        if (created.Count == 0 || SelectedPrimitive is GreyBooleanResult) return;
        using var selectionScope = ListPool<Object>.Get(out var selection);
        selection.AddRange(Selection.objects);
        // The lead's new box becomes the active object.
        selection.Remove(created[0]);
        selection.Insert(0, created[0]);
        for (int i = 1; i < created.Count; i++)
            if (!selection.Contains(created[i])) selection.Add(created[i]);
        Selection.objects = selection.ToArray();
    }

    static void ResetGreyboxDrag()
    {
        s_gbDrag = GbDrag.None;
        s_gbTarget = null;
        s_gbDragBoxes.Clear();
        s_gbDragElements.Clear();
        s_gbExtrusions.Clear();
        s_gbMirrorExtruded = false;
    }

    // ─── Extrude ────────────────────────────────────────────────

    /// <summary>
    /// Starts an extrude led by the pressed face. When that face is selected, every selected
    /// face is extruded by the same distance along its own forward axis, and linked extrusions
    /// of flush neighbouring faces are welded to each other. Expects s_gbWc to hold the lead
    /// box's world corners.
    /// </summary>
    static void StartGreyboxExtrude(Greybox sourceGb, int face, Vector2 mousePos, bool linked)
    {
        if (linked && !GreyboxLinkHierarchy.CanLink(sourceGb, sourceGb)) return;
        s_gbFace = face;
        s_gbExtrudeLinked = linked;
        Undo.IncrementCurrentGroup();
        s_gbUndoGroup = Undo.GetCurrentGroup();
        GreyPrimitive.BeginDeferredPersist();

        if (linked) GreyBooleanOrchestrator.EnsureManagedInput(sourceGb);
        bool local = Tools.pivotRotation == PivotRotation.Local;
        sourceGb.GetWorldCorners(s_gbWc);
        var globalCoordinates = GreyboxFaceCoordinates(sourceGb, face, false);
        var localCoordinates = GreyboxFaceCoordinates(sourceGb, face, true);
        s_gbCoordinates.Begin(GreyboxFaceCenter(face), mousePos,
            EditCoordinates.Face(globalCoordinates.a, globalCoordinates.a, Vector3.zero),
            EditCoordinates.Face(localCoordinates.a, localCoordinates.a, Vector3.zero));

        s_gbExtrusions.Clear();
        AddGreyboxExtrusion(sourceGb, face, local ? localCoordinates.a : globalCoordinates.a, linked);
        if (IsSubSelected(sourceGb, SubKind.BoxFace, face))
            foreach (var element in s_subSelection)
            {
                if (element.Matches(sourceGb, SubKind.BoxFace, face)) continue;
                var box = (Greybox)element.target;
                // Legitimate filter: a welded face has no free surface, and a box that cannot join
                // a link group takes no linked extrusion.
                if ((GreyboxSeamFaceMask(box) & (1 << element.index)) != 0) continue;
                if (linked && !GreyboxLinkHierarchy.CanLink(box, box)) continue;
                if (linked) GreyBooleanOrchestrator.EnsureManagedInput(box);
                box.GetWorldCorners(s_gbWc);
                AddGreyboxExtrusion(box, element.index, GreyboxFaceCoordinates(box, element.index, local).a, linked);
            }
        if (linked) WeldAdjacentExtrusions();
        sourceGb.GetWorldCorners(s_gbWc);
        s_gbDrag = GbDrag.Extrude;
    }

    static void AddGreyboxExtrusion(Greybox source, int face, Vector3 normal, bool linked)
    {
        var extrusion = new GbExtrusion { source = source, face = face, normal = normal };
        extrusion.created = CreateGreyboxExtrusion(source, face, normal, linked, out extrusion.baseCorners);
        s_gbExtrusions.Add(extrusion);
    }

    /// <summary>
    /// Creates the box an extrude drag grows out of <paramref name="face"/>. Its base sits on the
    /// source face (welded to it when linked) and its far face starts 0.001 along
    /// <paramref name="normal"/>. <paramref name="baseCorners"/> receives the corners the drag offsets from.
    /// </summary>
    static Greybox CreateGreyboxExtrusion(Greybox sourceGb, int face, Vector3 normal, bool linked, out Vector3[] baseCorners)
    {
        Vector3[] wc = sourceGb.GetWorldCorners();
        int[] ci = GPEditShared.FaceCornerIndices[face];
        Vector3 c0 = wc[ci[0]], c1 = wc[ci[1]], c2 = wc[ci[2]], c3 = wc[ci[3]];
        Vector3 center = (c0 + c1 + c2 + c3) * 0.25f;

        Vector3 up = normal;
        Vector3 tDir = Vector3.ProjectOnPlane(c1 - c0, up);
        if (tDir.sqrMagnitude < 0.0001f) tDir = Vector3.ProjectOnPlane(c3 - c0, up);
        if (tDir.sqrMagnitude < 0.0001f) tDir = Vector3.ProjectOnPlane(Vector3.forward, up);
        if (tDir.sqrMagnitude < 0.0001f) tDir = Vector3.ProjectOnPlane(Vector3.right, up);
        Quaternion extrudeRot = Quaternion.LookRotation(tDir.normalized, up);

        Undo.RegisterCompleteObjectUndo(sourceGb, "Greybox Extrude");
        if (linked) sourceGb.ActiveFaces[face] = false;
        sourceGb.RebuildMesh();
        EditorUtility.SetDirty(sourceGb);

        Transform parent = linked ? GreyboxLinkHierarchy.LinkedExtrusionParent(sourceGb) : GreyboxLinkHierarchy.UnlinkedExtrusionParent(sourceGb);
        parent = GreyPrimitiveSettings.ResolveParent(parent, sourceGb.gameObject.scene);
        var go = GreyboxSettings.PlaceGreybox(center, extrudeRot, parent, select: false);
        go.transform.localScale = Vector3.one;
        var extruded = go.GetComponent<Greybox>();

        Vector3[] newCorners = Greybox.DefaultCorners();
        Vector3[] srcFaceCorners = { c0, c1, c2, c3 };
        int[] linkChildCorners = (int[])k_ExtrusionBaseCorners.Clone();
        int[] linkParentCorners = (int[])ci.Clone();
        for (int k = 0; k < 4; k++)
        {
            int bottom = linkChildCorners[k];
            newCorners[bottom] = go.transform.InverseTransformPoint(srcFaceCorners[k]);
            newCorners[bottom | 2] = newCorners[bottom] + go.transform.InverseTransformVector(normal * 0.001f);
        }
        baseCorners = (Vector3[])newCorners.Clone();

        var srcSO      = new SerializedObject(sourceGb);
        var dstSO      = new SerializedObject(extruded);
        var cornersArr = dstSO.FindProperty("_corners");
        for (int i = 0; i < 8; i++)
            cornersArr.GetArrayElementAtIndex(i).vector3Value = newCorners[i];
        dstSO.FindProperty("_subdivisionMultiplier").floatValue = srcSO.FindProperty("_subdivisionMultiplier").floatValue;
        dstSO.FindProperty("_uvTileScale").floatValue           = srcSO.FindProperty("_uvTileScale").floatValue;
        dstSO.ApplyModifiedPropertiesWithoutUndo();

        var sourceMr = sourceGb.GetComponent<MeshRenderer>();
        var newMr    = go.GetComponent<MeshRenderer>();
        if (sourceMr != null && newMr != null)
        {
            newMr.enabled           = !linked || sourceMr.enabled;
            newMr.sharedMaterial    = sourceMr.sharedMaterial;
            newMr.shadowCastingMode = sourceMr.shadowCastingMode;
        }
        var sourceCollider = sourceGb.GetComponent<MeshCollider>();
        var newCollider = go.GetComponent<MeshCollider>();
        if (newCollider != null && sourceCollider != null) newCollider.enabled = !linked || sourceCollider.enabled;
        go.layer    = sourceGb.gameObject.layer;
        go.isStatic = sourceGb.gameObject.isStatic;

        if (linked) extruded.ActiveFaces[3] = false; // hide bottom — seam against source
        extruded.RebuildMesh();
        EditorUtility.SetDirty(extruded);

        if (linked)
        {
            extruded.SetSeamLink(sourceGb, linkChildCorners, linkParentCorners);
            sourceGb.AddSeamChild(extruded);
            KeepSplitInBoolean(sourceGb, extruded);
            baseCorners = (Vector3[])extruded.Corners.Clone();
            EditorUtility.SetDirty(extruded);
        }
        return extruded;
    }

    /// <summary>
    /// Welds new extrusions side to side when their source faces are flush neighbours (same
    /// forward axis, sharing an edge), so the extruded boxes form one connected block. Faces at
    /// an angle to each other stay separate extrusions.
    /// </summary>
    static void WeldAdjacentExtrusions()
    {
        for (int i = 0; i < s_gbExtrusions.Count; i++)
            for (int j = i + 1; j < s_gbExtrusions.Count; j++)
                TryWeldExtrusions(s_gbExtrusions[i], s_gbExtrusions[j]);
    }

    static void TryWeldExtrusions(GbExtrusion a, GbExtrusion b)
    {
        if (Vector3.Dot(a.normal, b.normal) < 0.9999f) return; // not flush

        Vector3[] wa = a.source.GetWorldCorners(), wb = b.source.GetWorldCorners();
        int[] fa = GPEditShared.FaceCornerIndices[a.face], fb = GPEditShared.FaceCornerIndices[b.face];
        for (int k = 0; k < 4; k++)
        {
            int next = (k + 1) % 4;
            int m = MatchingFaceCorner(wa[fa[k]], wb, fb), n = MatchingFaceCorner(wa[fa[next]], wb, fb);
            if (m < 0 || n < 0 || m == n) continue; // this edge of a's face is not shared

            // The side face of each new box that stands on the shared edge, corners paired by position.
            int a0 = k_ExtrusionBaseCorners[k], a1 = k_ExtrusionBaseCorners[next];
            int b0 = k_ExtrusionBaseCorners[m], b1 = k_ExtrusionBaseCorners[n];
            int[] cornersA = { a0, a1, a1 | 2, a0 | 2 };
            int[] cornersB = { b0, b1, b1 | 2, b0 | 2 };
            int faceA = GreyboxFaceOfCorners(cornersA), faceB = GreyboxFaceOfCorners(cornersB);
            if (faceA < 0 || faceB < 0) return; // the shared points are a diagonal, not an edge

            a.created.ActiveFaces[faceA] = false;
            b.created.ActiveFaces[faceB] = false;
            a.created.AddSeamLink(b.created, cornersA, cornersB);
            a.created.RebuildMesh();
            b.created.RebuildMesh();
            EditorUtility.SetDirty(a.created);
            EditorUtility.SetDirty(b.created);
            return;
        }
    }

    /// <summary>Position (0-3) in <paramref name="face"/> of the corner at <paramref name="point"/>, or -1.</summary>
    static int MatchingFaceCorner(Vector3 point, Vector3[] worldCorners, int[] face)
    {
        for (int i = 0; i < 4; i++)
            if ((worldCorners[face[i]] - point).sqrMagnitude < 1e-6f) return i;
        return -1;
    }

    static void ApplyGreyboxExtrude(Vector2 mousePos)
    {
        if (s_gbExtrusions.Count == 0) return;
        Vector3 worldDelta = s_gbCoordinates.Update(mousePos);
        SyncGreyboxMirrorExtrude();
        float forward = Mathf.Max(0.001f, Vector3.Dot(worldDelta, s_gbExtrusions[0].normal));
        foreach (var extrusion in s_gbExtrusions)
        {
            // An undo during the drag can delete an extruded box.
            if (extrusion.created == null) continue;
            OffsetGreyboxExtrusion(extrusion.created, extrusion.baseCorners, extrusion.normal * forward);
            // No mirror extrusion while symmetry is off or the opposite face cannot be extruded.
            if (extrusion.mirror != null)
                OffsetGreyboxExtrusion(extrusion.mirror, extrusion.mirrorBase, extrusion.mirrorNormal * forward);
        }
    }

    static void OffsetGreyboxExtrusion(Greybox extruded, Vector3[] baseCorners, Vector3 worldDelta)
    {
        OffsetExtrudedFace(baseCorners, extruded.transform.InverseTransformVector(worldDelta), extruded.Corners);
        extruded.RebuildMesh();
        GreyBooleanOrchestrator.ReBakeFrom(extruded);
        EditorUtility.SetDirty(extruded.gameObject);
    }

    static void FinishGreyboxExtrusions(List<GameObject> created)
    {
        using var recenteredScope = HashSetPool<int>.Get(out var recentered);
        using var organizedScope = HashSetPool<int>.Get(out var organized);
        foreach (var extrusion in s_gbExtrusions)
        {
            // An undo during the drag can delete an extruded box.
            if (extrusion.created == null) continue;
            if (GreyPrimitiveSettings.AutoUpdatePivot)
            {
                if (recentered.Add(extrusion.source.GetInstanceID())) GreyPrimitiveEditor.RecenterGreyboxPivot(extrusion.source);
                GreyPrimitiveEditor.RecenterGreyboxPivot(extrusion.created);
                if (extrusion.mirror != null) GreyPrimitiveEditor.RecenterGreyboxPivot(extrusion.mirror);
            }
            if (extrusion.created.IsLinkAlive)
            {
                KeepSplitInBoolean(extrusion.source, extrusion.created);
                if (extrusion.mirror != null) KeepSplitInBoolean(extrusion.source, extrusion.mirror);
                if (organized.Add(extrusion.source.GetInstanceID()))
                {
                    GreyboxLinkHierarchy.Organize(extrusion.source, "Greybox Extrude");
                    GreyBooleanOrchestrator.ReBakeFrom(extrusion.source);
                }
            }
            created.Add(extrusion.created.gameObject);
        }
        foreach (var extrusion in s_gbExtrusions)
            if (extrusion.mirror != null) created.Add(extrusion.mirror.gameObject);
    }

    // ─── Symmetrical extrude ────────────────────────────────────

    /// <summary>Keeps a second extrusion on each opposite face exactly while the drag is symmetric.</summary>
    static void SyncGreyboxMirrorExtrude()
    {
        if (s_gbCoordinates.symmetric == s_gbMirrorExtruded) return;
        if (s_gbMirrorExtruded) EndGreyboxMirrorExtrude();
        else BeginGreyboxMirrorExtrude();
    }

    static void BeginGreyboxMirrorExtrude()
    {
        // Own undo group, so switching symmetry off reverts exactly these extrusions.
        Undo.IncrementCurrentGroup();
        s_gbMirrorExtrudeUndoGroup = Undo.GetCurrentGroup();
        s_gbMirrorExtruded = true;
        bool local = Tools.pivotRotation == PivotRotation.Local;
        foreach (var extrusion in s_gbExtrusions)
        {
            if (extrusion.created == null) continue; // deleted by an undo during the drag
            int face = extrusion.face ^ 1;
            // Legitimate filter: a face welded to another box has no free surface to extrude, and
            // a face this drag already extrudes needs no mirror.
            if ((GreyboxSeamFaceMask(extrusion.source) & (1 << face)) != 0) continue;
            if (IsGreyboxFaceExtruding(extrusion.source, face)) continue;
            extrusion.source.GetWorldCorners(s_gbWc);
            extrusion.mirrorNormal = GreyboxFaceCoordinates(extrusion.source, face, local).a;
            extrusion.mirror = CreateGreyboxExtrusion(extrusion.source, face, extrusion.mirrorNormal, s_gbExtrudeLinked,
                out extrusion.mirrorBase);
        }
        s_gbTarget.GetWorldCorners(s_gbWc);
    }

    static bool IsGreyboxFaceExtruding(Greybox source, int face)
    {
        foreach (var extrusion in s_gbExtrusions)
            if (extrusion.face == face && ReferenceEquals(extrusion.source, source)) return true;
        return false;
    }

    static void EndGreyboxMirrorExtrude()
    {
        Undo.RevertAllDownToGroup(s_gbMirrorExtrudeUndoGroup);
        s_gbMirrorExtruded = false;
        foreach (var extrusion in s_gbExtrusions)
        {
            extrusion.mirror = null;
            extrusion.mirrorBase = null;
            // The revert restores the source's serialized face flags, not its in-memory mesh.
            extrusion.source.RebuildMesh();
            EditorUtility.SetDirty(extrusion.source);
            GreyBooleanOrchestrator.ReBakeFrom(extrusion.source);
        }
    }

    // ─── Draw ───────────────────────────────────────────────────

    static void OffsetExtrudedFace(Vector3[] baseCorners, Vector3 localDelta, Vector3[] corners)
    {
        for (int i = 0; i < 8; i++)
            if ((i & 2) == 0)
            {
                corners[i] = baseCorners[i];
                corners[i | 2] = baseCorners[i] + localDelta;
            }
    }

    /// <summary>Marks the element a symmetric drag moves along with the lead. Expects s_gbWc to hold the lead box's corners.</summary>
    static void DrawGreyboxSymmetryHighlight()
    {
        if (!s_gbCoordinates.symmetric) return;
        switch (s_gbDrag)
        {
            case GbDrag.FaceNormal:
                GPEditShared.DrawDot(GreyboxFaceCenter(s_gbFace ^ 1), GPEditShared.HandleHover, 0.055f);
                break;
            case GbDrag.Edge:
                int mirrorAxes = s_gbDragElements.Count > 0 ? s_gbDragElements[0].mirrorAxes : 0;
                if (mirrorAxes == 0) break; // partner edge not picked yet
                int[] corners = GPEditShared.EdgeCornerIndices[s_gbEdge];
                Handles.color = GPEditShared.OutlineHover;
                Handles.DrawLine(s_gbWc[corners[0] ^ mirrorAxes], s_gbWc[corners[1] ^ mirrorAxes], 4f);
                break;
            case GbDrag.Extrude:
                if (s_gbExtrusions.Count > 0 && s_gbExtrusions[0].mirror != null)
                    GPEditShared.DrawDot(GreyboxFaceCenter(s_gbFace ^ 1), GPEditShared.Create, 0.055f);
                break;
        }
    }

    static void DrawGreybox(Greybox gb, int hoverFace, int hoverEdge, int operandIndex = -1)
    {
        int seamFaces = GreyboxSeamFaceMask(gb);
        Color tint = operandIndex <= 0 ? Color.white
            : Color.HSVToRGB(Mathf.Repeat(0.55f + (operandIndex - 1) * 0.618034f, 1f), 0.4f, 1f);
        float outlineWidth = operandIndex >= 0 && gb.GetComponentInParent<GreyBooleanResult>() != null && (hoverFace >= 0 || hoverEdge >= 0) ? 1.6f : 0f;

        int selectedFaces = 0, selectedEdges = 0;
        foreach (var element in s_subSelection)
        {
            if (!ReferenceEquals(element.target, gb)) continue;
            if (element.kind == SubKind.BoxFace) selectedFaces |= 1 << element.index;
            else if (element.kind == SubKind.BoxEdge) selectedEdges |= 1 << element.index;
        }

        if (s_gbDrag == GbDrag.None && hoverFace >= 0)
        {
            var faceCorners = GPEditShared.FaceCornerIndices[hoverFace];
            Handles.color = GPEditShared.CreateFace;
            Handles.DrawAAConvexPolygon(s_gbWc[faceCorners[0]], s_gbWc[faceCorners[1]],
                s_gbWc[faceCorners[2]], s_gbWc[faceCorners[3]]);
        }
        for (int face = 0; face < 6; face++)
        {
            if ((selectedFaces & (1 << face)) == 0) continue;
            var faceCorners = GPEditShared.FaceCornerIndices[face];
            Handles.color = GPEditShared.SelectedFace;
            Handles.DrawAAConvexPolygon(s_gbWc[faceCorners[0]], s_gbWc[faceCorners[1]],
                s_gbWc[faceCorners[2]], s_gbWc[faceCorners[3]]);
        }
        if (s_gbDrag == GbDrag.None && hoverEdge >= 0)
            DrawGreyboxSplitPreview(gb, hoverEdge, Event.current.mousePosition);

        Vector3 camFwd = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;
        int frontFaces = 0;
        for (int face = 0; face < 6; face++)
            if ((seamFaces & (1 << face)) == 0 && Vector3.Dot(GreyboxFaceNormal(gb, face), camFwd) < 0f)
                frontFaces |= 1 << face;

        // All 12 edges are drawn; edges of hidden faces are dimmed, edges between two seam
        // faces have no rendered surface to be behind so they skip the backface dim.
        for (int ei = 0; ei < 12; ei++)
        {
            int[] ec = GPEditShared.EdgeCornerIndices[ei];
            var adjacent = GPEditShared.EdgeFaceAdjacency[ei];
            int adjacentMask = (1 << adjacent[0]) | (1 << adjacent[1]);
            bool front = (frontFaces & adjacentMask) != 0 || (seamFaces & adjacentMask) == adjacentMask;
            bool selected = (selectedEdges & (1 << ei)) != 0;
            bool emphasized = selected || ei == hoverEdge;
            Color color = selected ? GPEditShared.Selected
                : ei == hoverEdge ? GPEditShared.OutlineHover : GPEditShared.Outline * tint;
            if (!front) color.a *= GPEditShared.BackfaceAlpha;
            if (!emphasized && !IsOutlineEdge(gb, ei)) color.a *= 0.35f;
            Handles.color = color;
            Handles.DrawLine(s_gbWc[ec[0]], s_gbWc[ec[1]], selected ? 5f : ei == hoverEdge ? 4f : outlineWidth);
        }

        if (hoverFace >= 0 || hoverEdge >= 0)
        {
            if (s_gbDrag != GbDrag.None && gb == s_gbTarget)
                s_gbCoordinates.Draw(s_gbCoordinates.anchor + s_gbCoordinates.delta);
            else if (hoverFace >= 0)
                DrawCoordinates(GreyboxFaceCenter(hoverFace), GreyboxFaceCoordinates(gb, hoverFace, Tools.pivotRotation == PivotRotation.Local));
            else
            {
                var corners = GPEditShared.EdgeCornerIndices[hoverEdge];
                Vector3 point = GPEditShared.ClosestPointOnSegmentToScreenPos(s_gbWc[corners[0]], s_gbWc[corners[1]], Event.current.mousePosition);
                DrawCoordinates(point, GreyboxEdgeCoordinates(gb, hoverEdge, Tools.pivotRotation == PivotRotation.Local));
            }
        }

        // Hidden welded faces have no handles; other hidden faces can still be toggled back on.
        if (Camera.current == null) return;
        for (int face = 0; face < 6; face++)
        {
            if ((seamFaces & (1 << face)) != 0) continue;
            Vector3 center  = GreyboxFaceCenter(face);
            bool front      = Vector3.Dot(GreyboxFaceNormal(gb, face), camFwd) < 0f;
            bool active     = gb.IsFaceVisible(face);
            bool selected   = (selectedFaces & (1 << face)) != 0;
            Color col = selected          ? GPEditShared.Selected
                      : face == hoverFace ? (s_gbDrag == GbDrag.Extrude ? GPEditShared.Create : GPEditShared.HandleHover)
                      : active            ? GPEditShared.HandleActive * tint
                      :                     GPEditShared.HandleInactive;
            col.a *= front ? 1f : GPEditShared.BackfaceAlpha;
            GPEditShared.DrawDot(center, col, (selected ? 0.08f : 0.05f) * (front ? 1.1f : 0.85f));
        }
    }
}
#endif
