using UnityEngine;
using UnityEditor;

/// <summary>
/// Adds camera roll control to the Scene View.
/// Ctrl + Alt + middle-mouse drag rolls the camera (left/right movement).
/// Ctrl + Alt + middle-mouse click (no drag) resets the roll to zero.
/// </summary>
[InitializeOnLoad]
static class SceneViewRoll
{
	private const string k_EnabledKey = "SceneViewRoll_Enabled";
	private const float k_DragThreshold = 3f;
	private const float k_Sensitivity = 0.5f;

	private static bool s_dragging;
	private static Vector2 s_mouseStart;
	private static float s_rollStart;
	private static bool s_movedPastThreshold;

	internal static bool Enabled
	{
		get => EditorPrefs.GetBool(k_EnabledKey, true);
		set => EditorPrefs.SetBool(k_EnabledKey, value);
	}

	static SceneViewRoll()
	{
		SceneView.duringSceneGui += OnSceneGUI;
	}

	static void OnSceneGUI(SceneView sceneView)
	{
		if (!EditoolsOverlay.IsActive || !Enabled) return;

		Event e = Event.current;
		if (e == null) return;

		// Ctrl + Alt + middle mouse button
		if (e.type == EventType.MouseDown && e.button == 2 && e.control && e.alt)
		{
			s_dragging = true;
			s_mouseStart = e.mousePosition;
			s_rollStart = GetRoll(sceneView.rotation);
			s_movedPastThreshold = false;
			e.Use();
			return;
		}

		if (!s_dragging) return;

		if (e.type == EventType.MouseDrag && e.button == 2)
		{
			float dx = e.mousePosition.x - s_mouseStart.x;

			if (!s_movedPastThreshold)
			{
				if (Mathf.Abs(dx) >= k_DragThreshold)
					s_movedPastThreshold = true;
				else
				{
					e.Use();
					return;
				}
			}

			float rollAngle = s_rollStart - dx * k_Sensitivity;
			ApplyRoll(sceneView, rollAngle);
			e.Use();
		}
		else if (e.type == EventType.MouseUp && e.button == 2)
		{
			if (!s_movedPastThreshold)
			{
				// Click without drag — reset roll
				ApplyRoll(sceneView, 0f);
			}

			s_dragging = false;
			e.Use();
		}
		else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
		{
			// Cancel drag, restore original roll
			ApplyRoll(sceneView, s_rollStart);
			s_dragging = false;
			e.Use();
		}
	}

	static float GetRoll(Quaternion rotation)
	{
		// Extract roll (rotation around the forward/look axis)
		Vector3 euler = rotation.eulerAngles;
		return euler.z;
	}

	static void ApplyRoll(SceneView sceneView, float rollDegrees)
	{
		Vector3 euler = sceneView.rotation.eulerAngles;
		euler.z = rollDegrees;
		sceneView.rotation = Quaternion.Euler(euler);
		sceneView.Repaint();
	}
}
