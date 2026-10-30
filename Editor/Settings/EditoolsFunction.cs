#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

enum EditoolsCategory
{
	SceneTools,
	Helpers,
	Controls,
	EditorTools
}

/// <summary>How a function appears in the Scene View toolbar overlay.</summary>
enum EditoolsOverlayKind
{
	/// <summary>Never shown as a toolbar button (shortcut / drag tools and windows without a button).</summary>
	None,
	/// <summary>Button that runs the function or opens its popup / window.</summary>
	Element,
	/// <summary>Toggle bound to the function's active state (<see cref="EditoolsFunction.GetActive"/> / <see cref="EditoolsFunction.SetActive"/>).</summary>
	Toggle
}

/// <summary>
/// Describes one Editools function for the settings inspector and the toolbar overlay: its
/// category, enabled gate, optional active state, optional settings drawer, and optional toolbar
/// button. Instances are declared in <see cref="EditoolsFunctions"/>.
/// The gate decides whether the function exists for the editor at all; a Toggle function that is
/// enabled is only running while its active state is on.
/// </summary>
sealed class EditoolsFunction
{
	/// <summary>Stable key used for overlay membership and foldout state.</summary>
	public string Id;
	public string Name;
	public string Tooltip;
	public EditoolsCategory Category;

	public Func<bool> GetEnabled;
	/// <summary>Writes the gate and applies its side effects (subscribe / unsubscribe, hide panels).</summary>
	public Action<bool> SetEnabled;

	/// <summary>Toggle functions only: whether the function is currently running / shown.</summary>
	public Func<bool> GetActive;
	public Action<bool> SetActive;

	/// <summary>Draws the function's settings inside its inspector foldout. Null when it has none.</summary>
	public Action<SerializedObject> DrawSettings;

	/// <summary>Shortcut Manager ids whose current bindings are shown after the name in the inspector.</summary>
	public string[] ShortcutIds;
	/// <summary>Fixed input description shown after the name when the input is not a rebindable shortcut (e.g. "I + Click").</summary>
	public string ShortcutText;

	public EditoolsOverlayKind OverlayKind;
	public Func<Texture2D> Icon;
	/// <summary>Creates the toolbar button for the given overlay instance.</summary>
	public Func<EditoolsOverlay, VisualElement> CreateOverlayElement;
	/// <summary>Runs the function from the settings menu row. The rect is where a sub-popup should open, in the menu popup's GUI space; unused by most functions.</summary>
	public Action<Rect> Activate;

	public bool HasSettings => DrawSettings != null;
	public bool IsOverlayViable => OverlayKind != EditoolsOverlayKind.None;
	public bool IsToggle => OverlayKind == EditoolsOverlayKind.Toggle;

	public bool IsEnabled => GetEnabled();
	public bool IsInOverlay => EditoolsSettings.Current.IsInOverlay(Id);

	/// <summary>True when the overlay should currently show this function's button.</summary>
	public bool ShowsInOverlay => IsOverlayViable && IsInOverlay && IsEnabled;

	/// <summary>True when the settings menu should list this function (viable, enabled, not a toolbar button).</summary>
	public bool ShowsInSettingsMenu => IsOverlayViable && IsEnabled && !IsInOverlay;

	/// <summary>The current shortcut bindings (or the fixed input text), or null when the function has none.</summary>
	public string DescribeInput()
	{
		if (ShortcutIds != null && ShortcutIds.Length > 0)
		{
			var manager = UnityEditor.ShortcutManagement.ShortcutManager.instance;
			var parts = new string[ShortcutIds.Length];
			for (int i = 0; i < ShortcutIds.Length; i++)
			{
				var binding = manager.GetShortcutBinding(ShortcutIds[i]);
				parts[i] = binding.Equals(UnityEditor.ShortcutManagement.ShortcutBinding.empty) ? "unbound" : binding.ToString();
			}
			return string.Join(" / ", parts);
		}
		return ShortcutText;
	}
}
#endif
