#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Manager window for the Trigger Debug material whitelist. Opened by right-clicking
/// the Trigger Debug toolbar button. Add materials via the picker field or by dragging
/// them anywhere into the window; remove with the per-row X. An empty list means the
/// mode paints every opaque renderer (the default full-scene behavior).
/// </summary>
class TriggerDebugWhitelistWindow : EditorWindow
{
	static readonly GUIContent k_AddLabel = new GUIContent("Add Material",
		"Pick a material to whitelist. You can also drag materials anywhere into this window, or onto the Trigger Debug toolbar button.");

	readonly List<Material> _materials = new List<Material>();
	Vector2 _scroll;

	[MenuItem("Tools/Editools/Trigger Debug Whitelist")]
	internal static void Open()
	{
		var window = GetWindow<TriggerDebugWhitelistWindow>(utility: true, title: "Trigger Debug Whitelist");
		window.minSize = new Vector2(300, 220);
		window.Show();
	}

	void OnEnable()
	{
		RefreshList();
		TriggerDebugWhitelist.Changed += RefreshList;
	}

	void OnDisable()
	{
		TriggerDebugWhitelist.Changed -= RefreshList;
	}

	void RefreshList()
	{
		TriggerDebugWhitelist.GetMaterials(_materials);
		Repaint();
	}

	void OnGUI()
	{
		EditorGUILayout.HelpBox(
			"Extra materials Trigger Debug paints in addition to the normal opaque scene fill. " +
			"Whitelist materials that are otherwise skipped — e.g. transparent/fade ones — to include their renderers in the debug view.",
			MessageType.Info);

		DrawRows();
		DrawAddField();
		DrawFooter();
		HandleWindowDragAndDrop();
	}

	void DrawRows()
	{
		// Mutations are collected and applied after the loop — changing the whitelist
		// mid-draw rebuilds _materials via the Changed event and breaks IMGUI layout.
		Material removeTarget = null;
		Material replaceFrom = null;
		Material replaceTo = null;

		_scroll = EditorGUILayout.BeginScrollView(_scroll);

		if (_materials.Count == 0)
			EditorGUILayout.LabelField("Whitelist is empty — default behavior (opaque scene fill only).", EditorStyles.centeredGreyMiniLabel);

		for (int i = 0; i < _materials.Count; i++)
		{
			EditorGUILayout.BeginHorizontal();

			var current = _materials[i];
			var replacement = (Material)EditorGUILayout.ObjectField(current, typeof(Material), allowSceneObjects: false);
			if (replacement != current)
			{
				replaceFrom = current;
				replaceTo = replacement;
			}

			if (GUILayout.Button("X", GUILayout.Width(22)))
				removeTarget = current;

			EditorGUILayout.EndHorizontal();
		}

		EditorGUILayout.EndScrollView();

		if (removeTarget != null)
			TriggerDebugWhitelist.Remove(removeTarget);
		if (replaceFrom != null)
		{
			TriggerDebugWhitelist.Remove(replaceFrom);
			TriggerDebugWhitelist.Add(replaceTo);
		}
	}

	void DrawAddField()
	{
		var added = (Material)EditorGUILayout.ObjectField(k_AddLabel, null, typeof(Material), allowSceneObjects: false);
		if (added != null)
			TriggerDebugWhitelist.Add(added);
	}

	void DrawFooter()
	{
		using (new EditorGUI.DisabledScope(_materials.Count == 0))
		{
			if (GUILayout.Button("Clear All"))
				TriggerDebugWhitelist.Clear();
		}
	}

	// Whole-window drop target: dragging materials anywhere into the window adds them.
	void HandleWindowDragAndDrop()
	{
		Event e = Event.current;
		if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
			return;
		if (!DragContainsMaterial())
			return;

		DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

		if (e.type == EventType.DragPerform)
		{
			DragAndDrop.AcceptDrag();
			foreach (var obj in DragAndDrop.objectReferences)
				if (obj is Material mat)
					TriggerDebugWhitelist.Add(mat);
		}
		e.Use();
	}

	static bool DragContainsMaterial()
	{
		foreach (var obj in DragAndDrop.objectReferences)
			if (obj is Material)
				return true;
		return false;
	}
}
#endif
