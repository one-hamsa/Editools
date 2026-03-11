using UnityEditor;
using UnityEngine;

/// <summary>
/// Quick Transform: hold W/E/R + drag anywhere in the Scene View to
/// move/rotate/scale selected objects without grabbing a gizmo handle.
/// Standard tool switching (tap W/E/R) still works normally.
///
/// While any mode key is held, a wireframe bounding box wraps the selection
/// (including all child MeshRenderers and SkinnedMeshRenderers).
/// Hover state determines behavior:
///
/// W = Move
///   Outside box   → wireframe only, world XZ plane movement (gizmo preview)
///   LMB face      → face highlights, movement locked to face plane
///   RMB face      → movement locked along face normal
///
/// E = Rotate
///   Outside box   → wireframe only, rotate around world Y with center pivot (gizmo preview)
///   Hover face    → face highlights, rotate around face normal, pivot = face center
///   Hover edge    → edge highlights (incl. backface), rotate around edge dir, pivot = grab point
///   Ctrl held     → snap to 15° increments
///
/// R = Scale
///   Quadrant      → side face by screen direction, scale along face normal
///   Hover Y face  → top/bottom face, scale along Y
///   Anchor = opposite face, stays fixed; group scales as one unit
///
/// Shift + W/E/R = Duplicate then transform
///
/// Toggle on/off via the Editools toolbar button.
/// Configure per-project via a QuickTransformConfig ScriptableObject asset.
/// </summary>
[InitializeOnLoad]
static class QuickTransform
{
    // ─── Enums ──────────────────────────────────────────────────

    enum Mode { None, Move, Rotate, Scale }
    enum Phase { Idle, Ready, Dragging }
    enum HoverKind { None, AllSideFaces, Face, Edge }

    // ─── Public API / Settings (EditorPrefs-backed) ───────────

    const string k_Pref          = "QuickTransform_";
    const string k_EnabledPref   = k_Pref + "Enabled";
    const string k_EdgeHoverPref = k_Pref + "EdgeHoverPx";
    const string k_CircleRadPref = k_Pref + "CircleRadius";
    const string k_LinearRotPref = k_Pref + "LinearRot";
    const string k_LinearRotSensPref = k_Pref + "LinearRotSens";
    const string k_RotSnapPref      = k_Pref + "RotSnapAngle";
    internal static bool Enabled
    {
        get => EditorPrefs.GetBool(k_EnabledPref, true);
        set => EditorPrefs.SetBool(k_EnabledPref, value);
    }

    internal static float EdgeHoverPx
    {
        get => EditorPrefs.GetFloat(k_EdgeHoverPref, 10f);
        set => EditorPrefs.SetFloat(k_EdgeHoverPref, Mathf.Clamp(value, 4f, 30f));
    }

    internal static float CircleRadius
    {
        get => EditorPrefs.GetFloat(k_CircleRadPref, 1.2f);
        set => EditorPrefs.SetFloat(k_CircleRadPref, Mathf.Clamp(value, 0.3f, 5f));
    }

    internal static bool LinearRotation
    {
        get => EditorPrefs.GetBool(k_LinearRotPref, false);
        set => EditorPrefs.SetBool(k_LinearRotPref, value);
    }

    internal static float LinearRotSensitivity
    {
        get => EditorPrefs.GetFloat(k_LinearRotSensPref, 0.5f);
        set => EditorPrefs.SetFloat(k_LinearRotSensPref, Mathf.Clamp(value, 0.01f, 10f));
    }

    internal static float RotSnapAngle
    {
        get => EditorPrefs.GetFloat(k_RotSnapPref, 15f);
        set => EditorPrefs.SetFloat(k_RotSnapPref, Mathf.Clamp(value, 1f, 90f));
    }

    // ─── Config (legacy ScriptableObject, still used for UpAxis) ─

    static QuickTransformConfig config;
    static double configRetryTime;

    static QuickTransformConfig GetConfig()
    {
        if (config != null) return config;
        if (EditorApplication.timeSinceStartup < configRetryTime) return null;
        configRetryTime = EditorApplication.timeSinceStartup + 1.0;

        string[] guids = AssetDatabase.FindAssets("t:QuickTransformConfig");
        if (guids.Length > 0)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            config = AssetDatabase.LoadAssetAtPath<QuickTransformConfig>(path);
        }
        return config;
    }

    static Vector3 UpAxis
    {
        get
        {
            var cfg = GetConfig();
            return cfg != null ? cfg.upAxis.normalized : Vector3.up;
        }
    }

    // ─── State ──────────────────────────────────────────────────

    static Mode  activeMode;
    static Phase phase;

    // Key tracking
    static bool wHeld, eHeld, rHeld;
    static bool rmbHeld;
    static double modeKeyDownTime;   // EditorApplication.timeSinceStartup when mode key first pressed
    static Mode suppressKeyUpFor;

    // Drag data
    static Vector2      mousePressPos;
    static Transform[]  dragTargets;
    static Vector3[]    startPositions;
    static Quaternion[] startRotations;
    static Vector3[]    startScales;
    static Vector3      selectionPivot;
    static int          dragButton;         // 0=LMB, 1=RMB

    // ─── Shared Bounding Box ────────────────────────────────────
    //
    // OBB for single object (axes = object's right/up/forward).
    // AABB for multi-select (axes = world right/up/forward).
    // Face index: axisIdx = face/2, sign = (face%2==0) ? +1 : -1
    //   0:+axes[0]  1:-axes[0]  2:+axes[1]  3:-axes[1]  4:+axes[2]  5:-axes[2]

    static Vector3   boundsCenter;
    static Vector3[] boundsAxes;        // [3] world-space axes
    static float[]   boundsExtents;     // [3] half-extents

    // Hover result — updated in Idle, locked on click
    static HoverKind hoveredKind, lockedKind;
    static int       hoveredIndex, lockedIndex;   // face 0-5 or edge 0-11

    // ─── Move State ─────────────────────────────────────────────

    static Vector3 moveHitStart;        // plane hit at start
    static Vector3 movePlaneNormal;     // plane normal (face normal for face hover, UpAxis for outside)
    static Vector3 movePlanePoint;      // point on the plane (face center for face hover, pivot for outside)
    static bool    moveAlongNormal;     // true when RMB drag = move along face normal
    static float   moveNormalStartDist; // starting projection distance for normal-locked move

    // ─── Rotate State ───────────────────────────────────────────

    static Vector3 rotateAxis;              // world-space rotation axis
    static Vector3 rotatePivot;             // world-space pivot point
    static float   rotateAccumAngle;        // accumulated rotation (screen-space deg)
    static float   rotatePrevScreenAngle;   // previous frame's screen angle (for incremental delta)
    static Vector3 rotateStartDir;          // initial direction on rotation plane (for gizmo line)
    static Vector2 rotateLinearStartPos;    // screen pos when dragging started (for linear mode)
    static float   rotateLinearSign;        // precomputed sign for linear mode (mouse-right → rotation direction)

    // ─── Scale State ────────────────────────────────────────────

    static Vector3 scaleAnchorWorld;
    static Vector3 scaleNormalWorld;
    static float   scaleStartFaceDist;
    static float   scaleStartMouseDist;

    // ─── Undo ─────────────────────────────────────────────────

    static int undoGroup;

    // ─── Duplicate ──────────────────────────────────────────────

    static bool shiftHeldOnPress;
    static bool didDuplicate;

    // ─── Constants ──────────────────────────────────────────────

    const float DragThresholdPx  = 3f;
    const float MinBoundsExtent  = 0.05f;
    const double ModeKeyDelaySec = 0.1;   // delay before showing bounding box preview

    // ─── Cursor Warp (Windows) ──────────────────────────────────

#if UNITY_EDITOR_WIN
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);

    /// <summary>
    /// Warps the OS cursor by the delta between two GUI-space points.
    /// Using a delta avoids DPI-scaling and window-offset conversion issues.
    /// </summary>
    static void WarpCursorDelta(Vector2 fromGUI, Vector2 toGUI)
    {
        GetCursorPos(out POINT pt);
        Vector2 delta = toGUI - fromGUI;
        SetCursorPos(pt.X + Mathf.RoundToInt(delta.x), pt.Y + Mathf.RoundToInt(delta.y));
    }
#endif

    // ─── Hook ───────────────────────────────────────────────────

    static QuickTransform()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    static void OnUndoRedo()
    {
        // When undo/redo fires mid-operation, our cached state (startPositions,
        // startRotations, etc.) becomes stale.  Reset to Idle so we don't apply
        // transforms from an invalidated snapshot.
        if (phase != Phase.Idle)
        {
            GUIUtility.hotControl = 0;
            ResetState();
        }
    }

    // ─── Main Loop ──────────────────────────────────────────────

    static void OnSceneGUI(SceneView sv)
    {
        Event e = Event.current;

        if (e.type == EventType.MouseLeaveWindow)
        {
            wHeld = eHeld = rHeld = false;
            if (phase == Phase.Dragging)
            {
                // Commit the drag — treat leaving the window as a normal release
                Undo.CollapseUndoOperations(undoGroup);
                GUIUtility.hotControl = 0;
                suppressKeyUpFor = activeMode;
                ResetState();
            }
            else if (phase != Phase.Idle)
            {
                GUIUtility.hotControl = 0;
                ResetState();
            }
            return;
        }

        UpdateKeyTracking(e);

        // Early-out if disabled via toolbar toggle
        if (!Enabled)
        {
            wHeld = eHeld = rHeld = false;
            if (phase != Phase.Idle) { GUIUtility.hotControl = 0; ResetState(); }
            Tools.hidden = false;
            return;
        }

        if (suppressKeyUpFor != Mode.None && e.type == EventType.KeyUp)
        {
            bool suppress = (suppressKeyUpFor == Mode.Move   && e.keyCode == KeyCode.W)
                         || (suppressKeyUpFor == Mode.Rotate && e.keyCode == KeyCode.E)
                         || (suppressKeyUpFor == Mode.Scale  && e.keyCode == KeyCode.R);
            if (suppress) { suppressKeyUpFor = Mode.None; e.Use(); return; }
        }

        Transform[] selected = Selection.transforms;
        if (selected == null || selected.Length == 0)
        {
            if (phase != Phase.Idle) { GUIUtility.hotControl = 0; ResetState(); }
            Tools.hidden = false;
            return;
        }

        Mode heldMode = GetHeldMode();

        // Hide Unity's built-in gizmo when QuickTransform is active
        Tools.hidden = heldMode != Mode.None || phase != Phase.Idle;

        switch (phase)
        {
            case Phase.Idle:    HandleIdle(e, sv, selected, heldMode); break;
            case Phase.Ready:   HandleReady(e, sv);                    break;
            case Phase.Dragging: HandleDragging(e, sv);                break;
        }

        // ── Bounding box preview while any mode key is held in Idle ──
        bool showPreview = heldMode != Mode.None && phase == Phase.Idle
            && (EditorApplication.timeSinceStartup - modeKeyDownTime) >= ModeKeyDelaySec;
        if (showPreview)
        {
            ComputeBounds(selected);
            DetectHover(sv, e.mousePosition, heldMode);
            if (e.type == EventType.Repaint)
            {
                DrawBoundsBox(hoveredKind, hoveredIndex, heldMode);
                if (hoveredKind == HoverKind.AllSideFaces)
                {
                    if (heldMode == Mode.Move)        DrawWorldMoveGizmo(boundsCenter);
                    else if (heldMode == Mode.Rotate)  DrawWorldRotateGizmo(boundsCenter);
                }
            }
            if (e.type == EventType.MouseMove)
                sv.Repaint();
        }

        if (heldMode != Mode.None || phase != Phase.Idle)
        {
            EditorGUIUtility.AddCursorRect(
                new Rect(0, 0, sv.position.width, sv.position.height),
                MouseCursor.Pan);
        }
    }

    // ─── Key Tracking ───────────────────────────────────────────

    static void UpdateKeyTracking(Event e)
    {
        // Track RMB state. Mode keys pressed BEFORE RMB stay held so
        // W+RMB on a face can trigger normal-axis movement.  New mode keys
        // pressed WHILE RMB is held are still suppressed by the !rmbHeld
        // check in the KeyDown handler below (WASD navigation case).
        if (e.type == EventType.MouseDown && e.button == 1)
            rmbHeld = true;
        if (e.type == EventType.MouseUp && e.button == 1)
            rmbHeld = false;

        if (e.type == EventType.KeyDown && !e.alt && !e.control && !rmbHeld)
        {
            bool anyPrev = wHeld || eHeld || rHeld;
            if (e.keyCode == KeyCode.W) wHeld = true;
            if (e.keyCode == KeyCode.E) eHeld = true;
            if (e.keyCode == KeyCode.R) rHeld = true;
            // Record timestamp when we first enter a mode key hold
            if (!anyPrev && (wHeld || eHeld || rHeld))
                modeKeyDownTime = EditorApplication.timeSinceStartup;
        }
        if (e.type == EventType.KeyUp)
        {
            if (e.keyCode == KeyCode.W) wHeld = false;
            if (e.keyCode == KeyCode.E) eHeld = false;
            if (e.keyCode == KeyCode.R) rHeld = false;
        }
    }

    static Mode GetHeldMode()
    {
        if (wHeld) return Mode.Move;
        if (eHeld) return Mode.Rotate;
        if (rHeld) return Mode.Scale;
        return Mode.None;
    }

    // ─── Phase: Idle ────────────────────────────────────────────

    static void HandleIdle(Event e, SceneView sv, Transform[] selected, Mode heldMode)
    {
        if (heldMode == Mode.None) return;
        if (e.type != EventType.MouseDown) return;
        if (e.button != 0 && e.button != 1) return;

        // RMB is only used for face-normal movement in Move mode
        if (e.button == 1 && heldMode != Mode.Move) return;

        activeMode = heldMode;
        dragTargets = selected;
        SnapshotTransforms();
        selectionPivot = ComputePivot();

        // Lock hover result
        ComputeBounds(dragTargets);
        DetectHover(sv, e.mousePosition, activeMode);
        lockedKind  = hoveredKind;
        lockedIndex = hoveredIndex;

        // RMB drag requires a face to lock onto — otherwise let Unity handle scene nav
        if (e.button == 1 && lockedKind != HoverKind.Face)
        {
            activeMode = Mode.None;
            dragTargets = null;
            startPositions = null;
            startRotations = null;
            startScales = null;
            return;
        }

        dragButton = e.button;
        moveAlongNormal = e.button == 1;
        phase = Phase.Ready;
        mousePressPos = e.mousePosition;
        shiftHeldOnPress = e.shift;
        didDuplicate = false;

        // Mode-specific init
        switch (activeMode)
        {
            case Mode.Move:   InitMove(sv, e.mousePosition);   break;
            case Mode.Rotate: InitRotate(sv, e.mousePosition); break;
            case Mode.Scale:  InitScale(sv, e.mousePosition);  break;
        }

        int id = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(id);
        GUIUtility.hotControl = id;
        e.Use();
    }

    // ─── Mode Init ──────────────────────────────────────────────

    static void InitMove(SceneView sv, Vector2 mousePos)
    {
        if (lockedKind == HoverKind.Face)
        {
            movePlaneNormal = GetFaceNormal(lockedIndex);
            movePlanePoint  = GetFaceCenter(lockedIndex);
        }
        else
        {
            movePlaneNormal = UpAxis;
            movePlanePoint  = selectionPivot;
        }

        if (moveAlongNormal)
        {
            // RMB face drag → movement locked along face normal (ray-to-line projection)
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
            moveNormalStartDist = ProjectRayOntoLine(mouseRay, movePlanePoint, movePlaneNormal);
        }
        else
        {
            RaycastPlane(mousePos, movePlanePoint, movePlaneNormal, out moveHitStart);
        }
    }

    static void InitRotate(SceneView sv, Vector2 mousePos)
    {
        if (lockedKind == HoverKind.Face)
        {
            rotateAxis  = GetFaceNormal(lockedIndex);
            rotatePivot = GetFaceCenter(lockedIndex);
        }
        else if (lockedKind == HoverKind.Edge)
        {
            rotateAxis = GetEdgeDirection(lockedIndex);
            Vector3[] corners = GetBoxCorners();
            int[] ec = EdgeCornerIndices[lockedIndex];
            rotatePivot = ClosestPointOnSegmentToScreenPos(corners[ec[0]], corners[ec[1]], mousePos);

            // Warp cursor to the rotation circle edge, pointing outward from object center.
            // This replaces sensitivity-based slowdown — the cursor starts at the circle
            // perimeter so full 1:1 rotation feels natural from any edge.
            float worldRadius = HandleUtility.GetHandleSize(rotatePivot) * CircleRadius;
            Vector3 outward = rotatePivot - boundsCenter;
            outward -= Vector3.Dot(outward, rotateAxis) * rotateAxis; // project onto rotation plane
            if (outward.sqrMagnitude < 0.0001f)
                outward = Vector3.Cross(rotateAxis, sv.camera.transform.right);
            outward.Normalize();

            Vector3 warpWorld = rotatePivot + outward * worldRadius;
            Vector2 warpGUI = HandleUtility.WorldToGUIPoint(warpWorld);
#if UNITY_EDITOR_WIN
            WarpCursorDelta(mousePos, warpGUI);
            mousePos = warpGUI;
            mousePressPos = warpGUI;
#endif
        }
        else // AllSideFaces or None → world Y
        {
            rotateAxis  = UpAxis;
            rotatePivot = selectionPivot;
        }
        rotateAccumAngle = 0f;
        rotatePrevScreenAngle = ScreenAngleFrom(mousePos, rotatePivot);

        // Initial direction on the rotation plane (for gizmo line)
        Ray initRay = HandleUtility.GUIPointToWorldRay(mousePos);
        Plane initPlane = new Plane(rotateAxis, rotatePivot);
        if (initPlane.Raycast(initRay, out float initEnter))
            rotateStartDir = (initRay.GetPoint(initEnter) - rotatePivot).normalized;
        else
            rotateStartDir = Vector3.Cross(rotateAxis, Vector3.up).normalized;

        // Precompute linear-mode sign: determine which screen-X direction
        // corresponds to a positive rotation so mouse-right always maps to
        // a visually consistent rotation regardless of axis orientation.
        {
            Vector3 testOffset = rotateStartDir * 0.001f;
            Quaternion testRot = Quaternion.AngleAxis(1f, rotateAxis);
            Vector2 sBefore = HandleUtility.WorldToGUIPoint(rotatePivot + testOffset);
            Vector2 sAfter  = HandleUtility.WorldToGUIPoint(rotatePivot + testRot * testOffset);
            rotateLinearSign = (sAfter.x - sBefore.x) >= 0f ? 1f : -1f;
        }
    }

    static void InitScale(SceneView sv, Vector2 mousePos)
    {
        // lockedIndex is the face index (always Face kind for scale)
        int face = lockedIndex;
        int axisIdx = face / 2;
        float sign = (face % 2 == 0) ? 1f : -1f;

        scaleNormalWorld = boundsAxes[axisIdx] * sign;
        Vector3 faceCenter = boundsCenter + scaleNormalWorld * boundsExtents[axisIdx];
        scaleAnchorWorld   = boundsCenter - scaleNormalWorld * boundsExtents[axisIdx];

        scaleStartFaceDist = Vector3.Dot(faceCenter - scaleAnchorWorld, scaleNormalWorld);

        Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
        scaleStartMouseDist = ProjectRayOntoLine(mouseRay, scaleAnchorWorld, scaleNormalWorld);
    }

    // ─── Phase: Ready ───────────────────────────────────────────

    static void HandleReady(Event e, SceneView sv)
    {
        if (ModeKeyReleased()) { GUIUtility.hotControl = 0; ResetState(); return; }

        if (e.type == EventType.MouseUp && e.button == dragButton)
        { GUIUtility.hotControl = 0; ResetState(); e.Use(); return; }

        if (e.type == EventType.MouseDrag && e.button == dragButton)
        {
            if (Vector2.Distance(e.mousePosition, mousePressPos) >= DragThresholdPx)
            {
                if (shiftHeldOnPress && !didDuplicate)
                    DuplicateAndSwapTargets();

                string undoName = GetUndoName();
                foreach (var t in dragTargets)
                    Undo.RegisterCompleteObjectUndo(t, undoName);
                undoGroup = Undo.GetCurrentGroup();

                phase = Phase.Dragging;

                // Re-sync rotation reference to current cursor position.
                // Prevents a jump from cursor-warp latency or drag-threshold offset.
                if (activeMode == Mode.Rotate)
                {
                    rotatePrevScreenAngle = ScreenAngleFrom(e.mousePosition, rotatePivot);
                    rotateAccumAngle = 0f;
                    rotateLinearStartPos = e.mousePosition;
                }

                ApplyDrag(e, sv);
            }
            e.Use();
            return;
        }

        // Draw the locked bounding box (+ rotation gizmo) while waiting for drag threshold
        if (e.type == EventType.Repaint)
        {
            ComputeBounds(dragTargets);
            DrawBoundsBox(lockedKind, lockedIndex, activeMode);
            if (activeMode == Mode.Rotate && (lockedKind == HoverKind.Face || lockedKind == HoverKind.Edge))
                DrawRotationGizmo(sv);
        }

        if (IsMouseEvent(e)) e.Use();
    }

    // ─── Phase: Dragging ────────────────────────────────────────

    static void HandleDragging(Event e, SceneView sv)
    {
        if (e.type == EventType.MouseDrag && e.button == dragButton)
        { ApplyDrag(e, sv); e.Use(); return; }

        if ((e.type == EventType.MouseUp && e.button == dragButton) || ModeKeyReleased())
        {
            Undo.CollapseUndoOperations(undoGroup);
            GUIUtility.hotControl = 0;
            suppressKeyUpFor = activeMode;
            ResetState();
            e.Use();
            return;
        }

        if (e.type == EventType.Repaint) DrawFeedback(sv);
        if (IsMouseEvent(e)) e.Use();
    }

    // ─── Transform Operations ───────────────────────────────────

    static void ApplyDrag(Event e, SceneView sv)
    {
        switch (activeMode)
        {
            case Mode.Move:   ApplyMove(sv, e.mousePosition);   break;
            case Mode.Rotate: ApplyRotate(sv, e.mousePosition); break;
            case Mode.Scale:  ApplyScale(sv, e.mousePosition);  break;
        }
        sv.Repaint();
    }

    static void ApplyMove(SceneView sv, Vector2 mousePos)
    {
        if (moveAlongNormal)
        {
            // RMB face drag: movement locked along face normal
            Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
            float currentDist = ProjectRayOntoLine(mouseRay, movePlanePoint, movePlaneNormal);
            float delta = currentDist - moveNormalStartDist;
            for (int i = 0; i < dragTargets.Length; i++)
                dragTargets[i].position = startPositions[i] + movePlaneNormal * delta;
        }
        else
        {
            if (!RaycastPlane(mousePos, movePlanePoint, movePlaneNormal, out Vector3 hit)) return;
            Vector3 delta = hit - moveHitStart;
            for (int i = 0; i < dragTargets.Length; i++)
                dragTargets[i].position = startPositions[i] + delta;
        }
    }

    static void ApplyRotate(SceneView sv, Vector2 mousePos)
    {
        if (LinearRotation)
        {
            // Linear mode: horizontal screen-space delta → degrees, ignoring pivot/object size.
            float dx = mousePos.x - rotateLinearStartPos.x;
            rotateAccumAngle = dx * LinearRotSensitivity;
        }
        else
        {
            // Radial mode: frame-by-frame angular delta avoids wraparound glitches.
            float currentScreenAngle = ScreenAngleFrom(mousePos, rotatePivot);
            float frameDelta = currentScreenAngle - rotatePrevScreenAngle;

            if (frameDelta > 180f)  frameDelta -= 360f;
            if (frameDelta < -180f) frameDelta += 360f;

            rotateAccumAngle += frameDelta;
            rotatePrevScreenAngle = currentScreenAngle;
        }

        // Ctrl held: snap to configured increment
        float snapAngle = RotSnapAngle;
        if (Event.current.control)
            rotateAccumAngle = Mathf.Round(rotateAccumAngle / snapAngle) * snapAngle;

        // Sign: for linear mode use precomputed sign that maps mouse-right to
        // consistent visual rotation; for radial mode derive from camera/axis.
        float sign = LinearRotation
            ? rotateLinearSign
            : (Vector3.Dot(sv.camera.transform.forward, rotateAxis) > 0f ? -1f : 1f);

        Quaternion rot = Quaternion.AngleAxis(sign * rotateAccumAngle, rotateAxis);

        for (int i = 0; i < dragTargets.Length; i++)
        {
            Vector3 offset = startPositions[i] - rotatePivot;
            dragTargets[i].position = rotatePivot + rot * offset;
            // Normalize to prevent denormalized quaternions that cause
            // "Quaternion To Matrix conversion failed" errors on undo.
            dragTargets[i].rotation = Quaternion.Normalize(rot * startRotations[i]);
        }
    }

    static void ApplyScale(SceneView sv, Vector2 mousePos)
    {
        Ray mouseRay = HandleUtility.GUIPointToWorldRay(mousePos);
        float mouseDist  = ProjectRayOntoLine(mouseRay, scaleAnchorWorld, scaleNormalWorld);
        float mouseDelta = mouseDist - scaleStartMouseDist;

        float factor;
        if (Mathf.Abs(scaleStartFaceDist) < 0.001f)
            factor = Mathf.Max(1f + mouseDelta, 0.01f);
        else
            factor = Mathf.Max((scaleStartFaceDist + mouseDelta) / scaleStartFaceDist, 0.01f);

        for (int i = 0; i < dragTargets.Length; i++)
        {
            int localAxis = GetBestLocalAxis(startRotations[i], scaleNormalWorld);
            Vector3 s = startScales[i];
            s[localAxis] = startScales[i][localAxis] * factor;
            dragTargets[i].localScale = s;

            // Reposition: scale axial distance from anchor, preserve lateral offset
            Vector3 offset   = startPositions[i] - scaleAnchorWorld;
            float   axialDist = Vector3.Dot(offset, scaleNormalWorld);
            dragTargets[i].position = scaleAnchorWorld
                + scaleNormalWorld * (axialDist * factor)
                + (offset - scaleNormalWorld * axialDist);
        }

        // ── Verify and correct: guarantee anchor face stays pixel-perfect ──
        ComputeBounds(dragTargets);
        int axisIdx = lockedIndex / 2;
        float signF = (lockedIndex % 2 == 0) ? 1f : -1f;
        // The anchor is the face OPPOSITE to the locked face
        Vector3 actualAnchor = boundsCenter - boundsAxes[axisIdx] * (signF * boundsExtents[axisIdx]);
        Vector3 correction   = scaleAnchorWorld - actualAnchor;
        if (correction.sqrMagnitude > 0.00001f)
        {
            for (int i = 0; i < dragTargets.Length; i++)
                dragTargets[i].position += correction;
        }
    }

    // ─── Helpers: Plane & Ray ───────────────────────────────────

    static bool RaycastPlane(Vector2 mousePos, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit)
    {
        hit = Vector3.zero;
        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
        Plane plane = new Plane(planeNormal, planePoint);
        if (!plane.Raycast(ray, out float enter)) return false;
        hit = ray.GetPoint(enter);
        return true;
    }

    /// <summary>Screen angle (degrees) from a world-space pivot to a screen point.</summary>
    static float ScreenAngleFrom(Vector2 mousePos, Vector3 worldPivot)
    {
        Vector2 pivotScreen = HandleUtility.WorldToGUIPoint(worldPivot);
        Vector2 dir = mousePos - pivotScreen;
        return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Closest-point parameter t along a unit-length line from lineOrigin,
    /// for the point on the line nearest to the given ray.
    /// </summary>
    static float ProjectRayOntoLine(Ray ray, Vector3 lineOrigin, Vector3 lineDir)
    {
        Vector3 w = ray.origin - lineOrigin;
        float a = Vector3.Dot(ray.direction, ray.direction);
        float b = Vector3.Dot(ray.direction, lineDir);
        float d = Vector3.Dot(ray.direction, w);
        float e = Vector3.Dot(lineDir, w);

        float denom = a - b * b;
        if (denom < 0.0001f) return e;
        return (e * a - b * d) / denom;
    }

    /// <summary>
    /// Closest point on a world-space line segment to a screen position.
    /// Projects the screen position onto the segment in screen space, then lerps in world space.
    /// </summary>
    static Vector3 ClosestPointOnSegmentToScreenPos(Vector3 segA, Vector3 segB, Vector2 screenPos)
    {
        Vector2 sa = HandleUtility.WorldToGUIPoint(segA);
        Vector2 sb = HandleUtility.WorldToGUIPoint(segB);
        Vector2 ab = sb - sa;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.0001f) return (segA + segB) * 0.5f;
        float t = Mathf.Clamp01(Vector2.Dot(screenPos - sa, ab) / lenSq);
        return Vector3.Lerp(segA, segB, t);
    }

    // ─── Bounds Computation ─────────────────────────────────────

    static void ComputeBounds(Transform[] targets)
    {
        if (targets.Length == 1)
            ComputeOBB(targets[0]);
        else
            ComputeAABB(targets);

        for (int i = 0; i < 3; i++)
            boundsExtents[i] = Mathf.Max(boundsExtents[i], MinBoundsExtent);
    }

    /// <summary>
    /// OBB aligned to the object's local axes, encompassing all child
    /// MeshRenderers and SkinnedMeshRenderers using mesh-local bounds
    /// (not renderer.bounds which inflates for rotated objects).
    /// </summary>
    static void ComputeOBB(Transform t)
    {
        // Guard against denormalized quaternion (can happen mid-undo).
        Quaternion q = t.rotation;
        float sqrLen = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (sqrLen < 0.1f || sqrLen > 2f)
            q = Quaternion.identity;
        else if (sqrLen < 0.95f || sqrLen > 1.05f)
            q = Quaternion.Normalize(q);

        Vector3 axR = q * Vector3.right;
        Vector3 axU = q * Vector3.up;
        Vector3 axF = q * Vector3.forward;
        boundsAxes = new[] { axR, axU, axF };

        Vector3 pos = t.position;

        Bounds localBounds = default;
        bool first = true;

        // Collect mesh-local corners from MeshRenderers
        foreach (var mf in t.GetComponentsInChildren<MeshFilter>())
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || mf.sharedMesh == null) continue;

            Bounds mb = mf.sharedMesh.bounds;
            EncapsulateLocalBoundsCorners(mf.transform, mb, pos, axR, axU, axF, ref localBounds, ref first);
        }

        // Collect mesh-local corners from SkinnedMeshRenderers
        foreach (var smr in t.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            Bounds lb = smr.localBounds;
            EncapsulateLocalBoundsCorners(smr.transform, lb, pos, axR, axU, axF, ref localBounds, ref first);
        }

        if (first)
        {
            // No mesh renderers found — fall back to a point at the transform
            boundsCenter = pos;
            boundsExtents = new[] { 0f, 0f, 0f };
            return;
        }

        boundsCenter = pos + axR * localBounds.center.x
                           + axU * localBounds.center.y
                           + axF * localBounds.center.z;
        boundsExtents = new[] { localBounds.extents.x, localBounds.extents.y, localBounds.extents.z };
    }

    /// <summary>
    /// Transform 8 corners of a local-space bounds through a child transform
    /// into world space, then project onto root axes to build an enclosing OBB.
    /// </summary>
    static void EncapsulateLocalBoundsCorners(
        Transform child, Bounds localBounds,
        Vector3 rootPos, Vector3 axR, Vector3 axU, Vector3 axF,
        ref Bounds projBounds, ref bool first)
    {
        Vector3 center = localBounds.center;
        Vector3 ext    = localBounds.extents;

        for (int ci = 0; ci < 8; ci++)
        {
            float sx = (ci & 1) != 0 ? 1f : -1f;
            float sy = (ci & 2) != 0 ? 1f : -1f;
            float sz = (ci & 4) != 0 ? 1f : -1f;

            // Local-space corner → world-space via the child's transform
            Vector3 localCorner = center + new Vector3(ext.x * sx, ext.y * sy, ext.z * sz);
            Vector3 worldCorner = child.TransformPoint(localCorner);

            // Project onto root axes (OBB-local space relative to root position)
            Vector3 off = worldCorner - rootPos;
            Vector3 lp = new Vector3(
                Vector3.Dot(off, axR),
                Vector3.Dot(off, axU),
                Vector3.Dot(off, axF));

            if (first) { projBounds = new Bounds(lp, Vector3.zero); first = false; }
            else projBounds.Encapsulate(lp);
        }
    }

    /// <summary>AABB encompassing all targets and their child mesh renderers.</summary>
    static void ComputeAABB(Transform[] targets)
    {
        boundsAxes = new[] { Vector3.right, Vector3.up, Vector3.forward };

        Bounds combined = new Bounds(targets[0].position, Vector3.zero);
        foreach (var t in targets)
        {
            bool any = false;

            foreach (var mf in t.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null) continue;
                EncapsulateWorldBounds(mf.transform, mf.sharedMesh.bounds, ref combined);
                any = true;
            }

            foreach (var smr in t.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                EncapsulateWorldBounds(smr.transform, smr.localBounds, ref combined);
                any = true;
            }

            if (!any)
                combined.Encapsulate(t.position);
        }

        boundsCenter  = combined.center;
        boundsExtents = new[] { combined.extents.x, combined.extents.y, combined.extents.z };
    }

    /// <summary>Transform local bounds corners to world space and encapsulate into an AABB.</summary>
    static void EncapsulateWorldBounds(Transform child, Bounds localBounds, ref Bounds worldBounds)
    {
        Vector3 center = localBounds.center;
        Vector3 ext    = localBounds.extents;

        for (int ci = 0; ci < 8; ci++)
        {
            float sx = (ci & 1) != 0 ? 1f : -1f;
            float sy = (ci & 2) != 0 ? 1f : -1f;
            float sz = (ci & 4) != 0 ? 1f : -1f;

            Vector3 localCorner = center + new Vector3(ext.x * sx, ext.y * sy, ext.z * sz);
            worldBounds.Encapsulate(child.TransformPoint(localCorner));
        }
    }

    // ─── Hover Detection ────────────────────────────────────────

    static void DetectHover(SceneView sv, Vector2 mousePos, Mode mode)
    {
        switch (mode)
        {
            case Mode.Move:   DetectMoveHover(sv, mousePos);   break;
            case Mode.Rotate: DetectRotateHover(sv, mousePos); break;
            case Mode.Scale:  DetectScaleHover(sv, mousePos);  break;
        }
    }

    static void DetectMoveHover(SceneView sv, Vector2 mousePos)
    {
        // Y face hover → axis-locked Y movement
        int yFace = CheckYFaceHover(sv, mousePos);
        if (yFace >= 0) { hoveredKind = HoverKind.Face; hoveredIndex = yFace; return; }

        // Side face hover → lock movement to that face's plane
        int sideFace = CheckSideFaceHover(sv, mousePos);
        if (sideFace >= 0) { hoveredKind = HoverKind.Face; hoveredIndex = sideFace; return; }

        // Outside box → world XZ plane (no face highlights)
        hoveredKind  = HoverKind.AllSideFaces;
        hoveredIndex = 0;
    }

    static void DetectRotateHover(SceneView sv, Vector2 mousePos)
    {
        // Edge proximity first (highest priority)
        int edge = DetectNearestEdge(sv, mousePos);
        if (edge >= 0) { hoveredKind = HoverKind.Edge; hoveredIndex = edge; return; }

        // Y face hover
        int yFace = CheckYFaceHover(sv, mousePos);
        if (yFace >= 0) { hoveredKind = HoverKind.Face; hoveredIndex = yFace; return; }

        // Side face hover
        int sideFace = CheckSideFaceHover(sv, mousePos);
        if (sideFace >= 0) { hoveredKind = HoverKind.Face; hoveredIndex = sideFace; return; }

        // Outside → world Y-axis rotation (no face highlights)
        hoveredKind  = HoverKind.AllSideFaces;
        hoveredIndex = 0;
    }

    static void DetectScaleHover(SceneView sv, Vector2 mousePos)
    {
        // Y face hover (exact polygon hit)
        int yFace = CheckYFaceHover(sv, mousePos);
        if (yFace >= 0) { hoveredKind = HoverKind.Face; hoveredIndex = yFace; return; }

        // Y face proximity — if mouse is within EdgeHoverPx of a Y face edge,
        // give it to Y face. This provides precedence over quadrant-based side faces,
        // since side faces can always be selected from outside the box.
        int yProx = CheckYFaceProximity(sv, mousePos);
        if (yProx >= 0) { hoveredKind = HoverKind.Face; hoveredIndex = yProx; return; }

        // Quadrant for side faces (always picks one)
        hoveredKind  = HoverKind.Face;
        hoveredIndex = GetQuadrantFace(sv, mousePos);
    }

    /// <summary>Check if mouse is within EdgeHoverPx of a front-facing Y face's edges.</summary>
    static int CheckYFaceProximity(SceneView sv, Vector2 mousePos)
    {
        Vector3 camFwd = sv.camera.transform.forward;
        float threshold = EdgeHoverPx;
        for (int face = 2; face <= 3; face++)
        {
            Vector3 normal = GetFaceNormal(face);
            if (Vector3.Dot(normal, camFwd) > 0f) continue;

            Vector3[] corners = GetFaceWorldCorners(face);
            Vector2[] screen = new Vector2[4];
            for (int i = 0; i < 4; i++)
                screen[i] = HandleUtility.WorldToGUIPoint(corners[i]);

            // Check distance to each edge of the face quad
            for (int i = 0; i < 4; i++)
            {
                float dist = DistPointToSegment2D(mousePos, screen[i], screen[(i + 1) % 4]);
                if (dist < threshold) return face;
            }
        }
        return -1;
    }

    // ─── Hover Helpers ──────────────────────────────────────────

    /// <summary>Check if mouse is over top (+Y=2) or bottom (-Y=3) face polygon. Skips back-facing.</summary>
    static int CheckYFaceHover(SceneView sv, Vector2 mousePos)
    {
        Vector3 camFwd = sv.camera.transform.forward;
        for (int face = 2; face <= 3; face++)
        {
            // Skip back-facing Y faces
            Vector3 normal = GetFaceNormal(face);
            if (Vector3.Dot(normal, camFwd) > 0f) continue;

            Vector3[] corners = GetFaceWorldCorners(face);
            Vector2[] screen = new Vector2[4];
            for (int i = 0; i < 4; i++)
                screen[i] = HandleUtility.WorldToGUIPoint(corners[i]);
            if (IsPointInScreenQuad(mousePos, screen))
                return face;
        }
        return -1;
    }

    /// <summary>Check if mouse is over any front-facing side face (0,1,4,5).</summary>
    static int CheckSideFaceHover(SceneView sv, Vector2 mousePos)
    {
        Vector3 camFwd = sv.camera.transform.forward;
        int[] sideFaces = { 0, 1, 4, 5 };
        foreach (int face in sideFaces)
        {
            Vector3 normal = GetFaceNormal(face);
            // Skip back-facing faces
            if (Vector3.Dot(normal, camFwd) > 0f) continue;

            Vector3[] corners = GetFaceWorldCorners(face);
            Vector2[] screen = new Vector2[4];
            for (int i = 0; i < 4; i++)
                screen[i] = HandleUtility.WorldToGUIPoint(corners[i]);
            if (IsPointInScreenQuad(mousePos, screen))
                return face;
        }
        return -1;
    }

    /// <summary>Quadrant method: pick the side face whose screen-space center is most aligned with mouse direction.</summary>
    static int GetQuadrantFace(SceneView sv, Vector2 mousePos)
    {
        Vector2 centerScreen = HandleUtility.WorldToGUIPoint(boundsCenter);
        Vector2 mouseDir = mousePos - centerScreen;
        if (mouseDir.sqrMagnitude < 0.001f) return 0;
        mouseDir.Normalize();

        int bestFace = 0;
        float bestDot = float.NegativeInfinity;

        int[] sideFaces = { 0, 1, 4, 5 };
        foreach (int face in sideFaces)
        {
            Vector3 fc = GetFaceCenter(face);
            Vector2 fcScreen = HandleUtility.WorldToGUIPoint(fc);
            Vector2 fDir = fcScreen - centerScreen;
            if (fDir.sqrMagnitude < 0.001f) continue;
            float dot = Vector2.Dot(mouseDir, fDir.normalized);
            if (dot > bestDot) { bestDot = dot; bestFace = face; }
        }
        return bestFace;
    }

    /// <summary>Find the nearest front-facing edge within threshold pixels, or -1.</summary>
    static int DetectNearestEdge(SceneView sv, Vector2 mousePos)
    {
        Vector3[] corners = GetBoxCorners();
        float threshold = EdgeHoverPx;
        int bestEdge = -1;
        float bestDist = threshold;

        for (int ei = 0; ei < 12; ei++)
        {
            int[] ec = EdgeCornerIndices[ei];
            Vector2 a = HandleUtility.WorldToGUIPoint(corners[ec[0]]);
            Vector2 b = HandleUtility.WorldToGUIPoint(corners[ec[1]]);
            float dist = DistPointToSegment2D(mousePos, a, b);
            if (dist < bestDist) { bestDist = dist; bestEdge = ei; }
        }
        return bestEdge;
    }

    /// <summary>
    /// Returns true if at least one of the edge's two adjacent faces is front-facing.
    /// An edge along axis A is bordered by faces on the two other axes (B and C).
    /// The specific +/- face depends on which side of center the edge sits.
    /// </summary>
    static bool IsEdgeVisible(int edgeIdx, Vector3 camFwd)
    {
        int mainAxis = edgeIdx / 4;

        // The two perpendicular axis indices
        int perpA, perpB;
        switch (mainAxis)
        {
            case 0: perpA = 1; perpB = 2; break;
            case 1: perpA = 0; perpB = 2; break;
            default: perpA = 0; perpB = 1; break;
        }

        // Use the first corner's bit pattern to determine which face on each perp axis.
        // Corner bit N = axis N sign (0 = negative, 1 = positive).
        int[] ec = EdgeCornerIndices[edgeIdx];
        int cornerSample = ec[0]; // either corner gives same perp-axis signs

        // Check face on perpA axis
        bool perpAPositive = ((cornerSample >> perpA) & 1) != 0;
        int faceA = perpA * 2 + (perpAPositive ? 0 : 1);
        Vector3 normalA = GetFaceNormal(faceA);

        // Check face on perpB axis
        bool perpBPositive = ((cornerSample >> perpB) & 1) != 0;
        int faceB = perpB * 2 + (perpBPositive ? 0 : 1);
        Vector3 normalB = GetFaceNormal(faceB);

        // At least one adjacent face must be front-facing (normal opposes camera forward)
        return Vector3.Dot(normalA, camFwd) < 0f || Vector3.Dot(normalB, camFwd) < 0f;
    }

    static bool IsPointInScreenQuad(Vector2 point, Vector2[] quad)
    {
        bool allPos = true, allNeg = true;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = quad[i], b = quad[(i + 1) % 4];
            float cross = (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);
            if (cross > 0f) allNeg = false;
            if (cross < 0f) allPos = false;
        }
        return allPos || allNeg;
    }

    static float DistPointToSegment2D(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.0001f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        Vector2 proj = a + ab * t;
        return Vector2.Distance(p, proj);
    }

    // ─── Box Geometry ───────────────────────────────────────────

    /// <summary>Face normal (unit vector) for a face index 0-5.</summary>
    static Vector3 GetFaceNormal(int face)
    {
        int axisIdx = face / 2;
        float sign = (face % 2 == 0) ? 1f : -1f;
        return boundsAxes[axisIdx] * sign;
    }

    /// <summary>World-space center of a face.</summary>
    static Vector3 GetFaceCenter(int face)
    {
        return boundsCenter + GetFaceNormal(face) * boundsExtents[face / 2];
    }

    /// <summary>Find which local axis (0=X,1=Y,2=Z) best matches a world direction.</summary>
    static int GetBestLocalAxis(Quaternion rotation, Vector3 worldDir)
    {
        float bestDot = 0f;
        int best = 0;
        for (int i = 0; i < 3; i++)
        {
            Vector3 ax = rotation * (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
            float dot = Mathf.Abs(Vector3.Dot(ax, worldDir));
            if (dot > bestDot) { bestDot = dot; best = i; }
        }
        return best;
    }

    /// <summary>
    /// 8 corners of the bounding box.
    /// Corner index bits: bit0=X sign, bit1=Y sign, bit2=Z sign.
    /// </summary>
    static Vector3[] GetBoxCorners()
    {
        Vector3[] c = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            float sx = (i & 1) != 0 ? 1f : -1f;
            float sy = (i & 2) != 0 ? 1f : -1f;
            float sz = (i & 4) != 0 ? 1f : -1f;
            c[i] = boundsCenter
                + boundsAxes[0] * (boundsExtents[0] * sx)
                + boundsAxes[1] * (boundsExtents[1] * sy)
                + boundsAxes[2] * (boundsExtents[2] * sz);
        }
        return c;
    }

    /// <summary>Corner indices for each face (winding order).</summary>
    static readonly int[][] FaceCornerIndices =
    {
        new[] { 1, 3, 7, 5 }, // face 0: +X
        new[] { 0, 4, 6, 2 }, // face 1: -X
        new[] { 2, 6, 7, 3 }, // face 2: +Y
        new[] { 0, 1, 5, 4 }, // face 3: -Y
        new[] { 4, 5, 7, 6 }, // face 4: +Z
        new[] { 0, 2, 3, 1 }, // face 5: -Z
    };

    /// <summary>Corner pairs for each of 12 edges. Edges 0-3 along axis 0, 4-7 axis 1, 8-11 axis 2.</summary>
    static readonly int[][] EdgeCornerIndices =
    {
        new[] {0,1}, new[] {2,3}, new[] {4,5}, new[] {6,7}, // along axis 0
        new[] {0,2}, new[] {1,3}, new[] {4,6}, new[] {5,7}, // along axis 1
        new[] {0,4}, new[] {1,5}, new[] {2,6}, new[] {3,7}, // along axis 2
    };

    static Vector3[] GetFaceWorldCorners(int face)
    {
        Vector3[] all = GetBoxCorners();
        int[] ci = FaceCornerIndices[face];
        return new[] { all[ci[0]], all[ci[1]], all[ci[2]], all[ci[3]] };
    }

    /// <summary>World-space direction of an edge (unit vector along the edge's axis).</summary>
    static Vector3 GetEdgeDirection(int edgeIdx)
    {
        return boundsAxes[edgeIdx / 4];
    }

    /// <summary>World-space midpoint of an edge.</summary>
    static Vector3 GetEdgeMidpoint(int edgeIdx)
    {
        Vector3[] corners = GetBoxCorners();
        int[] ec = EdgeCornerIndices[edgeIdx];
        return (corners[ec[0]] + corners[ec[1]]) * 0.5f;
    }

    // ─── Common Helpers ─────────────────────────────────────────

    static void SnapshotTransforms()
    {
        int n = dragTargets.Length;
        startPositions = new Vector3[n];
        startRotations = new Quaternion[n];
        startScales    = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            startPositions[i] = dragTargets[i].position;
            startRotations[i] = dragTargets[i].rotation;
            startScales[i]    = dragTargets[i].localScale;
        }
    }

    static Vector3 ComputePivot()
    {
        Vector3 sum = Vector3.zero;
        foreach (var t in dragTargets) sum += t.position;
        return sum / dragTargets.Length;
    }

    static void RevertToSnapshot()
    {
        if (dragTargets == null || startPositions == null) return;
        for (int i = 0; i < dragTargets.Length; i++)
        {
            if (dragTargets[i] == null) continue;
            dragTargets[i].position   = startPositions[i];
            dragTargets[i].rotation   = startRotations[i];
            dragTargets[i].localScale = startScales[i];
        }
    }

    static void DuplicateAndSwapTargets()
    {
        didDuplicate = true;

        var originals = new GameObject[dragTargets.Length];
        for (int i = 0; i < dragTargets.Length; i++)
            originals[i] = dragTargets[i].gameObject;

        Selection.objects = originals;
        Undo.SetCurrentGroupName("QuickTransform Duplicate " + activeMode);
        EditorApplication.ExecuteMenuItem("Edit/Duplicate");

        Transform[] duplicates = Selection.transforms;
        if (duplicates == null || duplicates.Length == 0) return;

        dragTargets = duplicates;
        SnapshotTransforms();
        selectionPivot = ComputePivot();
    }

    static void ResetState()
    {
        phase = Phase.Idle;
        activeMode = Mode.None;
        dragTargets = null;
        startPositions = null;
        startRotations = null;
        startScales = null;
        hoveredKind = HoverKind.None;
        lockedKind  = HoverKind.None;
        hoveredIndex = 0;
        lockedIndex  = 0;
        movePlaneNormal = Vector3.up;
        moveAlongNormal = false;
        dragButton = 0;
        shiftHeldOnPress = false;
        didDuplicate = false;
    }

    static bool ModeKeyReleased()
    {
        return activeMode switch
        {
            Mode.Move   => !wHeld,
            Mode.Rotate => !eHeld,
            Mode.Scale  => !rHeld,
            _           => true
        };
    }

    static string GetUndoName()
    {
        return activeMode switch
        {
            Mode.Move   => "QuickTransform Move",
            Mode.Rotate => "QuickTransform Rotate",
            Mode.Scale  => "QuickTransform Scale",
            _           => "QuickTransform"
        };
    }

    static bool IsMouseEvent(Event e)
    {
        return e.type == EventType.MouseDown || e.type == EventType.MouseDrag
            || e.type == EventType.MouseUp   || e.type == EventType.MouseMove;
    }

    // ─── Visual Feedback ────────────────────────────────────────

    static void DrawFeedback(SceneView sv)
    {
        if (dragTargets == null) return;

        ComputeBounds(dragTargets);
        DrawBoundsBox(lockedKind, lockedIndex, activeMode);

        // Rotation gizmo: circle + line from pivot to mouse
        if (activeMode == Mode.Rotate && (lockedKind == HoverKind.Face || lockedKind == HoverKind.Edge))
        {
            DrawRotationGizmo(sv);
        }

        // World-space gizmo while dragging outside bounds
        if (lockedKind == HoverKind.AllSideFaces)
        {
            if (activeMode == Mode.Move)        DrawWorldMoveGizmo(boundsCenter);
            else if (activeMode == Mode.Rotate) DrawWorldRotateGizmo(boundsCenter);
        }
    }

    /// <summary>
    /// Draws a circle on the rotation plane at the pivot, a faint reference line at the
    /// original orientation, and a dynamic line showing the accumulated rotation.
    /// When Ctrl is held (snapping), shows the degree value outside the circle.
    /// </summary>
    static void DrawRotationGizmo(SceneView sv)
    {
        Color gizmoColor = GetModeColor(Mode.Rotate, 0.8f);

        // Fixed screen-space radius: HandleUtility.GetHandleSize ≈ 64px at that world point
        float worldRadius = HandleUtility.GetHandleSize(rotatePivot) * CircleRadius;

        // Circle
        Handles.color = gizmoColor;
        Handles.DrawWireDisc(rotatePivot, rotateAxis, worldRadius);

        // Faint reference line at original orientation (stays fixed)
        if (rotateStartDir.sqrMagnitude > 0.0001f)
        {
            Handles.color = GetModeColor(Mode.Rotate, 0.2f);
            Handles.DrawLine(rotatePivot, rotatePivot + rotateStartDir * worldRadius, 1f);
        }

        // Dynamic line showing accumulated rotation direction
        float sign = LinearRotation
            ? rotateLinearSign
            : (Vector3.Dot(sv.camera.transform.forward, rotateAxis) > 0f ? -1f : 1f);
        Quaternion vizRot = Quaternion.AngleAxis(sign * rotateAccumAngle, rotateAxis);
        Vector3 lineDir = vizRot * rotateStartDir;
        if (lineDir.sqrMagnitude > 0.0001f)
        {
            Handles.color = gizmoColor;
            Handles.DrawLine(rotatePivot, rotatePivot + lineDir.normalized * worldRadius, 2f);
        }

        // Degree label outside the circle when Ctrl-snapping
        if (Event.current.control && lineDir.sqrMagnitude > 0.0001f)
        {
            Vector3 labelPos = rotatePivot + lineDir.normalized * worldRadius * 1.25f;
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleCenter,
            };
            Handles.Label(labelPos, $"{rotateAccumAngle:F0}°", style);
        }
    }

    // ─── Box Drawing ────────────────────────────────────────────

    static void DrawBoundsBox(HoverKind kind, int index, Mode mode)
    {
        Vector3[] corners = GetBoxCorners();

        // ── Wireframe ──
        Handles.color = new Color(1f, 1f, 1f, 0.25f);
        int[][] edges =
        {
            new[] {0,1}, new[] {2,3}, new[] {4,5}, new[] {6,7},
            new[] {0,2}, new[] {1,3}, new[] {4,6}, new[] {5,7},
            new[] {0,4}, new[] {1,5}, new[] {2,6}, new[] {3,7},
        };
        foreach (var edge in edges)
            Handles.DrawLine(corners[edge[0]], corners[edge[1]]);

        // ── Highlights ──
        switch (kind)
        {
            case HoverKind.AllSideFaces:
                // Outside box: wireframe only, no face highlights (world-space operation)
                break;

            case HoverKind.Face:
                DrawFaceHighlight(corners, index, mode);
                break;

            case HoverKind.Edge:
                DrawEdgeHighlight(corners, index, mode);
                break;
        }
    }

    static void DrawFaceHighlight(Vector3[] corners, int face, Mode mode)
    {
        if (face < 0 || face >= 6) return;
        int[] ci = FaceCornerIndices[face];
        Vector3[] verts = { corners[ci[0]], corners[ci[1]], corners[ci[2]], corners[ci[3]] };

        Color fill, outline;
        if (mode == Mode.Scale)
        {
            int axisIdx = face / 2;
            fill = axisIdx == 0 ? new Color(1f, 0.3f, 0.3f, 0.25f)
                 : axisIdx == 1 ? new Color(0.3f, 1f, 0.3f, 0.25f)
                 : new Color(0.3f, 0.3f, 1f, 0.25f);
        }
        else
        {
            fill = GetModeColor(mode, 0.2f);
        }
        outline = fill; outline.a = 0.9f;
        Handles.DrawSolidRectangleWithOutline(verts, fill, outline);
    }

    static void DrawEdgeHighlight(Vector3[] corners, int edgeIdx, Mode mode)
    {
        if (edgeIdx < 0 || edgeIdx >= 12) return;
        int[] ec = EdgeCornerIndices[edgeIdx];
        Color c = GetModeColor(mode, 0.9f);
        Handles.color = c;
        Handles.DrawLine(corners[ec[0]], corners[ec[1]], 4f);
    }

    static Color GetModeColor(Mode mode, float alpha)
    {
        return mode switch
        {
            Mode.Move   => new Color(1f, 0.8f, 0.2f, alpha),   // yellow-orange
            Mode.Rotate => new Color(0.2f, 0.9f, 0.2f, alpha), // green
            Mode.Scale  => new Color(0.5f, 0.5f, 1f, alpha),   // blue-ish
            _           => new Color(1f, 1f, 1f, alpha),
        };
    }

    // ─── World-Space Gizmo Preview (outside bounding box) ────

    /// <summary>
    /// Move gizmo showing world XZ plane: X and Z arrows active (yellow),
    /// Y arrow grayed out, small XZ plane square.
    /// </summary>
    static void DrawWorldMoveGizmo(Vector3 center)
    {
        float sz = HandleUtility.GetHandleSize(center) * 0.9f;
        Color active   = new Color(1f, 1f, 0.2f, 0.8f);
        Color dimGreen = new Color(0.4f, 0.7f, 0.4f, 0.2f);

        // Active X axis
        Handles.color = active;
        Handles.DrawLine(center, center + Vector3.right * sz, 2f);
        Handles.ConeHandleCap(0, center + Vector3.right * sz,
            Quaternion.LookRotation(Vector3.right), sz * 0.1f, EventType.Repaint);

        // Active Z axis
        Handles.DrawLine(center, center + Vector3.forward * sz, 2f);
        Handles.ConeHandleCap(0, center + Vector3.forward * sz,
            Quaternion.LookRotation(Vector3.forward), sz * 0.1f, EventType.Repaint);

        // Inactive Y axis
        Handles.color = dimGreen;
        Handles.DrawLine(center, center + Vector3.up * sz);

        // XZ plane square
        float q = sz * 0.2f;
        Vector3[] quad =
        {
            center,
            center + Vector3.right * q,
            center + Vector3.right * q + Vector3.forward * q,
            center + Vector3.forward * q,
        };
        Handles.DrawSolidRectangleWithOutline(quad,
            new Color(1f, 1f, 0.2f, 0.12f), active);
    }

    /// <summary>
    /// Rotate gizmo showing world Y axis: Y disc active (yellow),
    /// X and Z discs grayed out.
    /// </summary>
    static void DrawWorldRotateGizmo(Vector3 center)
    {
        float sz = HandleUtility.GetHandleSize(center) * 0.9f;
        Color active = new Color(1f, 1f, 0.2f, 0.8f);

        // Active Y rotation disc
        Handles.color = active;
        Handles.DrawWireDisc(center, Vector3.up, sz);

        // Inactive X disc
        Handles.color = new Color(0.8f, 0.3f, 0.3f, 0.15f);
        Handles.DrawWireDisc(center, Vector3.right, sz);

        // Inactive Z disc
        Handles.color = new Color(0.3f, 0.3f, 0.8f, 0.15f);
        Handles.DrawWireDisc(center, Vector3.forward, sz);
    }
}
