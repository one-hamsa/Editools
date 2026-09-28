#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

static partial class GPEdit
{
    static GreyPrimitive s_tooltipPrimitive;
    static void CollectEditablePrimitives(GreyPrimitive primitive, List<GreyPrimitive> targets, HashSet<int> visited)
    {
        // Empty Boolean inputs and non-primitive selections are valid.
        if (primitive == null || !visited.Add(primitive.GetInstanceID())) return;
        if (primitive is GreyBooleanResult result)
        {
            CollectEditablePrimitives(result.Subject, targets, visited);
            CollectEditablePrimitives(result.Operator, targets, visited);
        }
        else if (primitive is GreyboxCompound compound)
            foreach (var part in compound.Parts) CollectEditablePrimitives(part, targets, visited);
        else targets.Add(primitive);
    }

    static GPSpline ProbeSpline(GreyPrimitive primitive)
    {
        if (primitive is Greypipe pipe) { s_pipeAdapter.Set(pipe); return s_pipeAdapter; }
        if (primitive is Greyroad road) { s_roadAdapter.Set(road); return s_roadAdapter; }
        return null;
    }

    static void OnSelectionSceneGUI(SceneView sv, Event e)
    {
        using var targetsScope = ListPool<GreyPrimitive>.Get(out var targets);
        using var visitedScope = HashSetPool<int>.Get(out var visited);
        foreach (var go in Selection.gameObjects)
            CollectEditablePrimitives(go.GetComponent<GreyPrimitive>(), targets, visited);

        GreyPrimitive active = null;
        if (s_gbDrag != GbDrag.None)
        {
            active = s_gbTarget;
            HandleGreyboxDrag(e, sv, SelectionTintIndex(active, targets));
        }
        else if (s_spDrag != SpDrag.None)
        {
            active = s_sp.Obj as GreyPrimitive;
            HandleSplineDrag(e, sv, s_sp);
        }
        else
        {
            float best = float.MaxValue;
            foreach (var primitive in targets)
            {
                float score = SelectionHoverScore(primitive, e.mousePosition);
                if (score < best) { best = score; active = primitive; }
            }
            s_gbHoverTint = SelectionTintIndex(active, targets);
            if (active is Greybox box) OnGreyboxSceneGUI(sv, e, box);
            else if (active is Greypipe pipe) OnGreypipeSceneGUI(sv, e, pipe);
            else if (active is Greyroad road) OnGreyroadSceneGUI(sv, e, road);
        }

        if (e.type == EventType.Repaint)
            foreach (var primitive in targets)
            {
                if (primitive == active) continue;
                if (primitive is Greybox box)
                {
                    box.GetWorldCorners(s_gbWc);
                    DrawGreybox(box, -1, -1, SelectionTintIndex(box, targets));
                }
                else
                {
                    var spline = ProbeSpline(primitive);
                    if (spline != null) DrawSpline(spline, -1, -1, 0, -1, 0, false, default);
                }
            }
        s_tooltipPrimitive = active;
        if (e.type == EventType.MouseMove || e.type == EventType.KeyDown || e.type == EventType.KeyUp) sv.Repaint();
    }

    static int SelectionTintIndex(GreyPrimitive primitive, List<GreyPrimitive> targets)
        => primitive != null && (targets.Count > 1 || primitive.transform.GetComponentInParent<GreyBooleanResult>() != null)
            ? targets.IndexOf(primitive) : -1;

    static float SelectionHoverScore(GreyPrimitive primitive, Vector2 mouse)
    {
        if (primitive is Greybox box)
        {
            box.GetWorldCorners(s_gbWc);
            int face = HitGreyboxFaceHandle(box, mouse);
            if (face >= 0) return Vector2.Distance(mouse, HandleUtility.WorldToGUIPoint(GreyboxFaceCenter(face)));
            int edge = HitGreyboxOutlineEdge(box, mouse);
            if (edge >= 0)
            {
                var corners = GPEditShared.EdgeCornerIndices[edge];
                return GPEditShared.HandlePx + GPEditShared.DistToSegment(s_gbWc[corners[0]], s_gbWc[corners[1]], mouse);
            }
            return float.MaxValue;
        }
        var spline = ProbeSpline(primitive);
        if (spline == null) return float.MaxValue;
        int vertex = HitSplineVertex(spline, mouse);
        if (vertex >= 0) return Vector2.Distance(mouse, HandleUtility.WorldToGUIPoint(spline.GetWorldVertexPos(vertex)));
        HitSplineBezier(spline, mouse, out vertex, out int side);
        if (vertex >= 0) return Vector2.Distance(mouse, HandleUtility.WorldToGUIPoint(spline.GetHandleWorldPosition(vertex, side)));
        if (spline.HasBanking)
        {
            HitSplineBanking(spline, mouse, out vertex, out side);
            if (vertex >= 0) return Vector2.Distance(mouse, HandleUtility.WorldToGUIPoint(spline.GetBankingHandleWorld(vertex, side)));
        }
        if (HitSplineSegment(spline, mouse, out int segment, out float t))
            return GPEditShared.HandlePx + Vector2.Distance(mouse, HandleUtility.WorldToGUIPoint(spline.EvaluateSplineWorld(segment, t)));
        return float.MaxValue;
    }
}
#endif
