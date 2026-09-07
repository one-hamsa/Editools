#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

/// <summary>
/// Select And Replace Material - selects scene renderers (or swaps their materials) by material
/// identity and material properties. Matching is per material slot: a slot passes when its material
/// is in the Materials list or uses a shader in the Shaders list (both lists empty = any material),
/// AND passes every property filter (surface type, Z write, render queue range). An object is
/// selected when any of its slots passes; Replace swaps exactly the slots that passed.
/// State persists in EditorPrefs. The UI is <see cref="SelectByMaterialOverlay"/>, a floating
/// Scene View panel toggled from the Editools toolbar.
/// </summary>
static class SelectByMaterial
{
    internal enum ZWriteFilter { Any, On, Off }

    const string k_Pref            = "Editools_SelectByMat_";
    const string k_Shown           = k_Pref + "PanelShown";
    const string k_Materials       = k_Pref + "Materials";       // newline-joined GUIDs
    const string k_Shaders         = k_Pref + "Shaders";         // newline-joined shader names
    const string k_ReplacementGuid = k_Pref + "ReplacementGUID";
    const string k_MeshRenderer    = k_Pref + "MeshRenderer";
    const string k_SkinnedMesh     = k_Pref + "SkinnedMesh";
    const string k_SpriteRenderer  = k_Pref + "SpriteRenderer";
    const string k_OnlyActive      = k_Pref + "OnlyActive";
    const string k_Opaque          = k_Pref + "Opaque";
    const string k_Transparent     = k_Pref + "Transparent";
    const string k_ZWrite          = k_Pref + "ZWrite";
    const string k_QueueRange      = k_Pref + "QueueRange";
    const string k_QueueMin        = k_Pref + "QueueMin";
    const string k_QueueMax        = k_Pref + "QueueMax";

    /// <summary>Queue at or below this is opaque, above is transparent - the split URP sorts by.</summary>
    internal const int k_OpaqueQueueMax = (int)RenderQueue.GeometryLast;

    // Editable state. The panel mutates these directly and calls Save() afterwards.
    internal static readonly List<Material> Materials = new();
    internal static readonly List<Shader> Shaders = new();
    internal static Material ReplacementMaterial;
    internal static bool MeshRenderers = true;
    internal static bool SkinnedMeshRenderers = true;
    internal static bool SpriteRenderers = true;
    internal static bool OnlyActive;
    internal static bool Opaque = true;
    internal static bool Transparent = true;
    internal static ZWriteFilter ZWrite = ZWriteFilter.Any;
    internal static bool QueueRange;
    internal static int QueueMin = 0;
    internal static int QueueMax = 5000;

    static bool s_loaded;

    /// <summary>Fired after Save(); the panel recounts matches on it.</summary>
    internal static event System.Action SettingsChanged;

    /// <summary>Fired when Shown flips; overlay instances follow it.</summary>
    internal static event System.Action ShownChanged;

    internal static bool Shown
    {
        get => EditorPrefs.GetBool(k_Shown, false);
        set
        {
            if (value == Shown) return;
            EditorPrefs.SetBool(k_Shown, value);
            ShownChanged?.Invoke();
        }
    }

    /// <summary>True when either list holds at least one non-null entry; otherwise every material matches.</summary>
    internal static bool HasIdentityFilter
    {
        get
        {
            foreach (var m in Materials) if (m != null) return true;
            foreach (var s in Shaders) if (s != null) return true;
            return false;
        }
    }

    // ─── Persistence ────────────────────────────────────────────

    internal static void EnsureLoaded()
    {
        if (s_loaded) return;
        s_loaded = true;

        Materials.Clear();
        foreach (var guid in SplitLines(EditorPrefs.GetString(k_Materials, "")))
        {
            var mat = LoadByGuid<Material>(guid);
            if (mat != null) Materials.Add(mat);
        }

        Shaders.Clear();
        foreach (var name in SplitLines(EditorPrefs.GetString(k_Shaders, "")))
        {
            var shader = Shader.Find(name);
            if (shader != null) Shaders.Add(shader);
        }

        ReplacementMaterial  = LoadByGuid<Material>(EditorPrefs.GetString(k_ReplacementGuid, ""));
        MeshRenderers        = EditorPrefs.GetBool(k_MeshRenderer, true);
        SkinnedMeshRenderers = EditorPrefs.GetBool(k_SkinnedMesh, true);
        SpriteRenderers      = EditorPrefs.GetBool(k_SpriteRenderer, true);
        OnlyActive           = EditorPrefs.GetBool(k_OnlyActive, false);
        Opaque               = EditorPrefs.GetBool(k_Opaque, true);
        Transparent          = EditorPrefs.GetBool(k_Transparent, true);
        ZWrite               = (ZWriteFilter)EditorPrefs.GetInt(k_ZWrite, 0);
        QueueRange           = EditorPrefs.GetBool(k_QueueRange, false);
        QueueMin             = EditorPrefs.GetInt(k_QueueMin, 0);
        QueueMax             = EditorPrefs.GetInt(k_QueueMax, 5000);
    }

    internal static void Save()
    {
        if (QueueMax < QueueMin) QueueMax = QueueMin;

        EditorPrefs.SetString(k_Materials, JoinGuids(Materials));
        EditorPrefs.SetString(k_Shaders, JoinShaderNames(Shaders));
        SaveGuid(k_ReplacementGuid, ReplacementMaterial);
        EditorPrefs.SetBool(k_MeshRenderer, MeshRenderers);
        EditorPrefs.SetBool(k_SkinnedMesh, SkinnedMeshRenderers);
        EditorPrefs.SetBool(k_SpriteRenderer, SpriteRenderers);
        EditorPrefs.SetBool(k_OnlyActive, OnlyActive);
        EditorPrefs.SetBool(k_Opaque, Opaque);
        EditorPrefs.SetBool(k_Transparent, Transparent);
        EditorPrefs.SetInt(k_ZWrite, (int)ZWrite);
        EditorPrefs.SetBool(k_QueueRange, QueueRange);
        EditorPrefs.SetInt(k_QueueMin, QueueMin);
        EditorPrefs.SetInt(k_QueueMax, QueueMax);

        SettingsChanged?.Invoke();
    }

    static string[] SplitLines(string s) =>
        s.Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

    static T LoadByGuid<T>(string guid) where T : Object
    {
        if (string.IsNullOrEmpty(guid)) return null;
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path)) return null;
        return AssetDatabase.LoadAssetAtPath<T>(path);
    }

    static void SaveGuid(string key, Object obj)
    {
        if (obj != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long _))
            EditorPrefs.SetString(key, guid);
        else
            EditorPrefs.DeleteKey(key);
    }

    static string JoinGuids(List<Material> mats)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in mats)
        {
            if (m == null) continue;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out string guid, out long _)) continue;
            sb.Append(guid).Append('\n');
        }
        return sb.ToString();
    }

    // Shaders are stored by name: built-in shaders share one GUID (unity_builtin_extra), so
    // a GUID round-trip would load the wrong shader. Shader.Find resolves any project shader in the editor.
    static string JoinShaderNames(List<Shader> shaders)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in shaders)
        {
            if (s == null) continue;
            sb.Append(s.name).Append('\n');
        }
        return sb.ToString();
    }

    // ─── List population ────────────────────────────────────────

    /// <summary>
    /// Adds materials found in <paramref name="objects"/>: Material assets directly, and the
    /// renderer materials of GameObjects. Returns how many new entries were added.
    /// </summary>
    internal static int AddMaterialsFrom(IList<Object> objects)
    {
        int added = 0;
        foreach (var o in objects)
        {
            if (o is Material mat)
            {
                if (AddMaterial(mat)) added++;
            }
            else if (o is GameObject go)
            {
                foreach (var r in go.GetComponents<Renderer>())
                    foreach (var m in r.sharedMaterials)
                        if (AddMaterial(m)) added++;
            }
        }
        if (added > 0) Save();
        return added;
    }

    /// <summary>
    /// Adds shaders found in <paramref name="objects"/>: Shader assets directly, the shader of
    /// Material assets, and the shaders of a GameObject's renderer materials. Returns how many were added.
    /// </summary>
    internal static int AddShadersFrom(IList<Object> objects)
    {
        int added = 0;
        foreach (var o in objects)
        {
            if (o is Shader shader)
            {
                if (AddShader(shader)) added++;
            }
            else if (o is Material mat)
            {
                if (AddShader(mat.shader)) added++;
            }
            else if (o is GameObject go)
            {
                foreach (var r in go.GetComponents<Renderer>())
                    foreach (var m in r.sharedMaterials)
                        if (m != null && AddShader(m.shader)) added++;
            }
        }
        if (added > 0) Save();
        return added;
    }

    static bool AddMaterial(Material m)
    {
        if (m == null || Materials.Contains(m)) return false;
        Materials.Add(m);
        return true;
    }

    static bool AddShader(Shader s)
    {
        if (s == null || Shaders.Contains(s)) return false;
        Shaders.Add(s);
        return true;
    }

    internal static bool ContainsMaterialOrShader(IList<Object> objects)
    {
        foreach (var o in objects)
            if (o is Material || o is Shader) return true;
        return false;
    }

    // ─── Matching ───────────────────────────────────────────────

    /// <summary>Whether a single material slot passes identity and every property filter.</summary>
    internal static bool SlotPasses(Material m)
    {
        if (m == null) return false;

        if (HasIdentityFilter)
        {
            bool identity = Materials.Contains(m) || (m.shader != null && Shaders.Contains(m.shader));
            if (!identity) return false;
        }

        int queue = m.renderQueue;
        bool transparent = queue > k_OpaqueQueueMax;
        if (transparent ? !Transparent : !Opaque) return false;

        if (QueueRange && (queue < QueueMin || queue > QueueMax)) return false;

        if (ZWrite != ZWriteFilter.Any && MaterialZWrite.WritesDepth(m) != (ZWrite == ZWriteFilter.On))
            return false;

        return true;
    }

    /// <summary>Fills <paramref name="results"/> with every renderer that has a passing slot; slotCount totals the passing slots.</summary>
    internal static void CollectMatches(List<Renderer> results, out int slotCount)
    {
        results.Clear();
        slotCount = 0;
        var findMode = OnlyActive ? FindObjectsInactive.Exclude : FindObjectsInactive.Include;

        if (MeshRenderers)        Collect<MeshRenderer>(findMode, results, ref slotCount);
        if (SkinnedMeshRenderers) Collect<SkinnedMeshRenderer>(findMode, results, ref slotCount);
        if (SpriteRenderers)      Collect<SpriteRenderer>(findMode, results, ref slotCount);
    }

    static void Collect<T>(FindObjectsInactive findMode, List<Renderer> results, ref int slotCount) where T : Renderer
    {
        foreach (var r in Object.FindObjectsByType<T>(findMode, FindObjectsSortMode.None))
        {
            int passing = CountPassingSlots(r);
            if (passing == 0) continue;
            results.Add(r);
            slotCount += passing;
        }
    }

    static int CountPassingSlots(Renderer r)
    {
        int count = 0;
        var mats = r.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
            if (SlotPasses(mats[i])) count++;
        return count;
    }

    // ─── Actions ────────────────────────────────────────────────

    internal static void SelectMatches()
    {
        var renderers = new List<Renderer>();
        CollectMatches(renderers, out _);

        var objects = new Object[renderers.Count];
        for (int i = 0; i < renderers.Count; i++)
            objects[i] = renderers[i].gameObject;
        Selection.objects = objects;

        Debug.Log($"[Editools] Select By Material: {renderers.Count} object(s) matched ({DescribeCriteria()})");
    }

    internal static void ReplaceMatches()
    {
        if (ReplacementMaterial == null) return;

        var renderers = new List<Renderer>();
        CollectMatches(renderers, out _);

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Replace Material");

        int replacedSlots = 0, replacedRenderers = 0;
        foreach (var r in renderers)
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == ReplacementMaterial || !SlotPasses(mats[i])) continue;
                mats[i] = ReplacementMaterial;
                changed = true;
                replacedSlots++;
            }
            if (!changed) continue;

            Undo.RegisterCompleteObjectUndo(r, "Replace Material");
            r.sharedMaterials = mats;
            replacedRenderers++;
        }

        Debug.Log($"[Editools] Replace Material: set '{ReplacementMaterial.name}' on {replacedSlots} slot(s) across {replacedRenderers} renderer(s) ({DescribeCriteria()})");
    }

    static string DescribeCriteria()
    {
        var sb = new System.Text.StringBuilder();
        if (!HasIdentityFilter) sb.Append("any material");
        else
        {
            int mats = 0, shaders = 0;
            foreach (var m in Materials) if (m != null) mats++;
            foreach (var s in Shaders) if (s != null) shaders++;
            if (mats > 0) sb.Append(mats).Append(" material(s)");
            if (mats > 0 && shaders > 0) sb.Append(" or ");
            if (shaders > 0) sb.Append(shaders).Append(" shader(s)");
        }
        if (Opaque != Transparent) sb.Append(Opaque ? ", opaque" : ", transparent");
        if (ZWrite != ZWriteFilter.Any) sb.Append(", zwrite ").Append(ZWrite == ZWriteFilter.On ? "on" : "off");
        if (QueueRange) sb.Append(", queue ").Append(QueueMin).Append('-').Append(QueueMax);
        return sb.ToString();
    }
}

/// <summary>
/// Best-effort depth-write detection for a material. Unity exposes no API for a pass's ZWrite
/// state, so the answer is layered:
/// 1. a _ZWrite float property (URP Lit, ASE exports, Shader Graph with material override) wins;
/// 2. else the .shader source is parsed: `ZWrite [Prop]` reads that property off the material, a
///    literal `ZWrite Off` anywhere means Off (shadow/depth passes are always On, so only the
///    forward pass can contribute an Off), and no ZWrite statement means Unity's default On;
/// 3. else (no source on disk, e.g. Shader Graph without override): opaque = On, transparent = Off.
/// Per-shader source results are cached; the panel clears the cache when it opens.
/// </summary>
static class MaterialZWrite
{
    enum SourceMode { Unknown, On, Off, Property }

    struct SourceInfo
    {
        public SourceMode mode;
        public string property;
    }

    static readonly Dictionary<Shader, SourceInfo> s_cache = new();

    static readonly Regex k_ZWriteStatement = new(@"\bZWrite\s+(On|Off|\[\s*(\w+)\s*\])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex k_BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex k_LineComment = new(@"//.*?$", RegexOptions.Multiline | RegexOptions.Compiled);

    internal static void ClearCache() => s_cache.Clear();

    internal static bool WritesDepth(Material m)
    {
        if (m.HasProperty("_ZWrite"))
            return m.GetFloat("_ZWrite") > 0.5f;

        var info = GetSourceInfo(m.shader);
        switch (info.mode)
        {
            case SourceMode.On:  return true;
            case SourceMode.Off: return false;
            case SourceMode.Property:
                return !m.HasProperty(info.property) || m.GetFloat(info.property) > 0.5f;
            default:
                return m.renderQueue <= SelectByMaterial.k_OpaqueQueueMax;
        }
    }

    static SourceInfo GetSourceInfo(Shader shader)
    {
        if (shader == null) return default;
        if (s_cache.TryGetValue(shader, out var cached)) return cached;

        var info = ParseSource(shader);
        s_cache[shader] = info;
        return info;
    }

    static SourceInfo ParseSource(Shader shader)
    {
        string path = AssetDatabase.GetAssetPath(shader);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader") || !File.Exists(path))
            return default;

        string src;
        try { src = File.ReadAllText(path); }
        catch (System.Exception) { return default; }

        src = k_BlockComment.Replace(src, "");
        src = k_LineComment.Replace(src, "");

        // Property-driven beats literals (the forward pass is the one exposing the property);
        // a literal Off beats On (shadow/depth passes contribute the Ons).
        var result = new SourceInfo { mode = SourceMode.On };
        foreach (Match match in k_ZWriteStatement.Matches(src))
        {
            if (match.Groups[2].Success)
                return new SourceInfo { mode = SourceMode.Property, property = match.Groups[2].Value };
            if (match.Groups[1].Value.Equals("Off", System.StringComparison.OrdinalIgnoreCase))
                result.mode = SourceMode.Off;
        }
        return result;
    }
}

/// <summary>
/// Floating Scene View panel for <see cref="SelectByMaterial"/>: the Materials and Shaders lists,
/// the property filters, a live match count, and the Select / Replace actions. Content is IMGUI
/// (matching the rest of Editools). Each SceneView gets its own instance; visibility tracks
/// <see cref="SelectByMaterial.Shown"/>, which the Editools toolbar toggle flips.
/// The match count is recomputed only when settings change, the hierarchy changes, or an undo runs.
/// </summary>
[Overlay(typeof(SceneView), k_Id, "Select By Material")]
class SelectByMaterialOverlay : Overlay
{
    public const string k_Id = "select-by-material";

    const float k_ListMaxHeight = 118f; // ~5 rows before a list scrolls
    const float k_LabelWidth = 76f;

    static GUIStyle s_header;

    static readonly GUIContent k_MaterialsHeader = new GUIContent("Materials",
        "Slots using one of these materials match. Empty (with Shaders also empty) = any material.");
    static readonly GUIContent k_ShadersHeader = new GUIContent("Shaders",
        "Slots whose material uses one of these shaders match. Empty (with Materials also empty) = any material.");
    static readonly GUIContent k_FromSelection = new GUIContent("From Selection",
        "Add the selected assets, plus what the selected objects' renderers use. Drop assets on a list to add them too.");
    static readonly GUIContent k_Clear = new GUIContent("Clear", "Remove every entry from this list.");

    static readonly GUIContent k_Renderers = new GUIContent("Renderers", "Renderer component types to search.");
    static readonly GUIContent k_Mesh    = new GUIContent("Mesh",    "Include MeshRenderers.");
    static readonly GUIContent k_Skinned = new GUIContent("Skinned", "Include SkinnedMeshRenderers.");
    static readonly GUIContent k_Sprite  = new GUIContent("Sprite",  "Include SpriteRenderers.");
    static readonly GUIContent k_OnlyActive = new GUIContent("Only Active Objects",
        "Skip renderers on inactive GameObjects.");

    static readonly GUIContent k_Surface = new GUIContent("Surface",
        "Classified by the material's effective render queue: 2500 and below is opaque, above is transparent.");
    static readonly GUIContent k_Opaque = new GUIContent("Opaque", "Include materials with render queue <= 2500.");
    static readonly GUIContent k_Transparent = new GUIContent("Transparent", "Include materials with render queue > 2500.");

    static readonly GUIContent k_ZWrite = new GUIContent("Z Write",
        "Depth write of the material's forward pass. Read from a _ZWrite property when the shader has one, " +
        "otherwise parsed from the .shader source; Shader Graph without a material override falls back to opaque = On, transparent = Off.");
    static readonly GUIContent[] k_ZWriteOptions =
    {
        new GUIContent("Any", "Ignore depth write."),
        new GUIContent("On",  "Only materials that write depth."),
        new GUIContent("Off", "Only materials that do not write depth."),
    };

    static readonly GUIContent k_QueueRange = new GUIContent("Queue",
        "Limit matches to an inclusive render queue range. Combines with Surface.");
    static readonly GUIContent k_ReplaceWith = new GUIContent("Replace With",
        "Material assigned to every matched slot by Replace.");

    IMGUIContainer _content;
    ReorderableList _materialList;
    ReorderableList _shaderList;
    Vector2 _materialScroll;
    Vector2 _shaderScroll;

    readonly List<Renderer> _matches = new();
    int _matchedSlots;
    bool _countDirty = true;

    // ─── Lifecycle ──────────────────────────────────────────────

    public override VisualElement CreatePanelContent()
    {
        SelectByMaterial.EnsureLoaded();
        var root = new VisualElement { style = { minWidth = 300 } };
        _content = new IMGUIContainer(DrawGUI);
        root.Add(_content);
        return root;
    }

    public override void OnCreated()
    {
        SelectByMaterial.EnsureLoaded();
        SelectByMaterial.ShownChanged += UpdateVisibility;
        SelectByMaterial.SettingsChanged += MarkCountDirty;
        displayedChanged += OnDisplayedChanged;
        UpdateVisibility();
        if (displayed) SubscribeSceneChanges();
    }

    public override void OnWillBeDestroyed()
    {
        UnsubscribeSceneChanges();
        displayedChanged -= OnDisplayedChanged;
        SelectByMaterial.SettingsChanged -= MarkCountDirty;
        SelectByMaterial.ShownChanged -= UpdateVisibility;
    }

    void UpdateVisibility() => displayed = SelectByMaterial.Shown;

    void OnDisplayedChanged(bool shown)
    {
        SelectByMaterial.Shown = shown;
        if (shown)
        {
            MaterialZWrite.ClearCache();
            SubscribeSceneChanges();
            MarkCountDirty();
        }
        else
        {
            UnsubscribeSceneChanges();
        }
    }

    // Scene-change hooks are live only while the panel is visible, so a hidden panel costs nothing.
    void SubscribeSceneChanges()
    {
        EditorApplication.hierarchyChanged -= MarkCountDirty;
        EditorApplication.hierarchyChanged += MarkCountDirty;
        Undo.undoRedoPerformed -= MarkCountDirty;
        Undo.undoRedoPerformed += MarkCountDirty;
    }

    void UnsubscribeSceneChanges()
    {
        EditorApplication.hierarchyChanged -= MarkCountDirty;
        Undo.undoRedoPerformed -= MarkCountDirty;
    }

    void MarkCountDirty()
    {
        _countDirty = true;
        _content?.MarkDirtyRepaint();
    }

    void Recount()
    {
        _countDirty = false;
        SelectByMaterial.CollectMatches(_matches, out _matchedSlots);
    }

    // ─── GUI ────────────────────────────────────────────────────

    void DrawGUI()
    {
        if (s_header == null)
            s_header = new GUIStyle(EditorStyles.boldLabel) { margin = new RectOffset(0, 0, 6, 2) };

        _materialList ??= BuildList(SelectByMaterial.Materials, typeof(Material));
        _shaderList   ??= BuildList(SelectByMaterial.Shaders, typeof(Shader));

        EditorGUIUtility.labelWidth = k_LabelWidth;

        DrawListSection(k_MaterialsHeader, _materialList, SelectByMaterial.Materials, ref _materialScroll,
            objs => SelectByMaterial.AddMaterialsFrom(objs));
        DrawListSection(k_ShadersHeader, _shaderList, SelectByMaterial.Shaders, ref _shaderScroll,
            objs => SelectByMaterial.AddShadersFrom(objs));

        if (!SelectByMaterial.HasIdentityFilter)
            EditorGUILayout.LabelField("Both lists empty: every material is a candidate.", EditorStyles.centeredGreyMiniLabel);

        EditorGUILayout.LabelField("Filters", s_header);
        DrawFilters();

        EditorGUILayout.Space(6);
        DrawActions();
    }

    static ReorderableList BuildList<T>(List<T> items, System.Type elementType) where T : Object
    {
        var list = new ReorderableList(items, elementType, true, false, true, true)
        {
            headerHeight = 2f,
            elementHeight = EditorGUIUtility.singleLineHeight + 4f,
        };

        list.drawElementCallback = (rect, index, _, _) =>
        {
            rect.y += 2f;
            rect.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.BeginChangeCheck();
            var next = (T)EditorGUI.ObjectField(rect, items[index], elementType, false);
            if (EditorGUI.EndChangeCheck())
            {
                items[index] = next;
                SelectByMaterial.Save();
            }
        };
        list.drawNoneElementCallback = rect =>
            EditorGUI.LabelField(rect, "Any", EditorStyles.centeredGreyMiniLabel);
        list.onAddCallback = _ =>
        {
            items.Add(null);
            SelectByMaterial.Save();
        };
        list.onRemoveCallback = l =>
        {
            int index = l.index >= 0 && l.index < items.Count ? l.index : items.Count - 1;
            items.RemoveAt(index);
            SelectByMaterial.Save();
        };
        list.onReorderCallback = _ => SelectByMaterial.Save();
        return list;
    }

    static void DrawListSection<T>(GUIContent header, ReorderableList list, List<T> items, ref Vector2 scroll,
        System.Func<Object[], int> addFrom) where T : Object
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(header, s_header);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(k_FromSelection, EditorStyles.miniButtonLeft))
                addFrom(Selection.objects);
            using (new EditorGUI.DisabledScope(items.Count == 0))
            {
                if (GUILayout.Button(k_Clear, EditorStyles.miniButtonRight))
                {
                    items.Clear();
                    SelectByMaterial.Save();
                }
            }
        }

        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(k_ListMaxHeight));
        list.DoLayoutList();
        EditorGUILayout.EndScrollView();

        HandleDrop(GUILayoutUtility.GetLastRect(), addFrom);
    }

    // Accepts a drag of any mix of materials, shaders and GameObjects onto the whole list area.
    // ObjectField elements consume single-asset drops on themselves first, so this only sees the rest.
    static void HandleDrop(Rect area, System.Func<Object[], int> addFrom)
    {
        var e = Event.current;
        if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
        if (!area.Contains(e.mousePosition)) return;

        bool accepted = false;
        foreach (var o in DragAndDrop.objectReferences)
            if (o is Material || o is Shader || o is GameObject) { accepted = true; break; }
        if (!accepted) return;

        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        if (e.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            addFrom(DragAndDrop.objectReferences);
        }
        e.Use();
    }

    static void DrawFilters()
    {
        EditorGUI.BeginChangeCheck();

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(k_Renderers);
            SelectByMaterial.MeshRenderers        = GUILayout.Toggle(SelectByMaterial.MeshRenderers, k_Mesh, GUILayout.Width(52f));
            SelectByMaterial.SkinnedMeshRenderers = GUILayout.Toggle(SelectByMaterial.SkinnedMeshRenderers, k_Skinned, GUILayout.Width(66f));
            SelectByMaterial.SpriteRenderers      = GUILayout.Toggle(SelectByMaterial.SpriteRenderers, k_Sprite, GUILayout.Width(56f));
        }
        SelectByMaterial.OnlyActive = EditorGUILayout.ToggleLeft(k_OnlyActive, SelectByMaterial.OnlyActive);

        EditorGUILayout.Space(4);

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(k_Surface);
            SelectByMaterial.Opaque      = GUILayout.Toggle(SelectByMaterial.Opaque, k_Opaque, GUILayout.Width(66f));
            SelectByMaterial.Transparent = GUILayout.Toggle(SelectByMaterial.Transparent, k_Transparent, GUILayout.Width(92f));
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel(k_ZWrite);
            SelectByMaterial.ZWrite = (SelectByMaterial.ZWriteFilter)GUILayout.Toolbar((int)SelectByMaterial.ZWrite, k_ZWriteOptions);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            SelectByMaterial.QueueRange = EditorGUILayout.ToggleLeft(k_QueueRange, SelectByMaterial.QueueRange, GUILayout.Width(k_LabelWidth));
            using (new EditorGUI.DisabledScope(!SelectByMaterial.QueueRange))
            {
                SelectByMaterial.QueueMin = EditorGUILayout.IntField(SelectByMaterial.QueueMin);
                GUILayout.Label("to", GUILayout.Width(16f));
                SelectByMaterial.QueueMax = EditorGUILayout.IntField(SelectByMaterial.QueueMax);
            }
        }

        if (EditorGUI.EndChangeCheck())
        {
            SelectByMaterial.QueueMin = Mathf.Clamp(SelectByMaterial.QueueMin, 0, 5000);
            SelectByMaterial.QueueMax = Mathf.Clamp(SelectByMaterial.QueueMax, 0, 5000);
            SelectByMaterial.Save();
        }

        if (!SelectByMaterial.Opaque && !SelectByMaterial.Transparent)
            EditorGUILayout.HelpBox("Enable Opaque and/or Transparent to match anything.", MessageType.Warning);
        else if (!SelectByMaterial.MeshRenderers && !SelectByMaterial.SkinnedMeshRenderers && !SelectByMaterial.SpriteRenderers)
            EditorGUILayout.HelpBox("Enable at least one renderer type to match anything.", MessageType.Warning);
    }

    void DrawActions()
    {
        if (_countDirty) Recount();

        using (new EditorGUI.DisabledScope(_matches.Count == 0))
        {
            if (GUILayout.Button($"Select ({_matches.Count} objects)", GUILayout.Height(24f)))
                SelectByMaterial.SelectMatches();
        }

        EditorGUILayout.Space(4);
        var sepRect = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(sepRect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        EditorGUILayout.Space(2);

        EditorGUI.BeginChangeCheck();
        var replacement = (Material)EditorGUILayout.ObjectField(k_ReplaceWith, SelectByMaterial.ReplacementMaterial, typeof(Material), false);
        if (EditorGUI.EndChangeCheck())
        {
            SelectByMaterial.ReplacementMaterial = replacement;
            SelectByMaterial.Save();
        }

        using (new EditorGUI.DisabledScope(_matchedSlots == 0 || SelectByMaterial.ReplacementMaterial == null))
        {
            if (GUILayout.Button($"Replace ({_matchedSlots} slots)", GUILayout.Height(24f)))
            {
                SelectByMaterial.ReplaceMatches();
                MarkCountDirty();
            }
        }
    }
}
#endif
