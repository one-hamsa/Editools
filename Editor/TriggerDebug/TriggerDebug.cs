#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Editools "Trigger Debug" mode. While active, paints every opaque scene renderer
/// gray and tints green any pixel that falls inside a selected box or sphere collider
/// (selection plus its children). Lets you see at a glance what world space is inside
/// vs. outside your trigger volumes. The selection can change live while the mode is on.
///
/// Delegates the gray scene fill to the shared SceneMaterialOverride engine (same one
/// Material Check / LG Edit Mode use) and uploads the selected collider volumes as global
/// shader uniforms that the Hidden/Editools/TriggerDebug shader tests per fragment.
/// </summary>
[InitializeOnLoad]
static class TriggerDebug
{
	const string k_ModeName = "TriggerDebug";
	const string k_SessionKey = "Editools_TriggerDebug_IsOn";

	// Must match TRIG_MAX in TriggerDebug.shader.
	const int k_MaxVolumes = 128;

	static readonly int s_idBoxMatrices = Shader.PropertyToID("_TrigBoxMatrices");
	static readonly int s_idBoxColors   = Shader.PropertyToID("_TrigBoxColors");
	static readonly int s_idBoxCount    = Shader.PropertyToID("_TrigBoxCount");
	static readonly int s_idSpheres     = Shader.PropertyToID("_TrigSpheres");
	static readonly int s_idSphereCount = Shader.PropertyToID("_TrigSphereCount");

	// Tint for collider volumes. Provider-contributed boxes carry their own color.
	static readonly Color k_ColliderColor = new Color(0.10f, 0.85f, 0.20f);

	static Material s_material;
	static readonly Matrix4x4[] s_boxBuffer = new Matrix4x4[k_MaxVolumes];
	static readonly Vector4[] s_boxColorBuffer = new Vector4[k_MaxVolumes];
	static readonly Vector4[] s_sphereBuffer = new Vector4[k_MaxVolumes];

	/// <summary>Raised whenever the mode turns on/off, including force-exit by another mode. UI toggles listen to stay in sync.</summary>
	internal static event System.Action<bool> StateChanged;

	internal static bool IsActive => SceneMaterialOverride.IsModeActive(k_ModeName);

	static TriggerDebug()
	{
		// Re-enter after a domain reload if we were active before it (SceneMaterialOverride
		// force-exits on assembly reload; the session flag survives so we can restore).
		if (SessionState.GetBool(k_SessionKey, false))
			EditorApplication.delayCall += () => SetEnabled(true);
	}

	internal static void SetEnabled(bool on)
	{
		if (on == IsActive) return;
		if (on) Enable();
		else Disable();
	}

	static void Enable()
	{
		var shader = Shader.Find("Hidden/Editools/TriggerDebug");
		if (shader == null)
		{
			Debug.LogError("[TriggerDebug] Shader 'Hidden/Editools/TriggerDebug' not found.");
			return;
		}
		s_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

		SceneMaterialOverride.Enter(s_material, k_ModeName, OnForceExit);
		Selection.selectionChanged += UploadVolumes;
		SceneView.duringSceneGui += OnSceneGui;
		TriggerDebugVolumes.Changed += UploadVolumes;
		UploadVolumes();

		SessionState.SetBool(k_SessionKey, true);
		StateChanged?.Invoke(true);
	}

	static void Disable()
	{
		Unsubscribe();
		SceneMaterialOverride.Exit();
		Cleanup();

		SessionState.SetBool(k_SessionKey, false);
		StateChanged?.Invoke(false);
	}

	/// <summary>
	/// Called by SceneMaterialOverride when another mode (Material Check, etc.) takes over.
	/// Leaves the session flag intact so the mode restores after a domain reload, matching
	/// Material Check's behavior.
	/// </summary>
	static void OnForceExit()
	{
		Unsubscribe();
		Cleanup();
		StateChanged?.Invoke(false);
	}

	static void Unsubscribe()
	{
		Selection.selectionChanged -= UploadVolumes;
		SceneView.duringSceneGui -= OnSceneGui;
		TriggerDebugVolumes.Changed -= UploadVolumes;
	}

	static void Cleanup()
	{
		if (s_material != null)
			Object.DestroyImmediate(s_material);
		s_material = null;
	}

	// Re-upload while dragging in the Scene View so the green region follows a collider
	// being moved with the transform handle. Event-driven (only on drag), not a per-frame
	// loop — selection changes are handled separately via Selection.selectionChanged.
	static void OnSceneGui(SceneView sceneView)
	{
		if (!EditoolsOverlay.IsActive) return;
		if (Event.current.type == EventType.MouseDrag)
			UploadVolumes();
	}

	static void UploadVolumes()
	{
		if (!IsActive) return;

		int boxCount = 0;
		int sphereCount = 0;

		foreach (var go in Selection.gameObjects)
		{
			if (go == null) continue;

			var boxes = go.GetComponentsInChildren<BoxCollider>(true);
			for (int i = 0; i < boxes.Length && boxCount < k_MaxVolumes; i++)
			{
				s_boxBuffer[boxCount] = BuildBoxMatrix(boxes[i]);
				s_boxColorBuffer[boxCount] = k_ColliderColor;
				boxCount++;
			}

			var spheres = go.GetComponentsInChildren<SphereCollider>(true);
			for (int i = 0; i < spheres.Length && sphereCount < k_MaxVolumes; i++)
				s_sphereBuffer[sphereCount++] = BuildSphere(spheres[i]);
		}

		// Extra box volumes contributed by game-specific systems (e.g. baker bounds).
		// Pushed after colliders; later boxes win on overlap, so nested inner boxes read correctly.
		TriggerDebugVolumes.Collect((matrix, tint) =>
		{
			if (boxCount >= k_MaxVolumes) return;
			s_boxBuffer[boxCount] = matrix;
			s_boxColorBuffer[boxCount] = tint;
			boxCount++;
		});

		Shader.SetGlobalMatrixArray(s_idBoxMatrices, s_boxBuffer);
		Shader.SetGlobalVectorArray(s_idBoxColors, s_boxColorBuffer);
		Shader.SetGlobalInt(s_idBoxCount, boxCount);
		Shader.SetGlobalVectorArray(s_idSpheres, s_sphereBuffer);
		Shader.SetGlobalInt(s_idSphereCount, sphereCount);

		SceneView.RepaintAll();
	}

	// World -> unit-cube space: interior of the box maps to |xyz| <= 0.5 on every axis.
	static Matrix4x4 BuildBoxMatrix(BoxCollider box)
	{
		Vector3 size = box.size;
		// Guard against degenerate (zero) extents to avoid divide-by-zero / NaN.
		Vector3 invSize = new Vector3(
			1f / Mathf.Max(Mathf.Abs(size.x), 1e-5f),
			1f / Mathf.Max(Mathf.Abs(size.y), 1e-5f),
			1f / Mathf.Max(Mathf.Abs(size.z), 1e-5f));

		return Matrix4x4.Scale(invSize)
			* Matrix4x4.Translate(-box.center)
			* box.transform.worldToLocalMatrix;
	}

	// World center + world radius. Unity scales a SphereCollider's radius by the largest
	// lossy-scale axis, so mirror that here.
	static Vector4 BuildSphere(SphereCollider sphere)
	{
		Transform t = sphere.transform;
		Vector3 worldCenter = t.TransformPoint(sphere.center);
		Vector3 ls = t.lossyScale;
		float maxScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
		return new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, sphere.radius * maxScale);
	}
}

/// <summary>
/// Scene View toolbar toggle for <see cref="TriggerDebug"/>. Stays in sync with the
/// underlying mode (e.g. when another override force-exits it).
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsTriggerDebugButton : EditorToolbarToggle
{
	public const string k_Id = "Editools/TriggerDebug";

	public EditoolsTriggerDebugButton()
	{
		icon = EditorGUIUtility.IconContent("d_BoxCollider Icon").image as Texture2D;
		tooltip = "Trigger Debug — paints the scene gray and tints anything inside the " +
			"selected box/sphere colliders (selection + children), plus any registered debug " +
			"volumes (e.g. baker bounds). Selection can change live.";

		SetValueWithoutNotify(TriggerDebug.IsActive);
		this.RegisterValueChangedCallback(OnToggled);

		TriggerDebug.StateChanged += OnExternalStateChanged;
		RegisterCallback<UnityEngine.UIElements.DetachFromPanelEvent>(_ =>
			TriggerDebug.StateChanged -= OnExternalStateChanged);
	}

	void OnToggled(UnityEngine.UIElements.ChangeEvent<bool> evt) => TriggerDebug.SetEnabled(evt.newValue);

	void OnExternalStateChanged(bool on) => SetValueWithoutNotify(on);
}
#endif
