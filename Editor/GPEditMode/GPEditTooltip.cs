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
        ("LMB", "move"),
        ("Ctrl", "alternative coordinates"),
        ("Shift", "axis lock"),
        ("MMB", "click face to hide / show"),
        ("RMB", "drag face to extrude linked / click edge to split"),
        ("RMB + Alt", "drag face to extrude unlinked"),
    };

    static readonly (string input, string output)[] k_PipeHints =
    {
        ("LMB", "move"),
        ("Ctrl", "alternative coordinates"),
        ("Shift", "axis lock"),
        ("LMB + Alt", "reshape spline from endpoint"),
        ("MMB", "click to delete vertex / reset handle"),
        ("RMB", "drag endpoint to extend / click spline to insert"),
    };

    static readonly (string input, string output)[] k_RoadHints = k_PipeHints;

    static partial void DrawTooltip(SceneView sv, GreyPrimitive gp)
    {
        (string, string)[] hints = gp switch
        {
            Greybox  => k_GreyboxHints,
            GreyBooleanResult => k_GreyboxHints,
            GreyboxCompound => k_GreyboxHints,
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

        float width = 260f;
        foreach (var hint in hints)
            width = Mathf.Max(width, s_ttStyle.CalcSize(new GUIContent(hint.Item1 + " - " + hint.Item2)).x + 8f);
        EditorGUI.DrawRect(new Rect(x - 4f, startY - 3f, width, totalH + 6f), new Color(0f, 0f, 0f, 0.55f));

        for (int i = 0; i < hints.Length; i++)
        {
            var r = new Rect(x, startY + i * lineH, width - 8f, lineH);
            GUI.Label(r, $"{hints[i].Item1}  —  {hints[i].Item2}", s_ttStyle);
        }

        Handles.EndGUI();
    }
}
#endif
