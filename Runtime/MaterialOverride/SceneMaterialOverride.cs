#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Centralized system for temporarily replacing all opaque scene renderer materials.
/// Enforces mutual exclusivity: only one mode can be active at a time.
/// </summary>
public static class SceneMaterialOverride
{
	static Dictionary<Renderer, Material[]> _backupMaterials;
	static Material _activeMaterial;
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
	/// Enters a material override mode. If another mode is already active,
	/// it is force-exited first (materials restored, its exit callback invoked).
	/// </summary>
	/// <param name="mat">The material to apply to all opaque renderers.</param>
	/// <param name="modeName">Unique name for this mode (e.g. "AODebug", "LocalGlobals").</param>
	/// <param name="exitCallback">Called when the mode is force-exited by another mode or safety event.
	/// The callback should clean up the caller's own state. It should NOT call Exit() again.</param>
	public static void Enter(Material mat, string modeName, Action exitCallback)
	{
		if (mat == null)
		{
			Debug.LogError($"[SceneMaterialOverride] Cannot enter mode '{modeName}' with null material.");
			return;
		}

		// If this exact mode is already active, do nothing
		if (_isActive && _activeModeName == modeName)
			return;

		// Force-exit any currently active mode first (mutual exclusivity)
		if (_isActive)
			ForceExit();

		_activeMaterial = mat;
		_activeModeName = modeName;
		_exitCallback = exitCallback;

		// Subscribe to safety events
		EditorApplication.playModeStateChanged += OnPlayModeChanged;
		AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
		EditorSceneManager.sceneSaving += OnSceneSaving;
		EditorSceneManager.sceneSaved += OnSceneSaved;

		// Swap materials on all opaque renderers
		SwapAllRenderers(mat);
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
	/// </summary>
	public static void SwapMaterial(Material newMat)
	{
		if (!_isActive || _backupMaterials == null || newMat == null) return;

		_activeMaterial = newMat;

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
	/// Finds all opaque renderers in the scene, backs up their materials,
	/// and replaces them with the given material.
	/// </summary>
	static void SwapAllRenderers(Material mat)
	{
		_backupMaterials = new Dictionary<Renderer, Material[]>();

		var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(false);

		foreach (var renderer in renderers)
		{
			if (renderer == null) continue;

			// Only MeshRenderer and SkinnedMeshRenderer
			if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;

			// Skip shadow-only renderers
			if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;

			// Skip renderers with any transparent material (renderQueue >= 3000)
			if (HasTransparentMaterial(renderer)) continue;

			// Cache original materials
			Material[] originals = renderer.sharedMaterials;
			_backupMaterials[renderer] = originals;

			// Replace with override material (matching submesh count)
			Material[] overrideMats = new Material[originals.Length];
			for (int i = 0; i < overrideMats.Length; i++)
				overrideMats[i] = mat;
			renderer.sharedMaterials = overrideMats;
		}
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
	/// After scene save: re-apply the override material to all backed-up renderers.
	/// </summary>
	static void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
	{
		if (!_isActive || _backupMaterials == null || _activeMaterial == null) return;

		foreach (var kvp in _backupMaterials)
		{
			if (kvp.Key == null) continue;
			int count = kvp.Value.Length;
			Material[] overrideMats = new Material[count];
			for (int i = 0; i < count; i++)
				overrideMats[i] = _activeMaterial;
			kvp.Key.sharedMaterials = overrideMats;
		}

		SceneView.RepaintAll();
	}
}
#endif
