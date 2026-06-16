#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Floating, minimizable Scene View panel that appears whenever a single Grey Primitive is
/// selected. Hosts the Edit Mode toggle, the primitive's key properties, its functions, and the
/// Edit Mode settings — so the common edits don't need the Inspector. Content is drawn with IMGUI
/// (matching the rest of Editools) and adapts to the selected GP type.
///
/// Each SceneView gets its own instance. The instances collectively own the Edit Mode scene hook
/// via <see cref="GPEdit.Subscribe"/> / <see cref="GPEdit.Unsubscribe"/> (ref-counted), which is
/// why GPEdit needs no InitializeOnLoad.
/// </summary>
[Overlay(typeof(SceneView), k_Id, "Grey Primitive")]
class GPWindowOverlay : Overlay
{
    public const string k_Id = "gp-window";

    static GUIStyle s_header;

    static readonly GUIContent k_Subdiv = new GUIContent("Subdiv Mult",
        "Per-primitive coefficient on the Greybox Manager's vertex density. 1 = use as-is, 0 = no subdivision.");
    static readonly GUIContent k_Girth = new GUIContent("Base Girth",
        "Base radius of the pipe's circular cross-section, in local units.");
    static readonly GUIContent k_Width = new GUIContent("Base Width",
        "Base width of the road's box cross-section, in local units.");
    static readonly GUIContent k_Height = new GUIContent("Base Height",
        "Base height (thickness) of the road's box cross-section, in local units.");
    static readonly GUIContent k_Boolean = new GUIContent("Operator",
        "Greybox to subtract from this one. A baked Boolean Result child holds Subject minus Operator.");
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
        var root = new VisualElement { style = { minWidth = 232 } };
        root.Add(new IMGUIContainer(DrawGUI));
        return root;
    }

    public override void OnCreated()
    {
        GPEdit.Subscribe();
        Selection.selectionChanged += UpdateVisibility;
        UpdateVisibility();
    }

    public override void OnWillBeDestroyed()
    {
        Selection.selectionChanged -= UpdateVisibility;
        GPEdit.Unsubscribe();
    }

    void UpdateVisibility() => displayed = GPEdit.SelectedPrimitive != null;

    // ─── GUI ────────────────────────────────────────────────────

    static void DrawGUI()
    {
        var gp = GPEdit.SelectedPrimitive;
        if (gp == null) return;

        if (s_header == null)
            s_header = new GUIStyle(EditorStyles.boldLabel) { margin = new RectOffset(0, 0, 6, 2) };

        EditorGUIUtility.labelWidth = 96f;

        DrawEditToggle();

        EditorGUILayout.LabelField("Object Properties", s_header);
        DrawProperties(gp);

        if (gp is Greybox || gp is GreyBooleanResult)
        {
            EditorGUILayout.LabelField("Boolean", s_header);
            DrawBooleanFunctions(gp);
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

                DrawRoadFaces(road);
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
                    bool v = EditorGUILayout.ToggleLeft(k_RoadFaces[idx], faces[idx], GUILayout.Width(96f));
                    if (v != faces[idx])
                        Apply(road, "Greyroad Face", () => road.ActiveFaces[idx] = v);
                }
            }
        }
    }

    static void DrawBooleanFunctions(GreyPrimitive gp)
    {
        var so = new SerializedObject(gp);
        so.Update();
        var prop = so.FindProperty("_booleanOperator");
        if (prop == null) return;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(prop, k_Boolean);
            bool changed = EditorGUI.EndChangeCheck();
            so.ApplyModifiedProperties();
            if (changed) GreyPrimitiveEditor.RebuildPrimitiveAndDependents(gp);

            bool picking = GreyBooleanPicker.IsPicking && GreyBooleanPicker.PickingSubject == gp;
            if (GUILayout.Button(picking ? "Picking…" : "Pick", GUILayout.Width(64f)))
                GreyBooleanPicker.Begin(gp);
        }

        // Select A / B reach into a Boolean Result's nested inputs so its constituents are easy to grab
        // through the result. They make sense only on the result itself — a Greybox that carries an
        // operator is a Subject whose inputs now live under the result, so both are disabled there.
        var result = gp as GreyBooleanResult;
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(result == null || result.Subject == null))
                if (GUILayout.Button(new GUIContent("Select A", "Select this Boolean Result's Subject (the object being carved).")))
                    Selection.activeObject = result.Subject.gameObject;

            using (new EditorGUI.DisabledScope(result == null || result.Operator == null))
                if (GUILayout.Button(new GUIContent("Select B", "Select this Boolean Result's Operator (the object being subtracted).")))
                    Selection.activeObject = result.Operator.gameObject;
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
