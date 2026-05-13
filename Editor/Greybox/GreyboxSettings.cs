#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

/// <summary>
/// Per-project EditorPrefs-backed settings for the Greybox tool.
/// Stores the default material assigned to newly created Greyboxes.
/// </summary>
static class GreyboxSettings
{
    const string k_MatGuidPref      = "Editools_Greybox_DefaultMatGUID";
    const string k_ScaleXPref       = "Editools_Greybox_DefaultScaleX";
    const string k_ScaleYPref       = "Editools_Greybox_DefaultScaleY";
    const string k_ScaleZPref       = "Editools_Greybox_DefaultScaleZ";
    const string k_StaticPref       = "Editools_Greybox_DefaultStatic";
    const string k_LayerPref        = "Editools_Greybox_DefaultLayer";
    const string k_MeshColliderPref = "Editools_Greybox_DefaultMeshCollider";

    /// <summary>Per-project default material for new Greyboxes. Null = built-in grey.</summary>
    internal static Material DefaultMaterial
    {
        get
        {
            string guid = EditorPrefs.GetString(k_MatGuidPref, "");
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }
        set
        {
            if (value != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long _))
                EditorPrefs.SetString(k_MatGuidPref, guid);
            else
                EditorPrefs.DeleteKey(k_MatGuidPref);
        }
    }

    /// <summary>Per-project default scale for new Greyboxes. Default 10×10×10.</summary>
    internal static Vector3 DefaultScale
    {
        get => new Vector3(
            EditorPrefs.GetFloat(k_ScaleXPref, 10f),
            EditorPrefs.GetFloat(k_ScaleYPref, 10f),
            EditorPrefs.GetFloat(k_ScaleZPref, 10f));
        set
        {
            EditorPrefs.SetFloat(k_ScaleXPref, value.x);
            EditorPrefs.SetFloat(k_ScaleYPref, value.y);
            EditorPrefs.SetFloat(k_ScaleZPref, value.z);
        }
    }

    /// <summary>Whether new Greyboxes are marked as static. Default true.</summary>
    internal static bool DefaultStatic
    {
        get => EditorPrefs.GetBool(k_StaticPref, true);
        set => EditorPrefs.SetBool(k_StaticPref, value);
    }

    /// <summary>Layer index applied to new Greyboxes. Default 0 (Default layer).</summary>
    internal static int DefaultLayer
    {
        get => EditorPrefs.GetInt(k_LayerPref, 0);
        set => EditorPrefs.SetInt(k_LayerPref, value);
    }

    /// <summary>Whether new Greyboxes get a MeshCollider component. Default true.</summary>
    internal static bool DefaultMeshCollider
    {
        get => EditorPrefs.GetBool(k_MeshColliderPref, true);
        set => EditorPrefs.SetBool(k_MeshColliderPref, value);
    }

    /// <summary>
    /// Returns the configured default material, or a built-in grey Standard fallback.
    /// The fallback is not cached — only used at Greybox creation time.
    /// </summary>
    internal static Material GetOrCreateDefaultMaterial()
    {
        var mat = DefaultMaterial;
        if (mat != null) return mat;

        return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
    }

    // ─── Menu item ───────────────────────────────────────────────

    [MenuItem("GameObject/3D Object/Greybox", false, 10)]
    static void CreateGreybox(MenuCommand cmd)
    {
        var go = new GameObject("Greybox");
        GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create Greybox");

        go.AddComponent<Greybox>();
        if (DefaultMeshCollider) go.AddComponent<MeshCollider>();

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
            mr.sharedMaterial = GetOrCreateDefaultMaterial();

        go.transform.localScale = DefaultScale;
        go.isStatic   = DefaultStatic;
        go.layer      = DefaultLayer;

        Selection.activeObject = go;
        SceneView.lastActiveSceneView?.MoveToView(go.transform);
    }

    [MenuItem("GameObject/3D Object/Greybox", true)]
    static bool CreateGreyboxValidate() => true;

    // ─── Shared placement helper (used by menu item and shortcut) ──

    /// <summary>
    /// Creates a Greybox at the given world position/rotation, wires up material,
    /// static flag, and Ground layer. Registers full undo.
    /// </summary>
    internal static GameObject PlaceGreybox(Vector3 worldPos, Quaternion worldRot, Transform parent)
    {
        var go = new GameObject("Greybox");
        if (parent != null)
            go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create Greybox");

        go.AddComponent<Greybox>();
        if (DefaultMeshCollider) go.AddComponent<MeshCollider>();

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
            mr.sharedMaterial = GetOrCreateDefaultMaterial();

        go.transform.position   = worldPos;
        go.transform.rotation   = worldRot;
        go.transform.localScale = DefaultScale;

        go.isStatic = DefaultStatic;
        go.layer    = DefaultLayer;

        Selection.activeObject = go;
        return go;
    }
}

// ─── Settings popup ──────────────────────────────────────────────────────────

/// <summary>
/// Popup for Greybox settings, accessible from the Editools Settings panel.
/// Currently exposes only the default material for new Greyboxes.
/// </summary>
class GreyboxSettingsPopup : PopupWindowContent
{
    static readonly GUIContent k_MatLabel = new GUIContent(
        "Default Material",
        "Material applied to new Greyboxes created via menu or Ctrl+G. " +
        "Leave empty to use the built-in grey fallback.");

    static readonly GUIContent k_ScaleLabel = new GUIContent(
        "Default Scale",
        "Local scale applied to new Greyboxes on creation. " +
        "The mesh corners define a 1×1×1 unit cube, so a scale of 10 creates a 10×10×10 box.");

    static readonly GUIContent k_StaticLabel = new GUIContent(
        "Static",
        "Mark new Greyboxes as static. Enables lightmap baking and occlusion culling contributions.");

    static readonly GUIContent k_LayerLabel = new GUIContent(
        "Default Layer",
        "Layer assigned to new Greyboxes on creation.");

    static readonly GUIContent k_MeshColliderLabel = new GUIContent(
        "Mesh Collider",
        "Add a MeshCollider component to new Greyboxes. Disable for purely visual blockout pieces.");

    public override Vector2 GetWindowSize() => new Vector2(260, 150);

    public override void OnGUI(Rect rect)
    {
        var current = GreyboxSettings.DefaultMaterial;
        var next = (Material)EditorGUILayout.ObjectField(k_MatLabel, current, typeof(Material), false);
        if (next != current)
            GreyboxSettings.DefaultMaterial = next;

        EditorGUILayout.Space(4);

        var curScale  = GreyboxSettings.DefaultScale;
        var nextScale = EditorGUILayout.Vector3Field(k_ScaleLabel, curScale);
        if (nextScale != curScale)
            GreyboxSettings.DefaultScale = nextScale;

        EditorGUILayout.Space(4);

        bool curStatic = GreyboxSettings.DefaultStatic;
        bool nextStatic = EditorGUILayout.Toggle(k_StaticLabel, curStatic);
        if (nextStatic != curStatic)
            GreyboxSettings.DefaultStatic = nextStatic;

        EditorGUILayout.Space(4);

        int curLayer  = GreyboxSettings.DefaultLayer;
        int nextLayer = EditorGUILayout.LayerField(k_LayerLabel, curLayer);
        if (nextLayer != curLayer)
            GreyboxSettings.DefaultLayer = nextLayer;

        EditorGUILayout.Space(4);

        bool curCollider  = GreyboxSettings.DefaultMeshCollider;
        bool nextCollider = EditorGUILayout.Toggle(k_MeshColliderLabel, curCollider);
        if (nextCollider != curCollider)
            GreyboxSettings.DefaultMeshCollider = nextCollider;
    }
}

// ─── Ctrl+G shortcut — create Greybox at surface under cursor ────────────────

/// <summary>
/// Tracks the scene-view mouse ray each frame so it's available when the
/// Ctrl+G shortcut fires (outside of OnGUI scope).
/// </summary>
[InitializeOnLoad]
static class GreyboxCreationShortcut
{
    static Ray      s_ray;
    static bool     s_hasRay;
    static SceneView s_view;

    static GreyboxCreationShortcut()
    {
        SceneView.duringSceneGui += Track;
    }

    static void Track(SceneView sv)
    {
        var e = Event.current;
        if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
        {
            s_ray    = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            s_hasRay = true;
            s_view   = sv;
        }
    }

    /// <summary>
    /// Ctrl+G in Scene View — fires a ray against all scene geometry and places
    /// a Greybox with its pivot on the hit surface, Y-axis aligned to the normal.
    /// Falls back to view-pivot depth if no surface is hit.
    /// </summary>
    [Shortcut("Editools/Create Greybox", typeof(SceneView), KeyCode.G, ShortcutModifiers.Control)]
    static void Execute()
    {
        Vector3    pos;
        Quaternion rot;

        if (s_hasRay && s_view != null && CastRayToScene(s_ray, out Vector3 hitPt, out Vector3 hitNorm))
        {
            pos = hitPt;
            rot = NormalToRotation(hitNorm);
        }
        else
        {
            // Fallback: place at cursor depth aligned to view pivot
            var sv = s_view ?? SceneView.lastActiveSceneView;
            if (sv != null && s_hasRay)
            {
                float depth = Vector3.Dot(sv.pivot - s_ray.origin, s_ray.direction);
                pos = s_ray.GetPoint(Mathf.Max(depth, 0.5f));
            }
            else if (sv != null)
            {
                pos = sv.pivot;
            }
            else
            {
                pos = Vector3.zero;
            }
            rot = Quaternion.identity;
        }

        // Place as sibling of the currently selected object (same parent in hierarchy)
        Transform selectedParent = Selection.activeTransform?.parent;
        GreyboxSettings.PlaceGreybox(pos, rot, selectedParent);
    }

    // ─── Surface raycasting ──────────────────────────────────────
    // Möller–Trumbore triangle intersection against all scene MeshFilters.
    // Mirrors the approach in SnapToSurface but operates as a one-shot cast.

    static bool CastRayToScene(Ray ray, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitPoint  = Vector3.zero;
        hitNormal = Vector3.up;

        float closest = float.MaxValue;
        bool  found   = false;

        foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (mf.sharedMesh == null) continue;
            if (mf.GetComponent<Greybox>() != null) continue; // skip existing greyboxes
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled) continue;

            if (RaycastMesh(ray, mf, out Vector3 p, out Vector3 n, out float d) && d < closest)
            {
                closest   = d;
                hitPoint  = p;
                hitNormal = n;
                found     = true;
            }
        }
        return found;
    }

    static bool RaycastMesh(Ray ray, MeshFilter mf, out Vector3 hitPoint, out Vector3 hitNormal, out float hitDist)
    {
        hitPoint  = Vector3.zero;
        hitNormal = Vector3.up;
        hitDist   = float.MaxValue;

        var mesh  = mf.sharedMesh;
        var tr    = mf.transform;
        var verts = mesh.vertices;
        var tris  = mesh.triangles;
        var norms = mesh.normals;

        float closestT   = float.MaxValue;
        int   closestTri = -1;

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 v0 = tr.TransformPoint(verts[tris[i]]);
            Vector3 v1 = tr.TransformPoint(verts[tris[i + 1]]);
            Vector3 v2 = tr.TransformPoint(verts[tris[i + 2]]);

            if (MollerTrumbore(ray, v0, v1, v2, out float t) && t < closestT)
            {
                closestT   = t;
                closestTri = i;
            }
        }

        if (closestTri < 0) return false;

        hitDist  = closestT;
        hitPoint = ray.GetPoint(closestT);

        // Interpolated world-space normal
        Vector3 wv0 = tr.TransformPoint(verts[tris[closestTri]]);
        Vector3 wv1 = tr.TransformPoint(verts[tris[closestTri + 1]]);
        Vector3 wv2 = tr.TransformPoint(verts[tris[closestTri + 2]]);
        Vector3 bary = Barycentric(hitPoint, wv0, wv1, wv2);

        Vector3 n0 = tr.TransformDirection(norms[tris[closestTri]]);
        Vector3 n1 = tr.TransformDirection(norms[tris[closestTri + 1]]);
        Vector3 n2 = tr.TransformDirection(norms[tris[closestTri + 2]]);
        hitNormal  = (n0 * bary.x + n1 * bary.y + n2 * bary.z).normalized;

        return true;
    }

    static bool MollerTrumbore(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        Vector3 e1 = v1 - v0, e2 = v2 - v0;
        Vector3 h  = Vector3.Cross(ray.direction, e2);
        float   a  = Vector3.Dot(e1, h);
        if (a > -1e-5f && a < 1e-5f) return false;
        float   f  = 1f / a;
        Vector3 s  = ray.origin - v0;
        float   u  = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;
        Vector3 q  = Vector3.Cross(s, e1);
        float   v  = f * Vector3.Dot(ray.direction, q);
        if (v < 0f || u + v > 1f) return false;
        t = f * Vector3.Dot(e2, q);
        return t > 1e-5f;
    }

    static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1);
        float d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        float den = d00 * d11 - d01 * d01;
        float bv  = (d11 * d20 - d01 * d21) / den;
        float bw  = (d00 * d21 - d01 * d20) / den;
        return new Vector3(1f - bv - bw, bv, bw);
    }

    // Align object Y-axis to surface normal, forward derived from world up
    static Quaternion NormalToRotation(Vector3 normal)
    {
        Vector3 up = normal;
        Vector3 fwd;
        if (Vector3.Dot(up, Vector3.up) > 0.99f)
            fwd = Vector3.forward;
        else if (Vector3.Dot(up, Vector3.up) < -0.99f)
            fwd = Vector3.back;
        else
            fwd = Vector3.ProjectOnPlane(Vector3.up, up).normalized;
        return Quaternion.LookRotation(fwd, up);
    }
}
#endif
