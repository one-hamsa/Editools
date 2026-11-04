#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Alt+S adds the cousins of the selected scene objects to the selection: for each selected object,
/// its one counterpart under each of its parent's siblings. The parent itself is never searched, so
/// siblings are not cousins, and an uncle contributes at most one object per selected object.
/// A counterpart is found by following the object's path of names down from the uncle; at each step
/// the child with the same exact name is taken (the n-th of them when the original is the n-th of
/// its name), otherwise the child at the same position among the similarly named ones (Door,
/// Door (1), Door_02 are one name). When a level turns up nothing outside the selection, the search
/// climbs one ancestor and follows the longer path, up to the scene roots.
/// Gated by the Select Cousins function in Editools Settings.
/// </summary>
static class SelectCousins
{
	static bool Enabled => EditoolsOverlay.IsActive && EditoolsSettings.Current.selectCousinsEnabled;

	static readonly Regex s_duplicateSuffix = new Regex(@"(?:\s*\(\d+\))+$|[\s_.\-]*\d+$", RegexOptions.Compiled);

	// One step of the path from an ancestor down to the origin: what the origin's ancestor at that
	// depth is called, and where it stands among its like-named siblings.
	struct Step
	{
		public string Name;      // exact name
		public string Key;       // name with the duplicate suffix stripped
		public int ExactIndex;   // position among siblings with the exact name
		public int KeyIndex;     // position among siblings with the same key
	}

	[Shortcut("Editools/Select Cousins", KeyCode.S, ShortcutModifiers.Alt)]
	static void OnShortcut()
	{
		if (!Enabled) return;
		Run();
	}

	/// <summary>Adds the counterpart of every selected scene object under each of its uncles to the selection.</summary>
	internal static void Run()
	{
		var selected = new HashSet<GameObject>();
		var origins = new List<GameObject>();
		foreach (var go in Selection.gameObjects)
		{
			if (go == null || EditorUtility.IsPersistent(go)) continue;
			if (selected.Add(go)) origins.Add(go);
		}
		if (origins.Count == 0) return;

		var added = new List<Object>();
		var found = new List<GameObject>();
		foreach (var origin in origins)
		{
			found.Clear();
			FindCousins(origin, selected, found);
			foreach (var go in found)
				if (selected.Add(go)) added.Add(go);
		}

		if (added.Count > 0)
		{
			var objects = new List<Object>(Selection.objects);
			objects.AddRange(added);
			Selection.objects = objects.ToArray();
		}
		Debug.Log($"[Editools] Select Cousins: {added.Count} object(s) added");
	}

	// Climbs one ancestor at a time; stops at the first level with a counterpart outside the selection,
	// or after the level whose uncles are the other scene roots.
	static void FindCousins(GameObject origin, HashSet<GameObject> selected, List<GameObject> result)
	{
		var scene = origin.scene;
		var path = new List<Step>();
		var matches = new List<GameObject>();
		Transform node = origin.transform;

		while (true)
		{
			path.Insert(0, MakeStep(node, scene));
			Transform top = node.parent;

			// The path hangs under `top`; it is followed down from each of top's siblings, never top
			// itself. A root top has the other roots as siblings; a root origin has no parent and so no cousins.
			matches.Clear();
			if (top != null)
			{
				foreach (var uncle in Children(top.parent, scene))
				{
					if (uncle == top) continue;
					var counterpart = Follow(uncle, path);
					if (counterpart != null) matches.Add(counterpart);
				}
			}

			bool anyNew = false;
			foreach (var go in matches)
				if (!selected.Contains(go)) { anyNew = true; break; }

			bool last = top == null || top.parent == null;
			if (anyNew || last)
			{
				result.AddRange(matches);
				return;
			}
			node = top;
		}
	}

	static Step MakeStep(Transform node, Scene scene)
	{
		var step = new Step { Name = node.name, Key = Key(node.name) };
		foreach (var sibling in Children(node.parent, scene))
		{
			if (sibling == node) break;
			if (sibling.name == step.Name) step.ExactIndex++;
			if (Key(sibling.name) == step.Key) step.KeyIndex++;
		}
		return step;
	}

	// Follows the path down from `parent`, one child per step. Null when a step has no counterpart.
	static GameObject Follow(Transform parent, List<Step> path)
	{
		Transform current = parent;
		for (int depth = 0; depth < path.Count; depth++)
		{
			current = Counterpart(current, path[depth]);
			if (current == null) return null;
		}
		return current.gameObject;
	}

	// The child of `parent` that corresponds to the step: the n-th child with the exact name, else the
	// n-th child with the same key. Hidden children are not counted.
	static Transform Counterpart(Transform parent, Step step)
	{
		Transform byKey = null;
		int exactSeen = 0, keySeen = 0;
		for (int i = 0; i < parent.childCount; i++)
		{
			var child = parent.GetChild(i);
			if ((child.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) continue;
			if (child.name == step.Name)
			{
				if (exactSeen == step.ExactIndex) return child;
				exactSeen++;
			}
			if (byKey == null && Key(child.name) == step.Key)
			{
				if (keySeen == step.KeyIndex) byKey = child;
				keySeen++;
			}
		}
		return byKey;
	}

	static IEnumerable<Transform> Children(Transform parent, Scene scene)
	{
		if (parent != null)
		{
			for (int i = 0; i < parent.childCount; i++)
				yield return parent.GetChild(i);
		}
		else
		{
			foreach (var root in scene.GetRootGameObjects())
				yield return root.transform;
		}
	}

	/// <summary>The name without its duplicate suffix, lower-cased. A name that is only a number keeps itself.</summary>
	internal static string Key(string name)
	{
		string trimmed = name.Trim();
		string stripped = s_duplicateSuffix.Replace(trimmed, "").TrimEnd();
		return (stripped.Length > 0 ? stripped : trimmed).ToLowerInvariant();
	}
}
#endif
