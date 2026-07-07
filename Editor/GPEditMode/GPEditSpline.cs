#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Spline behaviors for Edit Mode, shared by Greypipe and Greyroad (Greyroad adds banking
/// handles). The two types are driven through a thin editor-side <see cref="GPSpline"/> adapter
/// so the interaction logic lives once.
///
///   Vertex:  LMB drag = move in the main-axis plane · Shift+LMB = move orthogonally
///            Ctrl (on an end vertex) = move it alone, leaving the rest of the spline put
///            RMB (on an end vertex)  = extrude / extend a new vertex
///            MMB = delete the vertex
///   Handle:  LMB drag = move (no Ctrl) · MMB = reset the handle
///   Banking (Greyroad only): LMB drag = bank around the tangent · MMB = reset to 0°
///   Spline:  RMB = insert a vertex at the click point
/// </summary>
static partial class GPEdit
{
    enum SpDrag { None, VertexMove, Bezier, Banking, Extend }

    static GPSpline s_sp;
    static SpDrag   s_spDrag;
    static int      s_spButton;
    static int      s_spControl;
    static int      s_spUndoGroup;

    static int      s_spVertex;
    static int      s_spSide;
    static bool     s_spOrtho;
    static Vector3  s_spPlanePoint;
    static Vector3  s_spPlaneNormal;
    static Vector3  s_spHitStart;
    static Vector2  s_spPressPos;

    static Vector3  s_spStartLocalPos;
    static Vector3  s_spBezierStartEndpoint;

    static float    s_spBankingStartAngle;
    static Vector3  s_spBankingTangent;
    static Vector3  s_spBankingPoint;
    static Vector2  s_spBankingStartMouse;

    static int      s_spExtendEdge;
    static Vector3[] s_spReshape;
    static bool     s_spDoReshape;

    static readonly GPSplinePipe s_pipeAdapter = new GPSplinePipe();
    static readonly GPSplineRoad s_roadAdapter = new GPSplineRoad();

    static partial void OnGreypipeSceneGUI(SceneView sv, Event e, Greypipe pipe)
    {
        s_pipeAdapter.Set(pipe);
        OnSplineSceneGUI(sv, e, s_pipeAdapter);
    }

    static partial void OnGreyroadSceneGUI(SceneView sv, Event e, Greyroad road)
    {
        s_roadAdapter.Set(road);
        OnSplineSceneGUI(sv, e, s_roadAdapter);
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

        // Alt is the Scene View navigation modifier (orbit/pan/zoom) — never start an edit while
        // it's held, or an Alt-drag to navigate would grab a vertex/handle instead.
        if (e.type == EventType.MouseDown && !e.alt && (e.button == 0 || e.button == 1 || e.button == 2))
            BeginSpline(e, sp, hoverVertex, hoverBezier, hoverBezierSide, hoverBank, hoverBankSide);

        if (e.type == EventType.Repaint)
            DrawSpline(sp, hoverVertex, hoverBezier, hoverBezierSide, hoverBank, hoverBankSide, hasPreview, previewPos);

        if (e.type == EventType.MouseMove)
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
        float scale = sp.Transform.lossyScale.z;
        for (int i = 0; i < sp.Count; i++)
        {
            Vector3 vw = sp.GetWorldVertexPos(i);
            Vector3 dir = sp.GetHandleDirWorld(i);
            float len = sp.GetHandleLength(i) * scale;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                Vector2 s = HandleUtility.WorldToGUIPoint(vw + dir * len * sgn);
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

    static void BeginSpline(Event e, GPSpline sp, int vertex, int bezier, int bezierSide, int bank, int bankSide)
    {
        bool isEnd(int i) => i == 0 || i == sp.Count - 1;

        if (vertex >= 0)
        {
            switch (e.button)
            {
                case 0: StartSplineVertexMove(sp, vertex, e.mousePosition, e.shift, e.control); break;
                case 1: if (isEnd(vertex)) StartSplineExtend(sp, vertex, e.mousePosition); else { e.Use(); return; } break;
                case 2: DeleteSplineVertex(sp, vertex); e.Use(); return;
            }
        }
        else if (bezier >= 0)
        {
            switch (e.button)
            {
                case 0: StartSplineBezier(sp, bezier, bezierSide, e.mousePosition, e.shift); break;
                case 2: ResetSplineHandle(sp, bezier); e.Use(); return;
                default: return;
            }
        }
        else if (bank >= 0)
        {
            switch (e.button)
            {
                case 0: StartSplineBanking(sp, bank, bankSide, e.mousePosition); break;
                case 2: ResetSplineBanking(sp, bank); e.Use(); return;
                default: return;
            }
        }
        else if (e.button == 1)
        {
            if (HitSplineSegment(sp, e.mousePosition, out int seg, out float t))
            {
                InsertSplineVertex(sp, seg, t);
                e.Use();
            }
            return;
        }
        else return;

        if (s_spDrag == SpDrag.None) return;
        s_sp = sp;
        s_spButton = e.button;
        s_spPressPos = e.mousePosition;
        s_spControl = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(s_spControl);
        GUIUtility.hotControl = s_spControl;
        e.Use();
    }

    static void StartSplineVertexMove(GPSpline sp, int vertex, Vector2 mousePos, bool shift, bool ctrl)
    {
        BeginSplineUndo(sp, "Move Vertex");
        s_spDrag = SpDrag.VertexMove;
        s_spVertex = vertex;
        s_spOrtho = shift;
        s_spStartLocalPos = sp.GetLocalVertexPos(vertex);

        s_spPlanePoint = sp.GetWorldVertexPos(vertex);
        s_spPlaneNormal = sp.MovePlaneNormal;
        GPEditShared.RaycastPlane(mousePos, s_spPlanePoint, s_spPlaneNormal, out s_spHitStart);

        bool isEnd = vertex == 0 || vertex == sp.Count - 1;
        s_spDoReshape = isEnd && sp.Count > 2 && !ctrl;
        s_spReshape = s_spDoReshape ? CaptureReshape(sp) : null;
    }

    static void StartSplineBezier(GPSpline sp, int vertex, int side, Vector2 mousePos, bool shift)
    {
        BeginSplineUndo(sp, "Move Handle");
        s_spDrag = SpDrag.Bezier;
        s_spVertex = vertex;
        s_spSide = side;
        s_spOrtho = shift;
        float scale = sp.Transform.lossyScale.z;
        Vector3 vw = sp.GetWorldVertexPos(vertex);
        s_spBezierStartEndpoint = vw + sp.GetHandleDirWorld(vertex) * (sp.GetHandleLength(vertex) * scale) * side;
        s_spPlanePoint = s_spBezierStartEndpoint;
        s_spPlaneNormal = sp.MovePlaneNormal;
        GPEditShared.RaycastPlane(mousePos, s_spPlanePoint, s_spPlaneNormal, out s_spHitStart);
    }

    static void StartSplineBanking(GPSpline sp, int vertex, int side, Vector2 mousePos)
    {
        BeginSplineUndo(sp, "Bank Vertex");
        s_spDrag = SpDrag.Banking;
        s_spVertex = vertex;
        s_spSide = side;
        s_spBankingStartAngle = sp.GetBankingAngle(vertex);
        s_spBankingStartMouse = mousePos;
        s_spBankingPoint = sp.GetWorldVertexPos(vertex);
        sp.ComputeBankingAxis(vertex, out Vector3 up, out Vector3 right);
        s_spBankingTangent = Vector3.Cross(right, up).normalized;
        if (s_spBankingTangent.sqrMagnitude < 0.0001f) s_spBankingTangent = sp.MainAxisWorld;
    }

    static void StartSplineExtend(GPSpline sp, int edge, Vector2 mousePos)
    {
        Undo.IncrementCurrentGroup();
        s_spUndoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Transform, "Extend Spline");
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Extend Spline");
        Undo.SetCurrentGroupName("Extend Spline");

        s_spDrag = SpDrag.Extend;
        s_spExtendEdge = edge;
        Vector3 edgeWorld = sp.GetWorldVertexPos(edge);
        sp.ExtendFromEdge(edge, sp.GetLocalVertexPos(edge));
        s_spVertex = edge == 0 ? 0 : sp.Count - 1;
        s_spStartLocalPos = sp.GetLocalVertexPos(s_spVertex);

        s_spPlanePoint = edgeWorld;
        s_spPlaneNormal = sp.MovePlaneNormal;
        GPEditShared.RaycastPlane(mousePos, s_spPlanePoint, s_spPlaneNormal, out s_spHitStart);
    }

    static void BeginSplineUndo(GPSpline sp, string name)
    {
        Undo.IncrementCurrentGroup();
        s_spUndoGroup = Undo.GetCurrentGroup();
        Undo.RecordObject(sp.Transform, name);
        Undo.RegisterCompleteObjectUndo(sp.Obj, name);
        Undo.SetCurrentGroupName(name);
    }

    // ─── Immediate actions ──────────────────────────────────────

    static void DeleteSplineVertex(GPSpline sp, int vertex)
    {
        Undo.IncrementCurrentGroup();
        int g = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Transform, "Delete Vertex");
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Delete Vertex");
        if (sp.RemoveVertex(vertex))
        {
            sp.RecenterPivot();
            sp.Rebuild();
            EditorUtility.SetDirty(sp.Obj);
        }
        Undo.SetCurrentGroupName("Delete Vertex");
        Undo.CollapseUndoOperations(g);
    }

    static void InsertSplineVertex(GPSpline sp, int seg, float t)
    {
        Undo.IncrementCurrentGroup();
        int g = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(sp.Transform, "Insert Vertex");
        Undo.RegisterCompleteObjectUndo(sp.Obj, "Insert Vertex");
        sp.InsertVertex(seg, t);
        sp.RecenterPivot();
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

        if (type == EventType.MouseDrag && e.button == s_spButton)
        {
            ApplySplineDrag(e.mousePosition);
            s_sp.Rebuild();
            EditorUtility.SetDirty(s_sp.Obj);
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
            if (s_spDrag == SpDrag.VertexMove || s_spDrag == SpDrag.Bezier || s_spDrag == SpDrag.Extend)
                DrawSplineDragGizmo(s_spPlanePoint, s_spPlaneNormal, s_spOrtho);
            DrawSpline(s_sp, s_spVertex, -1, 0, -1, 0, false, default);
        }
    }

    /// <summary>
    /// While dragging: always shows the move plane (yellow quad); when Shift is held, additionally
    /// draws two arrows along the orthogonal axis — so the user sees both options.
    /// </summary>
    static void DrawSplineDragGizmo(Vector3 point, Vector3 normal, bool ortho)
    {
        if (normal.sqrMagnitude < 1e-4f) return;
        normal = normal.normalized;
        float size = HandleUtility.GetHandleSize(point);

        // Move plane (always).
        Vector3 a = Vector3.Cross(normal, Vector3.up);
        if (a.sqrMagnitude < 1e-3f) a = Vector3.Cross(normal, Vector3.right);
        a.Normalize();
        Vector3 b = Vector3.Cross(normal, a).normalized;
        float s = size * 1.1f;
        var quad = new[]
        {
            point + (a + b) * s, point + (a - b) * s, point + (-a - b) * s, point + (-a + b) * s,
        };
        Color outline = GPEditShared.OutlineHover; outline.a = 0.6f;
        Handles.DrawSolidRectangleWithOutline(quad, GPEditShared.DragPlane, outline);

        // Orthogonal arrows (Shift) — added on top of the plane.
        if (ortho)
        {
            Handles.color = GPEditShared.OutlineHover;
            float len = size * 0.9f;
            Handles.DrawLine(point, point + normal * len, 3f);
            Handles.ConeHandleCap(0, point + normal * len, Quaternion.LookRotation(normal), size * 0.12f, EventType.Repaint);
            Handles.DrawLine(point, point - normal * len, 3f);
            Handles.ConeHandleCap(0, point - normal * len, Quaternion.LookRotation(-normal), size * 0.12f, EventType.Repaint);
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
        }
    }

    static Vector3 PlaneDelta(Vector2 mousePos)
    {
        if (s_spOrtho)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
            float cur = GPEditShared.ProjectRayOntoLine(ray, s_spPlanePoint, s_spPlaneNormal);
            float start = GPEditShared.ProjectRayOntoLine(
                HandleUtility.GUIPointToWorldRay(s_spPressPos), s_spPlanePoint, s_spPlaneNormal);
            return s_spPlaneNormal * (cur - start);
        }
        if (!GPEditShared.RaycastPlane(mousePos, s_spPlanePoint, s_spPlaneNormal, out Vector3 hit))
            return Vector3.zero;
        return hit - s_spHitStart;
    }

    static void ApplySplineVertexMove(Vector2 mousePos)
    {
        Vector3 delta = PlaneDelta(mousePos);
        var xform = s_sp.Transform;
        Vector3 newLocal = xform.InverseTransformPoint(xform.TransformPoint(s_spStartLocalPos) + delta);
        s_sp.SetLocalVertexPos(s_spVertex, newLocal);
        if (s_spDoReshape && s_spReshape != null) ApplyReshape(s_sp, s_spReshape);
    }

    static void ApplySplineBezier(Vector2 mousePos)
    {
        Vector3 delta = PlaneDelta(mousePos);
        Vector3 vw = s_sp.GetWorldVertexPos(s_spVertex);
        Vector3 worldDir = (s_spBezierStartEndpoint + delta) - vw;
        if (s_spSide < 0) worldDir = -worldDir;
        float worldLen = worldDir.magnitude;
        if (worldLen < 0.0001f) return;

        s_sp.SetHandleDirLocal(s_spVertex, s_sp.Transform.InverseTransformDirection(worldDir.normalized));
        float scale = Mathf.Abs(s_sp.Transform.lossyScale.z);
        if (scale < 0.0001f) scale = 1f;
        s_sp.SetHandleLength(s_spVertex, Mathf.Max(0.001f, worldLen / scale));
    }

    static void ApplySplineBanking(Vector2 mousePos)
    {
        if (!GPEditShared.RaycastPlane(mousePos, s_spBankingPoint, s_spBankingTangent, out Vector3 hit)) return;
        if (!GPEditShared.RaycastPlane(s_spBankingStartMouse, s_spBankingPoint, s_spBankingTangent, out Vector3 startHit)) return;
        Vector3 fromVec = startHit - s_spBankingPoint;
        Vector3 toVec   = hit - s_spBankingPoint;
        if (fromVec.sqrMagnitude < 0.0001f || toVec.sqrMagnitude < 0.0001f) return;
        float deltaDeg = Vector3.SignedAngle(fromVec, toVec, s_spBankingTangent);
        s_sp.SetBankingAngle(s_spVertex, s_spBankingStartAngle + deltaDeg * Mathf.Deg2Rad);
    }

    static void ApplySplineExtend(Vector2 mousePos)
    {
        if (!GPEditShared.RaycastPlane(mousePos, s_spPlanePoint, s_spPlaneNormal, out Vector3 hit)) return;
        var xform = s_sp.Transform;
        Vector3 newWorld = xform.TransformPoint(s_spStartLocalPos) + (hit - s_spHitStart);
        Vector3 newLocal = xform.InverseTransformPoint(newWorld);
        s_sp.SetLocalVertexPos(s_spVertex, newLocal);

        Vector3 toNew = newLocal - s_spStartLocalPos;
        if (toNew.sqrMagnitude > 0.001f)
        {
            Vector3 dir = s_spExtendEdge == 0 ? -toNew.normalized : toNew.normalized;
            s_sp.SetHandleDirLocal(s_spVertex, dir);
            s_sp.SetHandleLength(s_spVertex, Mathf.Max(0.01f, toNew.magnitude / 3f));
        }
    }

    static void FinishSplineDrag()
    {
        bool moved = s_spDrag == SpDrag.VertexMove || s_spDrag == SpDrag.Extend;
        if (moved && s_sp != null && s_sp.Obj != null)
        {
            Undo.RecordObject(s_sp.Transform, "Recenter");
            Undo.RecordObject(s_sp.Obj, "Recenter");
            s_sp.RecenterPivot();
            s_sp.Rebuild();
            EditorUtility.SetDirty(s_sp.Obj);
            EditorUtility.SetDirty(s_sp.Transform);
        }
        Undo.CollapseUndoOperations(s_spUndoGroup);
        GUIUtility.hotControl = 0;
        ResetSplineDrag();
    }

    static void ResetSplineDrag()
    {
        s_spDrag = SpDrag.None;
        s_sp = null;
        s_spReshape = null;
        s_spDoReshape = false;
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
        float scale = self.lossyScale.z;
        for (int i = 0; i < sp.Count; i++)
        {
            Vector3 vw = sp.GetWorldVertexPos(i);
            float hs = HandleUtility.GetHandleSize(vw) * 0.08f;
            bool isEnd = i == 0 || i == sp.Count - 1;

            Color vColor = i == hoverVertex ? GPEditShared.VertexHover : GPEditShared.Vertex;
            Handles.color = GPEditShared.IsWorldPointOccluded(vw, self) ? GPEditShared.Occluded(vColor) : vColor;
            if (isEnd) Handles.CubeHandleCap(0, vw, Quaternion.identity, hs * 2f, EventType.Repaint);
            else       Handles.SphereHandleCap(0, vw, Quaternion.identity, hs * 2f, EventType.Repaint);

            // Bezier handle
            Vector3 dir = sp.GetHandleDirWorld(i);
            float len = sp.GetHandleLength(i) * scale;
            Vector3 a = vw + dir * len, b = vw - dir * len;
            bool occA = GPEditShared.IsWorldPointOccluded(a, self);
            bool occB = GPEditShared.IsWorldPointOccluded(b, self);
            // Line color follows both endpoints — faded only when the whole handle is hidden.
            Handles.color = occA && occB ? GPEditShared.Occluded(GPEditShared.Bezier) : GPEditShared.Bezier;
            Handles.DrawLine(a, vw);
            Handles.DrawLine(vw, b);
            float dot = hs * 0.8f;
            DrawBezierDot(a, i, +1, hoverBezier, hoverBezierSide, dot, occA);
            DrawBezierDot(b, i, -1, hoverBezier, hoverBezierSide, dot, occB);

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
            }
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
    /// <summary>The axis a Shift+LMB (orthogonal) drag moves along, and the normal of the plain LMB drag plane.</summary>
    public abstract Vector3 MovePlaneNormal { get; }
    public abstract Color SplineColor { get; }

    public abstract Vector3 GetWorldVertexPos(int i);
    public abstract Vector3 GetLocalVertexPos(int i);
    public abstract void SetLocalVertexPos(int i, Vector3 local);
    public abstract Vector3 GetHandleDirWorld(int i);
    public abstract void SetHandleDirLocal(int i, Vector3 dir);
    public abstract float GetHandleLength(int i);
    public abstract void SetHandleLength(int i, float len);
    public abstract void ResetHandle(int i);

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
    { var v = _p.Vertices[i]; v.position = local; _p.Vertices[i] = v; }
    public override Vector3 GetHandleDirWorld(int i) => _p.GetVertexHandleDirWorld(i);
    public override void SetHandleDirLocal(int i, Vector3 dir) => _p.SetVertexHandleDirLocal(i, dir);
    public override float GetHandleLength(int i) => _p.Vertices[i].handleLength;
    public override void SetHandleLength(int i, float len)
    { var v = _p.Vertices[i]; v.handleLength = len; _p.Vertices[i] = v; }
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
    // Road: orthogonal axis is always world up (Shift+LMB = vertical, LMB = horizontal XZ plane).
    public override Vector3 MovePlaneNormal => Vector3.up;
    public override Color SplineColor => new Color(0.4f, 1f, 0.7f, 0.9f);

    public override Vector3 GetWorldVertexPos(int i) => _r.GetWorldVertexPosition(i);
    public override Vector3 GetLocalVertexPos(int i) => _r.Vertices[i].position;
    public override void SetLocalVertexPos(int i, Vector3 local)
    { var v = _r.Vertices[i]; v.position = local; _r.Vertices[i] = v; }
    public override Vector3 GetHandleDirWorld(int i) => _r.GetVertexHandleDirWorld(i);
    public override void SetHandleDirLocal(int i, Vector3 dir) => _r.SetVertexHandleDirLocal(i, dir);
    public override float GetHandleLength(int i) => _r.Vertices[i].handleLength;
    public override void SetHandleLength(int i, float len)
    { var v = _r.Vertices[i]; v.handleLength = len; _r.Vertices[i] = v; }
    public override void ResetHandle(int i) => _r.ResetVertexHandle(i);

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
