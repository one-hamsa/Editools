#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

static partial class GPEdit
{
    struct EditCoordinates
    {
        public Vector3 a, b, alternateA, alternateB;

        public void Axes(bool alternate, out Vector3 first, out Vector3 second)
        {
            first = (alternate ? alternateA : a).normalized;
            second = (alternate ? alternateB : b).normalized;
        }

        public static EditCoordinates Surface(Vector3 a, Vector3 b, Vector3 normal)
            => new EditCoordinates { a = a, b = b, alternateA = normal };

        public static EditCoordinates Face(Vector3 normal, Vector3 a, Vector3 b)
            => new EditCoordinates { a = normal, alternateA = a, alternateB = b };
    }

    struct CoordinateDrag
    {
        public EditCoordinates global, local;
        public Vector3 anchor, delta;
        public int lockAxis;
        GPEditShared.PlaneDragFrame frame;
        Vector3 offset;
        Vector2 press, lastMouse;
        bool alternate, localMode;

        public void Begin(Vector3 point, Vector2 mouse, EditCoordinates globalCoordinates, EditCoordinates localCoordinates)
        {
            global = globalCoordinates;
            local = localCoordinates;
            anchor = point;
            delta = offset = Vector3.zero;
            lastMouse = mouse;
            Capture(mouse);
        }

        void Capture(Vector2 mouse)
        {
            alternate = Event.current.control;
            localMode = Tools.pivotRotation == PivotRotation.Local;
            (localMode ? local : global).Axes(alternate, out var a, out var b);
            frame = GPEditShared.PlaneDragFrame.Capture(anchor + offset, a, b);
            press = mouse;
            lockAxis = -1;
        }

        public Vector3 Update(Vector2 mouse)
        {
            if (alternate != Event.current.control || localMode != (Tools.pivotRotation == PivotRotation.Local))
            {
                offset = delta;
                Capture(lastMouse);
            }
            var movement = mouse - press;
            lockAxis = DominantCoordinateAxis(frame, movement, Event.current.shift, lockAxis);
            frame.Solve(movement, lockAxis, out float a, out float b, out _, out _);
            delta = offset + frame.WorldDelta(a, b);
            lastMouse = mouse;
            return delta;
        }

        public void Draw(Vector3 point)
        {
            bool sameMode = alternate == Event.current.control && localMode == (Tools.pivotRotation == PivotRotation.Local);
            var coordinates = Tools.pivotRotation == PivotRotation.Local ? local : global;
            coordinates.Axes(Event.current.control, out var a, out var b);
            DrawCoordinates(point, a, b, sameMode && Event.current.shift ? lockAxis : -1);
        }
    }

    static int DominantCoordinateAxis(GPEditShared.PlaneDragFrame frame, Vector2 movement, bool shift, int previous)
    {
        if (!shift || frame.axisB.sqrMagnitude < 0.0001f) return -1;
        frame.Solve(movement, -1, out _, out _, out float pxA, out float pxB);
        if (Mathf.Max(Mathf.Abs(pxA), Mathf.Abs(pxB)) < 2f) return previous;
        int dominant = Mathf.Abs(pxA) >= Mathf.Abs(pxB) ? 0 : 1;
        if (previous < 0) return dominant;
        float kept = previous == 0 ? Mathf.Abs(pxA) : Mathf.Abs(pxB);
        float other = previous == 0 ? Mathf.Abs(pxB) : Mathf.Abs(pxA);
        return other > kept * 1.3f ? dominant : previous;
    }

    static void DrawCoordinates(Vector3 point, EditCoordinates coordinates)
    {
        coordinates.Axes(Event.current.control, out var a, out var b);
        DrawCoordinates(point, a, b, -1);
    }

    static void DrawCoordinates(Vector3 point, Vector3 a, Vector3 b, int lockAxis)
    {
        var previousZ = Handles.zTest;
        var previousColor = Handles.color;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        float size = HandleUtility.GetHandleSize(point);
        DrawCoordinateArrow(point, a, size, lockAxis == 0, lockAxis == 1);
        if (b.sqrMagnitude > 0.0001f) DrawCoordinateArrow(point, b, size, lockAxis == 1, lockAxis == 0);
        Handles.color = previousColor;
        Handles.zTest = previousZ;
    }

    static void DrawCoordinateArrow(Vector3 point, Vector3 axis, float size, bool bold, bool faded)
    {
        if (axis.sqrMagnitude < 0.0001f) return;
        axis.Normalize();
        Color color = GPEditShared.Manipulate;
        if (faded) color.a *= 0.12f;
        Handles.color = color;
        Vector3 tip = point + axis * (size * 0.38f);
        Handles.DrawLine(point, tip, bold ? 4f : 2f);
        Handles.ConeHandleCap(0, tip, Quaternion.LookRotation(axis), size * (bold ? 0.09f : 0.065f), EventType.Repaint);
    }

    static EditCoordinates GreyboxFaceCoordinates(Greybox box, int face, bool local)
    {
        int axis = face / 2;
        Vector3 normal = GreyboxAxisWorldDir(box.transform, axis) * (face % 2 == 0 ? 1f : -1f);
        Vector3 a = GreyboxAxisWorldDir(box.transform, (axis + 1) % 3);
        Vector3 b = GreyboxAxisWorldDir(box.transform, (axis + 2) % 3);
        if (local)
        {
            var corners = GPEditShared.FaceCornerIndices[face];
            Vector3 surfaceA = (s_gbWc[corners[1]] - s_gbWc[corners[0]]) + (s_gbWc[corners[2]] - s_gbWc[corners[3]]);
            Vector3 surfaceB = (s_gbWc[corners[3]] - s_gbWc[corners[0]]) + (s_gbWc[corners[2]] - s_gbWc[corners[1]]);
            surfaceA.Normalize();
            surfaceB.Normalize();
            if (Vector3.Cross(surfaceA, surfaceB).sqrMagnitude > 0.000001f)
            {
                a = surfaceA.normalized;
                b = surfaceB.normalized;
                normal = Vector3.Cross(a, b).normalized;
                Vector3 center = Vector3.zero;
                foreach (var corner in s_gbWc) center += corner;
                if (Vector3.Dot(normal, GreyboxFaceCenter(face) - center / 8f) < 0f) normal = -normal;
            }
        }
        return EditCoordinates.Face(normal, a, b);
    }

    static EditCoordinates GreyboxEdgeCoordinates(Greybox box, int edge, bool local)
    {
        int axis = edge / 4;
        Vector3 a = GreyboxAxisWorldDir(box.transform, (axis + 1) % 3);
        Vector3 b = GreyboxAxisWorldDir(box.transform, (axis + 2) % 3);
        Vector3 along = GreyboxAxisWorldDir(box.transform, axis);
        if (local)
        {
            var faces = GPEditShared.EdgeFaceAdjacency[edge];
            Vector3 first = GreyboxFaceCoordinates(box, faces[0], true).a;
            Vector3 second = GreyboxFaceCoordinates(box, faces[1], true).a;
            if (Vector3.Cross(first, second).sqrMagnitude > 0.000001f)
            {
                a = first;
                b = second;
                along = Vector3.Cross(a, b).normalized;
                var corners = GPEditShared.EdgeCornerIndices[edge];
                if (Vector3.Dot(along, s_gbWc[corners[1]] - s_gbWc[corners[0]]) < 0f) along = -along;
            }
        }
        return EditCoordinates.Surface(a, b, along);
    }

    static EditCoordinates SplineCoordinates(GPSpline spline, int vertex, bool local)
    {
        if (!local) return EditCoordinates.Surface(Vector3.right, Vector3.forward, Vector3.up);
        Vector3 a, b, normal;
        if (spline.HasBanking)
        {
            a = spline.GetHandleWorldPosition(vertex, 1) - spline.GetWorldVertexPos(vertex);
            spline.ComputeBankingAxis(vertex, out _, out b);
            if (a.sqrMagnitude < 0.000000000001f) a = spline.MainAxisWorld;
            a.Normalize();
            normal = Vector3.Cross(a, b).normalized;
        }
        else
        {
            normal = spline.MovePlaneNormal.normalized;
            a = Vector3.ProjectOnPlane(spline.MainAxisWorld, normal).normalized;
            b = Vector3.Cross(normal, a).normalized;
        }
        if (normal.sqrMagnitude < 0.000001f || a.sqrMagnitude < 0.000001f || b.sqrMagnitude < 0.000001f)
            return EditCoordinates.Surface(Vector3.right, Vector3.forward, Vector3.up);
        return EditCoordinates.Surface(a, b, normal);
    }
}
#endif
