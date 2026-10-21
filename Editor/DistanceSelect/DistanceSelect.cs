#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.UIElements;

/// <summary>
/// Adds GameObjects near the selection to the selection. Each activation (Ctrl+= or the
/// Editools Settings row) searches one more distance interval:
/// aggregate mode searches one interval from the whole current selection;
/// otherwise the search starts from the original selection and its distance grows by one
/// interval per activation, until the selection is changed by something else.
/// Distance is measured between pivots, bounding boxes or mesh surfaces.
/// </summary>
static class DistanceSelect
{
    internal enum DistanceMode
    {
        Pivot,
        BoundingBox,
        Mesh
    }

    const string k_ShownPref = "Editools_DistanceSelect_Shown";
    const string k_FromPref = "Editools_DistanceSelect_From";
    const string k_ToPref = "Editools_DistanceSelect_To";
    const string k_IntervalPref = "Editools_DistanceSelect_Interval";
    const string k_AggregatePref = "Editools_DistanceSelect_Aggregate";
    const string k_EntirePrefabsPref = "Editools_DistanceSelect_EntirePrefabs";
    const float k_DefaultInterval = 0.5f;
    internal const float k_MinInterval = 0.001f;

    // notifies live overlay instances when the panel is shown/hidden
    internal static event System.Action s_onShownChanged;
    // notifies live overlay instances when the search state changes
    internal static event System.Action s_onStateChanged;

    internal static bool Shown
    {
        get => EditorPrefs.GetBool(k_ShownPref, false);
        set
        {
            if (value == Shown) return;
            EditorPrefs.SetBool(k_ShownPref, value);
            s_onShownChanged?.Invoke();
        }
    }

    internal static DistanceMode From
    {
        get => (DistanceMode)EditorPrefs.GetInt(k_FromPref, (int)DistanceMode.Pivot);
        set => EditorPrefs.SetInt(k_FromPref, (int)value);
    }

    internal static DistanceMode To
    {
        get => (DistanceMode)EditorPrefs.GetInt(k_ToPref, (int)DistanceMode.Pivot);
        set => EditorPrefs.SetInt(k_ToPref, (int)value);
    }

    internal static float Interval
    {
        get => EditorPrefs.GetFloat(k_IntervalPref, k_DefaultInterval);
        set => EditorPrefs.SetFloat(k_IntervalPref, Mathf.Max(k_MinInterval, value));
    }

    internal static bool Aggregate
    {
        get => EditorPrefs.GetBool(k_AggregatePref, false);
        set => EditorPrefs.SetBool(k_AggregatePref, value);
    }

    internal static bool EntirePrefabs
    {
        get => EditorPrefs.GetBool(k_EntirePrefabsPref, false);
        set => EditorPrefs.SetBool(k_EntirePrefabsPref, value);
    }

    // Current search: the objects it started from, how many non-aggregate intervals it has grown,
    // and the selection it produced (any other selection starts a new search).
    static readonly List<GameObject> s_origins = new List<GameObject>(8);
    static readonly HashSet<int> s_resultSelection = new HashSet<int>();
    static int s_step;

    internal static string Status { get; private set; } = "";

    // ─── Activation ─────────────────────────────────────────────

    [Shortcut("Editools/Distance Select", KeyCode.Equals, ShortcutModifiers.Action)]
    static void DistanceSelectShortcut() => Activate();

    /// <summary>Shows the settings panel and adds the next distance interval to the selection.</summary>
    internal static void Activate()
    {
        Shown = true;

        using var _ = ListPool<GameObject>.Get(out var selected);
        foreach (var go in Selection.gameObjects)
        {
            // Project assets in the selection aren't part of the scene search.
            if (!EditorUtility.IsPersistent(go))
                selected.Add(go);
        }

        if (selected.Count == 0)
        {
            Status = "Select an object first.";
            s_onStateChanged?.Invoke();
            return;
        }

        if (!IsSearchCurrent())
        {
            s_step = 0;
            s_origins.Clear();
            s_origins.AddRange(selected);
        }

        float distance;
        List<GameObject> sources;
        if (Aggregate)
        {
            distance = Interval;
            sources = selected;
        }
        else
        {
            s_step++;
            distance = Interval * s_step;
            sources = s_origins;
        }

        using var __ = HashSetPool<int>.Get(out var selectedIds);
        foreach (var go in selected)
            selectedIds.Add(go.GetInstanceID());

        using var ___ = ListPool<GameObject>.Get(out var found);
        FindWithin(sources, selectedIds, distance, found);

        bool entirePrefabs = EntirePrefabs;
        using var ____ = ListPool<Object>.Get(out var newSelection);
        foreach (var go in selected)
            newSelection.Add(go);
        int added = 0;
        foreach (var go in found)
        {
            var target = entirePrefabs ? GetEnclosingPrefabRoot(go, sources) : go;
            // Several found objects can resolve to the same prefab root.
            if (selectedIds.Add(target.GetInstanceID()))
            {
                newSelection.Add(target);
                added++;
            }
        }
        Selection.objects = newSelection.ToArray();

        s_resultSelection.Clear();
        foreach (var obj in newSelection)
            s_resultSelection.Add(obj.GetInstanceID());

        Status = Aggregate
            ? string.Format("Added {0} within {1:0.###} of the selection.", added, distance)
            : string.Format("Step {0}: added {1} within {2:0.###} of {3} original object(s).",
                s_step, added, distance, s_origins.Count);
        s_onStateChanged?.Invoke();
    }

    internal static bool CanResetSelection => s_origins.Count > 0;

    /// <summary>Selects only the objects the current search started from and ends the search.</summary>
    internal static void ResetSelection()
    {
        using var _ = ListPool<Object>.Get(out var origins);
        foreach (var go in s_origins)
        {
            // Destroyed since the search started.
            if (go != null)
                origins.Add(go);
        }
        Selection.objects = origins.ToArray();

        s_origins.Clear();
        s_resultSelection.Clear();
        s_step = 0;

        Status = string.Format("Selection reset to {0} original object(s).", origins.Count);
        s_onStateChanged?.Invoke();
    }

    /// <summary>True while the selection is still the one the last step produced.</summary>
    internal static bool IsSearchCurrent()
    {
        if (s_origins.Count == 0) return false;
        var ids = Selection.instanceIDs;
        if (ids.Length != s_resultSelection.Count) return false;
        foreach (int id in ids)
        {
            if (!s_resultSelection.Contains(id)) return false;
        }
        return true;
    }

    internal static int CurrentStep => IsSearchCurrent() ? s_step : 0;

    /// <summary>
    /// Outermost prefab instance root above <paramref name="go"/> that contains none of the
    /// search's source objects; <paramref name="go"/> itself when there is none.
    /// Model prefabs (imported meshes) are never used as the root.
    /// </summary>
    static GameObject GetEnclosingPrefabRoot(GameObject go, List<GameObject> sources)
    {
        GameObject result = go;
        var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
        while (root != null)
        {
            if (ContainsAny(root.transform, sources)) break;
            if (PrefabUtility.GetPrefabAssetType(root) != PrefabAssetType.Model)
                result = root;

            var parent = root.transform.parent;
            root = parent != null ? PrefabUtility.GetNearestPrefabInstanceRoot(parent.gameObject) : null;
        }
        return result;
    }

    static bool ContainsAny(Transform root, List<GameObject> objects)
    {
        foreach (var go in objects)
        {
            // Destroyed since the search started.
            if (go == null) continue;
            if (go.transform.IsChildOf(root)) return true;
        }
        return false;
    }

    // ─── Search ─────────────────────────────────────────────────

    static void FindWithin(List<GameObject> sources, HashSet<int> excludedIds, float distance, List<GameObject> found)
    {
        DistanceMode from = From;
        DistanceMode to = To;
        float sqrDistance = distance * distance;

        using var _ = DictionaryPool<int, Shape>.Get(out var shapes);
        using var __ = ListPool<Shape>.Get(out var sourceShapes);
        foreach (var go in sources)
        {
            // Destroyed since the search started.
            if (go == null) continue;
            var shape = Shape.Create(go, from, shapes);
            // Objects without the data the distance mode needs are skipped.
            if (shape != null)
                sourceShapes.Add(shape);
        }
        if (sourceShapes.Count == 0) return;

        using var ___ = ListPool<GameObject>.Get(out var candidates);
        GetCandidates(candidates);

        foreach (var go in candidates)
        {
            if (excludedIds.Contains(go.GetInstanceID())) continue;
            if (!Shape.TryGetBounds(go, to, out var candidateBounds)) continue;

            Shape candidate = null;
            foreach (var source in sourceShapes)
            {
                if (SqrGap(source.bounds, candidateBounds) > sqrDistance) continue;

                candidate ??= Shape.Create(go, to, shapes);
                if (candidate == null) break;
                if (Shape.Within(source, candidate, distance))
                {
                    found.Add(go);
                    break;
                }
            }
        }
    }

    static void GetCandidates(List<GameObject> candidates)
    {
        using var _ = ListPool<Transform>.Get(out var transforms);
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null)
            stage.prefabContentsRoot.GetComponentsInChildren(false, transforms);
        else
            transforms.AddRange(Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));

        foreach (var t in transforms)
        {
            var go = t.gameObject;
            if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) continue;
            candidates.Add(go);
        }
    }

    /// <summary>Squared distance between two AABBs; 0 when they overlap.</summary>
    static float SqrGap(Bounds a, Bounds b)
    {
        Vector3 gap = Vector3.Max(Vector3.zero, Vector3.Max(a.min - b.max, b.min - a.max));
        return gap.sqrMagnitude;
    }

    // ─── Shapes ─────────────────────────────────────────────────

    /// <summary>World-space geometry of one GameObject for one distance mode.</summary>
    class Shape
    {
        const int k_MaxGridCellsPerAxis = 32;

        public DistanceMode mode;
        public Vector3 point;
        public Bounds bounds;
        public Vector3[] vertices;
        public int[] triangles;
        TriangleGrid _grid;
        float _gridDistance;

        static Mesh s_bakedMesh;

        /// <summary>Builds the shape, cached per GameObject and mode. Null when the GameObject lacks the data (no renderer / no mesh).</summary>
        public static Shape Create(GameObject go, DistanceMode mode, Dictionary<int, Shape> cache)
        {
            int key = go.GetInstanceID() * 3 + (int)mode;
            if (cache.TryGetValue(key, out var cached)) return cached;

            Shape shape = null;
            switch (mode)
            {
                case DistanceMode.Pivot:
                    shape = new Shape { mode = mode, point = go.transform.position, bounds = new Bounds(go.transform.position, Vector3.zero) };
                    break;
                case DistanceMode.BoundingBox:
                    if (go.TryGetComponent(out Renderer renderer))
                        shape = new Shape { mode = mode, bounds = renderer.bounds };
                    break;
                case DistanceMode.Mesh:
                    shape = CreateMeshShape(go);
                    break;
            }

            cache[key] = shape;
            return shape;
        }

        /// <summary>World AABB for the broad-phase test, without reading mesh data.</summary>
        public static bool TryGetBounds(GameObject go, DistanceMode mode, out Bounds bounds)
        {
            switch (mode)
            {
                case DistanceMode.Pivot:
                    bounds = new Bounds(go.transform.position, Vector3.zero);
                    return true;
                case DistanceMode.BoundingBox:
                    if (go.TryGetComponent(out Renderer renderer))
                    {
                        bounds = renderer.bounds;
                        return true;
                    }
                    break;
                case DistanceMode.Mesh:
                    if (go.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.sharedMesh != null)
                    {
                        bounds = skinned.bounds;
                        return true;
                    }
                    if (go.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                    {
                        bounds = TransformBounds(filter.sharedMesh.bounds, go.transform.localToWorldMatrix);
                        return true;
                    }
                    break;
            }
            bounds = default;
            return false;
        }

        static Shape CreateMeshShape(GameObject go)
        {
            Mesh mesh;
            Matrix4x4 matrix;
            if (go.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.sharedMesh != null)
            {
                if (s_bakedMesh == null)
                    s_bakedMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skinned.BakeMesh(s_bakedMesh, true);
                mesh = s_bakedMesh;
                // BakeMesh with useScale already applies the transform's scale.
                matrix = Matrix4x4.TRS(go.transform.position, go.transform.rotation, Vector3.one);
            }
            else if (go.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
            {
                mesh = filter.sharedMesh;
                matrix = go.transform.localToWorldMatrix;
            }
            else
            {
                return null;
            }

            if (mesh.vertexCount == 0) return null;

            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);

            using var _ = ListPool<int>.Get(out var triangles);
            using var __ = ListPool<int>.Get(out var subMeshTriangles);
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) != MeshTopology.Triangles) continue;
                mesh.GetTriangles(subMeshTriangles, i);
                triangles.AddRange(subMeshTriangles);
            }
            if (triangles.Count == 0) return null;

            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var v in vertices)
                bounds.Encapsulate(v);

            return new Shape
            {
                mode = DistanceMode.Mesh,
                bounds = bounds,
                vertices = vertices,
                triangles = triangles.ToArray()
            };
        }

        static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
        {
            var result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
            Vector3 e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = local.center + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                result.Encapsulate(matrix.MultiplyPoint3x4(corner));
            }
            return result;
        }

        TriangleGrid GetGrid(float distance)
        {
            if (_grid == null || _gridDistance != distance)
            {
                Vector3 size = bounds.size;
                float largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                _grid = new TriangleGrid(vertices, triangles, Mathf.Max(distance, largest / k_MaxGridCellsPerAxis, 1e-4f));
                _gridDistance = distance;
            }
            return _grid;
        }

        bool PointNearSurface(Vector3 p, float distance)
        {
            float sqrDistance = distance * distance;
            if (bounds.SqrDistance(p) > sqrDistance) return false;
            return GetGrid(distance).AnyWithin(p, sqrDistance, vertices, triangles);
        }

        /// <summary>
        /// True when the two shapes are within <paramref name="distance"/>. Mesh distance is
        /// measured from each mesh's vertices to the other's triangles (both ways), so two
        /// surfaces that cross without any vertex near the other surface are missed.
        /// </summary>
        public static bool Within(Shape a, Shape b, float distance)
        {
            if (a.mode > b.mode)
                (a, b) = (b, a);

            float sqrDistance = distance * distance;
            switch (a.mode, b.mode)
            {
                case (DistanceMode.Pivot, DistanceMode.Pivot):
                    return (a.point - b.point).sqrMagnitude <= sqrDistance;
                case (DistanceMode.Pivot, DistanceMode.BoundingBox):
                    return b.bounds.SqrDistance(a.point) <= sqrDistance;
                case (DistanceMode.BoundingBox, DistanceMode.BoundingBox):
                    return SqrGap(a.bounds, b.bounds) <= sqrDistance;
                case (DistanceMode.Pivot, DistanceMode.Mesh):
                    return b.PointNearSurface(a.point, distance);
                case (DistanceMode.BoundingBox, DistanceMode.Mesh):
                    foreach (var v in b.vertices)
                    {
                        if (a.bounds.SqrDistance(v) <= sqrDistance) return true;
                    }
                    Vector3 min = a.bounds.min;
                    Vector3 max = a.bounds.max;
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new Vector3(
                            (i & 1) == 0 ? min.x : max.x,
                            (i & 2) == 0 ? min.y : max.y,
                            (i & 4) == 0 ? min.z : max.z);
                        if (b.PointNearSurface(corner, distance)) return true;
                    }
                    return false;
                default:
                    return VerticesNearSurface(a, b, distance) || VerticesNearSurface(b, a, distance);
            }
        }

        static bool VerticesNearSurface(Shape vertexSource, Shape surface, float distance)
        {
            foreach (var v in vertexSource.vertices)
            {
                if (surface.PointNearSurface(v, distance)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Uniform grid of triangle indices. The cell size is at least the query distance, so any
    /// triangle within that distance of a point lies in the point's cell or its 26 neighbours.
    /// </summary>
    class TriangleGrid
    {
        readonly float _cellSize;
        readonly Dictionary<Vector3Int, List<int>> _cells = new Dictionary<Vector3Int, List<int>>();

        public TriangleGrid(Vector3[] vertices, int[] triangles, float cellSize)
        {
            _cellSize = cellSize;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]];
                Vector3 b = vertices[triangles[t + 1]];
                Vector3 c = vertices[triangles[t + 2]];
                Vector3Int min = Cell(Vector3.Min(a, Vector3.Min(b, c)));
                Vector3Int max = Cell(Vector3.Max(a, Vector3.Max(b, c)));
                for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                {
                    var key = new Vector3Int(x, y, z);
                    if (!_cells.TryGetValue(key, out var list))
                    {
                        list = new List<int>(4);
                        _cells[key] = list;
                    }
                    list.Add(t);
                }
            }
        }

        Vector3Int Cell(Vector3 p) => new Vector3Int(
            Mathf.FloorToInt(p.x / _cellSize),
            Mathf.FloorToInt(p.y / _cellSize),
            Mathf.FloorToInt(p.z / _cellSize));

        public bool AnyWithin(Vector3 p, float sqrDistance, Vector3[] vertices, int[] triangles)
        {
            Vector3Int center = Cell(p);
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (!_cells.TryGetValue(new Vector3Int(center.x + x, center.y + y, center.z + z), out var list))
                    continue;
                foreach (int t in list)
                {
                    Vector3 closest = ClosestPointOnTriangle(p,
                        vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]);
                    if ((closest - p).sqrMagnitude <= sqrDistance) return true;
                }
            }
            return false;
        }

        /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
        static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
}

/// <summary>
/// Floating Scene View panel with the Distance Select settings. Opens when Distance Select
/// is activated and stays open for repeated Ctrl+= presses.
/// </summary>
[Overlay(typeof(SceneView), k_Id, "Distance Select")]
class DistanceSelectOverlay : Overlay
{
    public const string k_Id = "distance-select";

    static readonly GUIContent k_From = new GUIContent("From",
        "What distance is measured from on the selected objects: pivot, world bounding box (Renderer bounds), or mesh surface.");
    static readonly GUIContent k_To = new GUIContent("To",
        "What distance is measured to on the other objects: pivot, world bounding box (Renderer bounds), or mesh surface. " +
        "Objects without a renderer / mesh are skipped for Bounding Box / Mesh.");
    static readonly GUIContent k_Interval = new GUIContent("Interval",
        "Search distance in meters added per activation.");
    static readonly GUIContent k_Aggregate = new GUIContent("Aggregate",
        "On: each activation searches one interval from the whole current selection, so the selection grows outward.\n" +
        "Off: each activation searches from the original selection, one interval further than the last.");
    static readonly GUIContent k_EntirePrefabs = new GUIContent("Entire Prefabs",
        "When a found object is part of a prefab instance, select the outermost prefab instance root instead. " +
        "Prefabs that contain any of the objects the search started from are not used; their parts are selected individually. " +
        "Imported model prefabs (FBX etc.) are ignored.");
    static readonly GUIContent k_Select = new GUIContent("Select (Ctrl+=)",
        "Add the objects within the next distance interval to the selection.");
    static readonly GUIContent k_ResetSelection = new GUIContent("Reset Selection",
        "Select only the objects the search started from, dropping everything Distance Select added. " +
        "The next activation starts a new search from them.");
    static readonly GUIContent k_Close = new GUIContent("Close", "Hide this panel.");

    IMGUIContainer _content;

    // ─── Lifecycle ──────────────────────────────────────────────

    public override VisualElement CreatePanelContent()
    {
        var root = new VisualElement { style = { minWidth = 240 } };
        _content = new IMGUIContainer(DrawGUI);
        root.Add(_content);
        return root;
    }

    public override void OnCreated()
    {
        DistanceSelect.s_onShownChanged += UpdateVisibility;
        DistanceSelect.s_onStateChanged += Repaint;
        Selection.selectionChanged += Repaint;
        UpdateVisibility();
    }

    public override void OnWillBeDestroyed()
    {
        Selection.selectionChanged -= Repaint;
        DistanceSelect.s_onStateChanged -= Repaint;
        DistanceSelect.s_onShownChanged -= UpdateVisibility;
    }

    void UpdateVisibility() => displayed = DistanceSelect.Shown;

    void Repaint() => _content?.MarkDirtyRepaint();

    // ─── GUI ────────────────────────────────────────────────────

    static void DrawGUI()
    {
        float labelWidth = EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth = 64;

        DistanceSelect.From = (DistanceSelect.DistanceMode)EditorGUILayout.EnumPopup(k_From, DistanceSelect.From);
        DistanceSelect.To = (DistanceSelect.DistanceMode)EditorGUILayout.EnumPopup(k_To, DistanceSelect.To);
        DistanceSelect.Interval = Mathf.Max(DistanceSelect.k_MinInterval,
            EditorGUILayout.FloatField(k_Interval, DistanceSelect.Interval));
        DistanceSelect.Aggregate = EditorGUILayout.Toggle(k_Aggregate, DistanceSelect.Aggregate);
        EditorGUIUtility.labelWidth = 96;
        DistanceSelect.EntirePrefabs = EditorGUILayout.Toggle(k_EntirePrefabs, DistanceSelect.EntirePrefabs);

        EditorGUIUtility.labelWidth = labelWidth;

        int step = DistanceSelect.CurrentStep;
        string next = DistanceSelect.Aggregate
            ? string.Format("Next: {0:0.###} from the selection", DistanceSelect.Interval)
            : string.Format("Next: {0:0.###} from the original selection", DistanceSelect.Interval * (step + 1));
        EditorGUILayout.LabelField(next, EditorStyles.miniLabel);
        if (!string.IsNullOrEmpty(DistanceSelect.Status))
            EditorGUILayout.LabelField(DistanceSelect.Status, EditorStyles.wordWrappedMiniLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
            {
                if (GUILayout.Button(k_Select, GUILayout.Height(22)))
                    DistanceSelect.Activate();
            }
            if (GUILayout.Button(k_Close, GUILayout.Width(52), GUILayout.Height(22)))
                DistanceSelect.Shown = false;
        }

        using (new EditorGUI.DisabledScope(!DistanceSelect.CanResetSelection))
        {
            if (GUILayout.Button(k_ResetSelection, GUILayout.Height(20)))
                DistanceSelect.ResetSelection();
        }
    }
}
#endif
