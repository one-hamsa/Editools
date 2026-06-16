#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Gamma Corrector toolbar button. A native toolbar button framed around a color
/// swatch showing the converted color.
///   • Left-click — opens a color picker. Whatever you pick is the source; the
///     button live-converts it to the other color space and fills the swatch,
///     so you can dial in a color without pasting one from an external app.
///   • Right-click menu:
///       Copy  — write the converted color to Unity's color clipboard.
///       Paste — read Unity's color clipboard as the source (auto-converted).
///       "Switch to Gamma > Linear" / "Switch to Linear > Gamma" — picks which
///       conversion paste/pick apply. The label reflects the current mode.
/// Conversion is automatic on paste/pick; the switch item only flips the mode.
/// Source color + mode persist across domain reloads via EditorPrefs.
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsGammaCorrectorButton : EditorToolbarButton
{
	public const string k_Id = "Editools/GammaCorrector";

	const string k_SourcePref = "Editools_GammaCorrector_Source";
	const string k_ModePref = "Editools_GammaCorrector_ToLinear";
	const string k_EnabledPref = "Editools_GammaCorrector_Enabled";

	// Fires when the toolbar visibility toggle flips, so live button instances re-apply it.
	public static event Action EnabledChanged;

	public static bool Enabled
	{
		get => EditorPrefs.GetBool(k_EnabledPref, true);
		set
		{
			if (value == Enabled) return;
			EditorPrefs.SetBool(k_EnabledPref, value);
			EnabledChanged?.Invoke();
		}
	}

	// _toLinear: true → Gamma→Linear (apply Color.linear); false → Linear→Gamma (apply Color.gamma).
	bool _toLinear = true;
	bool _hasColor;
	Color _source = Color.white; // raw input (paste / picker)
	Color _color = Color.white;  // converted result — what the swatch shows and Copy copies

	// EditorToolbarButton paints an opaque skin over its own backgroundColor, so the
	// color is shown by a child element inset slightly to leave the button's frame visible.
	readonly VisualElement _swatch;

	public EditoolsGammaCorrectorButton()
	{
		tooltip = "Gamma Corrector\n\n" +
			"Left-click: open a color picker (live-converts your pick).\n" +
			"Right-click: Copy / Paste / switch conversion mode.";

		style.minWidth = 28;

		_swatch = new VisualElement { pickingMode = PickingMode.Ignore };
		_swatch.style.position = Position.Absolute;
		_swatch.style.left = 2;
		_swatch.style.top = 2;
		_swatch.style.right = 2;
		_swatch.style.bottom = 2;
		Add(_swatch);

		_toLinear = EditorPrefs.GetBool(k_ModePref, true);
		_hasColor = LoadSource(out _source);
		if (_hasColor) _color = Convert(_source);
		UpdateTint();

		clicked += OpenPicker;
		this.AddManipulator(new ContextualMenuManipulator(BuildMenu));

		ApplyEnabledState();
		EnabledChanged += ApplyEnabledState;
		RegisterCallback<DetachFromPanelEvent>(_ => EnabledChanged -= ApplyEnabledState);
	}

	void ApplyEnabledState() =>
		style.display = Enabled ? DisplayStyle.Flex : DisplayStyle.None;

	Color Convert(Color src) => _toLinear ? src.linear : src.gamma;

	void BuildMenu(ContextualMenuPopulateEvent evt)
	{
		evt.menu.AppendAction("Copy",
			_ => Copy(),
			_hasColor ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

		evt.menu.AppendAction("Paste",
			_ => Paste(),
			ColorClipboard.HasColor ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

		evt.menu.AppendSeparator();

		string switchLabel = _toLinear ? "Switch to Linear > Gamma" : "Switch to Gamma > Linear";
		evt.menu.AppendAction(switchLabel, _ => Switch(), DropdownMenuAction.Status.Normal);
	}

	void OpenPicker() => ColorPickerProxy.Show(SetSource, _hasColor ? _source : Color.white);

	void Copy() => ColorClipboard.Set(_color);

	void Paste()
	{
		if (!ColorClipboard.TryGet(out var pasted))
		{
			Debug.LogError("[Editools] Gamma Corrector: clipboard reported a color but none could be read.");
			return;
		}
		SetSource(pasted);
	}

	// New source from paste or the picker — convert automatically per the current mode.
	void SetSource(Color src)
	{
		_source = src;
		_color = Convert(src);
		_hasColor = true;
		Store();
		UpdateTint();
	}

	// Flip the conversion mode. Re-derives the shown color from the same source.
	void Switch()
	{
		_toLinear = !_toLinear;
		if (_hasColor) _color = Convert(_source);
		Store();
		UpdateTint();
	}

	void UpdateTint()
	{
		if (_hasColor)
		{
			// Render opaque — copied/picked colors often carry near-zero alpha, which
			// would let the toolbar show through. Alpha stays in _color for Copy fidelity.
			var c = _color;
			c.a = 1f;
			_swatch.style.backgroundColor = c;
		}
		else
		{
			_swatch.style.backgroundColor = StyleKeyword.Null;
		}
	}

	// ---- Persistence ----

	static bool LoadSource(out Color color)
	{
		string hex = EditorPrefs.GetString(k_SourcePref, "");
		if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString("#" + hex, out color))
			return true;

		color = Color.white;
		return false;
	}

	void Store()
	{
		EditorPrefs.SetString(k_SourcePref, ColorUtility.ToHtmlStringRGBA(_source));
		EditorPrefs.SetBool(k_ModePref, _toLinear);
	}
}

/// <summary>
/// Thin wrapper over Unity's internal <c>UnityEditor.Clipboard</c> — the same
/// color clipboard that ColorField's right-click Copy/Paste uses. Accessed via
/// reflection because the type is internal; resolution is cached and failures
/// log loudly + degrade to "no color" rather than throwing.
/// </summary>
static class ColorClipboard
{
	static readonly PropertyInfo s_hasColor;
	static readonly PropertyInfo s_colorValue;

	static ColorClipboard()
	{
		var type = typeof(Editor).Assembly.GetType("UnityEditor.Clipboard");
		if (type == null)
		{
			Debug.LogError("[Editools] Gamma Corrector: could not resolve internal type UnityEditor.Clipboard — color clipboard unavailable.");
			return;
		}

		const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		s_hasColor = type.GetProperty("hasColor", flags);
		s_colorValue = type.GetProperty("colorValue", flags);

		if (s_hasColor == null || s_colorValue == null)
			Debug.LogError("[Editools] Gamma Corrector: UnityEditor.Clipboard.hasColor/colorValue not found — color clipboard unavailable.");
	}

	public static bool HasColor
	{
		get
		{
			if (s_hasColor == null) return false;
			return (bool)s_hasColor.GetValue(null);
		}
	}

	public static bool TryGet(out Color color)
	{
		color = Color.white;
		if (s_colorValue == null || !HasColor) return false;
		color = (Color)s_colorValue.GetValue(null);
		return true;
	}

	public static void Set(Color color)
	{
		if (s_colorValue == null)
		{
			Debug.LogError("[Editools] Gamma Corrector: cannot copy — UnityEditor.Clipboard.colorValue unavailable.");
			return;
		}
		s_colorValue.SetValue(null, color);
	}
}

/// <summary>
/// Thin wrapper over Unity's internal <c>UnityEditor.ColorPicker</c>. Opens the
/// standard color picker and reports every change through the callback (realtime).
/// Reflection-resolved + cached; logs loudly and no-ops if the API moves.
/// </summary>
static class ColorPickerProxy
{
	static readonly MethodInfo s_show;

	static ColorPickerProxy()
	{
		var type = typeof(Editor).Assembly.GetType("UnityEditor.ColorPicker");
		if (type == null)
		{
			Debug.LogError("[Editools] Gamma Corrector: could not resolve internal type UnityEditor.ColorPicker — picker unavailable.");
			return;
		}

		const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		foreach (var m in type.GetMethods(flags))
		{
			if (m.Name != "Show") continue;
			var ps = m.GetParameters();
			if (ps.Length == 4 && ps[0].ParameterType == typeof(Action<Color>))
			{
				s_show = m;
				break;
			}
		}

		if (s_show == null)
			Debug.LogError("[Editools] Gamma Corrector: UnityEditor.ColorPicker.Show(Action<Color>, Color, bool, bool) not found — picker unavailable.");
	}

	public static void Show(Action<Color> onColorChanged, Color initial)
	{
		if (s_show == null)
		{
			Debug.LogError("[Editools] Gamma Corrector: cannot open picker — UnityEditor.ColorPicker.Show unavailable.");
			return;
		}
		s_show.Invoke(null, new object[] { onColorChanged, initial, true /*showAlpha*/, false /*hdr*/ });
	}
}
#endif
