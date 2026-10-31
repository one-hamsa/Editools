#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Per-user Editools settings: which functions are enabled, which ones show as buttons in the
/// Scene View toolbar overlay, and every function's own parameters. Saved to
/// <c>UserSettings/EditoolsSettings.asset</c> (git-ignored). Edited through its custom inspector
/// (<see cref="EditoolsSettingsEditor"/>), opened with <see cref="OpenInInspector"/>.
/// Tools read their values through <see cref="Current"/>; a change to a function's enabled state
/// goes through its <see cref="EditoolsFunction.SetEnabled"/> so the tool can subscribe or unsubscribe.
/// </summary>
[FilePath("UserSettings/EditoolsSettings.asset", FilePathAttribute.Location.ProjectFolder)]
sealed class EditoolsSettings : ScriptableSingleton<EditoolsSettings>
{
	const int k_CurrentVersion = 1;

	/// <summary>Raised after any value changed and was saved. Overlays and tools re-read state on it.</summary>
	internal static event Action Changed;

	// Plain cached reference: avoids the UnityEngine.Object null comparison inside the
	// ScriptableSingleton.instance getter on per-event paths.
	static EditoolsSettings s_current;
	static bool s_hasCurrent;

	internal static EditoolsSettings Current => s_hasCurrent ? s_current : instance;

	[Tooltip("Settings file format version. 0 means a fresh file whose values are migrated from EditorPrefs.")]
	public int version;

	// ─── Function gates ──────────────────────────────────────────

	[Tooltip("Capture Scene View screenshots into the overlay strip and compare them against the current view.")]
	public bool screenshotCaptureEnabled = true;
	[Tooltip("Material Check: preview one material on every opaque scene renderer.")]
	public bool materialCheckEnabled = true;
	[Tooltip("Select And Replace Material: floating panel that selects renderers by material / shader and can replace their material.")]
	public bool selectByMaterialEnabled = true;
	[Tooltip("I + left-click in the Scene View selects the material under the cursor.")]
	public bool selectMaterialEnabled = true;
	[Tooltip("Grey Primitives: Greybox / Greypipe / Greyroad / Greyquad creation shortcuts, menu items and Edit Mode.")]
	public bool greyPrimitivesEnabled = true;
	[Tooltip("Alt+A drops the selected object onto the surface under the cursor.")]
	public bool snapToSurfaceEnabled = true;
	[Tooltip("Ctrl+= adds objects near the selection to the selection.")]
	public bool distanceSelectEnabled = true;
	[Tooltip("Change Orientation: floating Scene View panel with X / Y / Z buttons that rotate the selection 90 degrees around a local axis.")]
	public bool changeOrientationEnabled = true;
	[Tooltip("Whether the Change Orientation panel is currently shown (its toolbar button / menu row).")]
	public bool changeOrientationShown;
	[Tooltip("Replace Object window: replaces the selected objects with copies of a reference object.")]
	public bool replaceObjectEnabled = true;
	[Tooltip("Plane To Quad: swaps the built-in 121-vertex Plane mesh on the selection for a 4-vertex quad.")]
	public bool planeToQuadEnabled = true;
	[Tooltip("Select Shadow Only: selects every renderer whose Cast Shadows is Shadows Only.")]
	public bool selectShadowOnlyEnabled = true;
	[Tooltip("Alt+Ctrl+C / V / X copy, paste and reset the local transform of the selection.")]
	public bool copyPasteTransformEnabled = true;
	[Tooltip("Trigger Debug: paints the scene grey and tints the inside of the selected trigger volumes.")]
	public bool triggerDebugEnabled = true;
	[Tooltip("Vertex counter in the top-left of each Scene View (scene / view / selection).")]
	public bool vertexCounterEnabled = true;
	[Tooltip("Whether the vertex counter is currently drawn (its toolbar button / menu row).")]
	public bool vertexCounterShown;
	[Tooltip("FPS counter in the top-left of each Scene View.")]
	public bool fpsCounterEnabled = true;
	[Tooltip("Whether the FPS counter is currently drawn (its toolbar button / menu row).")]
	public bool fpsCounterShown;
	[Tooltip("Gamma Corrector swatch: converts a picked or pasted color between gamma and linear.")]
	public bool gammaCorrectorEnabled = true;
	[Tooltip("Y cycles the selected objects between Normal, Bounding Box, Greybox and Outline display.")]
	public bool objectDisplayToggleEnabled = true;
	[Tooltip("Ctrl+click only adds to the selection, Alt+click removes from it.")]
	public bool altDeselectionEnabled;
	[Tooltip("Ctrl+Alt + middle-mouse drag rolls the Scene View camera; click resets the roll.")]
	public bool sceneViewRollEnabled = true;
	[Tooltip("Hold W / E / R and drag in the Scene View to move, rotate or scale without gizmo handles.")]
	public bool quickTransformEnabled = true;
	[Tooltip("Shift+Z / Shift+Y step back and forward through Scene View camera positions.")]
	public bool sceneViewUndoEnabled = true;
	[Tooltip("Alt+Z / Alt+Y restore the previous / next object selection.")]
	public bool selectionUndoEnabled = true;
	[Tooltip("Quick Access: on keeps its window open and its Ctrl+1..0 / 1..0 / Shift+1..9 shortcuts live; off closes the window.")]
	public bool quickAccessEnabled;
	[Tooltip("F2 batch-renames the selected objects or assets.")]
	public bool massRenameEnabled = true;
	[Tooltip("Hierarchy Heatmap: color-codes recently selected items in the Hierarchy and Project windows, plus Alt+D marks.")]
	public bool hierarchyHeatmapEnabled;
	[Tooltip("SDF Generator window: bakes a texture into a signed distance field.")]
	public bool sdfGeneratorEnabled = true;
	[Tooltip("FBX Export: bakes the selected renderers (current pose, world space) to one binary FBX file.")]
	public bool fbxExportEnabled = true;

	[Tooltip("Functions shown as their own button in the Scene View toolbar overlay, by function id.")]
	public List<string> overlayFunctionIds = new List<string>
	{
		"screenshot-capture", "material-check", "select-by-material", "trigger-debug", "gamma-corrector"
	};

	// ─── QuickTransform ──────────────────────────────────────────

	[Tooltip("Pixel distance from an edge of the selection box that counts as hovering it.")]
	[Range(4f, 30f)] public float quickTransformEdgeHoverPx = 10f;
	[Tooltip("Size multiplier of the rotation feedback circle drawn while rotating.")]
	[Range(0.3f, 5f)] public float quickTransformCircleRadius = 1.2f;
	[Tooltip("Angle increment in degrees when holding Ctrl while rotating.")]
	[Range(1f, 90f)] public float quickTransformRotSnapAngle = 15f;
	[Tooltip("Rotate from horizontal mouse movement instead of the radial motion around the pivot.")]
	public bool quickTransformLinearRotation;
	[Tooltip("Degrees of rotation per pixel of horizontal mouse movement in linear rotation mode.")]
	[Range(0.01f, 10f)] public float quickTransformLinearRotSensitivity = 0.5f;
	[Tooltip("Show the action hints at the bottom-left of the Scene View while a mode key is held.")]
	public bool quickTransformShowTooltips = true;

	// ─── Snap To Surface ─────────────────────────────────────────

	[Tooltip("Snap to SkinnedMeshRenderer surfaces. The posed mesh is baked when snapping starts.")]
	public bool snapSkinnedMeshes = true;
	[Tooltip("Also snap to objects that are not flagged Static.")]
	public bool snapNonStatic;
	[Tooltip("Also snap to surfaces whose material does not write depth (ZWrite Off).")]
	public bool snapTransparentMeshes;
	[Tooltip("Align the object's Z+ axis to the surface normal instead of its Y+ axis.")]
	public bool snapAlignZToSurface;
	[Tooltip("Distance to push the snapped object off the surface along its normal. Negative sinks it in.")]
	public float snapSurfaceOffset;
	[Tooltip("Point the aligned axis into the surface instead of out of it.")]
	public bool snapFlipAxis;

	// ─── Grey Primitives ─────────────────────────────────────────

	[Tooltip("Material applied to new grey primitives. Empty uses the built-in default material.")]
	public Material greyDefaultMaterial;
	[Tooltip("Mark new primitives as static.")]
	public bool greyDefaultStatic = true;
	[Tooltip("Layer assigned to new primitives.")]
	public int greyDefaultLayer;
	[Tooltip("Add a MeshCollider to new primitives.")]
	public bool greyDefaultMeshCollider = true;
	[Tooltip("New primitives cast shadows.")]
	public bool greyDefaultShadowCasting = true;
	[Tooltip("Recenter Greybox pivots after shape edits without moving the geometry or child objects.")]
	public bool greyAutoUpdatePivot = true;
	[Tooltip("Show the action hints in the Scene View while Grey Primitive Edit Mode is on.")]
	public bool greyEditModeTooltips = true;
	[Tooltip("Local scale of new Greyboxes. The mesh is a unit cube, so 10 gives a 10x10x10 box.")]
	public Vector3 greyboxDefaultScale = new Vector3(10f, 10f, 10f);
	[Tooltip("Base radius of the cross-section of new Greypipes.")]
	[Min(0.001f)] public float greypipeDefaultGirth = 0.5f;
	[Tooltip("Length between the two end vertices of new Greypipes, in local units.")]
	[Min(0.01f)] public float greypipeDefaultLength = 5f;
	[Tooltip("Local X / Z scale of new Greyquads. The mesh is a unit quad, so 10 gives a 10x10 quad.")]
	public Vector2 greyquadDefaultSize = new Vector2(10f, 10f);
	[Tooltip("Base width of the box cross-section of new Greyroads.")]
	[Min(0.001f)] public float greyroadDefaultWidth = 10f;
	[Tooltip("Base height (thickness) of the box cross-section of new Greyroads.")]
	[Min(0.001f)] public float greyroadDefaultHeight = 2f;
	[Tooltip("Length between the two end vertices of new Greyroads, in local units.")]
	[Min(0.01f)] public float greyroadDefaultLength = 5f;

	// ─── Hierarchy Heatmap ───────────────────────────────────────

	[Tooltip("Row color of the least recently selected tracked item.")]
	public Color heatmapLowColor = new Color(0.9f, 0.9f, 1f, 1f);
	[Tooltip("Row color of the most recently selected item.")]
	public Color heatmapHighColor = new Color(1f, 0.2f, 0.2f, 1f);
	[Tooltip("Opacity of the heat tint drawn over a row.")]
	[Range(0f, 1f)] public float heatmapAlpha = 0.3f;
	[Tooltip("How far the tint moves from the low color toward the high color across the recency range.")]
	[Range(0f, 1f)] public float heatmapGradientStrength = 0.5f;
	[Tooltip("Number of recent selections tracked per window.")]
	[Range(1, 20)] public int heatmapMaxRecent = 5;
	[Tooltip("Row color of items marked with Alt+D.")]
	public Color heatmapMarkColor = new Color(1f, 0.8f, 0f, 1f);
	[Tooltip("Opacity of the mark tint drawn over a row.")]
	[Range(0f, 1f)] public float heatmapMarkAlpha = 0.4f;
	[Tooltip("Maximum number of marked items per window; the oldest mark is dropped beyond it.")]
	[Range(1, 50)] public int heatmapMaxMarked = 10;

	// ─── Material Check ──────────────────────────────────────────

	[Tooltip("Material that Material Check applies to every opaque scene renderer while it is on.")]
	public Material materialCheckMaterial;

	// ─── FBX Export ──────────────────────────────────────────────

	[Tooltip("Merge adjacent coplanar triangle pairs into quads in the exported FBX. Off exports the triangles as they are.")]
	public bool fbxExportQuads = true;
	[Tooltip("Largest angle in degrees between the face normals of two adjacent triangles that still merges them into a quad.")]
	[Range(0f, 90f)] public float fbxExportQuadMaxAngle = 40f;

	// ─── Trigger Debug ───────────────────────────────────────────

	[Tooltip("Materials Trigger Debug paints in addition to the opaque scene fill, e.g. transparent ones it would otherwise skip.")]
	public List<Material> triggerDebugWhitelist = new List<Material>();

	// ─── Lifecycle ───────────────────────────────────────────────

	void OnEnable()
	{
		// ScriptableSingleton marks the object NotEditable, which greys out its inspector.
		hideFlags = HideFlags.DontSave;
		s_current = this;
		s_hasCurrent = true;

		if (version < k_CurrentVersion)
		{
			EditoolsSettingsMigration.MigrateFromEditorPrefs(this);
			version = k_CurrentVersion;
			Save(true);
		}
	}

	void OnDisable()
	{
		if (s_current == this)
		{
			s_current = null;
			s_hasCurrent = false;
		}
	}

	/// <summary>Writes the file and notifies listeners. Call after any change made outside the inspector.</summary>
	internal void SaveAndNotify()
	{
		Save(true);
		Changed?.Invoke();
	}

	internal static void RaiseChanged() => Changed?.Invoke();

	/// <summary>Writes one bool field with undo and saves. Used by the shown / active toggles.</summary>
	internal static void SetBool(ref bool field, bool value, string undoName)
	{
		if (field == value) return;
		var settings = Current;
		Undo.RecordObject(settings, undoName);
		field = value;
		settings.SaveAndNotify();
	}

	// ─── Overlay membership ──────────────────────────────────────

	internal bool IsInOverlay(string functionId) => overlayFunctionIds.Contains(functionId);

	internal void SetInOverlay(string functionId, bool inOverlay)
	{
		if (inOverlay == IsInOverlay(functionId)) return;
		Undo.RecordObject(this, inOverlay ? "Add To Editools Overlay" : "Remove From Editools Overlay");
		if (inOverlay) overlayFunctionIds.Add(functionId);
		else overlayFunctionIds.Remove(functionId);
		SaveAndNotify();
	}

	// ─── Inspector access ────────────────────────────────────────

	/// <summary>Selects the settings object so the Inspector window shows it.</summary>
	internal static void OpenInInspector()
	{
		Selection.activeObject = instance;
		EditorGUIUtility.PingObject(instance);
	}
}

/// <summary>
/// One-shot copy of the pre-file Editools settings out of EditorPrefs into a fresh
/// <see cref="EditoolsSettings"/>. Keys are the ones the tools used before the settings file existed.
/// EditorPrefs values are left in place; the file's version field keeps this from running twice.
/// </summary>
static class EditoolsSettingsMigration
{
	internal static void MigrateFromEditorPrefs(EditoolsSettings s)
	{
		s.selectMaterialEnabled      = EditorPrefs.GetBool("Editools_SelectMaterial_Enabled", s.selectMaterialEnabled);
		s.snapToSurfaceEnabled       = EditorPrefs.GetBool("SnapToSurface_Enabled", s.snapToSurfaceEnabled);
		s.changeOrientationShown     = EditorPrefs.GetBool("Editools_ChangeOrientation_Enabled", s.changeOrientationShown);
		s.vertexCounterShown         = EditorPrefs.GetBool("SceneViewVertexCounter_Enabled", s.vertexCounterShown);
		s.fpsCounterShown            = EditorPrefs.GetBool("SceneViewFpsCounter_Enabled", s.fpsCounterShown);
		s.gammaCorrectorEnabled      = EditorPrefs.GetBool("Editools_GammaCorrector_Enabled", s.gammaCorrectorEnabled);
		s.objectDisplayToggleEnabled = EditorPrefs.GetBool("ObjectDisplayToggle_Enabled", s.objectDisplayToggleEnabled);
		s.altDeselectionEnabled      = EditorPrefs.GetBool("AltDeselection_Enabled", s.altDeselectionEnabled);
		s.sceneViewRollEnabled       = EditorPrefs.GetBool("SceneViewRoll_Enabled", s.sceneViewRollEnabled);
		s.quickTransformEnabled      = EditorPrefs.GetBool("QuickTransform_Enabled", s.quickTransformEnabled);
		s.sceneViewUndoEnabled       = EditorPrefs.GetBool("SceneCameraUndo_Enabled", s.sceneViewUndoEnabled);
		s.selectionUndoEnabled       = EditorPrefs.GetBool("SelectionUndo_Enabled", s.selectionUndoEnabled);
		s.massRenameEnabled          = EditorPrefs.GetBool("MassRename_Enabled", s.massRenameEnabled);
		s.hierarchyHeatmapEnabled    = EditorPrefs.GetBool("HierarchyHeatmapEnabled", s.hierarchyHeatmapEnabled);

		s.quickTransformEdgeHoverPx          = EditorPrefs.GetFloat("QuickTransform_EdgeHoverPx", s.quickTransformEdgeHoverPx);
		s.quickTransformCircleRadius         = EditorPrefs.GetFloat("QuickTransform_CircleRadius", s.quickTransformCircleRadius);
		s.quickTransformRotSnapAngle         = EditorPrefs.GetFloat("QuickTransform_RotSnapAngle", s.quickTransformRotSnapAngle);
		s.quickTransformLinearRotation       = EditorPrefs.GetBool("QuickTransform_LinearRot", s.quickTransformLinearRotation);
		s.quickTransformLinearRotSensitivity = EditorPrefs.GetFloat("QuickTransform_LinearRotSens", s.quickTransformLinearRotSensitivity);
		s.quickTransformShowTooltips         = EditorPrefs.GetBool("QuickTransform_ShowTooltips", s.quickTransformShowTooltips);

		s.snapSkinnedMeshes     = EditorPrefs.GetBool("SnapToSurface_SnapSkinned", s.snapSkinnedMeshes);
		s.snapNonStatic         = EditorPrefs.GetBool("SnapToSurface_SnapNonStatic", s.snapNonStatic);
		s.snapTransparentMeshes = EditorPrefs.GetBool("SnapToSurface_SnapTransparent", s.snapTransparentMeshes);
		s.snapAlignZToSurface   = EditorPrefs.GetBool("SnapToSurface_AlignZ", s.snapAlignZToSurface);
		s.snapSurfaceOffset     = EditorPrefs.GetFloat("SnapToSurface_Offset", s.snapSurfaceOffset);
		s.snapFlipAxis          = EditorPrefs.GetBool("SnapToSurface_FlipAxis", s.snapFlipAxis);

		s.greyDefaultMaterial      = LoadByGuid<Material>(EditorPrefs.GetString("Editools_Greybox_DefaultMatGUID", ""));
		s.greyDefaultStatic        = EditorPrefs.GetBool("Editools_Greybox_DefaultStatic", s.greyDefaultStatic);
		s.greyDefaultLayer         = EditorPrefs.GetInt("Editools_Greybox_DefaultLayer", s.greyDefaultLayer);
		s.greyDefaultMeshCollider  = EditorPrefs.GetBool("Editools_Greybox_DefaultMeshCollider", s.greyDefaultMeshCollider);
		s.greyDefaultShadowCasting = EditorPrefs.GetBool("Editools_Greybox_DefaultShadowCast", s.greyDefaultShadowCasting);
		s.greyAutoUpdatePivot      = EditorPrefs.GetBool("Editools_GreyPrimitive_AutoUpdatePivot", s.greyAutoUpdatePivot);
		s.greyboxDefaultScale = new Vector3(
			EditorPrefs.GetFloat("Editools_Greybox_DefaultScaleX", s.greyboxDefaultScale.x),
			EditorPrefs.GetFloat("Editools_Greybox_DefaultScaleY", s.greyboxDefaultScale.y),
			EditorPrefs.GetFloat("Editools_Greybox_DefaultScaleZ", s.greyboxDefaultScale.z));
		s.greypipeDefaultGirth  = EditorPrefs.GetFloat("Editools_Greypipe_DefaultGirth", s.greypipeDefaultGirth);
		s.greypipeDefaultLength = EditorPrefs.GetFloat("Editools_Greypipe_DefaultLength", s.greypipeDefaultLength);
		s.greyquadDefaultSize = new Vector2(
			EditorPrefs.GetFloat("Editools_Greyquad_DefaultSizeX", s.greyquadDefaultSize.x),
			EditorPrefs.GetFloat("Editools_Greyquad_DefaultSizeZ", s.greyquadDefaultSize.y));
		s.greyroadDefaultWidth  = EditorPrefs.GetFloat("Editools_Greyroad_DefaultWidth", s.greyroadDefaultWidth);
		s.greyroadDefaultHeight = EditorPrefs.GetFloat("Editools_Greyroad_DefaultHeight", s.greyroadDefaultHeight);
		s.greyroadDefaultLength = EditorPrefs.GetFloat("Editools_Greyroad_DefaultLength", s.greyroadDefaultLength);

		s.heatmapLowColor = ReadColor("HierarchyHeatmap_Low", s.heatmapLowColor);
		s.heatmapHighColor = ReadColor("HierarchyHeatmap_High", s.heatmapHighColor);
		s.heatmapAlpha = EditorPrefs.GetFloat("HierarchyHeatmap_Alpha", s.heatmapAlpha);
		s.heatmapGradientStrength = EditorPrefs.GetFloat("HierarchyHeatmap_GradientStrength", s.heatmapGradientStrength);
		s.heatmapMaxRecent = EditorPrefs.GetInt("HierarchyHeatmap_MaxRecent", s.heatmapMaxRecent);
		s.heatmapMarkColor = ReadColor("HierarchyHeatmap_Mark", s.heatmapMarkColor);
		s.heatmapMarkAlpha = EditorPrefs.GetFloat("HierarchyHeatmap_MarkAlpha", s.heatmapMarkAlpha);
		s.heatmapMaxMarked = EditorPrefs.GetInt("HierarchyHeatmap_MaxMarked", s.heatmapMaxMarked);

		s.materialCheckMaterial = LoadByGuid<Material>(EditorPrefs.GetString("AdvancedVisuals_MaterialCheck_GUID", ""));

		s.triggerDebugWhitelist.Clear();
		foreach (string guid in EditorPrefs.GetString("Editools_TriggerDebug_MaterialWhitelist", "").Split(';'))
		{
			var mat = LoadByGuid<Material>(guid);
			if (mat != null) s.triggerDebugWhitelist.Add(mat);
		}
	}

	static Color ReadColor(string prefix, Color fallback) => new Color(
		EditorPrefs.GetFloat(prefix + "R", fallback.r),
		EditorPrefs.GetFloat(prefix + "G", fallback.g),
		EditorPrefs.GetFloat(prefix + "B", fallback.b),
		EditorPrefs.GetFloat(prefix + "A", fallback.a));

	static T LoadByGuid<T>(string guid) where T : UnityEngine.Object
	{
		if (string.IsNullOrEmpty(guid)) return null;
		string path = AssetDatabase.GUIDToAssetPath(guid);
		return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
	}
}
#endif
