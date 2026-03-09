using UnityEditor;
using UnityEngine;

/// <summary>
/// Quick Transform: hold W/E/R + drag anywhere in the Scene View to
/// move/rotate/scale selected objects without grabbing a gizmo handle.
/// Standard tool switching (tap W/E/R) still works normally.
///
/// W = Move (plane ⊥ upAxis), E = Rotate (around upAxis), R = Scale (local X or Z)
/// W+Shift = Duplicate then move
/// Multi-select rotation uses the selection's center pivot.
///
/// Configure per-project via a QuickTransformConfig ScriptableObject asset.
/// If no config asset exists, defaults are used (Y-up, click-required).
/// </summary>
[InitializeOnLoad]
static class QuickTransform
{
    // ─── Enums ──────────────────────────────────────────────────

    enum Mode { None, Move, Rotate, Scale }
    enum Phase { Idle, Ready, Dragging }

    // ─── Config ─────────────────────────────────────────────────

    static QuickTransformConfig config;
    static double configRetryTime;  // next EditorApplication.timeSinceStartup to retry FindAssets

    static QuickTransformConfig GetConfig()
    {
        if (config != null) return config;
        // Avoid calling FindAssets every event — retry at most once per second.
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

    /// <summary>Configured up axis, or Vector3.up if no config asset exists.</summary>
    static Vector3 UpAxis
    {
        get
        {
            var cfg = GetConfig();
            return cfg != null ? cfg.upAxis.normalized : Vector3.up;
        }
    }

    /// <summary>Whether click-free drag mode is enabled.</summary>
    static bool ClickFreeDrag
    {
        get
        {
            var cfg = GetConfig();
            return cfg != null && cfg.clickFreeDrag;
        }
    }

    // ─── State ──────────────────────────────────────────────────

    static Mode  activeMode;
    static Phase phase;

    // Key tracking
    static bool wHeld, eHeld, rHeld;
    static Mode suppressKeyUpFor;

    // Drag data
    static Vector2      mousePressPos;
    static Transform[]  dragTargets;
    static Vector3[]    startPositions;
    static Quaternion[] startRotations;
    static Vector3[]    startScales;
    static Vector3      selectionPivot;

    // Move
    static Vector3 moveHitStart;

    // Rotate
    static float rotateAngleStart;

    // Scale  (world-space raycasting for true 1:1 tracking)
    static int     scaleAxisIndex;          // 0 = local X, 2 = local Z
    static bool    scaleAxisChosen;
    static int     scalePullSign;           // +1 = pulling positive face, -1 = pulling negative face
    static Vector3 scaleAnchorWorld;        // opposite face center in world space
    static Vector3 scaleWorldAxis;          // world-space direction of the scale axis
    static float   scaleFaceStartDist;      // world distance: anchor → active face at start (= full extent)
    static float   scaleMouseStartDist;     // world distance: anchor → mouse raycast along axis at start

    // Duplicate-move
    static bool shiftHeldOnPress;        // was Shift down when mouse was pressed?
    static bool didDuplicate;            // did we already duplicate this drag?

    const float DragThresholdPx = 3f;

    // ─── Hook ───────────────────────────────────────────────────

    static QuickTransform()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    // ─── Main Loop ──────────────────────────────────────────────

    static void OnSceneGUI(SceneView sv)
    {
        Event e = Event.current;

        // Focus loss — reset everything
        if (e.type == EventType.MouseLeaveWindow)
        {
            wHeld = eHeld = rHeld = false;
            if (phase == Phase.Dragging)
                RevertToSnapshot();
            if (phase != Phase.Idle)
            {
                GUIUtility.hotControl = 0;
                ResetState();
            }
            return;
        }

        // ── Key tracking ────────────────────────────────────────
        UpdateKeyTracking(e);

        // ── Suppress tool-switch KeyUp after a successful drag ──
        if (suppressKeyUpFor != Mode.None && e.type == EventType.KeyUp)
        {
            bool suppress = (suppressKeyUpFor == Mode.Move   && e.keyCode == KeyCode.W)
                         || (suppressKeyUpFor == Mode.Rotate && e.keyCode == KeyCode.E)
                         || (suppressKeyUpFor == Mode.Scale  && e.keyCode == KeyCode.R);
            if (suppress)
            {
                suppressKeyUpFor = Mode.None;
                e.Use();
                return;
            }
        }

        // ── Nothing selected → nothing to do ────────────────────
        Transform[] selected = Selection.transforms;
        if (selected == null || selected.Length == 0)
        {
            if (phase != Phase.Idle)
            {
                GUIUtility.hotControl = 0;
                ResetState();
            }
            return;
        }

        Mode heldMode = GetHeldMode();

        // ── Phase dispatch ──────────────────────────────────────
        switch (phase)
        {
            case Phase.Idle:
                HandleIdle(e, sv, selected, heldMode);
                break;
            case Phase.Ready:
                HandleReady(e, sv);
                break;
            case Phase.Dragging:
                HandleDragging(e, sv);
                break;
        }

        // ── Cursor feedback ─────────────────────────────────────
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
        if (e.type == EventType.KeyDown && !e.alt && !e.control)
        {
            if (e.keyCode == KeyCode.W) wHeld = true;
            if (e.keyCode == KeyCode.E) eHeld = true;
            if (e.keyCode == KeyCode.R) rHeld = true;
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

        bool clickFree = ClickFreeDrag;

        // Click mode: require LMB press.
        // Click-free mode: enter Ready on the KeyDown that starts the mode.
        if (!clickFree)
        {
            if (e.type != EventType.MouseDown || e.button != 0) return;
        }
        else
        {
            if (e.type != EventType.KeyDown) return;
        }

        activeMode = heldMode;
        phase = Phase.Ready;
        mousePressPos = e.mousePosition;
        shiftHeldOnPress = !clickFree && e.shift; // shift-duplicate only in click mode
        didDuplicate = false;

        // Cache targets — immune to selection changes mid-drag
        dragTargets = selected;
        SnapshotTransforms();

        selectionPivot = ComputePivot();

        // Pre-compute mode-specific start data
        switch (activeMode)
        {
            case Mode.Move:
                RaycastMovePlane(sv, e.mousePosition, selectionPivot, out moveHitStart);
                break;
            case Mode.Rotate:
                rotateAngleStart = ScreenAngleFromPivot(e.mousePosition);
                break;
            case Mode.Scale:
                scaleAxisChosen = false;
                break;
        }

        // Claim input control
        int id = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(id);
        GUIUtility.hotControl = id;

        e.Use();
    }

    // ─── Phase: Ready (waiting for drag threshold) ──────────────

    static void HandleReady(Event e, SceneView sv)
    {
        bool clickFree = ClickFreeDrag;

        // ── Abort / commit conditions ───────────────────────────
        if (ModeKeyReleased())
        {
            // Click-free: key release in Ready = nothing happened, just reset.
            // Click mode: same — haven't started dragging yet.
            GUIUtility.hotControl = 0;
            ResetState();
            return;
        }

        if (!clickFree && e.type == EventType.MouseUp && e.button == 0)
        {
            GUIUtility.hotControl = 0;
            ResetState();
            e.Use();
            return;
        }

        // ── Threshold detection ─────────────────────────────────
        // Click mode: MouseDrag (button held).
        // Click-free: MouseMove (no button needed).
        bool isDragEvent = (e.type == EventType.MouseDrag && e.button == 0)
                        || (clickFree && e.type == EventType.MouseMove);

        if (isDragEvent)
        {
            if (Vector2.Distance(e.mousePosition, mousePressPos) >= DragThresholdPx)
            {
                // Shift = duplicate first, then drag the copies
                if (shiftHeldOnPress && !didDuplicate)
                    DuplicateAndSwapTargets();

                // Begin drag — register undo
                string undoName = GetUndoName();
                foreach (var t in dragTargets)
                    Undo.RecordObject(t, undoName);

                phase = Phase.Dragging;
                ApplyDrag(e, sv);
            }
            e.Use();
            return;
        }

        if (IsMouseEvent(e)) e.Use();
    }

    // ─── Phase: Dragging ────────────────────────────────────────

    static void HandleDragging(Event e, SceneView sv)
    {
        bool clickFree = ClickFreeDrag;

        // ── Continuous drag ─────────────────────────────────────
        bool isDragEvent = (e.type == EventType.MouseDrag && e.button == 0)
                        || (clickFree && e.type == EventType.MouseMove);

        if (isDragEvent)
        {
            ApplyDrag(e, sv);
            e.Use();
            return;
        }

        // ── Commit conditions ───────────────────────────────────
        bool commitOnMouseUp = !clickFree && e.type == EventType.MouseUp && e.button == 0;
        bool commitOnKeyUp   = ModeKeyReleased();
        // Click-free: LMB click while dragging also commits (prevents click falling through)
        bool commitOnClick   = clickFree && e.type == EventType.MouseDown && e.button == 0;

        if (commitOnMouseUp || commitOnKeyUp || commitOnClick)
        {
            Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
            GUIUtility.hotControl = 0;
            suppressKeyUpFor = activeMode;
            ResetState();
            e.Use();
            return;
        }

        // Draw visual feedback
        if (e.type == EventType.Repaint)
            DrawFeedback();

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
        if (!RaycastMovePlane(sv, mousePos, selectionPivot, out Vector3 hit))
            return;

        Vector3 delta = hit - moveHitStart;
        for (int i = 0; i < dragTargets.Length; i++)
            dragTargets[i].position = startPositions[i] + delta;
    }

    static void ApplyRotate(SceneView sv, Vector2 mousePos)
    {
        Vector3 upAxis = UpAxis;
        float currentAngle = ScreenAngleFromPivot(mousePos);
        float delta = currentAngle - rotateAngleStart;

        // Sign correction: ensure CW mouse motion = CW world rotation
        float sign = Vector3.Dot(sv.camera.transform.up, upAxis) > 0f ? 1f : -1f;
        Quaternion rot = Quaternion.AngleAxis(sign * delta, upAxis);

        for (int i = 0; i < dragTargets.Length; i++)
        {
            Vector3 offset = startPositions[i] - selectionPivot;
            dragTargets[i].position = selectionPivot + rot * offset;
            dragTargets[i].rotation = rot * startRotations[i];
        }
    }

    static void ApplyScale(SceneView sv, Vector2 mousePos)
    {
        Vector2 mouseDelta = mousePos - mousePressPos;

        if (!scaleAxisChosen)
        {
            if (mouseDelta.magnitude < DragThresholdPx) return;

            scaleAxisIndex = ChooseDominantAxis(sv, mouseDelta);
            scaleAxisChosen = true;

            // Determine which side of the axis we're pulling.
            Vector2 pivotScreen = HandleUtility.WorldToGUIPoint(selectionPivot);
            Vector2 axisDir = GetAxisScreenDir(sv, dragTargets[0], scaleAxisIndex);
            float dot = Vector2.Dot(mousePressPos - pivotScreen, axisDir);
            scalePullSign = dot >= 0f ? 1 : -1;

            // World-space axis and anchor for the reference object.
            scaleWorldAxis = scaleAxisIndex == 0
                ? dragTargets[0].rotation * Vector3.right
                : dragTargets[0].rotation * Vector3.forward;
            float refScale = scaleAxisIndex == 0 ? startScales[0].x : startScales[0].z;

            // Anchor = opposite face (stays fixed). Face = side being pulled.
            scaleAnchorWorld = startPositions[0] - scaleWorldAxis * (scalePullSign * refScale * 0.5f);
            Vector3 faceWorld = startPositions[0] + scaleWorldAxis * (scalePullSign * refScale * 0.5f);

            // World distance from anchor to face along the axis (= full extent along axis).
            scaleFaceStartDist = Vector3.Dot(faceWorld - scaleAnchorWorld, scaleWorldAxis) * scalePullSign;

            // Raycast mouse press to move plane, project onto axis to get starting mouse distance.
            if (RaycastMovePlane(sv, mousePressPos, startPositions[0], out Vector3 mouseWorldStart))
                scaleMouseStartDist = Vector3.Dot(mouseWorldStart - scaleAnchorWorld, scaleWorldAxis) * scalePullSign;
            else
                scaleMouseStartDist = scaleFaceStartDist; // fallback: pretend we clicked on the face
        }

        // Raycast current mouse to move plane, project onto the scale axis.
        if (!RaycastMovePlane(sv, mousePos, startPositions[0], out Vector3 mouseWorld))
            return;

        float mouseAlongAxis = Vector3.Dot(mouseWorld - scaleAnchorWorld, scaleWorldAxis) * scalePullSign;
        float mouseWorldDelta = mouseAlongAxis - scaleMouseStartDist;

        // The face should move the same world distance as the mouse along the axis.
        // factor = (original extent + mouse delta) / original extent
        float factor;
        if (Mathf.Abs(scaleFaceStartDist) < 0.001f)
            factor = Mathf.Max(1f + mouseWorldDelta, 0.01f);
        else
            factor = Mathf.Max((scaleFaceStartDist + mouseWorldDelta) / scaleFaceStartDist, 0.01f);

        for (int i = 0; i < dragTargets.Length; i++)
        {
            Vector3 s = startScales[i];
            float oldAxisScale, newAxisScale;
            Vector3 worldAxis;

            if (scaleAxisIndex == 0)
            {
                oldAxisScale = startScales[i].x;
                newAxisScale = oldAxisScale * factor;
                s.x = newAxisScale;
                worldAxis = startRotations[i] * Vector3.right;
            }
            else
            {
                oldAxisScale = startScales[i].z;
                newAxisScale = oldAxisScale * factor;
                s.z = newAxisScale;
                worldAxis = startRotations[i] * Vector3.forward;
            }

            dragTargets[i].localScale = s;

            // Offset position to keep the opposite face fixed.
            float scaleDelta = newAxisScale - oldAxisScale;
            dragTargets[i].position = startPositions[i]
                + worldAxis * (scalePullSign * scaleDelta * 0.5f);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Raycast a screen point onto the movement plane (perpendicular to upAxis,
    /// passing through <paramref name="planePoint"/>).
    /// </summary>
    static bool RaycastMovePlane(SceneView sv, Vector2 mousePos, Vector3 planePoint, out Vector3 hit)
    {
        hit = Vector3.zero;
        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
        Plane plane = new Plane(UpAxis, planePoint);
        if (!plane.Raycast(ray, out float enter)) return false;
        hit = ray.GetPoint(enter);
        return true;
    }

    static float ScreenAngleFromPivot(Vector2 mousePos)
    {
        Vector2 pivotScreen = HandleUtility.WorldToGUIPoint(selectionPivot);
        Vector2 dir = mousePos - pivotScreen;
        return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
    }

    static int ChooseDominantAxis(SceneView sv, Vector2 mouseDelta)
    {
        Vector2 xScreen = GetAxisScreenDir(sv, dragTargets[0], 0);
        Vector2 zScreen = GetAxisScreenDir(sv, dragTargets[0], 2);

        float dotX = Mathf.Abs(Vector2.Dot(mouseDelta.normalized, xScreen.normalized));
        float dotZ = Mathf.Abs(Vector2.Dot(mouseDelta.normalized, zScreen.normalized));

        return dotX >= dotZ ? 0 : 2;
    }

    static Vector2 GetAxisScreenDir(SceneView sv, Transform t, int axisIndex)
    {
        Vector3 worldAxis = axisIndex == 0 ? t.right : t.forward;
        Vector2 originScreen = HandleUtility.WorldToGUIPoint(t.position);
        Vector2 tipScreen = HandleUtility.WorldToGUIPoint(t.position + worldAxis);
        Vector2 dir = tipScreen - originScreen;
        return dir.magnitude > 0.001f ? dir.normalized : Vector2.right;
    }

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
        foreach (var t in dragTargets)
            sum += t.position;
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

    /// <summary>
    /// Duplicate selected objects and swap drag targets to the new copies.
    /// The originals stay in place; the copies become the active drag targets.
    /// </summary>
    static void DuplicateAndSwapTargets()
    {
        didDuplicate = true;

        // Save current selection, duplicate via menu command
        var originalObjects = new GameObject[dragTargets.Length];
        for (int i = 0; i < dragTargets.Length; i++)
            originalObjects[i] = dragTargets[i].gameObject;

        Selection.objects = originalObjects;
        Undo.SetCurrentGroupName("QuickTransform Duplicate " + activeMode);
        EditorApplication.ExecuteMenuItem("Edit/Duplicate");

        // The duplicated objects are now the selection
        Transform[] duplicates = Selection.transforms;
        if (duplicates == null || duplicates.Length == 0) return;

        // Swap drag targets to the duplicates
        dragTargets = duplicates;
        SnapshotTransforms();
        selectionPivot = ComputePivot();

        // Re-init move start for the duplicates
        // (they start at the same positions, so moveHitStart is still valid)
    }

    static void ResetState()
    {
        phase = Phase.Idle;
        activeMode = Mode.None;
        dragTargets = null;
        startPositions = null;
        startRotations = null;
        startScales = null;
        scaleAxisChosen = false;
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

    static void DrawFeedback()
    {
        if (dragTargets == null) return;

        switch (activeMode)
        {
            case Mode.Rotate:
                Handles.color = new Color(0.2f, 0.9f, 0.2f, 0.4f);
                Handles.DrawWireDisc(selectionPivot, UpAxis, 1.5f);
                break;

            case Mode.Scale:
                Handles.color = scaleAxisIndex == 0
                    ? new Color(1f, 0.2f, 0.2f, 0.8f)    // red = X
                    : new Color(0.2f, 0.2f, 1f, 0.8f);   // blue = Z
                foreach (var t in dragTargets)
                {
                    if (t == null) continue;
                    Vector3 axis = scaleAxisIndex == 0 ? t.right : t.forward;
                    Handles.DrawLine(t.position - axis * 3f, t.position + axis * 3f);
                }
                break;
        }
    }
}
