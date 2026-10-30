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
	static readonly string[] s_staleIds = {
		"view-comp", "advanced-visuals", "advanced-visuals-toolbar",
		"Editools/Heatmap", "Editools/QuickTransform"
	};
	const string k_OverlayId = "editools-toolbar";

	[InitializeOnLoadMethod]
	static void CleanOnStartup()
	{
		// Defer file I/O to avoid blocking domain reload
		EditorApplication.delayCall += () =>
		{
			var dir = Path.Combine("UserSettings", "Layouts");
			CleanLayoutDir(dir);
			EnsureToolbarMode(dir);
		};
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
/// Editools toolbar overlay in the Scene View. Its buttons are the functions the user selected
/// for the overlay in <see cref="EditoolsSettings"/> (the Scene Capture strip always comes first),
/// followed by the settings menu. Also owns the screenshot capture / compare feature.
/// Each SceneView window gets its own instance; <see cref="IsActive"/> is true while at least
/// one instance is displayed, and every Editools tool is subscribed only while it is.
/// </summary>
[Overlay(typeof(SceneView), "editools-toolbar", "Editools")]
public class EditoolsOverlay : Overlay, ICreateHorizontalToolbar, ICreateVerticalToolbar
{
	const int MaxScreenshots = 10;
	const string k_Session = "AV_";
	const string k_ScreenshotFunctionId = "screenshot-capture";
	static string ScreenshotDir => Path.Combine("Library", "AdvancedVisuals");

	internal static readonly Dictionary<SceneView, EditoolsOverlay> s_instances = new();
	static bool s_subscribedToSceneGui;
	static bool s_isActive;

	/// <summary>
	/// True when at least one Editools overlay is displayed in a SceneView. Tools subscribe to
	/// editor events only while this holds (see <see cref="EditoolsFunctions.SyncAll"/>).
	/// </summary>
	internal static bool IsActive => s_isActive;

	readonly List<ViewCompScreenshot> _screenshots = new();

	int _activeIndex = -1;
	Vector2 _lastViewportSize;

	VisualElement _displayRoot;
	IMGUIContainer _displayIMGUI;
	bool _pendingCapture;

	OverlayToolbar _toolbar;
	EditoolsScreenshotStrip _screenshotStrip;

	/// <summary>USS class for toolbar elements that handle right-click themselves; the overlay then does not open the settings for them.</summary>
	internal const string k_OwnContextMenuClass = "editools-own-context-menu";

	// ---- Toolbar content ----

	public override VisualElement CreatePanelContent() => CreateToolbar();
	public OverlayToolbar CreateHorizontalToolbarContent() => CreateToolbar();
	public OverlayToolbar CreateVerticalToolbarContent() => CreateToolbar();

	OverlayToolbar CreateToolbar()
	{
		_toolbar = new OverlayToolbar();
		// Right-click is consumed by the Scene View before it reaches toolbar elements, so the
		// watcher sits at the panel root (trickle-down) and tests the toolbar's children itself.
		_toolbar.RegisterCallback<AttachToPanelEvent>(evt =>
			evt.destinationPanel?.visualTree.RegisterCallback<PointerDownEvent>(OnPanelPointerDown, TrickleDown.TrickleDown));
		_toolbar.RegisterCallback<DetachFromPanelEvent>(evt =>
			evt.originPanel?.visualTree.UnregisterCallback<PointerDownEvent>(OnPanelPointerDown, TrickleDown.TrickleDown));
		_toolbar.RegisterCallback<ContextClickEvent>(evt =>
		{
			if (OpenSettingsForButtonAt(new Vector2(evt.mousePosition.x, evt.mousePosition.y)))
				evt.StopPropagation();
		});
		PopulateToolbar();
		return _toolbar;
	}

	void OnPanelPointerDown(PointerDownEvent evt)
	{
		if (evt.button != 1) return;
		if (OpenSettingsForButtonAt(new Vector2(evt.position.x, evt.position.y)))
			evt.StopImmediatePropagation();
	}

	// Right-click on any toolbar button opens the settings, except buttons with their own context menu.
	bool OpenSettingsForButtonAt(Vector2 panelPosition)
	{
		if (_toolbar == null || !_toolbar.worldBound.Contains(panelPosition)) return false;
		foreach (var child in _toolbar.Children())
		{
			if (!child.worldBound.Contains(panelPosition)) continue;
			if (child.ClassListContains(k_OwnContextMenuClass)) return false;
			EditoolsSettings.OpenInInspector();
			return true;
		}
		return false;
	}

	void PopulateToolbar()
	{
		_toolbar.Clear();
		_screenshotStrip = null;

		var capture = EditoolsFunctions.Find(k_ScreenshotFunctionId);
		if (capture.ShowsInOverlay)
		{
			_screenshotStrip = new EditoolsScreenshotStrip(this);
			_toolbar.Add(_screenshotStrip);
			_toolbar.Add(capture.CreateOverlayElement(this));
		}

		foreach (var function in EditoolsFunctions.All)
		{
			if (function == capture || !function.ShowsInOverlay) continue;
			var element = function.CreateOverlayElement(this);
			if (element != null) _toolbar.Add(element);
		}

		_toolbar.Add(new EditoolsSettingsButton());
		EditorToolbarUtility.SetupChildrenAsButtonStrip(_toolbar);
	}

	void OnSettingsChanged()
	{
		if (_toolbar != null) PopulateToolbar();
	}

	internal VisualElement CreateCaptureElement() => new EditoolsCaptureButton(this);

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

		displayedChanged += OnDisplayedChanged;
		AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
		EditoolsSettings.Changed += OnSettingsChanged;
		RestoreScreenshots();
		MaterialCheck.RestoreAfterReload();
		RefreshActive();
	}

	void OnDisplayedChanged(bool visible)
	{
		RefreshActive();
		// Hierarchy & project windows repaint so the heatmap follows the overlay immediately.
		EditorApplication.RepaintHierarchyWindow();
		EditorApplication.RepaintProjectWindow();
	}

	public override void OnWillBeDestroyed()
	{
		displayedChanged -= OnDisplayedChanged;
		AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
		EditoolsSettings.Changed -= OnSettingsChanged;

		var sceneView = containerWindow as SceneView;
		if (sceneView != null)
			s_instances.Remove(sceneView);

		ClearAllScreenshots();

		if (s_instances.Count == 0 && s_subscribedToSceneGui)
		{
			SceneView.duringSceneGui -= OnSceneGUI;
			s_subscribedToSceneGui = false;
		}

		RefreshActive();
	}

	// Recomputes IsActive from the live instances and re-syncs every tool to it.
	static void RefreshActive()
	{
		bool active = false;
		foreach (var kvp in s_instances)
			if (kvp.Value.displayed) { active = true; break; }

		s_isActive = active;
		EditoolsFunctions.SyncAll();
	}

	/// <summary>Captures the last active Scene View (settings menu entry point).</summary>
	internal static void CaptureLastActiveSceneView()
	{
		var view = SceneView.lastActiveSceneView;
		if (view != null && s_instances.TryGetValue(view, out var overlay))
			overlay.OnCaptureClicked();
	}

	// ---- Capture ----

	internal void OnCaptureClicked()
	{
		var sceneView = containerWindow as SceneView;
		if (sceneView == null) return;

		// Defer capture to the next Repaint so the gizmo RT has fresh content
		// from this frame's built-in gizmo pass (OnDrawGizmos, etc.).
		_pendingCapture = true;
		sceneView.Repaint();
	}

	void CaptureSceneView(SceneView sceneView)
	{
		Camera cam = sceneView.camera;
		if (cam == null) return;

		var prevTarget = cam.targetTexture;
		var prevActive = RenderTexture.active;

		// Use the camera's existing RT dimensions if available
		// (guarantees matching gizmo layer size). Otherwise fall back to viewport * DPI.
		int w, h;
		if (prevTarget != null)
		{
			w = prevTarget.width;
			h = prevTarget.height;
		}
		else
		{
			Rect viewport = sceneView.cameraViewport;
			float ppp = EditorGUIUtility.pixelsPerPoint;
			w = Mathf.Max(1, (int)(viewport.width * ppp));
			h = Mathf.Max(1, (int)(viewport.height * ppp));
		}

		// Read the gizmo layer from camera.targetTexture.
		// During Repaint, this RT has fresh content from the built-in gizmo pass
		// (OnDrawGizmos, OnDrawGizmosSelected, etc.) with transparent-cleared background.
		Texture2D gizmoTex = null;
		if (prevTarget != null)
		{
			RenderTexture.active = prevTarget;
			gizmoTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
			gizmoTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
			gizmoTex.Apply();
		}

		// Render 3D scene (no gizmos) to our own RT at matching dimensions.
		var sceneRT = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.Default);
		cam.targetTexture = sceneRT;
		cam.Render();
		cam.targetTexture = prevTarget;

		// Read scene pixels
		RenderTexture.active = sceneRT;
		var tex = new Texture2D(w, h, TextureFormat.RGB24, false)
		{
			hideFlags = HideFlags.HideAndDontSave
		};
		tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);

		// Alpha-composite gizmo layer over scene
		if (gizmoTex != null)
		{
			var scenePixels = tex.GetPixels32();
			var gizmoPixels = gizmoTex.GetPixels32();
			for (int i = 0; i < scenePixels.Length; i++)
			{
				byte a = gizmoPixels[i].a;
				if (a < 2) continue;
				float t = a / 255f;
				float inv = 1f - t;
				scenePixels[i].r = (byte)(scenePixels[i].r * inv + gizmoPixels[i].r * t);
				scenePixels[i].g = (byte)(scenePixels[i].g * inv + gizmoPixels[i].g * t);
				scenePixels[i].b = (byte)(scenePixels[i].b * inv + gizmoPixels[i].b * t);
			}
			tex.SetPixels32(scenePixels);
			Object.DestroyImmediate(gizmoTex);
		}

		tex.Apply();
		RenderTexture.active = prevActive;
		RenderTexture.ReleaseTemporary(sceneRT);

		if (SceneViewVertexCounter.IsDrawing)
			SceneViewVertexCounter.DrawToCapture(sceneView, tex);

		// Commit
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
		// Baseline for CheckViewportSize is taken on the next Scene GUI, after the toolbar has laid out the new strip button.
		_lastViewportSize = Vector2.zero;

		_screenshotStrip?.Rebuild();
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
		if (_lastViewportSize == Vector2.zero)
		{
			_lastViewportSize = currentSize;
			return;
		}
		if (_lastViewportSize != currentSize)
			ClearAllScreenshots();
	}

	// ---- Scene GUI: block navigation while viewing ----

	static void OnSceneGUI(SceneView sceneView)
	{
		if (!s_instances.TryGetValue(sceneView, out var overlay)) return;

		// Capture during Repaint so the gizmo RT has fresh content from this frame.
		if (overlay._pendingCapture && Event.current.type == EventType.Repaint)
		{
			overlay._pendingCapture = false;
			overlay.CaptureSceneView(sceneView);
		}

		overlay.CheckViewportSize(sceneView);

		if (overlay.displayed)
			SceneViewVertexCounter.OnSceneGUI(sceneView);

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

}

// ---- Toolbar elements ----

/// <summary>
/// Dynamic strip of numbered screenshot buttons. Empty when no screenshots exist.
/// </summary>
class EditoolsScreenshotStrip : VisualElement
{
	readonly EditoolsOverlay _overlay;
	readonly List<Button> _buttons = new();

	public EditoolsScreenshotStrip(EditoolsOverlay overlay)
	{
		_overlay = overlay;
		style.flexDirection = FlexDirection.Row;
		style.alignItems = Align.Center;
		AddToClassList(EditoolsOverlay.k_OwnContextMenuClass);
		Rebuild();
	}

	public void Rebuild()
	{
		Clear();
		_buttons.Clear();

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
			btn.style.marginTop = 0;
			btn.style.marginBottom = 0;

			btn.AddManipulator(new ContextualMenuManipulator(evt =>
			{
				evt.menu.AppendAction("Edit Comment...", _ => _overlay.ShowCommentPopup(idx, btn));
				evt.menu.AppendAction("Delete", _ => _overlay.DeleteScreenshot(idx));
				evt.menu.AppendSeparator("");
				evt.menu.AppendAction("Delete All", _ => _overlay.ClearAllScreenshots());
			}));

			_buttons.Add(btn);
			Add(btn);
		}

		UpdateStyles();
	}

	public void UpdateStyles()
	{
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
/// Capture button - takes a screenshot of the current scene view.
/// </summary>
class EditoolsCaptureButton : EditorToolbarButton
{
	public EditoolsCaptureButton(EditoolsOverlay overlay)
	{
		icon = EditoolsIcons.Builtin("d_SceneViewCamera");
		tooltip = "Capture current scene view";
		clicked += overlay.OnCaptureClicked;
	}
}

/// <summary>
/// Material Check dropdown - shows a small popup to assign and toggle
/// a material override on all opaque scene renderers.
/// </summary>
class EditoolsMaterialCheckButton : EditorToolbarDropdown
{
	public EditoolsMaterialCheckButton()
	{
		icon = EditoolsIcons.Builtin("d_Material Icon");
		tooltip = "Material Check - drag a material here to assign it";
		clicked += () => UnityEditor.PopupWindow.Show(worldBound, new MaterialCheckPopup());

		RegisterCallback<DragEnterEvent>(_ => AcceptMaterialDrag());
		RegisterCallback<DragUpdatedEvent>(_ => AcceptMaterialDrag());
		RegisterCallback<DragPerformEvent>(_ =>
		{
			if (DragAndDrop.objectReferences.Length == 1 && DragAndDrop.objectReferences[0] is Material mat)
			{
				DragAndDrop.AcceptDrag();
				MaterialCheck.Material = mat;
			}
		});
	}

	static void AcceptMaterialDrag()
	{
		if (DragAndDrop.objectReferences.Length == 1 && DragAndDrop.objectReferences[0] is Material)
			DragAndDrop.visualMode = DragAndDropVisualMode.Link;
	}
}

/// <summary>
/// Select By Material — toolbar toggle for the <see cref="SelectByMaterialOverlay"/> panel.
/// Drag materials, shaders or objects onto the button to add them to the panel's lists
/// (or set the Replacement material) via a context menu.
/// </summary>
class EditoolsSelectByMaterialButton : EditorToolbarToggle
{

	public EditoolsSelectByMaterialButton()
	{
		icon = EditorGUIUtility.IconContent("d_FilterByType").image as Texture2D;
		tooltip = "Select And Replace Material\n\n" +
			"Toggle the panel. Drag materials, shaders or objects onto this\n" +
			"button to add them to the Materials / Shaders lists.";

		SetValueWithoutNotify(SelectByMaterial.Shown);
		this.RegisterValueChangedCallback(evt => SelectByMaterial.Shown = evt.newValue);

		RegisterCallback<AttachToPanelEvent>(_ =>
		{
			SelectByMaterial.ShownChanged += OnShownChanged;
			SetValueWithoutNotify(SelectByMaterial.Shown);
		});
		RegisterCallback<DetachFromPanelEvent>(_ => SelectByMaterial.ShownChanged -= OnShownChanged);

		RegisterCallback<DragEnterEvent>(_ => AcceptDrag());
		RegisterCallback<DragUpdatedEvent>(_ => AcceptDrag());
		RegisterCallback<DragPerformEvent>(_ =>
		{
			if (!DragIsAcceptable()) return;
			DragAndDrop.AcceptDrag();
			ShowDropMenu(DragAndDrop.objectReferences);
		});
	}

	void OnShownChanged() => SetValueWithoutNotify(SelectByMaterial.Shown);

	static void AcceptDrag()
	{
		if (DragIsAcceptable())
			DragAndDrop.visualMode = DragAndDropVisualMode.Link;
	}

	static bool DragIsAcceptable()
	{
		foreach (var o in DragAndDrop.objectReferences)
			if (o is Material || o is Shader || o is GameObject) return true;
		return false;
	}

	static void ShowDropMenu(Object[] dropped)
	{
		var menu = new GenericMenu();
		menu.AddItem(new GUIContent("Add To Materials"), false, () =>
		{
			SelectByMaterial.AddMaterialsFrom(dropped);
			SelectByMaterial.Shown = true;
		});
		menu.AddItem(new GUIContent("Add To Shaders"), false, () =>
		{
			SelectByMaterial.AddShadersFrom(dropped);
			SelectByMaterial.Shown = true;
		});
		if (dropped.Length == 1 && dropped[0] is Material single)
		{
			menu.AddSeparator("");
			menu.AddItem(new GUIContent("Set As Replacement Material"), false, () =>
			{
				SelectByMaterial.ReplacementMaterial = single;
				SelectByMaterial.Save();
				SelectByMaterial.Shown = true;
			});
		}
		menu.ShowAsContext();
	}
}


/// <summary>
/// Settings dropdown at the end of the toolbar. Opens <see cref="EditoolsSettingsMenuPopup"/>.
/// </summary>
class EditoolsSettingsButton : EditorToolbarDropdown
{
	public EditoolsSettingsButton()
	{
		icon = EditoolsIcons.Builtin("d_Settings");
		tooltip = "Editools: functions not shown as toolbar buttons, and Settings.";
		clicked += () => UnityEditor.PopupWindow.Show(worldBound, new EditoolsSettingsMenuPopup());
	}
}

/// <summary>
/// Menu behind the settings button: one row per enabled function that supports a toolbar button
/// but is not selected for the overlay (clicking runs it; toggle functions show their on/off state),
/// then a bold Settings row that opens
/// <see cref="EditoolsSettings"/> in the Inspector.
/// </summary>
class EditoolsSettingsMenuPopup : PopupWindowContent
{
	const float k_RowHeight = 22f;
	const float k_Width = 230f;

	static GUIStyle s_rowStyle;
	static GUIStyle s_settingsRowStyle;

	readonly List<EditoolsFunction> _rows = new List<EditoolsFunction>(16);

	public EditoolsSettingsMenuPopup()
	{
		foreach (var function in EditoolsFunctions.All)
			if (function.ShowsInSettingsMenu)
				_rows.Add(function);
	}

	static void EnsureStyles()
	{
		if (s_rowStyle != null) return;
		s_rowStyle = new GUIStyle(EditorStyles.label)
		{
			alignment = TextAnchor.MiddleLeft,
			padding = new RectOffset(4, 4, 0, 0),
			margin = new RectOffset(0, 0, 0, 0)
		};
		s_settingsRowStyle = new GUIStyle(s_rowStyle) { fontStyle = FontStyle.Bold };
	}

	public override Vector2 GetWindowSize()
	{
		float separator = _rows.Count > 0 ? 6f : 0f;
		return new Vector2(k_Width, (_rows.Count + 1) * k_RowHeight + separator + 4f);
	}

	public override void OnGUI(Rect rect)
	{
		EnsureStyles();

		float y = 2f;
		foreach (var function in _rows)
		{
			var rowRect = new Rect(2f, y, rect.width - 4f, k_RowHeight);
			DrawRow(rowRect, function);
			y += k_RowHeight;
		}

		if (_rows.Count > 0)
		{
			EditorGUI.DrawRect(new Rect(6f, y + 2f, rect.width - 12f, 1f), new Color(1f, 1f, 1f, 0.15f));
			y += 6f;
		}

		var settingsRect = new Rect(2f, y, rect.width - 4f, k_RowHeight);
		if (GUI.Button(settingsRect, new GUIContent("Settings", "Open the Editools settings in the Inspector."), s_settingsRowStyle))
		{
			EditoolsSettings.OpenInInspector();
			editorWindow.Close();
		}
	}

	void DrawRow(Rect rowRect, EditoolsFunction function)
	{
		var iconRect = new Rect(rowRect.x + 4f, rowRect.y + 3f, 16f, 16f);
		var icon = function.Icon?.Invoke();
		if (icon != null)
			GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);

		var labelRect = new Rect(iconRect.xMax + 2f, rowRect.y, rowRect.width - 26f, rowRect.height);

		if (function.IsToggle)
		{
			var toggleRect = new Rect(rowRect.xMax - 20f, rowRect.y, 18f, rowRect.height);
			labelRect.width -= 20f;
			bool active = function.GetActive();
			bool newActive = GUI.Toggle(toggleRect, active, GUIContent.none);
			if (GUI.Button(labelRect, new GUIContent(function.Name, function.Tooltip), s_rowStyle))
				newActive = !active;
			if (newActive != active)
			{
				function.SetActive(newActive);
				editorWindow.Repaint();
			}
			return;
		}

		if (GUI.Button(labelRect, new GUIContent(function.Name, function.Tooltip), s_rowStyle))
		{
			// Sub-popups open to the right of this menu: (win.width, y) in GUI space lands at the
			// menu's right edge in screen space.
			Rect win = editorWindow != null ? editorWindow.position : new Rect(200, 300, k_Width, 200);
			function.Activate(new Rect(win.width, rowRect.y, 0f, 0f));
			if (function.Id != "material-check")
				editorWindow.Close();
		}
	}
}

/// <summary>
/// Material Check: previews one material on every opaque scene renderer through
/// <see cref="SceneMaterialOverride"/>. The material lives in <see cref="EditoolsSettings"/>;
/// the on/off state is session-scoped so it survives a domain reload.
/// </summary>
static class MaterialCheck
{
	const string k_ModeName = "MaterialCheck";
	const string k_IsOnSessionKey = "AV_MaterialCheck_IsOn";

	static bool s_isOn;

	internal static bool Enabled => EditoolsSettings.Current.materialCheckEnabled;

	/// <summary>True while the override is active. Another mode force-exiting us clears it.</summary>
	internal static bool IsOn
	{
		get
		{
			if (s_isOn && !SceneMaterialOverride.IsModeActive(k_ModeName)) s_isOn = false;
			return s_isOn;
		}
	}

	internal static Material Material
	{
		get => EditoolsSettings.Current.materialCheckMaterial;
		set
		{
			var settings = EditoolsSettings.Current;
			if (settings.materialCheckMaterial == value) return;
			Undo.RecordObject(settings, "Set Material Check Material");
			settings.materialCheckMaterial = value;
			settings.SaveAndNotify();
		}
	}

	internal static void TurnOn()
	{
		var mat = Material;
		if (mat == null)
		{
			Debug.LogError("[Editools] Material Check: assign a material before turning it on.");
			return;
		}
		SceneMaterialOverride.Enter(mat, k_ModeName, () => s_isOn = false);
		s_isOn = true;
		SessionState.SetBool(k_IsOnSessionKey, true);
	}

	internal static void TurnOff()
	{
		if (!IsOn) return;
		SceneMaterialOverride.Exit();
		s_isOn = false;
		SessionState.SetBool(k_IsOnSessionKey, false);
	}

	internal static void ApplyPermanently()
	{
		if (!IsOn) return;
		SceneMaterialOverride.ApplyPermanently();
		s_isOn = false;
		SessionState.SetBool(k_IsOnSessionKey, false);
	}

	/// <summary>Re-enters the mode after a domain reload when it was on before it. Called from the overlay.</summary>
	internal static void RestoreAfterReload()
	{
		if (!SessionState.GetBool(k_IsOnSessionKey, false)) return;
		if (!Enabled || Material == null || SceneMaterialOverride.IsModeActive(k_ModeName)) return;
		EditorApplication.delayCall += TurnOn;
	}

	/// <summary>Leaves the mode when the function is turned off.</summary>
	internal static void Sync()
	{
		if (!Enabled && IsOn) TurnOff();
	}
}

/// <summary>
/// Popup behind the Material Check toolbar dropdown: material slot and On / Off / Apply.
/// </summary>
class MaterialCheckPopup : PopupWindowContent
{
	public override Vector2 GetWindowSize() => new Vector2(220, 80);

	public override void OnGUI(Rect rect)
	{
		var current = MaterialCheck.Material;
		var next = (Material)EditorGUILayout.ObjectField(current, typeof(Material), false);
		if (next != current)
			MaterialCheck.Material = next;

		bool isOn = MaterialCheck.IsOn;

		EditorGUILayout.BeginHorizontal();
		using (new EditorGUI.DisabledScope(current == null || isOn))
		{
			if (GUILayout.Button("On"))
				MaterialCheck.TurnOn();
		}
		using (new EditorGUI.DisabledScope(!isOn))
		{
			if (GUILayout.Button("Off"))
				MaterialCheck.TurnOff();
		}
		EditorGUILayout.EndHorizontal();

		using (new EditorGUI.DisabledScope(!isOn))
		{
			if (GUILayout.Button("Apply"))
				MaterialCheck.ApplyPermanently();
		}
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
