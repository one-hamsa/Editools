#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// I + left-click in the Scene View selects the material of the renderer under
/// the cursor instead of the GameObject. I + Shift + left-click adds the material
/// to the current selection. First material slot is used when the renderer has
/// multiple materials. Shows an eyedropper cursor while I is held.
/// Toggle via Editools Settings.
/// </summary>
[InitializeOnLoad]
static class SelectMaterial
{
    const string k_EnabledPref = "Editools_SelectMaterial_Enabled";

    static bool s_enabled;
    static bool s_loaded;
    static bool s_iHeld;
    static Texture2D s_eyedropperCursor;

    static SelectMaterial()
    {
        EnsureLoaded();
        if (s_enabled)
            SceneView.duringSceneGui += OnSceneGUI;
    }

    static void EnsureLoaded()
    {
        if (s_loaded) return;
        s_loaded = true;
        s_enabled = EditorPrefs.GetBool(k_EnabledPref, true);
    }

    static Texture2D EyedropperCursor
    {
        get
        {
            if (s_eyedropperCursor == null)
                s_eyedropperCursor = BuildCursorTexture();
            return s_eyedropperCursor;
        }
    }

    /// <summary>
    /// Creates a white eyedropper cursor with a 1px black outline from Unity's
    /// built-in icon. The source texture is not readable, so we blit it through
    /// a RenderTexture to access its pixels.
    /// </summary>
    static Texture2D BuildCursorTexture()
    {
        var src = EditorGUIUtility.IconContent("d_eyeDropper.Large").image as Texture2D;
        if (src == null) return null;

        int w = src.width, h = src.height;

        // Blit to a RenderTexture so we can read the pixels
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        readable.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        var pixels = readable.GetPixels();

        // Build alpha mask for outline expansion
        var alphas = new float[w * h];
        for (int i = 0; i < pixels.Length; i++)
            alphas[i] = pixels[i].a;

        var result = new Color[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (alphas[i] > 0.1f)
                {
                    // Original visible pixel → white
                    result[i] = new Color(1f, 1f, 1f, pixels[i].a);
                }
                else
                {
                    // Check neighbours for outline
                    bool nearOpaque = false;
                    for (int dy = -1; dy <= 1 && !nearOpaque; dy++)
                    {
                        for (int dx = -1; dx <= 1 && !nearOpaque; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && nx < w && ny >= 0 && ny < h && alphas[ny * w + nx] > 0.1f)
                                nearOpaque = true;
                        }
                    }
                    result[i] = nearOpaque ? new Color(0f, 0f, 0f, 1f) : new Color(0, 0, 0, 0);
                }
            }
        }

        var cursor = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, alphaIsTransparency = true };
        cursor.SetPixels(result);
        cursor.Apply(false, false);

        Object.DestroyImmediate(readable);
        return cursor;
    }

    internal static bool Enabled
    {
        get { EnsureLoaded(); return s_enabled; }
        set
        {
            if (value == s_enabled) return;
            s_enabled = value;
            EditorPrefs.SetBool(k_EnabledPref, value);

            if (value)
                SceneView.duringSceneGui += OnSceneGUI;
            else
                SceneView.duringSceneGui -= OnSceneGUI;
        }
    }

    static void OnSceneGUI(SceneView sceneView)
    {
        Event e = Event.current;

        // Track I key state
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.I)
        {
            s_iHeld = true;
            e.Use();
        }
        if (e.type == EventType.KeyUp && e.keyCode == KeyCode.I)
        {
            s_iHeld = false;
            e.Use();
        }

        // Show eyedropper cursor while I is held
        if (s_iHeld && EyedropperCursor != null)
        {
            EditorGUIUtility.AddCursorRect(
                new Rect(0, 0, sceneView.position.width, sceneView.position.height),
                MouseCursor.CustomCursor);
            Cursor.SetCursor(EyedropperCursor, new Vector2(1, 23), CursorMode.Auto);

            if (e.type == EventType.Repaint)
                sceneView.Repaint();
        }
        else if (e.type == EventType.Repaint)
        {
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        // I + left-click to pick material, I + Shift + left-click to add to selection
        if (e.type != EventType.MouseDown || e.button != 0 || !s_iHeld)
            return;

        var picked = HandleUtility.PickGameObject(e.mousePosition, false);
        if (picked == null) return;

        var renderer = picked.GetComponent<Renderer>();
        if (renderer == null || renderer.sharedMaterial == null) return;

        var mat = renderer.sharedMaterial;

        if (e.shift)
        {
            // Additive — append to current selection
            var current = Selection.objects;
            var expanded = new Object[current.Length + 1];
            current.CopyTo(expanded, 0);
            expanded[current.Length] = mat;
            Selection.objects = expanded;
        }
        else
        {
            Selection.activeObject = mat;
        }

        EditorGUIUtility.PingObject(mat);
        e.Use();
    }
}
#endif
