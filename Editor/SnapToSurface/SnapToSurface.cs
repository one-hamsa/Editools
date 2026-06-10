using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        // Cached on build — snap targets don't move during a session, so the transform
        // matrices are computed once instead of per-vertex per-frame, and worldBounds
        // (the renderer's world-space AABB) drives the broad-phase ray cull.
        public Matrix4x4     localToWorld;
        public Matrix4x4     worldToLocal;
        public Bounds        worldBounds;
    }
    private static readonly List<SnapMeshEntry> s_snapMeshes = new();

    // Master on/off for the tool, matching the Editools gating convention
    // (see EditoolsSettingsPopup). Backed by EditorPrefs so it persists per machine.
    internal static bool Enabled {
        get => EditorPrefs.GetBool("SnapToSurface_Enabled", true);
        set => EditorPrefs.SetBool("SnapToSurface_Enabled", value);
    }

    // Per-mask filters deciding which surface types are eligible snap targets.
    // Exposed as checkboxes in the Snap To Surface settings submenu.
    internal static bool SnapSkinnedMeshes {
        get => EditorPrefs.GetBool("SnapToSurface_SnapSkinned", true);
        set => EditorPrefs.SetBool("SnapToSurface_SnapSkinned", value);
    }
    // Static-flag filter, orthogonal to renderer type: only objects flagged Static
    // (GameObject.isStatic) are snap targets by default. When on, non-static objects
    // are also eligible.
    internal static bool SnapNonStatic {
        get => EditorPrefs.GetBool("SnapToSurface_SnapNonStatic", false);
        set => EditorPrefs.SetBool("SnapToSurface_SnapNonStatic", value);
    }
    // ZWrite-off surfaces (glow/additive/transparent) are skipped unless this is on.
    internal static bool SnapTransparentMeshes {
        get => EditorPrefs.GetBool("SnapToSurface_SnapTransparent", false);
        set => EditorPrefs.SetBool("SnapToSurface_SnapTransparent", value);
    }
    // When on, the object's Z+ (forward) axis is aligned to the surface normal instead
    // of the default Y+ (up) axis. Useful for objects authored facing down their Z axis.
    internal static bool AlignZToSurface {
        get => EditorPrefs.GetBool("SnapToSurface_AlignZ", false);
        set => EditorPrefs.SetBool("SnapToSurface_AlignZ", value);
    }
    // Distance to push the snapped object off the surface along its normal. 0 sits flush
    // on the surface; positive lifts it away from the surface, negative sinks it in.
    internal static float SurfaceOffset {
        get => EditorPrefs.GetFloat("SnapToSurface_Offset", 0f);
        set => EditorPrefs.SetFloat("SnapToSurface_Offset", value);
    }

    [Shortcut("Editools/Snap To Surface", KeyCode.A, ShortcutModifiers.Alt)]
    private static void ActivateSnapMode() {
        if (!EditoolsOverlay.IsActive || !Enabled)
            return;

        // Alt+A is a sub-combo of the Ctrl+Alt+A toggle-active binding. On a three-key
        // chord the modifier state can momentarily read as just Alt, firing this
        // shortcut by accident — so ignore the press whenever Ctrl/Cmd is also held.
        const EventModifiers ctrlOrCmd = EventModifiers.Control | EventModifiers.Command;
        if (Event.current != null && (Event.current.modifiers & ctrlOrCmd) != 0)
            return;

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

        // When editing a prefab in isolation, Unity loads it into a separate preview
        // scene that FindObjectsByType doesn't see — without this branch the cache
        // would contain the (hidden) main-scene meshes instead of the prefab contents.
        var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
        Transform stageRoot = prefabStage != null ? prefabStage.prefabContentsRoot.transform : null;

        MeshFilter[] meshFilters = stageRoot != null
            ? stageRoot.GetComponentsInChildren<MeshFilter>(true)
            : GameObject.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        foreach (var mf in meshFilters) {
            MeshRenderer mr = mf.GetComponent<MeshRenderer>();
            // mr.enabled is the component toggle only; an inactive GameObject (or one
            // under an inactive parent) keeps enabled == true, so activeInHierarchy is
            // what actually excludes hidden objects from being snap targets.
            if (mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy)
                continue;
            AddSnapMesh(mf.gameObject, mf.transform, mr, mf.sharedMesh);
        }

        if (SnapSkinnedMeshes) {
            SkinnedMeshRenderer[] skinned = stageRoot != null
                ? stageRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                : GameObject.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);
            foreach (var smr in skinned) {
                if (!smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null)
                    continue;

                // Bake the current posed mesh so the raycast hits the deformed surface
                // the artist actually sees. Baked verts are in the renderer's local
                // space, so AddSnapMesh's TransformPoint maps them to world correctly.
                Mesh baked = new Mesh();
                smr.BakeMesh(baked);
                AddSnapMesh(smr.gameObject, smr.transform, smr, baked);
                // AddSnapMesh copies the data into pooled lists, so the temporary
                // baked mesh is no longer needed.
                Object.DestroyImmediate(baked);
            }
        }
    }

    /// <summary>
    /// Extract a renderer's mesh into pooled vertex/triangle/normal lists and add it
    /// to the snap cache. Skips ignored objects, null meshes, and — unless transparent
    /// surfaces are enabled — meshes whose materials don't write depth.
    /// </summary>
    private static void AddSnapMesh(GameObject obj, Transform tf, Renderer renderer, Mesh mesh) {
        if (mesh == null || ignoredObjects.Contains(obj))
            return;

        // Static-flag filter, orthogonal to renderer type: non-static objects are only
        // eligible snap targets when the Non Static mask is on.
        if (!SnapNonStatic && !obj.isStatic)
            return;

        // ZWrite Off is the universal signal for "I'm a glow/additive/transparent
        // surface" — those are skipped unless the Transparent Meshes mask is on, even
        // when their RenderType tag lies (e.g. Toon 2.0 Glow Sphere Lighten claims
        // Opaque/Geometry but uses BlendOp Max + ZWrite Off).
        if (!SnapTransparentMeshes && !AnyMaterialWritesDepth(renderer))
            return;

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
            transform = tf,
            vertices  = verts,
            triangles = tris,
            normals   = norms,
            localToWorld = tf.localToWorldMatrix,
            worldToLocal = tf.worldToLocalMatrix,
            worldBounds  = renderer.bounds,
        });
    }

    /// <summary>
    /// Returns true if at least one of the renderer's shared materials writes to the
    /// depth buffer. Reads the shader's first SubShader pass via ShaderUtil since
    /// ZWrite is a render-state token, not a uniform property (so `_ZWrite` isn't
    /// always exposed as a material property). Falls back to allowing the snap if
    /// the state can't be read — better to over-snap than to silently skip everything.
    /// </summary>
    private static bool AnyMaterialWritesDepth(Renderer renderer) {
        var mats = renderer.sharedMaterials;
        if (mats == null || mats.Length == 0)
            return true;

        for (int i = 0; i < mats.Length; i++) {
            var mat = mats[i];
            if (mat == null || mat.shader == null) continue;
            if (ShaderWritesDepth(mat))
                return true;
        }
        return false;
    }

    private static readonly Dictionary<Shader, bool> s_shaderZWriteCache = new();

    private static bool ShaderWritesDepth(Material mat) {
        var shader = mat.shader;
        if (s_shaderZWriteCache.TryGetValue(shader, out bool cached))
            return cached;

        bool writes = true; // default to "writes" so unknown shaders still snap

        // First try the material property — many shaders expose _ZWrite as a float
        // toggle (URP Lit, ASE-generated, etc.). 0 = Off, 1 = On.
        if (mat.HasProperty("_ZWrite")) {
            writes = mat.GetFloat("_ZWrite") > 0.5f;
            s_shaderZWriteCache[shader] = writes;
            return writes;
        }

        // Fallback: parse the shader source for a top-level `ZWrite Off`. This
        // catches hand-written shaders (like Toon 2.0 Glow Sphere variants) that
        // hardcode ZWrite Off without exposing it as a property.
        string path = AssetDatabase.GetAssetPath(shader);
        if (!string.IsNullOrEmpty(path) && path.EndsWith(".shader")) {
            try {
                string src = System.IO.File.ReadAllText(path);
                // Strip block + line comments before scanning so commented-out
                // `// ZWrite Off` doesn't trigger a false positive.
                src = System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                src = System.Text.RegularExpressions.Regex.Replace(src, @"//.*?$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (System.Text.RegularExpressions.Regex.IsMatch(src, @"\bZWrite\s+Off\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    writes = false;
            } catch {
                // Read failure — leave writes = true.
            }
        }

        s_shaderZWriteCache[shader] = writes;
        return writes;
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
            // Update position, pushed off the surface along its normal by SurfaceOffset.
            selectedObject.transform.position = closestHitPoint + closestHitNormal * SurfaceOffset;

            // Align one axis to the surface normal; the other in-plane axis takes a
            // stable tangent so the object doesn't spin as the cursor moves.
            Vector3 normal = closestHitNormal;
            Vector3 tangent;

            // Try to maintain a consistent tangent direction
            if (Vector3.Dot(normal, Vector3.up) > 0.99f) {
                // Surface is nearly horizontal, use world forward
                tangent = Vector3.forward;
            } else if (Vector3.Dot(normal, Vector3.up) < -0.99f) {
                // Surface is nearly horizontal but upside down
                tangent = Vector3.back;
            } else {
                // Project world up onto the surface plane to get the tangent
                tangent = Vector3.ProjectOnPlane(Vector3.up, normal).normalized;
            }

            // AlignZToSurface points Z+ (forward) at the normal; otherwise the default
            // points Y+ (up) at the normal. LookRotation's args are (forward, up).
            selectedObject.transform.rotation = AlignZToSurface
                ? Quaternion.LookRotation(normal, tangent)
                : Quaternion.LookRotation(tangent, normal);

            EditorUtility.SetDirty(selectedObject);
        }
    }

    private static void ExitSnapMode(bool confirm) {
        if (confirm && lastHitSurfaceObject != null)
            Debug.Log($"[SnapToSurface] Aligned to surface: {lastHitSurfaceObject.name}");

        if (!confirm && selectedObject != null) {
            // Revert through Unity's undo stack rather than restoring the transform
            // by hand. The snap entry (or, on the BeginSnap/greybox path, the caller's
            // RegisterCreatedObjectUndo) is the top group, so PerformUndo rolls back
            // exactly the snap and removes only that record — the object's earlier
            // undo history is left intact, and a no-op snap reverts cleanly.
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

        // Broad phase: skip the whole mesh if the ray never crosses its world-space
        // bounding box. The cursor ray only touches a handful of objects' boxes, so this
        // turns an all-triangles-in-scene test into just the meshes under the cursor.
        if (!entry.worldBounds.IntersectRay(ray))
            return false;

        var vertices  = entry.vertices;
        var triangles = entry.triangles;

        // Intersect in the mesh's local space: transform the ray once with the cached
        // inverse matrix instead of transforming every vertex to world space each frame.
        // Ray's constructor normalizes the direction, so closestT and the hit point below
        // are taken from localRay's own origin/direction to stay self-consistent.
        Vector3 localOrigin = entry.worldToLocal.MultiplyPoint3x4(ray.origin);
        Vector3 localDir     = entry.worldToLocal.MultiplyVector(ray.direction);
        Ray localRay = new Ray(localOrigin, localDir);

        // Backface culling happens in authored local space, which always matches what's
        // rendered: Unity reverses cull mode for negative-determinant (mirrored)
        // renderers, so the visible side of a triangle is its authored front face
        // regardless of the transform.
        float closestT = float.MaxValue;
        bool hitFound = false;
        int closestTriIndex = 0;

        int triCount = triangles.Count;
        for (int i = 0; i < triCount; i += 3) {
            Vector3 v0 = vertices[triangles[i]];
            Vector3 v1 = vertices[triangles[i + 1]];
            Vector3 v2 = vertices[triangles[i + 2]];

            if (RayIntersectsTriangle(localRay, v0, v1, v2, out float t)) {
                if (t < closestT) {
                    closestT = t;
                    hitFound = true;
                    closestTriIndex = i;
                }
            }
        }

        if (hitFound) {
            // Only the winning triangle is mapped to world space — three points, not
            // the whole mesh.
            Vector3 v0 = entry.localToWorld.MultiplyPoint3x4(vertices[triangles[closestTriIndex]]);
            Vector3 v1 = entry.localToWorld.MultiplyPoint3x4(vertices[triangles[closestTriIndex + 1]]);
            Vector3 v2 = entry.localToWorld.MultiplyPoint3x4(vertices[triangles[closestTriIndex + 2]]);

            hitPoint = entry.localToWorld.MultiplyPoint3x4(localRay.origin + localRay.direction * closestT);
            // World-space distance so the caller can compare hits across meshes on the
            // same scale — local t is in scaled-ray units and isn't comparable between
            // objects with different transforms.
            hitDistance = Vector3.Distance(ray.origin, hitPoint);

            // Use the hit triangle's true geometric normal rather than interpolating
            // stored vertex normals. Vertex normals reflect shading intent (smooth/flat)
            // and on deformed meshes — e.g. greybox faces with moved edges — they don't
            // match the actual surface orientation. For snapping we always want the real
            // angle of the triangle we hit.
            hitNormal = Vector3.Cross(v1 - v0, v2 - v0).normalized;

            // Mirrored transforms reverse world-space winding, so the raw cross product
            // points into the surface the artist sees — flip it back outward.
            if (entry.localToWorld.determinant < 0f)
                hitNormal = -hitNormal;
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

        // Reject backfaces (a < 0) and parallel rays (a ≈ 0)
        if (a < 0.00001f)
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
