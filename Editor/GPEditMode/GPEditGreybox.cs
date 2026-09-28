#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

/// <summary>Greybox editing in the coordinate frame shown by the hovered control.</summary>
static partial class GPEdit
{
    enum GbDrag { None, Edge, FaceNormal, Extrude }

    static GbDrag    s_gbDrag;
    static Greybox   s_gbTarget;
    static int       s_gbButton;
    static int       s_gbControlId;
    static int       s_gbUndoGroup;
    static Vector3[] s_gbStartCorners;

    static int s_gbEdge;
    static int s_gbFace;
    static CoordinateDrag s_gbCoordinates;

    // Extrude
    static Greybox s_gbExtrudeNew;
    static Vector3 s_gbExtrudeCenter;
    static Vector3 s_gbExtrudeNormal;
    static Vector3[] s_gbExtrudeBase;

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
        int seamFaces = GreyboxSeamFaceMask(gb);
        for (int ei = 0; ei < 12; ei++)
        {
            if (!IsExternalGreyboxEdge(gb, ei, seamFaces)) continue;
            int[] ec = GPEditShared.EdgeCornerIndices[ei];
            float dist = GPEditShared.DistToSegment(s_gbWc[ec[0]], s_gbWc[ec[1]], mousePos);
            if (dist < bestDist) { bestDist = dist; best = ei; }
        }
        return best;
    }

    struct WeldedEdge
    {
        public Greybox box;
        public int edge;
    }

    static bool IsExternalGreyboxEdge(Greybox box, int edge, int seamFaces)
    {
        var adjacent = GPEditShared.EdgeFaceAdjacency[edge];
        if ((seamFaces & ((1 << adjacent[0]) | (1 << adjacent[1]))) == 0) return true;

        using var edgesScope = ListPool<WeldedEdge>.Get(out var edges);
        using var visitedScope = HashSetPool<long>.Get(out var visited);
        edges.Add(new WeldedEdge { box = box, edge = edge });
        visited.Add(((long)box.GetInstanceID() << 4) | (uint)edge);
        for (int i = 0; i < edges.Count; i++)
        {
            var current = edges[i];
            var corners = GPEditShared.EdgeCornerIndices[current.edge];
            foreach (int face in GPEditShared.EdgeFaceAdjacency[current.edge])
                if (current.box.IsFaceVisible(face)) return true;
            using var seamsScope = ListPool<Greybox.Seam>.Get(out var seams);
            current.box.GetSeams(seams);
            foreach (var seam in seams)
            {
                int a = System.Array.IndexOf(seam.corners, corners[0]);
                int b = System.Array.IndexOf(seam.corners, corners[1]);
                if (a < 0 || b < 0) continue;
                int mappedA = seam.otherCorners[a], mappedB = seam.otherCorners[b];
                for (int otherEdge = 0; otherEdge < 12; otherEdge++)
                {
                    var pair = GPEditShared.EdgeCornerIndices[otherEdge];
                    if (!((pair[0] == mappedA && pair[1] == mappedB) || (pair[0] == mappedB && pair[1] == mappedA))) continue;
                    long key = ((long)seam.other.GetInstanceID() << 4) | (uint)otherEdge;
                    if (visited.Add(key)) edges.Add(new WeldedEdge { box = seam.other, edge = otherEdge });
                    break;
                }
            }
        }
        // Hide only edges with no visible surface anywhere around the weld.
        return false;
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

    // ─── Begin ──────────────────────────────────────────────────

    static void BeginGreybox(SceneView sv, Event e, Greybox gb, int hoverFace, int hoverEdge)
    {
        if (hoverFace >= 0)
        {
            if (e.button == 2) { BeginPrimitiveClick(sv, e, gb, ClickAction.ToggleFace, hoverFace); return; }

            if (e.button == 0)      StartGreyboxFaceMove(gb, hoverFace, e.mousePosition);
            else if (e.button == 1) StartGreyboxExtrude(gb, hoverFace, e.mousePosition, linked: !e.alt);
            else return;
        }
        else if (hoverEdge >= 0)
        {
            if (e.button == 1) { BeginPrimitiveClick(sv, e, gb, ClickAction.SplitEdge, hoverEdge, GreyboxSplitFraction(gb, hoverEdge, e.mousePosition)); return; }
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
        gb.SetFaceVisible(face, !gb.IsFaceVisible(face));
        gb.RebuildMesh();
        EditorUtility.SetDirty(gb);
    }

    static void StartGreyboxEdge(Greybox gb, int edge, Vector2 mousePos)
    {
        BeginGreyboxUndo(gb, "Greybox Edge Move");
        s_gbDrag = GbDrag.Edge;
        s_gbEdge = edge;
        s_gbStartCorners = (Vector3[])gb.Corners.Clone();
        int[] corners = GPEditShared.EdgeCornerIndices[edge];
        Vector3 anchor = GPEditShared.ClosestPointOnSegmentToScreenPos(s_gbWc[corners[0]], s_gbWc[corners[1]], mousePos);
        s_gbCoordinates.Begin(anchor, mousePos, GreyboxEdgeCoordinates(gb, edge, false), GreyboxEdgeCoordinates(gb, edge, true));
    }

    static Vector3 GreyboxAxisWorldDir(Transform t, int axis)
        => t.TransformVector(axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward).normalized;

    static void StartGreyboxFaceMove(Greybox gb, int face, Vector2 mousePos)
    {
        BeginGreyboxUndo(gb, "Greybox Move Face");
        s_gbDrag = GbDrag.FaceNormal;
        s_gbFace = face;
        s_gbStartCorners = (Vector3[])gb.Corners.Clone();
        s_gbCoordinates.Begin(GreyboxFaceCenter(face), mousePos,
            GreyboxFaceCoordinates(gb, face, false), GreyboxFaceCoordinates(gb, face, true));
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
        }
    }

    static void ApplyGreyboxDrag(Vector2 mousePos)
    {
        switch (s_gbDrag)
        {
            case GbDrag.Edge:       ApplyGreyboxEdge(mousePos);   break;
            case GbDrag.FaceNormal: ApplyGreyboxFaceNormal(mousePos); break;
            case GbDrag.Extrude:    ApplyGreyboxExtrude(mousePos);    break;
        }
    }

    static void ApplyGreyboxEdge(Vector2 mousePos)
    {
        Vector3 localDelta = s_gbTarget.transform.InverseTransformVector(s_gbCoordinates.Update(mousePos));
        int[] corners = GPEditShared.EdgeCornerIndices[s_gbEdge];
        s_gbTarget.Corners[corners[0]] = s_gbStartCorners[corners[0]] + localDelta;
        s_gbTarget.Corners[corners[1]] = s_gbStartCorners[corners[1]] + localDelta;
        RebuildGreyboxWithSeams(corners[0], corners[1]);
    }

    static void ApplyGreyboxFaceNormal(Vector2 mousePos)
    {
        MoveGreyboxFaceCorners(s_gbTarget.transform.InverseTransformVector(s_gbCoordinates.Update(mousePos)));
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
        GreyBooleanOrchestrator.ReBakeFrom(s_gbTarget);
    }

    static void FinishGreyboxDrag()
    {
        if (GreyPrimitiveSettings.AutoUpdatePivot)
        {
            GreyPrimitiveEditor.RecenterGreyboxPivot(s_gbTarget);
            if (s_gbExtrudeNew != null)
                GreyPrimitiveEditor.RecenterGreyboxPivot(s_gbExtrudeNew);
        }
        if (s_gbExtrudeNew != null && s_gbExtrudeNew.IsLinkAlive)
        {
            KeepSplitInBoolean(s_gbTarget, s_gbExtrudeNew);
            GreyboxLinkHierarchy.Organize(s_gbTarget, "Greybox Extrude");
            GreyBooleanOrchestrator.ReBakeFrom(s_gbTarget);
        }
        GreyPrimitive.EndDeferredPersist();
        GameObject extruded = s_gbExtrudeNew != null ? s_gbExtrudeNew.gameObject : null;
        Undo.CollapseUndoOperations(s_gbUndoGroup);
        GUIUtility.hotControl = 0;
        ResetGreyboxDrag();
        if (extruded != null && !(SelectedPrimitive is GreyBooleanResult))
        {
            using var selectionScope = ListPool<Object>.Get(out var selection);
            selection.AddRange(Selection.objects);
            selection.Remove(extruded);
            selection.Insert(0, extruded);
            Selection.objects = selection.ToArray();
        }
    }

    static void ResetGreyboxDrag()
    {
        s_gbDrag = GbDrag.None;
        s_gbTarget = null;
        s_gbStartCorners = null;
        s_gbExtrudeNew = null;
        s_gbExtrudeBase = null;
    }

    // ─── Extrude ────────────────────────────────────────────────

    static void StartGreyboxExtrude(Greybox sourceGb, int face, Vector2 mousePos, bool linked)
    {
        if (linked && !GreyboxLinkHierarchy.CanLink(sourceGb, sourceGb)) return;
        s_gbFace = face;
        Undo.IncrementCurrentGroup();
        s_gbUndoGroup = Undo.GetCurrentGroup();
        GreyPrimitive.BeginDeferredPersist();

        Vector3[] wc = sourceGb.GetWorldCorners();
        int[] ci = GPEditShared.FaceCornerIndices[face];
        Vector3 c0 = wc[ci[0]], c1 = wc[ci[1]], c2 = wc[ci[2]], c3 = wc[ci[3]];
        s_gbExtrudeCenter = (c0 + c1 + c2 + c3) * 0.25f;

        var globalCoordinates = GreyboxFaceCoordinates(sourceGb, face, false);
        var localCoordinates = GreyboxFaceCoordinates(sourceGb, face, true);
        s_gbExtrudeNormal = Tools.pivotRotation == PivotRotation.Local ? localCoordinates.a : globalCoordinates.a;
        s_gbCoordinates.Begin(s_gbExtrudeCenter, mousePos,
            EditCoordinates.Face(globalCoordinates.a, globalCoordinates.a, Vector3.zero),
            EditCoordinates.Face(localCoordinates.a, localCoordinates.a, Vector3.zero));

        Vector3 up = s_gbExtrudeNormal;
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
        var go = GreyboxSettings.PlaceGreybox(s_gbExtrudeCenter, extrudeRot, parent, select: false);
        if (parent == null && go.scene != sourceGb.gameObject.scene)
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, sourceGb.gameObject.scene);
        go.transform.localScale = Vector3.one;
        s_gbExtrudeNew = go.GetComponent<Greybox>();

        Vector3[] newCorners = Greybox.DefaultCorners();
        Vector3[] srcFaceCorners = { c0, c1, c2, c3 };
        int[] linkChildCorners = { 0, 4, 5, 1 };
        int[] linkParentCorners = (int[])ci.Clone();
        for (int k = 0; k < 4; k++)
        {
            int bottom = linkChildCorners[k];
            newCorners[bottom] = go.transform.InverseTransformPoint(srcFaceCorners[k]);
            newCorners[bottom | 2] = newCorners[bottom] + go.transform.InverseTransformVector(s_gbExtrudeNormal * 0.001f);
        }
        s_gbExtrudeBase = (Vector3[])newCorners.Clone();

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
            newMr.enabled           = !linked || sourceMr.enabled;
            newMr.sharedMaterial    = sourceMr.sharedMaterial;
            newMr.shadowCastingMode = sourceMr.shadowCastingMode;
        }
        var sourceCollider = sourceGb.GetComponent<MeshCollider>();
        var newCollider = go.GetComponent<MeshCollider>();
        if (newCollider != null && sourceCollider != null) newCollider.enabled = !linked || sourceCollider.enabled;
        go.layer    = sourceGb.gameObject.layer;
        go.isStatic = sourceGb.gameObject.isStatic;

        if (linked) s_gbExtrudeNew.ActiveFaces[3] = false; // hide bottom — seam against source
        s_gbExtrudeNew.RebuildMesh();
        EditorUtility.SetDirty(s_gbExtrudeNew);

        if (linked)
        {
            s_gbExtrudeNew.SetSeamLink(sourceGb, linkChildCorners, linkParentCorners);
            sourceGb.AddSeamChild(s_gbExtrudeNew);
            KeepSplitInBoolean(sourceGb, s_gbExtrudeNew);
            s_gbExtrudeBase = (Vector3[])s_gbExtrudeNew.Corners.Clone();
            EditorUtility.SetDirty(s_gbExtrudeNew);
        }

        s_gbDrag = GbDrag.Extrude;
    }

    static void ApplyGreyboxExtrude(Vector2 mousePos)
    {
        if (s_gbExtrudeNew == null) return;
        Vector3 worldDelta = s_gbCoordinates.Update(mousePos);
        float forward = Vector3.Dot(worldDelta, s_gbExtrudeNormal);
        if (forward < 0.001f) worldDelta += s_gbExtrudeNormal * (0.001f - forward);
        Vector3 localDelta = s_gbExtrudeNew.transform.InverseTransformVector(worldDelta);
        OffsetExtrudedFace(s_gbExtrudeBase, localDelta, s_gbExtrudeNew.Corners);
        s_gbExtrudeNew.RebuildMesh();
        GreyBooleanOrchestrator.ReBakeFrom(s_gbExtrudeNew);
        EditorUtility.SetDirty(s_gbExtrudeNew.gameObject);
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

    static void DrawGreybox(Greybox gb, int hoverFace, int hoverEdge, int operandIndex = -1)
    {
        int seamFaces = GreyboxSeamFaceMask(gb);
        Color tint = operandIndex <= 0 ? Color.white
            : Color.HSVToRGB(Mathf.Repeat(0.55f + (operandIndex - 1) * 0.618034f, 1f), 0.4f, 1f);
        float outlineWidth = operandIndex >= 0 && gb.GetComponentInParent<GreyBooleanResult>() != null && (hoverFace >= 0 || hoverEdge >= 0) ? 1.6f : 0f;

        if (s_gbDrag == GbDrag.None && hoverFace >= 0)
        {
            var faceCorners = GPEditShared.FaceCornerIndices[hoverFace];
            Handles.color = GPEditShared.CreateFace;
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

        // Fully internal welded edges are omitted; visible subdivision edges remain editable.
        for (int ei = 0; ei < 12; ei++)
        {
            if (!IsExternalGreyboxEdge(gb, ei, seamFaces)) continue;
            int[] ec = GPEditShared.EdgeCornerIndices[ei];
            var adjacent = GPEditShared.EdgeFaceAdjacency[ei];
            bool front = (frontFaces & ((1 << adjacent[0]) | (1 << adjacent[1]))) != 0;
            Color color = ei == hoverEdge ? GPEditShared.OutlineHover : GPEditShared.Outline * tint;
            if (!front) color.a *= GPEditShared.BackfaceAlpha;
            if (ei != hoverEdge && !IsOutlineEdge(gb, ei)) color.a *= 0.35f;
            Handles.color = color;
            Handles.DrawLine(s_gbWc[ec[0]], s_gbWc[ec[1]], ei == hoverEdge ? 4f : outlineWidth);
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
            Color col = face == hoverFace ? (s_gbDrag == GbDrag.Extrude ? GPEditShared.Create : GPEditShared.HandleHover)
                      : active            ? GPEditShared.HandleActive * tint
                      :                     GPEditShared.HandleInactive;
            col.a *= front ? 1f : GPEditShared.BackfaceAlpha;
            GPEditShared.DrawDot(center, col, 0.05f * (front ? 1.1f : 0.85f));
        }
    }
}
#endif
