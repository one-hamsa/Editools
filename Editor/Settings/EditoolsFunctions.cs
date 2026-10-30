#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The list of Editools functions, in the order the settings inspector and the toolbar overlay
/// show them. Each entry wires a function's gate to its tool, declares its settings drawer, and
/// how it appears in the overlay. <see cref="SyncAll"/> makes every tool match the current
/// settings and overlay state; it runs when the overlay is created, shown, hidden or destroyed and
/// after undo / redo on the settings object.
/// </summary>
static class EditoolsFunctions
{
	static EditoolsSettings S => EditoolsSettings.Current;

	internal static readonly List<EditoolsFunction> All = Build();

	internal static EditoolsFunction Find(string id)
	{
		foreach (var f in All)
			if (f.Id == id) return f;
		return null;
	}

	/// <summary>Re-applies every function's enabled state to its tool (subscriptions, panels, modes).</summary>
	internal static void SyncAll()
	{
		SceneCameraUndo.Sync();
		HierarchyHeatmap.Sync();
		QuickTransform.Sync();
		SceneViewRoll.Sync();
		SelectMaterial.Sync();
		AltDeselection.Sync();
		ObjectDisplayToggle.Sync();
		SelectionUndo.Sync();
		SceneViewFpsCounter.Sync();
		SceneViewVertexCounter.Sync();
		TriggerDebug.Sync();
		MaterialCheck.Sync();
		SelectByMaterial.Sync();
		DistanceSelect.Sync();
		ChangeOrientationOverlay.Sync();
		QuickAccess.Sync();
	}

	// ─── Gate plumbing ───────────────────────────────────────────

	static void SetGate(string name, Action<EditoolsSettings, bool> write, bool value, Action sync)
	{
		var settings = S;
		Undo.RecordObject(settings, (value ? "Enable " : "Disable ") + name);
		write(settings, value);
		settings.SaveAndNotify();
		sync?.Invoke();
	}

	static EditoolsFunction Gate(EditoolsFunction f, Func<EditoolsSettings, bool> read,
		Action<EditoolsSettings, bool> write, Action sync = null)
	{
		f.GetEnabled = () => read(S);
		f.SetEnabled = v => SetGate(f.Name, write, v, sync);
		return f;
	}

	// ─── Overlay element factories ───────────────────────────────

	static VisualElement MakeToggle(EditoolsFunction f)
	{
		var toggle = new EditorToolbarToggle { icon = f.Icon(), tooltip = f.Tooltip };
		toggle.SetValueWithoutNotify(f.GetActive());
		toggle.RegisterValueChangedCallback(evt => f.SetActive(evt.newValue));
		Action refresh = () => toggle.SetValueWithoutNotify(f.GetActive());
		toggle.RegisterCallback<AttachToPanelEvent>(_ => EditoolsSettings.Changed += refresh);
		toggle.RegisterCallback<DetachFromPanelEvent>(_ => EditoolsSettings.Changed -= refresh);
		return toggle;
	}

	static VisualElement MakeActionButton(EditoolsFunction f, Action onClick)
	{
		var button = new EditorToolbarButton(onClick) { icon = f.Icon(), tooltip = f.Tooltip };
		return button;
	}

	// ─── Settings drawers ────────────────────────────────────────

	static void Field(SerializedObject so, string property, string label) =>
		EditorGUILayout.PropertyField(so.FindProperty(property), new GUIContent(label));

	static void DrawQuickTransformSettings(SerializedObject so)
	{
		Field(so, nameof(EditoolsSettings.quickTransformEdgeHoverPx), "Edge Detection (px)");
		Field(so, nameof(EditoolsSettings.quickTransformCircleRadius), "Rotation Circle Size");
		Field(so, nameof(EditoolsSettings.quickTransformRotSnapAngle), "Rotation Snap (deg)");
		Field(so, nameof(EditoolsSettings.quickTransformLinearRotation), "Linear Rotation");
		if (so.FindProperty(nameof(EditoolsSettings.quickTransformLinearRotation)).boolValue)
		{
			EditorGUI.indentLevel++;
			Field(so, nameof(EditoolsSettings.quickTransformLinearRotSensitivity), "Sensitivity (deg/px)");
			EditorGUI.indentLevel--;
		}
		Field(so, nameof(EditoolsSettings.quickTransformShowTooltips), "Scene View Tooltips");
	}

	static void DrawSnapToSurfaceSettings(SerializedObject so)
	{
		Field(so, nameof(EditoolsSettings.snapSkinnedMeshes), "Skinned Meshes");
		Field(so, nameof(EditoolsSettings.snapNonStatic), "Non Static");
		Field(so, nameof(EditoolsSettings.snapTransparentMeshes), "Transparent Meshes");
		Field(so, nameof(EditoolsSettings.snapAlignZToSurface), "Align Z To Surface");
		Field(so, nameof(EditoolsSettings.snapFlipAxis), "Flip Axis");
		Field(so, nameof(EditoolsSettings.snapSurfaceOffset), "Surface Offset");
	}

	static void DrawGreyPrimitiveSettings(SerializedObject so)
	{
		Field(so, nameof(EditoolsSettings.greyDefaultMaterial), "Default Material");
		Field(so, nameof(EditoolsSettings.greyDefaultStatic), "Static");

		var layer = so.FindProperty(nameof(EditoolsSettings.greyDefaultLayer));
		layer.intValue = EditorGUILayout.LayerField(new GUIContent("Default Layer", layer.tooltip), layer.intValue);

		Field(so, nameof(EditoolsSettings.greyDefaultMeshCollider), "Mesh Collider");
		Field(so, nameof(EditoolsSettings.greyDefaultShadowCasting), "Cast Shadows");

		EditorGUILayout.LabelField("Greybox", EditorStyles.boldLabel);
		Field(so, nameof(EditoolsSettings.greyboxDefaultScale), "Default Scale");

		EditorGUILayout.LabelField("Greypipe", EditorStyles.boldLabel);
		Field(so, nameof(EditoolsSettings.greypipeDefaultGirth), "Default Girth");
		Field(so, nameof(EditoolsSettings.greypipeDefaultLength), "Default Length");

		EditorGUILayout.LabelField("Greyroad", EditorStyles.boldLabel);
		Field(so, nameof(EditoolsSettings.greyroadDefaultWidth), "Default Width");
		Field(so, nameof(EditoolsSettings.greyroadDefaultHeight), "Default Height");
		Field(so, nameof(EditoolsSettings.greyroadDefaultLength), "Default Length");

		EditorGUILayout.LabelField("Greyquad", EditorStyles.boldLabel);
		Field(so, nameof(EditoolsSettings.greyquadDefaultSize), "Default Size");

		EditorGUILayout.LabelField("Edit Mode", EditorStyles.boldLabel);
		Field(so, nameof(EditoolsSettings.greyEditModeTooltips), "Scene View Tooltips");
		Field(so, nameof(EditoolsSettings.greyAutoUpdatePivot), "Auto Update Pivot");
	}

	static void DrawHeatmapSettings(SerializedObject so)
	{
		Field(so, nameof(EditoolsSettings.heatmapLowColor), "Low Heat Color");
		Field(so, nameof(EditoolsSettings.heatmapHighColor), "High Heat Color");
		Field(so, nameof(EditoolsSettings.heatmapAlpha), "Heat Alpha");
		Field(so, nameof(EditoolsSettings.heatmapGradientStrength), "Gradient Strength");
		Field(so, nameof(EditoolsSettings.heatmapMaxRecent), "Max Recent Items");

		EditorGUILayout.LabelField("Marks (Alt+D)", EditorStyles.boldLabel);
		Field(so, nameof(EditoolsSettings.heatmapMarkColor), "Mark Color");
		Field(so, nameof(EditoolsSettings.heatmapMarkAlpha), "Mark Alpha");
		Field(so, nameof(EditoolsSettings.heatmapMaxMarked), "Max Marked Items");

		if (GUILayout.Button("Reset Recent History"))
			HierarchyHeatmap.ResetRecent();
	}

	static void DrawMaterialCheckSettings(SerializedObject so)
	{
		Field(so, nameof(EditoolsSettings.materialCheckMaterial), "Material");
	}

	static void DrawTriggerDebugSettings(SerializedObject so)
	{
		EditorGUILayout.HelpBox(
			"Whitelist: extra materials Trigger Debug paints in addition to the opaque scene fill, e.g. transparent " +
			"ones that are otherwise skipped. Drop materials on the list or on the Trigger Debug toolbar button.",
			MessageType.None);
		Field(so, nameof(EditoolsSettings.triggerDebugWhitelist), "Material Whitelist");
	}

	// ─── Registry ────────────────────────────────────────────────

	static List<EditoolsFunction> Build()
	{
		var list = new List<EditoolsFunction>(32);

		// ── Scene Tools ──

		list.Add(Gate(new EditoolsFunction
		{
			Id = "select-material", Name = "Select Material", Category = EditoolsCategory.SceneTools,
			Tooltip = "I + left-click in the Scene View selects the material of the renderer under the cursor " +
				"(first material slot). I + Shift + click adds it to the selection.",
			ShortcutText = "I + Click",
		}, s => s.selectMaterialEnabled, (s, v) => s.selectMaterialEnabled = v, SelectMaterial.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "select-by-material", Name = "Select And Replace Material", Category = EditoolsCategory.SceneTools,
			Tooltip = "Floating Scene View panel: selects renderers by material / shader / surface filters, or replaces " +
				"their material. Drag materials, shaders or objects onto the toolbar button to add them to its lists.",
			OverlayKind = EditoolsOverlayKind.Toggle,
			Icon = () => EditoolsIcons.Builtin("d_FilterByType"),
			CreateOverlayElement = _ => new EditoolsSelectByMaterialButton(),
			GetActive = () => SelectByMaterial.Shown,
			SetActive = v => SelectByMaterial.Shown = v,
		}, s => s.selectByMaterialEnabled, (s, v) => s.selectByMaterialEnabled = v, SelectByMaterial.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "grey-primitives", Name = "Grey Primitives", Category = EditoolsCategory.SceneTools,
			Tooltip = "Procedural primitives for level blockout.\n\n" +
				"Greybox - Ctrl+G - deformable box. Press again while placing to fit it to the previous selection.\n" +
				"Greypipe - Ctrl+T - spline tube.\nGreyroad - Ctrl+R - spline road.\nGreyquad - Ctrl+Q - flat quad.\n\n" +
				"Alt+~ toggles Edit Mode on the selected primitives.",
			DrawSettings = DrawGreyPrimitiveSettings,
		}, s => s.greyPrimitivesEnabled, (s, v) => s.greyPrimitivesEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "snap-to-surface", Name = "Snap To Surface", Category = EditoolsCategory.SceneTools,
			Tooltip = "Alt+A in the Scene View drops the selected object onto the surface under the cursor and aligns it " +
				"to that surface's normal. Left-click confirms, right-click cancels.",
			ShortcutIds = new[] { "Editools/Snap To Surface" },
			DrawSettings = DrawSnapToSurfaceSettings,
		}, s => s.snapToSurfaceEnabled, (s, v) => s.snapToSurfaceEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "distance-select", Name = "Distance Select", Category = EditoolsCategory.SceneTools,
			Tooltip = "Ctrl+= adds objects within the next distance interval of the selection to the selection and opens " +
				"the Distance Select panel (distance mode, interval, aggregate, entire prefabs).",
			ShortcutIds = new[] { "Editools/Distance Select" },
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Custom("radar"),
			Activate = _ => DistanceSelect.Activate(),
		}, s => s.distanceSelectEnabled, (s, v) => s.distanceSelectEnabled = v, DistanceSelect.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "change-orientation", Name = "Change Orientation", Category = EditoolsCategory.SceneTools,
			Tooltip = "Floating Scene View panel with X / Y / Z buttons. Each click rotates the selected objects 90 degrees " +
				"clockwise around their local axis and swaps the scale of the other two axes, so the object keeps its " +
				"world-space proportions.",
			OverlayKind = EditoolsOverlayKind.Toggle,
			Icon = () => EditoolsIcons.Custom("3d_rotation"),
			GetActive = () => ChangeOrientationOverlay.Shown,
			SetActive = v => ChangeOrientationOverlay.Shown = v,
		}, s => s.changeOrientationEnabled, (s, v) => s.changeOrientationEnabled = v, ChangeOrientationOverlay.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "replace-object", Name = "Replace Object", Category = EditoolsCategory.SceneTools,
			Tooltip = "Replaces every selected Hierarchy object with a duplicate of one reference object (prefab asset or " +
				"scene object), keeping each original's parent, transform, sibling index and name. Opens the window.",
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Custom("find_replace"),
			Activate = _ => ReplaceObjectWindow.ShowWindow(),
		}, s => s.replaceObjectEnabled, (s, v) => s.replaceObjectEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "plane-to-quad", Name = "Plane To Quad", Category = EditoolsCategory.SceneTools,
			Tooltip = "Replaces the built-in Plane mesh (121 vertices) on the selected objects with a shared 4-vertex quad in " +
				"the same coordinate system. Only the mesh reference changes; MeshColliders using the Plane are swapped too.",
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Custom("grid_off"),
			Activate = _ => PlaneToQuad.ReplaceSelectedPlanes(),
		}, s => s.planeToQuadEnabled, (s, v) => s.planeToQuadEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "select-shadow-only", Name = "Select Shadow Only", Category = EditoolsCategory.SceneTools,
			Tooltip = "Selects every scene object whose MeshRenderer has Cast Shadows set to Shadows Only, including inactive ones.",
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Custom("wb_shade"),
			Activate = _ => SelectShadowOnly.Run(),
		}, s => s.selectShadowOnlyEnabled, (s, v) => s.selectShadowOnlyEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "copy-paste-transform", Name = "Copy / Paste Transform", Category = EditoolsCategory.SceneTools,
			Tooltip = "Alt+Ctrl+C copies the active object's local transform, Alt+Ctrl+V pastes it onto the selection, " +
				"Alt+Ctrl+X resets the selection's local transform.",
			ShortcutIds = new[] { "Editools/Copy Transform", "Editools/Paste Transform", "Editools/Reset Transform" },
		}, s => s.copyPasteTransformEnabled, (s, v) => s.copyPasteTransformEnabled = v));

		// ── Helpers ──

		list.Add(Gate(new EditoolsFunction
		{
			Id = "screenshot-capture", Name = "Scene Capture", Category = EditoolsCategory.Helpers,
			Tooltip = "Captures the Scene View (up to 10 shots) into a numbered strip in the toolbar. Click a number to " +
				"overlay that capture on the view for comparison; right-click for a comment or to delete.",
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Builtin("d_SceneViewCamera"),
			CreateOverlayElement = overlay => overlay.CreateCaptureElement(),
			Activate = _ => EditoolsOverlay.CaptureLastActiveSceneView(),
		}, s => s.screenshotCaptureEnabled, (s, v) => s.screenshotCaptureEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "material-check", Name = "Material Check", Category = EditoolsCategory.Helpers,
			Tooltip = "Previews one material on every opaque scene renderer. Drag a material onto the toolbar button to " +
				"assign it; Apply writes it to the renderers for real.",
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Builtin("d_Material Icon"),
			CreateOverlayElement = _ => new EditoolsMaterialCheckButton(),
			Activate = rect => UnityEditor.PopupWindow.Show(rect, new MaterialCheckPopup()),
			DrawSettings = DrawMaterialCheckSettings,
		}, s => s.materialCheckEnabled, (s, v) => s.materialCheckEnabled = v, MaterialCheck.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "trigger-debug", Name = "Trigger Debug", Category = EditoolsCategory.Helpers,
			Tooltip = "Paints the scene grey and tints anything inside the selected box / sphere trigger colliders and " +
				"registered debug volumes. Right-click the toolbar button to open these settings; drop a material on it " +
				"to whitelist it.",
			OverlayKind = EditoolsOverlayKind.Toggle,
			Icon = () => EditoolsIcons.Builtin("d_BoxCollider Icon"),
			CreateOverlayElement = _ => new EditoolsTriggerDebugButton(),
			GetActive = () => TriggerDebug.IsActive,
			SetActive = TriggerDebug.SetActive,
			DrawSettings = DrawTriggerDebugSettings,
		}, s => s.triggerDebugEnabled, (s, v) => s.triggerDebugEnabled = v, TriggerDebug.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "vertex-counter", Name = "Vertex Counter", Category = EditoolsCategory.Helpers,
			Tooltip = "Shows the vertices sent to the GPU under the FPS counter in each Scene View: whole scene, inside the " +
				"frustum, and the selection. One draw per material slot; only renderers that draw are counted.",
			OverlayKind = EditoolsOverlayKind.Toggle,
			Icon = () => EditoolsIcons.Custom("scatter_plot"),
			GetActive = () => SceneViewVertexCounter.Shown,
			SetActive = v => SceneViewVertexCounter.Shown = v,
		}, s => s.vertexCounterEnabled, (s, v) => s.vertexCounterEnabled = v, SceneViewVertexCounter.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "fps-counter", Name = "FPS Counter", Category = EditoolsCategory.Helpers,
			Tooltip = "Shows a live FPS counter with frame time in the top-left corner of each Scene View.",
			OverlayKind = EditoolsOverlayKind.Toggle,
			Icon = () => EditoolsIcons.Custom("speed"),
			GetActive = () => SceneViewFpsCounter.Shown,
			SetActive = v => SceneViewFpsCounter.Shown = v,
		}, s => s.fpsCounterEnabled, (s, v) => s.fpsCounterEnabled = v, SceneViewFpsCounter.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "gamma-corrector", Name = "Gamma Corrector", Category = EditoolsCategory.Helpers,
			Tooltip = "Toolbar swatch: left-click picks a color and converts it between gamma and linear; right-click " +
				"copies / pastes or switches the conversion direction.",
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Builtin("d_ColorPicker.CycleColor"),
			CreateOverlayElement = _ => new EditoolsGammaCorrectorButton(),
			Activate = rect => EditoolsGammaCorrectorButton.OpenPickerStandalone(),
		}, s => s.gammaCorrectorEnabled, (s, v) => s.gammaCorrectorEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "object-display-toggle", Name = "Object Display Toggle", Category = EditoolsCategory.Helpers,
			Tooltip = "Y cycles the selected objects between Normal, Bounding Box (geometry hidden, bounds drawn as lines), " +
				"Greybox (Greybox corner edges) and Outline (mesh silhouette). Lines show with Gizmos off and are " +
				"click-selectable. Nothing is saved to the scene. The toolbar button cycles the selection like Y does.",
			ShortcutIds = new[] { "Editools/Object Display Toggle" },
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Custom("view_in_ar"),
			Activate = _ => ObjectDisplayToggle.CycleSelection(),
		}, s => s.objectDisplayToggleEnabled, (s, v) => s.objectDisplayToggleEnabled = v, ObjectDisplayToggle.Sync));

		// ── Controls ──

		list.Add(Gate(new EditoolsFunction
		{
			Id = "alt-deselection", Name = "Alt-Deselection", Category = EditoolsCategory.Controls,
			Tooltip = "Ctrl + left-click in the Scene View only adds the object under the cursor to the selection " +
				"(replaces Unity's Ctrl + click toggle). Alt + left-click without dragging removes it.",
			ShortcutText = "Ctrl + Click / Alt + Click",
		}, s => s.altDeselectionEnabled, (s, v) => s.altDeselectionEnabled = v, AltDeselection.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "scene-view-roll", Name = "Scene View Roll", Category = EditoolsCategory.Controls,
			Tooltip = "Ctrl + Alt + middle-mouse drag rolls the Scene View camera. Ctrl + Alt + middle-mouse click resets the roll.",
			ShortcutText = "Ctrl + Alt + MMB",
		}, s => s.sceneViewRollEnabled, (s, v) => s.sceneViewRollEnabled = v, SceneViewRoll.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "quick-transform", Name = "QuickTransform", Category = EditoolsCategory.Controls,
			Tooltip = "Hold W / E / R and drag in the Scene View to transform without gizmo handles.\n\n" +
				"W - Move: LMB outside box slides on world XZ, LMB on face slides on that plane, RMB on face pushes along its normal.\n" +
				"E - Rotate: LMB outside box rotates around world Y, on a face around its normal, on an edge around the edge. Ctrl snaps.\n" +
				"R - Scale: LMB on face single-axis (opposite face anchored), RMB on face uniform from opposite face, RMB outside uniform from pivot.\n\n" +
				"Shift + W/E/R duplicates first, then transforms the copies.",
			ShortcutText = "Hold W / E / R",
			DrawSettings = DrawQuickTransformSettings,
		}, s => s.quickTransformEnabled, (s, v) => s.quickTransformEnabled = v, QuickTransform.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "scene-view-undo", Name = "Scene View Undo", Category = EditoolsCategory.Controls,
			Tooltip = "Tracks Scene View camera movements. Shift+Z steps back, Shift+Y steps forward. Up to 32 positions per Scene View.",
			ShortcutIds = new[] { "Editools/Scene Camera Undo", "Editools/Scene Camera Redo" },
		}, s => s.sceneViewUndoEnabled, (s, v) => s.sceneViewUndoEnabled = v, SceneCameraUndo.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "selection-undo", Name = "Selection Undo", Category = EditoolsCategory.Controls,
			Tooltip = "Alt+Z restores the previous object selection, Alt+Y the next one (scene objects and project assets). " +
				"Selection only; Unity's Ctrl+Z action undo is unchanged.",
			ShortcutIds = new[] { "Editools/Selection Undo", "Editools/Selection Redo" },
		}, s => s.selectionUndoEnabled, (s, v) => s.selectionUndoEnabled = v, SelectionUndo.Sync));

		// ── Editor Tools ──

		list.Add(Gate(new EditoolsFunction
		{
			Id = "quick-access", Name = "Quick Access", Category = EditoolsCategory.EditorTools,
			Tooltip = "Dockable panel for pinning scene objects and project assets. Ctrl+1..0 saves selection groups, " +
				"1..0 recalls them, Shift+1..9 recalls Scene View anchors.",
			ShortcutText = "Ctrl + 1..0 / 1..0 / Shift + 1..9",
		}, s => s.quickAccessEnabled, (s, v) => s.quickAccessEnabled = v, QuickAccess.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "mass-rename", Name = "Mass Rename", Category = EditoolsCategory.EditorTools,
			Tooltip = "F2 batch-renames the selected objects or assets: find / replace with wildcards, prefix / suffix removal, numbering.",
			ShortcutIds = new[] { "Editools/Mass Rename" },
		}, s => s.massRenameEnabled, (s, v) => s.massRenameEnabled = v));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "hierarchy-heatmap", Name = "Hierarchy Heatmap", Category = EditoolsCategory.EditorTools,
			Tooltip = "Color-codes recently selected items in the Hierarchy and Project windows; the most recent are warmer. " +
				"Alt+D marks the selection, Alt+Shift+D clears all marks.",
			ShortcutIds = new[] { "Editools/Toggle Heatmap Mark", "Editools/Clear All Heatmap Marks" },
			DrawSettings = DrawHeatmapSettings,
		}, s => s.hierarchyHeatmapEnabled, (s, v) => s.hierarchyHeatmapEnabled = v, HierarchyHeatmap.Sync));

		list.Add(Gate(new EditoolsFunction
		{
			Id = "sdf-generator", Name = "SDF Generator", Category = EditoolsCategory.EditorTools,
			Tooltip = "Bakes a source texture into a signed distance field: channel, spread, smoothing and output packing, " +
				"with preview and PNG export. Opens the window.",
			OverlayKind = EditoolsOverlayKind.Element,
			Icon = () => EditoolsIcons.Custom("gradient"),
			Activate = _ => SdfGeneratorWindow.ShowWindow(),
		}, s => s.sdfGeneratorEnabled, (s, v) => s.sdfGeneratorEnabled = v));

		// Default toolbar buttons for functions that declared none: a state toggle or an action button.
		foreach (var f in list)
		{
			if (f.OverlayKind == EditoolsOverlayKind.Toggle && f.CreateOverlayElement == null)
				f.CreateOverlayElement = _ => MakeToggle(f);
			else if (f.OverlayKind == EditoolsOverlayKind.Element && f.CreateOverlayElement == null)
			{
				var activate = f.Activate;
				f.CreateOverlayElement = _ => MakeActionButton(f, () => activate(default));
			}
		}

		return list;
	}
}
#endif
