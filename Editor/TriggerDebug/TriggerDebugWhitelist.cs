#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Per-user material whitelist for Trigger Debug - ADDITIVE to the default paint set.
/// The mode always paints every opaque renderer; whitelisted materials are painted
/// on top of that even when they'd normally be excluded (renderers with transparent
/// materials are skipped by default). The list is <see cref="EditoolsSettings.triggerDebugWhitelist"/>,
/// edited in the Editools settings inspector or by dropping materials on the Trigger Debug
/// toolbar button.
/// </summary>
static class TriggerDebugWhitelist
{
	/// <summary>Raised whenever the list changes (any settings change counts). TriggerDebug re-applies its override.</summary>
	internal static event System.Action Changed
	{
		add => EditoolsSettings.Changed += value;
		remove => EditoolsSettings.Changed -= value;
	}

	static List<Material> List => EditoolsSettings.Current.triggerDebugWhitelist;

	internal static int Count => List.Count;
	internal static bool IsEmpty => Count == 0;

	internal static bool Contains(Material mat) => mat != null && List.Contains(mat);

	/// <summary>Adds a material (asset materials only - scene/runtime instances cannot be saved). Returns true if it was actually added.</summary>
	internal static bool Add(Material mat)
	{
		if (mat == null) return false;
		if (!EditorUtility.IsPersistent(mat))
		{
			Debug.LogError($"[TriggerDebug] '{mat.name}' is not an asset material - only project materials can be whitelisted.");
			return false;
		}

		var settings = EditoolsSettings.Current;
		if (settings.triggerDebugWhitelist.Contains(mat)) return false;
		Undo.RecordObject(settings, "Add Trigger Debug Whitelist Material");
		settings.triggerDebugWhitelist.Add(mat);
		settings.SaveAndNotify();
		return true;
	}

	internal static void Remove(Material mat)
	{
		if (mat == null) return;
		var settings = EditoolsSettings.Current;
		if (!settings.triggerDebugWhitelist.Contains(mat)) return;
		Undo.RecordObject(settings, "Remove Trigger Debug Whitelist Material");
		settings.triggerDebugWhitelist.Remove(mat);
		settings.SaveAndNotify();
	}

	internal static void Clear()
	{
		var settings = EditoolsSettings.Current;
		if (settings.triggerDebugWhitelist.Count == 0) return;
		Undo.RecordObject(settings, "Clear Trigger Debug Whitelist");
		settings.triggerDebugWhitelist.Clear();
		settings.SaveAndNotify();
	}

	/// <summary>Copies the whitelist into <paramref name="results"/>, skipping empty slots and deleted materials.</summary>
	internal static void GetMaterials(List<Material> results)
	{
		results.Clear();
		foreach (var mat in List)
			if (mat != null) results.Add(mat);
	}

	/// <summary>Snapshot of the whitelist as a set, for per-slot lookups during a material swap. Empty set when the whitelist is empty.</summary>
	internal static HashSet<Material> BuildSet()
	{
		var set = new HashSet<Material>();
		foreach (var mat in List)
			if (mat != null) set.Add(mat);
		return set;
	}
}
#endif
