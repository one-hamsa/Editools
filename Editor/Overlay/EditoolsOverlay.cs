#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Scrubs stale/duplicate overlay entries from layout files on startup.
/// Prevents ToolbarOverlay from accumulating duplicate instances across restarts.
/// </summary>
static class EditoolsLayoutCleaner
{
	static readonly string[] s_staleIds = { "view-comp", "advanced-visuals", "advanced-visuals-toolbar" };
	const string k_OverlayId = "editools-toolbar";

	[InitializeOnLoadMethod]
	static void CleanOnStartup()
	{
		var dir = Path.Combine("UserSettings", "Layouts");
		CleanLayoutDir(dir);
		EnsureToolbarMode(dir);
	}

	static void CleanLayoutDir(string dir)
	{
		if (!Directory.Exists(dir)) return;
		foreach (var file in Directory.GetFiles(dir, "*.dwlt"))
			CleanLayoutFile(file);
	}

	static void CleanLayoutFile(string path)
	{
		var lines = new List<string>(File.ReadAllLines(path));
		bool modified = false;

		for (int i = lines.Count - 1; i >= 0; i--)
		{
			string trimmed = lines[i].TrimEnd();
			if (!trimmed.StartsWith("      id: ")) continue;

			string id = trimmed.Substring("      id: ".Length).Trim();
			if (!IsStaleId(id)) continue;

			// Walk back to block start ("    - dockPosition:")
			int blockStart = i;
			while (blockStart > 0 && !lines[blockStart - 1].TrimEnd().StartsWith("    - dockPosition:"))
				blockStart--;
			if (blockStart > 0) blockStart--;

			// Walk forward to block end
			int blockEnd = i + 1;
			while (blockEnd < lines.Count)
			{
				string next = lines[blockEnd].TrimEnd();
				if (next.StartsWith("    - dockPosition:") || next.StartsWith("    m_"))
					break;
				blockEnd++;
			}

			lines.RemoveRange(blockStart, blockEnd - blockStart);
			modified = true;
			i = blockStart;
		}

		if (modified)
			File.WriteAllLines(path, lines.ToArray());
	}

	static bool IsStaleId(string id)
	{
		foreach (var stale in s_staleIds)
			if (id == stale) return true;

		if (id.StartsWith(k_OverlayId + " (") && id.EndsWith(")"))
			return true;

		return false;
	}

	/// <summary>
	/// Ensures the overlay is stored as HorizontalToolbar in the top toolbar strip,
	/// not as a collapsed panel. Fixes layout if Unity resets it.
	/// </summary>
	static void EnsureToolbarMode(string dir)
	{
		if (!Directory.Exists(dir)) return;
		foreach (var file in Directory.GetFiles(dir, "*.dwlt"))
			FixOverlayLayout(file);
	}

	static void FixOverlayLayout(string path)
	{
		var lines = File.ReadAllLines(path);
		bool modified = false;

		for (int i = 0; i < lines.Length; i++)
		{
			if (!lines[i].TrimEnd().EndsWith("id: " + k_OverlayId)) continue;

			// Fix layout value (forward from id: index, then layout)
			for (int j = i + 1; j < System.Math.Min(i + 4, lines.Length); j++)
			{
				string trimmed = lines[j].TrimEnd();
				if (trimmed.StartsWith("      layout:") && !trimmed.EndsWith("1"))
				{
					lines[j] = "      layout: 1";
					modified = true;
				}
			}

			// Fix dock properties (backward from id — containerId, collapsed, displayed)
			for (int j = i - 1; j >= System.Math.Max(0, i - 10); j--)
			{
				string trimmed = lines[j].TrimEnd();
				if (trimmed.StartsWith("    - dockPosition:")) break; // don't go past block start

				if (trimmed.StartsWith("      containerId:") && !trimmed.EndsWith("overlay-toolbar__top"))
				{
					lines[j] = "      containerId: overlay-toolbar__top";
					modified = true;
				}
				if (trimmed == "      collapsed: 1")
				{
					lines[j] = "      collapsed: 0";
					modified = true;
				}
				if (trimmed == "      displayed: 0")
				{
					lines[j] = "      displayed: 1";
					modified = true;
				}
			}
		}

		if (modified)
			File.WriteAllLines(path, lines);
	}
}

/// <summary>
/// Data container for a single captured screenshot.
/// </summary>
public class ViewCompScreenshot
{
	public Texture2D texture;
	public string comment;
}

/// <summary>
/// Main Editools overlay in the Scene View toolbar. Hosts screenshot capture,
/// material check, hierarchy heatmap, and serves as the central access point
/// for all Editools features. Each SceneView window gets its own instance.
/// </summary>
[Overlay(typeof(SceneView), "editools-toolbar", "Editools")]
public class EditoolsOverlay : ToolbarOverlay
{
	const int MaxScreenshots = 10;
	const string k_Session = "AV_";
	static string ScreenshotDir => Path.Combine("Library", "AdvancedVisuals");

	internal static readonly Dictionary<SceneView, EditoolsOverlay> s_instances = new();
	static bool s_subscribedToSceneGui;

	readonly List<ViewCompScreenshot> _screenshots = new();

	int _activeIndex = -1;
	Vector2 _lastViewportSize;

	VisualElement _displayRoot;
	IMGUIContainer _displayIMGUI;

	// Screenshot strip element reference (for rebuilding dynamic buttons)
	EditoolsScreenshotStrip _screenshotStrip;

	EditoolsOverlay() : base(
		EditoolsScreenshotStrip.k_Id,
		EditoolsCaptureButton.k_Id,
		EditoolsMaterialCheckButton.k_Id,
		EditoolsHeatmapButton.k_Id
	) { }

	// ---- Element registration ----

	internal void RegisterScreenshotStrip(EditoolsScreenshotStrip strip) => _screenshotStrip = strip;
	internal void UnregisterScreenshotStrip(EditoolsScreenshotStrip strip)
	{
		if (_screenshotStrip == strip) _screenshotStrip = null;
	}

	// ---- Internal accessors for toolbar elements ----

	internal IReadOnlyList<ViewCompScreenshot> Screenshots => _screenshots;
	internal int ActiveIndex => _activeIndex;

	// ---- Overlay lifecycle ----

	public override void OnCreated()
	{
		var sceneView = containerWindow as SceneView;
		if (sceneView != null)
			s_instances[sceneView] = this;

		if (!s_subscribedToSceneGui)
		{
			SceneView.duringSceneGui += OnSceneGUI;
			s_subscribedToSceneGui = true;
		}

		AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
		RestoreScreenshots();
		MaterialCheckPopup.RestoreAfterReload();
	}

	public override void OnWillBeDestroyed()
	{
		AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;

		var sceneView = containerWindow as SceneView;
		if (sceneView != null)
			s_instances.Remove(sceneView);

		ClearAllScreenshots();

		if (s_instances.Count == 0 && s_subscribedToSceneGui)
		{
			SceneView.duringSceneGui -= OnSceneGUI;
			s_subscribedToSceneGui = false;
		}
	}

	// ---- Capture ----

	internal void OnCaptureClicked()
	{
		var sceneView = containerWindow as SceneView;
		if (sceneView == null) return;

		var tex = CaptureSceneView(sceneView);
		if (tex == null) return;

		// If at max, discard oldest
		if (_screenshots.Count >= MaxScreenshots)
		{
			if (_activeIndex == 0)
				_activeIndex = -1;
			else if (_activeIndex > 0)
				_activeIndex--;

			Object.DestroyImmediate(_screenshots[0].texture);
			_screenshots.RemoveAt(0);
		}

		_screenshots.Add(new ViewCompScreenshot { texture = tex, comment = "" });
		_lastViewportSize = sceneView.cameraViewport.size;

		_screenshotStrip?.Rebuild();
	}

	Texture2D CaptureSceneView(SceneView sceneView)
	{
		Camera cam = sceneView.camera;
		if (cam == null) return null;

		Rect viewport = sceneView.cameraViewport;
		float ppp = EditorGUIUtility.pixelsPerPoint;
		int w = Mathf.Max(1, (int)(viewport.width * ppp));
		int h = Mathf.Max(1, (int)(viewport.height * ppp));

		var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.Default);
		var prevTarget = cam.targetTexture;
		var prevActive = RenderTexture.active;

		cam.targetTexture = rt;
		cam.Render();

		RenderTexture.active = rt;
		var tex = new Texture2D(w, h, TextureFormat.RGB24, false)
		{
			hideFlags = HideFlags.HideAndDontSave
		};
		tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
		tex.Apply();

		cam.targetTexture = prevTarget;
		RenderTexture.active = prevActive;
		RenderTexture.ReleaseTemporary(rt);

		return tex;
	}

	// ---- Screenshot selection ----

	internal void OnScreenshotClicked(int index)
	{
		if (index < 0 || index >= _screenshots.Count) return;

		_activeIndex = (_activeIndex == index) ? -1 : index;
		_screenshotStrip?.UpdateStyles();

		if (_activeIndex >= 0)
			ShowScreenshotDisplay();
		else
			HideScreenshotDisplay();

		containerWindow?.Repaint();
	}

	internal void DeleteScreenshot(int index)
	{
		if (index < 0 || index >= _screenshots.Count) return;

		if (_activeIndex == index)
		{
			_activeIndex = -1;
			HideScreenshotDisplay();
		}
		else if (_activeIndex > index)
			_activeIndex--;

		Object.DestroyImmediate(_screenshots[index].texture);
		_screenshots.RemoveAt(index);

		_screenshotStrip?.Rebuild();
		containerWindow?.Repaint();
	}

	internal void ShowCommentPopup(int index, VisualElement anchor)
	{
		if (index < 0 || index >= _screenshots.Count) return;
		var popup = new ViewCompCommentPopup(_screenshots[index]);
		UnityEditor.PopupWindow.Show(anchor.worldBound, popup);
	}

	// ---- Cleanup ----

	internal void ClearAllScreenshots()
	{
		foreach (var ss in _screenshots)
		{
			if (ss.texture != null)
				Object.DestroyImmediate(ss.texture);
		}
		_screenshots.Clear();
		_activeIndex = -1;
		_lastViewportSize = Vector2.zero;

		HideScreenshotDisplay();
		_screenshotStrip?.Rebuild();
	}

	// ---- Persistence across domain reload ----

	void OnBeforeAssemblyReload()
	{
		var dir = ScreenshotDir;
		if (Directory.Exists(dir)) Directory.Delete(dir, true);
		Directory.CreateDirectory(dir);

		SessionState.SetInt(k_Session + "Count", _screenshots.Count);
		SessionState.SetInt(k_Session + "ActiveIndex", _activeIndex);
		SessionState.SetFloat(k_Session + "ViewportW", _lastViewportSize.x);
		SessionState.SetFloat(k_Session + "ViewportH", _lastViewportSize.y);

		for (int i = 0; i < _screenshots.Count; i++)
		{
			SessionState.SetString(k_Session + $"Comment_{i}", _screenshots[i].comment ?? "");
			if (_screenshots[i].texture != null)
			{
				byte[] png = _screenshots[i].texture.EncodeToPNG();
				File.WriteAllBytes(Path.Combine(dir, $"{i}.png"), png);
			}
		}
	}

	void RestoreScreenshots()
	{
		int count = SessionState.GetInt(k_Session + "Count", 0);
		if (count <= 0) return;

		// Consume the saved state so other overlays don't double-restore
		SessionState.SetInt(k_Session + "Count", 0);

		var dir = ScreenshotDir;
		if (!Directory.Exists(dir)) return;

		_lastViewportSize = new Vector2(
			SessionState.GetFloat(k_Session + "ViewportW", 0f),
			SessionState.GetFloat(k_Session + "ViewportH", 0f));

		for (int i = 0; i < count; i++)
		{
			string path = Path.Combine(dir, $"{i}.png");
			if (!File.Exists(path)) continue;

			byte[] png = File.ReadAllBytes(path);
			var tex = new Texture2D(2, 2, TextureFormat.RGB24, false)
			{
				hideFlags = HideFlags.HideAndDontSave
			};
			tex.LoadImage(png);

			string comment = SessionState.GetString(k_Session + $"Comment_{i}", "");
			_screenshots.Add(new ViewCompScreenshot { texture = tex, comment = comment });
		}

		_activeIndex = SessionState.GetInt(k_Session + "ActiveIndex", -1);
		if (_activeIndex >= _screenshots.Count) _activeIndex = -1;

		// Clean up temp files
		try { Directory.Delete(dir, true); } catch { }

		// Defer display restoration until the scene view is fully ready
		if (_activeIndex >= 0)
		{
			EditorApplication.delayCall += () =>
			{
				_screenshotStrip?.Rebuild();
				if (_activeIndex >= 0 && _activeIndex < _screenshots.Count)
					ShowScreenshotDisplay();
			};
		}
	}

	void CheckViewportSize(SceneView sceneView)
	{
		if (_screenshots.Count == 0) return;

		Vector2 currentSize = sceneView.cameraViewport.size;
		if (_lastViewportSize != Vector2.zero && _lastViewportSize != currentSize)
			ClearAllScreenshots();
	}

	// ---- Scene GUI: block navigation while viewing ----

	static void OnSceneGUI(SceneView sceneView)
	{
		if (!s_instances.TryGetValue(sceneView, out var overlay)) return;

		overlay.CheckViewportSize(sceneView);

		if (overlay._activeIndex < 0 || overlay._activeIndex >= overlay._screenshots.Count)
			return;

		var screenshot = overlay._screenshots[overlay._activeIndex];
		if (screenshot.texture == null) return;

		int controlId = GUIUtility.GetControlID(FocusType.Passive);
		HandleUtility.AddDefaultControl(controlId);

		Event e = Event.current;
		if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
		{
			overlay._activeIndex = -1;
			overlay._screenshotStrip?.UpdateStyles();
			overlay.HideScreenshotDisplay();
			overlay.containerWindow?.Repaint();
			e.Use();
			return;
		}

		if (e.type != EventType.Repaint && e.type != EventType.Layout)
			e.Use();

		overlay._displayIMGUI?.MarkDirtyRepaint();
	}

	// ---- Screenshot display ----

	void ShowScreenshotDisplay()
	{
		var sceneView = containerWindow as SceneView;
		if (sceneView == null) return;

		if (_displayRoot == null)
		{
			_displayRoot = new VisualElement();
			_displayRoot.name = "view-comp-display";
			_displayRoot.style.position = Position.Absolute;
			_displayRoot.style.left = 0;
			_displayRoot.style.top = 0;
			_displayRoot.style.right = 0;
			_displayRoot.style.bottom = 0;

			_displayRoot.pickingMode = PickingMode.Position;
			_displayRoot.RegisterCallback<WheelEvent>(e =>
			{
				e.StopPropagation();
				e.PreventDefault();
			});

			_displayRoot.RegisterCallback<PointerDownEvent>(e =>
			{
				_activeIndex = -1;
				_screenshotStrip?.UpdateStyles();
				HideScreenshotDisplay();
				containerWindow?.Repaint();
				e.StopPropagation();
			});

			_displayIMGUI = new IMGUIContainer(DrawScreenshotDisplay);
			_displayIMGUI.style.position = Position.Absolute;
			_displayIMGUI.style.left = 0;
			_displayIMGUI.style.top = 0;
			_displayIMGUI.style.right = 0;
			_displayIMGUI.style.bottom = 0;
			_displayIMGUI.pickingMode = PickingMode.Ignore;
			_displayRoot.Add(_displayIMGUI);
		}

		if (_displayRoot.parent == null)
		{
			sceneView.rootVisualElement.Add(_displayRoot);

			foreach (var child in sceneView.rootVisualElement.Children())
			{
				if (child == _displayRoot) continue;
				if (child is IMGUIContainer) continue;
				_displayRoot.PlaceBehind(child);
				break;
			}
		}

		_displayRoot.style.display = DisplayStyle.Flex;
		_displayIMGUI.MarkDirtyRepaint();
	}

	void HideScreenshotDisplay()
	{
		if (_displayRoot != null)
			_displayRoot.RemoveFromHierarchy();
	}

	void DrawScreenshotDisplay()
	{
		if (_activeIndex < 0 || _activeIndex >= _screenshots.Count) return;

		var screenshot = _screenshots[_activeIndex];
		if (screenshot.texture == null) return;

		Rect area = _displayIMGUI.contentRect;
		if (float.IsNaN(area.width) || area.width <= 0) return;

		GUI.DrawTexture(area, screenshot.texture, ScaleMode.StretchToFill);

		if (!string.IsNullOrEmpty(screenshot.comment))
		{
			float padding = 6f;
			var style = new GUIStyle(EditorStyles.label)
			{
				normal = { textColor = Color.white },
				fontSize = 12,
				wordWrap = true
			};

			var content = new GUIContent(screenshot.comment);
			float maxWidth = Mathf.Min(400f, area.width - padding * 4);
			float textHeight = style.CalcHeight(content, maxWidth);

			var bgRect = new Rect(padding, padding, maxWidth + padding * 2, textHeight + padding * 2);
			EditorGUI.DrawRect(bgRect, new Color(0f, 0f, 0f, 0.65f));

			var textRect = new Rect(bgRect.x + padding, bgRect.y + padding, maxWidth, textHeight);
			GUI.Label(textRect, content, style);
		}

		{
			string label = $"#{_activeIndex + 1}";
			var style = new GUIStyle(EditorStyles.boldLabel)
			{
				normal = { textColor = new Color(1f, 1f, 1f, 0.7f) },
				fontSize = 11,
				alignment = TextAnchor.UpperRight
			};
			GUI.Label(new Rect(area.xMax - 40, 6, 34, 20), label, style);
		}
	}

	// ---- Shared helper: find overlay from a toolbar element ----

	internal static EditoolsOverlay FindOverlayFromElement(VisualElement element)
	{
		// Match by panel — toolbar strip elements share a panel with their SceneView
		// even though they're not children of rootVisualElement
		var p = element.panel;
		if (p != null)
		{
			foreach (var kvp in s_instances)
			{
				if (kvp.Key != null && kvp.Key.rootVisualElement?.panel == p)
					return kvp.Value;
			}
		}

		// Fallback: single SceneView
		if (s_instances.Count == 1)
		{
			foreach (var kvp in s_instances)
				return kvp.Value;
		}

		return null;
	}
}

// ---- Toolbar elements (each is a separate class so Unity can lay them out individually) ----

/// <summary>
/// Dynamic strip of numbered screenshot buttons. Empty when no screenshots exist.
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsScreenshotStrip : VisualElement
{
	public const string k_Id = "Editools/Screenshots";

	EditoolsOverlay _overlay;
	readonly List<Button> _buttons = new();

	public EditoolsScreenshotStrip()
	{
		style.flexDirection = FlexDirection.Row;
		style.alignItems = Align.Center;

		RegisterCallback<AttachToPanelEvent>(_ =>
		{
			// Defer lookup to avoid recursive AttachToPanelEvent from rootVisualElement access
			EditorApplication.delayCall += () =>
			{
				if (panel == null) return; // detached before callback fired
				_overlay = EditoolsOverlay.FindOverlayFromElement(this);
				_overlay?.RegisterScreenshotStrip(this);
				Rebuild();
			};
		});

		RegisterCallback<DetachFromPanelEvent>(_ =>
		{
			_overlay?.UnregisterScreenshotStrip(this);
			_overlay = null;
		});
	}

	public void Rebuild()
	{
		Clear();
		_buttons.Clear();

		_overlay ??= EditoolsOverlay.FindOverlayFromElement(this);
		if (_overlay == null) return;

		for (int i = 0; i < _overlay.Screenshots.Count; i++)
		{
			int idx = i;
			var btn = new Button(() => _overlay.OnScreenshotClicked(idx))
			{
				text = (idx + 1).ToString(),
				tooltip = $"View screenshot {idx + 1}"
			};
			btn.style.minWidth = 24;
			btn.style.paddingLeft = 4;
			btn.style.paddingRight = 4;

			btn.AddManipulator(new ContextualMenuManipulator(evt =>
			{
				evt.menu.AppendAction("Edit Comment...", _ => _overlay?.ShowCommentPopup(idx, btn));
				evt.menu.AppendAction("Delete", _ => _overlay?.DeleteScreenshot(idx));
				evt.menu.AppendSeparator("");
				evt.menu.AppendAction("Delete All", _ => _overlay?.ClearAllScreenshots());
			}));

			_buttons.Add(btn);
			Add(btn);
		}

		UpdateStyles();
	}

	public void UpdateStyles()
	{
		if (_overlay == null) return;

		for (int i = 0; i < _buttons.Count; i++)
		{
			bool isActive = (i == _overlay.ActiveIndex);
			_buttons[i].style.backgroundColor = isActive
				? new Color(0.8f, 0.35f, 0.35f, 1f)
				: StyleKeyword.Null;
			_buttons[i].style.color = isActive
				? Color.white
				: StyleKeyword.Null;
		}
	}
}

/// <summary>
/// Capture button — takes a screenshot of the current scene view.
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsCaptureButton : EditorToolbarButton
{
	public const string k_Id = "Editools/Capture";

	EditoolsOverlay _overlay;

	public EditoolsCaptureButton() : base()
	{
		icon = EditorGUIUtility.IconContent("d_SceneViewCamera").image as Texture2D;
		tooltip = "Capture current scene view";
		clicked += OnClick;

		RegisterCallback<AttachToPanelEvent>(_ =>
		{
			EditorApplication.delayCall += () =>
			{
				if (panel == null) return;
				_overlay = EditoolsOverlay.FindOverlayFromElement(this);
			};
		});
	}

	void OnClick()
	{
		_overlay ??= EditoolsOverlay.FindOverlayFromElement(this);
		_overlay?.OnCaptureClicked();
	}
}

/// <summary>
/// Material Check dropdown — shows a small popup to assign and toggle
/// a material override on all opaque scene renderers.
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsMaterialCheckButton : EditorToolbarDropdown
{
	public const string k_Id = "Editools/MaterialCheck";

	public EditoolsMaterialCheckButton()
	{
		icon = EditorGUIUtility.IconContent("d_Material Icon").image as Texture2D;
		tooltip = "Material Check — drag a material here to assign it";
		clicked += () => UnityEditor.PopupWindow.Show(worldBound, new MaterialCheckPopup());

		// Accept drag-and-drop of materials onto this button
		RegisterCallback<DragEnterEvent>(_ =>
		{
			if (DragAndDrop.objectReferences.Length == 1 && DragAndDrop.objectReferences[0] is Material)
				DragAndDrop.visualMode = DragAndDropVisualMode.Link;
		});
		RegisterCallback<DragUpdatedEvent>(_ =>
		{
			if (DragAndDrop.objectReferences.Length == 1 && DragAndDrop.objectReferences[0] is Material)
				DragAndDrop.visualMode = DragAndDropVisualMode.Link;
		});
		RegisterCallback<DragPerformEvent>(_ =>
		{
			if (DragAndDrop.objectReferences.Length == 1 && DragAndDrop.objectReferences[0] is Material mat)
			{
				DragAndDrop.AcceptDrag();
				MaterialCheckPopup.SetMaterial(mat);
			}
		});
	}
}

/// <summary>
/// Hierarchy Heatmap toggle+dropdown — click the icon to toggle on/off,
/// click the arrow to open settings menu. Mirrors the Gizmos button pattern.
/// Toggle state is synced across all SceneView instances.
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsHeatmapButton : VisualElement
{
	public const string k_Id = "Editools/Heatmap";

	static readonly List<EditoolsHeatmapButton> s_instances = new();

	readonly EditorToolbarToggle _toggle;

	public EditoolsHeatmapButton()
	{
		style.flexDirection = FlexDirection.Row;
		style.alignItems = Align.Center;

		_toggle = new EditorToolbarToggle
		{
			icon = EditorGUIUtility.IconContent("d_Grid.Default").image as Texture2D,
			tooltip = "Toggle Hierarchy Heatmap",
			value = EditorPrefs.GetBool("HierarchyHeatmapEnabled", false)
		};
		_toggle.RegisterValueChangedCallback(evt =>
		{
			HierarchyHeatmap.SetEnabled(evt.newValue);
			// Sync all other instances
			foreach (var inst in s_instances)
				if (inst != this)
					inst._toggle.SetValueWithoutNotify(evt.newValue);
		});
		// Fuse right edge with arrow
		_toggle.style.borderTopRightRadius = 0;
		_toggle.style.borderBottomRightRadius = 0;
		_toggle.style.marginRight = 0;
		_toggle.style.paddingLeft = 2;
		_toggle.style.paddingRight = 2;
		Add(_toggle);

		var arrow = new EditorToolbarButton { text = "\u25BE", tooltip = "Heatmap options" };
		arrow.clicked += ShowMenu;
		// Fuse left edge with toggle, 1px gap
		arrow.style.borderTopLeftRadius = 0;
		arrow.style.borderBottomLeftRadius = 0;
		arrow.style.marginLeft = 1;
		arrow.style.paddingLeft = 2;
		arrow.style.paddingRight = 4;
		arrow.style.minWidth = StyleKeyword.Auto;
		Add(arrow);

		RegisterCallback<AttachToPanelEvent>(_ => s_instances.Add(this));
		RegisterCallback<DetachFromPanelEvent>(_ => s_instances.Remove(this));
	}

	void ShowMenu()
	{
		var menu = new GenericMenu();
		menu.AddItem(new GUIContent("Settings..."), false, () => HeatmapSettingsWindow.ShowWindow());
		menu.AddItem(new GUIContent("Reset Recent"), false, () => HierarchyHeatmap.ResetRecent());
		menu.ShowAsContext();
	}
}

/// <summary>
/// Small popup shown from the Material Check toolbar dropdown.
/// Allows assigning a material and toggling the override on/off.
/// State (material + active flag) is static so it persists across popup opens.
/// </summary>
class MaterialCheckPopup : PopupWindowContent
{
	const string k_MaterialGuidKey = "AdvancedVisuals_MaterialCheck_GUID";
	const string k_IsOnSessionKey = "AV_MaterialCheck_IsOn";

	static Material s_material;
	static bool s_isOn;
	static bool s_loaded;

	public MaterialCheckPopup()
	{
		EnsureLoaded();

		// Sync with actual state in case another mode force-exited us
		if (s_isOn && !SceneMaterialOverride.IsModeActive("MaterialCheck"))
			s_isOn = false;
	}

	static void EnsureLoaded()
	{
		if (s_loaded) return;
		s_loaded = true;

		string guid = EditorPrefs.GetString(k_MaterialGuidKey, "");
		if (!string.IsNullOrEmpty(guid))
		{
			string path = AssetDatabase.GUIDToAssetPath(guid);
			if (!string.IsNullOrEmpty(path))
				s_material = AssetDatabase.LoadAssetAtPath<Material>(path);
		}
	}

	internal static void SetMaterial(Material mat)
	{
		s_material = mat;
		SaveMaterialPref(mat);
	}

	/// <summary>
	/// Called from the overlay after domain reload to re-enter MaterialCheck
	/// if it was active before the reload.
	/// </summary>
	internal static void RestoreAfterReload()
	{
		if (!SessionState.GetBool(k_IsOnSessionKey, false)) return;

		EnsureLoaded();
		if (s_material != null && !SceneMaterialOverride.IsModeActive("MaterialCheck"))
		{
			EditorApplication.delayCall += () =>
			{
				SceneMaterialOverride.Enter(s_material, "MaterialCheck", () => s_isOn = false);
				s_isOn = true;
			};
		}
	}

	static void SaveMaterialPref(Material mat)
	{
		if (mat != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out string guid, out long _))
			EditorPrefs.SetString(k_MaterialGuidKey, guid);
		else
			EditorPrefs.DeleteKey(k_MaterialGuidKey);
	}

	public override Vector2 GetWindowSize() => new Vector2(220, 58);

	public override void OnGUI(Rect rect)
	{
		var newMat = (Material)EditorGUILayout.ObjectField(s_material, typeof(Material), false);
		if (newMat != s_material)
		{
			s_material = newMat;
			SaveMaterialPref(s_material);
		}

		EditorGUILayout.BeginHorizontal();

		using (new EditorGUI.DisabledScope(s_material == null || s_isOn))
		{
			if (GUILayout.Button("On"))
			{
				SceneMaterialOverride.Enter(s_material, "MaterialCheck", () => s_isOn = false);
				s_isOn = true;
				SessionState.SetBool(k_IsOnSessionKey, true);
			}
		}

		using (new EditorGUI.DisabledScope(!s_isOn))
		{
			if (GUILayout.Button("Off"))
			{
				SceneMaterialOverride.Exit();
				s_isOn = false;
				SessionState.SetBool(k_IsOnSessionKey, false);
			}
		}

		EditorGUILayout.EndHorizontal();
	}
}

/// <summary>
/// Small popup window for editing a screenshot's comment.
/// </summary>
public class ViewCompCommentPopup : PopupWindowContent
{
	readonly ViewCompScreenshot _screenshot;
	string _text;

	public ViewCompCommentPopup(ViewCompScreenshot screenshot)
	{
		_screenshot = screenshot;
		_text = screenshot.comment ?? "";
	}

	public override Vector2 GetWindowSize()
	{
		return new Vector2(260, 58);
	}

	public override void OnGUI(Rect rect)
	{
		EditorGUILayout.LabelField("Comment:", EditorStyles.miniLabel);
		_text = EditorGUILayout.TextField(_text);

		EditorGUILayout.BeginHorizontal();
		GUILayout.FlexibleSpace();
		if (GUILayout.Button("OK", GUILayout.Width(50)))
		{
			_screenshot.comment = _text;
			editorWindow.Close();
		}
		EditorGUILayout.EndHorizontal();
	}

	public override void OnClose()
	{
		_screenshot.comment = _text;
	}
}
#endif
