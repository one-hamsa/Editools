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

	/// <summary>
	/// True when at least one Editools overlay is displayed in a SceneView.
	/// Subsystems should check this before doing any work.
	/// </summary>
	internal static bool IsActive
	{
		get
		{
			foreach (var kvp in s_instances)
				if (kvp.Value.displayed) return true;
			return false;
		}
	}

	readonly List<ViewCompScreenshot> _screenshots = new();

	int _activeIndex = -1;
	Vector2 _lastViewportSize;

	VisualElement _displayRoot;
	IMGUIContainer _displayIMGUI;
	bool _pendingCapture;

	// Screenshot strip element reference (for rebuilding dynamic buttons)
	EditoolsScreenshotStrip _screenshotStrip;

	EditoolsOverlay() : base(
		EditoolsScreenshotStrip.k_Id,
		EditoolsCaptureButton.k_Id,
		EditoolsMaterialCheckButton.k_Id,
		EditoolsSelectByMaterialButton.k_Id,
		EditoolsSettingsButton.k_Id
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

		displayedChanged += OnDisplayedChanged;
		AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
		RestoreScreenshots();
		MaterialCheckPopup.RestoreAfterReload();
	}

	void OnDisplayedChanged(bool visible)
	{
		// When the overlay is hidden/shown, repaint hierarchy & project windows
		// so heatmap drawing responds immediately
		EditorApplication.RepaintHierarchyWindow();
		EditorApplication.RepaintProjectWindow();
	}

	public override void OnWillBeDestroyed()
	{
		displayedChanged -= OnDisplayedChanged;
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
		_lastViewportSize = sceneView.cameraViewport.size;

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
		if (_lastViewportSize != Vector2.zero && _lastViewportSize != currentSize)
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
/// Select By Material — toolbar dropdown that opens a popup for selecting or
/// replacing renderers by material. Drag a material onto the button to assign
/// it as Selection Material or Replacement Material via a context menu.
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsSelectByMaterialButton : EditorToolbarDropdown
{
	public const string k_Id = "Editools/SelectByMaterial";

	public EditoolsSelectByMaterialButton()
	{
		icon = EditorGUIUtility.IconContent("d_FilterByType").image as Texture2D;
		tooltip = "Select And Replace Material\n\n" +
			"Click to open settings. Drag a material onto this button\n" +
			"to assign it as the Selection or Replacement material.";
		clicked += () => UnityEditor.PopupWindow.Show(worldBound, new SelectByMaterialPopup());

		RegisterCallback<DragEnterEvent>(_ => AcceptMaterialDrag());
		RegisterCallback<DragUpdatedEvent>(_ => AcceptMaterialDrag());
		RegisterCallback<DragPerformEvent>(_ =>
		{
			if (GetDraggedMaterial() is Material mat)
			{
				DragAndDrop.AcceptDrag();
				ShowSlotMenu(mat);
			}
		});
	}

	static void AcceptMaterialDrag()
	{
		if (GetDraggedMaterial() != null)
			DragAndDrop.visualMode = DragAndDropVisualMode.Link;
	}

	static Material GetDraggedMaterial()
	{
		if (DragAndDrop.objectReferences.Length == 1 && DragAndDrop.objectReferences[0] is Material mat)
			return mat;
		return null;
	}

	static void ShowSlotMenu(Material mat)
	{
		var menu = new GenericMenu();
		menu.AddItem(new GUIContent("Selection Material"), false, () => SelectByMaterialPopup.SetSelectionMaterial(mat));
		menu.AddItem(new GUIContent("Replacement Material"), false, () => SelectByMaterialPopup.SetReplacementMaterial(mat));
		menu.ShowAsContext();
	}
}

/// <summary>
/// Popup for Select And Replace Material. Persists state in static fields
/// across popup opens, and material GUIDs in EditorPrefs across sessions.
/// </summary>
class SelectByMaterialPopup : PopupWindowContent
{
	const string k_Pref = "Editools_SelectByMat_";
	const string k_SelectionGuid  = k_Pref + "SelectionGUID";
	const string k_ReplacementGuid = k_Pref + "ReplacementGUID";
	const string k_MeshRenderer   = k_Pref + "MeshRenderer";
	const string k_SkinnedMesh    = k_Pref + "SkinnedMesh";
	const string k_SpriteRenderer = k_Pref + "SpriteRenderer";
	const string k_OnlyActive     = k_Pref + "OnlyActive";

	static Material s_selectionMat;
	static Material s_replacementMat;
	static bool s_meshRenderer = true;
	static bool s_skinnedMesh  = true;
	static bool s_spriteRenderer = true;
	static bool s_onlyActive;
	static bool s_loaded;

	public SelectByMaterialPopup()
	{
		EnsureLoaded();
	}

	static void EnsureLoaded()
	{
		if (s_loaded) return;
		s_loaded = true;

		s_selectionMat  = LoadMaterialPref(k_SelectionGuid);
		s_replacementMat = LoadMaterialPref(k_ReplacementGuid);
		s_meshRenderer   = EditorPrefs.GetBool(k_MeshRenderer, true);
		s_skinnedMesh    = EditorPrefs.GetBool(k_SkinnedMesh, true);
		s_spriteRenderer = EditorPrefs.GetBool(k_SpriteRenderer, true);
		s_onlyActive     = EditorPrefs.GetBool(k_OnlyActive, false);
	}

	static Material LoadMaterialPref(string key)
	{
		string guid = EditorPrefs.GetString(key, "");
		if (string.IsNullOrEmpty(guid)) return null;
		string path = AssetDatabase.GUIDToAssetPath(guid);
		if (string.IsNullOrEmpty(path)) return null;
		return AssetDatabase.LoadAssetAtPath<Material>(path);
	}

	static void SaveMaterialPref(string key, Material mat)
	{
		if (mat != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out string guid, out long _))
			EditorPrefs.SetString(key, guid);
		else
			EditorPrefs.DeleteKey(key);
	}

	internal static void SetSelectionMaterial(Material mat)
	{
		EnsureLoaded();
		s_selectionMat = mat;
		SaveMaterialPref(k_SelectionGuid, mat);
	}

	internal static void SetReplacementMaterial(Material mat)
	{
		EnsureLoaded();
		s_replacementMat = mat;
		SaveMaterialPref(k_ReplacementGuid, mat);
	}

	public override Vector2 GetWindowSize() => new Vector2(260, 244);

	public override void OnGUI(Rect rect)
	{
		// ── Selection Material ──
		EditorGUILayout.LabelField("Selection Material", EditorStyles.boldLabel);
		var newSel = (Material)EditorGUILayout.ObjectField(s_selectionMat, typeof(Material), false);
		if (newSel != s_selectionMat)
		{
			s_selectionMat = newSel;
			SaveMaterialPref(k_SelectionGuid, s_selectionMat);
		}

		// ── Renderer type toggles ──
		EditorGUILayout.Space(2);
		bool newMR = EditorGUILayout.Toggle("MeshRenderer", s_meshRenderer);
		if (newMR != s_meshRenderer) { s_meshRenderer = newMR; EditorPrefs.SetBool(k_MeshRenderer, newMR); }

		bool newSMR = EditorGUILayout.Toggle("SkinnedMeshRenderer", s_skinnedMesh);
		if (newSMR != s_skinnedMesh) { s_skinnedMesh = newSMR; EditorPrefs.SetBool(k_SkinnedMesh, newSMR); }

		bool newSR = EditorGUILayout.Toggle("SpriteRenderer", s_spriteRenderer);
		if (newSR != s_spriteRenderer) { s_spriteRenderer = newSR; EditorPrefs.SetBool(k_SpriteRenderer, newSR); }

		bool newOA = EditorGUILayout.Toggle("Only Active Objects", s_onlyActive);
		if (newOA != s_onlyActive) { s_onlyActive = newOA; EditorPrefs.SetBool(k_OnlyActive, newOA); }

		// ── Select button ──
		EditorGUILayout.Space(2);
		using (new EditorGUI.DisabledScope(s_selectionMat == null))
		{
			if (GUILayout.Button("Select"))
			{
				DoSelect();
				editorWindow.Close();
			}
		}

		// ── Separator ──
		EditorGUILayout.Space(4);
		var sepRect = EditorGUILayout.GetControlRect(false, 1);
		EditorGUI.DrawRect(sepRect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
		EditorGUILayout.Space(2);

		// ── Replacement Material ──
		EditorGUILayout.LabelField("Replacement Material", EditorStyles.boldLabel);
		var newRep = (Material)EditorGUILayout.ObjectField(s_replacementMat, typeof(Material), false);
		if (newRep != s_replacementMat)
		{
			s_replacementMat = newRep;
			SaveMaterialPref(k_ReplacementGuid, s_replacementMat);
		}

		// ── Replace button ──
		using (new EditorGUI.DisabledScope(s_selectionMat == null || s_replacementMat == null))
		{
			if (GUILayout.Button("Replace"))
			{
				DoReplace();
				editorWindow.Close();
			}
		}
	}

	// ── Shared: collect matching renderers ──

	static List<Renderer> CollectMatchingRenderers(Material mat)
	{
		var results = new List<Renderer>();
		var findMode = s_onlyActive ? FindObjectsInactive.Exclude : FindObjectsInactive.Include;

		if (s_meshRenderer)
			foreach (var r in Object.FindObjectsByType<MeshRenderer>(findMode, FindObjectsSortMode.None))
				if (HasMaterial(r, mat)) results.Add(r);

		if (s_skinnedMesh)
			foreach (var r in Object.FindObjectsByType<SkinnedMeshRenderer>(findMode, FindObjectsSortMode.None))
				if (HasMaterial(r, mat)) results.Add(r);

		if (s_spriteRenderer)
			foreach (var r in Object.FindObjectsByType<SpriteRenderer>(findMode, FindObjectsSortMode.None))
				if (HasMaterial(r, mat)) results.Add(r);

		return results;
	}

	static bool HasMaterial(Renderer r, Material mat)
	{
		var mats = r.sharedMaterials;
		for (int i = 0; i < mats.Length; i++)
			if (mats[i] == mat) return true;
		return false;
	}

	// ── Select ──

	static void DoSelect()
	{
		var renderers = CollectMatchingRenderers(s_selectionMat);
		var objects = new Object[renderers.Count];
		for (int i = 0; i < renderers.Count; i++)
			objects[i] = renderers[i].gameObject;
		Selection.objects = objects;
		Debug.Log($"[Editools] Select By Material: {renderers.Count} object(s) using '{s_selectionMat.name}'");
	}

	// ── Replace ──

	static void DoReplace()
	{
		var renderers = CollectMatchingRenderers(s_selectionMat);
		int replaced = 0;

		foreach (var r in renderers)
		{
			Undo.RegisterCompleteObjectUndo(r, "Replace Material");
			var mats = r.sharedMaterials;
			bool changed = false;
			for (int i = 0; i < mats.Length; i++)
			{
				if (mats[i] == s_selectionMat)
				{
					mats[i] = s_replacementMat;
					changed = true;
				}
			}
			if (changed)
			{
				r.sharedMaterials = mats;
				replaced++;
			}
		}

		Debug.Log($"[Editools] Replace Material: replaced '{s_selectionMat.name}' → '{s_replacementMat.name}' on {replaced} renderer(s)");
	}
}

/// <summary>
/// Settings dropdown — opens the central Editools settings popup where all
/// features can be toggled on/off and their sub-settings accessed.
/// </summary>
[EditorToolbarElement(k_Id, typeof(SceneView))]
class EditoolsSettingsButton : EditorToolbarDropdown
{
	public const string k_Id = "Editools/Settings";

	public EditoolsSettingsButton()
	{
		icon = EditorGUIUtility.IconContent("d_Settings").image as Texture2D;
		tooltip = "Editools Settings";
		clicked += () => UnityEditor.PopupWindow.Show(worldBound, new EditoolsSettingsPopup());
	}
}

/// <summary>
/// Central settings popup — lists all Editools features with checkboxes to
/// toggle them on/off and arrows to open sub-settings where applicable.
/// </summary>
class EditoolsSettingsPopup : PopupWindowContent
{
	static GUIStyle s_rowButtonStyle;
	static GUIStyle s_arrowStyle;

	static void EnsureStyles()
	{
		if (s_rowButtonStyle != null) return;
		s_rowButtonStyle = new GUIStyle(EditorStyles.label)
		{
			alignment = TextAnchor.MiddleLeft,
			padding = new RectOffset(0, 0, 0, 0),
			margin = new RectOffset(0, 0, 0, 0)
		};
		s_arrowStyle = new GUIStyle(EditorStyles.label)
		{
			alignment = TextAnchor.MiddleCenter,
			fontStyle = FontStyle.Normal,
			padding = new RectOffset(0, 0, 0, 0)
		};
	}

	public override Vector2 GetWindowSize() => new Vector2(200, 4 * 22 + 4);

	public override void OnGUI(Rect rect)
	{
		EnsureStyles();

		// Scene View Undo — simple toggle, no submenu
		DrawToggleRow("Scene View Undo", SceneCameraUndo.Enabled,
			v => SceneCameraUndo.Enabled = v, null);

		// Hierarchy Heatmap — toggle + submenu
		DrawToggleRow("Hierarchy Heatmap",
			EditorPrefs.GetBool("HierarchyHeatmapEnabled", false),
			v => HierarchyHeatmap.SetEnabled(v),
			() => ShowHeatmapMenu());

		// QuickTransform — toggle + submenu
		DrawToggleRow("QuickTransform", QuickTransform.Enabled,
			v => QuickTransform.Enabled = v,
			() => UnityEditor.PopupWindow.Show(
				GUILayoutUtility.GetLastRect(), new QuickTransformPopup()));

		// Quick Access — toggle window on/off
		DrawToggleRow("Quick Access", QuickAccess.IsOpen,
			_ => QuickAccess.ToggleWindow(), null);
	}

	void DrawToggleRow(string label, bool isOn, System.Action<bool> onToggle,
		System.Action onSubmenu)
	{
		EditorGUILayout.BeginHorizontal(GUILayout.Height(20));

		// Checkbox
		bool newValue = EditorGUILayout.Toggle(isOn, GUILayout.Width(16));
		if (newValue != isOn)
			onToggle?.Invoke(newValue);

		// Label — clickable for submenu items, toggles for non-submenu items
		if (onSubmenu != null)
		{
			if (GUILayout.Button(label, s_rowButtonStyle))
				onSubmenu.Invoke();

			// Arrow indicator
			GUILayout.Label("\u25B8", s_arrowStyle, GUILayout.Width(16));
		}
		else
		{
			if (GUILayout.Button(label, s_rowButtonStyle))
				onToggle?.Invoke(!isOn);
		}

		EditorGUILayout.EndHorizontal();
	}

	static void ShowHeatmapMenu()
	{
		var menu = new GenericMenu();
		menu.AddItem(new GUIContent("Settings..."), false,
			() => HeatmapSettingsWindow.ShowWindow());
		menu.AddItem(new GUIContent("Reset Recent"), false,
			() => HierarchyHeatmap.ResetRecent());
		menu.ShowAsContext();
	}
}

/// <summary>
/// Settings popup for QuickTransform. Shows editable fields backed by EditorPrefs.
/// </summary>
class QuickTransformPopup : PopupWindowContent
{
	static readonly GUIContent k_EdgeHoverLabel = new GUIContent(
		"Edge Detection (px)",
		"Pixel distance threshold for detecting edge hover in the Scene View.");
	static readonly GUIContent k_CircleRadLabel = new GUIContent(
		"Rotation Circle Size",
		"Size multiplier for the rotation feedback circle shown during rotation.");
	static readonly GUIContent k_RotSnapLabel = new GUIContent(
		"Rotation Snap (\u00B0)",
		"Angle increment in degrees when holding Ctrl during rotation.");
	static readonly GUIContent k_LinearRotLabel = new GUIContent(
		"Linear Rotation",
		"Use horizontal mouse movement instead of radial motion for rotation control.");
	static readonly GUIContent k_LinearSensLabel = new GUIContent(
		"  Sensitivity (\u00B0/px)",
		"Degrees of rotation per pixel of horizontal mouse movement.");

	public override Vector2 GetWindowSize()
	{
		int rows = QuickTransform.LinearRotation ? 6 : 5;
		return new Vector2(240, rows * 22);
	}

	public override void OnGUI(Rect rect)
	{
		float hover = QuickTransform.EdgeHoverPx;
		float newHover = EditorGUILayout.FloatField(k_EdgeHoverLabel, hover);
		if (!Mathf.Approximately(newHover, hover))
			QuickTransform.EdgeHoverPx = newHover;

		float rad = QuickTransform.CircleRadius;
		float newRad = EditorGUILayout.FloatField(k_CircleRadLabel, rad);
		if (!Mathf.Approximately(newRad, rad))
			QuickTransform.CircleRadius = newRad;

		float snap = QuickTransform.RotSnapAngle;
		float newSnap = EditorGUILayout.FloatField(k_RotSnapLabel, snap);
		if (!Mathf.Approximately(newSnap, snap))
			QuickTransform.RotSnapAngle = newSnap;

		bool linear = QuickTransform.LinearRotation;
		bool newLinear = EditorGUILayout.Toggle(k_LinearRotLabel, linear);
		if (newLinear != linear)
		{
			QuickTransform.LinearRotation = newLinear;
			editorWindow.Repaint(); // resize popup
		}

		if (QuickTransform.LinearRotation)
		{
			float sens = QuickTransform.LinearRotSensitivity;
			float newSens = EditorGUILayout.FloatField(k_LinearSensLabel, sens);
			if (!Mathf.Approximately(newSens, sens))
				QuickTransform.LinearRotSensitivity = newSens;
		}
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

	public override Vector2 GetWindowSize() => new Vector2(220, 80);

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

		using (new EditorGUI.DisabledScope(!s_isOn))
		{
			if (GUILayout.Button("Apply"))
			{
				SceneMaterialOverride.ApplyPermanently();
				s_isOn = false;
				SessionState.SetBool(k_IsOnSessionKey, false);
			}
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
