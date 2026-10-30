#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="EditoolsSettings"/>: functions grouped by category, each with an
/// enabled checkbox, its current shortcut in grey brackets, a foldout for its own settings when it
/// has any, and an "Overlay" checkbox that adds it to the Scene View toolbar as its own button when
/// the function supports that.
/// </summary>
[CustomEditor(typeof(EditoolsSettings))]
sealed class EditoolsSettingsEditor : Editor
{
	const float k_RowHeight = 20f;
	const float k_ToggleWidth = 18f;
	const float k_OverlayColumnWidth = 52f;
	const string k_FoldoutKeyPrefix = "Editools_SettingsFoldout_";

	static readonly GUIContent k_OverlayHeader = new GUIContent("Overlay",
		"Checked functions appear as their own button in the Scene View toolbar overlay. " +
		"Unchecked ones are reachable from the overlay's settings menu.");

	static readonly (EditoolsCategory category, string title)[] k_Categories =
	{
		(EditoolsCategory.SceneTools, "Scene Tools"),
		(EditoolsCategory.Helpers, "Helpers"),
		(EditoolsCategory.Controls, "Controls"),
		(EditoolsCategory.EditorTools, "Editor Tools"),
	};

	static GUIStyle s_categoryStyle;
	static GUIStyle s_rowLabelStyle;
	static GUIStyle s_shortcutStyle;

	void OnEnable() => Undo.undoRedoPerformed += OnUndoRedo;
	void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

	void OnUndoRedo()
	{
		if (target == null) return;
		((EditoolsSettings)target).SaveAndNotify();
		EditoolsFunctions.SyncAll();
		Repaint();
	}

	static void EnsureStyles()
	{
		if (s_categoryStyle != null) return;
		s_categoryStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
		s_rowLabelStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft };
		s_shortcutStyle = new GUIStyle(EditorStyles.label)
		{
			alignment = TextAnchor.MiddleLeft,
			normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 1f) },
			hover = { textColor = new Color(0.6f, 0.6f, 0.6f, 1f) }
		};
	}

	public override void OnInspectorGUI()
	{
		EnsureStyles();
		serializedObject.Update();

		DrawColumnHeader();

		foreach (var (category, title) in k_Categories)
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField(title, s_categoryStyle);
			foreach (var function in EditoolsFunctions.All)
				if (function.Category == category)
					DrawFunction(function);
		}

		EditorGUILayout.Space(8);
		EditorGUILayout.LabelField(
			"Stored per user in UserSettings/EditoolsSettings.asset. Functions whose checkbox is off have no editor cost.",
			EditorStyles.miniLabel);

		if (serializedObject.ApplyModifiedProperties())
			((EditoolsSettings)target).SaveAndNotify();
	}

	void DrawColumnHeader()
	{
		var rect = EditorGUILayout.GetControlRect(false, k_RowHeight);
		var overlayRect = new Rect(rect.xMax - k_OverlayColumnWidth, rect.y, k_OverlayColumnWidth, rect.height);
		GUI.Label(overlayRect, k_OverlayHeader, EditorStyles.miniBoldLabel);
	}

	void DrawFunction(EditoolsFunction function)
	{
		var rect = EditorGUILayout.GetControlRect(false, k_RowHeight);
		var toggleRect = new Rect(rect.x, rect.y, k_ToggleWidth, rect.height);
		var overlayRect = new Rect(rect.xMax - k_OverlayColumnWidth, rect.y, k_OverlayColumnWidth, rect.height);
		var labelRect = new Rect(toggleRect.xMax + 2f, rect.y, overlayRect.x - toggleRect.xMax - 6f, rect.height);

		bool enabled = function.IsEnabled;
		bool newEnabled = GUI.Toggle(toggleRect, enabled, GUIContent.none);
		if (newEnabled != enabled)
			function.SetEnabled(newEnabled);

		var label = new GUIContent(function.Name, function.Tooltip);
		float nameWidth = s_rowLabelStyle.CalcSize(label).x;
		bool expanded = false;
		if (function.HasSettings)
		{
			labelRect.x += 12f;
			labelRect.width -= 12f;
			nameWidth += 14f;
			expanded = GetFoldout(function.Id);
			bool newExpanded = EditorGUI.Foldout(labelRect, expanded, label, true);
			if (newExpanded != expanded)
			{
				expanded = newExpanded;
				SetFoldout(function.Id, expanded);
			}
		}
		else
		{
			if (GUI.Button(labelRect, label, s_rowLabelStyle))
				function.SetEnabled(!enabled);
		}

		DrawInput(function, new Rect(labelRect.x + nameWidth + 4f, rect.y, labelRect.xMax - labelRect.x - nameWidth - 4f, rect.height));

		if (function.IsOverlayViable)
		{
			var iconRect = new Rect(overlayRect.x, rect.y + 2f, 16f, 16f);
			var icon = function.Icon?.Invoke();
			if (icon != null)
				GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);

			var overlayToggleRect = new Rect(overlayRect.xMax - k_ToggleWidth - 8f, rect.y, k_ToggleWidth, rect.height);
			bool inOverlay = function.IsInOverlay;
			bool newInOverlay = GUI.Toggle(overlayToggleRect, inOverlay,
				new GUIContent(string.Empty, "Show " + function.Name + " as a button in the Scene View toolbar."));
			if (newInOverlay != inOverlay)
				((EditoolsSettings)target).SetInOverlay(function.Id, newInOverlay);
		}

		if (expanded)
		{
			EditorGUI.indentLevel += 2;
			using (new EditorGUI.DisabledScope(!function.IsEnabled))
				function.DrawSettings(serializedObject);
			EditorGUI.indentLevel -= 2;
			EditorGUILayout.Space(4);
		}
	}

	// Current shortcut binding (or fixed input text) in grey brackets after the function name.
	static void DrawInput(EditoolsFunction function, Rect rect)
	{
		if (rect.width < 20f) return;
		string input = function.DescribeInput();
		if (string.IsNullOrEmpty(input)) return;
		GUI.Label(rect, new GUIContent("[" + input + "]", "Current input. Rebindable shortcuts: Edit > Shortcuts, Editools category."), s_shortcutStyle);
	}

	static bool GetFoldout(string id) => SessionState.GetBool(k_FoldoutKeyPrefix + id, false);
	static void SetFoldout(string id, bool value) => SessionState.SetBool(k_FoldoutKeyPrefix + id, value);
}
#endif
