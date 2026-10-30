using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Rendering;

/// <summary>
/// Scene View vertex counter: vertices sent to the GPU for the whole scene, for the
/// Scene View frustum, and for the selection. Counts per draw call (one per material slot,
/// using the submesh's vertex range), only for renderers that actually draw: enabled,
/// not Shadows Only, and in the LOD the Scene View camera shows.
/// Drawn under the FPS counter and into Editools scene captures.
/// Gated by Editools Settings.
/// </summary>
static class SceneViewVertexCounter
{
    private const double k_RecountInterval = 0.1;
    private const float k_Margin = 6f;
    private const float k_RowSpacing = 2f;
    private const int k_FontSize = 12;
    private const int k_LodFilterOff = -2;
    private const int k_LodCulled = -1;

    static readonly Color k_BackgroundColor = new Color(0f, 0f, 0f, 0.55f);

    internal static bool Enabled => EditoolsSettings.Current.vertexCounterEnabled;

    /// <summary>Whether the counter is drawn. Flipped by the toolbar toggle / settings menu row.</summary>
    internal static bool Shown
    {
        get => EditoolsSettings.Current.vertexCounterShown;
        set
        {
            EditoolsSettings.SetBool(ref EditoolsSettings.Current.vertexCounterShown, value, "Toggle Vertex Counter");
            Sync();
        }
    }

    /// <summary>True while the counter draws: enabled, shown and the overlay is active.</summary>
    internal static bool IsDrawing => EditoolsOverlay.IsActive && Enabled && Shown;

    /// <summary>Drops the change-tracking subscriptions when not drawing; drawing re-subscribes.</summary>
    internal static void Sync()
    {
        if (!IsDrawing) Unsubscribe();
        SceneView.RepaintAll();
    }

    class ViewState
    {
        public readonly GUIContent label = new GUIContent();
        public Matrix4x4 viewProjection;
        public int version = -1;
        public double lastCountTime = double.MinValue;
    }

    struct LodGroupEntry
    {
        public LODGroup group;
        public float[] transitions;
    }

    struct LodMembership
    {
        public int groupIndex;
        public int lodMask;
    }

    static readonly Dictionary<int, ViewState> s_perView = new Dictionary<int, ViewState>();
    static readonly List<Renderer> s_renderers = new List<Renderer>(1024);
    static readonly List<LodGroupEntry> s_lodGroups = new List<LodGroupEntry>(64);
    // Renderer instance ID -> the LODGroup it belongs to and the LOD levels it appears in.
    static readonly Dictionary<int, LodMembership> s_lodMembership = new Dictionary<int, LodMembership>(256);
    static readonly List<int> s_activeLods = new List<int>(64);
    static readonly List<Material> s_materials = new List<Material>(8);
    static readonly Plane[] s_frustumPlanes = new Plane[6];

    static bool s_subscribed;
    static bool s_renderersDirty = true;
    static int s_version;

    static GUIStyle s_style;
    static Material s_colorMaterial;

    // ---- Scene GUI ----

    /// <summary>Called from <see cref="EditoolsOverlay"/>'s scene GUI while the overlay is displayed.</summary>
    internal static void OnSceneGUI(SceneView sceneView)
    {
        if (!IsDrawing) return;
        if (Event.current.type != EventType.Repaint) return;

        Subscribe();
        var state = GetState(sceneView);
        UpdateCounts(sceneView, state, false);
        EnsureStyle();

        Handles.BeginGUI();

        var size = s_style.CalcSize(state.label);
        float y = k_Margin;
        if (SceneViewFpsCounter.Enabled && SceneViewFpsCounter.Shown)
            y += size.y + 2f + k_RowSpacing;

        EditorGUI.DrawRect(new Rect(k_Margin, y, size.x + 2, size.y + 2), k_BackgroundColor);
        GUI.Label(new Rect(k_Margin + 1, y, size.x, size.y), state.label, s_style);

        Handles.EndGUI();
    }

    /// <summary>
    /// Draws the counter into the top-left corner of a captured Scene View image.
    /// Must run during a Scene View repaint.
    /// </summary>
    internal static void DrawToCapture(SceneView sceneView, Texture2D capture)
    {
        Subscribe();
        var state = GetState(sceneView);
        UpdateCounts(sceneView, state, true);
        EnsureStyle();

        int w = capture.width;
        int h = capture.height;
        float ppp = EditorGUIUtility.pixelsPerPoint;
        int fontSize = Mathf.RoundToInt(k_FontSize * ppp);
        // A null style font means the style uses the editor default font.
        Font font = s_style.font != null ? s_style.font : EditorStyles.standardFont;
        string text = state.label.text;

        font.RequestCharactersInTexture(text, fontSize, FontStyle.Bold);

        float textWidth = 0f;
        int maxY = 0;
        int minY = 0;
        foreach (char c in text)
        {
            if (!font.GetCharacterInfo(c, out var ci, fontSize, FontStyle.Bold)) continue;
            textWidth += ci.advance;
            maxY = Mathf.Max(maxY, ci.maxY);
            minY = Mathf.Min(minY, ci.minY);
        }

        float padX = 4f * ppp;
        float padY = 2f * ppp;
        float left = k_Margin * ppp;
        float top = k_Margin * ppp;
        float bgWidth = textWidth + padX * 2f;
        float bgHeight = (maxY - minY) + padY * 2f;

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.Default);
        var prevActive = RenderTexture.active;
        Graphics.Blit(capture, rt);
        RenderTexture.active = rt;

        GL.PushMatrix();
        GL.LoadPixelMatrix(0, w, h, 0);

        EnsureColorMaterial();
        s_colorMaterial.SetPass(0);
        GL.Begin(GL.QUADS);
        GL.Color(k_BackgroundColor);
        GL.Vertex3(left, top, 0f);
        GL.Vertex3(left + bgWidth, top, 0f);
        GL.Vertex3(left + bgWidth, top + bgHeight, 0f);
        GL.Vertex3(left, top + bgHeight, 0f);
        GL.End();

        font.material.SetPass(0);
        GL.Begin(GL.QUADS);
        GL.Color(Color.white);
        float penX = left + padX;
        float baseline = top + padY + maxY;
        foreach (char c in text)
        {
            if (!font.GetCharacterInfo(c, out var ci, fontSize, FontStyle.Bold)) continue;
            float x0 = penX + ci.minX;
            float x1 = penX + ci.maxX;
            float y0 = baseline - ci.maxY;
            float y1 = baseline - ci.minY;
            GL.TexCoord(ci.uvTopLeft);
            GL.Vertex3(x0, y0, 0f);
            GL.TexCoord(ci.uvTopRight);
            GL.Vertex3(x1, y0, 0f);
            GL.TexCoord(ci.uvBottomRight);
            GL.Vertex3(x1, y1, 0f);
            GL.TexCoord(ci.uvBottomLeft);
            GL.Vertex3(x0, y1, 0f);
            penX += ci.advance;
        }
        GL.End();

        GL.PopMatrix();

        capture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        capture.Apply();

        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
    }

    // ---- Counting ----

    static ViewState GetState(SceneView sceneView)
    {
        int id = sceneView.GetInstanceID();
        if (!s_perView.TryGetValue(id, out var state))
        {
            state = new ViewState();
            s_perView[id] = state;
        }
        return state;
    }

    /// <summary>
    /// Recounts when the scene, selection or camera changed. Camera-driven recounts
    /// are throttled to <see cref="k_RecountInterval"/> unless forced.
    /// </summary>
    static void UpdateCounts(SceneView sceneView, ViewState state, bool force)
    {
        Camera cam = sceneView.camera;
        Matrix4x4 viewProjection = cam.projectionMatrix * cam.worldToCameraMatrix;
        if (state.version == s_version && state.viewProjection == viewProjection) return;

        double now = EditorApplication.timeSinceStartup;
        if (!force && now - state.lastCountTime < k_RecountInterval)
        {
            sceneView.Repaint();
            return;
        }

        if (s_renderersDirty)
            RebuildRenderers();

        Count(cam, out long total, out long view, out long selected);

        state.version = s_version;
        state.viewProjection = viewProjection;
        state.lastCountTime = now;
        state.label.text = string.Format("Verts  {0:N0} total | {1:N0} view | {2:N0} selected", total, view, selected);
    }

    static void Count(Camera cam, out long total, out long view, out long selected)
    {
        GeometryUtility.CalculateFrustumPlanes(cam, s_frustumPlanes);

        s_activeLods.Clear();
        foreach (var entry in s_lodGroups)
        {
            bool filters = entry.group.enabled && entry.group.gameObject.activeInHierarchy;
            s_activeLods.Add(filters ? GetActiveLodIndex(entry, cam) : k_LodFilterOff);
        }

        using var _ = HashSetPool<int>.Get(out var selectedIds);
        using var __ = ListPool<Renderer>.Get(out var childRenderers);
        foreach (Transform t in Selection.transforms)
        {
            t.GetComponentsInChildren(false, childRenderers);
            foreach (var r in childRenderers)
                selectedIds.Add(r.GetInstanceID());
        }

        var visibility = SceneVisibilityManager.instance;
        total = 0;
        view = 0;
        selected = 0;

        foreach (var r in s_renderers)
        {
            if (!IsDrawn(r)) continue;

            long count = GetDrawnVertexCount(r);
            if (count == 0) continue;

            total += count;
            if (selectedIds.Contains(r.GetInstanceID()))
                selected += count;
            if (!visibility.IsHidden(r.gameObject) && GeometryUtility.TestPlanesAABB(s_frustumPlanes, r.bounds))
                view += count;
        }
    }

    static bool IsDrawn(Renderer r)
    {
        if (!r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy) return false;
        if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return false;

        if (!s_lodMembership.TryGetValue(r.GetInstanceID(), out var membership)) return true;
        int activeLod = s_activeLods[membership.groupIndex];
        if (activeLod == k_LodFilterOff) return true;
        return activeLod >= 0 && (membership.lodMask & (1 << activeLod)) != 0;
    }

    /// <summary>
    /// Vertices processed by the vertex shader for one renderer: one draw per material slot,
    /// each covering its submesh's vertex range. Slots beyond the submesh count redraw the last submesh.
    /// Mesh vertex counts already include splits from hard edges and UV seams.
    /// </summary>
    static long GetDrawnVertexCount(Renderer r)
    {
        Mesh mesh;
        if (r is SkinnedMeshRenderer skinned)
            mesh = skinned.sharedMesh;
        else if (r is MeshRenderer && r.TryGetComponent(out MeshFilter filter))
            mesh = filter.sharedMesh;
        else
            return 0;

        // A renderer without a mesh issues no draw.
        if (mesh == null) return 0;

        int subMeshCount = mesh.subMeshCount;
        if (subMeshCount == 0) return 0;

        r.GetSharedMaterials(s_materials);
        long count = 0;
        for (int i = 0; i < s_materials.Count; i++)
            count += mesh.GetSubMesh(Mathf.Min(i, subMeshCount - 1)).vertexCount;
        return count;
    }

    /// <summary>
    /// LOD index the camera selects, matching Unity's screen-relative-height rule
    /// (LOD bias and maximum LOD level from Quality Settings). <see cref="k_LodCulled"/> when culled.
    /// </summary>
    static int GetActiveLodIndex(LodGroupEntry entry, Camera cam)
    {
        LODGroup group = entry.group;
        Transform t = group.transform;
        Vector3 scale = t.lossyScale;
        float worldSize = group.size * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

        float relativeHeight;
        if (cam.orthographic)
        {
            relativeHeight = worldSize * 0.5f / cam.orthographicSize;
        }
        else
        {
            float distance = Vector3.Distance(t.TransformPoint(group.localReferencePoint), cam.transform.position);
            relativeHeight = worldSize * 0.5f / (distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
        }
        relativeHeight *= QualitySettings.lodBias;

        var transitions = entry.transitions;
        for (int i = QualitySettings.maximumLODLevel; i < transitions.Length; i++)
        {
            if (relativeHeight >= transitions[i])
                return i;
        }
        return k_LodCulled;
    }

    static void RebuildRenderers()
    {
        s_renderersDirty = false;
        s_renderers.Clear();
        s_lodGroups.Clear();
        s_lodMembership.Clear();

        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null)
        {
            stage.prefabContentsRoot.GetComponentsInChildren(false, s_renderers);
            using var _ = ListPool<LODGroup>.Get(out var groups);
            stage.prefabContentsRoot.GetComponentsInChildren(false, groups);
            foreach (var group in groups)
                AddLodGroup(group);
        }
        else
        {
            s_renderers.AddRange(Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            foreach (var group in Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                AddLodGroup(group);
        }
    }

    static void AddLodGroup(LODGroup group)
    {
        LOD[] lods = group.GetLODs();
        int groupIndex = s_lodGroups.Count;
        var transitions = new float[lods.Length];

        for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
        {
            transitions[lodIndex] = lods[lodIndex].screenRelativeTransitionHeight;
            foreach (var r in lods[lodIndex].renderers)
            {
                // Empty slots in the LOD renderer list are allowed.
                if (r == null) continue;
                int id = r.GetInstanceID();
                s_lodMembership.TryGetValue(id, out var membership);
                membership.groupIndex = groupIndex;
                membership.lodMask |= 1 << lodIndex;
                s_lodMembership[id] = membership;
            }
        }

        s_lodGroups.Add(new LodGroupEntry { group = group, transitions = transitions });
    }

    // ---- Change tracking ----

    static void Subscribe()
    {
        if (s_subscribed) return;
        s_subscribed = true;
        s_renderersDirty = true;
        s_version++;

        EditorApplication.hierarchyChanged += MarkRenderersDirty;
        ObjectChangeEvents.changesPublished += OnChangesPublished;
        Undo.undoRedoPerformed += MarkRenderersDirty;
        Selection.selectionChanged += MarkCountsDirty;
        PrefabStage.prefabStageOpened += OnPrefabStageChanged;
        PrefabStage.prefabStageClosing += OnPrefabStageChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    static void Unsubscribe()
    {
        if (!s_subscribed) return;
        s_subscribed = false;

        EditorApplication.hierarchyChanged -= MarkRenderersDirty;
        ObjectChangeEvents.changesPublished -= OnChangesPublished;
        Undo.undoRedoPerformed -= MarkRenderersDirty;
        Selection.selectionChanged -= MarkCountsDirty;
        PrefabStage.prefabStageOpened -= OnPrefabStageChanged;
        PrefabStage.prefabStageClosing -= OnPrefabStageChanged;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

        s_renderers.Clear();
        s_lodGroups.Clear();
        s_lodMembership.Clear();
        s_perView.Clear();
    }

    static void OnChangesPublished(ref ObjectChangeEventStream stream) => MarkRenderersDirty();
    static void OnPrefabStageChanged(PrefabStage stage) => MarkRenderersDirty();
    static void OnPlayModeStateChanged(PlayModeStateChange change) => MarkRenderersDirty();

    static void MarkRenderersDirty()
    {
        s_renderersDirty = true;
        MarkCountsDirty();
    }

    static void MarkCountsDirty()
    {
        s_version++;
        SceneView.RepaintAll();
    }

    // ---- Styles ----

    static void EnsureStyle()
    {
        if (s_style != null) return;
        s_style = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = k_FontSize,
            normal = { textColor = Color.white },
            padding = new RectOffset(4, 4, 2, 2)
        };
    }

    static void EnsureColorMaterial()
    {
        if (s_colorMaterial != null) return;
        s_colorMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        s_colorMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        s_colorMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        s_colorMaterial.SetInt("_Cull", (int)CullMode.Off);
        s_colorMaterial.SetInt("_ZWrite", 0);
        s_colorMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
    }
}
