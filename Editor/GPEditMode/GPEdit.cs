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
///   LMB = manipulate · Shift = alternative action · MMB = remove/reset · RMB = add/create · Ctrl = isolate
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
    /// Alt+~ toggles Edit Mode while the Scene View is focused and a single Grey Primitive is
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
            SceneView.duringSceneGui += OnSceneGUI;
    }

    /// <summary>Called by each GP overlay instance on destruction. Unhooks when the last one goes.</summary>
    internal static void Unsubscribe()
    {
        if (s_subscribers > 0 && --s_subscribers == 0)
            SceneView.duringSceneGui -= OnSceneGUI;
    }

    // ─── Selection helpers ──────────────────────────────────────

    /// <summary>The single selected Grey Primitive, or null when the selection isn't exactly one GP.</summary>
    internal static GreyPrimitive SelectedPrimitive
    {
        get
        {
            if (Selection.count != 1) return null;
            var go = Selection.activeGameObject;
            return go != null ? go.GetComponent<GreyPrimitive>() : null;
        }
    }

    // ─── Main loop ──────────────────────────────────────────────

    static void OnSceneGUI(SceneView sv)
    {
        Event e = Event.current;
        var gp = SelectedPrimitive;

        if (!Enabled || gp == null) return;

        switch (gp)
        {
            case Greybox gb:   OnGreyboxSceneGUI(sv, e, gb);   break;
            case Greypipe pipe: OnGreypipeSceneGUI(sv, e, pipe); break;
            case Greyroad road: OnGreyroadSceneGUI(sv, e, road); break;
        }

        if (ShowTooltips && e.type == EventType.Repaint)
            DrawTooltip(sv, gp);
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
