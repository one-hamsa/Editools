#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Self-contained scene-view primitives for Grey Primitive Edit Mode: screen-space hover math,
/// ray/plane helpers, the box corner/edge/face lookup tables (matching the Greybox corner
/// convention documented on <see cref="Greybox"/>), and the shared edit gizmo colors.
///
/// Kept independent of QuickTransform so Edit Mode owns its own behavior — the two tools share
/// patterns, not code.
/// </summary>
static class GPEditShared
{
    // ─── Hover thresholds (screen px) ───────────────────────────

    public const float HoverPx  = 10f;  // edges / spline / vertices
    public const float HandlePx = 18f;  // face-center dot handles

    // ─── Edit gizmo colors ──────────────────────────────────────

    public static readonly Color Outline       = new Color(1f, 1f, 1f, 0.6f);
    public static readonly Color OutlineHover   = new Color(1f, 0.8f, 0.2f, 0.95f);
    public static readonly Color HandleActive   = new Color(1f, 1f, 1f, 0.8f);
    public static readonly Color HandleInactive = new Color(1f, 0.3f, 0.3f, 0.7f);
    public static readonly Color HandleHover    = new Color(0.3f, 0.7f, 1f, 1f);
    public static readonly Color Spline         = new Color(0.4f, 0.85f, 1f, 0.9f);
    public static readonly Color Vertex         = new Color(1f, 1f, 1f, 0.85f);
    public static readonly Color VertexHover    = new Color(0.3f, 0.7f, 1f, 1f);
    public static readonly Color Bezier         = new Color(1f, 0.7f, 0.2f, 0.9f);
    public static readonly Color Banking        = new Color(0.7f, 0.4f, 1f, 0.95f);
    public static readonly Color DragPlane      = new Color(1f, 0.9f, 0.3f, 0.12f);
    public static readonly Color NewVertex      = new Color(0.4f, 1f, 0.5f, 0.95f);

    // ─── Box lookup tables ──────────────────────────────────────
    // Corner index bits: bit0 = +X, bit1 = +Y, bit2 = +Z (Greybox convention).

    /// <summary>Corner indices per face (winding order). Face index: 0=+X,1=-X,2=+Y,3=-Y,4=+Z,5=-Z.</summary>
    public static readonly int[][] FaceCornerIndices =
    {
        new[] { 1, 3, 7, 5 }, // 0 +X
        new[] { 0, 4, 6, 2 }, // 1 -X
        new[] { 2, 6, 7, 3 }, // 2 +Y
        new[] { 0, 1, 5, 4 }, // 3 -Y
        new[] { 4, 5, 7, 6 }, // 4 +Z
        new[] { 0, 2, 3, 1 }, // 5 -Z
    };

    /// <summary>Corner pairs per edge. Edges 0-3 run along axis 0, 4-7 axis 1, 8-11 axis 2.</summary>
    public static readonly int[][] EdgeCornerIndices =
    {
        new[] {0,1}, new[] {2,3}, new[] {4,5}, new[] {6,7}, // axis 0 (X)
        new[] {0,2}, new[] {1,3}, new[] {4,6}, new[] {5,7}, // axis 1 (Y)
        new[] {0,4}, new[] {1,5}, new[] {2,6}, new[] {3,7}, // axis 2 (Z)
    };

    /// <summary>The two face indices adjacent to each edge (index matches EdgeCornerIndices).</summary>
    public static readonly int[][] EdgeFaceAdjacency =
    {
        new[] {3, 5}, new[] {2, 5}, new[] {3, 4}, new[] {2, 4}, // axis 0 edges
        new[] {1, 5}, new[] {0, 5}, new[] {1, 4}, new[] {0, 4}, // axis 1 edges
        new[] {1, 3}, new[] {0, 3}, new[] {1, 2}, new[] {0, 2}, // axis 2 edges
    };

    // ─── Ray / plane math ───────────────────────────────────────

    /// <summary>
    /// Two-sided ray-plane intersection from a screen point. Unlike Plane.Raycast it accepts a
    /// negative t, so dragging keeps working when the camera is on the plane's back side.
    /// </summary>
    public static bool RaycastPlane(Vector2 mousePos, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit)
    {
        hit = Vector3.zero;
        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);
        float denom = Vector3.Dot(ray.direction, planeNormal);
        if (Mathf.Abs(denom) < 1e-6f) return false;
        float enter = Vector3.Dot(planePoint - ray.origin, planeNormal) / denom;
        hit = ray.origin + ray.direction * enter;
        return true;
    }

    /// <summary>Signed distance along (lineOrigin, lineDir) of the point nearest to the ray.</summary>
    public static float ProjectRayOntoLine(Ray ray, Vector3 lineOrigin, Vector3 lineDir)
    {
        Vector3 w = ray.origin - lineOrigin;
        float a = Vector3.Dot(ray.direction, ray.direction);
        float b = Vector3.Dot(ray.direction, lineDir);
        float d = Vector3.Dot(ray.direction, w);
        float ee = Vector3.Dot(lineDir, w);
        float denom = a - b * b;
        if (denom < 0.0001f) return ee;
        return (ee * a - b * d) / denom;
    }

    /// <summary>Screen angle (degrees) from a world pivot to a screen point.</summary>
    public static float ScreenAngleFrom(Vector2 mousePos, Vector3 worldPivot)
    {
        Vector2 pivotScreen = HandleUtility.WorldToGUIPoint(worldPivot);
        Vector2 dir = mousePos - pivotScreen;
        return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
    }

    /// <summary>Screen-space distance from a point to a world-space segment.</summary>
    public static float DistToSegment(Vector3 segA, Vector3 segB, Vector2 screenPos)
    {
        Vector2 a = HandleUtility.WorldToGUIPoint(segA);
        Vector2 b = HandleUtility.WorldToGUIPoint(segB);
        return DistPointToSegment2D(screenPos, a, b);
    }

    public static float DistPointToSegment2D(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.0001f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Closest point on a world segment to a screen position (projected in screen space).</summary>
    public static Vector3 ClosestPointOnSegmentToScreenPos(Vector3 segA, Vector3 segB, Vector2 screenPos)
    {
        Vector2 sa = HandleUtility.WorldToGUIPoint(segA);
        Vector2 sb = HandleUtility.WorldToGUIPoint(segB);
        Vector2 ab = sb - sa;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.0001f) return (segA + segB) * 0.5f;
        float t = Mathf.Clamp01(Vector2.Dot(screenPos - sa, ab) / lenSq);
        return Vector3.Lerp(segA, segB, t);
    }

    // ─── Handle drawing ─────────────────────────────────────────

    /// <summary>Always-on-top dot handle at a world point; size scales with handle distance.</summary>
    public static void DrawDot(Vector3 world, Color color, float scale = 0.05f)
    {
        var prevZ = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        Handles.color = color;
        Handles.DotHandleCap(0, world, Quaternion.identity, HandleUtility.GetHandleSize(world) * scale, EventType.Repaint);
        Handles.zTest = prevZ;
    }
}
#endif
