#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Floating, minimizable Scene View panel that appears only while every selected object is a Grey
/// Primitive of the same type (e.g. only Greyboxes). Hosts the Edit Mode toggle, the primitive's key properties, its functions, and the
/// Edit Mode settings — so the common edits don't need the Inspector. Content is drawn with IMGUI
/// (matching the rest of Editools) and adapts to the selected GP type.
///
/// Each SceneView gets its own instance. The instances collectively own the Edit Mode scene hook
/// via <see cref="GPEdit.Subscribe"/> / <see cref="GPEdit.Unsubscribe"/> (ref-counted), held only while
/// Grey Primitives are enabled in Editools Settings, which is why GPEdit needs no InitializeOnLoad.
/// </summary>
[Overlay(typeof(SceneView), k_Id, "Grey Primitive")]
class GPWindowOverlay : Overlay
{
    public const string k_Id = "gp-window";

    static GUIStyle s_header;

    static readonly GUIContent k_Subdiv = new GUIContent("Subdiv Mult",
        "Per-primitive coefficient on the Grey Primitive Manager's vertex density. 1 = use as-is, 0 = no subdivision.");
    static readonly GUIContent k_Girth = new GUIContent("Base Girth",
        "Base radius of the pipe's circular cross-section, in local units.");
    static readonly GUIContent k_Width = new GUIContent("Base Width",
        "Base width of the road's box cross-section, in local units.");
    static readonly GUIContent k_Height = new GUIContent("Base Height",
        "Base height (thickness) of the road's box cross-section, in local units.");
    static readonly GUIContent k_SplineSmoothing = new GUIContent("Spline Smooth",
        "Smooth the road path through every vertex without narrowing it. 0 = authored Beziers; 1 = natural cubic.");
    static readonly GUIContent k_LedgeLeft = new GUIContent("Left",
        "Create a ledge along the whole left edge of this road. RMB-drag along an edge in Edit Mode for a partial one.");
    static readonly GUIContent k_LedgeRight = new GUIContent("Right",
        "Create a ledge along the whole right edge of this road. RMB-drag along an edge in Edit Mode for a partial one.");
    static readonly GUIContent k_UnlinkLedge = new GUIContent("Unlink",
        "Turn this ledge into a normal road on its current path. It stops following the source road.");
    static readonly GUIContent k_Boolean = new GUIContent("Operator",
        "Grey object to subtract from this one: a primitive, a linked group or a Boolean Result. Creates a " +
        "Boolean Result that holds both inputs. An Operator already used by another Boolean stays in place and is shared.");
    static readonly GUIContent k_Edit = new GUIContent("Edit",
        "Toggle Grey Primitive Edit Mode for all Grey Primitives this session (Alt+~).");

    static readonly GUIContent[] k_RoadFaces =
    {
        new GUIContent("Top",    "Include the road's top face."),
        new GUIContent("Bottom", "Include the road's bottom face."),
        new GUIContent("Left",   "Include the road's left side face."),
        new GUIContent("Right",  "Include the road's right side face."),
        new GUIContent("Cap A",  "Include the start cap face."),
        new GUIContent("Cap B",  "Include the end cap face."),
    };

    // ─── Lifecycle ──────────────────────────────────────────────

    public override VisualElement CreatePanelContent()
    {
        var root = new VisualElement { style = { minWidth = 174 } };
        root.Add(new IMGUIContainer(DrawGUI));
        return root;
    }

    bool _hooked;

    public override void OnCreated()
    {
        EditoolsSettings.Changed += SyncGate;
        floatingPositionChanged += SavePosition;
        displayedChanged += OnDisplayedChanged;
        SyncGate();
        OnDisplayedChanged(displayed);
    }

    public override void OnWillBeDestroyed()
    {
        EditoolsSettings.Changed -= SyncGate;
        floatingPositionChanged -= SavePosition;
        displayedChanged -= OnDisplayedChanged;
        EditorApplication.update -= RestorePosition;
        SetHooked(false);
    }

    // ─── Floating position ──────────────────────────────────────
    //
    // Unity shows the panel at its default top-left spot when it reappears, so the last position the
    // user dragged it to is saved and put back on the editor update after each show.

    const string k_PositionXPref = "Editools_GPWindow_FloatingX";
    const string k_PositionYPref = "Editools_GPWindow_FloatingY";

    // True from a show until the saved position is put back; position changes Unity makes in between are not saved.
    bool _restorePending;

    void OnDisplayedChanged(bool shown)
    {
        if (!shown || !floating || !EditorPrefs.HasKey(k_PositionXPref)) return;
        _restorePending = true;
        EditorApplication.update -= RestorePosition;
        EditorApplication.update += RestorePosition;
    }

    void RestorePosition()
    {
        EditorApplication.update -= RestorePosition;
        _restorePending = false;
        // Legitimate filter: the user docked or hid the panel before the update ran.
        if (!floating || !displayed) return;
        floatingPosition = new Vector2(EditorPrefs.GetFloat(k_PositionXPref), EditorPrefs.GetFloat(k_PositionYPref));
    }

    void SavePosition(Vector3 position)
    {
        if (_restorePending || !displayed || !floating) return;
        EditorPrefs.SetFloat(k_PositionXPref, position.x);
        EditorPrefs.SetFloat(k_PositionYPref, position.y);
    }

    // The Edit Mode scene hook and the selection watch are held only while Grey Primitives are enabled.
    void SyncGate()
    {
        SetHooked(GreyPrimitiveSettings.Enabled);
        UpdateVisibility();
    }

    void SetHooked(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on)
        {
            GPEdit.Subscribe();
            Selection.selectionChanged += UpdateVisibility;
        }
        else
        {
            Selection.selectionChanged -= UpdateVisibility;
            GPEdit.Unsubscribe();
        }
    }

    void UpdateVisibility() => displayed = _hooked && IsSingleTypeSelection();

    static bool IsSingleTypeSelection()
    {
        var selected = Selection.gameObjects;
        if (selected.Length == 0) return false;

        System.Type type = null;
        foreach (var go in selected)
        {
            if (!go.TryGetComponent<GreyPrimitive>(out var gp)) return false;
            if (type == null) type = gp.GetType();
            else if (gp.GetType() != type) return false;
        }
        return true;
    }

    // ─── GUI ────────────────────────────────────────────────────

    static void DrawGUI()
    {
        var gp = GPEdit.SelectedPrimitive;
        if (gp == null) return;

        if (s_header == null)
            s_header = new GUIStyle(EditorStyles.boldLabel) { margin = new RectOffset(0, 0, 6, 2) };

        float previousLabelWidth = EditorGUIUtility.labelWidth;
        try
        {
            EditorGUIUtility.labelWidth = 72f;

            DrawEditToggle();

            EditorGUILayout.LabelField(Selection.count > 1 ? "Active Object Properties" : "Object Properties", s_header);
            DrawProperties(gp);

            if (gp is Greybox || gp is GreyBooleanResult || gp is GreyboxCompound)
            {
                EditorGUILayout.LabelField("Boolean", s_header);
                DrawBooleanFunctions(gp);
            }
        }
        finally
        {
            EditorGUIUtility.labelWidth = previousLabelWidth;
        }
    }

    static void DrawEditToggle()
    {
        bool on = GPEdit.Enabled;
        var bg = GUI.backgroundColor;
        if (on) GUI.backgroundColor = new Color(0.35f, 0.6f, 0.9f, 1f);
        bool next = GUILayout.Toggle(on, k_Edit, "Button", GUILayout.Height(24));
        GUI.backgroundColor = bg;
        if (next != on) GPEdit.Enabled = next;
        EditorGUILayout.Space(2);
    }

    static void DrawProperties(GreyPrimitive gp)
    {
        EditorGUI.BeginChangeCheck();
        float subdiv = EditorGUILayout.FloatField(k_Subdiv, gp.SubdivisionMultiplier);
        if (EditorGUI.EndChangeCheck())
            Apply(gp, "Greybox Subdiv", () => gp.SubdivisionMultiplier = Mathf.Max(0f, subdiv));

        switch (gp)
        {
            case Greypipe pipe:
                EditorGUI.BeginChangeCheck();
                float girth = EditorGUILayout.FloatField(k_Girth, pipe.BaseGirth);
                if (EditorGUI.EndChangeCheck())
                    Apply(pipe, "Greypipe Girth", () => pipe.BaseGirth = girth);
                break;

            case Greyroad road:
                EditorGUI.BeginChangeCheck();
                float width = EditorGUILayout.FloatField(k_Width, road.BaseWidth);
                float height = EditorGUILayout.FloatField(k_Height, road.BaseHeight);
                if (EditorGUI.EndChangeCheck())
                    Apply(road, "Greyroad Size", () => { road.BaseWidth = width; road.BaseHeight = height; });

                // A ledge takes its path from the source road, so it has no smoothing.
                if (!road.IsLedge)
                {
                    EditorGUI.BeginChangeCheck();
                    float smoothing = EditorGUILayout.Slider(k_SplineSmoothing, road.SplineSmoothing, 0f, 1f);
                    if (EditorGUI.EndChangeCheck())
                        Apply(road, "Greyroad Spline Smoothing", () => road.SplineSmoothing = smoothing);
                }

                // A ledge hides and shows faces with MMB on its face handles.
                if (!road.IsLedge) DrawRoadFaces(road);
                DrawLedgeFunctions(road);
                break;
        }
    }

    static void DrawRoadFaces(Greyroad road)
    {
        EditorGUILayout.LabelField("Faces", EditorStyles.miniBoldLabel);
        var faces = road.ActiveFaces;
        if (faces == null || faces.Length != 6) return;

        for (int row = 0; row < 3; row++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int col = 0; col < 2; col++)
                {
                    int idx = row * 2 + col;
                    bool v = EditorGUILayout.ToggleLeft(k_RoadFaces[idx], faces[idx], GUILayout.Width(80f));
                    if (v != faces[idx])
                        Apply(road, "Greyroad Face", () => road.ActiveFaces[idx] = v);
                }
            }
        }
    }

    static void DrawLedgeFunctions(Greyroad road)
    {
        EditorGUILayout.LabelField(road.IsLedge ? "Ledge" : "Add Ledge", EditorStyles.miniBoldLabel);
        if (road.IsLedge)
        {
            if (GUILayout.Button(k_UnlinkLedge))
            {
                GPEdit.UnlinkLedge(road);
                GUIUtility.ExitGUI();
            }
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(k_LedgeLeft))
            {
                GPEdit.CreateFullLedge(road, -1);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button(k_LedgeRight))
            {
                GPEdit.CreateFullLedge(road, 1);
                GUIUtility.ExitGUI();
            }
        }
    }

    static void DrawBooleanFunctions(GreyPrimitive gp)
    {
        var subject = GreyBooleanOrchestrator.EditSubject(gp);
        var so = new SerializedObject(subject);
        so.Update();
        bool changed = GreyPrimitiveEditor.DrawBooleanInput(so, subject, k_Boolean);
        so.ApplyModifiedProperties();
        if (changed) GreyPrimitiveEditor.RebuildPrimitiveAndDependents(subject);

        var result = gp as GreyBooleanResult;
        if (result != null && GUILayout.Button(GreyPrimitiveEditor.ExplodeLabel))
        {
            GreyBooleanOrchestrator.Explode(result);
            GUIUtility.ExitGUI();
        }
    }

    /// <summary>Register undo, mutate, then rebuild the primitive and everything derived from it.</summary>
    static void Apply(GreyPrimitive gp, string undoName, System.Action mutate)
    {
        Undo.RegisterCompleteObjectUndo(gp, undoName);
        mutate();
        GreyPrimitiveEditor.RebuildPrimitiveAndDependents(gp);
        EditorUtility.SetDirty(gp);
    }
}
#endif
