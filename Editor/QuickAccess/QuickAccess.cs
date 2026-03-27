#if UNITY_EDITOR
using System.Collections.Generic;
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
/// Selection Groups (0-9): QuickAccess owns its own selection groups stored in EditorPrefs.
/// Save via Ctrl+1..0, recall via 1..0. Multi-object items supported.
/// </summary>
public class QuickAccess : EditorWindow
{
	// ─── Serialized State (undo support) ───────────────────────

	[SerializeField] List<string> sceneIds   = new List<string>();
	[SerializeField] List<string> projectIds = new List<string>();

	// ─── UI ────────────────────────────────────────────────────

	ScrollView    sceneRows, projectRows;
	VisualElement sceneDropZone, projectDropZone;
	Label         sceneEmptyHint, projectEmptyHint;

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

	// ─── Prefs Keys ────────────────────────────────────────────

	static string PrefKeyProject         => Application.dataPath + "QuickAccess_Project";
	static string PrefKeyScene           => Application.dataPath + "QuickAccess_Scene_" + ActiveSceneName;
	static string PrefKeySelectionGroups => Application.dataPath + "QuickAccess_SelectionGroups_V2";

	static string ActiveSceneName
	{
		get
		{
			var scene = SceneManager.GetActiveScene();
			return scene.IsValid() ? scene.name : "";
		}
	}

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

	// ─── One-time V1 → V2 Migration ───────────────────────────

	[InitializeOnLoadMethod]
	static void MigrateSelectionGroupsV1ToV2()
	{
		// Old V1 key stored item→group mappings. Migrate to V2 (group→items) once.
		var v1Key = Application.dataPath + "QuickAccess_SelectionGroups";
		var v1Data = EditorPrefs.GetString(v1Key, "");
		if (string.IsNullOrEmpty(v1Data)) return;

		// Only migrate if V2 doesn't exist yet
		var v2Key = Application.dataPath + "QuickAccess_SelectionGroups_V2";
		if (EditorPrefs.HasKey(v2Key))
		{
			EditorPrefs.DeleteKey(v1Key);
			return;
		}

		// Parse V1: "id1=0;id2=1;id3=2" → invert to group→items
		var groups = new Dictionary<int, List<string>>();
		foreach (var entry in v1Data.Split(';'))
		{
			if (string.IsNullOrEmpty(entry)) continue;
			int eq = entry.LastIndexOf('=');
			if (eq <= 0) continue;
			var id = entry.Substring(0, eq);
			if (int.TryParse(entry.Substring(eq + 1), out int group) && group >= 0 && group <= 9)
			{
				if (!groups.TryGetValue(group, out var list))
				{
					list = new List<string>();
					groups[group] = list;
				}
				list.Add(id);
			}
		}

		// Write V2 format and delete V1
		if (groups.Count > 0)
		{
			var parts = new List<string>();
			foreach (var kvp in groups)
				parts.Add($"{kvp.Key}={string.Join("\t", kvp.Value)}");
			EditorPrefs.SetString(v2Key, string.Join(";", parts));
		}
		EditorPrefs.DeleteKey(v1Key);
	}

	// ─── Lifecycle ─────────────────────────────────────────────

	void OnEnable()
	{
		s_instance = this;

		rootVisualElement.styleSheets.Add(Resources.Load<StyleSheet>("QuickAccess"));
		rootVisualElement.Clear();

		BuildLayout();
		LoadFromPrefs();
		RebuildAllRows();

		Undo.undoRedoPerformed += OnUndoRedo;
		Selection.selectionChanged += OnSelectionChanged;
		EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
		EditorSceneManager.sceneOpened += OnSceneOpened;
	}

	void OnDisable()
	{
		if (s_instance == this)
			s_instance = null;

		Undo.undoRedoPerformed -= OnUndoRedo;
		Selection.selectionChanged -= OnSelectionChanged;
		EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
		EditorSceneManager.sceneOpened -= OnSceneOpened;
	}

	void OnUndoRedo()
	{
		RebuildAllRows();
		SaveToPrefs();
	}

	// ─── Layout ────────────────────────────────────────────────

	void BuildLayout()
	{
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
			SaveSelectionGroupsToPrefs();
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

	void SyncSelectionHighlights(ScrollView rows)
	{
		var currentSelection = Selection.objects;

		foreach (var row in rows.Children())
		{
			var id = row.userData as string;
			bool isMulti = id != null && id.StartsWith("multi:");

			if (isMulti)
			{
				var resolved = ObjectsFromID(id);
				bool anySelected = resolved.Any(o => currentSelection.Contains(o));
				row.EnableInClassList("selected", anySelected);

				// Refresh details for multi items
				var primaryObj = resolved.Length > 0 ? resolved[0] : null;
				RefreshRowDetails(row, primaryObj, id, true, resolved);
			}
			else
			{
				var obj = ObjectFromID(id);
				RefreshRowDetails(row, obj, id, false, null);

				if (obj != null && currentSelection.Contains(obj))
					row.AddToClassList("selected");
				else
					row.RemoveFromClassList("selected");
			}
		}
	}

	// ─── Scene Change ──────────────────────────────────────────

	void OnActiveSceneChanged(Scene oldScene, Scene newScene)
	{
		ReloadSceneItems(newScene);
	}

	void OnSceneOpened(Scene scene, OpenSceneMode mode)
	{
		// Safety net: activeSceneChangedInEditMode may not fire for all scene opens.
		if (scene == SceneManager.GetActiveScene())
			ReloadSceneItems(scene);
	}

	void ReloadSceneItems(Scene scene)
	{
		InvalidateCache(); // Scene objects may have changed
		sceneIds.Clear();
		var sceneName = scene.IsValid() ? scene.name : "";
		LoadListFromPrefs(sceneIds,
			Application.dataPath + "QuickAccess_Scene_" + sceneName);
		RebuildRows(isScene: true);
	}

	// ─── Persistence ───────────────────────────────────────────

	void LoadFromPrefs()
	{
		projectIds.Clear();
		sceneIds.Clear();
		LoadListFromPrefs(projectIds, PrefKeyProject);
		LoadListFromPrefs(sceneIds, PrefKeyScene);
		LoadSelectionGroupsFromPrefs();
	}

	void LoadListFromPrefs(List<string> ids, string key)
	{
		var data = EditorPrefs.GetString(key, "");
		if (string.IsNullOrEmpty(data)) return;

		foreach (var id in data.Split(','))
			if (!string.IsNullOrEmpty(id))
				ids.Add(id);
	}

	void SaveToPrefs()
	{
		EditorPrefs.SetString(PrefKeyProject, string.Join(",", projectIds));
		EditorPrefs.SetString(PrefKeyScene, string.Join(",", sceneIds));
	}

	// ─── Selection Group Persistence ───────────────────────────

	void SaveSelectionGroupsToPrefs()
	{
		if (selectionGroups.Count == 0)
		{
			EditorPrefs.DeleteKey(PrefKeySelectionGroups);
			return;
		}
		var parts = new List<string>(selectionGroups.Count);
		foreach (var kvp in selectionGroups)
		{
			// Tab separates multiple items per slot (pipe is used inside multi: IDs)
			parts.Add($"{kvp.Key}={string.Join("\t", kvp.Value)}");
		}
		EditorPrefs.SetString(PrefKeySelectionGroups, string.Join(";", parts));
	}

	void LoadSelectionGroupsFromPrefs()
	{
		selectionGroups.Clear();
		var data = EditorPrefs.GetString(PrefKeySelectionGroups, "");
		if (string.IsNullOrEmpty(data)) return;
		ParseSelectionGroupsV2(data, selectionGroups);
	}

	static void ParseSelectionGroupsV2(string data, Dictionary<int, List<string>> target)
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

	/// <summary>Static version for when window is closed.</summary>
	static Dictionary<int, List<string>> LoadSelectionGroupsFromPrefsStatic()
	{
		var groups = new Dictionary<int, List<string>>();
		var key = Application.dataPath + "QuickAccess_SelectionGroups_V2";
		var data = EditorPrefs.GetString(key, "");
		if (string.IsNullOrEmpty(data)) return groups;
		ParseSelectionGroupsV2(data, groups);
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
		SaveSelectionGroupsToPrefs();

		if (sceneChanged)  RebuildRows(isScene: true);
		if (projectChanged) RebuildRows(isScene: false);
		if (!sceneChanged && !projectChanged) RefreshAllBadges();
	}

	// ─── Selection Group Shortcuts ─────────────────────────────

	static void DoSaveSelectionGroup(int slot)
	{
		var objects = Selection.objects;
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
				s_instance.SaveSelectionGroupsToPrefs();
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
		var groups = LoadSelectionGroupsFromPrefsStatic();

		// Remove old multi items from lists
		if (groups.TryGetValue(slot, out var oldIds))
		{
			// Load current lists from prefs
			var sceneList = new List<string>();
			var projList = new List<string>();
			var sceneData = EditorPrefs.GetString(PrefKeyScene, "");
			if (!string.IsNullOrEmpty(sceneData))
				foreach (var id in sceneData.Split(','))
					if (!string.IsNullOrEmpty(id)) sceneList.Add(id);
			var projData = EditorPrefs.GetString(PrefKeyProject, "");
			if (!string.IsNullOrEmpty(projData))
				foreach (var id in projData.Split(','))
					if (!string.IsNullOrEmpty(id)) projList.Add(id);

			foreach (var oldId in oldIds)
			{
				if (oldId.StartsWith("multi:"))
				{
					sceneList.Remove(oldId);
					projList.Remove(oldId);
				}
			}

			EditorPrefs.SetString(PrefKeyScene, string.Join(",", sceneList));
			EditorPrefs.SetString(PrefKeyProject, string.Join(",", projList));
		}

		var newSlotIds = new List<string>();

		if (sceneObjects.Length > 0)
		{
			var id = ObjectsToID(sceneObjects);
			if (!string.IsNullOrEmpty(id))
			{
				// Add to scene list if not present
				var data = EditorPrefs.GetString(PrefKeyScene, "");
				var list = string.IsNullOrEmpty(data) ? new List<string>() :
					data.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();
				if (!list.Contains(id))
				{
					list.Add(id);
					EditorPrefs.SetString(PrefKeyScene, string.Join(",", list));
				}
				newSlotIds.Add(id);
			}
		}

		if (projectObjects.Length > 0)
		{
			var id = ObjectsToID(projectObjects);
			if (!string.IsNullOrEmpty(id))
			{
				var data = EditorPrefs.GetString(PrefKeyProject, "");
				var list = string.IsNullOrEmpty(data) ? new List<string>() :
					data.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();
				if (!list.Contains(id))
				{
					list.Add(id);
					EditorPrefs.SetString(PrefKeyProject, string.Join(",", list));
				}
				newSlotIds.Add(id);
			}
		}

		if (newSlotIds.Count > 0)
			groups[slot] = newSlotIds;
		else
			groups.Remove(slot);

		// Save groups
		var key = Application.dataPath + "QuickAccess_SelectionGroups_V2";
		if (groups.Count == 0)
		{
			EditorPrefs.DeleteKey(key);
		}
		else
		{
			var parts = new List<string>();
			foreach (var kvp in groups)
				parts.Add($"{kvp.Key}={string.Join("\t", kvp.Value)}");
			EditorPrefs.SetString(key, string.Join(";", parts));
		}
	}

	static void DoRecallSelectionGroup(int slot)
	{
		List<string> itemIds = null;

		if (s_instance != null)
			s_instance.selectionGroups.TryGetValue(slot, out itemIds);
		else
		{
			var groups = LoadSelectionGroupsFromPrefsStatic();
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

	// Recall shortcuts use globalEventHandler instead of [Shortcut] because bare
	// number keys are unreliable with [Shortcut] — many Unity windows (Scene view,
	// Hierarchy, Inspector) consume key events before the shortcut system sees them.

	[InitializeOnLoadMethod]
	static void RegisterRecallGlobalHandler()
	{
		// globalEventHandler fires before individual window event processing.
		var fi = typeof(EditorApplication).GetField("globalEventHandler",
			System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
		if (fi == null) return;

		var handler = (EditorApplication.CallbackFunction)HandleRecallKeyEvent;
		// Remove first to avoid double registration on domain reload
		fi.SetValue(null, (EditorApplication.CallbackFunction)fi.GetValue(null) - handler);
		fi.SetValue(null, (EditorApplication.CallbackFunction)fi.GetValue(null) + handler);
	}

	/// <summary>
	/// Returns true if an IMGUI or UIToolkit text field actually has keyboard focus.
	/// More reliable than EditorGUIUtility.editingTextField which can get stuck.
	/// </summary>
	static bool IsTextFieldFocused()
	{
		// IMGUI: keyboardControl > 0 means some control has focus
		if (GUIUtility.keyboardControl != 0 && EditorGUIUtility.editingTextField)
			return true;

		// UIToolkit: check if a text input element is focused in the active window
		var win = EditorWindow.focusedWindow;
		if (win != null)
		{
			var focused = win.rootVisualElement?.focusController?.focusedElement;
			if (focused is TextField || focused is TextElement { enableRichText: false })
				return true;
			// Also catch the inner TextInput child of TextField
			if (focused?.GetType().Name == "TextInput")
				return true;
		}
		return false;
	}

	static void HandleRecallKeyEvent()
	{
		var evt = Event.current;
		if (evt == null || evt.type != EventType.KeyDown) return;
		if (evt.shift || evt.control || evt.command || evt.alt) return;
		if (IsTextFieldFocused()) return;

		int slot = -1;
		switch (evt.keyCode)
		{
			case KeyCode.Alpha1: slot = 0; break;
			case KeyCode.Alpha2: slot = 1; break;
			case KeyCode.Alpha3: slot = 2; break;
			case KeyCode.Alpha4: slot = 3; break;
			case KeyCode.Alpha5: slot = 4; break;
			case KeyCode.Alpha6: slot = 5; break;
			case KeyCode.Alpha7: slot = 6; break;
			case KeyCode.Alpha8: slot = 7; break;
			case KeyCode.Alpha9: slot = 8; break;
			case KeyCode.Alpha0: slot = 9; break;
		}
		if (slot < 0) return;

		// Only consume the event if this slot actually has a group
		List<string> itemIds = null;
		if (s_instance != null)
			s_instance.selectionGroups.TryGetValue(slot, out itemIds);
		else
		{
			var groups = LoadSelectionGroupsFromPrefsStatic();
			groups.TryGetValue(slot, out itemIds);
		}
		if (itemIds == null || itemIds.Count == 0) return;

		evt.Use(); // Consume the event so no other handler sees it
		DoRecallSelectionGroup(slot);
	}

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
		RebuildRows(isScene: true);
		RebuildRows(isScene: false);
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
				result = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
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

		return null;
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
		       && !AssetDatabase.Contains(obj);
	}

	static bool IsFolder(Object obj)
	{
		var path = AssetDatabase.GetAssetPath(obj);
		return !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path);
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
#endif
