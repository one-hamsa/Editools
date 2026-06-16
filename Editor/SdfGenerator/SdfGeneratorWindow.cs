#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Editools window that bakes a source texture into a signed distance field.
/// Opened from the Editools Settings dropdown. Pick a source, tune the settings, hit
/// Generate to preview, then Save to write the SDF out as a PNG next to the source.
///
/// Edit-time only and push-based: nothing bakes until the user presses Generate.
/// </summary>
class SdfGeneratorWindow : EditorWindow
{
	[MenuItem("Tools/Editools/SDF Generator")]
	public static void ShowWindow()
	{
		var window = GetWindow<SdfGeneratorWindow>();
		window.titleContent = new GUIContent("SDF Generator",
			EditorGUIUtility.IconContent("d_PreTextureRGB").image);
		window.minSize = new Vector2(320, 460);
	}

	EnumField _channelField;
	Slider _thresholdField;
	IntegerField _spreadInsideField;
	IntegerField _spreadOutsideField;
	SliderInt _blurField;
	Toggle _aaField;
	EnumField _packingField;
	IntegerField _widthField;
	VisualElement _previewArea;
	Image _preview;
	Button _saveButton;
	Label _status;

	Texture2D _source;
	Texture2D _baked; // last generated SDF, owned by this window until saved/regenerated

	Texture2D Source => _source;

	void OnEnable()
	{
		var root = rootVisualElement;
		root.style.paddingLeft = 8;
		root.style.paddingRight = 8;
		root.style.paddingTop = 8;
		root.style.paddingBottom = 8;

		// Large square object-field drawer (material-inspector style) with the source thumbnail.
		var sourceGui = new IMGUIContainer(DrawSourceField);
		sourceGui.style.marginBottom = 4;
		root.Add(sourceGui);

		_channelField = new EnumField("Source Channel", SdfBaker.ChannelSource.Alpha);
		_channelField.tooltip = "Which channel decides inside vs. outside: the texture's Alpha, or its Luminance (brightness).";
		root.Add(_channelField);

		_thresholdField = new Slider("Threshold", 0f, 1f) { value = 0.5f };
		_thresholdField.tooltip = "Coverage cutoff. Pixels at or above this value count as inside the shape. 0.5 is typical.";
		_thresholdField.showInputField = true;
		root.Add(_thresholdField);

		_spreadInsideField = new IntegerField("Spread Inside (px)") { value = 8 };
		_spreadInsideField.tooltip = "How many pixels of inward falloff the SDF encodes. Larger = softer/longer inner gradient before clamping to fully-inside.";
		root.Add(_spreadInsideField);

		_spreadOutsideField = new IntegerField("Spread Outside (px)") { value = 8 };
		_spreadOutsideField.tooltip = "How many pixels of outward falloff the SDF encodes. Controls the max reach of outlines/glows derived from this SDF.";
		root.Add(_spreadOutsideField);

		_blurField = new SliderInt("Source Smoothing (px)", 0, 16) { value = 0 };
		_blurField.tooltip = "Pre-blur applied to the source before binarizing. Rounds and softens edges — use sparingly. 0 = off.";
		_blurField.showInputField = true;
		root.Add(_blurField);

		_aaField = new Toggle("Edge Anti-aliasing") { value = true };
		_aaField.tooltip = "Sub-pixel edge reconstruction. Reads the anti-aliased pixels along edges to straighten aliasing without rounding corners, and produces smooth (band-free) distances. Leave on for icons with slightly aliased edges.";
		root.Add(_aaField);

		_packingField = new EnumField("Output Packing", SdfBaker.Packing.Alpha);
		_packingField.tooltip = "Where the SDF is written. Alpha: RGB stays white, SDF in alpha. RGB: grayscale SDF in RGB, alpha stays white.";
		root.Add(_packingField);

		_widthField = new IntegerField("Output Width (px)") { value = 0 };
		_widthField.tooltip = "Output width in pixels. Height is inferred from the source aspect ratio. Clamped to the source width (the maximum).";
		_widthField.RegisterValueChangedCallback(e => ClampResolution(_widthField, e.newValue, Source != null ? Source.width : 0));
		root.Add(_widthField);

		var generateButton = new Button(Generate) { text = "Generate" };
		generateButton.style.marginTop = 10;
		generateButton.style.height = 26;
		root.Add(generateButton);

		_saveButton = new Button(Save) { text = "Save SDF…" };
		_saveButton.style.height = 24;
		_saveButton.SetEnabled(false);
		root.Add(_saveButton);

		_status = new Label(string.Empty) { style = { marginTop = 6, whiteSpace = WhiteSpace.Normal } };
		root.Add(_status);

		// Fills the remaining window space and centers the preview within it.
		_previewArea = new VisualElement();
		_previewArea.style.flexGrow = 1;
		_previewArea.style.marginTop = 8;
		_previewArea.style.alignItems = Align.Center;
		_previewArea.style.justifyContent = Justify.Center;
		_previewArea.RegisterCallback<GeometryChangedEvent>(_ => UpdatePreviewLayout());
		root.Add(_previewArea);

		// Explicit size + StretchToFill so the bounds border hugs the image exactly. The
		// border marks where the generated image ends (SDFs fade to black/transparent at edges).
		_preview = new Image { scaleMode = ScaleMode.StretchToFill };
		SetUniformBorder(_preview, 1, new Color(1f, 0.5f, 0f));
		_previewArea.Add(_preview);

		LoadSettings();

		// Persist every setting on change so the window reopens with the latest values.
		_channelField.RegisterValueChangedCallback(_ => SaveSettings());
		_thresholdField.RegisterValueChangedCallback(_ => SaveSettings());
		_spreadInsideField.RegisterValueChangedCallback(_ => SaveSettings());
		_spreadOutsideField.RegisterValueChangedCallback(_ => SaveSettings());
		_blurField.RegisterValueChangedCallback(_ => SaveSettings());
		_aaField.RegisterValueChangedCallback(_ => SaveSettings());
		_packingField.RegisterValueChangedCallback(_ => SaveSettings());
		_widthField.RegisterValueChangedCallback(_ => SaveSettings());
	}

	void DrawSourceField()
	{
		EditorGUILayout.BeginHorizontal();
		GUILayout.Label("Source Texture", GUILayout.Width(90), GUILayout.Height(96));
		var picked = (Texture2D)EditorGUILayout.ObjectField(
			_source, typeof(Texture2D), false, GUILayout.Width(96), GUILayout.Height(96));
		GUILayout.FlexibleSpace();
		EditorGUILayout.EndHorizontal();

		if (picked != _source)
		{
			_source = picked;
			OnSourceChanged();
			SaveSettings();
		}
	}

	void OnDestroy()
	{
		if (_baked != null) DestroyImmediate(_baked);
	}

	void OnSourceChanged()
	{
		var src = Source;
		if (src == null) return;

		// Default width to the source width (the maximum); height follows the aspect ratio.
		_widthField.SetValueWithoutNotify(src.width);
		SetStatus($"Source: {src.width}×{src.height}.");
	}

	static void ClampResolution(IntegerField field, int value, int max)
	{
		if (max <= 0) return;
		int clamped = Mathf.Clamp(value, 1, max);
		if (clamped != value) field.SetValueWithoutNotify(clamped);
	}

	void Generate()
	{
		var src = Source;
		if (src == null)
		{
			SetStatus("Assign a source texture first.", error: true);
			return;
		}

		int outW = Mathf.Clamp(_widthField.value, 1, src.width);
		int outH = Mathf.Max(1, Mathf.RoundToInt(outW * (float)src.height / src.width));

		var settings = new SdfBaker.Settings
		{
			source = (SdfBaker.ChannelSource)_channelField.value,
			threshold = _thresholdField.value,
			spreadInside = Mathf.Max(0, _spreadInsideField.value),
			spreadOutside = Mathf.Max(0, _spreadOutsideField.value),
			blurRadius = _blurField.value,
			antiAlias = _aaField.value,
			packing = (SdfBaker.Packing)_packingField.value,
			width = outW,
			height = outH,
		};

		if (_baked != null) DestroyImmediate(_baked);
		_baked = SdfBaker.Bake(src, settings);

		_preview.image = _baked;
		UpdatePreviewLayout();
		_saveButton.SetEnabled(true);
		SetStatus($"Generated {settings.width}×{settings.height}. Adjust and re-Generate, or Save.");
	}

	static void SetUniformBorder(VisualElement e, float width, Color color)
	{
		e.style.borderTopWidth = width;
		e.style.borderBottomWidth = width;
		e.style.borderLeftWidth = width;
		e.style.borderRightWidth = width;
		e.style.borderTopColor = color;
		e.style.borderBottomColor = color;
		e.style.borderLeftColor = color;
		e.style.borderRightColor = color;
	}

	// Shows the SDF at full resolution (1:1) when it fits, and scales it down to fit the
	// preview area when there isn't enough room. Never upscales past native resolution.
	void UpdatePreviewLayout()
	{
		if (_baked == null) return;

		float availW = _previewArea.resolvedStyle.width;
		float availH = _previewArea.resolvedStyle.height;
		if (availW <= 0f || availH <= 0f) return;

		// Leave a couple of px so the border isn't clipped against the area edges.
		float scale = Mathf.Min(1f, (availW - 2f) / _baked.width, (availH - 2f) / _baked.height);
		_preview.style.width = _baked.width * scale;
		_preview.style.height = _baked.height * scale;
	}

	void Save()
	{
		if (_baked == null)
		{
			SetStatus("Generate an SDF before saving.", error: true);
			return;
		}

		string sourcePath = AssetDatabase.GetAssetPath(Source);
		string dir = string.IsNullOrEmpty(sourcePath) ? "Assets" : Path.GetDirectoryName(sourcePath);
		string defaultName = (Source != null ? Source.name : "Texture") + " SDF";

		string path = EditorUtility.SaveFilePanelInProject(
			"Save SDF", defaultName, "png", "Choose where to save the generated SDF", dir);
		if (string.IsNullOrEmpty(path)) return;

		File.WriteAllBytes(path, _baked.EncodeToPNG());
		AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

		// SDF values are linear data, not color — sampling must return the literal stored value.
		var importer = AssetImporter.GetAtPath(path) as TextureImporter;
		if (importer != null)
		{
			importer.textureType = TextureImporterType.Default;
			importer.sRGBTexture = false;
			importer.wrapMode = TextureWrapMode.Clamp;
			importer.alphaIsTransparency = (SdfBaker.Packing)_packingField.value == SdfBaker.Packing.Alpha;
			importer.SaveAndReimport();
		}

		var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
		EditorGUIUtility.PingObject(asset);
		Selection.activeObject = asset;
		SetStatus($"Saved to {path}.");
	}

	// ---- Settings persistence (per-project, local) ----

	static string Key(string name) => Application.dataPath + "_SdfGenerator_" + name;

	void SaveSettings()
	{
		EditorPrefs.SetInt(Key("Channel"), (int)(SdfBaker.ChannelSource)_channelField.value);
		EditorPrefs.SetFloat(Key("Threshold"), _thresholdField.value);
		EditorPrefs.SetInt(Key("SpreadInside"), _spreadInsideField.value);
		EditorPrefs.SetInt(Key("SpreadOutside"), _spreadOutsideField.value);
		EditorPrefs.SetInt(Key("Blur"), _blurField.value);
		EditorPrefs.SetBool(Key("AntiAlias"), _aaField.value);
		EditorPrefs.SetInt(Key("Packing"), (int)(SdfBaker.Packing)_packingField.value);
		EditorPrefs.SetInt(Key("Width"), _widthField.value);

		string guid = _source != null
			? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_source))
			: string.Empty;
		EditorPrefs.SetString(Key("SourceGuid"), guid);
	}

	void LoadSettings()
	{
		_channelField.SetValueWithoutNotify((SdfBaker.ChannelSource)EditorPrefs.GetInt(Key("Channel"), 0));
		_thresholdField.SetValueWithoutNotify(EditorPrefs.GetFloat(Key("Threshold"), 0.5f));
		_spreadInsideField.SetValueWithoutNotify(EditorPrefs.GetInt(Key("SpreadInside"), 8));
		_spreadOutsideField.SetValueWithoutNotify(EditorPrefs.GetInt(Key("SpreadOutside"), 8));
		_blurField.SetValueWithoutNotify(EditorPrefs.GetInt(Key("Blur"), 0));
		_aaField.SetValueWithoutNotify(EditorPrefs.GetBool(Key("AntiAlias"), true));
		_packingField.SetValueWithoutNotify((SdfBaker.Packing)EditorPrefs.GetInt(Key("Packing"), 0));

		string guid = EditorPrefs.GetString(Key("SourceGuid"), string.Empty);
		if (!string.IsNullOrEmpty(guid))
		{
			string path = AssetDatabase.GUIDToAssetPath(guid);
			if (!string.IsNullOrEmpty(path))
			{
				_source = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
				if (_source != null) OnSourceChanged(); // sets width to source width
			}
		}

		// Restore the saved width last so it overrides the source-derived default (clamped to source).
		int savedWidth = EditorPrefs.GetInt(Key("Width"), 0);
		if (savedWidth > 0)
			_widthField.SetValueWithoutNotify(_source != null ? Mathf.Clamp(savedWidth, 1, _source.width) : savedWidth);
	}

	void SetStatus(string message, bool error = false)
	{
		_status.text = message;
		_status.style.color = error ? new Color(0.9f, 0.4f, 0.4f) : new Color(0.7f, 0.7f, 0.7f);
	}
}
#endif
