#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

static partial class QuickTransform
{
    // ─── Greypipe State ─────────────────────────────────────────

    static Greypipe greypipeTarget;
    static int     greypipeSelectedVertex = -1;
    static int     greypipeHoveredVertex  = -1;
    static Mode    greypipeVertexMode;

    // Vertex drag state
    static Greypipe.SplineVertex greypipeStartVertex;
    static Vector3 greypipeDragPlanePoint;
    static Vector3 greypipeDragPlaneNormal;
    static Vector3 greypipeDragHitStart;
    static float   greypipeDragStartDist;

    // Edge-vertex reshape state: snapshot of all vertex positions in edge-axis-relative coords
    static bool      greypipeReshapeMode;
    static Vector3[] greypipeEdgeSnapshot;
    static Vector3   greypipeFixedEdgeWorldPos;  // the OTHER edge that stays fixed during reshape
    static int       greypipeMovingEdgeIdx;

    // Bezier handle endpoint drag state
    static int     greypipeBezierHandleVertex = -1;
    static int     greypipeBezierHandleSide;  // +1 = forward endpoint, -1 = back endpoint
    static int     greypipeHoveredBezierVertex = -1;
    static int     greypipeHoveredBezierSide;
    static Vector3 greypipeBezierStartEndpointWorld;  // snapshot of endpoint world pos at drag begin

    // Special mode drag state
    static bool    greypipeExtending;
    static int     greypipeExtendEdgeIndex;
    static bool    greypipeGirthDragging;
    static int     greypipeGirthVertex;
    static float   greypipeGirthStartMult;
    static float   greypipeGirthStartMouseY;

    static void ResetGreypipeState()
    {
        greypipeTarget         = null;
        greypipeSelectedVertex = -1;
        greypipeHoveredVertex  = -1;
        greypipeExtending      = false;
        greypipeGirthDragging  = false;
        greypipeReshapeMode    = false;
        greypipeEdgeSnapshot   = null;
        greypipeBezierHandleVertex = -1;
        greypipeHoveredBezierVertex = -1;
    }

    // ─── Hover Detection ────────────────────────────────────────

    static void DetectGreypipeHover(SceneView sv, Vector2 mousePos)
    {
        greypipeHoveredVertex = -1;
        greypipeHoveredBezierVertex = -1;
        var sel = Selection.transforms;
        if (sel == null || sel.Length != 1) return;
        var pipe = sel[0].GetComponent<Greypipe>();
        if (pipe == null) return;

        // Rotate mode: Greypipe rotates as a whole object — no per-vertex hover.
        if (GetHeldMode() == Mode.Rotate) return;

        // Vertex takes priority over bezier handle endpoints. Only fall back to handle hover
        // (in Move mode) when no vertex is under the cursor.
        greypipeHoveredVertex = DetectGreypipeVertexHover(sv, mousePos, pipe);
        if (greypipeHoveredVertex >= 0) return;

        if (GetHeldMode() == Mode.Move)
            DetectGreypipeBezierHover(sv, mousePos, pipe, out greypipeHoveredBezierVertex, out greypipeHoveredBezierSide);
    }

    static void DetectGreypipeBezierHover(SceneView sv, Vector2 mousePos, Greypipe pipe, out int vertexIdx, out int side)
    {
        vertexIdx = -1;
        side = 0;
        float bestDist = EdgeHoverPx * 2f;

        for (int i = 0; i < pipe.Vertices.Count; i++)
        {
            Vector3 vertexWorld = pipe.GetWorldVertexPosition(i);
            Vector3 handleDir = pipe.GetVertexHandleDirWorld(i);
            float worldLen = pipe.Vertices[i].handleLength * pipe.transform.lossyScale.z;

            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 endpointWorld = vertexWorld + handleDir * worldLen * s;
                Vector2 screenPos = HandleUtility.WorldToGUIPoint(endpointWorld);
                float dist = Vector2.Distance(mousePos, screenPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    vertexIdx = i;
                    side = s;
                }
            }
        }
    }

    static int DetectGreypipeVertexHover(SceneView sv, Vector2 mousePos, Greypipe pipe)
    {
        float bestDist = EdgeHoverPx * 2f;
        int bestIdx = -1;

        for (int i = 0; i < pipe.Vertices.Count; i++)
        {
            Vector3 worldPos = pipe.GetWorldVertexPosition(i);
            Vector2 screenPos = HandleUtility.WorldToGUIPoint(worldPos);
            float dist = Vector2.Distance(mousePos, screenPos);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx  = i;
            }
        }

        return bestIdx;
    }

    // ─── Spline segment hit-testing ─────────────────────────────

    static bool DetectGreypipeSplineHover(SceneView sv, Vector2 mousePos, Greypipe pipe,
        out int segmentIndex, out float segmentT)
    {
        segmentIndex = -1;
        segmentT = 0f;
        float bestDist = EdgeHoverPx * 2f;

        for (int seg = 0; seg < pipe.SegmentCount; seg++)
        {
            const int samples = 20;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 worldPt = pipe.EvaluateSplineWorld(seg, t);
                Vector2 screenPt = HandleUtility.WorldToGUIPoint(worldPt);
                float dist = Vector2.Distance(mousePos, screenPt);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    segmentIndex = seg;
                    segmentT = t;
                }
            }
        }

        return segmentIndex >= 0;
    }

    // ─── Vertex Transform ───────────────────────────────────────

    static void BeginGreypipeVertexTransform(Greypipe pipe, int vertexIndex, Mode mode, Vector2 mousePos)
    {
        greypipeTarget         = pipe;
        greypipeSelectedVertex = vertexIndex;
        greypipeVertexMode     = mode;
        greypipeStartVertex    = pipe.Vertices[vertexIndex];

        // Open a fresh undo group so the registrations below + all future drag/release mutations
        // collapse into a single undo entry. Without IncrementCurrentGroup, later code paths
        // (HandleReady's re-registration) can land in a later group and split the entry in two.
        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        // Transform uses RecordObject so Unity's change-tracking captures the post-drag state for redo.
        // Component uses RegisterCompleteObjectUndo because the vertex array mutations aren't tracked
        // by the property-diff system.
        Undo.RecordObject(pipe.transform, $"Greypipe Vertex {mode}");
        Undo.RegisterCompleteObjectUndo(pipe, $"Greypipe Vertex {mode}");
        Undo.SetCurrentGroupName($"Greypipe Vertex {mode}");

        Vector3 vertexWorld = pipe.GetWorldVertexPosition(vertexIndex);
        Vector3 mainAxis    = pipe.MainAxisWorld;

        // Edge-vertex move triggers reshape mode — capture relative positions so all interior
        // vertices follow as the axis changes. Skips when only 2 vertices (no interior to remap).
        bool isEdge = vertexIndex == 0 || vertexIndex == pipe.Vertices.Count - 1;
        greypipeReshapeMode = isEdge && mode == Mode.Move && pipe.Vertices.Count > 2;
        if (greypipeReshapeMode)
        {
            greypipeEdgeSnapshot       = pipe.CaptureEdgeAxisRelativePositions();
            greypipeMovingEdgeIdx      = vertexIndex;
            int fixedIdx               = vertexIndex == 0 ? pipe.Vertices.Count - 1 : 0;
            greypipeFixedEdgeWorldPos  = pipe.GetWorldVertexPosition(fixedIdx);
        }

        greypipeDragPlanePoint  = vertexWorld;
        greypipeDragPlaneNormal = ComputeMainPlaneNormal(mainAxis);

        switch (mode)
        {
            case Mode.Move:
                RaycastPlane(mousePos, greypipeDragPlanePoint, greypipeDragPlaneNormal, out greypipeDragHitStart);
                break;
            case Mode.Scale:
                greypipeDragStartDist = mousePos.x;
                break;
        }
    }

    static void BeginGreypipeBezierHandleDrag(Greypipe pipe, int vertexIndex, int side, Vector2 mousePos)
    {
        greypipeTarget             = pipe;
        greypipeBezierHandleVertex = vertexIndex;
        greypipeBezierHandleSide   = side;
        greypipeStartVertex        = pipe.Vertices[vertexIndex];

        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(pipe, "Greypipe Bezier Handle");
        Undo.SetCurrentGroupName("Greypipe Bezier Handle");

        Vector3 vertexWorld = pipe.GetWorldVertexPosition(vertexIndex);
        Vector3 handleDir   = pipe.GetVertexHandleDirWorld(vertexIndex);
        float   worldLen    = pipe.Vertices[vertexIndex].handleLength * pipe.transform.lossyScale.z;
        greypipeBezierStartEndpointWorld = vertexWorld + handleDir * worldLen * side;

        Vector3 mainAxis = pipe.MainAxisWorld;
        // Plane sits at the dragged endpoint (so feedback gizmo draws there too).
        greypipeDragPlanePoint  = greypipeBezierStartEndpointWorld;
        greypipeDragPlaneNormal = ComputeMainPlaneNormal(mainAxis);
        RaycastPlane(mousePos, greypipeDragPlanePoint, greypipeDragPlaneNormal, out greypipeDragHitStart);
    }

    static Vector3 ComputeMainPlaneNormal(Vector3 mainAxis)
    {
        Vector3 normal = Vector3.Cross(mainAxis, Vector3.up).normalized;
        if (normal.sqrMagnitude < 0.01f)
            normal = Vector3.Cross(mainAxis, Vector3.right).normalized;
        return normal;
    }

    static void ApplyGreypipeVertexDrag(Event e, SceneView sv)
    {
        if (greypipeTarget == null) return;
        Vector2 mousePos = e.mousePosition;

        // Bezier handle endpoint drag
        if (greypipeBezierHandleVertex >= 0)
        {
            ApplyGreypipeBezierHandleDrag(e, mousePos);
            greypipeTarget.RebuildMesh();
            EditorUtility.SetDirty(greypipeTarget);
            return;
        }

        // Special mode: extend or girth drag
        if (greypipeExtending)
        {
            ApplyGreypipeExtendDrag(mousePos);
            greypipeTarget.RebuildMesh();
            EditorUtility.SetDirty(greypipeTarget);
            return;
        }

        if (greypipeGirthDragging)
        {
            ApplyGreypipeGirthDrag(mousePos);
            greypipeTarget.RebuildMesh();
            EditorUtility.SetDirty(greypipeTarget);
            return;
        }

        // Vertex transform mode
        if (greypipeSelectedVertex < 0) return;

        switch (greypipeVertexMode)
        {
            case Mode.Move:
                ApplyGreypipeVertexMove(mousePos, e.button);
                break;
            case Mode.Scale:
                ApplyGreypipeVertexScale(mousePos);
                break;
        }

        greypipeTarget.RebuildMesh();
        EditorUtility.SetDirty(greypipeTarget);
    }

    static void ApplyGreypipeVertexMove(Vector2 mousePos, int button)
    {
        Vector3 delta;
        if (button == 1)
        {
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
            float currentDist = ProjectRayOntoLine(mouseRay, greypipeDragPlanePoint, greypipeDragPlaneNormal);
            float startDist = ProjectRayOntoLine(
                HandleUtility.GUIPointToWorldRay(mousePressPos), greypipeDragPlanePoint, greypipeDragPlaneNormal);
            delta = greypipeDragPlaneNormal * (currentDist - startDist);
        }
        else
        {
            if (!RaycastPlane(mousePos, greypipeDragPlanePoint, greypipeDragPlaneNormal, out Vector3 hit)) return;
            delta = hit - greypipeDragHitStart;
        }

        var verts = greypipeTarget.Vertices;
        var xform = greypipeTarget.transform;
        bool altHeld = Event.current.alt;

        // Alt + edge drag: snapshot all other vertices' world state before moving,
        // then restore after — so only the dragged edge vertex moves.
        Vector3[] snapshotWorldPos = null;
        Vector3[] snapshotWorldDir = null;
        float[] snapshotHandleLen = null;
        if (greypipeReshapeMode && altHeld)
        {
            int count = verts.Count;
            snapshotWorldPos = new Vector3[count];
            snapshotWorldDir = new Vector3[count];
            snapshotHandleLen = new float[count];
            for (int i = 0; i < count; i++)
            {
                snapshotWorldPos[i] = xform.TransformPoint(verts[i].position);
                snapshotWorldDir[i] = greypipeTarget.GetVertexHandleDirWorld(i);
                snapshotHandleLen[i] = verts[i].handleLength;
            }
        }

        var v = greypipeStartVertex;
        v.position = xform.InverseTransformPoint(
            xform.TransformPoint(greypipeStartVertex.position) + delta);
        verts[greypipeSelectedVertex] = v;

        if (greypipeReshapeMode && altHeld && snapshotWorldPos != null)
        {
            for (int i = 0; i < verts.Count; i++)
            {
                if (i == greypipeSelectedVertex) continue;
                var vert = verts[i];
                vert.position = xform.InverseTransformPoint(snapshotWorldPos[i]);
                vert.handleLength = snapshotHandleLen[i];
                verts[i] = vert;
                greypipeTarget.SetVertexHandleDirLocal(i,
                    xform.InverseTransformDirection(snapshotWorldDir[i]));
            }
        }
        else if (greypipeReshapeMode && greypipeEdgeSnapshot != null)
        {
            greypipeTarget.ApplyEdgeAxisRelativePositions(greypipeEdgeSnapshot);
        }
    }

    static void ApplyGreypipeBezierHandleDrag(Event e, Vector2 mousePos)
    {
        // Compute drag delta with the same mode plane logic as vertex move:
        //   LMB → in the Main Plane (mainAxis × worldUp)
        //   RMB → perpendicular to the Main Plane
        Vector3 delta;
        if (e.button == 1)
        {
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
            float currentDist = ProjectRayOntoLine(mouseRay, greypipeDragPlanePoint, greypipeDragPlaneNormal);
            float startDist   = ProjectRayOntoLine(
                HandleUtility.GUIPointToWorldRay(mousePressPos), greypipeDragPlanePoint, greypipeDragPlaneNormal);
            delta = greypipeDragPlaneNormal * (currentDist - startDist);
        }
        else
        {
            if (!RaycastPlane(mousePos, greypipeDragPlanePoint, greypipeDragPlaneNormal, out Vector3 hit)) return;
            delta = hit - greypipeDragHitStart;
        }

        Vector3 vertexWorld   = greypipeTarget.GetWorldVertexPosition(greypipeBezierHandleVertex);
        Vector3 worldEndpoint = greypipeBezierStartEndpointWorld + delta;
        Vector3 worldDir      = worldEndpoint - vertexWorld;

        // Back-endpoint drag flips: handleRotation always represents the "forward" side.
        if (greypipeBezierHandleSide < 0) worldDir = -worldDir;

        float worldLen = worldDir.magnitude;
        if (worldLen < 0.0001f) return;

        Vector3 localDir = greypipeTarget.transform.InverseTransformDirection(worldDir.normalized);
        greypipeTarget.SetVertexHandleDirLocal(greypipeBezierHandleVertex, localDir);

        float scale = greypipeTarget.transform.lossyScale.z;
        if (Mathf.Abs(scale) < 0.0001f) scale = 1f;

        var verts = greypipeTarget.Vertices;
        var v = verts[greypipeBezierHandleVertex];
        v.handleLength = Mathf.Max(0.001f, worldLen / Mathf.Abs(scale));
        verts[greypipeBezierHandleVertex] = v;
    }

    static void ApplyGreypipeVertexScale(Vector2 mousePos)
    {
        float delta = (mousePos.x - greypipeDragStartDist) * 0.01f;
        float factor = Mathf.Max(0.01f, 1f + delta);

        var verts = greypipeTarget.Vertices;
        var v = verts[greypipeSelectedVertex];
        v.handleLength = greypipeStartVertex.handleLength * factor;
        verts[greypipeSelectedVertex] = v;
    }

    // ─── Special Mode (Q held on Greypipe) ──────────────────────

    static void HandleGreypipeSpecialIdle(Event e, SceneView sv, Greypipe pipe, Transform[] selected)
    {
        // Update hover for rendering
        if (e.type == EventType.MouseMove)
        {
            greypipeHoveredVertex = DetectGreypipeVertexHover(sv, e.mousePosition, pipe);
            sv.Repaint();
        }

        if (e.type != EventType.MouseDown) return;

        int hoveredVtx = DetectGreypipeVertexHover(sv, e.mousePosition, pipe);

        if (e.button == 1) // RMB: delete vertex
        {
            if (hoveredVtx >= 0)
            {
                Undo.IncrementCurrentGroup();
                int g = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(pipe.transform, "Delete Greypipe Vertex");
                Undo.RegisterCompleteObjectUndo(pipe, "Delete Greypipe Vertex");
                if (pipe.RemoveVertex(hoveredVtx))
                {
                    pipe.RecenterPivot();
                    pipe.RebuildMesh();
                    EditorUtility.SetDirty(pipe);
                }
                Undo.SetCurrentGroupName("Delete Greypipe Vertex");
                Undo.CollapseUndoOperations(g);
            }
            // Always consume in Special mode so RMB doesn't trigger scene-view nav.
            e.Use();
            return;
        }

        if (e.button == 0) // LMB: insert vertex on spline, or extend from edge
        {
            bool isEdge = hoveredVtx == 0 || hoveredVtx == pipe.Vertices.Count - 1;

            if (hoveredVtx >= 0 && isEdge)
            {
                // Begin extend drag from edge vertex
                greypipeExtending      = true;
                greypipeExtendEdgeIndex = hoveredVtx;
                greypipeTarget         = pipe;
                greypipeSelectedVertex = hoveredVtx;

                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(pipe.transform, "Extend Greypipe");
                Undo.RegisterCompleteObjectUndo(pipe, "Extend Greypipe");
                Undo.SetCurrentGroupName("Extend Greypipe");

                // Create the new vertex at the edge position initially
                Vector3 edgeWorld = pipe.GetWorldVertexPosition(hoveredVtx);
                Vector3 localPos  = pipe.Vertices[hoveredVtx].position;
                pipe.ExtendFromEdge(hoveredVtx, localPos);

                // The new edge vertex is now at index 0 (if extending from start) or Count-1 (if from end)
                int newIdx = hoveredVtx == 0 ? 0 : pipe.Vertices.Count - 1;
                greypipeSelectedVertex = newIdx;

                greypipeDragPlanePoint  = edgeWorld;
                greypipeDragPlaneNormal = ComputeMainPlaneNormal(pipe.MainAxisWorld);
                RaycastPlane(e.mousePosition, greypipeDragPlanePoint, greypipeDragPlaneNormal, out greypipeDragHitStart);

                greypipeStartVertex = pipe.Vertices[newIdx];

                activeMode       = Mode.Special;
                dragTargets      = selected;
                SnapshotTransforms();
                selectionPivot   = ComputePivot();
                dragButton       = 0;
                shiftHeldOnPress = false;
                didDuplicate     = false;
                phase            = Phase.Ready;
                mousePressPos    = e.mousePosition;

                int id = GUIUtility.GetControlID(FocusType.Passive);
                HandleUtility.AddDefaultControl(id);
                GUIUtility.hotControl = id;
                e.Use();
                return;
            }

            // LMB on spline (not a vertex): insert new vertex
            if (DetectGreypipeSplineHover(sv, e.mousePosition, pipe, out int segIdx, out float segT))
            {
                Undo.IncrementCurrentGroup();
                int g = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(pipe.transform, "Insert Greypipe Vertex");
                Undo.RegisterCompleteObjectUndo(pipe, "Insert Greypipe Vertex");
                pipe.InsertVertex(segIdx, segT);
                pipe.RecenterPivot();
                pipe.RebuildMesh();
                EditorUtility.SetDirty(pipe);
                Undo.SetCurrentGroupName("Insert Greypipe Vertex");
                Undo.CollapseUndoOperations(g);
            }
            e.Use();
            return;
        }

        if (e.button == 2) // MMB: girth adjustment
        {
            if (hoveredVtx >= 0)
            {
                // Quick click = reset girth to 1.0
                // We handle drag in the drag path
                greypipeGirthDragging   = true;
                greypipeGirthVertex     = hoveredVtx;
                greypipeGirthStartMult  = pipe.Vertices[hoveredVtx].girthMultiplier;
                greypipeGirthStartMouseY = e.mousePosition.y;
                greypipeTarget          = pipe;

                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(pipe, "Greypipe Girth");
                Undo.SetCurrentGroupName("Greypipe Girth");

                activeMode       = Mode.Special;
                dragTargets      = selected;
                SnapshotTransforms();
                selectionPivot   = ComputePivot();
                dragButton       = 2;
                shiftHeldOnPress = false;
                didDuplicate     = false;
                phase            = Phase.Ready;
                mousePressPos    = e.mousePosition;

                int id = GUIUtility.GetControlID(FocusType.Passive);
                HandleUtility.AddDefaultControl(id);
                GUIUtility.hotControl = id;
            }
            // Always consume — MMB shouldn't bleed through to scene-nav even when no vertex hit.
            e.Use();
            return;
        }
    }

    // ─── Special Mode Drag Implementations ────────────────────────

    static void ApplyGreypipeExtendDrag(Vector2 mousePos)
    {
        if (!RaycastPlane(mousePos, greypipeDragPlanePoint, greypipeDragPlaneNormal, out Vector3 hit)) return;
        Vector3 delta = hit - greypipeDragHitStart;
        Vector3 newWorldPos = greypipeTarget.transform.TransformPoint(greypipeStartVertex.position) + delta;
        Vector3 newLocalPos = greypipeTarget.transform.InverseTransformPoint(newWorldPos);

        var verts = greypipeTarget.Vertices;
        var v = verts[greypipeSelectedVertex];
        v.position = newLocalPos;
        verts[greypipeSelectedVertex] = v;

        Vector3 toNew = (newLocalPos - greypipeStartVertex.position);
        if (toNew.sqrMagnitude > 0.001f)
        {
            Vector3 handleDir = greypipeExtendEdgeIndex == 0 ? -toNew.normalized : toNew.normalized;
            greypipeTarget.SetVertexHandleDirLocal(greypipeSelectedVertex, handleDir);
            v = verts[greypipeSelectedVertex];
            v.handleLength = Mathf.Max(0.01f, toNew.magnitude / 3f);
            verts[greypipeSelectedVertex] = v;
        }
    }

    static void ApplyGreypipeGirthDrag(Vector2 mousePos)
    {
        float deltaY = greypipeGirthStartMouseY - mousePos.y;
        float factor = Mathf.Max(0.01f, greypipeGirthStartMult + deltaY * 0.005f);

        var verts = greypipeTarget.Vertices;
        var v = verts[greypipeGirthVertex];
        v.girthMultiplier = factor;
        verts[greypipeGirthVertex] = v;
    }

    static void HandleGreypipeGirthRelease()
    {
        if (greypipeTarget == null) return;

        // Girth quick-click → reset girth to 1.0
        if (greypipeGirthDragging)
        {
            float dragDist = Mathf.Abs(greypipeGirthStartMouseY - Event.current.mousePosition.y);
            if (dragDist < DragThresholdPx)
            {
                var verts = greypipeTarget.Vertices;
                var v = verts[greypipeGirthVertex];
                v.girthMultiplier = 1f;
                verts[greypipeGirthVertex] = v;
            }
        }

        // Recenter the object's pivot after any spline edit that changes geometry.
        // Girth-only and bezier-handle-only changes don't move vertices, so skip recenter for those.
        bool vertexMoved = greypipeExtending
            || (greypipeSelectedVertex >= 0 && !greypipeGirthDragging && greypipeBezierHandleVertex < 0);
        if (vertexMoved)
        {
            // Re-record transform + component RIGHT BEFORE the mutation so undo
            // captures the pre-recenter state. The drag-start registration may be
            // collapsed away by undoGroup before that point.
            Undo.RecordObject(greypipeTarget.transform, "Greypipe Recenter");
            Undo.RecordObject(greypipeTarget, "Greypipe Recenter");
            greypipeTarget.RecenterPivot();
        }

        greypipeTarget.RebuildMesh();
        EditorUtility.SetDirty(greypipeTarget);
        EditorUtility.SetDirty(greypipeTarget.transform);
    }

    // ─── Drawing ────────────────────────────────────────────────

    /// <summary>
    /// Returns the QT mode currently relevant for visual feedback — either the
    /// active drag mode, or the held key in idle/preview.
    /// </summary>
    static Mode GetCurrentDisplayMode()
    {
        if (phase != Phase.Idle && activeMode != Mode.None) return activeMode;
        return GetHeldMode();
    }

    static readonly Color s_splineColor        = new Color(0.3f, 0.8f, 1f, 0.8f);
    static readonly Color s_vertexHandleColor  = new Color(0.3f, 0.8f, 1f, 1f);
    static readonly Color s_vertexHoverColor   = new Color(1f,   0.9f, 0.3f, 1f);
    static readonly Color s_bezierHandleColor  = new Color(0.6f, 0.6f, 0.6f, 0.6f);
    static readonly Color s_dragPlaneFill      = new Color(1f,   0.9f, 0.3f, 0.15f);
    static readonly Color s_dragPlaneOutline   = new Color(1f,   0.9f, 0.3f, 0.6f);

    static void DrawGreypipeDragPlane()
    {
        if (greypipeTarget == null) return;
        if (greypipeSelectedVertex < 0 && greypipeBezierHandleVertex < 0) return;
        if (greypipeDragPlaneNormal.sqrMagnitude < 0.0001f) return;

        Vector3 center = greypipeDragPlanePoint;
        Vector3 normal = greypipeDragPlaneNormal.normalized;

        Vector3 axisA = Vector3.Cross(normal, Vector3.up);
        if (axisA.sqrMagnitude < 0.001f) axisA = Vector3.Cross(normal, Vector3.right);
        axisA.Normalize();
        Vector3 axisB = Vector3.Cross(normal, axisA).normalized;

        float size = HandleUtility.GetHandleSize(center) * 1.2f;
        Vector3 a = center + (axisA + axisB) * size;
        Vector3 b = center + (axisA - axisB) * size;
        Vector3 c = center + (-axisA - axisB) * size;
        Vector3 d = center + (-axisA + axisB) * size;

        // Match plane colors to the active mode
        Mode planeMode = GetCurrentDisplayMode();
        Color baseColor = planeMode != Mode.None ? GetModeColor(planeMode, 1f) : s_vertexHoverColor;
        Color fill = baseColor; fill.a    = 0.15f;
        Color outline = baseColor; outline.a = 0.6f;

        Handles.DrawSolidRectangleWithOutline(new[] { a, b, c, d }, fill, outline);
    }

    static void DrawGreypipeSpline(Greypipe pipe, int highlightVertex)
    {
        // Spline lines: still use Handles.zTest two-pass (works reliably for DrawBezier).
        var prevZTest = Handles.zTest;
        for (int pass = 0; pass < 2; pass++)
        {
            bool occluded = pass == 1;
            Handles.zTest = occluded
                ? UnityEngine.Rendering.CompareFunction.Greater
                : UnityEngine.Rendering.CompareFunction.LessEqual;
            Color color = occluded ? Occluded(s_splineColor) : s_splineColor;

            for (int seg = 0; seg < pipe.SegmentCount; seg++)
            {
                pipe.GetSegmentControlPoints(seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
                Vector3 wp0 = pipe.transform.TransformPoint(p0);
                Vector3 wp1 = pipe.transform.TransformPoint(p1);
                Vector3 wp2 = pipe.transform.TransformPoint(p2);
                Vector3 wp3 = pipe.transform.TransformPoint(p3);
                Handles.DrawBezier(wp0, wp3, wp1, wp2, color, null, 2f);
            }
        }
        Handles.zTest = prevZTest;
    }

    /// <summary>Faded variant of a color, used when a gizmo is occluded by other geometry.</summary>
    static Color Occluded(Color c) => new Color(c.r, c.g, c.b, c.a * 0.30f);

    /// <summary>
    /// Returns true if the world-space point is occluded from the scene camera by any
    /// collider that is NOT part of the given Greypipe (the pipe occludes its own vertices
    /// trivially because they sit on the tube's center axis).
    /// </summary>
    static bool IsWorldPointOccluded(Vector3 worldPoint, Greypipe pipe)
    {
        var sv = SceneView.lastActiveSceneView;
        if (sv == null || sv.camera == null) return false;
        Vector3 camPos = sv.camera.transform.position;
        Vector3 toPoint = worldPoint - camPos;
        float dist = toPoint.magnitude;
        if (dist < 0.001f) return false;
        Vector3 dir = toPoint / dist;

        // Pull the hit test slightly short so we don't self-occlude on the target point.
        var hits = Physics.RaycastAll(camPos, dir, dist - 0.01f, ~0, QueryTriggerInteraction.Ignore);
        Transform pipeT = pipe.transform;
        for (int i = 0; i < hits.Length; i++)
        {
            var col = hits[i].collider;
            if (col == null) continue;
            // Ignore colliders belonging to the Greypipe itself.
            if (col.transform == pipeT || col.transform.IsChildOf(pipeT)) continue;
            return true;
        }
        return false;
    }

    static void DrawGreypipeVertexHandles(Greypipe pipe, int highlightIndex)
    {
        Mode highlightMode = GetCurrentDisplayMode();
        Color highlightColor = highlightMode != Mode.None
            ? GetModeColor(highlightMode, 1f)
            : s_vertexHoverColor;

        // Pre-compute occlusion for each vertex and each handle endpoint.
        int n = pipe.Vertices.Count;
        var worldPositions  = new Vector3[n];
        var occludedVertex  = new bool[n];
        for (int i = 0; i < n; i++)
        {
            worldPositions[i] = pipe.GetWorldVertexPosition(i);
            occludedVertex[i] = IsWorldPointOccluded(worldPositions[i], pipe);
        }

        for (int i = 0; i < n; i++)
        {
            Vector3 worldPos = worldPositions[i];
            float handleSize = HandleUtility.GetHandleSize(worldPos) * 0.08f;

            bool isHighlighted = i == highlightIndex;
            bool isEdge = i == 0 || i == n - 1;
            bool vertOccluded = occludedVertex[i];

            Color vColor = isHighlighted ? highlightColor : s_vertexHandleColor;
            Handles.color = vertOccluded ? Occluded(vColor) : vColor;

            if (isEdge)
                Handles.CubeHandleCap(0, worldPos, Quaternion.identity, handleSize * 2f, EventType.Repaint);
            else
                Handles.SphereHandleCap(0, worldPos, Quaternion.identity, handleSize * 2f, EventType.Repaint);

            Vector3 handleDir = pipe.GetVertexHandleDirWorld(i);
            float worldHandleLen = pipe.Vertices[i].handleLength * pipe.transform.lossyScale.z;
            Vector3 handleA = worldPos + handleDir * worldHandleLen;
            Vector3 handleB = worldPos - handleDir * worldHandleLen;

            bool occA = IsWorldPointOccluded(handleA, pipe);
            bool occB = IsWorldPointOccluded(handleB, pipe);

            // Bezier-handle line color is averaged from its two endpoints' occlusion states.
            Color lineColor = s_bezierHandleColor;
            Handles.color = (occA && occB) ? Occluded(lineColor) : lineColor;
            Handles.DrawLine(handleA, worldPos);
            Handles.DrawLine(worldPos, handleB);

            float dotSize = handleSize * 0.8f;
            bool isMoveMode = (GetCurrentDisplayMode() == Mode.Move);
            bool hoverA = isMoveMode && greypipeHoveredBezierVertex == i && greypipeHoveredBezierSide > 0;
            bool hoverB = isMoveMode && greypipeHoveredBezierVertex == i && greypipeHoveredBezierSide < 0;
            bool selA   = greypipeBezierHandleVertex == i && greypipeBezierHandleSide > 0;
            bool selB   = greypipeBezierHandleVertex == i && greypipeBezierHandleSide < 0;

            Color dotAColor = (hoverA || selA) ? highlightColor : s_bezierHandleColor;
            Handles.color = occA ? Occluded(dotAColor) : dotAColor;
            Handles.DotHandleCap(0, handleA, Quaternion.identity, (hoverA || selA) ? dotSize * 1.4f : dotSize, EventType.Repaint);

            Color dotBColor = (hoverB || selB) ? highlightColor : s_bezierHandleColor;
            Handles.color = occB ? Occluded(dotBColor) : dotBColor;
            Handles.DotHandleCap(0, handleB, Quaternion.identity, (hoverB || selB) ? dotSize * 1.4f : dotSize, EventType.Repaint);
        }
    }
}
#endif
