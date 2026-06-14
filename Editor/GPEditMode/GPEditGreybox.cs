#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Greybox behaviors for Edit Mode. The bounding box is replaced by an Outline — the actual
/// deformed edges of the visible faces — plus a dot handle at each face center.
///
///   Outline edge:  LMB drag = deform edge (Shift confines to a local axis)
///   Face handle:   LMB drag = move face along its normal
///                  Shift+LMB = skew face (slide its 4 corners together across the face plane)
///                  MMB       = hide / unhide face
///                  RMB drag  = extrude an independent box from the face
///                  Shift+RMB = extrude a seam-linked box from the face
/// </summary>
static partial class GPEdit
{
    enum GbDrag { None, Edge, FaceNormal, FaceSkew, Extrude }

    static GbDrag    s_gbDrag;
    static Greybox   s_gbTarget;
    static int       s_gbButton;
    static int       s_gbControlId;
    static int       s_gbUndoGroup;
    static Vector3[] s_gbStartCorners;

    // Edge / face drag
    static int     s_gbEdge;
    static int     s_gbFace;
    static int     s_gbEdgeAxis;
    static Vector3 s_gbPlanePoint;
    static Vector3 s_gbPlaneNormal;
    static Vector3 s_gbHitStart;
    static float   s_gbNormalStartDist;

    // Extrude
    static Greybox s_gbExtrudeNew;
    static Vector3 s_gbExtrudeCenter;
    static Vector3 s_gbExtrudeNormal;
    static float   s_gbExtrudeStartDist;

    static readonly Vector3[] s_gbWc = new Vector3[8];

    static partial void OnGreyboxSceneGUI(SceneView sv, Event e, Greybox gb)
    {
        if (s_gbDrag != GbDrag.None) { HandleGreyboxDrag(e, sv); return; }

        gb.GetWorldCorners(s_gbWc);
        int hoverFace = HitGreyboxFaceHandle(e.mousePosition);
        int hoverEdge = hoverFace >= 0 ? -1 : HitGreyboxOutlineEdge(gb, e.mousePosition);

        // Alt is the Scene View navigation modifier (orbit/pan/zoom) — never start an edit while
        // it's held, or an Alt-drag to navigate would grab an edge/handle instead.
        if (e.type == EventType.MouseDown && !e.alt && (e.button == 0 || e.button == 1 || e.button == 2))
            BeginGreybox(e, gb, hoverFace, hoverEdge);

        if (e.type == EventType.Repaint)
            DrawGreybox(gb, hoverFace, hoverEdge);

        if (e.type == EventType.MouseMove)
            sv.Repaint();
    }

    // ─── Hover ──────────────────────────────────────────────────

    static int HitGreyboxFaceHandle(Vector2 mousePos)
    {
        int best = -1;
        float bestDist = GPEditShared.HandlePx;
        for (int face = 0; face < 6; face++)
        {
            Vector2 s = HandleUtility.WorldToGUIPoint(GreyboxFaceCenter(face));
            float dist = Vector2.Distance(mousePos, s);
            if (dist < bestDist) { bestDist = dist; best = face; }
        }
        return best;
    }

    static int HitGreyboxOutlineEdge(Greybox gb, Vector2 mousePos)
    {
        int best = -1;
        float bestDist = GPEditShared.HoverPx;
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
        return gb.ActiveFaces[adj[0]] || gb.ActiveFaces[adj[1]];
    }

    static Vector3 GreyboxFaceCenter(int face)
    {
        int[] ci = GPEditShared.FaceCornerIndices[face];
        return (s_gbWc[ci[0]] + s_gbWc[ci[1]] + s_gbWc[ci[2]] + s_gbWc[ci[3]]) * 0.25f;
    }

    /// <summary>Outward world normal of a face, from winding, corrected against the local-axis sign.</summary>
    static Vector3 GreyboxFaceNormal(Greybox gb, int face)
    {
        int[] ci = GPEditShared.FaceCornerIndices[face];
        Vector3 n = Vector3.Cross(s_gbWc[ci[1]] - s_gbWc[ci[0]], s_gbWc[ci[3]] - s_gbWc[ci[0]]).normalized;
        int axis = face / 2;
        float sign = (face % 2 == 0) ? 1f : -1f;
        Vector3 localNormal = axis == 0 ? new Vector3(sign, 0, 0)
                            : axis == 1 ? new Vector3(0, sign, 0)
                            : new Vector3(0, 0, sign);
        if (Vector3.Dot(n, gb.transform.TransformDirection(localNormal)) < 0f) n = -n;
        return n;
    }

    // ─── Begin ──────────────────────────────────────────────────

    static void BeginGreybox(Event e, Greybox gb, int hoverFace, int hoverEdge)
    {
        if (hoverFace >= 0)
        {
            if (e.button == 2) { ToggleGreyboxFace(gb, hoverFace); e.Use(); return; }

            if (e.button == 0 && !e.shift)      StartGreyboxFaceNormal(gb, hoverFace, e.mousePosition);
            else if (e.button == 0 && e.shift)  StartGreyboxFaceSkew(gb, hoverFace, e.mousePosition);
            else if (e.button == 1)             StartGreyboxExtrude(gb, hoverFace, e.mousePosition, linked: e.shift);
            else return;
        }
        else if (hoverEdge >= 0)
        {
            if (e.button != 0) return;
            StartGreyboxEdge(gb, hoverEdge, e.mousePosition);
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

    static void ToggleGreyboxFace(Greybox gb, int face)
    {
        Undo.RegisterCompleteObjectUndo(gb, "Toggle Greybox Face");
        gb.ActiveFaces[face] = !gb.ActiveFaces[face];
        gb.RebuildMesh();
        EditorUtility.SetDirty(gb);
    }

    static void StartGreyboxEdge(Greybox gb, int edge, Vector2 mousePos)
    {
        BeginGreyboxUndo(gb, "Greybox Edge Move");
        s_gbDrag = GbDrag.Edge;
        s_gbEdge = edge;
        s_gbStartCorners = (Vector3[])gb.Corners.Clone();
        s_gbEdgeAxis = edge / 4;
        s_gbPlaneNormal = s_gbEdgeAxis == 0 ? gb.transform.right
                        : s_gbEdgeAxis == 1 ? gb.transform.up
                        : gb.transform.forward;
        int[] ec = GPEditShared.EdgeCornerIndices[edge];
        s_gbPlanePoint = (s_gbWc[ec[0]] + s_gbWc[ec[1]]) * 0.5f;
        GPEditShared.RaycastPlane(mousePos, s_gbPlanePoint, s_gbPlaneNormal, out s_gbHitStart);
    }

    static void StartGreyboxFaceNormal(Greybox gb, int face, Vector2 mousePos)
    {
        BeginGreyboxUndo(gb, "Greybox Move Face");
        s_gbDrag = GbDrag.FaceNormal;
        s_gbFace = face;
        s_gbStartCorners = (Vector3[])gb.Corners.Clone();
        s_gbPlaneNormal = GreyboxFaceNormal(gb, face);
        s_gbPlanePoint = GreyboxFaceCenter(face);
        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
        s_gbNormalStartDist = GPEditShared.ProjectRayOntoLine(ray, s_gbPlanePoint, s_gbPlaneNormal);
    }

    static void StartGreyboxFaceSkew(Greybox gb, int face, Vector2 mousePos)
    {
        BeginGreyboxUndo(gb, "Greybox Skew Face");
        s_gbDrag = GbDrag.FaceSkew;
        s_gbFace = face;
        s_gbStartCorners = (Vector3[])gb.Corners.Clone();
        s_gbPlaneNormal = GreyboxFaceNormal(gb, face);
        s_gbPlanePoint = GreyboxFaceCenter(face);
        GPEditShared.RaycastPlane(mousePos, s_gbPlanePoint, s_gbPlaneNormal, out s_gbHitStart);
    }

    static void BeginGreyboxUndo(Greybox gb, string name)
    {
        Undo.IncrementCurrentGroup();
        s_gbUndoGroup = Undo.GetCurrentGroup();
        Undo.RegisterCompleteObjectUndo(gb, name);
        GreyboxSeamSolver.BeginUndoScope(name);
        Undo.SetCurrentGroupName(name);
    }

    // ─── Drag ───────────────────────────────────────────────────

    static void HandleGreyboxDrag(Event e, SceneView sv)
    {
        if (s_gbTarget == null) { ResetGreyboxDrag(); return; }

        if (e.type == EventType.MouseDrag && e.button == s_gbButton)
        {
            ApplyGreyboxDrag(e.mousePosition);
            sv.Repaint();
            e.Use();
            return;
        }

        if ((e.type == EventType.MouseUp && e.button == s_gbButton) || !Enabled)
        {
            FinishGreyboxDrag();
            e.Use();
            return;
        }

        if (e.type == EventType.Repaint)
        {
            s_gbTarget.GetWorldCorners(s_gbWc);
            int face = (s_gbDrag == GbDrag.FaceNormal || s_gbDrag == GbDrag.FaceSkew) ? s_gbFace : -1;
            int edge = s_gbDrag == GbDrag.Edge ? s_gbEdge : -1;
            DrawGreybox(s_gbTarget, face, edge);
        }
    }

    static void ApplyGreyboxDrag(Vector2 mousePos)
    {
        switch (s_gbDrag)
        {
            case GbDrag.Edge:       ApplyGreyboxEdge(mousePos);   break;
            case GbDrag.FaceNormal: ApplyGreyboxFaceNormal(mousePos); break;
            case GbDrag.FaceSkew:   ApplyGreyboxFaceSkew(mousePos);   break;
            case GbDrag.Extrude:    ApplyGreyboxExtrude(mousePos);    break;
        }
    }

    static void ApplyGreyboxEdge(Vector2 mousePos)
    {
        if (!GPEditShared.RaycastPlane(mousePos, s_gbPlanePoint, s_gbPlaneNormal, out Vector3 hit)) return;
        Vector3 localDelta = s_gbTarget.transform.InverseTransformVector(hit - s_gbHitStart);

        if (Event.current.shift)
        {
            int a = (s_gbEdgeAxis + 1) % 3;
            int b = (s_gbEdgeAxis + 2) % 3;
            if (Mathf.Abs(localDelta[a]) >= Mathf.Abs(localDelta[b])) localDelta[b] = 0f;
            else localDelta[a] = 0f;
        }

        int[] ec = GPEditShared.EdgeCornerIndices[s_gbEdge];
        s_gbTarget.Corners[ec[0]] = s_gbStartCorners[ec[0]] + localDelta;
        s_gbTarget.Corners[ec[1]] = s_gbStartCorners[ec[1]] + localDelta;
        RebuildGreyboxWithSeams(ec[0], ec[1]);
    }

    static void ApplyGreyboxFaceNormal(Vector2 mousePos)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
        float dist = GPEditShared.ProjectRayOntoLine(ray, s_gbPlanePoint, s_gbPlaneNormal);
        Vector3 localDelta = s_gbTarget.transform.InverseTransformVector(s_gbPlaneNormal * (dist - s_gbNormalStartDist));
        MoveGreyboxFaceCorners(localDelta);
    }

    static void ApplyGreyboxFaceSkew(Vector2 mousePos)
    {
        if (!GPEditShared.RaycastPlane(mousePos, s_gbPlanePoint, s_gbPlaneNormal, out Vector3 hit)) return;
        Vector3 localDelta = s_gbTarget.transform.InverseTransformVector(hit - s_gbHitStart);
        MoveGreyboxFaceCorners(localDelta);
    }

    /// <summary>Translate the dragged face's four corners together by a local-space delta.</summary>
    static void MoveGreyboxFaceCorners(Vector3 localDelta)
    {
        int[] ci = GPEditShared.FaceCornerIndices[s_gbFace];
        for (int k = 0; k < 4; k++)
            s_gbTarget.Corners[ci[k]] = s_gbStartCorners[ci[k]] + localDelta;
        RebuildGreyboxWithSeams(ci[0], ci[1], ci[2], ci[3]);
    }

    static void RebuildGreyboxWithSeams(params int[] corners)
    {
        s_gbTarget.RebuildMesh();
        EditorUtility.SetDirty(s_gbTarget);
        foreach (int c in corners)
            GreyboxSeamSolver.SyncCorner(s_gbTarget, c);
    }

    static void FinishGreyboxDrag()
    {
        GameObject extruded = s_gbExtrudeNew != null ? s_gbExtrudeNew.gameObject : null;
        Undo.CollapseUndoOperations(s_gbUndoGroup);
        GUIUtility.hotControl = 0;
        ResetGreyboxDrag();
        if (extruded != null) Selection.activeObject = extruded;
    }

    static void ResetGreyboxDrag()
    {
        s_gbDrag = GbDrag.None;
        s_gbTarget = null;
        s_gbStartCorners = null;
        s_gbExtrudeNew = null;
    }

    // ─── Extrude ────────────────────────────────────────────────

    static void StartGreyboxExtrude(Greybox sourceGb, int face, Vector2 mousePos, bool linked)
    {
        Undo.IncrementCurrentGroup();
        s_gbUndoGroup = Undo.GetCurrentGroup();

        Vector3[] wc = sourceGb.GetWorldCorners();
        int[] ci = GPEditShared.FaceCornerIndices[face];
        Vector3 c0 = wc[ci[0]], c1 = wc[ci[1]], c2 = wc[ci[2]], c3 = wc[ci[3]];
        s_gbExtrudeCenter = (c0 + c1 + c2 + c3) * 0.25f;

        s_gbExtrudeNormal = Vector3.Cross(c1 - c0, c3 - c0).normalized;
        int axisIdx = face / 2;
        float signF = (face % 2 == 0) ? 1f : -1f;
        Vector3 localNormal = axisIdx == 0 ? new Vector3(signF, 0, 0)
                            : axisIdx == 1 ? new Vector3(0, signF, 0)
                            : new Vector3(0, 0, signF);
        if (Vector3.Dot(s_gbExtrudeNormal, sourceGb.transform.TransformDirection(localNormal)) < 0f)
            s_gbExtrudeNormal = -s_gbExtrudeNormal;

        Vector3 up = s_gbExtrudeNormal;
        Vector3 tDir = Vector3.ProjectOnPlane(c3 - c0, up);
        if (tDir.sqrMagnitude < 0.0001f) tDir = Vector3.ProjectOnPlane(c1 - c0, up);
        if (tDir.sqrMagnitude < 0.0001f) tDir = Vector3.ProjectOnPlane(Vector3.forward, up);
        Quaternion extrudeRot = Quaternion.LookRotation(tDir.normalized, up);

        Undo.RegisterCompleteObjectUndo(sourceGb, "Greybox Extrude");
        if (linked) sourceGb.ActiveFaces[face] = false;
        sourceGb.RebuildMesh();
        EditorUtility.SetDirty(sourceGb);

        Transform parent = sourceGb.transform.parent;
        var go = GreyboxSettings.PlaceGreybox(s_gbExtrudeCenter, extrudeRot, parent);
        go.transform.localScale = Vector3.one;
        s_gbExtrudeNew = go.GetComponent<Greybox>();

        Vector3[] newCorners        = Greybox.DefaultCorners();
        Vector3[] srcFaceCorners    = { c0, c1, c2, c3 };
        int[]     linkChildCorners  = new int[4];
        int[]     linkParentCorners = new int[4];
        for (int k = 0; k < 4; k++)
        {
            Vector3 lc     = go.transform.InverseTransformPoint(srcFaceCorners[k]);
            int     botIdx = (lc.x >= 0f ? 1 : 0) | (lc.z >= 0f ? 4 : 0);
            int     topIdx = botIdx | 2;
            newCorners[botIdx] = new Vector3(lc.x, 0f,     lc.z);
            newCorners[topIdx] = new Vector3(lc.x, 0.001f, lc.z);
            linkChildCorners[k]  = botIdx;
            linkParentCorners[k] = ci[k];
        }

        var srcSO      = new SerializedObject(sourceGb);
        var dstSO      = new SerializedObject(s_gbExtrudeNew);
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
            newMr.sharedMaterial    = sourceMr.sharedMaterial;
            newMr.shadowCastingMode = sourceMr.shadowCastingMode;
        }
        go.layer    = sourceGb.gameObject.layer;
        go.isStatic = sourceGb.gameObject.isStatic;

        if (linked) s_gbExtrudeNew.ActiveFaces[3] = false; // hide bottom — seam against source
        s_gbExtrudeNew.RebuildMesh();
        EditorUtility.SetDirty(s_gbExtrudeNew);

        if (linked)
        {
            s_gbExtrudeNew.SetSeamLink(sourceGb, linkChildCorners, linkParentCorners);
            sourceGb.AddSeamChild(s_gbExtrudeNew);
            EditorUtility.SetDirty(s_gbExtrudeNew);
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
        s_gbExtrudeStartDist = GPEditShared.ProjectRayOntoLine(ray, s_gbExtrudeCenter, s_gbExtrudeNormal);
        s_gbDrag = GbDrag.Extrude;
    }

    static void ApplyGreyboxExtrude(Vector2 mousePos)
    {
        if (s_gbExtrudeNew == null) return;
        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
        float dist = GPEditShared.ProjectRayOntoLine(ray, s_gbExtrudeCenter, s_gbExtrudeNormal);
        float height = Mathf.Max(dist - s_gbExtrudeStartDist, 0.001f);
        var corners = s_gbExtrudeNew.Corners;
        for (int i = 0; i < 8; i++)
            if ((i & 2) != 0)
                corners[i] = new Vector3(corners[i].x, height, corners[i].z);
        s_gbExtrudeNew.RebuildMesh();
        EditorUtility.SetDirty(s_gbExtrudeNew.gameObject);
    }

    // ─── Draw ───────────────────────────────────────────────────

    static void DrawGreybox(Greybox gb, int hoverFace, int hoverEdge)
    {
        // Outline: all 12 edges. Edges that border only hidden faces draw dimmer but stay
        // visible and grabbable; the hovered edge is brighter/thicker.
        for (int ei = 0; ei < 12; ei++)
        {
            int[] ec = GPEditShared.EdgeCornerIndices[ei];
            if (ei == hoverEdge)
            {
                Handles.color = GPEditShared.OutlineHover;
                Handles.DrawLine(s_gbWc[ec[0]], s_gbWc[ec[1]], 4f);
            }
            else
            {
                Color c = GPEditShared.Outline;
                if (!IsOutlineEdge(gb, ei)) c.a *= 0.35f;
                Handles.color = c;
                Handles.DrawLine(s_gbWc[ec[0]], s_gbWc[ec[1]]);
            }
        }

        // Face handles (all six, so hidden faces can be toggled back on).
        if (Camera.current == null) return;
        Vector3 camFwd = Camera.current.transform.forward;
        for (int face = 0; face < 6; face++)
        {
            Vector3 center  = GreyboxFaceCenter(face);
            bool front      = Vector3.Dot(GreyboxFaceNormal(gb, face), camFwd) < 0f;
            bool active     = gb.ActiveFaces[face];
            Color col = face == hoverFace ? GPEditShared.HandleHover
                      : active            ? GPEditShared.HandleActive
                      :                     GPEditShared.HandleInactive;
            col.a *= front ? 1f : 0.35f;
            GPEditShared.DrawDot(center, col, 0.05f * (front ? 1.1f : 0.85f));
        }
    }
}
#endif
