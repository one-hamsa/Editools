#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>Shared spline controls: Ctrl changes coordinates, Shift locks an axis, Alt reshapes from an endpoint,
/// Alt pressed during a drag mirrors it onto the vertex at the mirrored index.</summary>
static partial class GPEdit
{
    enum SpDrag { None, VertexMove, Bezier, Banking, Extend, Branch, AnchorSlide, Chamfer }

    static GPSpline s_sp;
    static SpDrag   s_spDrag;
    static int      s_spButton;
    static int      s_spControl;
    static int      s_spUndoGroup;

    static int      s_spVertex;
    static int      s_spSide;
    static CoordinateDrag s_spCoordinates;

    static Vector3  s_spStartLocalPos;
    static Vector3  s_spBezierStartEndpoint;

    static float    s_spBankingStartAngle;
    static Vector3  s_spBankingTangent;
    static Vector3  s_spBankingPoint;

    static int      s_spExtendEdge;
    static Vector3[] s_spReshape;
    static bool     s_spDoReshape;

    // Symmetry (Alt pressed during a drag, see CoordinateDrag.symmetric)
    static Vector3  s_spMirrorAxis;       // world main axis at drag start; normal of the mirror plane
    static int      s_spMirrorVertex = -1; // vertex at the mirrored index; -1 = the dragged vertex is the middle one
    static Vector3  s_spMirrorStartLocalPos;
    static Vector3  s_spMirrorStartHandleDirLocal;
    static float    s_spMirrorStartHandleLength;
    static float    s_spMirrorStartBankingAngle;
    static bool     s_spMirrorApplied;    // the last event wrote mirrored values to s_spMirrorVertex
    static bool     s_spMirrorExtended;   // a symmetric extend holds a vertex at the other end

    // Chamfer (RMB drag on an inner vertex): the vertex becomes two that slide apart along the original curve.
    static bool     s_spChamferCreated;     // the second vertex exists; unset until the press becomes a drag
    static Vector2  s_spChamferPress;
    static float    s_spChamferUnitsPerPixel;
    static readonly Vector3[] s_spChamferBefore = new Vector3[4]; // world control points of the segment ending at the vertex
    static readonly Vector3[] s_spChamferAfter  = new Vector3[4]; // world control points of the segment starting at it
    static float    s_spChamferBank, s_spChamferBankBefore, s_spChamferBankAfter;

    /// <summary>One more selected vertex (or its handle) taking part in a drag led by s_spVertex.</summary>
    struct SpFollower
    {
        public GPSpline sp;            // the lead's binding, or the binding of another spline
        public int vertex;
        public Vector3 startLocalPos;  // vertex move
        public Vector3 startEndpoint;  // Bezier: world position of the handle on the lead's side
        public float startBanking;
        public CoordinateFollower follower;
    }

    static readonly List<SpFollower> s_spFollowers = new List<SpFollower>(16);
    // Bindings of the other splines that have followers; the lead's spline is s_sp.
    static readonly List<GPSpline> s_spExtraSplines = new List<GPSpline>(4);

    static readonly GPSplinePipe s_pipeAdapter = new GPSplinePipe();
    static readonly GPSplineRoad s_roadAdapter = new GPSplineRoad();

    static partial void OnGreypipeSceneGUI(SceneView sv, Event e, Greypipe pipe)
    {
        s_pipeAdapter.Set(pipe);
        OnSplineSceneGUI(sv, e, s_pipeAdapter);
    }

    static partial void OnGreyroadSceneGUI(SceneView sv, Event e, Greyroad road)
    {
        // A ledge's vertices are derived from its source, so it gets the ledge controls instead.
        if (road.IsLedge)
        {
            OnLedgeSceneGUI(sv, e, road);
            return;
        }
        s_roadAdapter.Set(road);
        OnSplineSceneGUI(sv, e, s_roadAdapter);
        OnRoadEdgeSceneGUI(sv, e, road);
    }

    static void OnSplineSceneGUI(SceneView sv, Event e, GPSpline sp)
    {
        if (s_spDrag != SpDrag.None) { HandleSplineDrag(e, sv, sp); return; }

        // Hover
        int hoverVertex = HitSplineVertex(sp, e.mousePosition);
        int hoverBezier = -1, hoverBezierSide = 0;
        int hoverBank = -1, hoverBankSide = 0;
        if (hoverVertex < 0)
        {
            HitSplineBezier(sp, e.mousePosition, out hoverBezier, out hoverBezierSide);
            if (hoverBezier < 0 && sp.HasBanking)
                HitSplineBanking(sp, e.mousePosition, out hoverBank, out hoverBankSide);
        }

        // When hovering the bare spline (no vertex/handle), preview where RMB would insert a vertex.
        bool hasPreview = false;
        Vector3 previewPos = default;
        if (hoverVertex < 0 && hoverBezier < 0 && hoverBank < 0
            && HitSplineSegment(sp, e.mousePosition, out int pSeg, out float pT))
        {
            previewPos = sp.EvaluateSplineWorld(pSeg, pT);
            hasPreview = true;
        }

        bool altEndpoint = e.button == 0 && (hoverVertex == 0 || hoverVertex == sp.Count - 1);
        if (e.type == EventType.MouseDown && (!e.alt || altEndpoint) && (e.button == 0 || e.button == 1 || e.button == 2))
            BeginSpline(sv, e, sp, hoverVertex, hoverBezier, hoverBezierSide, hoverBank, hoverBankSide);

        if (e.type == EventType.Repaint)
            DrawSpline(sp, hoverVertex, hoverBezier, hoverBezierSide, hoverBank, hoverBankSide, hasPreview, previewPos);

        if (e.type == EventType.MouseMove || e.type == EventType.KeyDown || e.type == EventType.KeyUp)
            sv.Repaint();
    }

    // ─── Hover ──────────────────────────────────────────────────

    static int HitSplineVertex(GPSpline sp, Vector2 mousePos)
    {
        int best = -1;
        float bestDist = GPEditShared.HandlePx;
        for (int i = 0; i < sp.Count; i++)
        {
            Vector2 s = HandleUtility.WorldToGUIPoint(sp.GetWorldVertexPos(i));
            float dist = Vector2.Distance(mousePos, s);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    static void HitSplineBezier(GPSpline sp, Vector2 mousePos, out int vertex, out int side)
    {
        vertex = -1; side = 0;
        float bestDist = GPEditShared.HandlePx;
        for (int i = 0; i < sp.Count; i++)
        {
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                Vector2 s = HandleUtility.WorldToGUIPoint(sp.GetHandleWorldPosition(i, sgn));
                float dist = Vector2.Distance(mousePos, s);
                if (dist < bestDist) { bestDist = dist; vertex = i; side = sgn; }
            }
        }
    }

    static void HitSplineBanking(GPSpline sp, Vector2 mousePos, out int vertex, out int side)
    {
        vertex = -1; side = 0;
        float bestDist = GPEditShared.HoverPx * 1.2f;
        for (int i = 0; i < sp.Count; i++)
        {
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                Vector2 s = HandleUtility.WorldToGUIPoint(sp.GetBankingHandleWorld(i, sgn));
                float dist = Vector2.Distance(mousePos, s);
                if (dist < bestDist) { bestDist = dist; vertex = i; side = sgn; }
            }
        }
    }

    static bool HitSplineSegment(GPSpline sp, Vector2 mousePos, out int seg, out float t)
    {
        seg = -1; t = 0f;
        float bestDist = GPEditShared.HandlePx;

        // Coarse pass: nearest sample across all segments.
        const int coarse = 24;
        for (int s = 0; s < sp.SegmentCount; s++)
        {
            for (int i = 0; i <= coarse; i++)
            {
                float st = i / (float)coarse;
                float dist = Vector2.Distance(mousePos, HandleUtility.WorldToGUIPoint(sp.EvaluateSplineWorld(s, st)));
                if (dist < bestDist) { bestDist = dist; seg = s; t = st; }
            }
        }
        if (seg < 0) return false;

        // Fine pass within the winning segment so the point slides smoothly instead of snapping.
        float step = 1f / coarse;
        float lo = Mathf.Max(0f, t - step), hi = Mathf.Min(1f, t + step);
        const int fine = 24;
        for (int i = 0; i <= fine; i++)
        {
            float ft = Mathf.Lerp(lo, hi, i / (float)fine);
            float dist = Vector2.Distance(mousePos, HandleUtility.WorldToGUIPoint(sp.EvaluateSplineWorld(seg, ft)));
            if (dist < bestDist) { bestDist = dist; t = ft; }
        }
        return true;
    }

    // ─── Begin ──────────────────────────────────────────────────

    static void BeginSpline(SceneView sv, Event e, GPSpline sp, int vertex, int bezier, int bezierSide, int bank, int bankSide)
    {
        bool isEnd(int i) => i == 0 || i == sp.Count - 1;

        if (vertex >= 0 && sp.IsAnchoredVertex(vertex))
        {
            // A branch road's first vertex only slides along its source road.
            if (e.button != 0) return;
            StartBranchAnchorSlide(sp, e.mousePosition);
        }
        else if (vertex >= 0)
        {
            switch (e.button)
            {
                // LMB: a click selects the vertex, a drag moves it (GPEditSubSelection.cs).
                case 0: BeginSubPress(e, (GreyPrimitive)sp.Obj, SubKind.SplineVertex, vertex); return;
                case 1:
                    if (isEnd(vertex)) StartSplineExtend(sp, vertex, e.mousePosition);
                    else StartSplineChamfer(vertex, e.mousePosition);
                    break;
                case 2: BeginPrimitiveClick(sv, e, (GreyPrimitive)sp.Obj, ClickAction.DeleteVertex, vertex, vertexCount: sp.Count); return;
            }
        }
        else if (bezier >= 0)
        {
            switch (e.button)
            {
                case 0: BeginSubPress(e, (GreyPrimitive)sp.Obj, SubKind.SplineBezier, bezier, bezierSide); return;
                case 2: BeginPrimitiveClick(sv, e, (GreyPrimitive)sp.Obj, ClickAction.ResetHandle, bezier, vertexCount: sp.Count); return;
                default: return;
            }
        }
        else if (bank >= 0)
        {
            // Legitimate filter: a branch start's banking follows its source road.
            if (sp.IsAnchoredVertex(bank)) return;
            switch (e.button)
            {
                case 0: BeginSubPress(e, (GreyPrimitive)sp.Obj, SubKind.SplineBanking, bank, bankSide); return;
                case 2: BeginPrimitiveClick(sv, e, (GreyPrimitive)sp.Obj, ClickAction.ResetBanking, bank, vertexCount: sp.Count); return;
                default: return;
            }
        }
        else if (e.button == 1)
        {
            if (!HitSplineSegment(sp, e.mousePosition, out int seg, out float t)) return;
            // On a road a click inserts a vertex on release and a drag pulls out a branch road (GPEditBranch.cs).
            if (sp.Obj is Greyroad road) StartSplineBranch(road, seg, t, e.mousePosition);
            else
            {
                BeginPrimitiveClick(sv, e, (GreyPrimitive)sp.Obj, ClickAction.InsertVertex, seg, t, sp.Count);
                return;
            }
        }
        else return;

        if (s_spDrag == SpDrag.None) return;
        s_sp = CreateSplineBinding((GreyPrimitive)sp.Obj);
        s_spButton = e.button;
        s_spControl = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(s_spControl);
        GUIUtility.hotControl = s_spControl;
        e.Use();
    }

    // Independent binding keeps drawing other selected splines from retargeting an action.
    static GPSpline CreateSplineBinding(GreyPrimitive primitive)
    {
        if (primitive is Greypipe pipe)
        {
            var binding = new GPSplinePipe();
            binding.Set(pipe);
            return binding;
        }
        if (primitive is Greyroad road)
        {
            var binding = new GPSplineRoad();
            binding.Set(road);
            return binding;
        }
        return null; // Other primitive types have no spline controls.
    }

    static bool CommitSplineClick(PrimitiveClick click)
    {
        var sp = CreateSplineBinding(click.target);
        // Topology may change while the button is held; stale indices must not edit another vertex.
        if (sp == null || sp.Count != click.vertexCount || click.element < 0
            || click.element >= (click.action == ClickAction.InsertVertex ? sp.SegmentCount : sp.Count)) return false;
        switch (click.action)
        {
            case ClickAction.InsertVertex: InsertSplineVertex(sp, click.element, click.fraction); break;
            case ClickAction.DeleteVertex: DeleteSplineVertices(sp, click.element); break;
            case ClickAction.ResetHandle: ApplySplineClickAction(sp, SubKind.SplineBezier, click.element, ResetSplineHandle); break;
            case ClickAction.ResetBanking:
                if (!sp.HasBanking) return false;
                ApplySplineClickAction(sp, SubKind.SplineBanking, click.element, ResetSplineBanking);
                break;
            default: return false;
        }
        return true;
    }

    static void StartSplineVertexMove(GPSpline sp, int vertex, Vector2 mousePos, bool reshape)
    {
        BeginSplineUndo(sp, "Move Vertex");
        s_spDrag = SpDrag.VertexMove;
        s_spVertex = vertex;
        s_spStartLocalPos = sp.GetLocalVertexPos(vertex);
        CaptureSplineMirror(sp, vertex);
        s_spCoordinates.Begin(sp.GetWorldVertexPos(vertex), mousePos, SplineCoordinates(sp, vertex, false), SplineCoordinates(sp, vertex, true));
        CaptureSplineFollowers(sp, SubKind.SplineVertex, vertex, "Move Vertex");
        bool isEnd = vertex == 0 || vertex == sp.Count - 1;
        s_spDoReshape = isEnd && sp.Count > 2 && reshape;
        s_spReshape = s_spDoReshape ? CaptureReshape(sp) : null;
    }

    static void StartSplineBezier(GPSpline sp, int vertex, int side, Vector2 mousePos)
    {
        BeginSplineUndo(sp, "Move Handle");
        s_spDrag = SpDrag.Bezier;
        s_spVertex = vertex;
        s_spSide = side;
        s_spBezierStartEndpoint = sp.GetHandleWorldPosition(vertex, side);
        CaptureSplineMirror(sp, vertex);
        s_spCoordinates.Begin(s_spBezierStartEndpoint, mousePos, SplineCoordinates(sp, vertex, false), SplineCoordinates(sp, vertex, true));
        CaptureSplineFollowers(sp, SubKind.SplineBezier, vertex, "Move Handle");
    }

    static void StartSplineBanking(GPSpline sp, int vertex, int side, Vector2 mousePos)
    {
        BeginSplineUndo(sp, "Bank Vertex");
        s_spDrag = SpDrag.Banking;
        s_spVertex = vertex;
        s_spSide = side;
        s_spBankingStartAngle = sp.GetBankingAngle(vertex);
        CaptureSplineMirror(sp, vertex);
        s_spBankingPoint = sp.GetWorldVertexPos(vertex);
        s_spBezierStartEndpoint = sp.GetBankingHandleWorld(vertex, side);
        sp.ComputeBankingAxis(vertex, out Vector3 up, out Vector3 right);
        s_spBankingTangent = Vector3.Cross(sp.Transform.InverseTransformVector(right), sp.Transform.InverseTransformVector(up)).normalized;
        s_spCoordinates.Begin(s_spBezierStartEndpoint, mousePos, BankingCoordinates(sp, vertex, false), BankingCoordinates(sp, vertex, true));
        CaptureSplineFollowers(sp, SubKind.SplineBanking, vertex, "Bank Vertex");
    }

    /// <summary>
    /// When the lead vertex (or handle) is selected, gathers every other selected one of that kind
    /// to move with it. Call after s_spCoordinates.Begin, with s_spSide set for handle drags.
    /// </summary>
    static void CaptureSplineFollowers(GPSpline lead, SubKind kind, int vertex, string undoName)
    {
        s_spFollowers.Clear();
        s_spExtraSplines.Clear();
        var leadTarget = (GreyPrimitive)lead.Obj;
        if (!IsSubSelected(leadTarget, kind, vertex)) return;

        foreach (var element in s_subSelection)
        {
            if (element.Matches(leadTarget, kind, vertex)) continue;
            GPSpline sp = SplineFollowerBinding(lead, element.target, undoName);
            // Legitimate filter: a vertex removed since it was selected, a branch start (it stays
            // on its source road) and a pipe in a banking selection take no part.
            if (sp == null || element.index >= sp.Count || sp.IsAnchoredVertex(element.index)) continue;
            if (kind == SubKind.SplineBanking && !sp.HasBanking) continue;

            var entry = new SpFollower
            {
                sp = sp,
                vertex = element.index,
                startLocalPos = sp.GetLocalVertexPos(element.index),
                startEndpoint = sp.GetHandleWorldPosition(element.index, s_spSide),
                startBanking = sp.GetBankingAngle(element.index),
            };
            entry.follower.Begin(kind == SubKind.SplineBanking
                ? BankingCoordinates(sp, element.index, true) : SplineCoordinates(sp, element.index, true), ref s_spCoordinates);
            s_spFollowers.Add(entry);
        }
    }

    /// <summary>Binding for a follower's spline: the lead's own, or one per other spline (registered for undo once).</summary>
    static GPSpline SplineFollowerBinding(GPSpline lead, GreyPrimitive target, string undoName)
    {
        if (ReferenceEquals(target, lead.Obj)) return lead;
        foreach (var extra in s_spExtraSplines)
            if (ReferenceEquals(extra.Obj, target)) return extra;
        var binding = CreateSplineBinding(target);
        if (binding == null) return null; // not a spline primitive
        Undo.RecordObject(binding.Transform, undoName);
        Undo.RegisterCompleteObjectUndo(binding.Obj, undoName);
        s_spExtraSplines.Add(binding);
        return binding;
    }

    static void StartSplineExtend(GPSpline sp, int edge, Vector2 mousePos)
    {
        // Prepending a vertex shifts every vertex index.
        ClearSubSelection();
        Undo.IncrementCurrentGroup();
        s_spUndoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Transform, "Extend Spline");
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Extend Spline");
        sp.RegisterDependentsUndo("Extend Spline");
        Undo.SetCurrentGroupName("Extend Spline");

        s_spDrag = SpDrag.Extend;
        s_spExtendEdge = edge;
        s_spMirrorAxis = sp.MainAxisWorld;
        Vector3 edgeWorld = sp.GetWorldVertexPos(edge);
        var global = SplineCoordinates(sp, edge, false);
        var local = SplineCoordinates(sp, edge, true);
        sp.ExtendFromEdge(edge, sp.GetLocalVertexPos(edge));
        s_spVertex = edge == 0 ? 0 : sp.Count - 1;
        s_spStartLocalPos = sp.GetLocalVertexPos(s_spVertex);

        s_spCoordinates.Begin(edgeWorld, mousePos, global, local);
    }

    /// <summary>Snapshots the vertex at the mirrored index, which a symmetric drag changes along with <paramref name="vertex"/>.</summary>
    static void CaptureSplineMirror(GPSpline sp, int vertex)
    {
        s_spMirrorAxis = sp.MainAxisWorld;
        int mirror = sp.Count - 1 - vertex;
        s_spMirrorVertex = mirror == vertex || sp.IsAnchoredVertex(mirror) ? -1 : mirror;
        if (s_spMirrorVertex < 0) return;
        s_spMirrorStartLocalPos = sp.GetLocalVertexPos(mirror);
        s_spMirrorStartHandleDirLocal = sp.Transform.InverseTransformDirection(sp.GetHandleDirWorld(mirror));
        s_spMirrorStartHandleLength = sp.GetHandleLength(mirror);
        s_spMirrorStartBankingAngle = sp.GetBankingAngle(mirror);
    }

    /// <summary>
    /// True when this event must write the mirror vertex: with mirrored values while the drag is
    /// symmetric, or back to its start values on the first event after symmetry is switched off.
    /// </summary>
    static bool SplineMirrorNeedsWrite()
    {
        // Legitimate filter: the middle vertex of an odd count is its own mirror.
        if (s_spMirrorVertex < 0) return false;
        bool write = s_spCoordinates.symmetric || s_spMirrorApplied;
        s_spMirrorApplied = s_spCoordinates.symmetric;
        return write;
    }

    static void BeginSplineUndo(GPSpline sp, string name)
    {
        Undo.IncrementCurrentGroup();
        s_spUndoGroup = Undo.GetCurrentGroup();
        Undo.RecordObject(sp.Transform, name);
        Undo.RegisterCompleteObjectUndo(sp.Obj, name);
        Undo.SetCurrentGroupName(name);
    }

    // ─── Click actions ──────────────────────────────────────

    static void DeleteSplineVertex(GPSpline sp, int vertex)
    {
        Undo.IncrementCurrentGroup();
        int g = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Transform, "Delete Vertex");
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Delete Vertex");
        sp.RegisterDependentsUndo("Delete Vertex");
        if (sp.RemoveVertex(vertex))
        {
            sp.RecenterPivot();
            sp.Rebuild();
            EditorUtility.SetDirty(sp.Obj);
        }
        Undo.SetCurrentGroupName("Delete Vertex");
        Undo.CollapseUndoOperations(g);
    }

    /// <summary>
    /// Deletes the clicked vertex; when it is selected, every selected vertex. Clears the
    /// sub-selection: deleting shifts vertex indices.
    /// </summary>
    static void DeleteSplineVertices(GPSpline clicked, int vertex)
    {
        var target = (GreyPrimitive)clicked.Obj;
        if (!IsSubSelected(target, SubKind.SplineVertex, vertex))
        {
            DeleteSplineVertex(clicked, vertex);
            ClearSubSelection();
            return;
        }

        using var elementsScope = ListPool<SubElement>.Get(out var elements);
        elements.AddRange(s_subSelection);
        ClearSubSelection();
        // Highest index first, so a removal never shifts a vertex still to be removed.
        elements.Sort((a, b) => b.index.CompareTo(a.index));
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        foreach (var element in elements)
        {
            var sp = CreateSplineBinding(element.target);
            // Legitimate filter: a stale index, or a branch start, which cannot be deleted.
            if (sp == null || element.index >= sp.Count || sp.IsAnchoredVertex(element.index)) continue;
            DeleteSplineVertex(sp, element.index);
        }
        Undo.SetCurrentGroupName("Delete Vertices");
        Undo.CollapseUndoOperations(group);
    }

    /// <summary>
    /// Runs a per-vertex click action on the clicked vertex; when its handle of
    /// <paramref name="kind"/> is selected, on every selected one, as one undo step.
    /// </summary>
    static void ApplySplineClickAction(GPSpline clicked, SubKind kind, int vertex, System.Action<GPSpline, int> action)
    {
        var target = (GreyPrimitive)clicked.Obj;
        if (!IsSubSelected(target, kind, vertex))
        {
            action(clicked, vertex);
            return;
        }
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        foreach (var element in s_subSelection)
        {
            var sp = ReferenceEquals(element.target, target) ? clicked : CreateSplineBinding(element.target);
            if (sp == null || element.index >= sp.Count) continue; // stale index
            if (kind == SubKind.SplineBanking && (!sp.HasBanking || sp.IsAnchoredVertex(element.index))) continue;
            action(sp, element.index);
        }
        Undo.CollapseUndoOperations(group);
    }

    static void InsertSplineVertex(GPSpline sp, int seg, float t)
    {
        // Inserting shifts vertex indices.
        ClearSubSelection();
        Undo.IncrementCurrentGroup();
        int g = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Insert Vertex");
        sp.RegisterDependentsUndo("Insert Vertex");
        sp.InsertVertex(seg, t);
        sp.Rebuild();
        EditorUtility.SetDirty(sp.Obj);
        Undo.SetCurrentGroupName("Insert Vertex");
        Undo.CollapseUndoOperations(g);
    }

    static void ResetSplineHandle(GPSpline sp, int vertex)
    {
        Undo.IncrementCurrentGroup();
        int g = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Reset Handle");
        sp.ResetHandle(vertex);
        sp.Rebuild();
        EditorUtility.SetDirty(sp.Obj);
        Undo.SetCurrentGroupName("Reset Handle");
        Undo.CollapseUndoOperations(g);
    }

    static void ResetSplineBanking(GPSpline sp, int vertex)
    {
        Undo.IncrementCurrentGroup();
        int g = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Reset Banking");
        sp.ResetBanking(vertex);
        sp.Rebuild();
        EditorUtility.SetDirty(sp.Obj);
        Undo.SetCurrentGroupName("Reset Banking");
        Undo.CollapseUndoOperations(g);
    }

    // ─── Drag ───────────────────────────────────────────────────

    static void HandleSplineDrag(Event e, SceneView sv, GPSpline sp)
    {
        if (s_sp == null || s_sp.Obj == null) { ResetSplineDrag(); return; }

        // Events outside the window arrive as Ignore with the real type in rawType — honoring it
        // keeps the drag updating off-window and lets the release register wherever it happens.
        EventType type = e.type == EventType.Ignore ? e.rawType : e.type;

        if ((type == EventType.MouseDrag && e.button == s_spButton)
            || type == EventType.KeyDown || type == EventType.KeyUp)
        {
            ApplySplineDrag(e.mousePosition);
            // A branch or chamfer press changes nothing until it becomes a drag.
            if ((s_spDrag != SpDrag.Branch || s_spBranch != null) && (s_spDrag != SpDrag.Chamfer || s_spChamferCreated))
            {
                s_sp.Rebuild();
                EditorUtility.SetDirty(s_sp.Obj);
            }
            foreach (var extra in s_spExtraSplines)
            {
                extra.Rebuild();
                EditorUtility.SetDirty(extra.Obj);
            }
            sv.Repaint();
            e.Use();
            return;
        }

        if ((type == EventType.MouseUp && e.button == s_spButton) || !Enabled)
        {
            FinishSplineDrag();
            e.Use();
            return;
        }

        if (e.type == EventType.Repaint)
        {
            DrawSpline(s_sp, s_spDrag == SpDrag.VertexMove || s_spDrag == SpDrag.Extend || s_spDrag == SpDrag.Branch
                    || s_spDrag == SpDrag.AnchorSlide || s_spDrag == SpDrag.Chamfer ? s_spVertex : -1,
                s_spDrag == SpDrag.Bezier ? s_spVertex : -1, s_spSide,
                s_spDrag == SpDrag.Banking ? s_spVertex : -1, s_spSide, false, default);
            DrawSplineSymmetryHighlight();
        }
    }

    static void ApplySplineDrag(Vector2 mousePos)
    {
        switch (s_spDrag)
        {
            case SpDrag.VertexMove: ApplySplineVertexMove(mousePos); break;
            case SpDrag.Bezier:     ApplySplineBezier(mousePos);     break;
            case SpDrag.Banking:    ApplySplineBanking(mousePos);    break;
            case SpDrag.Extend:     ApplySplineExtend(mousePos);     break;
            case SpDrag.Branch:     ApplySplineBranch(mousePos);     break;
            case SpDrag.AnchorSlide: ApplyBranchAnchorSlide(mousePos); break;
            case SpDrag.Chamfer:    ApplySplineChamfer(mousePos);    break;
        }
    }

    static void ApplySplineVertexMove(Vector2 mousePos)
    {
        Vector3 delta = s_spCoordinates.Update(mousePos);
        var xform = s_sp.Transform;
        Vector3 newLocal = xform.InverseTransformPoint(xform.TransformPoint(s_spStartLocalPos) + delta);
        s_sp.SetLocalVertexPos(s_spVertex, newLocal);
        if (SplineMirrorNeedsWrite())
            s_sp.SetLocalVertexPos(s_spMirrorVertex, s_spCoordinates.symmetric
                ? xform.InverseTransformPoint(xform.TransformPoint(s_spMirrorStartLocalPos) + Vector3.Reflect(delta, s_spMirrorAxis))
                : s_spMirrorStartLocalPos);
        for (int i = 0; i < s_spFollowers.Count; i++)
        {
            var entry = s_spFollowers[i];
            Vector3 world = entry.follower.Update(ref s_spCoordinates);
            var t = entry.sp.Transform;
            entry.sp.SetLocalVertexPos(entry.vertex, t.InverseTransformPoint(t.TransformPoint(entry.startLocalPos) + world));
            s_spFollowers[i] = entry;
        }
        if (s_spDoReshape && s_spReshape != null) ApplyReshape(s_sp, s_spReshape);
    }

    /// <summary>
    /// Points a vertex's handle so its endpoint on <paramref name="side"/> sits at a world position.
    /// False (nothing set) when the endpoint is on the vertex. <paramref name="worldDir"/> is the
    /// handle vector toward higher vertex indices.
    /// </summary>
    static bool SetSplineHandleEndpoint(GPSpline sp, int vertex, int side, Vector3 endpointWorld, out Vector3 worldDir)
    {
        worldDir = endpointWorld - sp.GetWorldVertexPos(vertex);
        if (side < 0) worldDir = -worldDir;
        if (worldDir.magnitude < 0.0001f) return false;
        Vector3 localHandle = sp.Transform.InverseTransformVector(worldDir);
        sp.SetHandleDirLocal(vertex, localHandle.normalized);
        sp.SetHandleLength(vertex, Mathf.Max(0.001f, localHandle.magnitude));
        return true;
    }

    static void ApplySplineBezier(Vector2 mousePos)
    {
        Vector3 delta = s_spCoordinates.Update(mousePos);
        for (int i = 0; i < s_spFollowers.Count; i++)
        {
            var entry = s_spFollowers[i];
            Vector3 endpoint = entry.startEndpoint + entry.follower.Update(ref s_spCoordinates);
            SetSplineHandleEndpoint(entry.sp, entry.vertex, s_spSide, endpoint, out _);
            s_spFollowers[i] = entry;
        }
        if (!SetSplineHandleEndpoint(s_sp, s_spVertex, s_spSide, s_spBezierStartEndpoint + delta, out Vector3 worldDir)) return;

        if (!SplineMirrorNeedsWrite()) return;
        if (s_spCoordinates.symmetric)
        {
            // Vertex order runs the other way on the mirrored side, so the reflected handle is reversed.
            Vector3 mirrorHandle = s_sp.Transform.InverseTransformVector(-Vector3.Reflect(worldDir, s_spMirrorAxis));
            s_sp.SetHandleDirLocal(s_spMirrorVertex, mirrorHandle.normalized);
            s_sp.SetHandleLength(s_spMirrorVertex, Mathf.Max(0.001f, mirrorHandle.magnitude));
        }
        else
        {
            s_sp.SetHandleDirLocal(s_spMirrorVertex, s_spMirrorStartHandleDirLocal);
            s_sp.SetHandleLength(s_spMirrorVertex, s_spMirrorStartHandleLength);
        }
    }

    static void ApplySplineBanking(Vector2 mousePos)
    {
        Vector3 desired = s_spBezierStartEndpoint + s_spCoordinates.Update(mousePos);
        Vector3 from = s_sp.Transform.InverseTransformVector(s_spBezierStartEndpoint - s_spBankingPoint);
        Vector3 to = s_sp.Transform.InverseTransformVector(desired - s_spBankingPoint);
        from = Vector3.ProjectOnPlane(from, s_spBankingTangent);
        to = Vector3.ProjectOnPlane(to, s_spBankingTangent);
        if (from.sqrMagnitude < 0.000001f || to.sqrMagnitude < 0.000001f) return;
        float angle = Vector3.SignedAngle(from, to, s_spBankingTangent);
        s_sp.SetBankingAngle(s_spVertex, s_spBankingStartAngle + angle * Mathf.Deg2Rad);
        foreach (var entry in s_spFollowers)
            entry.sp.SetBankingAngle(entry.vertex, entry.startBanking + angle * Mathf.Deg2Rad);
        // The reversed vertex order and the reflection cancel out: the same angle change banks
        // the mirror vertex toward the same world side.
        if (SplineMirrorNeedsWrite())
            s_sp.SetBankingAngle(s_spMirrorVertex,
                s_spMirrorStartBankingAngle + (s_spCoordinates.symmetric ? angle * Mathf.Deg2Rad : 0f));
    }

    static void ApplySplineExtend(Vector2 mousePos)
    {
        Vector3 delta = s_spCoordinates.Update(mousePos);
        SyncSplineMirrorExtend();
        MoveExtendedSplineVertex(s_spVertex, s_spStartLocalPos, delta);
        if (s_spMirrorExtended)
            MoveExtendedSplineVertex(SplineMirrorExtendVertex, s_spMirrorStartLocalPos, Vector3.Reflect(delta, s_spMirrorAxis));
    }

    static void MoveExtendedSplineVertex(int vertex, Vector3 startLocalPos, Vector3 worldDelta)
    {
        var xform = s_sp.Transform;
        Vector3 newLocal = xform.InverseTransformPoint(xform.TransformPoint(startLocalPos) + worldDelta);
        s_sp.SetLocalVertexPos(vertex, newLocal);

        if ((newLocal - startLocalPos).sqrMagnitude > 0.001f)
        {
            s_sp.ResetHandle(vertex);
        }
    }

    // ─── Chamfer ────────────────────────────────────────────────

    static void StartSplineChamfer(int vertex, Vector2 mousePos)
    {
        s_spDrag = SpDrag.Chamfer;
        s_spVertex = vertex;
        s_spChamferCreated = false;
        s_spChamferPress = mousePos;
    }

    /// <summary>
    /// Chamfers the pressed inner vertex: it and a second vertex slide apart along the original
    /// curve, toward the neighbours on either side, by the distance the cursor moved from the
    /// press. Their handles are refit to their neighbours on every event and their banking is
    /// blended toward the neighbour each one approaches, so the corner stays one smooth curve.
    /// </summary>
    static void ApplySplineChamfer(Vector2 mousePos)
    {
        float pixels = Vector2.Distance(mousePos, s_spChamferPress);
        if (!s_spChamferCreated)
        {
            if (pixels < GPEditShared.DragStartPx) return;
            CreateSplineChamfer();
            // Legitimate filter: the spline refused the second vertex.
            if (!s_spChamferCreated) return;
        }

        float distance = pixels * s_spChamferUnitsPerPixel;
        float before = ChamferFraction(s_spChamferBefore, distance), after = ChamferFraction(s_spChamferAfter, distance);
        int first = s_spVertex, second = s_spVertex + 1;
        var xform = s_sp.Transform;
        s_sp.SetLocalVertexPos(first, xform.InverseTransformPoint(BezierPoint(s_spChamferBefore, 1f - before)));
        s_sp.SetLocalVertexPos(second, xform.InverseTransformPoint(BezierPoint(s_spChamferAfter, after)));
        s_sp.ResetHandle(first);
        s_sp.ResetHandle(second);
        if (s_sp.HasBanking)
        {
            s_sp.SetBankingAngle(first, LerpBanking(s_spChamferBank, s_spChamferBankBefore, before));
            s_sp.SetBankingAngle(second, LerpBanking(s_spChamferBank, s_spChamferBankAfter, after));
        }
    }

    /// <summary>Captures the original curve around the vertex and adds the second vertex on top of it.</summary>
    static void CreateSplineChamfer()
    {
        const string label = "Chamfer Vertex";
        var sp = s_sp;
        int vertex = s_spVertex;
        // The vertex can be gone, or have become an end, after an undo during the press.
        if (vertex <= 0 || vertex >= sp.Count - 1) return;

        sp.GetSegmentControlPointsWorld(vertex - 1, out s_spChamferBefore[0], out s_spChamferBefore[1], out s_spChamferBefore[2], out s_spChamferBefore[3]);
        sp.GetSegmentControlPointsWorld(vertex, out s_spChamferAfter[0], out s_spChamferAfter[1], out s_spChamferAfter[2], out s_spChamferAfter[3]);
        s_spChamferBank = sp.GetBankingAngle(vertex);
        s_spChamferBankBefore = sp.GetBankingAngle(vertex - 1);
        s_spChamferBankAfter = sp.GetBankingAngle(vertex + 1);
        // GetHandleSize is the world size of about 80 screen pixels at that point.
        s_spChamferUnitsPerPixel = HandleUtility.GetHandleSize(sp.GetWorldVertexPos(vertex)) / 80f;

        Undo.IncrementCurrentGroup();
        s_spUndoGroup = Undo.GetCurrentGroup();
        Undo.RecordObject(sp.Transform, label);
        Undo.RegisterCompleteObjectUndo(sp.Obj, label);
        sp.RegisterDependentsUndo(label);
        Undo.SetCurrentGroupName(label);
        // The new vertex shifts vertex indices.
        ClearSubSelection();

        int count = sp.Count;
        sp.InsertVertex(vertex, 0.001f);
        s_spChamferCreated = sp.Count == count + 1;
    }

    /// <summary>How far along a segment (0-0.9 of it) a chamfered vertex sits for a slide distance in world units.</summary>
    static float ChamferFraction(Vector3[] segment, float distance)
        => Mathf.Clamp(distance / Mathf.Max(0.0001f, Vector3.Distance(segment[0], segment[3])), 0f, 0.9f);

    static Vector3 BezierPoint(Vector3[] p, float t)
    {
        float u = 1f - t;
        return u * u * u * p[0] + 3f * u * u * t * p[1] + 3f * u * t * t * p[2] + t * t * t * p[3];
    }

    /// <summary>Banking angle (radians) blended from <paramref name="from"/> toward <paramref name="to"/> along the shorter way round.</summary>
    static float LerpBanking(float from, float to, float t)
        => Mathf.LerpAngle(from * Mathf.Rad2Deg, to * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;

    /// <summary>Index of the vertex a symmetric extend holds at the end opposite the dragged one.</summary>
    static int SplineMirrorExtendVertex => s_spExtendEdge == 0 ? s_sp.Count - 1 : 0;

    /// <summary>Keeps an extra vertex at the other end exactly while an extend drag is symmetric.</summary>
    static void SyncSplineMirrorExtend()
    {
        if (s_spCoordinates.symmetric == s_spMirrorExtended) return;
        // Legitimate filter: an anchored branch start cannot be extended.
        if (!s_spMirrorExtended && s_sp.IsAnchoredVertex(SplineMirrorExtendVertex)) return;
        if (s_spMirrorExtended)
            s_sp.RemoveVertex(SplineMirrorExtendVertex);
        else
        {
            int otherEnd = SplineMirrorExtendVertex;
            s_spMirrorStartLocalPos = s_sp.GetLocalVertexPos(otherEnd);
            s_sp.ExtendFromEdge(otherEnd, s_spMirrorStartLocalPos);
        }
        s_spMirrorExtended = !s_spMirrorExtended;
        // Adding or removing a vertex at index 0 shifts the dragged end vertex.
        s_spVertex = s_spExtendEdge == 0 ? 0 : s_sp.Count - 1;
    }

    static void FinishSplineDrag()
    {
        if (s_spDrag == SpDrag.Branch && s_spBranch == null)
        {
            FinishSplineBranchClick();
            return;
        }
        if (s_spDrag == SpDrag.Chamfer && !s_spChamferCreated)
        {
            // A press on an inner vertex that never became a drag changed nothing.
            GUIUtility.hotControl = 0;
            ResetSplineDrag();
            return;
        }
        bool moved = s_spDrag == SpDrag.VertexMove || s_spDrag == SpDrag.Extend || s_spDrag == SpDrag.Branch
            || s_spDrag == SpDrag.AnchorSlide || s_spDrag == SpDrag.Chamfer;
        GameObject branch = s_spDrag == SpDrag.Branch ? s_spBranch.gameObject : null;
        if (moved && s_sp != null && s_sp.Obj != null)
        {
            Undo.RecordObject(s_sp.Transform, "Recenter");
            Undo.RecordObject(s_sp.Obj, "Recenter");
            s_sp.RecenterPivot();
            s_sp.Rebuild();
            EditorUtility.SetDirty(s_sp.Obj);
            EditorUtility.SetDirty(s_sp.Transform);
        }
        if (moved)
            foreach (var extra in s_spExtraSplines)
            {
                if (extra.Obj == null) continue; // deleted by an undo during the drag
                Undo.RecordObject(extra.Transform, "Recenter");
                Undo.RecordObject(extra.Obj, "Recenter");
                extra.RecenterPivot();
                extra.Rebuild();
                EditorUtility.SetDirty(extra.Obj);
                EditorUtility.SetDirty(extra.Transform);
            }
        Undo.CollapseUndoOperations(s_spUndoGroup);
        GUIUtility.hotControl = 0;
        ResetSplineDrag();
        if (branch != null) Selection.activeGameObject = branch;
    }

    static void ResetSplineDrag()
    {
        s_spDrag = SpDrag.None;
        s_sp = null;
        s_spReshape = null;
        s_spDoReshape = false;
        s_spMirrorVertex = -1;
        s_spMirrorApplied = false;
        s_spMirrorExtended = false;
        s_spBranch = null;
        s_spBranchSource = null;
        s_spFollowers.Clear();
        s_spExtraSplines.Clear();
        s_spChamferCreated = false;
    }

    // ─── Edge-vertex reshape (interior vertices follow the moved end) ────

    /// <summary>Snapshot interior vertices in edge-axis-relative coords (perpX, perpY, fractionAlongAxis).</summary>
    static Vector3[] CaptureReshape(GPSpline sp)
    {
        int n = sp.Count;
        var result = new Vector3[n];
        Vector3 origin = sp.GetLocalVertexPos(0);
        Vector3 axis = sp.GetLocalVertexPos(n - 1) - origin;
        float axisLen = axis.magnitude;
        if (axisLen < 0.0001f) return result;
        Quaternion frameInv = Quaternion.Inverse(EdgeFrame(axis / axisLen));
        for (int i = 0; i < n; i++)
        {
            Vector3 framed = frameInv * (sp.GetLocalVertexPos(i) - origin);
            result[i] = new Vector3(framed.x, framed.y, framed.z / axisLen);
        }
        return result;
    }

    static void ApplyReshape(GPSpline sp, Vector3[] snapshot)
    {
        int n = sp.Count;
        if (snapshot == null || snapshot.Length != n) return;
        Vector3 origin = sp.GetLocalVertexPos(0);
        Vector3 axis = sp.GetLocalVertexPos(n - 1) - origin;
        float axisLen = axis.magnitude;
        if (axisLen < 0.0001f) return;
        Quaternion frame = EdgeFrame(axis / axisLen);
        for (int i = 1; i < n - 1; i++)
        {
            Vector3 rel = snapshot[i];
            Vector3 framed = new Vector3(rel.x, rel.y, rel.z * axisLen);
            sp.SetLocalVertexPos(i, origin + frame * framed);
        }
    }

    static Quaternion EdgeFrame(Vector3 forward)
    {
        Vector3 up = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.99f) up = Vector3.right;
        return Quaternion.LookRotation(forward, up);
    }

    // ─── Draw ───────────────────────────────────────────────────

    static void DrawSpline(GPSpline sp, int hoverVertex, int hoverBezier, int hoverBezierSide, int hoverBank, int hoverBankSide,
        bool previewVertex, Vector3 previewPos)
    {
        // Spline curve: two depth-tested passes — hidden-behind-geometry parts faded, visible
        // parts full color. DrawBezier honors Handles.zTest; the solid handle caps below do NOT,
        // so their occlusion is raycast per point instead.
        var prevZTest = Handles.zTest;
        for (int pass = 0; pass < 2; pass++)
        {
            bool curveOccluded = pass == 0;
            Handles.zTest = curveOccluded
                ? UnityEngine.Rendering.CompareFunction.Greater
                : UnityEngine.Rendering.CompareFunction.LessEqual;
            Color splineColor = curveOccluded ? GPEditShared.Occluded(sp.SplineColor) : sp.SplineColor;
            for (int seg = 0; seg < sp.SegmentCount; seg++)
            {
                sp.GetSegmentControlPointsWorld(seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
                Handles.DrawBezier(p0, p3, p1, p2, splineColor, null, 2f);
            }
        }
        Handles.zTest = prevZTest;

        // Preview of the vertex RMB would insert at the hovered spline point (DrawDot is always-on-top).
        if (previewVertex)
            GPEditShared.DrawDot(previewPos, GPEditShared.NewVertex, 0.06f);

        Transform self = sp.Transform;
        var target = (GreyPrimitive)sp.Obj;
        for (int i = 0; i < sp.Count; i++)
        {
            Vector3 vw = sp.GetWorldVertexPos(i);
            float hs = HandleUtility.GetHandleSize(vw) * 0.08f;
            bool isEnd = i == 0 || i == sp.Count - 1;

            bool anchored = sp.IsAnchoredVertex(i);
            bool vertexSelected = IsSubSelected(target, SubKind.SplineVertex, i);
            if (vertexSelected) hs *= 1.5f;
            Color vColor = vertexSelected ? GPEditShared.Selected
                : i == hoverVertex
                ? (s_spDrag == SpDrag.Extend || s_spDrag == SpDrag.Branch || s_spDrag == SpDrag.Chamfer ? GPEditShared.Create : GPEditShared.VertexHover)
                : anchored ? GPEditShared.Anchor : GPEditShared.Vertex;
            Handles.color = GPEditShared.IsWorldPointOccluded(vw, self) ? GPEditShared.Occluded(vColor) : vColor;
            if (anchored)
            {
                // Branch start: a sphere in a ring, unlike the cube of a free vertex.
                Vector3 view = Camera.current != null ? Camera.current.transform.forward : Vector3.forward;
                Handles.SphereHandleCap(0, vw, Quaternion.identity, hs * 2.2f, EventType.Repaint);
                Handles.DrawWireDisc(vw, view, hs * 1.8f, 2f);
            }
            else if (isEnd) Handles.CubeHandleCap(0, vw, Quaternion.identity, hs * 2f, EventType.Repaint);
            else            Handles.CubeHandleCap(0, vw, Quaternion.identity, hs * 1.2f, EventType.Repaint);

            // Bezier handle
            Vector3 a = sp.GetHandleWorldPosition(i, +1), b = sp.GetHandleWorldPosition(i, -1);
            bool occA = GPEditShared.IsWorldPointOccluded(a, self);
            bool occB = GPEditShared.IsWorldPointOccluded(b, self);
            // Line color follows both endpoints — faded only when the whole handle is hidden.
            Handles.color = occA && occB ? GPEditShared.Occluded(GPEditShared.Bezier) : GPEditShared.Bezier;
            Handles.DrawLine(a, vw);
            Handles.DrawLine(vw, b);
            float dot = hs * 0.8f;
            DrawBezierDot(a, i, +1, hoverBezier, hoverBezierSide, dot, occA);
            DrawBezierDot(b, i, -1, hoverBezier, hoverBezierSide, dot, occB);
            if (IsSubSelected(target, SubKind.SplineBezier, i))
            {
                // A vertex has one handle value for both sides, so both dots show the selection.
                Handles.color = GPEditShared.Selected;
                Handles.DotHandleCap(0, a, Quaternion.identity, dot * 1.6f, EventType.Repaint);
                Handles.DotHandleCap(0, b, Quaternion.identity, dot * 1.6f, EventType.Repaint);
            }

            // Banking handle (road)
            if (sp.HasBanking)
            {
                Vector3 ba = sp.GetBankingHandleWorld(i, +1), bb = sp.GetBankingHandleWorld(i, -1);
                bool occBa = GPEditShared.IsWorldPointOccluded(ba, self);
                bool occBb = GPEditShared.IsWorldPointOccluded(bb, self);
                Handles.color = occBa && occBb ? GPEditShared.Occluded(GPEditShared.Banking) : GPEditShared.Banking;
                Handles.DrawLine(ba, vw);
                Handles.DrawLine(vw, bb);
                float bdot = hs * 0.55f;
                DrawBankingDot(ba, i, +1, hoverBank, hoverBankSide, bdot, occBa);
                DrawBankingDot(bb, i, -1, hoverBank, hoverBankSide, bdot, occBb);
                if (IsSubSelected(target, SubKind.SplineBanking, i))
                {
                    Handles.color = GPEditShared.Selected;
                    Handles.DotHandleCap(0, ba, Quaternion.identity, bdot * 1.6f, EventType.Repaint);
                    Handles.DotHandleCap(0, bb, Quaternion.identity, bdot * 1.6f, EventType.Repaint);
                }
            }
        }
        int hovered = hoverVertex >= 0 ? hoverVertex : hoverBezier >= 0 ? hoverBezier : hoverBank;
        // A branch start slides along its source road; it shows only the Ctrl sideways arrow.
        if (hoverVertex >= 0 && sp.IsAnchoredVertex(hoverVertex) && Event.current.control)
            DrawBranchOffsetArrow((Greyroad)sp.Obj);
        if (hovered >= 0 && !(hoverVertex >= 0 && sp.IsAnchoredVertex(hoverVertex)))
        {
            Vector3 point = hoverVertex >= 0 ? sp.GetWorldVertexPos(hovered)
                : hoverBezier >= 0 ? sp.GetHandleWorldPosition(hovered, hoverBezierSide)
                : sp.GetBankingHandleWorld(hovered, hoverBankSide);
            bool local = Tools.pivotRotation == PivotRotation.Local;
            if (s_spDrag != SpDrag.None && s_sp != null && s_sp.Obj == sp.Obj)
                s_spCoordinates.Draw(point);
            else DrawCoordinates(point, hoverVertex < 0 && hoverBezier < 0
                ? BankingCoordinates(sp, hovered, local) : SplineCoordinates(sp, hovered, local));
        }
    }

    /// <summary>Marks the vertex or handle a symmetric drag changes along with the dragged one.</summary>
    static void DrawSplineSymmetryHighlight()
    {
        if (!s_spCoordinates.symmetric) return;
        if (s_spDrag == SpDrag.Extend)
        {
            if (s_spMirrorExtended)
                GPEditShared.DrawDot(s_sp.GetWorldVertexPos(SplineMirrorExtendVertex), GPEditShared.Create, 0.07f);
            return;
        }
        // Legitimate filter: the middle vertex of an odd count is its own mirror.
        if (s_spMirrorVertex < 0) return;
        switch (s_spDrag)
        {
            case SpDrag.VertexMove:
                GPEditShared.DrawDot(s_sp.GetWorldVertexPos(s_spMirrorVertex), GPEditShared.VertexHover, 0.07f);
                break;
            case SpDrag.Bezier: // vertex order is reversed on the mirrored side, so the handle side flips
                GPEditShared.DrawDot(s_sp.GetHandleWorldPosition(s_spMirrorVertex, -s_spSide), GPEditShared.VertexHover, 0.06f);
                break;
            case SpDrag.Banking:
                GPEditShared.DrawDot(s_sp.GetBankingHandleWorld(s_spMirrorVertex, s_spSide), GPEditShared.VertexHover, 0.05f);
                break;
        }
    }

    static void DrawBezierDot(Vector3 p, int i, int side, int hoverV, int hoverSide, float size, bool occluded)
    {
        bool hot = hoverV == i && hoverSide == side;
        Color c = hot ? GPEditShared.VertexHover : GPEditShared.Bezier;
        Handles.color = occluded ? GPEditShared.Occluded(c) : c;
        Handles.DotHandleCap(0, p, Quaternion.identity, hot ? size * 1.4f : size, EventType.Repaint);
    }

    static void DrawBankingDot(Vector3 p, int i, int side, int hoverV, int hoverSide, float size, bool occluded)
    {
        bool hot = hoverV == i && hoverSide == side;
        Color c = hot ? GPEditShared.VertexHover : GPEditShared.Banking;
        Handles.color = occluded ? GPEditShared.Occluded(c) : c;
        Handles.DotHandleCap(0, p, Quaternion.identity, hot ? size * 1.4f : size, EventType.Repaint);
    }
}

// ─── Adapters ───────────────────────────────────────────────────

/// <summary>Editor-side facade over a Greypipe or Greyroad so the spline edit logic is written once.</summary>
abstract class GPSpline
{
    public abstract Transform Transform { get; }
    public abstract Object Obj { get; }
    public abstract int Count { get; }
    public abstract int SegmentCount { get; }
    public abstract Vector3 MainAxisWorld { get; }
    /// <summary>Normal of the pipe main plane used by Local coordinates.</summary>
    public abstract Vector3 MovePlaneNormal { get; }
    public abstract Color SplineColor { get; }

    public abstract Vector3 GetWorldVertexPos(int i);
    public abstract Vector3 GetLocalVertexPos(int i);
    public abstract void SetLocalVertexPos(int i, Vector3 local);
    public abstract Vector3 GetHandleDirWorld(int i);
    public abstract void SetHandleDirLocal(int i, Vector3 dir);
    public abstract float GetHandleLength(int i);
    public abstract void SetHandleLength(int i, float len);
    public Vector3 GetHandleWorldPosition(int i, int side)
    {
        Vector3 localDir = Transform.InverseTransformDirection(GetHandleDirWorld(i));
        return GetWorldVertexPos(i) + Transform.TransformVector(localDir * (GetHandleLength(i) * side));
    }
    public abstract void ResetHandle(int i);

    /// <summary>Registers undo for other objects a vertex insert, delete or extend also changes.</summary>
    public virtual void RegisterDependentsUndo(string name) { }

    /// <summary>True for a vertex whose position is owned by another object (a branch road's first vertex).</summary>
    public virtual bool IsAnchoredVertex(int i) => false;

    public abstract void InsertVertex(int seg, float t);
    public abstract void ExtendFromEdge(int i, Vector3 localPos);
    public abstract bool RemoveVertex(int i);
    public abstract void RecenterPivot();
    public abstract void Rebuild();

    public abstract Vector3 EvaluateSplineWorld(int seg, float t);
    public abstract void GetSegmentControlPointsWorld(int seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);

    public virtual bool HasBanking => false;
    public virtual Vector3 GetBankingHandleWorld(int i, int side) => Vector3.zero;
    public virtual void ComputeBankingAxis(int i, out Vector3 up, out Vector3 right) { up = Vector3.up; right = Vector3.right; }
    public virtual float GetBankingAngle(int i) => 0f;
    public virtual void SetBankingAngle(int i, float rad) { }
    public virtual void ResetBanking(int i) { }
}

sealed class GPSplinePipe : GPSpline
{
    Greypipe _p;
    public void Set(Greypipe p) => _p = p;

    public override Transform Transform => _p.transform;
    public override Object Obj => _p;
    public override int Count => _p.Vertices.Count;
    public override int SegmentCount => _p.SegmentCount;
    public override Vector3 MainAxisWorld => _p.MainAxisWorld;
    // Pipe: orthogonal axis is sideways — perpendicular to both the main axis and world up.
    public override Vector3 MovePlaneNormal
    {
        get
        {
            Vector3 n = Vector3.Cross(_p.MainAxisWorld, Vector3.up).normalized;
            if (n.sqrMagnitude < 0.01f) n = Vector3.Cross(_p.MainAxisWorld, Vector3.right).normalized;
            return n;
        }
    }
    public override Color SplineColor => GPEditShared.Spline;

    public override Vector3 GetWorldVertexPos(int i) => _p.GetWorldVertexPosition(i);
    public override Vector3 GetLocalVertexPos(int i) => _p.Vertices[i].position;
    public override void SetLocalVertexPos(int i, Vector3 local)
    { _p.SetVertexPosition(i, local); }
    public override Vector3 GetHandleDirWorld(int i) => _p.GetVertexHandleDirWorld(i);
    public override void SetHandleDirLocal(int i, Vector3 dir) => _p.SetVertexHandleDirLocal(i, dir);
    public override float GetHandleLength(int i) => _p.Vertices[i].handleLength;
    public override void SetHandleLength(int i, float len) => _p.SetVertexHandleLength(i, len);
    public override void ResetHandle(int i) => _p.ResetVertexHandle(i);

    public override void InsertVertex(int seg, float t) => _p.InsertVertex(seg, t);
    public override void ExtendFromEdge(int i, Vector3 localPos) => _p.ExtendFromEdge(i, localPos);
    public override bool RemoveVertex(int i) => _p.RemoveVertex(i);
    public override void RecenterPivot() => _p.RecenterPivot();
    public override void Rebuild() => _p.RebuildMesh();

    public override Vector3 EvaluateSplineWorld(int seg, float t) => _p.EvaluateSplineWorld(seg, t);
    public override void GetSegmentControlPointsWorld(int seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3)
    {
        _p.GetSegmentControlPoints(seg, out Vector3 l0, out Vector3 l1, out Vector3 l2, out Vector3 l3);
        var t = _p.transform;
        p0 = t.TransformPoint(l0); p1 = t.TransformPoint(l1); p2 = t.TransformPoint(l2); p3 = t.TransformPoint(l3);
    }
}

sealed class GPSplineRoad : GPSpline
{
    Greyroad _r;
    public void Set(Greyroad r) => _r = r;

    public override Transform Transform => _r.transform;
    public override Object Obj => _r;
    public override int Count => _r.Vertices.Count;
    public override int SegmentCount => _r.SegmentCount;
    public override Vector3 MainAxisWorld => _r.MainAxisWorld;
    // Global road coordinates use world XZ/up; Local coordinates use the vertex tangent and bank.
    public override Vector3 MovePlaneNormal => Vector3.up;
    public override Color SplineColor => new Color(0.4f, 1f, 0.7f, 0.9f);

    public override Vector3 GetWorldVertexPos(int i) => _r.GetWorldVertexPosition(i);
    public override Vector3 GetLocalVertexPos(int i) => _r.Vertices[i].position;
    public override void SetLocalVertexPos(int i, Vector3 local)
    { var v = _r.Vertices[i]; v.position = local; _r.Vertices[i] = v; }
    public override Vector3 GetHandleDirWorld(int i) => _r.GetVertexHandleDirWorld(i);
    public override void SetHandleDirLocal(int i, Vector3 dir) => _r.SetVertexHandleDirLocal(i, dir);
    public override float GetHandleLength(int i) => _r.Vertices[i].handleLength;
    public override void SetHandleLength(int i, float len) => _r.SetVertexHandleLength(i, len);
    public override void ResetHandle(int i) => _r.ResetVertexHandle(i);

    // Vertex count changes shift the ranges of the road's ledges and the anchors of its branches.
    public override void RegisterDependentsUndo(string name)
    {
        using var roadsScope = ListPool<Greyroad>.Get(out var roads);
        _r.GetLedges(roads);
        foreach (var ledge in roads) Undo.RegisterCompleteObjectUndo(ledge, name);
        _r.GetBranches(roads);
        foreach (var branch in roads) Undo.RegisterCompleteObjectUndo(branch, name);
    }

    public override bool IsAnchoredVertex(int i) => i == 0 && _r.IsBranch;

    public override void InsertVertex(int seg, float t) => _r.InsertVertex(seg, t);
    public override void ExtendFromEdge(int i, Vector3 localPos) => _r.ExtendFromEdge(i, localPos);
    public override bool RemoveVertex(int i) => _r.RemoveVertex(i);
    public override void RecenterPivot() => _r.RecenterPivot();
    public override void Rebuild() => _r.RebuildMesh();

    public override Vector3 EvaluateSplineWorld(int seg, float t) => _r.EvaluateSplineWorld(seg, t);
    public override void GetSegmentControlPointsWorld(int seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3)
    {
        _r.GetSegmentControlPoints(seg, out Vector3 l0, out Vector3 l1, out Vector3 l2, out Vector3 l3);
        var t = _r.transform;
        p0 = t.TransformPoint(l0); p1 = t.TransformPoint(l1); p2 = t.TransformPoint(l2); p3 = t.TransformPoint(l3);
    }

    public override bool HasBanking => true;
    public override Vector3 GetBankingHandleWorld(int i, int side) => _r.GetBankingHandleWorld(i, side);
    public override void ComputeBankingAxis(int i, out Vector3 up, out Vector3 right)
        => _r.ComputeBankingHandleAxisWorld(i, out up, out right);
    public override float GetBankingAngle(int i) => _r.GetVertexBankingAngle(i);
    public override void SetBankingAngle(int i, float rad) => _r.SetVertexBankingAngle(i, rad);
    public override void ResetBanking(int i) => _r.ResetVertexBanking(i);
}
#endif
