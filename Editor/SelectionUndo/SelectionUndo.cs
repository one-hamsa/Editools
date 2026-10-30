using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Undo and redo for object selection only, separate from Unity's action undo stack.
/// Every change to Selection.objects (scene objects or project assets) is recorded;
/// Alt + Z restores the previous selection, Alt + Y the next one.
/// History lives in memory and resets on domain reload. Recording is subscribed only while
/// enabled in Editools Settings and the overlay is displayed.
/// </summary>
static class SelectionUndo
{
	const int k_MaxHistory = 64;

	// Index Count - 1 is the selection currently applied.
	static readonly List<Object[]> s_undo = new List<Object[]>();
	static readonly List<Object[]> s_redo = new List<Object[]>();
	static bool s_subscribed;

	internal static bool Enabled => EditoolsSettings.Current.selectionUndoEnabled;

	/// <summary>Subscribes to selection changes while enabled and the overlay is active; unsubscribes otherwise.</summary>
	internal static void Sync()
	{
		bool wanted = EditoolsOverlay.IsActive && Enabled;
		if (wanted == s_subscribed) return;
		s_subscribed = wanted;

		if (wanted)
		{
			if (s_undo.Count == 0)
				s_undo.Add(Selection.objects);
			Selection.selectionChanged += OnSelectionChanged;
		}
		else
			Selection.selectionChanged -= OnSelectionChanged;
	}

	static void OnSelectionChanged()
	{
		Object[] current = Selection.objects;
		// A restore from this tool reports the snapshot it just applied; nothing to record and the redo stack stays.
		if (SameSelection(current, s_undo[s_undo.Count - 1])) return;

		s_undo.Add(current);
		if (s_undo.Count > k_MaxHistory)
			s_undo.RemoveAt(0);
		s_redo.Clear();
	}

	static bool SameSelection(Object[] a, Object[] b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++)
		{
			if (Array.IndexOf(b, a[i]) < 0) return false;
		}
		return true;
	}

	[Shortcut("Editools/Selection Undo", KeyCode.Z, ShortcutModifiers.Alt)]
	static void UndoSelection()
	{
		if (!s_subscribed) return;
		if (s_undo.Count <= 1) return;

		s_redo.Add(s_undo[s_undo.Count - 1]);
		s_undo.RemoveAt(s_undo.Count - 1);
		s_undo[s_undo.Count - 1] = Apply(s_undo[s_undo.Count - 1]);
	}

	[Shortcut("Editools/Selection Redo", KeyCode.Y, ShortcutModifiers.Alt)]
	static void RedoSelection()
	{
		if (!s_subscribed) return;
		if (s_redo.Count == 0) return;

		Object[] target = s_redo[s_redo.Count - 1];
		s_redo.RemoveAt(s_redo.Count - 1);
		s_undo.Add(Apply(target));
	}

	// Objects deleted since the snapshot was taken are dropped rather than selected as missing references.
	// Returns what was actually applied so the history top matches the selection Unity reports back.
	static Object[] Apply(Object[] snapshot)
	{
		Object[] live = Array.FindAll(snapshot, o => o != null);
		Selection.objects = live;
		return live;
	}
}
