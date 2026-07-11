#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

/// <summary>
/// A user-curated quick-access panel for frequently used scene objects and project assets.
/// Two sections: Scene (per-scene, local) and Project (per-project, local).
/// Add items by dragging from Hierarchy/Project. Remove via right-click (undoable).
/// Reorder within a section by dragging (swap). Drag out to use like Hierarchy/Project.
///
/// Selection Groups (0-9): QuickAccess owns its own selection groups, stored
/// per-scene alongside the scene's item list in UserSettings/QuickAccess/<sceneGuid>.json.
/// Save via Ctrl+1..0, recall via 1..0. Multi-object items supported.
/// </summary>
public class QuickAccess : EditorWindow
{
	// ─── Serialized State (undo support) ───────────────────────

	[SerializeField] List<string> sceneIds   = new List<string>();
	[SerializeField] List<string> projectIds = new List<string>();

	/// <summary>GUID of the scene that <c>sceneIds</c> belongs to. Travels in the same
	/// <c>Undo.RecordObject</c> snapshot as <c>sceneIds</c>, so an undo that reverts our state
	/// also reverts the owner stamp. After a scene switch, the new scene's data has a different
	/// owner GUID; <c>OnUndoRedo</c> compares this against <c>currentSceneGuid</c> to detect
	/// cross-scene undos and reject them without writing them back to disk.</summary>
	[SerializeField] string sceneIdsOwnerGuid = "";

	/// <summary>Per-scene Scene View camera bookmarks. Each entry stores a camera position +
	/// rotation; recalled by aligning the last active Scene View camera back to it. Numbered
	/// 1..9 by list order; adding past <see cref="k_MaxAnchors"/> drops the oldest (FIFO).
	/// Serialized so add/remove travel in the same undo snapshot as <c>sceneIds</c> — the
	/// cross-scene undo guard in <c>OnUndoRedo</c> covers them too.</summary>
	[SerializeField] List<QuickAccessStore.AnchorEntry> viewAnchors = new List<QuickAccessStore.AnchorEntry>();

	const int k_MaxAnchors = 9;

	// ─── UI ────────────────────────────────────────────────────

	ScrollView    sceneRows, projectRows, anchorRows;
	VisualElement sceneDropZone, projectDropZone;
	Label         sceneEmptyHint, projectEmptyHint, anchorEmptyHint;

	// ─── Drag State ────────────────────────────────────────────

	const string k_ReorderKey    = "QuickAccess_Reorder";
	const float  k_DragThreshold = 4f;

	Object  dragCandidate;
	Vector2 dragStartPos;
	bool    isDragActive;
	VisualElement currentSwapTarget;

	// ─── Selection Groups ──────────────────────────────────────

	/// <summary>Group slot (0-9) → list of QuickAccess item IDs in that group.
	/// A slot can have up to 2 items (scene + project) for mixed selections.</summary>
	Dictionary<int, List<string>> selectionGroups = new Dictionary<int, List<string>>();

	// ─── Resolution Cache ──────────────────────────────────────

	/// <summary>Caches resolved Objects for IDs to avoid repeated GlobalObjectIdentifierToObjectSlow calls.
	/// Invalidated on scene change and when items are added/removed.</summary>
	static readonly Dictionary<string, Object>   s_singleCache = new Dictionary<string, Object>();
	static readonly Dictionary<string, Object[]>  s_multiCache  = new Dictionary<string, Object[]>();

	static void InvalidateCache()
	{
		s_singleCache.Clear();
		s_multiCache.Clear();
	}

	// ─── Static Instance ───────────────────────────────────────

	static QuickAccess s_instance;

	// ─── Storage ───────────────────────────────────────────────
	//
	// Per-user data lives under UserSettings/QuickAccess/ — a folder Unity
	// preserves across cache nukes, never ships in builds, and that the
	// project's .gitignore keeps out of source control.
	//
	// - <sceneGuid>.json — one file per scene: item list + selection groups.
	//                     Keyed by scene-asset GUID so renames/duplicates can't collide.
	// - _project.json   — the project-wide item list.
	//
	// Writes go through QuickAccessStore which writes atomically (tmp + replace),
	// so a crashed editor can never leave a half-written file. Untitled scenes
	// (no asset GUID) keep their list in memory for the session only.

	/// <summary>GUID of the scene whose data is currently loaded into sceneIds/selectionGroups.
	/// Empty string = untitled scene → persistence disabled for this scene.</summary>
	string currentSceneGuid = "";

	/// <summary>Get the asset GUID of a scene, or "" if it's untitled / unsaved.</summary>
	static string GuidForScene(Scene scene)
	{
		if (!scene.IsValid()) return "";
		var path = scene.path;
		if (string.IsNullOrEmpty(path)) return "";
		var guid = AssetDatabase.AssetPathToGUID(path);
		return string.IsNullOrEmpty(guid) ? "" : guid;
	}

	static string GuidForActiveScene() => GuidForScene(SceneManager.GetActiveScene());

	// ─── Window access ─────────────────────────────────────────

	internal static bool IsOpen => Resources.FindObjectsOfTypeAll<QuickAccess>().Length > 0;

	public static QuickAccess ShowWindow()
	{
		var win = GetWindow<QuickAccess>();
		win.titleContent = new GUIContent("Quick Access",
			EditorGUIUtility.IconContent("d_Favorite Icon").image);
		return win;
	}

	internal static void ToggleWindow()
	{
		var existing = Resources.FindObjectsOfTypeAll<QuickAccess>();
		if (existing.Length > 0)
			existing[0].Close();
		else
			ShowWindow();
	}

	// ─── One-time EditorPrefs → Files Migration ───────────────
	//
	// On first load after the storage rewrite, copy any existing data out of
	// EditorPrefs and into the file store, then delete the prefs keys so this
	// only ever runs once. Selection groups were previously global; we attach
	// them to whatever scene is open at migration time and warn in the console.

	[InitializeOnLoadMethod]
	static void MigrateFromEditorPrefsOnce()
	{
		const string kMigrationFlagKey = "QuickAccess_MigratedToFiles_V1";
		if (EditorPrefs.GetBool(kMigrationFlagKey, false)) return;

		try
		{
			// 1. Project list
			var legacyProjectKey = Application.dataPath + "QuickAccess_Project";
			var projectData = EditorPrefs.GetString(legacyProjectKey, "");
			if (!string.IsNullOrEmpty(projectData))
			{
				var items = projectData.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();
				if (items.Count > 0)
				{
					var store = QuickAccessStore.LoadProject();
					store.items = items;
					QuickAccessStore.SaveProject(store);
				}
				EditorPrefs.DeleteKey(legacyProjectKey);
			}

			// 2. Per-scene lists. Keys are stored as
			//    <dataPath>QuickAccess_Scene_<guid-or-name>. We can't enumerate
			//    EditorPrefs, so we migrate lazily on scene open as well — this
			//    just handles the currently-open scene proactively.
			var activeScene = SceneManager.GetActiveScene();
			var activeGuid = GuidForScene(activeScene);
			if (!string.IsNullOrEmpty(activeGuid))
				MigrateSceneFromPrefs(activeScene, activeGuid, alsoMigrateGlobalGroups: true);

			EditorPrefs.SetBool(kMigrationFlagKey, true);
		}
		catch (Exception e)
		{
			Debug.LogWarning($"[QuickAccess] EditorPrefs→file migration failed: {e.Message}");
		}
	}

	/// <summary>
	/// Migrate a single scene's data from EditorPrefs to its sidecar file.
	/// Called from <see cref="MigrateFromEditorPrefsOnce"/> and lazily from
	/// <see cref="ReloadSceneItems"/> for scenes opened after first run.
	/// </summary>
	static void MigrateSceneFromPrefs(Scene scene, string sceneGuid, bool alsoMigrateGlobalGroups)
	{
		var guidKey   = Application.dataPath + "QuickAccess_Scene_" + sceneGuid;
		var nameKey   = !string.IsNullOrEmpty(scene.name)
			? Application.dataPath + "QuickAccess_Scene_" + scene.name
			: null;

		string raw = EditorPrefs.GetString(guidKey, "");
		string usedKey = guidKey;
		if (string.IsNullOrEmpty(raw) && nameKey != null && EditorPrefs.HasKey(nameKey))
		{
			raw = EditorPrefs.GetString(nameKey, "");
			usedKey = nameKey;
		}

		var data = QuickAccessStore.LoadScene(sceneGuid);
		bool changed = false;

		if (!string.IsNullOrEmpty(raw))
		{
			var items = raw.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();
			if (items.Count > 0 && data.items.Count == 0)
			{
				data.items = items;
				changed = true;
			}
			EditorPrefs.DeleteKey(usedKey);
			if (nameKey != null && nameKey != usedKey) EditorPrefs.DeleteKey(nameKey);
		}

		if (alsoMigrateGlobalGroups)
		{
			var groupsKey = Application.dataPath + "QuickAccess_SelectionGroups_V2";
			var groupsRaw = EditorPrefs.GetString(groupsKey, "");
			if (!string.IsNullOrEmpty(groupsRaw))
			{
				var parsed = new Dictionary<int, List<string>>();
				ParseLegacySelectionGroups(groupsRaw, parsed);
				foreach (var kvp in parsed)
					data.SetGroup(kvp.Key, kvp.Value);
				if (parsed.Count > 0)
				{
					changed = true;
					Debug.Log($"[QuickAccess] Migrated {parsed.Count} selection group(s) to scene '{scene.name}'.");
				}
				EditorPrefs.DeleteKey(groupsKey);
			}
			// Also clean up the V1 key if it somehow still exists
			EditorPrefs.DeleteKey(Application.dataPath + "QuickAccess_SelectionGroups");
		}

		if (changed) QuickAccessStore.SaveScene(sceneGuid, data);
	}

	static void ParseLegacySelectionGroups(string data, Dictionary<int, List<string>> target)
	{
		foreach (var entry in data.Split(';'))
		{
			if (string.IsNullOrEmpty(entry)) continue;
			int eq = entry.IndexOf('=');
			if (eq <= 0) continue;
			if (int.TryParse(entry.Substring(0, eq), out int slot) && slot >= 0 && slot <= 9)
			{
				var itemIds = entry.Substring(eq + 1).Split('\t')
					.Where(s => !string.IsNullOrEmpty(s)).ToList();
				if (itemIds.Count > 0)
					target[slot] = itemIds;
			}
		}
	}

	// ─── Lifecycle ─────────────────────────────────────────────

	void OnEnable()
	{
		s_instance = this;

		rootVisualElement.styleSheets.Add(Resources.Load<StyleSheet>("QuickAccess"));
		rootVisualElement.Clear();

		BuildLayout();
		LoadFromPrefs();          // loads data AND builds scene rows + anchors + badges (via ReloadSceneItems)
		RebuildRows(isScene: false); // project rows are the only list LoadFromPrefs didn't build — avoids a full second rebuild

		Undo.undoRedoPerformed += OnUndoRedo;
		Selection.selectionChanged += OnSelectionChanged;
		EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
		EditorSceneManager.sceneOpened += OnSceneOpened;
	}

	void OnDisable()
	{
		// Flush before tear-down — catches data that lived only in memory
		// across a domain reload or window close. Synchronous: delayCall
		// would not fire after the window is gone.
		try { SaveToPrefsImmediate(); } catch (Exception e) { Debug.LogWarning($"[QuickAccess] Save on disable failed: {e.Message}"); }

		if (s_instance == this)
			s_instance = null;

		Undo.undoRedoPerformed -= OnUndoRedo;
		Selection.selectionChanged -= OnSelectionChanged;
		EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
		EditorSceneManager.sceneOpened -= OnSceneOpened;
	}

	void OnUndoRedo()
	{
		// Cross-scene undo guard: Unity's undo system is global, but sceneIds is scoped per-scene.
		// An undo can land us on a snapshot taken under the previous active scene — the reverted
		// sceneIds belong to that scene, not this one. sceneIdsOwnerGuid was captured into the
		// same snapshot, so a mismatch with currentSceneGuid means "this revert is for a different
		// scene's data." Drop it; reload current scene's data from disk so we don't persist the
		// reverted values under the wrong GUID.
		if (sceneIdsOwnerGuid != currentSceneGuid)
		{
			ReloadSceneItems(SceneManager.GetActiveScene());
			return;
		}
		RebuildAllRows();
		SaveToPrefs();
	}

	// ─── Layout ────────────────────────────────────────────────

	void BuildLayout()
	{
		// View Anchors header — title with an inline "add" button on the same line
		var anchorHeader = new VisualElement();
		anchorHeader.AddToClassList("section-header");
		anchorHeader.AddToClassList("header-with-button");

		var anchorTitle = new Label("View Anchors");
		anchorTitle.style.flexGrow = 1;
		anchorHeader.Add(anchorTitle);

		var addAnchorButton = new Button(AddCurrentViewAsAnchor) { text = "+" };
		addAnchorButton.tooltip = "Capture the current Scene View camera as a numbered anchor";
		addAnchorButton.AddToClassList("add-button");
		anchorHeader.Add(addAnchorButton);

		rootVisualElement.Add(anchorHeader);

		var anchorContainer = new VisualElement();
		anchorContainer.AddToClassList("section");

		anchorRows = new ScrollView();
		anchorContainer.Add(anchorRows);

		anchorEmptyHint = new Label("Click + to capture the Scene View");
		anchorEmptyHint.AddToClassList("empty-hint");
		anchorContainer.Add(anchorEmptyHint);

		rootVisualElement.Add(anchorContainer);

		var anchorSep = new VisualElement();
		anchorSep.AddToClassList("separator");
		rootVisualElement.Add(anchorSep);

		// Scene header
		var sceneHeader = new Label("Scene");
		sceneHeader.AddToClassList("section-header");
		rootVisualElement.Add(sceneHeader);

		var sceneContainer = new VisualElement();
		sceneContainer.AddToClassList("section");

		sceneRows = new ScrollView();
		sceneContainer.Add(sceneRows);

		sceneEmptyHint = new Label("Drag scene objects here");
		sceneEmptyHint.AddToClassList("empty-hint");
		sceneContainer.Add(sceneEmptyHint);

		sceneDropZone = new VisualElement();
		sceneDropZone.AddToClassList("drop-zone");
		sceneContainer.Add(sceneDropZone);

		RegisterSectionDragHandlers(sceneContainer, isScene: true);
		rootVisualElement.Add(sceneContainer);

		// Separator
		var sep = new VisualElement();
		sep.AddToClassList("separator");
		rootVisualElement.Add(sep);

		// Project header
		var projectHeader = new Label("Project");
		projectHeader.AddToClassList("section-header");
		rootVisualElement.Add(projectHeader);

		var projectContainer = new VisualElement();
		projectContainer.AddToClassList("section");
		projectContainer.style.flexGrow = 1;    // Fill remaining window space

		projectRows = new ScrollView();
		projectContainer.Add(projectRows);

		projectEmptyHint = new Label("Drag project assets here");
		projectEmptyHint.AddToClassList("empty-hint");
		projectContainer.Add(projectEmptyHint);

		projectDropZone = new VisualElement();
		projectDropZone.AddToClassList("drop-zone");
		projectContainer.Add(projectDropZone);

		RegisterSectionDragHandlers(projectContainer, isScene: false);
		rootVisualElement.Add(projectContainer);

		// Root-level handler catches external drops anywhere in the window
		RegisterRootDragHandlers();
	}

	// ─── Drag Handlers ─────────────────────────────────────────
	//
	// Two layers:
	//  1. Section handlers — only handle internal reorder (tagged drags).
	//     StopPropagation prevents the root handler from also firing.
	//  2. Root handler — catches everything else (external drops from
	//     Hierarchy/Project). Auto-classifies objects into the right section.

	void RegisterSectionDragHandlers(VisualElement section, bool isScene)
	{
		section.RegisterCallback<DragUpdatedEvent>(evt =>
		{
			var reorderInfo = DragAndDrop.GetGenericData(k_ReorderKey) as string;
			if (!IsReorderForSection(reorderInfo, isScene)) return;

			DragAndDrop.visualMode = DragAndDropVisualMode.Move;
			rootVisualElement.RemoveFromClassList("external-drag");
			var rows = isScene ? sceneRows : projectRows;
			UpdateSwapTarget(evt.mousePosition, rows);
			evt.StopPropagation();
		});

		section.RegisterCallback<DragPerformEvent>(evt =>
		{
			var reorderInfo = DragAndDrop.GetGenericData(k_ReorderKey) as string;
			if (!IsReorderForSection(reorderInfo, isScene)) return;

			DragAndDrop.AcceptDrag();
			PerformSwap(reorderInfo, isScene);
			ClearSwapTarget();
			isDragActive = false;
			evt.StopPropagation();
		});

		section.RegisterCallback<DragLeaveEvent>(evt =>
		{
			var reorderInfo = DragAndDrop.GetGenericData(k_ReorderKey) as string;
			if (!IsReorderForSection(reorderInfo, isScene)) return;
			ClearSwapTarget();
			evt.StopPropagation();
		});

		section.RegisterCallback<DragExitedEvent>(evt =>
		{
			var reorderInfo = DragAndDrop.GetGenericData(k_ReorderKey) as string;
			if (!IsReorderForSection(reorderInfo, isScene)) return;
			ClearSwapTarget();
			isDragActive = false;
			evt.StopPropagation();
		});
	}

	void RegisterRootDragHandlers()
	{
		rootVisualElement.RegisterCallback<DragUpdatedEvent>(_ =>
		{
			if (HasExternalDropCandidates())
			{
				DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
				rootVisualElement.AddToClassList("external-drag");
			}
			else
			{
				DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
			}
			ClearSwapTarget();
		});

		rootVisualElement.RegisterCallback<DragPerformEvent>(_ =>
		{
			DragAndDrop.AcceptDrag();
			PerformExternalAdd();
			rootVisualElement.RemoveFromClassList("external-drag");
			isDragActive = false;
		});

		rootVisualElement.RegisterCallback<DragLeaveEvent>(_ =>
		{
			rootVisualElement.RemoveFromClassList("external-drag");
		});

		rootVisualElement.RegisterCallback<DragExitedEvent>(_ =>
		{
			rootVisualElement.RemoveFromClassList("external-drag");
			isDragActive = false;
		});
	}

	bool IsReorderForSection(string reorderInfo, bool isScene)
	{
		if (string.IsNullOrEmpty(reorderInfo)) return false;
		return isScene ? reorderInfo.StartsWith("scene:") : reorderInfo.StartsWith("project:");
	}

	bool HasExternalDropCandidates()
	{
		if (DragAndDrop.objectReferences == null || DragAndDrop.objectReferences.Length == 0)
			return false;

		// Reject our own internal reorder drags — those are handled by section handlers
		var reorderInfo = DragAndDrop.GetGenericData(k_ReorderKey) as string;
		if (!string.IsNullOrEmpty(reorderInfo)) return false;

		foreach (var obj in DragAndDrop.objectReferences)
			if (obj != null) return true;

		return false;
	}

	// ─── Swap Target Tracking ──────────────────────────────────

	void UpdateSwapTarget(Vector2 mousePos, ScrollView rows)
	{
		string sourceId = GetReorderSourceId();

		VisualElement newTarget = null;
		foreach (var row in rows.Children())
		{
			if ((row.userData as string) == sourceId) continue;
			if (row.worldBound.Contains(mousePos))
			{
				newTarget = row;
				break;
			}
		}

		if (newTarget == currentSwapTarget) return;

		ClearSwapTarget();
		currentSwapTarget = newTarget;
		currentSwapTarget?.AddToClassList("swap-hover");
	}

	string GetReorderSourceId()
	{
		var info = DragAndDrop.GetGenericData(k_ReorderKey) as string;
		if (string.IsNullOrEmpty(info)) return null;
		int colon = info.IndexOf(':');
		return colon >= 0 ? info.Substring(colon + 1) : null;
	}

	void ClearSwapTarget()
	{
		currentSwapTarget?.RemoveFromClassList("swap-hover");
		currentSwapTarget = null;
	}

	// ─── Swap & Add Operations ─────────────────────────────────

	void PerformSwap(string reorderInfo, bool isScene)
	{
		if (currentSwapTarget == null) return;

		string sourceId = GetReorderSourceId();
		string targetId = currentSwapTarget.userData as string;
		if (sourceId == null || targetId == null || sourceId == targetId) return;

		var ids = isScene ? sceneIds : projectIds;
		int srcIdx = ids.IndexOf(sourceId);
		int dstIdx = ids.IndexOf(targetId);
		if (srcIdx < 0 || dstIdx < 0) return;

		Undo.RecordObject(this, "Reorder Quick Access");

		// Two-item swap — no other items move
		ids[srcIdx] = targetId;
		ids[dstIdx] = sourceId;

		RebuildRows(isScene);
		SaveToPrefs();
	}

	void PerformExternalAdd()
	{
		var objects = DragAndDrop.objectReferences;
		if (objects == null || objects.Length == 0) return;

		var validObjects = objects.Where(o => o != null).ToArray();
		if (validObjects.Length == 0) return;

		// Classify into scene vs project
		var sceneObjects   = validObjects.Where(IsSceneObject).ToArray();
		var projectObjects = validObjects.Where(o => !IsSceneObject(o)).ToArray();

		bool sceneChanged   = false;
		bool projectChanged = false;

		// Multiple objects → single multi-object item per section
		if (sceneObjects.Length > 1)
		{
			var id = ObjectsToID(sceneObjects);
			if (!string.IsNullOrEmpty(id) && !sceneIds.Contains(id))
			{
				if (!sceneChanged && !projectChanged)
					Undo.RecordObject(this, "Add to Quick Access");
				sceneIds.Add(id);
				sceneChanged = true;
			}
		}
		else if (sceneObjects.Length == 1)
		{
			var id = ObjectToID(sceneObjects[0]);
			if (!string.IsNullOrEmpty(id) && !sceneIds.Contains(id))
			{
				if (!sceneChanged && !projectChanged)
					Undo.RecordObject(this, "Add to Quick Access");
				sceneIds.Add(id);
				sceneChanged = true;
			}
		}

		if (projectObjects.Length > 1)
		{
			var id = ObjectsToID(projectObjects);
			if (!string.IsNullOrEmpty(id) && !projectIds.Contains(id))
			{
				if (!sceneChanged && !projectChanged)
					Undo.RecordObject(this, "Add to Quick Access");
				projectIds.Add(id);
				projectChanged = true;
			}
		}
		else if (projectObjects.Length == 1)
		{
			var id = ObjectToID(projectObjects[0]);
			if (!string.IsNullOrEmpty(id) && !projectIds.Contains(id))
			{
				if (!sceneChanged && !projectChanged)
					Undo.RecordObject(this, "Add to Quick Access");
				projectIds.Add(id);
				projectChanged = true;
			}
		}

		if (sceneChanged)   RebuildRows(isScene: true);
		if (projectChanged) RebuildRows(isScene: false);
		if (sceneChanged || projectChanged) SaveToPrefs();
	}

	// ─── View Anchors ──────────────────────────────────────────
	//
	// Per-scene Scene View camera bookmarks. The "+" button captures the last active
	// Scene View camera transform; recall (click row or Shift+1..9) aligns that camera
	// back to the stored position/orientation. Only the transform is recalled — zoom
	// (SceneView.size) is left untouched.

	void AddCurrentViewAsAnchor()
	{
		var sv = SceneView.lastActiveSceneView;
		if (sv == null || sv.camera == null)
		{
			Debug.LogWarning("[QuickAccess] No active Scene View to capture a view anchor from.");
			return;
		}

		Undo.RecordObject(this, "Add View Anchor");
		var t = sv.camera.transform;
		viewAnchors.Add(new QuickAccessStore.AnchorEntry { position = t.position, rotation = t.rotation });
		if (viewAnchors.Count > k_MaxAnchors)
			viewAnchors.RemoveAt(0);  // FIFO: oldest drops out, the rest renumber on rebuild

		RebuildAnchorRows();
		SaveToPrefs();
	}

	void RemoveAnchorAt(int index)
	{
		if (index < 0 || index >= viewAnchors.Count) return;
		Undo.RecordObject(this, "Remove View Anchor");
		viewAnchors.RemoveAt(index);
		RebuildAnchorRows();
		SaveToPrefs();
	}

	/// <summary>Align the last active Scene View camera to a stored anchor. Works whether or not
	/// the window is open (falls back to reading the active scene's sidecar).</summary>
	static void DoRecallAnchor(int index)
	{
		var anchors = s_instance != null ? s_instance.viewAnchors : LoadAnchorsForActiveScene();
		if (anchors == null || index < 0 || index >= anchors.Count) return;

		var sv = SceneView.lastActiveSceneView;
		if (sv == null) return;

		var anchor = anchors[index];
		sv.rotation = anchor.rotation;
		// SceneView orbits its pivot, not the camera. Place the pivot ahead of the target
		// position along the view direction so the camera itself lands on the stored transform.
		sv.pivot = anchor.position + anchor.rotation * Vector3.forward * sv.cameraDistance;
		sv.Repaint();
	}

	static List<QuickAccessStore.AnchorEntry> LoadAnchorsForActiveScene()
	{
		var guid = GuidForActiveScene();
		if (string.IsNullOrEmpty(guid)) return null;
		return QuickAccessStore.LoadScene(guid).viewAnchors;
	}

	// ─── Row Creation ──────────────────────────────────────────

	VisualElement CreateRow(string id, bool isScene)
	{
		bool isMulti = id.StartsWith("multi:");
		var objects = isMulti ? ObjectsFromID(id) : null;
		var obj = isMulti ? (objects.Length > 0 ? objects[0] : null) : ObjectFromID(id);

		var row = new VisualElement();
		row.AddToClassList("row");
		if (isMulti) row.AddToClassList("multi-object");
		row.userData = id;

		var title = new VisualElement();
		title.AddToClassList("title");

		var icon = new Image();
		icon.AddToClassList("icon");
		title.Add(icon);

		var label = new Label();
		title.Add(label);

		row.Add(title);

		var badge = new Label();
		badge.AddToClassList("selection-badge");
		badge.style.display = DisplayStyle.None;
		row.Add(badge);

		// Click badge to recall its selection group
		badge.RegisterCallback<ClickEvent>(evt =>
		{
			if (badge.style.display == DisplayStyle.None) return;
			if (!int.TryParse(badge.text, out int displayNum)) return;
			// Display "1"→slot 0, "0"→slot 9
			int slot = displayNum == 0 ? 9 : displayNum - 1;
			DoRecallSelectionGroup(slot);
			evt.StopPropagation();
		});

		RefreshRowDetails(row, obj, id, isMulti, objects);
		UpdateSelectionBadge(row, id);

		// Click to select / double-click to open
		row.RegisterCallback<ClickEvent>(HandleRowClick);

		// Right-click to remove (undoable)
		row.RegisterCallback<ContextClickEvent>(evt =>
		{
			var ids = isScene ? sceneIds : projectIds;
			Undo.RecordObject(this, "Remove from Quick Access");
			ids.Remove(id);
			RemoveItemFromSelectionGroups(id);
			RebuildRows(isScene);
			SaveToPrefs();
			evt.StopPropagation();
		});

		// Drag initiation — always uses DragAndDrop so it works across windows
		row.RegisterCallback<PointerDownEvent>(evt =>
		{
			if (evt.button != 0) return;
			if (isMulti)
			{
				// Multi-object items: need at least one resolved object to drag
				if (objects == null || objects.Length == 0) return;
				dragCandidate = objects[0]; // Primary object for drag start
			}
			else
			{
				var o = ObjectFromID(id);
				if (o == null) return;
				dragCandidate = o;
			}
			dragStartPos = evt.position;
			isDragActive = false;
		});

		row.RegisterCallback<PointerMoveEvent>(evt =>
		{
			if ((evt.pressedButtons & 1) == 0 || dragCandidate == null || isDragActive)
				return;

			if (Vector2.Distance(evt.position, dragStartPos) < k_DragThreshold)
				return;

			isDragActive = true;

			DragAndDrop.PrepareStartDrag();

			if (isMulti)
			{
				var resolved = ObjectsFromID(id);
				DragAndDrop.objectReferences = resolved;
				DragAndDrop.paths = new string[0];
			}
			else
			{
				DragAndDrop.objectReferences = new[] { dragCandidate };
				var path = AssetDatabase.GetAssetPath(dragCandidate);
				DragAndDrop.paths = string.IsNullOrEmpty(path) ? new string[0] : new[] { path };
			}

			// Tag as internal reorder so our DragPerform can distinguish
			string sectionTag = isScene ? "scene:" : "project:";
			DragAndDrop.SetGenericData(k_ReorderKey, sectionTag + id);

			DragAndDrop.StartDrag(isMulti ? $"{objects.Length} Objects" : dragCandidate.name);
		});

		row.RegisterCallback<PointerUpEvent>(evt =>
		{
			if (evt.button == 0)
			{
				dragCandidate = null;
				isDragActive = false;
			}
		});

		return row;
	}

	// ─── Row Interaction ───────────────────────────────────────

	void HandleRowClick(ClickEvent evt)
	{
		var row = evt.currentTarget as VisualElement;
		var id = row.userData as string;
		bool isMulti = id != null && id.StartsWith("multi:");

		if (isMulti)
		{
			var resolved = ObjectsFromID(id);
			if (resolved.Length == 0) return;

			if (evt.clickCount == 2)
			{
				// No single "open" target for multi items — just select all
				Selection.objects = resolved;
			}
			else if (evt.ctrlKey || evt.commandKey)
			{
				// Toggle: add/remove all group objects from selection
				var current = new HashSet<Object>(Selection.objects);
				bool allSelected = resolved.All(o => current.Contains(o));
				if (allSelected)
					Selection.objects = Selection.objects.Where(o => !resolved.Contains(o)).ToArray();
				else
					Selection.objects = Selection.objects.Concat(resolved).Distinct().ToArray();
			}
			else
			{
				Selection.objects = resolved;
			}
			return;
		}

		var obj = ObjectFromID(id);
		RefreshRowDetails(row, obj, id, false, null);
		if (obj == null) return;

		if (evt.clickCount == 2)
		{
			AssetDatabase.OpenAsset(obj);
		}
		else if (IsFolder(obj))
		{
			ShowFolderInProjectWindow(obj.GetInstanceID());
		}
		else
		{
			if (evt.ctrlKey || evt.commandKey)
			{
				if (Selection.objects.Contains(obj))
					Selection.objects = Selection.objects.Where(o => o != obj).ToArray();
				else
					Selection.objects = Selection.objects.Concat(new[] { obj }).ToArray();
			}
			else
			{
				Selection.activeObject = obj;
				EditorGUIUtility.PingObject(obj);
			}
		}
	}

	// ─── Selection Sync ────────────────────────────────────────

	void OnSelectionChanged()
	{
		SyncSelectionHighlights(sceneRows);
		SyncSelectionHighlights(projectRows);
	}

	// Hot path: fires on EVERY global selection change, even when the selected
	// object has nothing to do with QuickAccess. It must stay cheap — do nothing
	// but toggle the "selected" class.
	//
	// Two things are deliberately NOT done here:
	//  1. No RefreshRowDetails — selecting an object doesn't change its name or
	//     icon, so there's no reason to rebuild them (ObjectContent + UQuery
	//     traversal per row is what made selection feel sluggish). Labels/icons
	//     refresh on rebuild and scene-change instead.
	//  2. No slow ID resolution — we read only the resolution caches (warmed when
	//     rows are built) and never fall through to GlobalObjectIdentifierToObjectSlow.
	//     An unresolved/missing item simply won't highlight until the next rebuild,
	//     which is fine and infinitely cheaper than re-running the slow API on
	//     every click.
	void SyncSelectionHighlights(ScrollView rows)
	{
		if (rows == null) return;

		var selected = Selection.objects;
		if (selected == null || selected.Length == 0)
		{
			foreach (var row in rows.Children())
				row.RemoveFromClassList("selected");
			return;
		}

		// HashSet so per-row lookup is O(1) rather than a linear array scan.
		var selectedSet = new HashSet<Object>(selected);

		foreach (var row in rows.Children())
		{
			var id = row.userData as string;
			row.EnableInClassList("selected", id != null && IsIdSelected(id, selectedSet));
		}
	}

	/// <summary>Cache-only membership test used by the selection-highlight hot path.
	/// Reads s_singleCache/s_multiCache only — never triggers the slow resolve APIs.</summary>
	static bool IsIdSelected(string id, HashSet<Object> selectedSet)
	{
		if (id.StartsWith("multi:"))
		{
			if (s_multiCache.TryGetValue(id, out var objs))
				foreach (var o in objs)
					if (o != null && selectedSet.Contains(o)) return true;
			return false;
		}

		return s_singleCache.TryGetValue(id, out var obj)
		       && obj != null
		       && selectedSet.Contains(obj);
	}

	// ─── Scene Change ──────────────────────────────────────────

	void OnActiveSceneChanged(Scene oldScene, Scene newScene)
	{
		// Flush old scene's state under the GUID it was loaded with —
		// currentSceneGuid is the single source of truth, never re-derive
		// from oldScene (it may have been renamed / unloaded since load).
		FlushCurrentSceneToDisk();
		ReloadSceneItems(newScene);
	}

	void OnSceneOpened(Scene scene, OpenSceneMode mode)
	{
		// Safety net: activeSceneChangedInEditMode may not fire for all scene opens.
		if (scene == SceneManager.GetActiveScene())
			ReloadSceneItems(scene);
	}

	/// <summary>Write the in-memory scene state to disk under currentSceneGuid.
	/// No-op for untitled scenes.</summary>
	void FlushCurrentSceneToDisk()
	{
		if (string.IsNullOrEmpty(currentSceneGuid)) return;
		var data = new QuickAccessStore.SceneData
		{
			items = new List<string>(sceneIds),
			viewAnchors = new List<QuickAccessStore.AnchorEntry>(viewAnchors),
		};
		foreach (var kvp in selectionGroups)
			data.SetGroup(kvp.Key, kvp.Value);
		QuickAccessStore.SaveScene(currentSceneGuid, data);
	}

	void ReloadSceneItems(Scene scene)
	{
		InvalidateCache(); // Scene objects may have changed
		sceneIds.Clear();
		selectionGroups.Clear();
		viewAnchors.Clear();

		currentSceneGuid = GuidForScene(scene);
		if (!string.IsNullOrEmpty(currentSceneGuid))
		{
			// Lazy migration: if a sidecar doesn't exist yet but EditorPrefs has
			// data for this scene, migrate now. Cheap if there's nothing to do.
			if (!QuickAccessStore.SceneFileExists(currentSceneGuid))
				MigrateSceneFromPrefs(scene, currentSceneGuid, alsoMigrateGlobalGroups: false);

			var data = QuickAccessStore.LoadScene(currentSceneGuid);
			sceneIds.AddRange(data.items);
			viewAnchors.AddRange(data.viewAnchors);
			foreach (var g in data.groups)
				selectionGroups[g.slot] = new List<string>(g.items);
		}

		// Stamp the in-memory state with its owning scene. Read in OnUndoRedo to detect when an
		// undo reverted us to a different scene's data — see the field comment for the full
		// rationale.
		sceneIdsOwnerGuid = currentSceneGuid;

		// Batch-resolve all scene items up front (one scene scan) so the per-row
		// ObjectFromID calls inside RebuildRows are cache hits instead of N slow ones.
		WarmSceneIdCache(sceneIds);

		RebuildAnchorRows();
		RebuildRows(isScene: true);
		RefreshAllBadges();
	}

	// ─── Persistence ───────────────────────────────────────────

	void LoadFromPrefs()
	{
		projectIds.Clear();
		var proj = QuickAccessStore.LoadProject();
		projectIds.AddRange(proj.items);

		// Scene list, selectionGroups and currentSceneGuid are established
		// via ReloadSceneItems (also called from OnSceneOpened / OnActiveSceneChanged).
		ReloadSceneItems(SceneManager.GetActiveScene());
	}

	bool saveQueued;

	// Coalesces multiple mutations in the same frame into a single disk write.
	// Selection changes, undo/redo and slot edits can all fire back-to-back;
	// without this, File.Replace on _project.json races its own previous swap.
	void SaveToPrefs()
	{
		if (saveQueued) return;
		saveQueued = true;
		EditorApplication.delayCall += FlushPendingSave;
	}

	void FlushPendingSave()
	{
		EditorApplication.delayCall -= FlushPendingSave;
		if (!saveQueued) return;
		saveQueued = false;
		SaveToPrefsImmediate();
	}

	void SaveToPrefsImmediate()
	{
		// Project file
		QuickAccessStore.SaveProject(new QuickAccessStore.ProjectData
		{
			items = new List<string>(projectIds),
		});

		// Scene file (items + per-scene selection groups). Untitled scenes
		// (no asset GUID) keep their state in memory for the session only —
		// writing under an empty GUID would pollute a shared bucket.
		FlushCurrentSceneToDisk();
	}

	// ─── Selection Group Persistence ───────────────────────────
	//
	// Selection groups are part of the per-scene sidecar file. The window
	// keeps a live in-memory copy; SaveToPrefs flushes the whole scene state
	// (one codepath for everything).

	/// <summary>Load the selection groups for the active scene without needing the window open.</summary>
	static Dictionary<int, List<string>> LoadSelectionGroupsForActiveScene()
	{
		var guid = GuidForActiveScene();
		if (string.IsNullOrEmpty(guid)) return new Dictionary<int, List<string>>();
		var data = QuickAccessStore.LoadScene(guid);
		var groups = new Dictionary<int, List<string>>();
		foreach (var g in data.groups)
			groups[g.slot] = new List<string>(g.items);
		return groups;
	}

	// ─── Selection Group Operations ────────────────────────────

	/// <summary>Remove an item ID from any selection group slot that references it.</summary>
	void RemoveItemFromSelectionGroups(string id)
	{
		var slotsToClean = new List<int>();
		foreach (var kvp in selectionGroups)
		{
			kvp.Value.Remove(id);
			if (kvp.Value.Count == 0)
				slotsToClean.Add(kvp.Key);
		}
		foreach (var slot in slotsToClean)
			selectionGroups.Remove(slot);
	}

	/// <summary>Find which group slot (0-9) an item belongs to, or -1 if none.</summary>
	int FindGroupForItem(string id)
	{
		foreach (var kvp in selectionGroups)
			if (kvp.Value.Contains(id))
				return kvp.Key;
		return -1;
	}

	/// <summary>
	/// Save the current selection to a group slot. Creates multi-object items as needed.
	/// Mixed selections (scene + project) split into two items, both tagged with the same slot.
	/// </summary>
	void SaveSelectionGroupToSlot(int slot, Object[] sceneObjects, Object[] projectObjects)
	{
		// Clear old slot contents — remove items that were ONLY created for this group
		if (selectionGroups.TryGetValue(slot, out var oldIds))
		{
			foreach (var oldId in oldIds.ToList())
			{
				// If this item is a multi-object item created by a group save,
				// remove it from the item list entirely (it exists solely for the group)
				if (oldId.StartsWith("multi:"))
				{
					sceneIds.Remove(oldId);
					projectIds.Remove(oldId);
				}
			}
			selectionGroups.Remove(slot);
		}

		var newSlotIds = new List<string>();
		bool sceneChanged = false;
		bool projectChanged = false;

		// Scene objects
		if (sceneObjects.Length > 0)
		{
			var id = ObjectsToID(sceneObjects);
			if (!string.IsNullOrEmpty(id))
			{
				if (!sceneIds.Contains(id))
				{
					Undo.RecordObject(this, "Save Selection Group");
					sceneIds.Add(id);
					sceneChanged = true;
				}
				newSlotIds.Add(id);
			}
		}

		// Project objects
		if (projectObjects.Length > 0)
		{
			var id = ObjectsToID(projectObjects);
			if (!string.IsNullOrEmpty(id))
			{
				if (!projectIds.Contains(id))
				{
					if (!sceneChanged)
						Undo.RecordObject(this, "Save Selection Group");
					projectIds.Add(id);
					projectChanged = true;
				}
				newSlotIds.Add(id);
			}
		}

		if (newSlotIds.Count > 0)
			selectionGroups[slot] = newSlotIds;

		SaveToPrefs();

		if (sceneChanged)  RebuildRows(isScene: true);
		if (projectChanged) RebuildRows(isScene: false);
		if (!sceneChanged && !projectChanged) RefreshAllBadges();
	}

	// ─── Selection Group Shortcuts ─────────────────────────────

	static void DoSaveSelectionGroup(int slot)
	{
		// When the Project window's folder tree (left column) has focus,
		// Selection.objects contains the right-pane assets, not the folder.
		// Detect this and override with the actual folder the user clicked.
		var folderOverride = GetActiveFolderIfTreeFocused();
		var objects = folderOverride != null
			? new Object[] { folderOverride }
			: Selection.objects;

		if (objects == null || objects.Length == 0)
		{
			// Clear the slot
			if (s_instance != null)
			{
				if (s_instance.selectionGroups.TryGetValue(slot, out var oldIds))
				{
					foreach (var oldId in oldIds.ToList())
					{
						if (oldId.StartsWith("multi:"))
						{
							s_instance.sceneIds.Remove(oldId);
							s_instance.projectIds.Remove(oldId);
						}
					}
				}
				s_instance.selectionGroups.Remove(slot);
				s_instance.SaveToPrefs();
				s_instance.RebuildAllRows();
			}
			return;
		}

		var sceneObjects   = objects.Where(IsSceneObject).ToArray();
		var projectObjects = objects.Where(o => !IsSceneObject(o)).ToArray();

		if (s_instance != null)
		{
			s_instance.SaveSelectionGroupToSlot(slot, sceneObjects, projectObjects);
		}
		else
		{
			// Window not open — save directly to prefs
			SaveSelectionGroupWithoutWindow(slot, sceneObjects, projectObjects);
		}
	}

	static void SaveSelectionGroupWithoutWindow(int slot, Object[] sceneObjects, Object[] projectObjects)
	{
		// Load both sidecars, mutate in memory, write back. Single-writer per
		// file (no other process touches these), so no race.
		var sceneGuid = GuidForActiveScene();
		bool canPersistScene = !string.IsNullOrEmpty(sceneGuid);

		var sceneData   = canPersistScene ? QuickAccessStore.LoadScene(sceneGuid) : new QuickAccessStore.SceneData();
		var projectData = QuickAccessStore.LoadProject();

		// Drop old multi-items that existed solely to back this slot
		if (sceneData.TryGetGroup(slot, out var oldIds))
		{
			foreach (var oldId in oldIds)
			{
				if (oldId.StartsWith("multi:"))
				{
					sceneData.items.Remove(oldId);
					projectData.items.Remove(oldId);
				}
			}
		}

		var newSlotIds = new List<string>();

		if (sceneObjects.Length > 0 && canPersistScene)
		{
			var id = ObjectsToID(sceneObjects);
			if (!string.IsNullOrEmpty(id))
			{
				if (!sceneData.items.Contains(id)) sceneData.items.Add(id);
				newSlotIds.Add(id);
			}
		}

		if (projectObjects.Length > 0)
		{
			var id = ObjectsToID(projectObjects);
			if (!string.IsNullOrEmpty(id))
			{
				if (!projectData.items.Contains(id)) projectData.items.Add(id);
				newSlotIds.Add(id);
			}
		}

		if (newSlotIds.Count > 0)
			sceneData.SetGroup(slot, newSlotIds);
		else
			sceneData.RemoveGroup(slot);

		if (canPersistScene) QuickAccessStore.SaveScene(sceneGuid, sceneData);
		QuickAccessStore.SaveProject(projectData);
	}

	static void DoRecallSelectionGroup(int slot)
	{
		List<string> itemIds = null;

		if (s_instance != null)
			s_instance.selectionGroups.TryGetValue(slot, out itemIds);
		else
		{
			var groups = LoadSelectionGroupsForActiveScene();
			groups.TryGetValue(slot, out itemIds);
		}

		if (itemIds == null || itemIds.Count == 0) return;

		var allObjects = new List<Object>();
		foreach (var id in itemIds)
			allObjects.AddRange(ObjectsFromID(id));

		if (allObjects.Count > 0)
			Selection.objects = allObjects.ToArray();
	}

	// ─── Keyboard Shortcuts ────────────────────────────────────

	[Shortcut("Editools/QuickAccess Save Group 1", KeyCode.Alpha1, ShortcutModifiers.Action)]
	static void SaveGroup1() => DoSaveSelectionGroup(0);
	[Shortcut("Editools/QuickAccess Save Group 2", KeyCode.Alpha2, ShortcutModifiers.Action)]
	static void SaveGroup2() => DoSaveSelectionGroup(1);
	[Shortcut("Editools/QuickAccess Save Group 3", KeyCode.Alpha3, ShortcutModifiers.Action)]
	static void SaveGroup3() => DoSaveSelectionGroup(2);
	[Shortcut("Editools/QuickAccess Save Group 4", KeyCode.Alpha4, ShortcutModifiers.Action)]
	static void SaveGroup4() => DoSaveSelectionGroup(3);
	[Shortcut("Editools/QuickAccess Save Group 5", KeyCode.Alpha5, ShortcutModifiers.Action)]
	static void SaveGroup5() => DoSaveSelectionGroup(4);
	[Shortcut("Editools/QuickAccess Save Group 6", KeyCode.Alpha6, ShortcutModifiers.Action)]
	static void SaveGroup6() => DoSaveSelectionGroup(5);
	[Shortcut("Editools/QuickAccess Save Group 7", KeyCode.Alpha7, ShortcutModifiers.Action)]
	static void SaveGroup7() => DoSaveSelectionGroup(6);
	[Shortcut("Editools/QuickAccess Save Group 8", KeyCode.Alpha8, ShortcutModifiers.Action)]
	static void SaveGroup8() => DoSaveSelectionGroup(7);
	[Shortcut("Editools/QuickAccess Save Group 9", KeyCode.Alpha9, ShortcutModifiers.Action)]
	static void SaveGroup9() => DoSaveSelectionGroup(8);
	[Shortcut("Editools/QuickAccess Save Group 0", KeyCode.Alpha0, ShortcutModifiers.Action)]
	static void SaveGroup0() => DoSaveSelectionGroup(9);

	[Shortcut("Editools/QuickAccess Recall Group 1", KeyCode.Alpha1)]
	static void RecallGroup1() => DoRecallSelectionGroup(0);
	[Shortcut("Editools/QuickAccess Recall Group 2", KeyCode.Alpha2)]
	static void RecallGroup2() => DoRecallSelectionGroup(1);
	[Shortcut("Editools/QuickAccess Recall Group 3", KeyCode.Alpha3)]
	static void RecallGroup3() => DoRecallSelectionGroup(2);
	[Shortcut("Editools/QuickAccess Recall Group 4", KeyCode.Alpha4)]
	static void RecallGroup4() => DoRecallSelectionGroup(3);
	[Shortcut("Editools/QuickAccess Recall Group 5", KeyCode.Alpha5)]
	static void RecallGroup5() => DoRecallSelectionGroup(4);
	[Shortcut("Editools/QuickAccess Recall Group 6", KeyCode.Alpha6)]
	static void RecallGroup6() => DoRecallSelectionGroup(5);
	[Shortcut("Editools/QuickAccess Recall Group 7", KeyCode.Alpha7)]
	static void RecallGroup7() => DoRecallSelectionGroup(6);
	[Shortcut("Editools/QuickAccess Recall Group 8", KeyCode.Alpha8)]
	static void RecallGroup8() => DoRecallSelectionGroup(7);
	[Shortcut("Editools/QuickAccess Recall Group 9", KeyCode.Alpha9)]
	static void RecallGroup9() => DoRecallSelectionGroup(8);
	[Shortcut("Editools/QuickAccess Recall Group 0", KeyCode.Alpha0)]
	static void RecallGroup0() => DoRecallSelectionGroup(9);

	// View anchor recall — Shift+1..9, only while the Scene View has focus so they
	// don't collide with Shift+number bindings in other editor windows.
	[Shortcut("Editools/QuickAccess Recall View Anchor 1", typeof(SceneView), KeyCode.Alpha1, ShortcutModifiers.Shift)]
	static void RecallAnchor1() => DoRecallAnchor(0);
	[Shortcut("Editools/QuickAccess Recall View Anchor 2", typeof(SceneView), KeyCode.Alpha2, ShortcutModifiers.Shift)]
	static void RecallAnchor2() => DoRecallAnchor(1);
	[Shortcut("Editools/QuickAccess Recall View Anchor 3", typeof(SceneView), KeyCode.Alpha3, ShortcutModifiers.Shift)]
	static void RecallAnchor3() => DoRecallAnchor(2);
	[Shortcut("Editools/QuickAccess Recall View Anchor 4", typeof(SceneView), KeyCode.Alpha4, ShortcutModifiers.Shift)]
	static void RecallAnchor4() => DoRecallAnchor(3);
	[Shortcut("Editools/QuickAccess Recall View Anchor 5", typeof(SceneView), KeyCode.Alpha5, ShortcutModifiers.Shift)]
	static void RecallAnchor5() => DoRecallAnchor(4);
	[Shortcut("Editools/QuickAccess Recall View Anchor 6", typeof(SceneView), KeyCode.Alpha6, ShortcutModifiers.Shift)]
	static void RecallAnchor6() => DoRecallAnchor(5);
	[Shortcut("Editools/QuickAccess Recall View Anchor 7", typeof(SceneView), KeyCode.Alpha7, ShortcutModifiers.Shift)]
	static void RecallAnchor7() => DoRecallAnchor(6);
	[Shortcut("Editools/QuickAccess Recall View Anchor 8", typeof(SceneView), KeyCode.Alpha8, ShortcutModifiers.Shift)]
	static void RecallAnchor8() => DoRecallAnchor(7);
	[Shortcut("Editools/QuickAccess Recall View Anchor 9", typeof(SceneView), KeyCode.Alpha9, ShortcutModifiers.Shift)]
	static void RecallAnchor9() => DoRecallAnchor(8);

	// ─── Badge Refresh ─────────────────────────────────────────

	void RefreshAllBadges()
	{
		RefreshBadges(sceneRows);
		RefreshBadges(projectRows);
	}

	void RefreshBadges(ScrollView rows)
	{
		if (rows == null) return;
		foreach (var row in rows.Children())
			UpdateSelectionBadge(row, row.userData as string);
	}

	// ─── Row Rebuild ───────────────────────────────────────────

	void RebuildAllRows()
	{
		RebuildAnchorRows();
		RebuildRows(isScene: true);
		RebuildRows(isScene: false);
	}

	void RebuildAnchorRows()
	{
		// UI not built yet (shortcut fired before window layout)
		if (anchorRows == null) return;

		anchorRows.Clear();

		for (int i = 0; i < viewAnchors.Count; i++)
		{
			var row = CreateAnchorRow(i);
			row.EnableInClassList("variant1", i % 2 == 0);
			row.EnableInClassList("variant2", i % 2 == 1);
			anchorRows.Add(row);
		}

		anchorEmptyHint.style.display = viewAnchors.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
	}

	VisualElement CreateAnchorRow(int index)
	{
		var row = new VisualElement();
		row.AddToClassList("row");
		row.tooltip = $"Recall (Shift+{index + 1})  ·  right-click to remove";

		var title = new VisualElement();
		title.AddToClassList("title");

		var icon = new Image();
		icon.AddToClassList("icon");
		icon.image = EditorGUIUtility.IconContent("d_SceneViewCamera").image;
		title.Add(icon);

		var label = new Label($"Anchor {index + 1}");
		title.Add(label);

		row.Add(title);

		// Auto number 1..9, reusing the selection-group badge styling
		var numberBadge = new Label((index + 1).ToString());
		numberBadge.AddToClassList("selection-badge");
		row.Add(numberBadge);

		row.RegisterCallback<ClickEvent>(_ => DoRecallAnchor(index));

		row.RegisterCallback<ContextClickEvent>(evt =>
		{
			RemoveAnchorAt(index);
			evt.StopPropagation();
		});

		return row;
	}

	void RebuildRows(bool isScene)
	{
		var rows     = isScene ? sceneRows : projectRows;
		var ids      = isScene ? sceneIds : projectIds;
		var hint     = isScene ? sceneEmptyHint : projectEmptyHint;
		var dropZone = isScene ? sceneDropZone : projectDropZone;

		// UI not built yet (shortcut fired before window layout)
		if (rows == null) return;

		rows.Clear();

		for (int i = 0; i < ids.Count; i++)
		{
			var row = CreateRow(ids[i], isScene);
			row.EnableInClassList("variant1", i % 2 == 0);
			row.EnableInClassList("variant2", i % 2 == 1);
			rows.Add(row);
		}

		bool empty = ids.Count == 0;
		hint.style.display     = empty ? DisplayStyle.Flex : DisplayStyle.None;
		dropZone.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
	}

	// ─── Row Helpers ───────────────────────────────────────────

	void RefreshRowDetails(VisualElement row, Object obj, string id, bool isMulti, Object[] multiObjects)
	{
		if (isMulti)
		{
			int totalCount = 0;
			if (id.StartsWith("multi:"))
				totalCount = id.Substring("multi:".Length).Split('|').Length;
			int resolvedCount = multiObjects?.Length ?? 0;

			row.EnableInClassList("unavailable", resolvedCount == 0);

			var titleLabel = row.Q(className: "title")?.Q<Label>();
			if (titleLabel != null)
			{
				titleLabel.text = resolvedCount == 0
					? $"({totalCount} Missing Objects)"
					: $"{totalCount} Objects";
			}

			// Tooltip: first few object names
			if (multiObjects != null && multiObjects.Length > 0)
			{
				var names = multiObjects.Take(5).Select(o => o.name);
				var tooltip = string.Join("\n", names);
				if (multiObjects.Length > 5) tooltip += $"\n... +{multiObjects.Length - 5} more";
				row.tooltip = tooltip;
			}

			var icon = row.Q<Image>(className: "icon");
			if (icon != null)
				icon.image = GetObjectIcon(obj);
		}
		else
		{
			row.EnableInClassList("unavailable", obj == null);

			var titleLabel = row.Q(className: "title")?.Q<Label>();
			if (titleLabel != null)
				titleLabel.text = obj != null ? obj.name : "(Missing)";

			var icon = row.Q<Image>(className: "icon");
			if (icon != null)
				icon.image = GetObjectIcon(obj);
		}
	}

	void UpdateSelectionBadge(VisualElement row, string id)
	{
		var badge = row.Q<Label>(className: "selection-badge");
		if (badge == null) return;

		int groupSlot = FindGroupForItem(id);
		if (groupSlot >= 0)
		{
			// Display: slot 0→"1", slot 8→"9", slot 9→"0" (matches keyboard layout)
			badge.text = (groupSlot == 9 ? 0 : groupSlot + 1).ToString();
			badge.style.display = DisplayStyle.Flex;
		}
		else
		{
			badge.style.display = DisplayStyle.None;
		}
	}

	// ─── Multi-Object ID Encoding ──────────────────────────────

	/// <summary>
	/// Encode multiple objects as a single ID string.
	/// Returns "multi:subid1|subid2|..." for 2+ objects, or a plain single ID for 1 object.
	/// </summary>
	static string ObjectsToID(Object[] objects)
	{
		if (objects == null || objects.Length == 0) return null;
		if (objects.Length == 1) return ObjectToID(objects[0]);

		var ids = new List<string>(objects.Length);
		foreach (var obj in objects)
		{
			var id = ObjectToID(obj);
			if (!string.IsNullOrEmpty(id))
				ids.Add(id);
		}
		if (ids.Count == 0) return null;
		if (ids.Count == 1) return ids[0];
		return "multi:" + string.Join("|", ids);
	}

	/// <summary>
	/// Batch pre-resolve every <c>globalid:</c> scene item in a single
	/// <c>GlobalObjectIdentifiersToObjectsSlow</c> call, warming <c>s_singleCache</c>
	/// so the subsequent per-row <c>ObjectFromID</c> calls are cache hits.
	///
	/// This is the load-time hot spot: resolving N scene items used to mean N separate
	/// <c>GlobalObjectIdentifierToObjectSlow</c> calls, each of which scans the scene.
	/// The batch form scans once for all ids. Only active-scene ids are included — the
	/// same cross-scene guard <see cref="ObjectFromID"/> uses, so the batch never
	/// synchronously loads another scene.
	/// </summary>
	static void WarmSceneIdCache(IEnumerable<string> ids)
	{
		var activeGuid = GuidForActiveScene();
		if (string.IsNullOrEmpty(activeGuid)) return;

		// Gather uncached globalid: strings, expanding multi: items into their sub-ids.
		var pending = new List<string>();
		void Collect(string id)
		{
			if (string.IsNullOrEmpty(id)) return;
			if (id.StartsWith("multi:"))
			{
				foreach (var sub in id.Substring("multi:".Length).Split('|'))
					Collect(sub);
				return;
			}
			if (!id.StartsWith("globalid:")) return;   // guid:/instance: resolve cheaply on demand
			if (s_singleCache.ContainsKey(id)) return;
			pending.Add(id);
		}
		foreach (var id in ids) Collect(id);
		if (pending.Count == 0) return;

		// Parse, keeping only ids that belong to the active scene.
		var gids = new List<GlobalObjectId>(pending.Count);
		var keys = new List<string>(pending.Count);
		foreach (var s in pending)
		{
			if (GlobalObjectId.TryParse(s.Substring("globalid:".Length), out var gid)
			    && gid.assetGUID.ToString() == activeGuid)
			{
				gids.Add(gid);
				keys.Add(s);
			}
		}
		if (gids.Count == 0) return;

		var results = new Object[gids.Count];
		GlobalObjectId.GlobalObjectIdentifiersToObjectsSlow(gids.ToArray(), results);
		for (int i = 0; i < results.Length; i++)
			if (results[i] != null)
				s_singleCache[keys[i]] = results[i];
	}

	/// <summary>
	/// Resolve an ID (single or multi) to an array of Objects.
	/// For single IDs, returns a 1-element array. For multi IDs, returns all resolved objects.
	/// Results are cached to avoid repeated GlobalObjectIdentifierToObjectSlow calls.
	/// </summary>
	static Object[] ObjectsFromID(string id)
	{
		if (string.IsNullOrEmpty(id)) return new Object[0];

		if (!id.StartsWith("multi:"))
		{
			var single = ObjectFromID(id);
			return single != null ? new[] { single } : new Object[0];
		}

		if (s_multiCache.TryGetValue(id, out var cached))
		{
			// Verify cached objects are still alive (not destroyed)
			bool allValid = true;
			foreach (var o in cached)
				if (o == null) { allValid = false; break; }
			if (allValid) return cached;
			s_multiCache.Remove(id);
		}

		var subIds = id.Substring("multi:".Length).Split('|');
		var objects = new List<Object>(subIds.Length);
		foreach (var subId in subIds)
		{
			var obj = ObjectFromID(subId);
			if (obj != null) objects.Add(obj);
		}
		var result = objects.ToArray();
		if (result.Length > 0)
			s_multiCache[id] = result;
		return result;
	}

	// ─── Object ID Encoding (same pattern as LRUAssets) ────────

	static string ObjectToID(Object obj)
	{
		if (obj == null) return null;

		var path = AssetDatabase.GetAssetPath(obj);
		var guid = AssetDatabase.AssetPathToGUID(path);
		if (!string.IsNullOrEmpty(guid))
			return $"guid:{guid}";

		if (obj is Component comp)
			obj = comp.gameObject;

		if (obj is GameObject go && go.scene.IsValid() && go.scene.isLoaded
		    && PrefabStageUtility.GetCurrentPrefabStage() == null)
		{
			// GlobalObjectId uses scene GUID + local file ID — survives renames
			var gid = GlobalObjectId.GetGlobalObjectIdSlow(obj);
			if (gid.identifierType != 0)
				return $"globalid:{gid}";
			// Fallback for objects not yet serialized
			return $"gameObject:{GetGameObjectPath(go)}";
		}

		return $"instance:{obj.GetInstanceID()}";
	}

	static Object ObjectFromID(string id)
	{
		if (string.IsNullOrEmpty(id)) return null;

		// Multi IDs: return first resolved object for backward compat
		if (id.StartsWith("multi:"))
		{
			var objects = ObjectsFromID(id);
			return objects.Length > 0 ? objects[0] : null;
		}

		// Check cache first (avoids repeated GlobalObjectIdentifierToObjectSlow)
		if (s_singleCache.TryGetValue(id, out var cached))
		{
			if (cached != null) return cached;
			s_singleCache.Remove(id); // Object was destroyed, re-resolve
		}

		Object result = null;

		if (id.StartsWith("guid:"))
			result = AssetDatabase.LoadAssetAtPath<Object>(
				AssetDatabase.GUIDToAssetPath(id.Substring("guid:".Length)));
		else if (id.StartsWith("globalid:"))
		{
			if (GlobalObjectId.TryParse(id.Substring("globalid:".Length), out var gid))
			{
				// Guard: GlobalObjectIdentifierToObjectSlow will *synchronously load
				// the referenced scene* in the background if the embedded scene GUID
				// is not the active scene — that's what caused the multi-minute
				// "QuickAccess.OnSceneOpened" stalls. Skip cross-scene refs entirely.
				var activeGuid = GuidForActiveScene();
				if (string.IsNullOrEmpty(activeGuid)
				    || gid.assetGUID.ToString() == activeGuid)
				{
					result = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
				}
			}
		}
		// Legacy path-based format — kept for backward compatibility
		else if (id.StartsWith("gameObject:"))
			result = GetGameObjectFromPath(id.Substring("gameObject:".Length));
		else if (id.StartsWith("instance:") &&
		    int.TryParse(id.Substring("instance:".Length), out int iid))
			result = EditorUtility.InstanceIDToObject(iid);

		if (result != null)
			s_singleCache[id] = result;

		return result;
	}

	static string GetGameObjectPath(GameObject obj)
	{
		string path = "/" + obj.name;
		while (obj.transform.parent != null)
		{
			obj = obj.transform.parent.gameObject;
			path = "/" + obj.name + path;
		}
		return (obj.scene != null ? $"{obj.scene.name}:" : "") + path.TrimStart('/');
	}

	static Object GetGameObjectFromPath(string path)
	{
		int colonIdx = path.IndexOf(':');
		if (colonIdx < 0) return null;

		var sceneName = path.Substring(0, colonIdx);
		var goPath    = path.Substring(colonIdx + 1);

		var scene = EditorSceneManager.GetSceneByName(sceneName);
		if (!scene.IsValid() || !scene.isLoaded) return null;

		int slashIdx = goPath.IndexOf('/');
		string rootName = slashIdx >= 0 ? goPath.Substring(0, slashIdx) : goPath;
		var rootGo = scene.GetRootGameObjects().FirstOrDefault(g => g.name == rootName);
		if (rootGo == null) return null;

		if (slashIdx < 0) return rootGo;
		return rootGo.transform.Find(goPath.Substring(slashIdx + 1))?.gameObject;
	}

	// ─── Classification ────────────────────────────────────────

	static bool IsSceneObject(Object obj)
	{
		if (obj is Component comp) obj = comp.gameObject;
		return obj is GameObject go && go.scene.IsValid() && go.scene.isLoaded
		       && !AssetDatabase.Contains(obj)
		       && PrefabStageUtility.GetCurrentPrefabStage() == null;
	}

	static bool IsFolder(Object obj)
	{
		var path = AssetDatabase.GetAssetPath(obj);
		return !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path);
	}

	/// <summary>
	/// If the focused window is a ProjectBrowser whose folder tree (left column) has
	/// keyboard focus, returns the active folder asset.  Returns null otherwise, so the
	/// caller can fall back to <see cref="Selection.objects"/>.
	/// Uses reflection into ProjectBrowser + ProjectWindowUtil (same pattern as HierarchyHeatmap).
	/// </summary>
	static Object GetActiveFolderIfTreeFocused()
	{
		const System.Reflection.BindingFlags kNonPublic =
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		const System.Reflection.BindingFlags kPublic =
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

		// 1. Is the focused window a ProjectBrowser?
		var pbType = typeof(Editor).Assembly.GetType("UnityEditor.ProjectBrowser");
		if (pbType == null) return null;

		var focused = EditorWindow.focusedWindow;
		if (focused == null || focused.GetType() != pbType)
			return null;

		// 2. Does it have a folder tree (two-column mode)?
		var treeField = pbType.GetField("m_FolderTree", kNonPublic);
		if (treeField == null) return null;

		var tree = treeField.GetValue(focused);
		if (tree == null) return null;   // one-column mode — no separate tree

		// 3. Does the folder tree have keyboard focus?
		var hasFocusMethod = tree.GetType().GetMethod("HasFocus", kPublic);
		if (hasFocusMethod == null) return null;

		if (!(bool)hasFocusMethod.Invoke(tree, null))
			return null;

		// 4. Get the active folder path via ProjectWindowUtil (internal API).
		var utilType = typeof(Editor).Assembly.GetType("UnityEditor.ProjectWindowUtil");
		if (utilType == null) return null;

		var getPathMethod = utilType.GetMethod("GetActiveFolderPath",
			System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
		if (getPathMethod == null) return null;

		var path = getPathMethod.Invoke(null, null) as string;
		if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
			return null;

		return AssetDatabase.LoadAssetAtPath<Object>(path);
	}

	/// <summary>
	/// Navigate the Project window into a folder: select it in the left tree column,
	/// show its contents in the right column, and ping (yellow outline) in the tree.
	/// Uses reflection into ProjectBrowser (same pattern as HierarchyHeatmap).
	/// </summary>
	static void ShowFolderInProjectWindow(int folderInstanceId)
	{
		const System.Reflection.BindingFlags kNonPublic =
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

		var pbType = typeof(Editor).Assembly.GetType("UnityEditor.ProjectBrowser");
		if (pbType == null) return;

		var showMethod = pbType.GetMethod("ShowFolderContents", kNonPublic);
		if (showMethod == null) return;

		var windows = Resources.FindObjectsOfTypeAll(pbType);
		if (windows.Length == 0) return;

		var browser = windows[0] as EditorWindow;

		// Navigate: selects folder in tree (left column), shows contents in right column
		showMethod.Invoke(browser, new object[] { folderInstanceId, true });

		// Yellow ping directly on the folder tree item.
		// PingObject can't be used — it navigates to the parent folder.
		// Instead, call TreeViewController.Frame(id, frame: true, ping: true).
		var treeField = pbType.GetField("m_FolderTree", kNonPublic);
		if (treeField != null)
		{
			var tree = treeField.GetValue(browser);
			if (tree != null)
			{
				var frameMethod = tree.GetType().GetMethod("Frame",
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
					null, new[] { typeof(int), typeof(bool), typeof(bool) }, null);
				frameMethod?.Invoke(tree, new object[] { folderInstanceId, true, true });
			}
		}

		browser.Repaint();
	}

	static Texture GetObjectIcon(Object obj)
	{
		return EditorGUIUtility.ObjectContent(obj,
			obj != null ? obj.GetType() : typeof(Object)).image;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// QuickAccessStore — per-user file persistence under UserSettings/QuickAccess/.
//
// One file per scene (<sceneGuid>.json) carrying both the scene's item list
// and its selection groups; one file (_project.json) for the project items.
// Writes are atomic (tmp file + File.Replace) so a crash can never leave a
// half-written sidecar. Files live in UserSettings/ — Unity preserves it
// across Library nukes, it's gitignored by convention, and it never ships
// in builds. There is only ever one writer (this editor session), so reads
// and writes don't need locking.
// ─────────────────────────────────────────────────────────────────────────────
internal static class QuickAccessStore
{
	[Serializable]
	internal class GroupEntry
	{
		public int slot;
		public List<string> items = new List<string>();
	}

	[Serializable]
	internal class AnchorEntry
	{
		public Vector3    position;
		public Quaternion rotation;
	}

	[Serializable]
	internal class SceneData
	{
		public List<string>      items       = new List<string>();
		public List<GroupEntry>  groups      = new List<GroupEntry>();
		public List<AnchorEntry> viewAnchors = new List<AnchorEntry>();

		public bool TryGetGroup(int slot, out List<string> ids)
		{
			foreach (var g in groups)
				if (g.slot == slot) { ids = g.items; return true; }
			ids = null;
			return false;
		}

		public void SetGroup(int slot, List<string> ids)
		{
			foreach (var g in groups)
				if (g.slot == slot) { g.items = new List<string>(ids); return; }
			groups.Add(new GroupEntry { slot = slot, items = new List<string>(ids) });
		}

		public void RemoveGroup(int slot)
		{
			for (int i = 0; i < groups.Count; i++)
				if (groups[i].slot == slot) { groups.RemoveAt(i); return; }
		}
	}

	[Serializable]
	internal class ProjectData
	{
		public List<string> items = new List<string>();
	}

	/// <summary>Root folder for all QuickAccess sidecar files. Resolved relative to the
	/// project root, NOT Application.dataPath, so it sits next to Assets/ rather than inside it.</summary>
	internal static string Root
	{
		get
		{
			var projectRoot = Directory.GetParent(Application.dataPath).FullName;
			return Path.Combine(projectRoot, "UserSettings", "QuickAccess");
		}
	}

	internal static string SceneFilePath(string sceneGuid) =>
		Path.Combine(Root, sceneGuid + ".json");

	internal static string ProjectFilePath =>
		Path.Combine(Root, "_project.json");

	internal static bool SceneFileExists(string sceneGuid) =>
		!string.IsNullOrEmpty(sceneGuid) && File.Exists(SceneFilePath(sceneGuid));

	internal static SceneData LoadScene(string sceneGuid)
	{
		if (string.IsNullOrEmpty(sceneGuid)) return new SceneData();
		return LoadJson<SceneData>(SceneFilePath(sceneGuid));
	}

	internal static void SaveScene(string sceneGuid, SceneData data)
	{
		if (string.IsNullOrEmpty(sceneGuid) || data == null) return;
		SaveJson(SceneFilePath(sceneGuid), data);
	}

	internal static ProjectData LoadProject() => LoadJson<ProjectData>(ProjectFilePath);

	internal static void SaveProject(ProjectData data)
	{
		if (data == null) return;
		SaveJson(ProjectFilePath, data);
	}

	// ─── JSON I/O (shared codepath for both file kinds) ────────

	static T LoadJson<T>(string path) where T : class, new()
	{
		try
		{
			if (!File.Exists(path)) return new T();
			var json = File.ReadAllText(path);
			if (string.IsNullOrEmpty(json)) return new T();
			var parsed = JsonUtility.FromJson<T>(json);
			return parsed ?? new T();
		}
		catch (Exception e)
		{
			Debug.LogWarning($"[QuickAccess] Failed to load {path}: {e.Message}");
			return new T();
		}
	}

	static void SaveJson<T>(string path, T data) where T : class
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var json = JsonUtility.ToJson(data, prettyPrint: true);

			// Write to .tmp, then swap into place. Delete + Move is used
			// instead of File.Replace because Replace briefly holds the
			// destination open during the swap, which on Windows can return
			// "Unable to remove the file to be replaced" when called twice
			// in quick succession on the same path.
			var tmp = path + ".tmp";
			File.WriteAllText(tmp, json);
			if (File.Exists(path)) File.Delete(path);
			File.Move(tmp, path);
		}
		catch (Exception e)
		{
			Debug.LogWarning($"[QuickAccess] Failed to save {path}: {e.Message}");
		}
	}
}
#endif
