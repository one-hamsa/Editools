#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

/// <summary>
/// Grey Primitive Edit Mode — a continuous, always-on editing layer for the selected Grey
/// Primitive (Greybox / Greypipe / Greyroad). When enabled, it draws the type-specific gizmos
/// (Greybox outline + face handles, spline vertices + bezier/banking handles) and routes mouse
/// input straight to the primitive's substructure, with no mode-key hold required.
///
/// This is the home for everything QuickTransform used to special-case for Grey Primitives.
/// QuickTransform keeps only generic whole-object transforms; while Edit Mode is on, QT still
/// works on the selection's transform — the two coexist because Edit Mode only consumes events
/// when the cursor is over one of its sub-elements, letting everything else fall through to QT.
///
/// Control grammar (shared across all three types):
///   LMB = manipulate · Ctrl = alternative coordinates · MMB = remove/reset · RMB = add/create · Shift = axis lock; Alt = special action
///
/// The mode toggle and the tooltip flag are SessionState-backed, so they persist across
/// selection changes for the whole editor session and reset on restart. The hook into
/// SceneView.duringSceneGui is owned by <see cref="GPWindowOverlay"/> (Subscribe/Unsubscribe),
/// so there is no InitializeOnLoad side effect.
/// </summary>
static partial class GPEdit
{
    // ─── Session state ──────────────────────────────────────────

    const string k_EnabledKey  = "GPEdit_Enabled";
    const string k_TooltipsKey = "GPEdit_Tooltips";

    /// <summary>Session-wide Edit Mode toggle. Persists across GP selections, resets on editor restart.</summary>
    internal static bool Enabled
    {
        get => SessionState.GetBool(k_EnabledKey, false);
        set
        {
            if (value == Enabled) return;
            SessionState.SetBool(k_EnabledKey, value);
            CancelPrimitiveClick();
            if (value)
            {
                Tools.current = Tool.None;
                GreyboxSeamSolver.RequestLinkedSelectionExpansion();
            }
            onStateChanged?.Invoke();
            SceneView.RepaintAll();
        }
    }

    /// <summary>Show the simplified Scene View tooltip while in Edit Mode.</summary>
    internal static bool ShowTooltips
    {
        get => SessionState.GetBool(k_TooltipsKey, true);
        set
        {
            if (value == ShowTooltips) return;
            SessionState.SetBool(k_TooltipsKey, value);
            onStateChanged?.Invoke();
            SceneView.RepaintAll();
        }
    }

    /// <summary>Fired when Enabled / ShowTooltips change, so UI (the overlay) can resync. Consumers own the wiring.</summary>
    internal static event Action onStateChanged;

    // ─── Hotkey ─────────────────────────────────────────────────

    /// <summary>
    /// Alt+~ toggles Edit Mode while the Scene View is focused and at least one Grey Primitive is
    /// selected. Routed through ShortcutManager (not IMGUI/UI Toolkit key events) so it fires once
    /// at the editor level — IMGUIContainers swallow raw key events, and Tab fought the focus ring.
    /// Rebindable in Edit ▸ Shortcuts.
    /// </summary>
    [Shortcut("Editools/Grey Primitive/Toggle Edit Mode", typeof(SceneView), KeyCode.BackQuote, ShortcutModifiers.Alt)]
    static void ToggleEditModeShortcut()
    {
        if (SelectedPrimitive != null)
            Enabled = !Enabled;
    }

    // ─── Hook lifecycle (owned by GPWindowOverlay) ──────────────

    static int s_subscribers;

    /// <summary>Called by each GP overlay instance on creation. Hooks duringSceneGui once.</summary>
    internal static void Subscribe()
    {
        if (s_subscribers++ == 0)
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += CancelPrimitiveClick;
            Undo.undoRedoPerformed += CancelPrimitiveClick;
        }
    }

    /// <summary>Called by each GP overlay instance on destruction. Unhooks when the last one goes.</summary>
    internal static void Unsubscribe()
    {
        if (s_subscribers > 0 && --s_subscribers == 0)
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= CancelPrimitiveClick;
            Undo.undoRedoPerformed -= CancelPrimitiveClick;
            CancelPrimitiveClick();
        }
    }

    // ─── Selection helpers ──────────────────────────────────────

    /// <summary>The active selected primitive, falling back to another selected primitive.</summary>
    internal static GreyPrimitive SelectedPrimitive
    {
        get
        {
            var go = Selection.activeGameObject;
            if (go != null && go.TryGetComponent<GreyPrimitive>(out var active)) return active;
            foreach (var selected in Selection.gameObjects)
                if (selected.TryGetComponent<GreyPrimitive>(out var primitive)) return primitive;
            return null;
        }
    }

    // Click actions commit on release; drag actions own their input separately.

    enum ClickAction { SplitEdge, ToggleFace, InsertVertex, DeleteVertex, ResetHandle, ResetBanking }

    struct PrimitiveClick
    {
        public GreyPrimitive target;
        public SceneView view;
        public ClickAction action;
        public int button;
        public int element;
        public float fraction;
        public int vertexCount;
        public Vector2 mouse;
        public Matrix4x4 cameraMatrix;
        public Matrix4x4 projectionMatrix;
    }

    static PrimitiveClick s_click;

    static void BeginPrimitiveClick(SceneView view, Event e, GreyPrimitive target, ClickAction action, int element,
        float fraction = 0f, int vertexCount = 0)
    {
        // Observe the click without capturing input needed by Scene View camera navigation.
        s_click = new PrimitiveClick
        {
            target = target,
            view = view,
            action = action,
            button = e.button,
            element = element,
            fraction = fraction,
            vertexCount = vertexCount,
            mouse = e.mousePosition,
            cameraMatrix = view.camera.worldToCameraMatrix,
            projectionMatrix = view.camera.projectionMatrix,
        };
    }

    static void CancelPrimitiveClick() => s_click = default;

    static bool HandlePrimitiveClick(SceneView view, Event e)
    {
        var click = s_click;
        // A pending target may have been deleted or its Scene View closed.
        if (click.target == null || click.view == null || EditorWindow.focusedWindow != click.view)
        {
            CancelPrimitiveClick();
            return false;
        }
        if (view != click.view) return false;

        // Camera controls can consume an event before this callback sees it.
        EventType type = e.rawType;
        if (type == EventType.MouseDrag || type == EventType.MouseMove || type == EventType.MouseLeaveWindow
            || type == EventType.MouseDown || type == EventType.KeyDown || type == EventType.ScrollWheel
            || type == EventType.Ignore || e.alt
            || view.camera.worldToCameraMatrix != click.cameraMatrix
            || view.camera.projectionMatrix != click.projectionMatrix)
        {
            CancelPrimitiveClick();
            return false;
        }
        if (type != EventType.MouseUp) return false;

        CancelPrimitiveClick();
        if (e.button != click.button || e.type == EventType.Ignore || EditorWindow.mouseOverWindow != view
            || (e.mousePosition - click.mouse).sqrMagnitude > 9f) return false;

        switch (click.action)
        {
            case ClickAction.SplitEdge: SplitGreybox((Greybox)click.target, click.element, click.fraction); break;
            case ClickAction.ToggleFace: ToggleGreyboxFace((Greybox)click.target, click.element); break;
            default: if (!CommitSplineClick(click)) return false; break;
        }
        if (e.type != EventType.Used) e.Use();
        return true;
    }

    // ─── Main loop ──────────────────────────────────────────────

    static void OnSceneGUI(SceneView sv)
    {
        Event e = Event.current;
        var gp = SelectedPrimitive;

        if (!Enabled || gp == null)
        {
            CancelPrimitiveClick();
            return;
        }

        // While an object is being placed (post-create snap or Snap To Surface), stand down — otherwise
        // a face/edge under the cursor would swallow the LMB that confirms the placement.
        if (SnapToSurface.IsSnapping)
        {
            CancelPrimitiveClick();
            return;
        }

        if (HandlePrimitiveClick(sv, e)) return;

        OnSelectionSceneGUI(sv, e);

        if (ShowTooltips && e.type == EventType.Repaint)
            DrawTooltip(sv, s_tooltipPrimitive != null ? s_tooltipPrimitive : gp);
    }

    // ─── Per-type entry points ──────────────────────────────────
    // Implemented in GPEditGreybox.cs and GPEditSpline.cs. Unimplemented partials are no-ops,
    // so the scaffolding compiles and runs before the type behaviors land.

    static partial void OnGreyboxSceneGUI(SceneView sv, Event e, Greybox gb);
    static partial void OnGreypipeSceneGUI(SceneView sv, Event e, Greypipe pipe);
    static partial void OnGreyroadSceneGUI(SceneView sv, Event e, Greyroad road);

    // Implemented in GPEditTooltip.cs.
    static partial void DrawTooltip(SceneView sv, GreyPrimitive gp);
}
#endif
