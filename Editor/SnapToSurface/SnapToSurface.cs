using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using System.Collections.Generic;
using UnityEngine.Pool;

public class SnapToSurface : EditorWindow
{
    private static bool isSnapping = false;
    private static GameObject selectedObject;
    private static Vector3 originalPosition;
    private static Quaternion originalRotation;
    private static HashSet<GameObject> ignoredObjects = new HashSet<GameObject>();
    private static GameObject lastHitSurfaceObject;

    // Per-session scene snapshot built on snap entry so MouseMove doesn't have to
    // FindObjectsOfType<MeshFilter>() and re-fetch mesh.vertices/triangles every frame
    // (mesh.vertices returns a fresh array each call — that was the GC hotspot).
    struct SnapMeshEntry
    {
        public Transform     transform;
        public List<Vector3> vertices;
        public List<int>     triangles;
        public List<Vector3> normals;
    }
    private static readonly List<SnapMeshEntry> s_snapMeshes = new();

    [Shortcut("Editools/Snap To Surface", KeyCode.A, ShortcutModifiers.Alt)]
    private static void ActivateSnapMode() {
        if (Selection.activeGameObject == null)
            return;

        if (!isSnapping) {
            selectedObject = Selection.activeGameObject;
            originalPosition = selectedObject.transform.position;
            originalRotation = selectedObject.transform.rotation;

            // Record undo so the snap can be reverted with Ctrl+Z
            Undo.RegisterCompleteObjectUndo(selectedObject.transform, "Snap To Surface");

            // Build list of objects to ignore (selected object and all children)
            ignoredObjects.Clear();
            ignoredObjects.Add(selectedObject);
            foreach (Transform child in selectedObject.GetComponentsInChildren<Transform>(true)) {
                ignoredObjects.Add(child.gameObject);
            }

            BuildSnapMeshCache();

            isSnapping = true;
            SceneView.duringSceneGui += OnSceneGUI;
            SceneView.RepaintAll();
        }
    }

    /// <summary>
    /// Programmatically begin snap mode for a given target without registering a
    /// transform undo. Used by Ctrl+G greybox creation: the caller's
    /// RegisterCreatedObjectUndo covers the cancel/delete path via PerformUndo.
    /// </summary>
    internal static void BeginSnap(GameObject target) {
        if (isSnapping || target == null)
            return;

        selectedObject   = target;
        originalPosition = target.transform.position;
        originalRotation = target.transform.rotation;

        ignoredObjects.Clear();
        ignoredObjects.Add(selectedObject);
        foreach (Transform child in selectedObject.GetComponentsInChildren<Transform>(true))
            ignoredObjects.Add(child.gameObject);

        BuildSnapMeshCache();

        isSnapping = true;
        SceneView.duringSceneGui += OnSceneGUI;
        SceneView.RepaintAll();
    }

    /// <summary>
    /// Build a per-session snapshot of all eligible meshes in the scene. This is done
    /// once on snap entry instead of on every MouseMove. Vertex/triangle/normal lists
    /// are taken from UnityEngine.Pool.ListPool — they get returned on cleanup so
    /// repeated snap sessions reuse the same buffers.
    /// </summary>
    private static void BuildSnapMeshCache() {
        ReleaseSnapMeshCache();

        var meshFilters = GameObject.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        foreach (var mf in meshFilters) {
            GameObject obj = mf.gameObject;
            if (ignoredObjects.Contains(obj))
                continue;

            MeshRenderer mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled)
                continue;

            Mesh mesh = mf.sharedMesh;
            if (mesh == null)
                continue;

            var verts = ListPool<Vector3>.Get();
            var tris  = ListPool<int>.Get();
            var norms = ListPool<Vector3>.Get();

            mesh.GetVertices(verts);
            mesh.GetNormals(norms);

            // Aggregate all submeshes' triangles to preserve the original
            // mesh.triangles-flattened behaviour.
            for (int sm = 0; sm < mesh.subMeshCount; sm++) {
                var smTris = ListPool<int>.Get();
                mesh.GetTriangles(smTris, sm);
                tris.AddRange(smTris);
                ListPool<int>.Release(smTris);
            }

            s_snapMeshes.Add(new SnapMeshEntry {
                transform = mf.transform,
                vertices  = verts,
                triangles = tris,
                normals   = norms,
            });
        }
    }

    private static void ReleaseSnapMeshCache() {
        foreach (var entry in s_snapMeshes) {
            ListPool<Vector3>.Release(entry.vertices);
            ListPool<int>.Release(entry.triangles);
            ListPool<Vector3>.Release(entry.normals);
        }
        s_snapMeshes.Clear();
    }

    private static void OnSceneGUI(SceneView sceneView) {
        if (!isSnapping || selectedObject == null) {
            CleanupSnapMode();
            return;
        }

        Event e = Event.current;
        if (e == null)
            return;

        // Only handle mouse events
        if (e.type == EventType.MouseDown) {
            if (e.button == 0) // Left click - confirm
            {
                e.Use();
                ExitSnapMode(true);
                return;
            } else if (e.button == 1) // Right click - cancel
              {
                e.Use();
                ExitSnapMode(false);
                return;
            }
        }

        // Update position on mouse move
        if (e.type == EventType.MouseMove) {
            UpdateObjectPosition(e.mousePosition);
            e.Use();
            sceneView.Repaint();
        }
    }

    private static void UpdateObjectPosition(Vector2 mousePosition) {
        if (!isSnapping || selectedObject == null)
            return;

        Ray ray = HandleUtility.GUIPointToWorldRay(mousePosition);

        float closestDistance = float.MaxValue;
        Vector3 closestHitPoint = Vector3.zero;
        Vector3 closestHitNormal = Vector3.up;
        bool foundHit = false;

        for (int i = 0; i < s_snapMeshes.Count; i++) {
            var entry = s_snapMeshes[i];
            if (entry.transform == null) continue;

            if (RaycastMesh(ray, entry, out Vector3 hitPoint, out Vector3 hitNormal, out float hitDistance)) {
                if (hitDistance < closestDistance) {
                    closestDistance = hitDistance;
                    closestHitPoint = hitPoint;
                    closestHitNormal = hitNormal;
                    foundHit = true;
                    lastHitSurfaceObject = entry.transform.gameObject;
                }
            }
        }

        if (foundHit) {
            // Update position
            selectedObject.transform.position = closestHitPoint;

            // Update rotation - align Y+ with normal
            // Use a more stable calculation for the forward direction
            Vector3 up = closestHitNormal;
            Vector3 forward;

            // Try to maintain a consistent forward direction
            if (Vector3.Dot(up, Vector3.up) > 0.99f) {
                // Surface is nearly horizontal, use world forward
                forward = Vector3.forward;
            } else if (Vector3.Dot(up, Vector3.up) < -0.99f) {
                // Surface is nearly horizontal but upside down
                forward = Vector3.back;
            } else {
                // Project world up onto the surface plane to get forward
                forward = Vector3.ProjectOnPlane(Vector3.up, up).normalized;
            }

            selectedObject.transform.rotation = Quaternion.LookRotation(forward, up);

            EditorUtility.SetDirty(selectedObject);
        }
    }

    private static void ExitSnapMode(bool confirm) {
        if (confirm && lastHitSurfaceObject != null)
            Debug.Log($"[SnapToSurface] Aligned to surface: {lastHitSurfaceObject.name}");

        if (!confirm && selectedObject != null) {
            // Revert via undo so no stale undo entry remains
            Undo.PerformUndo();
        }

        EditorApplication.delayCall += CleanupSnapMode;
    }

    private static void CleanupSnapMode() {
        isSnapping = false;
        SceneView.duringSceneGui -= OnSceneGUI;
        ignoredObjects.Clear();
        ReleaseSnapMeshCache();
        selectedObject = null;
        lastHitSurfaceObject = null;
        SceneView.RepaintAll();
    }

    private static bool RaycastMesh(Ray ray, SnapMeshEntry entry, out Vector3 hitPoint, out Vector3 hitNormal, out float hitDistance) {
        hitPoint = Vector3.zero;
        hitNormal = Vector3.up;
        hitDistance = float.MaxValue;

        Transform transform = entry.transform;
        var vertices  = entry.vertices;
        var triangles = entry.triangles;
        var normals   = entry.normals;

        float closestT = float.MaxValue;
        bool hitFound = false;
        Vector3 closestLocalHitPoint = Vector3.zero;
        int closestTriIndex = 0;

        int triCount = triangles.Count;
        for (int i = 0; i < triCount; i += 3) {
            Vector3 v0 = transform.TransformPoint(vertices[triangles[i]]);
            Vector3 v1 = transform.TransformPoint(vertices[triangles[i + 1]]);
            Vector3 v2 = transform.TransformPoint(vertices[triangles[i + 2]]);

            if (RayIntersectsTriangle(ray, v0, v1, v2, out float t)) {
                if (t < closestT) {
                    closestT = t;
                    hitFound = true;
                    closestTriIndex = i;
                    closestLocalHitPoint = ray.origin + ray.direction * t;
                }
            }
        }

        if (hitFound) {
            hitPoint = closestLocalHitPoint;
            hitDistance = closestT;

            Vector3 v0 = transform.TransformPoint(vertices[triangles[closestTriIndex]]);
            Vector3 v1 = transform.TransformPoint(vertices[triangles[closestTriIndex + 1]]);
            Vector3 v2 = transform.TransformPoint(vertices[triangles[closestTriIndex + 2]]);

            Vector3 barycentric = GetBarycentricCoordinates(hitPoint, v0, v1, v2);

            Vector3 n0 = transform.TransformDirection(normals[triangles[closestTriIndex]]);
            Vector3 n1 = transform.TransformDirection(normals[triangles[closestTriIndex + 1]]);
            Vector3 n2 = transform.TransformDirection(normals[triangles[closestTriIndex + 2]]);

            hitNormal = (n0 * barycentric.x + n1 * barycentric.y + n2 * barycentric.z).normalized;
        }

        return hitFound;
    }

    private static bool RayIntersectsTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float t) {
        t = 0;

        // Möller–Trumbore intersection algorithm
        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 h = Vector3.Cross(ray.direction, edge2);
        float a = Vector3.Dot(edge1, h);

        if (a > -0.00001f && a < 0.00001f)
            return false;

        float f = 1.0f / a;
        Vector3 s = ray.origin - v0;
        float u = f * Vector3.Dot(s, h);

        if (u < 0.0f || u > 1.0f)
            return false;

        Vector3 q = Vector3.Cross(s, edge1);
        float v = f * Vector3.Dot(ray.direction, q);

        if (v < 0.0f || u + v > 1.0f)
            return false;

        t = f * Vector3.Dot(edge2, q);

        if (t > 0.00001f) {
            return true;
        }

        return false;
    }

    private static Vector3 GetBarycentricCoordinates(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        Vector3 v0 = b - a;
        Vector3 v1 = c - a;
        Vector3 v2 = p - a;

        float d00 = Vector3.Dot(v0, v0);
        float d01 = Vector3.Dot(v0, v1);
        float d11 = Vector3.Dot(v1, v1);
        float d20 = Vector3.Dot(v2, v0);
        float d21 = Vector3.Dot(v2, v1);

        float denom = d00 * d11 - d01 * d01;
        float v = (d11 * d20 - d01 * d21) / denom;
        float w = (d00 * d21 - d01 * d20) / denom;
        float u = 1.0f - v - w;

        return new Vector3(u, v, w);
    }
}
