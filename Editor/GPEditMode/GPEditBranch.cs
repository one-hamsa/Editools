#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Branch roads: RMB on a road's spline inserts a vertex on a click, and on a drag pulls a new
/// road out of that point. The new road's first vertex stays on the source road's spline at the
/// same place along it (the branch anchor on <see cref="Greyroad"/>); nothing else is shared.
/// </summary>
static partial class GPEdit
{
    static Greyroad s_spBranch;        // created road; unset until the press passes the drag threshold
    static Greyroad s_spBranchSource;
    static int      s_spBranchSegment;
    static float    s_spBranchT;       // Bezier parameter on the segment, used by the click's vertex insert
    static Vector2  s_spBranchPress;

    static void StartSplineBranch(Greyroad source, int segment, float t, Vector2 mousePos)
    {
        s_spDrag = SpDrag.Branch;
        s_spBranch = null;
        s_spBranchSource = source;
        s_spBranchSegment = segment;
        s_spBranchT = t;
        s_spBranchPress = mousePos;
        s_spVertex = -1;
    }

    static void ApplySplineBranch(Vector2 mousePos)
    {
        if (s_spBranch == null)
        {
            // A press that never becomes a drag is a click.
            if (Vector2.Distance(mousePos, s_spBranchPress) < GPEditShared.DragStartPx) return;
            CreateBranchRoad();
        }
        var xform = s_sp.Transform;
        Vector3 end = xform.InverseTransformPoint(xform.TransformPoint(s_spStartLocalPos) + s_spCoordinates.Update(mousePos));
        s_sp.SetLocalVertexPos(1, end);
        s_sp.ResetHandle(0);
        s_sp.ResetHandle(1);
    }

    /// <summary>Creates the branch road with both vertices on the anchor and rebinds the drag to its end vertex.</summary>
    static void CreateBranchRoad()
    {
        const string label = "Branch Road";
        var source = s_spBranchSource;
        Undo.IncrementCurrentGroup();
        s_spUndoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);

        float param = source.PathParamFromBezier(s_spBranchSegment, s_spBranchT);
        Vector3 anchorWorld = source.transform.TransformPoint(source.GetPathPointLocal(param));

        // Beside the source, outside its ledge folder and outside any Boolean or compound it feeds.
        Transform parent = source.transform.parent;
        if (IsLedgeGroupFolder(parent, source)) parent = parent.parent;
        while (parent != null && parent.TryGetComponent<GreyPrimitive>(out _)) parent = parent.parent;

        var go = GreyPrimitiveSettings.PlacePrimitive<Greyroad>(source.name + " Branch", anchorWorld,
            source.transform.rotation, parent, meshCollider: source.GetComponent<MeshCollider>() != null, select: false);
        var branch = CopyPrimitiveSettings<Greyroad>(source, go);
        branch.BaseWidth = source.BaseWidth;
        branch.BaseHeight = source.BaseHeight;
        branch.SplineSmoothing = source.SplineSmoothing;
        for (int face = 0; face < 6; face++) branch.ActiveFaces[face] = source.ActiveFaces[face];
        Vector3 anchorLocal = go.transform.InverseTransformPoint(anchorWorld);
        var vertices = branch.Vertices;
        for (int i = 0; i < vertices.Count; i++)
        {
            var vertex = vertices[i];
            vertex.position = anchorLocal;
            vertices[i] = vertex;
        }

        Undo.RegisterCompleteObjectUndo(source, label);
        branch.LinkAsBranch(source, param);
        branch.RebuildMesh();
        EditorUtility.SetDirty(source);
        EditorUtility.SetDirty(branch);

        var sourceSpline = CreateSplineBinding(source);
        s_spBranch = branch;
        s_sp = CreateSplineBinding(branch);
        s_spVertex = 1;
        s_spStartLocalPos = anchorLocal;
        s_spCoordinates.Begin(anchorWorld, s_spBranchPress,
            SplineCoordinates(sourceSpline, s_spBranchSegment, false), SplineCoordinates(sourceSpline, s_spBranchSegment, true));
    }

    // Ctrl offset drag of a branch start: a line across the source road through the anchor.
    static bool    s_spAnchorOffsetMode;
    static Vector3 s_spAnchorLinePoint;
    static Vector3 s_spAnchorLineDir;
    static float   s_spAnchorUnit;        // world length of one source local unit along the line
    static float   s_spAnchorStartOffset;
    static float   s_spAnchorStartDist;

    static void StartBranchAnchorSlide(GPSpline sp, Vector2 mousePos)
    {
        BeginSplineUndo(sp, "Slide Branch Start");
        s_spDrag = SpDrag.AnchorSlide;
        s_spVertex = 0;
        s_spAnchorOffsetMode = Event.current.control;
        if (s_spAnchorOffsetMode) CaptureBranchOffsetLine((Greyroad)sp.Obj, mousePos);
    }

    /// <summary>
    /// Moves a branch road's anchored start to the point of its source road's spline nearest the
    /// cursor; with Ctrl, moves it sideways across the source road instead. Switching Ctrl
    /// mid-drag continues from where the start is.
    /// </summary>
    static void ApplyBranchAnchorSlide(Vector2 mousePos)
    {
        var branch = (Greyroad)s_sp.Obj;
        // Legitimate filter: the source can be deleted during the drag; the branch is then a free road.
        if (!branch.IsBranch) return;

        bool control = Event.current.control;
        if (control != s_spAnchorOffsetMode)
        {
            s_spAnchorOffsetMode = control;
            if (control) CaptureBranchOffsetLine(branch, mousePos);
        }
        if (s_spAnchorOffsetMode)
        {
            float distance = GPEditShared.ProjectRayOntoLine(HandleUtility.GUIPointToWorldRay(mousePos), s_spAnchorLinePoint, s_spAnchorLineDir);
            branch.BranchOffset = s_spAnchorStartOffset + (distance - s_spAnchorStartDist) / s_spAnchorUnit;
            return;
        }

        branch.BranchSource.GetPathFramesWorld(s_ldSourceFrames);
        // Legitimate filter: no part of the source path is in front of the camera.
        if (NearestOnPath(s_ldSourceFrames, 0, mousePos, out float param, out _) == float.MaxValue) return;
        branch.SetBranchParam(param);
    }

    /// <summary>Starts a Ctrl offset drag from the start's current position. Stays in slide mode when the source has no cross-section there.</summary>
    static void CaptureBranchOffsetLine(Greyroad branch, Vector2 mousePos)
    {
        if (!branch.GetBranchAnchorWorld(out s_spAnchorLinePoint, out Vector3 sideways) || sideways.sqrMagnitude < 1e-12f)
        {
            s_spAnchorOffsetMode = false;
            return;
        }
        s_spAnchorUnit = sideways.magnitude;
        s_spAnchorLineDir = sideways / s_spAnchorUnit;
        s_spAnchorStartOffset = branch.BranchOffset;
        s_spAnchorStartDist = GPEditShared.ProjectRayOntoLine(HandleUtility.GUIPointToWorldRay(mousePos), s_spAnchorLinePoint, s_spAnchorLineDir);
    }

    /// <summary>Arrow across the source road at a branch start, shown while Ctrl is held.</summary>
    static void DrawBranchOffsetArrow(Greyroad branch)
    {
        if (!branch.GetBranchAnchorWorld(out Vector3 point, out Vector3 sideways)) return;
        DrawCoordinates(point, sideways, Vector3.zero, -1);
    }

    /// <summary>Frees a branch road's first vertex; the road keeps its shape. One undo step.</summary>
    internal static void UnlinkBranch(Greyroad branch)
    {
        const string label = "Unlink Branch";
        var source = branch.BranchSource;
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        Undo.RegisterCompleteObjectUndo(branch, label);
        Undo.RegisterCompleteObjectUndo(source, label);
        branch.UnlinkBranch();
        GreyPrimitiveEditor.RebuildPrimitiveAndDependents(branch);
        EditorUtility.SetDirty(branch);
        EditorUtility.SetDirty(source);
        Undo.CollapseUndoOperations(group);
    }

    /// <summary>Release of a branch press that never became a drag: insert a vertex there, as a click on a pipe does.</summary>
    static void FinishSplineBranchClick()
    {
        var source = s_spBranchSource;
        int segment = s_spBranchSegment;
        float t = s_spBranchT;
        GUIUtility.hotControl = 0;
        ResetSplineDrag();
        // Leaving Edit Mode also ends the press; only a real release inserts.
        if (Enabled) InsertSplineVertex(CreateSplineBinding(source), segment, t);
    }
}
#endif
