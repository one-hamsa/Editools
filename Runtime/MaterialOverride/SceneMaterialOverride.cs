using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
#endif

/// <summary>
/// Per-renderer per-slot override resolver. Returning null leaves a slot unchanged;
/// returning a Material overrides that slot. If a renderer has zero changed slots,
/// it is left entirely untouched (and not backed up, so save/restore is a no-op for it).
///
/// Defined outside #if UNITY_EDITOR so runtime consumers (e.g. Scene Reskinner's
/// runtime apply path) can share the same strategy classes used in editor.
/// </summary>
public interface ISceneMaterialOverrideStrategy
{
	/// <summary>
	/// Resolve the material for a single renderer slot. Return null to leave the original in place.
	/// </summary>
	Material Resolve(Renderer renderer, int slotIndex, Material original);
}

#if UNITY_EDITOR

/// <summary>
/// Centralized system for temporarily replacing scene renderer materials.
/// Enforces mutual exclusivity: only one mode can be active at a time.
///
/// Two entry shapes:
///  - Enter(Material, ...) — fill mode, stamps one material onto every opaque slot.
///    Used by Material Check, LG Edit Mode, AO Debug, HFF Debug, UDShadow.
///  - Enter(ISceneMaterialOverrideStrategy, ...) — strategy mode, per-slot resolution
///    with optional scope roots (only walk renderers under those roots).
///    Used by Scene Reskinner.
/// </summary>
public static class SceneMaterialOverride
{
	static Dictionary<Renderer, Material[]> _backupMaterials;
	static ISceneMaterialOverrideStrategy _strategy;
	static IList<GameObject> _scopeRoots;
	static Material _activeMaterial;          // non-null only in fill mode
	static string _activeModeName;
	static Action _exitCallback;
	static bool _isActive;

	public static bool IsActive => _isActive;
	public static string ActiveMode => _activeModeName;

	/// <summary>
	/// Returns true if the specified mode is the currently active one.
	/// </summary>
	public static bool IsModeActive(string modeName) => _isActive && _activeModeName == modeName;

	/// <summary>
	/// Enters fill mode: applies one material to every opaque renderer slot in the scene.
	/// If another mode is already active, it is force-exited first.
	/// </summary>
	public static void Enter(Material mat, string modeName, Action exitCallback)
	{
		if (mat == null)
		{
			Debug.LogError($"[SceneMaterialOverride] Cannot enter mode '{modeName}' with null material.");
			return;
		}
		EnterInternal(new FillStrategy(mat), modeName, exitCallback, scopeRoots: null, fillMaterial: mat);
	}

	/// <summary>
	/// Enters strategy mode: a custom resolver decides per-slot what to override (or skip).
	/// Optionally scoped to renderers under <paramref name="scopeRoots"/>; null = whole scene.
	/// If another mode is already active, it is force-exited first.
	/// </summary>
	public static void Enter(ISceneMaterialOverrideStrategy strategy, string modeName, Action exitCallback, IList<GameObject> scopeRoots = null)
	{
		if (strategy == null)
		{
			Debug.LogError($"[SceneMaterialOverride] Cannot enter mode '{modeName}' with null strategy.");
			return;
		}
		EnterInternal(strategy, modeName, exitCallback, scopeRoots, fillMaterial: null);
	}

	static void EnterInternal(ISceneMaterialOverrideStrategy strategy, string modeName, Action exitCallback, IList<GameObject> scopeRoots, Material fillMaterial)
	{
		// If this exact mode is already active, do nothing
		if (_isActive && _activeModeName == modeName)
			return;

		// Force-exit any currently active mode first (mutual exclusivity)
		if (_isActive)
			ForceExit();

		_strategy = strategy;
		_scopeRoots = scopeRoots;
		_activeMaterial = fillMaterial;
		_activeModeName = modeName;
		_exitCallback = exitCallback;

		// Subscribe to safety events
		EditorApplication.playModeStateChanged += OnPlayModeChanged;
		AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
		EditorSceneManager.sceneSaving += OnSceneSaving;
		EditorSceneManager.sceneSaved += OnSceneSaved;

		// Swap materials on candidate renderers
		SwapAllRenderers();
		_isActive = true;

		SceneView.RepaintAll();
	}

	/// <summary>
	/// Exits the current mode. Restores all original materials.
	/// Called by the mode owner when it decides to exit on its own.
	/// Does NOT invoke the exit callback (the caller is already exiting).
	/// </summary>
	public static void Exit()
	{
		if (!_isActive) return;

		RestoreMaterials();
		UnsubscribeSafetyEvents();

		_strategy = null;
		_scopeRoots = null;
		_activeMaterial = null;
		_activeModeName = null;
		_exitCallback = null;
		_backupMaterials = null;
		_isActive = false;

		SceneView.RepaintAll();
	}

	/// <summary>
	/// Hot-swaps all overridden renderers to a different material without
	/// doing a full restore/re-swap. The backup remains unchanged.
	/// Only valid in fill mode (entered via Enter(Material, ...)).
	/// </summary>
	public static void SwapMaterial(Material newMat)
	{
		if (!_isActive || _backupMaterials == null || newMat == null) return;

		if (_activeMaterial == null)
		{
			Debug.LogError("[SceneMaterialOverride] SwapMaterial only works in fill mode. Use Exit() + Enter(strategy, ...) to swap strategies.");
			return;
		}

		_activeMaterial = newMat;
		_strategy = new FillStrategy(newMat);

		foreach (var kvp in _backupMaterials)
		{
			if (kvp.Key == null) continue;
			int count = kvp.Value.Length;
			Material[] mats = new Material[count];
			for (int i = 0; i < count; i++)
				mats[i] = newMat;
			kvp.Key.sharedMaterials = mats;
		}
	}

	/// <summary>
	/// Permanently applies the current override material to all affected renderers.
	/// Registers Undo so the operation can be reverted with Ctrl+Z.
	/// Then exits the override mode (since materials are now permanently set).
	/// Only valid in fill mode (entered via Enter(Material, ...)).
	/// </summary>
	public static void ApplyPermanently()
	{
		if (!_isActive || _backupMaterials == null || _activeMaterial == null) return;

		// First restore originals so Undo snapshots the correct state
		RestoreMaterials();

		Undo.IncrementCurrentGroup();
		int undoGroup = Undo.GetCurrentGroup();

		foreach (var kvp in _backupMaterials)
		{
			if (kvp.Key == null) continue;

			Undo.RegisterCompleteObjectUndo(kvp.Key, "Apply Material Check");

			int count = kvp.Value.Length;
			Material[] mats = new Material[count];
			for (int i = 0; i < count; i++)
				mats[i] = _activeMaterial;
			kvp.Key.sharedMaterials = mats;
		}

		Undo.CollapseUndoOperations(undoGroup);

		// Clear backup so Exit() won't restore original materials
		_backupMaterials = null;
		Exit();
	}

	// ---- Internal ----

	/// <summary>
	/// Force-exits the current mode: restores materials, clears state,
	/// and invokes the exit callback so the mode owner can clean up.
	/// Used by mutual exclusivity (another mode entering) and safety events.
	/// </summary>
	static void ForceExit()
	{
		if (!_isActive) return;

		RestoreMaterials();
		UnsubscribeSafetyEvents();

		var callback = _exitCallback;

		_strategy = null;
		_scopeRoots = null;
		_activeMaterial = null;
		_activeModeName = null;
		_exitCallback = null;
		_backupMaterials = null;
		_isActive = false;

		SceneView.RepaintAll();

		// Invoke the callback AFTER clearing state, so if the callback
		// triggers another Exit() call it will be a no-op.
		callback?.Invoke();
	}

	/// <summary>
	/// Walks candidate renderers (whole scene or scoped subtrees), asks the strategy
	/// per slot, and applies the override only to renderers that had at least one
	/// changed slot. Skipped renderers are not backed up.
	/// </summary>
	static void SwapAllRenderers()
	{
		_backupMaterials = new Dictionary<Renderer, Material[]>();

		var candidates = GetCandidateRenderers();

		foreach (var renderer in candidates)
		{
			if (renderer == null) continue;

			// Only MeshRenderer and SkinnedMeshRenderer
			if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;

			// Skip shadow-only renderers
			if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;

			// Skip renderers with any transparent material (renderQueue >= 3000)
			if (HasTransparentMaterial(renderer)) continue;

			Material[] originals = renderer.sharedMaterials;
			Material[] overrideMats = BuildOverrideArray(renderer, originals, out bool changed);
			if (!changed) continue;

			// Cache original materials and apply override
			_backupMaterials[renderer] = originals;
			renderer.sharedMaterials = overrideMats;
		}
	}

	static Material[] BuildOverrideArray(Renderer renderer, Material[] originals, out bool changed)
	{
		changed = false;
		Material[] result = new Material[originals.Length];
		for (int i = 0; i < originals.Length; i++)
		{
			Material resolved = _strategy.Resolve(renderer, i, originals[i]);
			if (resolved != null && resolved != originals[i])
			{
				result[i] = resolved;
				changed = true;
			}
			else
			{
				result[i] = originals[i];
			}
		}
		return result;
	}

	static IEnumerable<Renderer> GetCandidateRenderers()
	{
		if (_scopeRoots == null || _scopeRoots.Count == 0)
			return UnityEngine.Object.FindObjectsOfType<Renderer>(false);

		var set = new HashSet<Renderer>();
		foreach (var root in _scopeRoots)
		{
			if (root == null) continue;
			var found = root.GetComponentsInChildren<Renderer>(false);
			for (int i = 0; i < found.Length; i++)
				set.Add(found[i]);
		}
		return set;
	}

	/// <summary>
	/// Restores all backed-up renderers to their original materials.
	/// </summary>
	static void RestoreMaterials()
	{
		if (_backupMaterials == null) return;

		foreach (var kvp in _backupMaterials)
		{
			if (kvp.Key != null)
				kvp.Key.sharedMaterials = kvp.Value;
		}
	}

	/// <summary>
	/// Re-applies the current strategy to all backed-up renderers (used after a save).
	/// </summary>
	static void ReapplyOverride()
	{
		if (_backupMaterials == null || _strategy == null) return;

		foreach (var kvp in _backupMaterials)
		{
			if (kvp.Key == null) continue;
			Material[] originals = kvp.Value;
			Material[] mats = new Material[originals.Length];
			for (int i = 0; i < originals.Length; i++)
			{
				Material resolved = _strategy.Resolve(kvp.Key, i, originals[i]);
				mats[i] = (resolved != null) ? resolved : originals[i];
			}
			kvp.Key.sharedMaterials = mats;
		}
	}

	static bool HasTransparentMaterial(Renderer renderer)
	{
		var materials = renderer.sharedMaterials;
		for (int i = 0; i < materials.Length; i++)
		{
			if (materials[i] != null && materials[i].renderQueue >= 3000)
				return true;
		}
		return false;
	}

	static void UnsubscribeSafetyEvents()
	{
		EditorApplication.playModeStateChanged -= OnPlayModeChanged;
		AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
		EditorSceneManager.sceneSaving -= OnSceneSaving;
		EditorSceneManager.sceneSaved -= OnSceneSaved;
	}

	// ---- Safety callbacks ----

	static void OnPlayModeChanged(PlayModeStateChange state)
	{
		if (state == PlayModeStateChange.ExitingEditMode)
			ForceExit();
	}

	static void OnBeforeAssemblyReload()
	{
		ForceExit();
	}

	/// <summary>
	/// Before scene save: restore original materials so the scene file
	/// is saved with correct materials. Keep backup and mode state intact.
	/// </summary>
	static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
	{
		if (!_isActive || _backupMaterials == null) return;

		// Restore materials for saving, but don't exit the mode
		foreach (var kvp in _backupMaterials)
		{
			if (kvp.Key != null)
				kvp.Key.sharedMaterials = kvp.Value;
		}
	}

	/// <summary>
	/// After scene save: re-apply the override (via strategy) to all backed-up renderers.
	/// </summary>
	static void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
	{
		if (!_isActive || _backupMaterials == null || _strategy == null) return;
		ReapplyOverride();
		SceneView.RepaintAll();
	}

	// ---- Built-in fill strategy ----

	sealed class FillStrategy : ISceneMaterialOverrideStrategy
	{
		readonly Material _material;
		public FillStrategy(Material material) { _material = material; }
		public Material Resolve(Renderer renderer, int slotIndex, Material original) => _material;
	}
}
#endif
