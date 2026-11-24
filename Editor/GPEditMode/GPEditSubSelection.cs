#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sub-object selection for Grey Primitive Edit Mode. Box faces and edges, spline vertices with
/// their Bezier and banking handles, and ledge faces can be selected: an LMB click selects one,
/// Ctrl + click adds or removes one of the same kind, and a plain LMB click on nothing clears the
/// selection. The selection spans every selected primitive and holds one kind at a time.
///
/// An action started on a selected element (LMB drag, RMB drag, RMB / MMB click) runs on every
/// selected element; started on an unselected one it runs on that element alone and leaves the
/// selection as it is. A left press only becomes a drag after the cursor moves
/// <see cref="GPEditShared.DragStartPx"/> pixels; released before that it is the selection click.
/// </summary>
static partial class GPEdit
{
    enum SubKind { None, BoxFace, BoxEdge, SplineVertex, SplineBezier, SplineBanking, LedgeFace }

    struct SubElement
    {
        public GreyPrimitive target;
        public SubKind kind;
        public int index; // face, edge or vertex index on the target

        public bool Matches(GreyPrimitive otherTarget, SubKind otherKind, int otherIndex)
            => kind == otherKind && index == otherIndex && ReferenceEquals(target, otherTarget);
    }

    /// <summary>A left press on a selectable element that is not yet known to be a click or a drag.</summary>
    struct SubPress
    {
        public bool active;
        public SubElement element;
        public int side;      // Bezier / banking: which of the vertex's two handles was pressed
        public bool reshape;  // Alt was held: a spline endpoint drag reshapes the interior vertices
        public Vector2 mouse;
        public int control;
    }

    static readonly List<SubElement> s_subSelection = new List<SubElement>(16);
    static SubPress s_subPress;

    // ─── Selection set ──────────────────────────────────────────

    static bool IsSubSelected(GreyPrimitive target, SubKind kind, int index)
    {
        foreach (var element in s_subSelection)
            if (element.Matches(target, kind, index)) return true;
        return false;
    }

    internal static void ClearSubSelection()
    {
        if (s_subSelection.Count == 0) return;
        s_subSelection.Clear();
        SceneView.RepaintAll();
    }

    /// <summary>Drops the entries of primitives that are no longer shown for editing (deselected or deleted).</summary>
    static void PruneSubSelection(List<GreyPrimitive> targets)
    {
        for (int i = s_subSelection.Count - 1; i >= 0; i--)
        {
            var target = s_subSelection[i].target;
            if (target == null || !targets.Contains(target)) s_subSelection.RemoveAt(i);
        }
    }

    /// <summary>
    /// Selection click: a plain click makes the element the whole selection; Ctrl adds it to, or
    /// removes it from, a selection of the same kind and does nothing for another kind.
    /// </summary>
    static void ClickSubSelect(SubElement element, bool additive)
    {
        if (!additive)
        {
            s_subSelection.Clear();
            s_subSelection.Add(element);
            return;
        }
        for (int i = 0; i < s_subSelection.Count; i++)
            if (s_subSelection[i].Matches(element.target, element.kind, element.index))
            {
                s_subSelection.RemoveAt(i);
                return;
            }
        if (s_subSelection.Count == 0 || s_subSelection[0].kind == element.kind) s_subSelection.Add(element);
    }

    // ─── Left press: click or drag ──────────────────────────────

    static void BeginSubPress(Event e, GreyPrimitive target, SubKind kind, int index, int side = 0)
    {
        s_subPress = new SubPress
        {
            active = true,
            element = new SubElement { target = target, kind = kind, index = index },
            side = side,
            reshape = e.alt,
            mouse = e.mousePosition,
            control = GUIUtility.GetControlID(FocusType.Passive),
        };
        HandleUtility.AddDefaultControl(s_subPress.control);
        GUIUtility.hotControl = s_subPress.control;
        e.Use();
    }

    static void CancelSubPress()
    {
        if (s_subPress.active) GUIUtility.hotControl = 0;
        s_subPress = default;
    }

    /// <summary>
    /// Resolves the pending left press. A release is the selection click. Movement past the
    /// threshold starts the element's drag and leaves this event unused, so the drag handler
    /// that runs next applies the movement.
    /// </summary>
    static void HandleSubPress(Event e, SceneView sv)
    {
        var press = s_subPress;
        // The pressed primitive can be deleted, and Edit Mode switched off, under the press.
        if (press.element.target == null || !Enabled)
        {
            CancelSubPress();
            return;
        }

        // Events outside the window arrive as Ignore with the real type in rawType.
        EventType type = e.type == EventType.Ignore ? e.rawType : e.type;
        if (type == EventType.MouseUp && e.button == 0)
        {
            CancelSubPress();
            ClickSubSelect(press.element, e.control);
            sv.Repaint();
            e.Use();
        }
        else if (type == EventType.MouseDrag && e.button == 0)
        {
            if ((e.mousePosition - press.mouse).magnitude < GPEditShared.DragStartPx)
            {
                e.Use();
                return;
            }
            s_subPress = default;
            StartSubDrag(press, e);
            // Nothing to drag (stale index): release the control the press was holding.
            if (s_gbDrag == GbDrag.None && s_spDrag == SpDrag.None && s_ldDrag == LdDrag.None) GUIUtility.hotControl = 0;
        }
        else if (type == EventType.MouseDown)
            e.Use(); // another button during the press starts nothing
    }

    /// <summary>Starts the drag the pressed element had before selection existed, on the control the press holds.</summary>
    static void StartSubDrag(SubPress press, Event e)
    {
        var target = press.element.target;
        int index = press.element.index;
        switch (press.element.kind)
        {
            case SubKind.BoxFace:
            case SubKind.BoxEdge:
            {
                var box = (Greybox)target;
                box.GetWorldCorners(s_gbWc);
                StartGreyboxMove(box, press.element.kind == SubKind.BoxFace ? GbDrag.FaceNormal : GbDrag.Edge, index, press.mouse);
                s_gbTarget = box;
                s_gbButton = 0;
                s_gbControlId = press.control;
                break;
            }
            case SubKind.SplineVertex:
            case SubKind.SplineBezier:
            case SubKind.SplineBanking:
            {
                var sp = CreateSplineBinding(target);
                // The vertex can be gone after an undo during the press.
                if (sp == null || index >= sp.Count) return;
                if (press.element.kind == SubKind.SplineVertex) StartSplineVertexMove(sp, index, press.mouse, press.reshape);
                else if (press.element.kind == SubKind.SplineBezier) StartSplineBezier(sp, index, press.side, press.mouse);
                else StartSplineBanking(sp, index, press.side, press.mouse);
                s_sp = sp;
                s_spButton = 0;
                s_spControl = press.control;
                break;
            }
            case SubKind.LedgeFace:
            {
                var ledge = (Greyroad)target;
                // Legitimate filter: the ledge was unlinked or lost its source during the press.
                if (!ledge.IsLedge) return;
                ledge.GetPathFramesWorld(s_ldFrames);
                if (s_ldFrames.Count < 2) return;
                BeginLedgeDrag(ledge, false, index, press.mouse, e.control);
                s_ldButton = 0;
                s_ldControl = press.control;
                break;
            }
        }
    }
}
#endif
