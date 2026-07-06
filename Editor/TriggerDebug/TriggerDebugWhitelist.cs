#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Per-user material whitelist for Trigger Debug — ADDITIVE to the default paint set.
/// The mode always paints every opaque renderer; whitelisted materials are painted
/// on top of that even when they'd normally be excluded (renderers with transparent
/// materials are skipped by default). Stored locally in EditorPrefs as material GUIDs,
/// so it survives asset moves/renames and never touches version control.
///
/// Manage it from the Trigger Debug toolbar button: right-click opens the manager
/// window, drag-dropping materials onto the button adds them.
/// </summary>
static class TriggerDebugWhitelist
{
	const string k_Pref = "Editools_TriggerDebug_MaterialWhitelist";
	const char k_Separator = ';';

	static List<string> s_guids;

	/// <summary>Raised whenever the list changes. TriggerDebug re-applies its override; the window rebuilds.</summary>
	internal static event System.Action Changed;

	internal static int Count { get { EnsureLoaded(); return s_guids.Count; } }
	internal static bool IsEmpty => Count == 0;

	static void EnsureLoaded()
	{
		if (s_guids != null) return;
		s_guids = new List<string>();
		string raw = EditorPrefs.GetString(k_Pref, string.Empty);
		foreach (string guid in raw.Split(k_Separator))
			if (!string.IsNullOrEmpty(guid))
				s_guids.Add(guid);
	}

	static void Save()
	{
		EditorPrefs.SetString(k_Pref, string.Join(k_Separator, s_guids));
		Changed?.Invoke();
	}

	internal static bool Contains(Material mat)
	{
		if (mat == null) return false;
		EnsureLoaded();
		return s_guids.Contains(GuidOf(mat));
	}

	/// <summary>Adds a material (asset materials only — scene/runtime instances have no GUID). Returns true if it was actually added.</summary>
	internal static bool Add(Material mat)
	{
		if (mat == null) return false;
		string guid = GuidOf(mat);
		if (string.IsNullOrEmpty(guid))
		{
			Debug.LogError($"[TriggerDebug] '{mat.name}' is not an asset material — only project materials can be whitelisted.");
			return false;
		}

		EnsureLoaded();
		if (s_guids.Contains(guid)) return false;
		s_guids.Add(guid);
		Save();
		return true;
	}

	internal static void Remove(Material mat)
	{
		if (mat == null) return;
		EnsureLoaded();
		if (s_guids.Remove(GuidOf(mat)))
			Save();
	}

	internal static void Clear()
	{
		EnsureLoaded();
		if (s_guids.Count == 0) return;
		s_guids.Clear();
		Save();
	}

	/// <summary>
	/// Resolves the stored GUIDs to loaded materials. Entries whose asset no longer
	/// exists (deleted material) are pruned silently — stale local prefs, not an error.
	/// </summary>
	internal static void GetMaterials(List<Material> results)
	{
		EnsureLoaded();
		results.Clear();

		bool pruned = false;
		for (int i = s_guids.Count - 1; i >= 0; i--)
		{
			string path = AssetDatabase.GUIDToAssetPath(s_guids[i]);
			var mat = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
			if (mat == null)
			{
				s_guids.RemoveAt(i);
				pruned = true;
				continue;
			}
			results.Insert(0, mat);
		}

		// Persist the prune without firing Changed — the effective list is unchanged
		// (those materials no longer exist), and notifying here would re-enter callers.
		if (pruned)
			EditorPrefs.SetString(k_Pref, string.Join(k_Separator, s_guids));
	}

	/// <summary>Snapshot of the whitelist as a set, for per-slot lookups during a material swap. Empty set when the whitelist is empty.</summary>
	internal static HashSet<Material> BuildSet()
	{
		var mats = new List<Material>(Count);
		GetMaterials(mats);
		return new HashSet<Material>(mats);
	}

	static string GuidOf(Material mat) =>
		AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out string guid, out long _) ? guid : null;
}
#endif
