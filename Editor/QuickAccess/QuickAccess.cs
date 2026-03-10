#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

/// <summary>
/// A user-curated quick-access panel for frequently used scene objects and project assets.
/// Two sections: Scene (per-scene, local) and Project (per-project, local).
/// Add items by dragging from Hierarchy/Project. Remove via right-click (undoable).
/// Reorder within a section by dragging (swap). Drag out to use like Hierarchy/Project.
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

	// ─── Prefs Keys ────────────────────────────────────────────

	static string PrefKeyProject => Application.dataPath + "QuickAccess_Project";
	static string PrefKeyScene   => Application.dataPath + "QuickAccess_Scene_" + ActiveSceneName;

	static string ActiveSceneName
	{
		get
		{
			var scene = SceneManager.GetActiveScene();
			return scene.IsValid() ? scene.name : "";
		}
	}

	// ─── Menu ──────────────────────────────────────────────────

	[MenuItem("Tools/Editools/Quick Access")]
	public static QuickAccess ShowWindow()
	{
		var win = GetWindow<QuickAccess>();
		win.titleContent = new GUIContent("Quick Access",
			EditorGUIUtility.IconContent("d_Favorite Icon").image);
		return win;
	}

	// ─── Lifecycle ─────────────────────────────────────────────

	void OnEnable()
	{
		rootVisualElement.styleSheets.Add(Resources.Load<StyleSheet>("QuickAccess"));
		rootVisualElement.Clear();

		BuildLayout();
		LoadFromPrefs();
		RebuildAllRows();

		Undo.undoRedoPerformed += OnUndoRedo;
		Selection.selectionChanged += OnSelectionChanged;
		EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
	}

	void OnDisable()
	{
		Undo.undoRedoPerformed -= OnUndoRedo;
		Selection.selectionChanged -= OnSelectionChanged;
		EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
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
		bool sceneChanged   = false;
		bool projectChanged = false;

		foreach (var obj in DragAndDrop.objectReferences)
		{
			if (obj == null) continue;

			bool objIsScene = IsSceneObject(obj);
			var  ids        = objIsScene ? sceneIds : projectIds;

			string id = ObjectToID(obj);
			if (string.IsNullOrEmpty(id) || ids.Contains(id)) continue;

			if (!sceneChanged && !projectChanged)
				Undo.RecordObject(this, "Add to Quick Access");

			ids.Add(id);
			if (objIsScene) sceneChanged   = true;
			else            projectChanged = true;
		}

		if (sceneChanged)   RebuildRows(isScene: true);
		if (projectChanged) RebuildRows(isScene: false);
		if (sceneChanged || projectChanged) SaveToPrefs();
	}

	// ─── Row Creation ──────────────────────────────────────────

	VisualElement CreateRow(string id, bool isScene)
	{
		var obj = ObjectFromID(id);
		var row = new VisualElement();
		row.AddToClassList("row");
		row.userData = id;

		var title = new VisualElement();
		title.AddToClassList("title");

		var icon = new Image();
		icon.AddToClassList("icon");
		title.Add(icon);

		var label = new Label();
		title.Add(label);

		row.Add(title);
		RefreshRowDetails(row, obj, id);

		// Click to select / double-click to open
		row.RegisterCallback<ClickEvent>(HandleRowClick);

		// Right-click to remove (undoable)
		row.RegisterCallback<ContextClickEvent>(evt =>
		{
			var ids = isScene ? sceneIds : projectIds;
			Undo.RecordObject(this, "Remove from Quick Access");
			ids.Remove(id);
			RebuildRows(isScene);
			SaveToPrefs();
			evt.StopPropagation();
		});

		// Drag initiation — always uses DragAndDrop so it works across windows
		row.RegisterCallback<PointerDownEvent>(evt =>
		{
			if (evt.button != 0) return;
			var o = ObjectFromID(id);
			if (o == null) return;

			dragCandidate = o;
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
			DragAndDrop.objectReferences = new[] { dragCandidate };
			var path = AssetDatabase.GetAssetPath(dragCandidate);
			DragAndDrop.paths = string.IsNullOrEmpty(path) ? new string[0] : new[] { path };

			// Tag as internal reorder so our DragPerform can distinguish
			string sectionTag = isScene ? "scene:" : "project:";
			DragAndDrop.SetGenericData(k_ReorderKey, sectionTag + id);

			DragAndDrop.StartDrag(dragCandidate.name);
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
		var obj = ObjectFromID(id);

		RefreshRowDetails(row, obj, id);
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
		foreach (var row in rows.Children())
		{
			var id = row.userData as string;
			var obj = ObjectFromID(id);
			RefreshRowDetails(row, obj, id);

			if (obj != null && Selection.objects.Contains(obj))
				row.AddToClassList("selected");
			else
				row.RemoveFromClassList("selected");
		}
	}

	// ─── Scene Change ──────────────────────────────────────────

	void OnActiveSceneChanged(Scene oldScene, Scene newScene)
	{
		sceneIds.Clear();
		LoadListFromPrefs(sceneIds, PrefKeyScene);
		RebuildRows(isScene: true);
	}

	// ─── Persistence ───────────────────────────────────────────

	void LoadFromPrefs()
	{
		projectIds.Clear();
		sceneIds.Clear();
		LoadListFromPrefs(projectIds, PrefKeyProject);
		LoadListFromPrefs(sceneIds, PrefKeyScene);
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

	void RefreshRowDetails(VisualElement row, Object obj, string id)
	{
		row.EnableInClassList("unavailable", obj == null);

		var label = row.Q<Label>();
		if (label != null)
			label.text = obj != null ? obj.name : "(Missing)";

		var icon = row.Q<Image>(className: "icon");
		if (icon != null)
			icon.image = GetObjectIcon(obj);
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

		if (id.StartsWith("guid:"))
			return AssetDatabase.LoadAssetAtPath<Object>(
				AssetDatabase.GUIDToAssetPath(id.Substring("guid:".Length)));

		if (id.StartsWith("globalid:"))
		{
			if (GlobalObjectId.TryParse(id.Substring("globalid:".Length), out var gid))
				return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
			return null;
		}

		// Legacy path-based format — kept for backward compatibility
		if (id.StartsWith("gameObject:"))
			return GetGameObjectFromPath(id.Substring("gameObject:".Length));

		if (id.StartsWith("instance:") &&
		    int.TryParse(id.Substring("instance:".Length), out int iid))
			return EditorUtility.InstanceIDToObject(iid);

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
