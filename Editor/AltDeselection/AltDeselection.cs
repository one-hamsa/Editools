using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Scene View click selection with separate add and remove modifiers.
/// Ctrl + click adds the unselected object under the cursor (Unity's Ctrl + click toggle is suppressed).
/// Alt + click without drag removes the selected object under the cursor.
/// Box selection and Alt + drag camera orbit are untouched.
/// </summary>
static class AltDeselection
{
	static bool s_subscribed;

	// Unity's modifier + click picking is a mouse shortcut that fires on release and sends this command
	// to the Scene View; RectSelection toggles the selection when it receives it.
	const string k_PickingCommandName = "SceneViewPickingEventCommand";

	// Same distance Unity uses to tell a click from a box-select drag.
	const float k_ClickDragThreshold = 6f;

	// Upper bound on overlapping unselected objects looked through by an Alt + click.
	const int k_MaxPickDepth = 32;

	enum ClickMode { None, Add, Remove }

	struct PendingClick
	{
		public ClickMode mode;
		public SceneView view;
		public Vector2 mouse;
		public GameObject hovered;
		public GameObject selectionRoot;
	}

	static PendingClick s_click;

	internal static bool Enabled => EditoolsSettings.Current.altDeselectionEnabled;

	/// <summary>Hooks the Scene View while enabled and the overlay is active; unhooks otherwise.</summary>
	internal static void Sync()
	{
		bool wanted = EditoolsOverlay.IsActive && Enabled;
		if (wanted == s_subscribed) return;
		s_subscribed = wanted;

		if (wanted)
		{
			SceneView.beforeSceneGui += OnBeforeSceneGUI;
			SceneView.duringSceneGui += OnSceneGUI;
		}
		else
		{
			SceneView.beforeSceneGui -= OnBeforeSceneGUI;
			SceneView.duringSceneGui -= OnSceneGUI;
			CancelClick();
		}
	}

	// Records the click. Picking needs the Scene View camera, which is set up by the time this callback runs.
	static void OnSceneGUI(SceneView view)
	{
		Event e = Event.current;
		if (e == null || e.rawType != EventType.MouseDown || e.button != 0) return;

		ClickMode mode = ClickMode.None;
		if (EditorGUI.actionKey && !e.alt && !e.shift)
		{
			// A handle that already captured the mouse also stops Unity's own picking.
			if (GUIUtility.hotControl != 0) return;
			mode = ClickMode.Add;
		}
		else if (e.alt && !e.control && !e.command && !e.shift)
		{
			mode = ClickMode.Remove;
		}
		if (mode == ClickMode.None) return;

		// Selected hierarchies are skipped when adding and are the only targets when removing,
		// so an object of the other kind in front never blocks the click.
		GameObject[] selectedHierarchies = GetSelectedHierarchies();
		GameObject hovered;
		GameObject selectionRoot = null;
		if (mode == ClickMode.Add)
		{
			hovered = HandleUtility.PickGameObject(e.mousePosition, false, selectedHierarchies);
			if (hovered != null)
				selectionRoot = HandleUtility.PickGameObject(e.mousePosition, true, selectedHierarchies);
		}
		else
		{
			hovered = PickSelectedThroughUnselected(e.mousePosition, selectedHierarchies);
		}

		s_click = new PendingClick
		{
			mode = mode,
			view = view,
			mouse = e.mousePosition,
			hovered = hovered,
			selectionRoot = selectionRoot,
		};
	}

	// Picks front to back, skipping unselected objects, until a selected hierarchy is hit.
	static GameObject PickSelectedThroughUnselected(Vector2 position, GameObject[] selectedHierarchies)
	{
		if (selectedHierarchies.Length == 0) return null;

		var selectedSet = new HashSet<GameObject>(selectedHierarchies);
		var ignore = new List<GameObject>();
		for (int depth = 0; depth < k_MaxPickDepth; depth++)
		{
			GameObject picked = HandleUtility.PickGameObject(position, false, ignore.ToArray());
			if (picked == null) return null;
			if (selectedSet.Contains(picked)) return picked;
			ignore.Add(picked);
		}
		return null;
	}

	static GameObject[] GetSelectedHierarchies()
	{
		var result = new List<GameObject>();
		foreach (GameObject selected in Selection.gameObjects)
		{
			foreach (Transform t in selected.GetComponentsInChildren<Transform>(true))
				result.Add(t.gameObject);
		}
		return result.ToArray();
	}

	// Commits or cancels the recorded click. Runs before RectSelection, so the picking command can be
	// consumed here before Unity toggles the selection.
	static void OnBeforeSceneGUI(SceneView view)
	{
		if (s_click.mode == ClickMode.None) return;

		Event e = Event.current;
		if (e == null || view != s_click.view) return;

		// Camera controls can consume an event before this callback sees it.
		switch (e.rawType)
		{
			case EventType.MouseDown:
			case EventType.ScrollWheel:
			case EventType.MouseLeaveWindow:
				CancelClick();
				break;

			// A held modifier auto-repeats KeyDown, so only Escape cancels.
			case EventType.KeyDown:
				if (e.keyCode == KeyCode.Escape)
					CancelClick();
				break;

			case EventType.MouseDrag:
				if ((e.mousePosition - s_click.mouse).sqrMagnitude > k_ClickDragThreshold * k_ClickDragThreshold)
					CancelClick();
				break;

			case EventType.MouseUp:
				// An Add click is committed by the picking command that follows the release, not by the release itself.
				if (s_click.mode != ClickMode.Remove) break;
				if (e.button == 0 && EditorWindow.mouseOverWindow == view && ClickIsStillValid(e.mousePosition))
					RemoveFromSelection(s_click.hovered);
				CancelClick();
				break;

			case EventType.ExecuteCommand:
				if (s_click.mode != ClickMode.Add || e.commandName != k_PickingCommandName) break;
				if (ClickIsStillValid(s_click.mouse))
					AddToSelection(s_click.selectionRoot);
				CancelClick();
				e.Use();
				break;
		}
	}

	// The orbit tool rotates the camera on any Alt + drag, so a small cursor move is still a click.
	static bool ClickIsStillValid(Vector2 mouse)
	{
		return (mouse - s_click.mouse).sqrMagnitude <= k_ClickDragThreshold * k_ClickDragThreshold;
	}

	static void CancelClick() => s_click = default;

	static void AddToSelection(GameObject selectionRoot)
	{
		if (selectionRoot == null || Selection.Contains(selectionRoot)) return;

		Object[] current = Selection.objects;
		var objects = new Object[current.Length + 1];
		current.CopyTo(objects, 0);
		objects[current.Length] = selectionRoot;
		Selection.objects = objects;
	}

	// Removes the hovered object and every selected ancestor, so whichever selected object contains the click is deselected.
	static void RemoveFromSelection(GameObject hovered)
	{
		if (hovered == null) return;

		var objects = new List<Object>(Selection.objects);
		bool changed = false;
		for (Transform t = hovered.transform; t != null; t = t.parent)
			changed |= objects.Remove(t.gameObject);
		if (changed)
			Selection.objects = objects.ToArray();
	}
}
