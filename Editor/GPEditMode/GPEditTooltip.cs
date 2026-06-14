#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// The simplified Edit Mode tooltip: a small bottom-left list of input → output hints for the
/// selected Grey Primitive type only. Shown while Edit Mode is on and the Tooltips setting is
/// enabled (gated by the caller in <see cref="GPEdit.OnSceneGUI"/>).
/// </summary>
static partial class GPEdit
{
    static GUIStyle s_ttStyle;

    static readonly (string input, string output)[] k_GreyboxHints =
    {
        ("Edge LMB",          "move"),
        ("Edge LMB+Shift",    "axis lock"),
        ("Face LMB",          "move along normal"),
        ("Face LMB+Shift",    "skew"),
        ("Face MMB",          "hide / show"),
        ("Face RMB",          "extrude"),
        ("Face RMB+Shift",    "extrude linked"),
    };

    static readonly (string input, string output)[] k_PipeHints =
    {
        ("Vertex LMB",        "move"),
        ("Vertex LMB+Shift",  "move orth"),
        ("End LMB+Ctrl",      "move alone"),
        ("End RMB",           "extrude"),
        ("Vertex MMB",        "delete"),
        ("Handle LMB",        "move"),
        ("Handle MMB",        "reset"),
        ("Spline RMB",        "insert vertex"),
    };

    static readonly (string input, string output)[] k_RoadHints =
    {
        ("Vertex LMB",        "move"),
        ("Vertex LMB+Shift",  "move orth"),
        ("End LMB+Ctrl",      "move alone"),
        ("End RMB",           "extrude"),
        ("Vertex MMB",        "delete"),
        ("Handle LMB",        "move"),
        ("Handle MMB",        "reset"),
        ("Banking LMB",       "bank"),
        ("Banking MMB",       "reset"),
        ("Spline RMB",        "insert vertex"),
    };

    static partial void DrawTooltip(SceneView sv, GreyPrimitive gp)
    {
        (string, string)[] hints = gp switch
        {
            Greybox  => k_GreyboxHints,
            Greypipe => k_PipeHints,
            Greyroad => k_RoadHints,
            _        => null,
        };
        if (hints == null) return;

        if (s_ttStyle == null)
            s_ttStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f, 0.9f) },
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };

        Handles.BeginGUI();

        const float lineH = 14f;
        const float x = 10f;
        float totalH = hints.Length * lineH;
        float startY = sv.position.height - 30f - totalH;

        EditorGUI.DrawRect(new Rect(x - 4f, startY - 3f, 210f, totalH + 6f), new Color(0f, 0f, 0f, 0.55f));

        for (int i = 0; i < hints.Length; i++)
        {
            var r = new Rect(x, startY + i * lineH, 202f, lineH);
            GUI.Label(r, $"{hints[i].Item1}  —  {hints[i].Item2}", s_ttStyle);
        }

        Handles.EndGUI();
    }
}
#endif
