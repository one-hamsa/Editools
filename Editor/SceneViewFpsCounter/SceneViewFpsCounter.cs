using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Displays a live FPS counter overlay in the top-left corner of each Scene View.
/// Tracks frame timing independently per Scene View instance.
/// Gated by Editools Settings; hooked into the Scene View only while enabled and the overlay is displayed.
/// </summary>
static class SceneViewFpsCounter
{
    private const int k_SampleCount = 60;

    static bool s_subscribed;

    internal static bool Enabled => EditoolsSettings.Current.fpsCounterEnabled;

    /// <summary>Whether the counter is drawn. Flipped by the toolbar toggle / settings menu row.</summary>
    internal static bool Shown
    {
        get => EditoolsSettings.Current.fpsCounterShown;
        set
        {
            EditoolsSettings.SetBool(ref EditoolsSettings.Current.fpsCounterShown, value, "Toggle FPS Counter");
            Sync();
        }
    }

    /// <summary>Hooks the Scene View while enabled, shown and the overlay is active; unhooks otherwise.</summary>
    internal static void Sync()
    {
        bool wanted = EditoolsOverlay.IsActive && Enabled && Shown;
        if (wanted == s_subscribed) return;
        s_subscribed = wanted;

        if (wanted)
            SceneView.duringSceneGui += OnSceneGUI;
        else
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            s_perView.Clear();
        }
        SceneView.RepaintAll();
    }

    struct FrameData
    {
        public double lastTime;
        public float[] samples;
        public int sampleIndex;
        public bool filled;
    }

    static readonly Dictionary<int, FrameData> s_perView = new Dictionary<int, FrameData>();

    static GUIStyle s_style;

    static void OnSceneGUI(SceneView sceneView)
    {
        if (Event.current.type != EventType.Repaint) return;

        int id = sceneView.GetInstanceID();
        double now = EditorApplication.timeSinceStartup;

        if (!s_perView.TryGetValue(id, out var data))
        {
            data = new FrameData
            {
                lastTime = now,
                samples = new float[k_SampleCount],
                sampleIndex = 0,
                filled = false
            };
            s_perView[id] = data;
            sceneView.Repaint();
            return;
        }

        float dt = (float)(now - data.lastTime);
        data.lastTime = now;

        if (dt > 0f)
        {
            data.samples[data.sampleIndex] = dt;
            data.sampleIndex++;
            if (data.sampleIndex >= k_SampleCount)
            {
                data.sampleIndex = 0;
                data.filled = true;
            }
        }

        s_perView[id] = data;

        // Compute average FPS from samples
        int count = data.filled ? k_SampleCount : data.sampleIndex;
        if (count < 2)
        {
            sceneView.Repaint();
            return;
        }

        float sum = 0f;
        float min = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            sum += data.samples[i];
            if (data.samples[i] < min) min = data.samples[i];
        }

        float avgDt = sum / count;
        float avgFps = avgDt > 0f ? 1f / avgDt : 0f;
        float maxFps = min > 0f ? 1f / min : 0f;

        if (s_style == null)
        {
            s_style = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                normal = { textColor = Color.white },
                padding = new RectOffset(4, 4, 2, 2)
            };
        }

        Handles.BeginGUI();

        string label = $"{avgFps:0} FPS ({avgDt * 1000f:0.0} ms)";
        var content = new GUIContent(label);
        var size = s_style.CalcSize(content);

        var bgRect = new Rect(6, 6, size.x + 2, size.y + 2);
        EditorGUI.DrawRect(bgRect, new Color(0f, 0f, 0f, 0.55f));
        GUI.Label(new Rect(7, 6, size.x, size.y), content, s_style);

        Handles.EndGUI();

        sceneView.Repaint();
    }
}
