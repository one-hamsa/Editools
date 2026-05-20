#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

static partial class QuickTransform
{
    // ─── Greyroad State ─────────────────────────────────────────

    static Greyroad greyroadTarget;
    static int      greyroadSelectedVertex = -1;
    static int      greyroadHoveredVertex  = -1;
    static int      greyroadHoveredFace    = -1;
    static Mode     greyroadVertexMode;

    // Vertex drag state (mirrors the Greypipe vertex/handle drag pattern, but uses
    // a horizontal Main Plane — drag plane normal = world up, RMB drags vertically.)
    static Greyroad.RoadVertex greyroadStartVertex;
    static Vector3 greyroadDragPlanePoint;
    static Vector3 greyroadDragPlaneNormal;
    static Vector3 greyroadDragHitStart;
    static float   greyroadDragStartDist;

    // Bezier handle endpoint drag state
    static int     greyroadBezierHandleVertex = -1;
    static int     greyroadBezierHandleSide;
    static int     greyroadHoveredBezierVertex = -1;
    static int     greyroadHoveredBezierSide;
    static Vector3 greyroadBezierStartEndpointWorld;

    // Banking handle drag state
    static int     greyroadBankingHandleVertex = -1;
    static int     greyroadBankingHandleSide;
    static int     greyroadHoveredBankingVertex = -1;
    static int     greyroadHoveredBankingSide;
    static float   greyroadBankingStartAngle;
    static Vector3 greyroadBankingTangentWorld;
    static Vector3 greyroadBankingDragPlanePoint;
    static Vector2 greyroadBankingStartMouse;

    // Special-mode drag state
    static bool  greyroadExtending;
    static int   greyroadExtendEdgeIndex;
    static bool  greyroadWidthDragging;
    static int   greyroadWidthVertex;
    static float greyroadWidthStartMult;
    static float greyroadWidthStartMouseY;

    static void ResetGreyroadState()
    {
        greyroadTarget               = null;
        greyroadSelectedVertex       = -1;
        greyroadHoveredVertex        = -1;
        greyroadHoveredFace          = -1;
        greyroadExtending            = false;
        greyroadWidthDragging        = false;
        greyroadBezierHandleVertex   = -1;
        greyroadHoveredBezierVertex  = -1;
        greyroadBankingHandleVertex  = -1;
        greyroadHoveredBankingVertex = -1;
    }

    // ─── Hover Detection ────────────────────────────────────────

    static void DetectGreyroadHover(SceneView sv, Vector2 mousePos)
    {
        greyroadHoveredVertex        = -1;
        greyroadHoveredBezierVertex  = -1;
        greyroadHoveredBankingVertex = -1;
        greyroadHoveredFace          = -1;

        var sel = Selection.transforms;
        if (sel == null || sel.Length != 1) return;
        var road = sel[0].GetComponent<Greyroad>();
        if (road == null) return;

        var mode = GetHeldMode();
        if (mode == Mode.Rotate) return;

        greyroadHoveredVertex = DetectGreyroadVertexHover(sv, mousePos, road);
        if (greyroadHoveredVertex >= 0) return;

        if (mode == Mode.Move)
        {
            DetectGreyroadBezierHover(sv, mousePos, road, out greyroadHoveredBezierVertex, out greyroadHoveredBezierSide);
            if (greyroadHoveredBezierVertex >= 0) return;

            DetectGreyroadBankingHover(sv, mousePos, road,
                out greyroadHoveredBankingVertex, out greyroadHoveredBankingSide);
        }
        else if (mode == Mode.Special)
        {
            greyroadHoveredFace = DetectGreyroadFaceHoverAll(sv, mousePos, road);
        }
    }

    static int DetectGreyroadVertexHover(SceneView sv, Vector2 mousePos, Greyroad road)
    {
        float bestDist = EdgeHoverPx * 2f;
        int bestIdx = -1;
        for (int i = 0; i < road.Vertices.Count; i++)
        {
            Vector3 worldPos = road.GetWorldVertexPosition(i);
            Vector2 screenPos = HandleUtility.WorldToGUIPoint(worldPos);
            float dist = Vector2.Distance(mousePos, screenPos);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    static void DetectGreyroadBezierHover(SceneView sv, Vector2 mousePos, Greyroad road, out int vertexIdx, out int side)
    {
        vertexIdx = -1;
        side = 0;
        float bestDist = EdgeHoverPx * 2f;

        for (int i = 0; i < road.Vertices.Count; i++)
        {
            Vector3 vertexWorld = road.GetWorldVertexPosition(i);
            Vector3 handleDir   = road.GetVertexHandleDirWorld(i);
            float worldLen      = road.Vertices[i].handleLength * road.transform.lossyScale.z;

            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 endpointWorld = vertexWorld + handleDir * worldLen * s;
                Vector2 screenPos = HandleUtility.WorldToGUIPoint(endpointWorld);
                float dist = Vector2.Distance(mousePos, screenPos);
                if (dist < bestDist) { bestDist = dist; vertexIdx = i; side = s; }
            }
        }
    }

    /// <summary>Smaller hit radius — banking handles always lose ties to spline handles.</summary>
    static void DetectGreyroadBankingHover(SceneView sv, Vector2 mousePos, Greyroad road, out int vertexIdx, out int side)
    {
        vertexIdx = -1;
        side = 0;
        float bestDist = EdgeHoverPx * 1.2f;

        for (int i = 0; i < road.Vertices.Count; i++)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 endpointWorld = road.GetBankingHandleWorld(i, s);
                Vector2 screenPos = HandleUtility.WorldToGUIPoint(endpointWorld);
                float dist = Vector2.Distance(mousePos, screenPos);
                if (dist < bestDist) { bestDist = dist; vertexIdx = i; side = s; }
            }
        }
    }

    static bool DetectGreyroadSplineHover(SceneView sv, Vector2 mousePos, Greyroad road,
        out int segmentIndex, out float segmentT)
    {
        segmentIndex = -1;
        segmentT = 0f;
        float bestDist = EdgeHoverPx * 2f;

        for (int seg = 0; seg < road.SegmentCount; seg++)
        {
            const int samples = 20;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 worldPt = road.EvaluateSplineWorld(seg, t);
                Vector2 screenPt = HandleUtility.WorldToGUIPoint(worldPt);
                float dist = Vector2.Distance(mousePos, screenPt);
                if (dist < bestDist) { bestDist = dist; segmentIndex = seg; segmentT = t; }
            }
        }
        return segmentIndex >= 0;
    }

    // ─── Vertex Transform ───────────────────────────────────────

    /// <summary>
    /// Greyroad's Main Plane is horizontal (XZ): drag-plane normal = world up.
    /// RMB drags perpendicular (vertical / Y elevation).
    /// </summary>
    static Vector3 ComputeRoadMainPlaneNormal()
    {
        return Vector3.up;
    }

    static void BeginGreyroadVertexTransform(Greyroad road, int vertexIndex, Mode mode, Vector2 mousePos)
    {
        greyroadTarget         = road;
        greyroadSelectedVertex = vertexIndex;
        greyroadVertexMode     = mode;
        greyroadStartVertex    = road.Vertices[vertexIndex];

        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(road.transform, $"Greyroad Vertex {mode}");
        Undo.RegisterCompleteObjectUndo(road, $"Greyroad Vertex {mode}");
        Undo.SetCurrentGroupName($"Greyroad Vertex {mode}");

        Vector3 vertexWorld = road.GetWorldVertexPosition(vertexIndex);

        greyroadDragPlanePoint  = vertexWorld;
        greyroadDragPlaneNormal = ComputeRoadMainPlaneNormal();

        switch (mode)
        {
            case Mode.Move:
                RaycastPlane(mousePos, greyroadDragPlanePoint, greyroadDragPlaneNormal, out greyroadDragHitStart);
                break;
            case Mode.Scale:
                greyroadDragStartDist = mousePos.x;
                break;
        }
    }

    static void BeginGreyroadBezierHandleDrag(Greyroad road, int vertexIndex, int side, Vector2 mousePos)
    {
        greyroadTarget             = road;
        greyroadBezierHandleVertex = vertexIndex;
        greyroadBezierHandleSide   = side;
        greyroadStartVertex        = road.Vertices[vertexIndex];

        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(road, "Greyroad Bezier Handle");
        Undo.SetCurrentGroupName("Greyroad Bezier Handle");

        Vector3 vertexWorld = road.GetWorldVertexPosition(vertexIndex);
        Vector3 handleDir   = road.GetVertexHandleDirWorld(vertexIndex);
        float   worldLen    = road.Vertices[vertexIndex].handleLength * road.transform.lossyScale.z;
        greyroadBezierStartEndpointWorld = vertexWorld + handleDir * worldLen * side;

        greyroadDragPlanePoint  = greyroadBezierStartEndpointWorld;
        greyroadDragPlaneNormal = ComputeRoadMainPlaneNormal();
        RaycastPlane(mousePos, greyroadDragPlanePoint, greyroadDragPlaneNormal, out greyroadDragHitStart);
    }

    static void BeginGreyroadBankingHandleDrag(Greyroad road, int vertexIndex, int side, Vector2 mousePos)
    {
        greyroadTarget              = road;
        greyroadBankingHandleVertex = vertexIndex;
        greyroadBankingHandleSide   = side;
        greyroadStartVertex         = road.Vertices[vertexIndex];
        greyroadBankingStartAngle   = road.Vertices[vertexIndex].bankingAngle;
        greyroadBankingStartMouse   = mousePos;

        Undo.IncrementCurrentGroup();
        undoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(road, "Greyroad Banking");
        Undo.SetCurrentGroupName("Greyroad Banking");

        // Drag happens in the vertical plane perpendicular to the spline tangent at this vertex.
        Vector3 vertexWorld = road.GetWorldVertexPosition(vertexIndex);
        road.ComputeBankingHandleAxisWorld(vertexIndex, out Vector3 upWorld, out Vector3 rightWorld);
        greyroadBankingTangentWorld   = Vector3.Cross(rightWorld, upWorld).normalized;
        if (greyroadBankingTangentWorld.sqrMagnitude < 0.0001f)
            greyroadBankingTangentWorld = road.MainAxisWorld;
        greyroadBankingDragPlanePoint = vertexWorld;
    }

    static void ApplyGreyroadVertexDrag(Event e, SceneView sv)
    {
        if (greyroadTarget == null) return;
        Vector2 mousePos = e.mousePosition;

        if (greyroadBankingHandleVertex >= 0)
        {
            ApplyGreyroadBankingDrag(mousePos);
            greyroadTarget.RebuildMesh();
            EditorUtility.SetDirty(greyroadTarget);
            return;
        }

        if (greyroadBezierHandleVertex >= 0)
        {
            ApplyGreyroadBezierHandleDrag(e, mousePos);
            greyroadTarget.RebuildMesh();
            EditorUtility.SetDirty(greyroadTarget);
            return;
        }

        if (greyroadExtending)
        {
            ApplyGreyroadExtendDrag(mousePos);
            greyroadTarget.RebuildMesh();
            EditorUtility.SetDirty(greyroadTarget);
            return;
        }

        if (greyroadWidthDragging)
        {
            ApplyGreyroadWidthDrag(mousePos);
            greyroadTarget.RebuildMesh();
            EditorUtility.SetDirty(greyroadTarget);
            return;
        }

        if (greyroadSelectedVertex < 0) return;

        switch (greyroadVertexMode)
        {
            case Mode.Move:  ApplyGreyroadVertexMove(mousePos, e.button); break;
            case Mode.Scale: ApplyGreyroadVertexScale(mousePos);          break;
        }

        greyroadTarget.RebuildMesh();
        EditorUtility.SetDirty(greyroadTarget);
    }

    static void ApplyGreyroadVertexMove(Vector2 mousePos, int button)
    {
        Vector3 delta;
        if (button == 1)
        {
            // RMB: vertical (perpendicular to horizontal Main Plane)
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
            float currentDist = ProjectRayOntoLine(mouseRay, greyroadDragPlanePoint, greyroadDragPlaneNormal);
            float startDist   = ProjectRayOntoLine(
                HandleUtility.GUIPointToWorldRay(mousePressPos), greyroadDragPlanePoint, greyroadDragPlaneNormal);
            delta = greyroadDragPlaneNormal * (currentDist - startDist);
        }
        else
        {
            if (!RaycastPlane(mousePos, greyroadDragPlanePoint, greyroadDragPlaneNormal, out Vector3 hit)) return;
            delta = hit - greyroadDragHitStart;
        }

        // Shift constrains movement to horizontal in world space.
        if (Event.current.shift)
            delta.y = 0f;

        var verts = greyroadTarget.Vertices;
        var v = greyroadStartVertex;
        v.position = greyroadTarget.transform.InverseTransformPoint(
            greyroadTarget.transform.TransformPoint(greyroadStartVertex.position) + delta);
        verts[greyroadSelectedVertex] = v;
    }

    static void ApplyGreyroadBezierHandleDrag(Event e, Vector2 mousePos)
    {
        Vector3 delta;
        if (e.button == 1)
        {
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
            float currentDist = ProjectRayOntoLine(mouseRay, greyroadDragPlanePoint, greyroadDragPlaneNormal);
            float startDist   = ProjectRayOntoLine(
                HandleUtility.GUIPointToWorldRay(mousePressPos), greyroadDragPlanePoint, greyroadDragPlaneNormal);
            delta = greyroadDragPlaneNormal * (currentDist - startDist);
        }
        else
        {
            if (!RaycastPlane(mousePos, greyroadDragPlanePoint, greyroadDragPlaneNormal, out Vector3 hit)) return;
            delta = hit - greyroadDragHitStart;
        }

        Vector3 vertexWorld   = greyroadTarget.GetWorldVertexPosition(greyroadBezierHandleVertex);
        Vector3 worldEndpoint = greyroadBezierStartEndpointWorld + delta;
        Vector3 worldDir      = worldEndpoint - vertexWorld;

        if (greyroadBezierHandleSide < 0) worldDir = -worldDir;

        float worldLen = worldDir.magnitude;
        if (worldLen < 0.0001f) return;

        Vector3 localDir = greyroadTarget.transform.InverseTransformDirection(worldDir.normalized);
        greyroadTarget.SetVertexHandleDirLocal(greyroadBezierHandleVertex, localDir);

        float scale = greyroadTarget.transform.lossyScale.z;
        if (Mathf.Abs(scale) < 0.0001f) scale = 1f;

        var verts = greyroadTarget.Vertices;
        var v = verts[greyroadBezierHandleVertex];
        v.handleLength = Mathf.Max(0.001f, worldLen / Mathf.Abs(scale));
        verts[greyroadBezierHandleVertex] = v;
    }

    /// <summary>
    /// Banking drag: rotates the handle around the spline tangent. Drag-plane is vertical
    /// (perpendicular to tangent at the vertex). Length is fixed; only the angle changes.
    /// </summary>
    static void ApplyGreyroadBankingDrag(Vector2 mousePos)
    {
        if (!RaycastPlane(mousePos, greyroadBankingDragPlanePoint, greyroadBankingTangentWorld, out Vector3 hit))
            return;
        if (!RaycastPlane(greyroadBankingStartMouse, greyroadBankingDragPlanePoint, greyroadBankingTangentWorld,
            out Vector3 startHit))
            return;

        Vector3 fromVec = (startHit - greyroadBankingDragPlanePoint);
        Vector3 toVec   = (hit       - greyroadBankingDragPlanePoint);
        if (fromVec.sqrMagnitude < 0.0001f || toVec.sqrMagnitude < 0.0001f) return;

        // SignedAngle around the tangent (deg) → add to the starting angle. Both handles
        // rotate the same direction with banking (the -side is just diametrically opposite),
        // so no sign flip is needed when grabbing either side.
        float deltaDeg = Vector3.SignedAngle(fromVec, toVec, greyroadBankingTangentWorld);

        float newRad = greyroadBankingStartAngle + deltaDeg * Mathf.Deg2Rad;
        greyroadTarget.SetVertexBankingAngle(greyroadBankingHandleVertex, newRad);
    }

    static void ApplyGreyroadVertexScale(Vector2 mousePos)
    {
        float delta = (mousePos.x - greyroadDragStartDist) * 0.01f;
        float factor = Mathf.Max(0.01f, 1f + delta);

        var verts = greyroadTarget.Vertices;
        var v = verts[greyroadSelectedVertex];
        v.handleLength = greyroadStartVertex.handleLength * factor;
        verts[greyroadSelectedVertex] = v;
    }

    // ─── Special Mode (Q held on Greyroad) ──────────────────────

    /// <summary>
    /// Detects which face dot handle the mouse is near. Tests against the same centroid
    /// positions that DrawGreyroadFaceDots renders, so hit zones match the visuals exactly.
    /// Considers all 6 faces (including hidden ones) for re-enabling in Special mode.
    /// </summary>
    static int DetectGreyroadFaceHoverAll(SceneView sv, Vector2 mousePos, Greyroad road)
    {
        float bestDist = EdgeHoverPx * 2f;
        int bestFace = -1;
        for (int face = 0; face < 6; face++)
        {
            Vector3 center = ComputeGreyroadFaceCenter(road, face);
            Vector2 screenPt = HandleUtility.WorldToGUIPoint(center);
            float d = Vector2.Distance(mousePos, screenPt);
            if (d < bestDist) { bestDist = d; bestFace = face; }
        }
        return bestFace;
    }

    static void HandleGreyroadSpecialIdle(Event e, SceneView sv, Greyroad road, Transform[] selected)
    {
        if (e.type == EventType.MouseMove)
        {
            greyroadHoveredVertex = DetectGreyroadVertexHover(sv, e.mousePosition, road);
            greyroadHoveredFace   = DetectGreyroadFaceHoverAll(sv, e.mousePosition, road);
            sv.Repaint();
        }

        if (e.type != EventType.MouseDown) return;

        int hoveredVtx = DetectGreyroadVertexHover(sv, e.mousePosition, road);

        if (e.button == 1) // RMB: delete vertex
        {
            if (hoveredVtx >= 0)
            {
                Undo.IncrementCurrentGroup();
                int g = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(road.transform, "Delete Greyroad Vertex");
                Undo.RegisterCompleteObjectUndo(road, "Delete Greyroad Vertex");
                if (road.RemoveVertex(hoveredVtx))
                {
                    road.RecenterPivot();
                    road.RebuildMesh();
                    EditorUtility.SetDirty(road);
                }
                Undo.SetCurrentGroupName("Delete Greyroad Vertex");
                Undo.CollapseUndoOperations(g);
            }
            e.Use();
            return;
        }

        if (e.button == 0) // LMB: face toggle / insert vertex / extend
        {
            bool isEdge = hoveredVtx == 0 || hoveredVtx == road.Vertices.Count - 1;

            if (hoveredVtx >= 0 && isEdge)
            {
                greyroadExtending       = true;
                greyroadExtendEdgeIndex = hoveredVtx;
                greyroadTarget          = road;
                greyroadSelectedVertex  = hoveredVtx;

                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(road.transform, "Extend Greyroad");
                Undo.RegisterCompleteObjectUndo(road, "Extend Greyroad");
                Undo.SetCurrentGroupName("Extend Greyroad");

                Vector3 edgeWorld = road.GetWorldVertexPosition(hoveredVtx);
                Vector3 localPos  = road.Vertices[hoveredVtx].position;
                road.ExtendFromEdge(hoveredVtx, localPos);

                int newIdx = hoveredVtx == 0 ? 0 : road.Vertices.Count - 1;
                greyroadSelectedVertex = newIdx;

                greyroadDragPlanePoint  = edgeWorld;
                greyroadDragPlaneNormal = ComputeRoadMainPlaneNormal();
                RaycastPlane(e.mousePosition, greyroadDragPlanePoint, greyroadDragPlaneNormal, out greyroadDragHitStart);

                greyroadStartVertex = road.Vertices[newIdx];

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

            // Vertex is not an edge — try face handle toggle, then mid-spline insert.
            // Only toggle when clicking directly on a face dot handle, not the face area.
            if (hoveredVtx < 0 && greyroadHoveredFace >= 0)
            {
                Undo.RegisterCompleteObjectUndo(road, "Toggle Greyroad Face");
                road.ActiveFaces[greyroadHoveredFace] = !road.ActiveFaces[greyroadHoveredFace];
                road.RebuildMesh();
                EditorUtility.SetDirty(road);
                e.Use();
                return;
            }

            if (DetectGreyroadSplineHover(sv, e.mousePosition, road, out int segIdx, out float segT))
            {
                Undo.IncrementCurrentGroup();
                int g = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(road.transform, "Insert Greyroad Vertex");
                Undo.RegisterCompleteObjectUndo(road, "Insert Greyroad Vertex");
                road.InsertVertex(segIdx, segT);
                road.RecenterPivot();
                road.RebuildMesh();
                EditorUtility.SetDirty(road);
                Undo.SetCurrentGroupName("Insert Greyroad Vertex");
                Undo.CollapseUndoOperations(g);
            }
            e.Use();
            return;
        }

        if (e.button == 2) // MMB: width adjustment (analog of Greypipe girth)
        {
            if (hoveredVtx >= 0)
            {
                greyroadWidthDragging    = true;
                greyroadWidthVertex      = hoveredVtx;
                greyroadWidthStartMult   = road.Vertices[hoveredVtx].widthMultiplier;
                greyroadWidthStartMouseY = e.mousePosition.y;
                greyroadTarget           = road;

                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.RegisterCompleteObjectUndo(road, "Greyroad Width");
                Undo.SetCurrentGroupName("Greyroad Width");

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
            e.Use();
            return;
        }
    }

    static void ApplyGreyroadExtendDrag(Vector2 mousePos)
    {
        if (!RaycastPlane(mousePos, greyroadDragPlanePoint, greyroadDragPlaneNormal, out Vector3 hit)) return;
        Vector3 delta = hit - greyroadDragHitStart;
        Vector3 newWorldPos = greyroadTarget.transform.TransformPoint(greyroadStartVertex.position) + delta;

        // Shift: snap world Y to the closest existing vertex.
        if (Event.current.shift)
        {
            float bestDist = float.MaxValue;
            float bestY = newWorldPos.y;
            var allVerts = greyroadTarget.Vertices;
            for (int i = 0; i < allVerts.Count; i++)
            {
                if (i == greyroadSelectedVertex) continue;
                Vector3 wp = greyroadTarget.transform.TransformPoint(allVerts[i].position);
                float d = Vector2.Distance(
                    new Vector2(newWorldPos.x, newWorldPos.z),
                    new Vector2(wp.x, wp.z));
                if (d < bestDist) { bestDist = d; bestY = wp.y; }
            }
            newWorldPos.y = bestY;
        }

        Vector3 newLocalPos = greyroadTarget.transform.InverseTransformPoint(newWorldPos);

        var verts = greyroadTarget.Vertices;
        var v = verts[greyroadSelectedVertex];
        v.position = newLocalPos;
        verts[greyroadSelectedVertex] = v;

        Vector3 toNew = (newLocalPos - greyroadStartVertex.position);
        if (toNew.sqrMagnitude > 0.001f)
        {
            Vector3 handleDir = greyroadExtendEdgeIndex == 0 ? -toNew.normalized : toNew.normalized;
            greyroadTarget.SetVertexHandleDirLocal(greyroadSelectedVertex, handleDir);
            v = verts[greyroadSelectedVertex];
            v.handleLength = Mathf.Max(0.01f, toNew.magnitude / 3f);
            verts[greyroadSelectedVertex] = v;
        }
    }

    static void ApplyGreyroadWidthDrag(Vector2 mousePos)
    {
        float deltaY = greyroadWidthStartMouseY - mousePos.y;
        float factor = Mathf.Max(0.01f, greyroadWidthStartMult + deltaY * 0.005f);

        var verts = greyroadTarget.Vertices;
        var v = verts[greyroadWidthVertex];
        v.widthMultiplier = factor;
        verts[greyroadWidthVertex] = v;
    }

    static void HandleGreyroadWidthRelease()
    {
        if (greyroadTarget == null) return;

        if (greyroadWidthDragging)
        {
            float dragDist = Mathf.Abs(greyroadWidthStartMouseY - Event.current.mousePosition.y);
            if (dragDist < DragThresholdPx)
            {
                var verts = greyroadTarget.Vertices;
                var v = verts[greyroadWidthVertex];
                v.widthMultiplier = 1f;
                verts[greyroadWidthVertex] = v;
            }
        }

        // Cancel extend if the mouse didn't move past the drag threshold (click without drag).
        if (greyroadExtending)
        {
            float dragDist = Vector2.Distance(Event.current.mousePosition, mousePressPos);
            if (dragDist < DragThresholdPx)
            {
                Undo.PerformUndo();
                return;
            }
        }

        bool vertexMoved = greyroadExtending
            || (greyroadSelectedVertex >= 0
                && !greyroadWidthDragging
                && greyroadBezierHandleVertex < 0
                && greyroadBankingHandleVertex < 0);
        if (vertexMoved)
        {
            Undo.RecordObject(greyroadTarget.transform, "Greyroad Recenter");
            Undo.RecordObject(greyroadTarget, "Greyroad Recenter");
            greyroadTarget.RecenterPivot();
        }

        greyroadTarget.RebuildMesh();
        EditorUtility.SetDirty(greyroadTarget);
        EditorUtility.SetDirty(greyroadTarget.transform);
    }

    // ─── Banking quick-click reset (MMB or W+MMB on banking handle) ──

    static void HandleGreyroadBankingMMBReset(Greyroad road, int vertexIndex)
    {
        Undo.IncrementCurrentGroup();
        int g = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(road, "Reset Greyroad Banking");
        road.ResetVertexBanking(vertexIndex);
        road.RebuildMesh();
        EditorUtility.SetDirty(road);
        Undo.SetCurrentGroupName("Reset Greyroad Banking");
        Undo.CollapseUndoOperations(g);
    }

    // ─── Drawing ────────────────────────────────────────────────

    static readonly Color s_roadSplineColor    = new Color(0.4f, 1f,   0.7f, 0.85f);
    static readonly Color s_roadHandleColor    = new Color(0.4f, 1f,   0.7f, 1f);
    static readonly Color s_roadBezierColor    = new Color(0.6f, 0.6f, 0.6f, 0.6f);
    static readonly Color s_roadBankingColor   = new Color(1f,   0.55f, 0.2f, 0.85f);

    static void DrawGreyroadDragPlane()
    {
        if (greyroadTarget == null) return;
        if (greyroadSelectedVertex < 0
            && greyroadBezierHandleVertex < 0
            && greyroadBankingHandleVertex < 0) return;

        Vector3 center; Vector3 normal;
        if (greyroadBankingHandleVertex >= 0)
        {
            center = greyroadBankingDragPlanePoint;
            normal = greyroadBankingTangentWorld;
        }
        else
        {
            center = greyroadDragPlanePoint;
            normal = greyroadDragPlaneNormal;
        }
        if (normal.sqrMagnitude < 0.0001f) return;
        normal.Normalize();

        Vector3 axisA = Vector3.Cross(normal, Vector3.up);
        if (axisA.sqrMagnitude < 0.001f) axisA = Vector3.Cross(normal, Vector3.right);
        axisA.Normalize();
        Vector3 axisB = Vector3.Cross(normal, axisA).normalized;

        float size = HandleUtility.GetHandleSize(center) * 1.2f;
        Vector3 a = center + (axisA + axisB) * size;
        Vector3 b = center + (axisA - axisB) * size;
        Vector3 c = center + (-axisA - axisB) * size;
        Vector3 d = center + (-axisA + axisB) * size;

        Mode planeMode = GetCurrentDisplayMode();
        Color baseColor = planeMode != Mode.None ? GetModeColor(planeMode, 1f) : s_roadHandleColor;
        Color fill = baseColor; fill.a = 0.15f;
        Color outline = baseColor; outline.a = 0.6f;
        Handles.DrawSolidRectangleWithOutline(new[] { a, b, c, d }, fill, outline);
    }

    static void DrawGreyroadSpline(Greyroad road, int highlightVertex)
    {
        var prevZTest = Handles.zTest;
        for (int pass = 0; pass < 2; pass++)
        {
            bool occluded = pass == 1;
            Handles.zTest = occluded
                ? UnityEngine.Rendering.CompareFunction.Greater
                : UnityEngine.Rendering.CompareFunction.LessEqual;
            Color color = occluded ? Occluded(s_roadSplineColor) : s_roadSplineColor;

            for (int seg = 0; seg < road.SegmentCount; seg++)
            {
                road.GetSegmentControlPoints(seg, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
                Vector3 wp0 = road.transform.TransformPoint(p0);
                Vector3 wp1 = road.transform.TransformPoint(p1);
                Vector3 wp2 = road.transform.TransformPoint(p2);
                Vector3 wp3 = road.transform.TransformPoint(p3);
                Handles.DrawBezier(wp0, wp3, wp1, wp2, color, null, 2f);
            }
        }
        Handles.zTest = prevZTest;
    }

    static void DrawGreyroadVertexHandles(Greyroad road, int highlightIndex)
    {
        Mode highlightMode = GetCurrentDisplayMode();
        Color highlightColor = highlightMode != Mode.None
            ? GetModeColor(highlightMode, 1f)
            : new Color(1f, 0.9f, 0.3f, 1f);

        int n = road.Vertices.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 worldPos = road.GetWorldVertexPosition(i);
            float handleSize = HandleUtility.GetHandleSize(worldPos) * 0.08f;

            bool isHighlighted = i == highlightIndex;
            bool isEdge = i == 0 || i == n - 1;

            Color vColor = isHighlighted ? highlightColor : s_roadHandleColor;
            Handles.color = vColor;
            if (isEdge)
                Handles.CubeHandleCap(0, worldPos, Quaternion.identity, handleSize * 2f, EventType.Repaint);
            else
                Handles.SphereHandleCap(0, worldPos, Quaternion.identity, handleSize * 2f, EventType.Repaint);

            // ── Regular spline handle ──
            Vector3 handleDir = road.GetVertexHandleDirWorld(i);
            float worldHandleLen = road.Vertices[i].handleLength * road.transform.lossyScale.z;
            Vector3 handleA = worldPos + handleDir * worldHandleLen;
            Vector3 handleB = worldPos - handleDir * worldHandleLen;

            Handles.color = s_roadBezierColor;
            Handles.DrawLine(handleA, worldPos);
            Handles.DrawLine(worldPos, handleB);

            float dotSize = handleSize * 0.8f;
            bool isMoveMode = GetCurrentDisplayMode() == Mode.Move;
            bool hoverBA = isMoveMode && greyroadHoveredBezierVertex == i && greyroadHoveredBezierSide > 0;
            bool hoverBB = isMoveMode && greyroadHoveredBezierVertex == i && greyroadHoveredBezierSide < 0;
            bool selBA   = greyroadBezierHandleVertex == i && greyroadBezierHandleSide > 0;
            bool selBB   = greyroadBezierHandleVertex == i && greyroadBezierHandleSide < 0;

            Handles.color = (hoverBA || selBA) ? highlightColor : s_roadBezierColor;
            Handles.DotHandleCap(0, handleA, Quaternion.identity,
                (hoverBA || selBA) ? dotSize * 1.4f : dotSize, EventType.Repaint);
            Handles.color = (hoverBB || selBB) ? highlightColor : s_roadBezierColor;
            Handles.DotHandleCap(0, handleB, Quaternion.identity,
                (hoverBB || selBB) ? dotSize * 1.4f : dotSize, EventType.Repaint);

            // ── Banking handles ──
            Vector3 bankA = road.GetBankingHandleWorld(i, +1);
            Vector3 bankB = road.GetBankingHandleWorld(i, -1);

            Handles.color = s_roadBankingColor;
            Handles.DrawLine(bankA, worldPos);
            Handles.DrawLine(worldPos, bankB);

            float bankDotSize = handleSize * 0.55f;
            bool hoverKA = isMoveMode && greyroadHoveredBankingVertex == i && greyroadHoveredBankingSide > 0;
            bool hoverKB = isMoveMode && greyroadHoveredBankingVertex == i && greyroadHoveredBankingSide < 0;
            bool selKA   = greyroadBankingHandleVertex == i && greyroadBankingHandleSide > 0;
            bool selKB   = greyroadBankingHandleVertex == i && greyroadBankingHandleSide < 0;

            Handles.color = (hoverKA || selKA) ? highlightColor : s_roadBankingColor;
            Handles.DotHandleCap(0, bankA, Quaternion.identity,
                (hoverKA || selKA) ? bankDotSize * 1.4f : bankDotSize, EventType.Repaint);
            Handles.color = (hoverKB || selKB) ? highlightColor : s_roadBankingColor;
            Handles.DotHandleCap(0, bankB, Quaternion.identity,
                (hoverKB || selKB) ? bankDotSize * 1.4f : bankDotSize, EventType.Repaint);
        }
    }

    // ─── Special-mode bounding box + face dots ──────────────────

    /// <summary>
    /// Build the 4 world-space box corners (top-left, top-right, bottom-right, bottom-left)
    /// at each spline vertex, given the new convention where the spline runs along the TOP
    /// of the road (top h=0, bottom h=-fullH).
    /// </summary>
    static void BuildGreyroadBoxCorners(Greyroad road, out Vector3[,] corners)
    {
        int n = road.Vertices.Count;
        corners = new Vector3[n, 4];
        for (int i = 0; i < n; i++)
        {
            Vector3 pos = road.GetWorldVertexPosition(i);
            road.ComputeBankingHandleAxisWorld(i, out Vector3 upW, out Vector3 rightW);
            var v = road.Vertices[i];
            float halfW = road.BaseWidth  * 0.5f * Mathf.Max(0.001f, v.widthMultiplier);
            float fullH = road.BaseHeight        * Mathf.Max(0.001f, v.heightMultiplier);
            corners[i, 0] = pos + (-rightW * halfW);                    // top-left  (h=0)
            corners[i, 1] = pos + ( rightW * halfW);                    // top-right (h=0)
            corners[i, 2] = pos + ( rightW * halfW) + (-upW * fullH);   // bottom-right
            corners[i, 3] = pos + (-rightW * halfW) + (-upW * fullH);   // bottom-left
        }
    }

    /// <summary>
    /// Draws the road's box outline: 4 longitudinal corner edges along the spline + one
    /// cross-section rectangle at every vertex (faint at interior verts, full at the caps).
    /// </summary>
    static void DrawGreyroadBoundsOutline(Greyroad road)
    {
        int n = road.Vertices.Count;
        if (n < 2) return;
        BuildGreyroadBoxCorners(road, out Vector3[,] corners);

        // Longitudinal edges between consecutive vertices.
        Handles.color = new Color(1f, 1f, 1f, 0.55f);
        for (int seg = 0; seg < n - 1; seg++)
            for (int c = 0; c < 4; c++)
                Handles.DrawLine(corners[seg, c], corners[seg + 1, c]);

        // Cap rectangles at start + end.
        Handles.color = new Color(1f, 1f, 1f, 0.65f);
        for (int v = 0; v < n; v++)
        {
            if (v != 0 && v != n - 1) continue;
            for (int c = 0; c < 4; c++)
                Handles.DrawLine(corners[v, c], corners[v, (c + 1) % 4]);
        }

        // Faint interior cross-section rectangles for orientation.
        Handles.color = new Color(1f, 1f, 1f, 0.18f);
        for (int v = 1; v < n - 1; v++)
            for (int c = 0; c < 4; c++)
                Handles.DrawLine(corners[v, c], corners[v, (c + 1) % 4]);
    }

    /// <summary>
    /// Returns the world-space centroid of one face — used both for the face-dot handle
    /// position in Special mode and (matching) for face-hover hit detection.
    /// </summary>
    static Vector3 ComputeGreyroadFaceCenter(Greyroad road, int face)
    {
        int n = road.Vertices.Count;
        if (face == Greyroad.FaceStartCap || face == Greyroad.FaceEndCap)
        {
            int idx = face == Greyroad.FaceStartCap ? 0 : n - 1;
            Vector3 pos = road.GetWorldVertexPosition(idx);
            road.ComputeBankingHandleAxisWorld(idx, out Vector3 upW, out _);
            var vc = road.Vertices[idx];
            float fullH = road.BaseHeight * Mathf.Max(0.001f, vc.heightMultiplier);
            return pos - upW * (fullH * 0.5f);
        }

        int seg = Mathf.Clamp((n - 1) / 2, 0, Mathf.Max(0, n - 2));
        Vector3 splinePt = road.EvaluateSplineWorld(seg, 0.5f);
        int v = Mathf.Clamp(seg, 0, n - 1);
        road.ComputeBankingHandleAxisWorld(v, out Vector3 upW2, out Vector3 rightW);
        var rv = road.Vertices[v];
        float halfW = road.BaseWidth  * 0.5f * Mathf.Max(0.001f, rv.widthMultiplier);
        float fullH2 = road.BaseHeight       * Mathf.Max(0.001f, rv.heightMultiplier);
        switch (face)
        {
            case Greyroad.FaceTop:       return splinePt;
            case Greyroad.FaceBottom:    return splinePt - upW2 * fullH2;
            case Greyroad.FaceLeftSide:  return splinePt - rightW * halfW - upW2 * (fullH2 * 0.5f);
            case Greyroad.FaceRightSide: return splinePt + rightW * halfW - upW2 * (fullH2 * 0.5f);
        }
        return splinePt;
    }

    static void DrawGreyroadFaceDots(Greyroad road, int hoveredFace)
    {
        if (road.Vertices.Count < 2) return;

        var prevZ = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

        Color modeCol = GetModeColor(Mode.Special, 1f);
        Color visible = new Color(1f,   1f,   1f,   0.85f);
        Color hidden  = new Color(1f,   0.3f, 0.3f, 0.9f);

        for (int face = 0; face < 6; face++)
        {
            Vector3 center = ComputeGreyroadFaceCenter(road, face);
            bool isHovered = face == hoveredFace;
            bool isHidden  = !road.ActiveFaces[face];
            Color col = isHovered ? modeCol : (isHidden ? hidden : visible);
            Handles.color = col;
            float sz = HandleUtility.GetHandleSize(center) * (isHovered ? 0.07f : 0.05f);
            Handles.DotHandleCap(0, center, Quaternion.identity, sz, EventType.Repaint);
        }

        Handles.zTest = prevZ;
    }
}
#endif
