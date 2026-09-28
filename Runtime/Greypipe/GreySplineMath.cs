using UnityEngine;

internal static class GreySplineMath
{
    public static void FitInsertedHandle(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t,
        out Vector3 point, out Vector3 direction, out float length)
    {
        Vector3 leftHandle = Vector3.Lerp(p0, p1, t);
        Vector3 middle = Vector3.Lerp(p1, p2, t);
        Vector3 rightHandle = Vector3.Lerp(p2, p3, t);
        Vector3 incoming = Vector3.Lerp(leftHandle, middle, t);
        Vector3 outgoing = Vector3.Lerp(middle, rightHandle, t);
        point = Vector3.Lerp(incoming, outgoing, t);
        direction = outgoing - incoming;
        if (direction.sqrMagnitude < 1e-8f) direction = p3 - p0;
        if (direction.sqrMagnitude < 1e-8f) direction = Vector3.forward;
        direction.Normalize();

        // Fit only the new symmetric handle; the existing control points stay fixed.
        float numerator = 0f, denominator = 0f;
        for (int i = 1; i < 16; i++)
        {
            float u = i / 16f, v = 1f - u;
            float leftBasis = -3f * v * u * u, rightBasis = 3f * v * v * u;
            Vector3 leftError = Bezier(p0, p1, p2, p3, t * u) - Bezier(p0, p1, point, point, u);
            Vector3 rightError = Bezier(p0, p1, p2, p3, t + (1f - t) * u) - Bezier(point, point, p2, p3, u);
            numerator += leftBasis * Vector3.Dot(direction, leftError) + rightBasis * Vector3.Dot(direction, rightError);
            denominator += leftBasis * leftBasis + rightBasis * rightBasis;
        }
        float maxLength = 0.5f * Mathf.Min(Vector3.Distance(p0, point), Vector3.Distance(point, p3));
        length = Mathf.Clamp(numerator / denominator, 0f, maxLength);
    }

    static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
    {
        float s = 1f - t;
        return s * s * s * a + 3f * s * s * t * b + 3f * s * t * t * c + t * t * t * d;
    }

    public static void AdaptiveHandle(Vector3 before, Vector3 point, Vector3 after,
        out Vector3 direction, out float length)
    {
        Vector3 incoming = point - before;
        Vector3 outgoing = after - point;
        float beforeLength = incoming.magnitude;
        float afterLength = outgoing.magnitude;
        direction = incoming.normalized + outgoing.normalized;
        if (direction.sqrMagnitude < 1e-8f)
            direction = afterLength > 1e-5f ? outgoing : incoming;
        if (direction.sqrMagnitude < 1e-8f) direction = Vector3.forward;
        direction.Normalize();
        if (beforeLength < 1e-5f) beforeLength = afterLength;
        if (afterLength < 1e-5f) afterLength = beforeLength;
        length = Mathf.Min(beforeLength, afterLength) / 3f;
    }

    public static Vector3 Hermite(Vector3 a, Vector3 da, Vector3 b, Vector3 db, float u, float length)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * a + (u3 - 2f * u2 + u) * length * da
             + (-2f * u3 + 3f * u2) * b + (u3 - u2) * length * db;
    }

    public static void DirectionTangents(Vector3[] values, float[] distances, Vector3[] tangents)
    {
        for (int i = 0; i < values.Length; i++)
        {
            int before = Mathf.Max(0, i - 1), after = Mathf.Min(values.Length - 1, i + 1);
            float left = distances[i] - distances[before];
            float right = distances[after] - distances[i];
            Vector3 a = left > 1e-5f ? (values[i] - values[before]) / left : Vector3.zero;
            Vector3 b = right > 1e-5f ? (values[after] - values[i]) / right : Vector3.zero;
            tangents[i] = left <= 1e-5f ? b : right <= 1e-5f ? a : new Vector3(
                MonotoneSlope(a.x, b.x, left, right),
                MonotoneSlope(a.y, b.y, left, right),
                MonotoneSlope(a.z, b.z, left, right));
        }
    }

    static float MonotoneSlope(float a, float b, float left, float right)
    {
        if (a * b <= 0f) return 0f;
        float w1 = 2f * right + left, w2 = right + 2f * left;
        return (w1 + w2) / (w1 / a + w2 / b);
    }
}
