#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Selects every scene object whose MeshRenderer casts shadows only (including inactive
/// objects). Run from the Editools toolbar button or settings menu.
/// </summary>
static class SelectShadowOnly
{
	internal static void Run()
	{
		var objects = new List<Object>();
		foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
				objects.Add(r.gameObject);
		Selection.objects = objects.ToArray();
		Debug.Log($"[Editools] Select Shadow Only: {objects.Count} object(s)");
	}
}
#endif
