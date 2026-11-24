#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;

/// <summary>
/// Ledges: Greyroads that follow one edge of a source Greyroad (see the ledge link on
/// <see cref="Greyroad"/>). RMB-drag along a road's edge creates a ledge over the dragged stretch.
/// A selected ledge shows two end handles that slide along the source edge, a handle per face
/// (LMB drag = resize, Ctrl + LMB on top / bottom = tilt, MMB = hide / show) and splits in two on an
/// RMB click on its top line.
/// </summary>
static partial class GPEdit
{
    enum LdDrag { None, Create, End, Face }
    enum LdHoverKind { None, End, Face, Body }

    struct LedgeHover
    {
        public LdHoverKind kind;
        public int face;       // Greyroad face index of the hovered handle (End and Face)
        public float param;    // Body: source path parameter under the cursor
        public Vector3 point;  // Body: world point under the cursor
        public float distance; // screen px from the cursor
    }

    const float k_MinLedgeSize = 0.01f;

    static LdDrag   s_ldDrag;
    static Greyroad s_ldTarget;      // dragged ledge; unset while a Create drag has not passed its drag threshold
    static Greyroad s_ldSource;
    static int      s_ldButton;
    static int      s_ldControl;
    static int      s_ldUndoGroup;
    static int      s_ldSide;        // Create: followed edge of the source
    static float    s_ldAnchorParam; // Create: source path parameter under the press
    static Vector2  s_ldPress;
    static int      s_ldFace;        // End and Face: Greyroad face index of the dragged handle
    static Vector3  s_ldFacePoint;
    static Vector3  s_ldFaceNormal;
    static float    s_ldFaceStartDist;
    static float    s_ldStartWidth, s_ldStartHeight, s_ldStartInset, s_ldStartRise;

    // Ctrl + top / bottom face drag: the ledge turns around the centre line of the opposite face.
    static bool     s_ldTilting;
    static bool     s_ldTiltPivotTop;  // pivot is the top centre line (bottom face dragged)
    static Vector3  s_ldHinge;
    static Vector3  s_ldHingeAxis;
    static Vector3  s_ldTiltFrom;      // hinge to the pressed point, in the plane across the ledge
    static Vector2  s_ldTiltPivot;     // pivot in the cross-section, from the source edge (x = outward, y = up)
    static float    s_ldStartTilt;

    /// <summary>One more selected ledge face moved by a face drag, with its ledge's values at drag start.</summary>
    struct LdFollower
    {
        public Greyroad ledge;
        public int face;
        public float width, height, inset, rise;
    }

    static readonly List<LdFollower> s_ldFollowers = new List<LdFollower>(8);

    static readonly List<Greyroad.PathFrame> s_ldFrames = new List<Greyroad.PathFrame>(128);
    static readonly List<Greyroad.PathFrame> s_ldSourceFrames = new List<Greyroad.PathFrame>(128);
    static Vector3[] s_ldLine = new Vector3[128];

    // ─── Geometry ───────────────────────────────────────────────

    /// <summary>World point of a path frame's top line: 0 = centre, +1 / -1 = right / left edge.</summary>
    static Vector3 PathPoint(in Greyroad.PathFrame frame, int side) => frame.position + frame.halfRight * side;

    /// <summary>
    /// Screen distance from the cursor to the polyline along the frames' top line, with the source
    /// path parameter and world point of the nearest spot.
    /// </summary>
    static float NearestOnPath(List<Greyroad.PathFrame> frames, int side, Vector2 mouse, out float param, out Vector3 point)
    {
        float best = float.MaxValue;
        param = 0f;
        point = default;
        for (int i = 0; i < frames.Count - 1; i++)
        {
            Vector3 a = PathPoint(frames[i], side), b = PathPoint(frames[i + 1], side);
            if (!GPEditShared.ClipSegmentToCameraFront(ref a, ref b)) continue;
            Vector2 sa = HandleUtility.WorldToGUIPoint(a), sb = HandleUtility.WorldToGUIPoint(b);
            Vector2 ab = sb - sa;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq < 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(mouse - sa, ab) / lenSq);
            float distance = Vector2.Distance(mouse, sa + ab * t);
            if (distance >= best) continue;
            best = distance;
            point = Vector3.Lerp(a, b, t);
            param = Mathf.Lerp(frames[i].param, frames[i + 1].param, t);
        }
        return best;
    }

    /// <summary>World position and outward normal of a ledge face's handle. Expects at least two frames.</summary>
    static Vector3 LedgeFacePoint(List<Greyroad.PathFrame> frames, int face, out Vector3 normal)
    {
        var first = frames[0];
        var last = frames[frames.Count - 1];
        var mid = frames[frames.Count / 2];
        switch (face)
        {
            case Greyroad.FaceTop:
                normal = -mid.drop.normalized;
                return mid.position;
            case Greyroad.FaceBottom:
                normal = mid.drop.normalized;
                return mid.position + mid.drop;
            case Greyroad.FaceLeftSide:
                normal = -mid.halfRight.normalized;
                return mid.position - mid.halfRight + mid.drop * 0.5f;
            case Greyroad.FaceRightSide:
                normal = mid.halfRight.normalized;
                return mid.position + mid.halfRight + mid.drop * 0.5f;
            case Greyroad.FaceStartCap:
                normal = -first.tangent;
                return first.position + first.drop * 0.5f;
            default:
                normal = last.tangent;
                return last.position + last.drop * 0.5f;
        }
    }

    /// <summary>Face of the ledge that touches its source road.</summary>
    static int LedgeInnerFace(Greyroad ledge) => ledge.LedgeSide > 0 ? Greyroad.FaceLeftSide : Greyroad.FaceRightSide;

    // ─── Hover ──────────────────────────────────────────────────

    /// <summary>Nearest ledge control under the cursor. Leaves the ledge's world frames in s_ldFrames.</summary>
    static LedgeHover HitLedge(Greyroad ledge, Vector2 mouse)
    {
        var hover = new LedgeHover { distance = float.MaxValue };
        ledge.GetPathFramesWorld(s_ldFrames);
        if (s_ldFrames.Count < 2) return hover; // source has no path to follow

        for (int face = 0; face < 6; face++)
        {
            float distance = Vector2.Distance(mouse, HandleUtility.WorldToGUIPoint(LedgeFacePoint(s_ldFrames, face, out _)));
            if (distance >= GPEditShared.HandlePx || distance >= hover.distance) continue;
            hover.kind = face >= Greyroad.FaceStartCap ? LdHoverKind.End : LdHoverKind.Face;
            hover.face = face;
            hover.distance = distance;
        }
        if (hover.kind != LdHoverKind.None) return hover;

        float body = NearestOnPath(s_ldFrames, 0, mouse, out float param, out Vector3 point);
        if (body >= GPEditShared.HandlePx) return hover;
        hover.kind = LdHoverKind.Body;
        hover.param = param;
        hover.point = point;
        hover.distance = body;
        return hover;
    }

    /// <summary>True when the cursor is on one of the road's two top edges. Leaves the road's world frames in s_ldSourceFrames.</summary>
    static bool HitRoadEdge(Greyroad road, Vector2 mouse, out int side, out float param, out Vector3 point, out float distance)
    {
        side = 0;
        param = 0f;
        point = default;
        distance = GPEditShared.HoverPx;
        road.GetPathFramesWorld(s_ldSourceFrames);
        for (int s = -1; s <= 1; s += 2)
        {
            float d = NearestOnPath(s_ldSourceFrames, s, mouse, out float p, out Vector3 w);
            if (d >= distance) continue;
            distance = d;
            side = s;
            param = p;
            point = w;
        }
        return side != 0;
    }

    static float LedgeHoverScore(Greyroad ledge, Vector2 mouse)
    {
        var hover = HitLedge(ledge, mouse);
        if (hover.kind == LdHoverKind.None) return float.MaxValue;
        return hover.kind == LdHoverKind.Body ? GPEditShared.HandlePx + hover.distance : hover.distance;
    }

    static float RoadEdgeHoverScore(Greyroad road, Vector2 mouse)
        => HitRoadEdge(road, mouse, out _, out _, out _, out float distance) ? GPEditShared.HandlePx + distance : float.MaxValue;

    // ─── Scene GUI ──────────────────────────────────────────────

    /// <summary>Controls of a selected ledge. Its spline vertices are derived, so the spline controls are not offered.</summary>
    static void OnLedgeSceneGUI(SceneView sv, Event e, Greyroad ledge)
    {
        var hover = HitLedge(ledge, e.mousePosition);

        if (e.type == EventType.MouseDown && !e.alt)
            BeginLedge(sv, e, ledge, hover);

        if (e.type == EventType.Repaint)
            DrawLedge(ledge, hover);

        if (e.type == EventType.MouseMove || e.type == EventType.KeyDown || e.type == EventType.KeyUp)
            sv.Repaint();
    }

    /// <summary>Ledge creation from the edges of a normal road. Runs after the road's spline controls, on events they left unused.</summary>
    static void OnRoadEdgeSceneGUI(SceneView sv, Event e, Greyroad road)
    {
        if (s_spDrag != SpDrag.None) return;
        if (e.type != EventType.MouseDown && e.type != EventType.Repaint) return;
        bool hit = HitRoadEdge(road, e.mousePosition, out int side, out float param, out Vector3 point, out _);

        if (e.type == EventType.Repaint)
        {
            DrawRoadEdgeHints(s_ldSourceFrames);
            if (hit) GPEditShared.DrawDot(point, GPEditShared.NewVertex, 0.06f);
            return;
        }
        if (!hit) return;

        if (e.button != 1 || e.alt) return;
        s_ldDrag = LdDrag.Create;
        s_ldSource = road;
        s_ldTarget = null;
        s_ldSide = side;
        s_ldAnchorParam = param;
        s_ldPress = e.mousePosition;
        GrabLedgeControl(e);
    }

    static void BeginLedge(SceneView sv, Event e, Greyroad ledge, LedgeHover hover)
    {
        switch (hover.kind)
        {
            case LdHoverKind.End:
            case LdHoverKind.Face:
                if (e.button == 2) BeginPrimitiveClick(sv, e, ledge, ClickAction.ToggleLedgeFace, hover.face);
                else if (e.button != 0) break;
                // LMB on a face: a click selects it, a drag moves it (GPEditSubSelection.cs). Ends drag at once.
                else if (hover.kind == LdHoverKind.Face) BeginSubPress(e, ledge, SubKind.LedgeFace, hover.face);
                else
                {
                    BeginLedgeDrag(ledge, true, hover.face, e.mousePosition, e.control);
                    GrabLedgeControl(e);
                }
                break;
            case LdHoverKind.Body:
                if (e.button == 1) BeginPrimitiveClick(sv, e, ledge, ClickAction.SplitLedge, 0, hover.param);
                break;
        }
    }

    /// <summary>
    /// Starts an end or face drag from the ledge frames in s_ldFrames; the caller binds the
    /// control. A plain face drag on a selected face also moves every other selected ledge face.
    /// </summary>
    static void BeginLedgeDrag(Greyroad ledge, bool end, int face, Vector2 mouse, bool control)
    {
        string name = end ? "Move Ledge End" : "Resize Ledge";
        Undo.IncrementCurrentGroup();
        s_ldUndoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(ledge, name);
        Undo.SetCurrentGroupName(name);
        GreyPrimitive.BeginDeferredPersist();

        s_ldDrag = end ? LdDrag.End : LdDrag.Face;
        s_ldTarget = ledge;
        s_ldSource = ledge.LedgeSource;
        s_ldFace = face;
        s_ldFacePoint = LedgeFacePoint(s_ldFrames, face, out s_ldFaceNormal);
        s_ldFaceStartDist = GPEditShared.ProjectRayOntoLine(HandleUtility.GUIPointToWorldRay(mouse), s_ldFacePoint, s_ldFaceNormal);
        s_ldStartWidth = ledge.BaseWidth;
        s_ldStartHeight = ledge.BaseHeight;
        s_ldStartInset = ledge.LedgeInset;
        s_ldStartRise = ledge.LedgeRise;
        s_ldTilting = !end && (face == Greyroad.FaceTop || face == Greyroad.FaceBottom) && control;
        if (s_ldTilting) CaptureLedgeTilt(ledge, mouse);

        s_ldFollowers.Clear();
        if (end || s_ldTilting || !IsSubSelected(ledge, SubKind.LedgeFace, face)) return;
        foreach (var element in s_subSelection)
        {
            if (element.Matches(ledge, SubKind.LedgeFace, face)) continue;
            var other = (Greyroad)element.target;
            if (!other.IsLedge) continue; // unlinked since it was selected
            Undo.RegisterCompleteObjectUndo(other, name);
            s_ldFollowers.Add(new LdFollower
            {
                ledge = other, face = element.index,
                width = other.BaseWidth, height = other.BaseHeight, inset = other.LedgeInset, rise = other.LedgeRise,
            });
        }
    }

    /// <summary>
    /// Pivot line and pressed direction of a tilt drag, from the ledge frames in s_ldFrames.
    /// Dragging the top turns the ledge around its bottom centre line; dragging the bottom, around its top centre line.
    /// </summary>
    static void CaptureLedgeTilt(Greyroad ledge, Vector2 mouse)
    {
        var mid = s_ldFrames[s_ldFrames.Count / 2];
        s_ldTiltPivotTop = s_ldFace == Greyroad.FaceBottom;
        Vector3 pressed = s_ldTiltPivotTop ? mid.position + mid.drop : mid.position;
        s_ldHinge = s_ldTiltPivotTop ? mid.position : mid.position + mid.drop;
        s_ldHingeAxis = mid.tangent;
        s_ldStartTilt = ledge.LedgeTilt;
        s_ldTiltPivot = new Vector2(-ledge.LedgeInset, ledge.LedgeRise) + LedgeTiltPivotOffset(ledge, s_ldStartTilt);
        s_ldTiltFrom = GPEditShared.RaycastPlane(mouse, s_ldHinge, s_ldHingeAxis, out Vector3 hit) ? hit - s_ldHinge : pressed - s_ldHinge;
    }

    /// <summary>
    /// Offset of the tilt pivot line from the ledge's inner top edge in its cross-section
    /// (x = outward, y = up) at a tilt in degrees: the top centre or the bottom centre.
    /// </summary>
    static Vector2 LedgeTiltPivotOffset(Greyroad ledge, float tilt)
    {
        float radians = tilt * Mathf.Deg2Rad, sin = Mathf.Sin(radians), cos = Mathf.Cos(radians);
        var topCenter = new Vector2(ledge.BaseWidth * 0.5f * cos, -ledge.BaseWidth * 0.5f * sin);
        return s_ldTiltPivotTop ? topCenter : topCenter - new Vector2(ledge.BaseHeight * sin, ledge.BaseHeight * cos);
    }

    static void GrabLedgeControl(Event e)
    {
        s_ldButton = e.button;
        s_ldControl = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(s_ldControl);
        GUIUtility.hotControl = s_ldControl;
        e.Use();
    }

    // ─── Drag ───────────────────────────────────────────────────

    static void HandleLedgeDrag(Event e, SceneView sv)
    {
        // The source, or the ledge once it exists, can be deleted under the drag.
        bool created = s_ldDrag != LdDrag.Create || s_ldTarget != null;
        if (s_ldSource == null || (created && s_ldTarget == null))
        {
            GUIUtility.hotControl = 0;
            ResetLedgeDrag();
            return;
        }

        // Events outside the window arrive as Ignore with the real type in rawType.
        EventType type = e.type == EventType.Ignore ? e.rawType : e.type;

        if (type == EventType.MouseDrag && e.button == s_ldButton)
        {
            ApplyLedgeDrag(e.mousePosition);
            sv.Repaint();
            e.Use();
            return;
        }

        if ((type == EventType.MouseUp && e.button == s_ldButton) || !Enabled)
        {
            FinishLedgeDrag();
            e.Use();
            return;
        }

        if (e.type == EventType.Repaint && s_ldTarget != null)
        {
            s_ldTarget.GetPathFramesWorld(s_ldFrames);
            var dragged = new LedgeHover { kind = LdHoverKind.None };
            if (s_ldDrag != LdDrag.Create)
            {
                dragged.kind = s_ldDrag == LdDrag.End ? LdHoverKind.End : LdHoverKind.Face;
                dragged.face = s_ldFace;
            }
            DrawLedge(s_ldTarget, dragged, s_ldDrag == LdDrag.Create);
        }
    }

    static void ApplyLedgeDrag(Vector2 mouse)
    {
        switch (s_ldDrag)
        {
            case LdDrag.Create: ApplyLedgeCreate(mouse); break;
            case LdDrag.End:    ApplyLedgeEnd(mouse);    break;
            case LdDrag.Face:   ApplyLedgeFace(mouse);   break;
        }
    }

    static void ApplyLedgeCreate(Vector2 mouse)
    {
        s_ldSource.GetPathFramesWorld(s_ldSourceFrames);
        NearestOnPath(s_ldSourceFrames, s_ldSide, mouse, out float param, out _);
        float start = Mathf.Min(s_ldAnchorParam, param), end = Mathf.Max(s_ldAnchorParam, param);

        if (s_ldTarget == null)
        {
            // A press that never becomes a drag creates nothing.
            if ((mouse - s_ldPress).magnitude < GPEditShared.DragStartPx) return;
            Undo.IncrementCurrentGroup();
            s_ldUndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Ledge");
            GreyPrimitive.BeginDeferredPersist();
            s_ldTarget = CreateLedge(s_ldSource, s_ldSide, start, end, null);
            return;
        }
        s_ldTarget.SetLedgeRange(start, end);
        s_ldTarget.RebuildMesh();
    }

    static void ApplyLedgeEnd(Vector2 mouse)
    {
        s_ldSource.GetPathFramesWorld(s_ldSourceFrames);
        NearestOnPath(s_ldSourceFrames, s_ldTarget.LedgeSide, mouse, out float param, out _);
        if (s_ldFace == Greyroad.FaceStartCap)
            s_ldTarget.SetLedgeRange(Mathf.Min(param, s_ldTarget.LedgeEnd - Greyroad.MinLedgeSpan), s_ldTarget.LedgeEnd);
        else
            s_ldTarget.SetLedgeRange(s_ldTarget.LedgeStart, Mathf.Max(param, s_ldTarget.LedgeStart + Greyroad.MinLedgeSpan));
        s_ldTarget.RebuildMesh();
    }

    static void ApplyLedgeFace(Vector2 mouse)
    {
        float distance = GPEditShared.ProjectRayOntoLine(HandleUtility.GUIPointToWorldRay(mouse), s_ldFacePoint, s_ldFaceNormal);
        float delta = distance - s_ldFaceStartDist;
        var ledge = s_ldTarget;
        if (s_ldTilting)
        {
            // Legitimate filter: the cursor ray runs parallel to the plane across the ledge.
            if (!GPEditShared.RaycastPlane(mouse, s_ldHinge, s_ldHingeAxis, out Vector3 hit)) return;
            float angle = Vector3.SignedAngle(s_ldTiltFrom, hit - s_ldHinge, s_ldHingeAxis);
            ledge.LedgeTilt = s_ldStartTilt - ledge.LedgeSide * angle;
            // The tilt turns the ledge around its inner top edge; inset and rise move that edge so the pivot line stays put.
            Vector2 innerTop = s_ldTiltPivot - LedgeTiltPivotOffset(ledge, ledge.LedgeTilt);
            ledge.LedgeInset = -innerTop.x;
            ledge.LedgeRise = innerTop.y;
        }
        else
        {
            MoveLedgeFace(ledge, s_ldFace, delta, s_ldStartWidth, s_ldStartHeight, s_ldStartInset, s_ldStartRise);
            foreach (var follower in s_ldFollowers)
            {
                MoveLedgeFace(follower.ledge, follower.face, delta, follower.width, follower.height, follower.inset, follower.rise);
                follower.ledge.RebuildMesh();
            }
        }
        ledge.RebuildMesh();
    }

    /// <summary>Moves one face of a ledge outward by <paramref name="delta"/> from the given drag-start values.</summary>
    static void MoveLedgeFace(Greyroad ledge, int face, float delta, float width, float height, float inset, float rise)
    {
        if (face == Greyroad.FaceTop)
        {
            delta = Mathf.Max(delta, k_MinLedgeSize - height);
            ledge.LedgeRise = rise + delta;
            ledge.BaseHeight = height + delta;
        }
        else if (face == Greyroad.FaceBottom)
            ledge.BaseHeight = Mathf.Max(k_MinLedgeSize, height + delta);
        else if (face == LedgeInnerFace(ledge))
        {
            // The inner side moves; the outer side stays where it is.
            delta = Mathf.Max(delta, k_MinLedgeSize - width);
            ledge.LedgeInset = inset + delta;
            ledge.BaseWidth = width + delta;
        }
        else if (face == Greyroad.FaceLeftSide || face == Greyroad.FaceRightSide)
            ledge.BaseWidth = Mathf.Max(k_MinLedgeSize, width + delta);
    }

    static void FinishLedgeDrag()
    {
        GameObject created = s_ldDrag == LdDrag.Create && s_ldTarget != null ? s_ldTarget.gameObject : null;
        // Unset only for a Create press that never passed its drag threshold: nothing was made.
        if (s_ldTarget != null)
        {
            GreyPrimitive.EndDeferredPersist();
            EditorUtility.SetDirty(s_ldTarget);
            foreach (var follower in s_ldFollowers) EditorUtility.SetDirty(follower.ledge);
            Undo.CollapseUndoOperations(s_ldUndoGroup);
        }
        GUIUtility.hotControl = 0;
        ResetLedgeDrag();
        if (created != null) Selection.activeGameObject = created;
    }

    static void ResetLedgeDrag()
    {
        s_ldDrag = LdDrag.None;
        s_ldTarget = null;
        s_ldSource = null;
        s_ldFollowers.Clear();
    }

    // ─── Create / split / unlink ────────────────────────────────

    /// <summary>
    /// Creates a ledge on one edge of <paramref name="source"/> over a range of its path and puts
    /// both in the roads' shared folder. Size, faces and renderer settings come from
    /// <paramref name="template"/> (another ledge) when given, else the ledge defaults and the
    /// source's renderer settings. The caller owns the undo group.
    /// </summary>
    static Greyroad CreateLedge(Greyroad source, int side, float start, float end, Greyroad template)
    {
        const string label = "Create Ledge";
        Transform parent = LedgeGroupParent(source, label);
        GreyPrimitive settings = template != null ? template : source;
        var st = source.transform;
        var go = GreyPrimitiveSettings.PlacePrimitive<Greyroad>(source.name + " Ledge", st.position, st.rotation, parent,
            meshCollider: settings.GetComponent<MeshCollider>() != null, select: false);
        var ledge = CopyPrimitiveSettings<Greyroad>(settings, go);
        ledge.SplineSmoothing = 1f; // an unlinked ledge passes smoothly through its derived vertices
        ledge.BaseWidth = template != null ? template.BaseWidth : 1f;
        ledge.BaseHeight = template != null ? template.BaseHeight : 3f;
        for (int face = 0; face < 6; face++) ledge.ActiveFaces[face] = template == null || template.ActiveFaces[face];
        if (template != null)
        {
            ledge.LedgeInset = template.LedgeInset;
            ledge.LedgeRise = template.LedgeRise;
            ledge.LedgeTilt = template.LedgeTilt;
        }

        Undo.RegisterCompleteObjectUndo(source, label);
        ledge.LinkAsLedge(source, side, start, end);
        ledge.RebuildMesh();
        EditorUtility.SetDirty(source);
        EditorUtility.SetDirty(ledge);
        return ledge;
    }

    /// <summary>Gives a new primitive the GameObject flags, renderer, collider and mesh settings of <paramref name="from"/>.</summary>
    static T CopyPrimitiveSettings<T>(GreyPrimitive from, GameObject go) where T : GreyPrimitive
    {
        go.layer = from.gameObject.layer;
        go.tag = from.gameObject.tag;
        GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(from.gameObject));
        EditorUtility.CopySerialized(from.GetComponent<MeshRenderer>(), go.GetComponent<MeshRenderer>());
        var collider = from.GetComponent<MeshCollider>();
        if (collider != null) EditorUtility.CopySerialized(collider, go.GetComponent<MeshCollider>());
        var primitive = go.GetComponent<T>();
        primitive.SubdivisionMultiplier = from.SubdivisionMultiplier;
        primitive.PlanarUv = from.PlanarUv;
        primitive.FlipFaces = from.FlipFaces;
        return primitive;
    }

    /// <summary>Creates a ledge along the whole length of one side of a road, as one undo step, and selects it.</summary>
    internal static void CreateFullLedge(Greyroad source, int side)
    {
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Create Ledge");
        var ledge = CreateLedge(source, side, 0f, source.SegmentCount, null);
        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = ledge.gameObject;
    }

    static void SplitLedge(Greyroad ledge, float param)
    {
        float start = ledge.LedgeStart, end = ledge.LedgeEnd;
        // Legitimate filter: a click on an end leaves no room for a second ledge.
        if (param - start < Greyroad.MinLedgeSpan || end - param < Greyroad.MinLedgeSpan) return;

        const string label = "Split Ledge";
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        Undo.RegisterCompleteObjectUndo(ledge, label);
        CreateLedge(ledge.LedgeSource, ledge.LedgeSide, param, end, ledge);
        ledge.SetLedgeRange(start, param);
        ledge.RebuildMesh();
        EditorUtility.SetDirty(ledge);
        Undo.CollapseUndoOperations(group);
    }

    /// <summary>Hides or shows the clicked face; when it is selected, every selected ledge face gets the clicked face's new state.</summary>
    static void ToggleLedgeFace(Greyroad ledge, int face)
    {
        const string label = "Toggle Ledge Face";
        bool visible = !ledge.ActiveFaces[face];
        if (!IsSubSelected(ledge, SubKind.LedgeFace, face))
        {
            SetLedgeFaceVisible(ledge, face, visible, label);
            return;
        }
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        foreach (var element in s_subSelection)
            SetLedgeFaceVisible((Greyroad)element.target, element.index, visible, label);
        Undo.CollapseUndoOperations(group);
    }

    static void SetLedgeFaceVisible(Greyroad ledge, int face, bool visible, string label)
    {
        Undo.RegisterCompleteObjectUndo(ledge, label);
        ledge.ActiveFaces[face] = visible;
        ledge.RebuildMesh();
        EditorUtility.SetDirty(ledge);
    }

    /// <summary>
    /// Turns a ledge into a normal road on its current path and moves it out of the roads' shared
    /// folder. The folder is removed once the source has no ledge left.
    /// </summary>
    internal static void UnlinkLedge(Greyroad ledge)
    {
        const string label = "Unlink Ledge";
        var source = ledge.LedgeSource;
        Transform folder = ledge.transform.parent;
        bool grouped = IsLedgeGroupFolder(folder, source);

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        Undo.RegisterCompleteObjectUndo(ledge, label);
        Undo.RegisterCompleteObjectUndo(source, label);
        Undo.RecordObject(ledge.transform, label);
        ledge.UnlinkLedge();

        if (grouped)
        {
            Undo.SetTransformParent(ledge.transform, folder.parent, label);
            using var ledgesScope = ListPool<Greyroad>.Get(out var ledges);
            source.GetLedges(ledges);
            if (ledges.Count == 0)
            {
                Undo.SetTransformParent(source.transform, folder.parent, label);
                if (folder.childCount == 0) Undo.DestroyObjectImmediate(folder.gameObject);
            }
        }

        ledge.RecenterPivot();
        GreyPrimitiveEditor.RebuildPrimitiveAndDependents(ledge);
        EditorUtility.SetDirty(ledge);
        EditorUtility.SetDirty(source);
        Undo.CollapseUndoOperations(group);
    }

    // ─── Hierarchy ──────────────────────────────────────────────

    /// <summary>
    /// Parent for a new ledge of <paramref name="source"/>: the plain folder that holds only the
    /// source and its ledges. Creates the folder in the source's place and moves the source into
    /// it when there is none yet.
    /// </summary>
    static Transform LedgeGroupParent(Greyroad source, string label)
    {
        Transform parent = source.transform.parent;
        // A road inside a prefab instance cannot be reparented, and a prefab's root cannot get a
        // sibling: their ledges go beside or under them.
        if (PrefabUtility.IsPartOfPrefabInstance(source.gameObject)) return parent;
        var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(source.gameObject);
        if (stage != null && stage.prefabContentsRoot == source.gameObject) return source.transform;
        // A road that is an input of a Boolean Result or compound stays a direct child of it.
        if (parent != null && parent.TryGetComponent<GreyPrimitive>(out _)) return parent;
        if (IsLedgeGroupFolder(parent, source)) return parent;

        var go = new GameObject("Linked Greyroads");
        SceneManager.MoveGameObjectToScene(go, source.gameObject.scene);
        go.transform.SetParent(parent, false);
        go.transform.SetSiblingIndex(source.transform.GetSiblingIndex());
        go.layer = source.gameObject.layer;
        Undo.RegisterCreatedObjectUndo(go, label);
        Undo.SetTransformParent(source.transform, go.transform, label);
        return go.transform;
    }

    /// <summary>True when <paramref name="folder"/> is a component-less object whose children are exactly the source and ledges of it.</summary>
    static bool IsLedgeGroupFolder(Transform folder, Greyroad source)
    {
        // A scene-root road has no parent, so no folder.
        if (folder == null || PrefabUtility.IsPartOfAnyPrefab(folder.gameObject)) return false;
        var components = folder.GetComponents<Component>();
        if (components.Length != 1) return false;
        foreach (Transform child in folder)
        {
            if (!child.TryGetComponent<Greyroad>(out var road)) return false;
            if (road != source && road.LedgeSource != source) return false;
        }
        return true;
    }

    // ─── Draw ───────────────────────────────────────────────────

    /// <summary>Draws a ledge's outline and handles from the world frames in s_ldFrames.</summary>
    static void DrawLedge(Greyroad ledge, LedgeHover hover, bool creating = false)
    {
        var frames = s_ldFrames;
        if (frames.Count < 2) return; // source has no path to follow

        if (s_ldLine.Length < frames.Count) s_ldLine = new Vector3[frames.Count * 2];
        var previousZ = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        Handles.color = creating ? GPEditShared.Create : GPEditShared.Outline;
        for (int rail = 0; rail < 4; rail++)
        {
            int side = (rail & 1) == 0 ? -1 : 1;
            bool bottom = rail >= 2;
            for (int i = 0; i < frames.Count; i++)
                s_ldLine[i] = PathPoint(frames[i], side) + (bottom ? frames[i].drop : Vector3.zero);
            Handles.DrawAAPolyLine(2f, frames.Count, s_ldLine);
        }
        DrawLedgeEndRect(frames[0]);
        DrawLedgeEndRect(frames[frames.Count - 1]);
        Handles.zTest = previousZ;
        if (creating) return;

        if (hover.kind == LdHoverKind.Body)
            GPEditShared.DrawDot(hover.point, GPEditShared.NewVertex, 0.06f);

        for (int face = 0; face < 6; face++)
        {
            bool hot = (hover.kind == LdHoverKind.End || hover.kind == LdHoverKind.Face) && hover.face == face;
            bool selected = IsSubSelected(ledge, SubKind.LedgeFace, face);
            Color color = selected ? GPEditShared.Selected
                : hot ? GPEditShared.HandleHover
                : ledge.ActiveFaces[face] ? GPEditShared.HandleActive : GPEditShared.HandleInactive;
            float size = face >= Greyroad.FaceStartCap ? 0.07f : 0.05f;
            GPEditShared.DrawDot(LedgeFacePoint(frames, face, out _), color, selected ? size * 1.6f : hot ? size * 1.2f : size);
        }
    }

    /// <summary>Faint lines along both top edges of a normal road, marking where an RMB drag creates a ledge.</summary>
    static void DrawRoadEdgeHints(List<Greyroad.PathFrame> frames)
    {
        if (frames.Count < 2) return; // road has no path
        if (s_ldLine.Length < frames.Count) s_ldLine = new Vector3[frames.Count * 2];
        var previousZ = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        Handles.color = GPEditShared.CreateEdge;
        for (int side = -1; side <= 1; side += 2)
        {
            for (int i = 0; i < frames.Count; i++) s_ldLine[i] = PathPoint(frames[i], side);
            Handles.DrawAAPolyLine(1.5f, frames.Count, s_ldLine);
        }
        Handles.zTest = previousZ;
    }

    static void DrawLedgeEndRect(in Greyroad.PathFrame frame)
    {
        Vector3 left = PathPoint(frame, -1), right = PathPoint(frame, 1);
        Handles.DrawLine(left, right);
        Handles.DrawLine(right, right + frame.drop);
        Handles.DrawLine(right + frame.drop, left + frame.drop);
        Handles.DrawLine(left + frame.drop, left);
    }
}
#endif
