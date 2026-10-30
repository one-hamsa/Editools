using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>
/// Object Display Toggle. Y cycles the selected objects through display states:
/// Normal, Bounding Box (geometry hidden, the object's bounding box drawn as lines),
/// Greybox (Greybox objects only: geometry hidden, the Greybox's own 12 corner edges drawn) and
/// Outline (geometry hidden, the mesh silhouette drawn as lines). The lines are Handles drawn
/// from duringSceneGui, so they show with Gizmos off. Each object's lines are a Scene View
/// control: clicking a line selects the object (Shift adds, Ctrl toggles), and the lines of a
/// selected object draw in the selection color.
///
/// The shortcut is global, and every other shortcut bound to the same key (Unity's Transform
/// tool on Y) is unbound in the active shortcut profile by <see cref="ClaimShortcutBinding"/>,
/// so the press reaches this tool whichever window has focus.
///
/// Geometry is hidden with Renderer.forceRenderingOff, which is not serialized: nothing is
/// written to the scene and the state lasts until the scene is reloaded. Every state is dropped on
/// domain reload and when entering Play mode. There is no InitializeOnLoad: the shortcut is the
/// entry point, and the Scene View hook is subscribed only while an object is in a non-Normal state.
/// </summary>
static class ObjectDisplayToggle
{
	const string k_ShortcutId = "Editools/Object Display Toggle";
	// Shortcut profile created when the read-only default profile is active and a binding must change.
	const string k_ProfileId = "Editools";
	static readonly int k_ControlHash = "ObjectDisplayToggle".GetHashCode();

	static readonly Color k_BoundingBoxColor = new Color(0.35f, 0.85f, 1f, 0.9f);
	static readonly Color k_GreyboxColor = new Color(1f, 0.8f, 0.35f, 0.9f);
	static readonly Color k_OutlineColor = new Color(0.8f, 0.7f, 1f, 0.9f);
	// Lines of an object that is selected (or under a selected object), in every state.
	static readonly Color k_SelectedColor = new Color(1f, 0.55f, 0.1f, 1f);
	const float k_BoxLineWidth = 2f;
	const float k_SelectedLineWidthAdd = 1f;
	// Alpha multiplier for the pass drawn where other geometry occludes the lines.
	const float k_OccludedAlpha = 0.5f;

	internal enum DisplayState { Normal, BoundingBox, Greybox, Outline }
	const int k_StateCount = 4;

	/// <summary>
	/// Edge adjacency of a mesh for silhouette extraction. An edge is drawn when it borders
	/// a single face (open boundary) or when its two faces differ in camera facing.
	/// </summary>
	sealed class OutlineMesh
	{
		public Transform transform;
		public Vector3[] vertices;
		public Vector3[] faceNormals;
		public Vector3[] faceCentroids;
		public int[] edgeVertexA, edgeVertexB;
		// Second face index is -1 on an open boundary edge.
		public int[] edgeFaceA, edgeFaceB;
		// Per-Layout scratch: camera facing per face, world segments of the silhouette edges.
		public bool[] faceFront;
		public Vector3[] worldSegments;
		// Number of points in use at the front of worldSegments; the tail is padded to zero-length segments.
		public int pointCount;
	}

	sealed class Entry
	{
		public GameObject root;
		public DisplayState state;
		// Only renderers this tool hid; renderers already hidden by another system are left alone.
		public readonly List<Renderer> hiddenRenderers = new();
		public bool hasBounds;
		public Bounds localBounds;
		public Greybox greybox;
		// Bounding Box / Greybox corners in world space (bit0 = +X, bit1 = +Y, bit2 = +Z), refilled on Layout.
		public readonly Vector3[] worldCorners = new Vector3[8];
		public bool hasCorners;
		public readonly List<OutlineMesh> outlineMeshes = new();
		// Scene View control for click selection; reassigned on every event.
		public int controlId;
	}

	static readonly Dictionary<GameObject, Entry> s_entries = new();
	static readonly List<GameObject> s_destroyedRoots = new();
	static bool s_subscribed;
	static bool s_claimScheduled;

	internal static bool Enabled => EditoolsSettings.Current.objectDisplayToggleEnabled;

	/// <summary>Claims the Y binding while enabled and the overlay is active; returns every object to Normal otherwise.</summary>
	internal static void Sync()
	{
		if (EditoolsOverlay.IsActive && Enabled) ScheduleClaimShortcutBinding();
		else ClearAll();
	}

	// ─── Shortcut ────────────────────────────────────────────────

	/// <summary>
	/// Cycles every selected object to the state after the active object's current state, so a
	/// mixed selection lands on one state.
	/// </summary>
	[Shortcut(k_ShortcutId, KeyCode.Y)]
	internal static void CycleSelection()
	{
		if (!EditoolsOverlay.IsActive || !Enabled) return;

		GameObject[] selected = Selection.gameObjects;
		if (selected.Length == 0) return;

		GameObject active = Selection.activeGameObject != null ? Selection.activeGameObject : selected[0];
		DisplayState next = NextState(active, GetState(active));
		foreach (var go in selected)
			SetState(go, NextState(go, next, allowSame: true));
		SceneView.RepaintAll();
	}

	/// <summary>Runs <see cref="ClaimShortcutBinding"/> on the next editor update (ticks in a background editor too).</summary>
	internal static void ScheduleClaimShortcutBinding()
	{
		if (s_claimScheduled) return;
		s_claimScheduled = true;
		EditorApplication.update += ClaimOnce;
	}

	static void ClaimOnce()
	{
		EditorApplication.update -= ClaimOnce;
		s_claimScheduled = false;
		ClaimShortcutBinding();
	}

	/// <summary>
	/// Unbinds every other shortcut that shares this tool's binding, so the press reaches this tool
	/// in every window instead of Unity's Transform tool or a conflict dialog. The default profile
	/// is read-only, so the Editools profile is created and activated when needed. Does nothing
	/// while the tool is disabled or its shortcut is unbound.
	/// </summary>
	internal static void ClaimShortcutBinding()
	{
		if (!EditoolsOverlay.IsActive || !Enabled) return;

		IShortcutManager manager = ShortcutManager.instance;
		ShortcutBinding ours = manager.GetShortcutBinding(k_ShortcutId);
		if (ours.Equals(ShortcutBinding.empty)) return;

		bool conflict = false;
		foreach (string id in manager.GetAvailableShortcutIds())
			if (id != k_ShortcutId && manager.GetShortcutBinding(id).Equals(ours))
			{
				conflict = true;
				break;
			}
		if (!conflict) return;

		if (manager.activeProfileId == ShortcutManager.defaultProfileId)
		{
			bool exists = false;
			foreach (string profile in manager.GetAvailableProfileIds())
				if (profile == k_ProfileId) exists = true;
			if (!exists) manager.CreateProfile(k_ProfileId);
			manager.activeProfileId = k_ProfileId;
		}

		foreach (string id in manager.GetAvailableShortcutIds())
		{
			if (id == k_ShortcutId || !manager.GetShortcutBinding(id).Equals(ours)) continue;
			manager.RebindShortcut(id, ShortcutBinding.empty);
			Debug.Log($"[ObjectDisplayToggle] Unbound shortcut '{id}' ({ours}) in profile '{manager.activeProfileId}' so the key reaches Object Display Toggle.");
		}
	}

	/// <summary>The next state after <paramref name="current"/> that applies to the object (Greybox only for Greybox objects).</summary>
	static DisplayState NextState(GameObject go, DisplayState current, bool allowSame = false)
	{
		var state = current;
		if (!allowSame || !Applies(go, state))
		{
			do state = (DisplayState)(((int)state + 1) % k_StateCount);
			while (!Applies(go, state));
		}
		return state;
	}

	static bool Applies(GameObject go, DisplayState state)
		=> state != DisplayState.Greybox || go.GetComponent<Greybox>() != null;

	internal static DisplayState GetState(GameObject go)
		=> s_entries.TryGetValue(go, out var entry) ? entry.state : DisplayState.Normal;

	internal static void SetState(GameObject go, DisplayState state)
	{
		if (s_entries.TryGetValue(go, out var entry))
			RestoreRenderers(entry);

		if (state == DisplayState.Normal)
		{
			s_entries.Remove(go);
			UpdateSubscription();
			return;
		}

		if (entry == null)
		{
			entry = new Entry { root = go };
			s_entries[go] = entry;
		}

		entry.state = state;
		HideRenderers(entry);
		switch (state)
		{
			case DisplayState.BoundingBox: ComputeLocalBounds(entry); break;
			case DisplayState.Greybox: entry.greybox = go.GetComponent<Greybox>(); break;
			case DisplayState.Outline: BuildOutlineMeshes(entry); break;
		}
		UpdateSubscription();
	}

	/// <summary>Returns every object to Normal.</summary>
	internal static void ClearAll()
	{
		foreach (var entry in s_entries.Values)
			RestoreRenderers(entry);
		s_entries.Clear();
		UpdateSubscription();
		SceneView.RepaintAll();
	}

	// ─── Renderer visibility ─────────────────────────────────────

	static void HideRenderers(Entry entry)
	{
		foreach (var renderer in entry.root.GetComponentsInChildren<Renderer>(true))
		{
			if (renderer.forceRenderingOff) continue;
			renderer.forceRenderingOff = true;
			entry.hiddenRenderers.Add(renderer);
		}
	}

	static void RestoreRenderers(Entry entry)
	{
		foreach (var renderer in entry.hiddenRenderers)
		{
			// Legitimate filter: the renderer may have been destroyed since it was hidden.
			if (renderer == null) continue;
			renderer.forceRenderingOff = false;
		}
		entry.hiddenRenderers.Clear();
		entry.outlineMeshes.Clear();
		entry.greybox = null;
		entry.hasBounds = false;
		entry.hasCorners = false;
	}

	// ─── Bounding box ────────────────────────────────────────────

	/// <summary>
	/// Encloses the mesh-local bounds of every child MeshFilter / SkinnedMeshRenderer in the
	/// root's local space, so the box follows the root transform while drawn.
	/// </summary>
	static void ComputeLocalBounds(Entry entry)
	{
		Transform root = entry.root.transform;
		entry.hasBounds = false;

		foreach (var meshFilter in entry.root.GetComponentsInChildren<MeshFilter>(true))
		{
			// Legitimate filter: a MeshFilter with no mesh assigned contributes nothing.
			if (meshFilter.sharedMesh == null) continue;
			EncapsulateLocalBoundsCorners(entry, root, meshFilter.transform, meshFilter.sharedMesh.bounds);
		}

		foreach (var skinned in entry.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
			EncapsulateLocalBoundsCorners(entry, root, skinned.transform, skinned.localBounds);
	}

	static void EncapsulateLocalBoundsCorners(Entry entry, Transform root, Transform child, Bounds meshBounds)
	{
		Vector3 center = meshBounds.center;
		Vector3 extents = meshBounds.extents;

		for (int ci = 0; ci < 8; ci++)
		{
			Vector3 corner = center + new Vector3(
				(ci & 1) != 0 ? extents.x : -extents.x,
				(ci & 2) != 0 ? extents.y : -extents.y,
				(ci & 4) != 0 ? extents.z : -extents.z);
			Vector3 rootLocal = root.InverseTransformPoint(child.TransformPoint(corner));

			if (!entry.hasBounds)
			{
				entry.localBounds = new Bounds(rootLocal, Vector3.zero);
				entry.hasBounds = true;
			}
			else
				entry.localBounds.Encapsulate(rootLocal);
		}
	}

	static void UpdateBoundingBoxCorners(Entry entry)
	{
		entry.hasCorners = entry.hasBounds;
		if (!entry.hasBounds) return;

		Matrix4x4 localToWorld = entry.root.transform.localToWorldMatrix;
		Vector3 center = entry.localBounds.center;
		Vector3 extents = entry.localBounds.extents;
		for (int ci = 0; ci < 8; ci++)
			entry.worldCorners[ci] = localToWorld.MultiplyPoint3x4(center + new Vector3(
				(ci & 1) != 0 ? extents.x : -extents.x,
				(ci & 2) != 0 ? extents.y : -extents.y,
				(ci & 4) != 0 ? extents.z : -extents.z));
	}

	// Corners are read each Layout so the lines follow Greybox edits (same bit encoding as the bounding box).
	static void UpdateGreyboxCorners(Entry entry)
	{
		// Legitimate filter: the Greybox component may have been removed since the state was entered.
		entry.hasCorners = entry.greybox != null;
		if (entry.hasCorners)
			entry.greybox.GetWorldCorners(entry.worldCorners);
	}

	/// <summary>Draws the 12 edges of a box whose corners use the bit0 = +X, bit1 = +Y, bit2 = +Z encoding.</summary>
	static void DrawBoxEdges(Vector3[] corners, float width)
	{
		// An edge joins two corners that differ in exactly one bit.
		for (int ci = 0; ci < 8; ci++)
			for (int bit = 1; bit <= 4; bit <<= 1)
				if ((ci & bit) == 0)
					Handles.DrawLine(corners[ci], corners[ci | bit], width);
	}

	static float DistanceToBoxEdges(Vector3[] corners)
	{
		float min = float.MaxValue;
		for (int ci = 0; ci < 8; ci++)
			for (int bit = 1; bit <= 4; bit <<= 1)
				if ((ci & bit) == 0)
					min = Mathf.Min(min, HandleUtility.DistanceToLine(corners[ci], corners[ci | bit]));
		return min;
	}

	// ─── Outline ─────────────────────────────────────────────────

	static void BuildOutlineMeshes(Entry entry)
	{
		foreach (var meshFilter in entry.root.GetComponentsInChildren<MeshFilter>(true))
		{
			// Legitimate filter: a MeshFilter with no mesh assigned contributes nothing.
			if (meshFilter.sharedMesh == null) continue;
			AddOutlineMesh(entry, meshFilter.transform, meshFilter.sharedMesh);
		}

		// Skinned meshes are drawn in bind pose through the renderer transform.
		foreach (var skinned in entry.root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
		{
			if (skinned.sharedMesh == null) continue;
			AddOutlineMesh(entry, skinned.transform, skinned.sharedMesh);
		}
	}

	/// <summary>
	/// Builds the edge adjacency of a mesh: faces with their normals and centroids, and the
	/// unique edges with up to two adjacent faces each. Vertices are merged by position so edges
	/// shared across split vertices (hard normals, UV seams) are one edge.
	/// </summary>
	static void AddOutlineMesh(Entry entry, Transform transform, Mesh mesh)
	{
		Vector3[] vertices = mesh.vertices;
		int[] remap = BuildPositionRemap(vertices);

		var faceNormals = new List<Vector3>();
		var faceCentroids = new List<Vector3>();
		// Edge key -> (first face, second face or -1). Faces beyond the second are ignored.
		var edgeFaces = new Dictionary<long, (int a, int b)>();

		for (int sub = 0; sub < mesh.subMeshCount; sub++)
		{
			if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
			int[] triangles = mesh.GetTriangles(sub);
			for (int i = 0; i + 2 < triangles.Length; i += 3)
			{
				int a = remap[triangles[i]], b = remap[triangles[i + 1]], c = remap[triangles[i + 2]];
				Vector3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
				int face = faceNormals.Count;
				faceNormals.Add(Vector3.Cross(pb - pa, pc - pa).normalized);
				faceCentroids.Add((pa + pb + pc) / 3f);
				AddEdgeFace(edgeFaces, EdgeKey(a, b), face);
				AddEdgeFace(edgeFaces, EdgeKey(b, c), face);
				AddEdgeFace(edgeFaces, EdgeKey(c, a), face);
			}
		}
		if (edgeFaces.Count == 0) return;

		var outline = new OutlineMesh
		{
			transform = transform,
			vertices = vertices,
			faceNormals = faceNormals.ToArray(),
			faceCentroids = faceCentroids.ToArray(),
			edgeVertexA = new int[edgeFaces.Count],
			edgeVertexB = new int[edgeFaces.Count],
			edgeFaceA = new int[edgeFaces.Count],
			edgeFaceB = new int[edgeFaces.Count],
			faceFront = new bool[faceNormals.Count],
			worldSegments = new Vector3[edgeFaces.Count * 2],
		};
		int n = 0;
		foreach (var kvp in edgeFaces)
		{
			outline.edgeVertexA[n] = (int)(kvp.Key >> 32);
			outline.edgeVertexB[n] = (int)(kvp.Key & 0xFFFFFFFF);
			outline.edgeFaceA[n] = kvp.Value.a;
			outline.edgeFaceB[n] = kvp.Value.b;
			n++;
		}
		entry.outlineMeshes.Add(outline);
	}

	/// <summary>Maps every vertex index to the first vertex index at the same position.</summary>
	static int[] BuildPositionRemap(Vector3[] vertices)
	{
		var canonicalIndex = new Dictionary<Vector3, int>(vertices.Length);
		var remap = new int[vertices.Length];
		for (int i = 0; i < vertices.Length; i++)
		{
			if (!canonicalIndex.TryGetValue(vertices[i], out int canonical))
			{
				canonical = i;
				canonicalIndex.Add(vertices[i], i);
			}
			remap[i] = canonical;
		}
		return remap;
	}

	// Order-independent key for an edge between two vertex indices; the degenerate a == b edge keys to -1.
	static long EdgeKey(int a, int b)
	{
		if (a == b) return -1;
		int lo = a < b ? a : b, hi = a < b ? b : a;
		return ((long)lo << 32) | (uint)hi;
	}

	static void AddEdgeFace(Dictionary<long, (int a, int b)> edgeFaces, long key, int face)
	{
		if (key < 0) return;
		if (!edgeFaces.TryGetValue(key, out var faces))
			edgeFaces[key] = (face, -1);
		else if (faces.b < 0)
			edgeFaces[key] = (faces.a, face);
	}

	/// <summary>
	/// Collects the silhouette into each mesh's world segments: open boundary edges, and edges
	/// whose two faces differ in camera facing. Facing is evaluated in mesh-local space.
	/// </summary>
	static void UpdateOutline(Entry entry, Camera camera)
	{
		foreach (var outline in entry.outlineMeshes)
		{
			outline.pointCount = 0;
			// Legitimate filter: the child may have been destroyed since the state was entered.
			if (outline.transform == null) continue;

			Matrix4x4 localToWorld = outline.transform.localToWorldMatrix;
			Matrix4x4 worldToLocal = outline.transform.worldToLocalMatrix;
			Vector3 cameraLocal = worldToLocal.MultiplyPoint3x4(camera.transform.position);
			Vector3 viewDirLocal = worldToLocal.MultiplyVector(camera.transform.forward);

			for (int f = 0; f < outline.faceNormals.Length; f++)
			{
				Vector3 toCamera = camera.orthographic ? -viewDirLocal : cameraLocal - outline.faceCentroids[f];
				outline.faceFront[f] = Vector3.Dot(outline.faceNormals[f], toCamera) > 0f;
			}

			int n = 0;
			for (int e = 0; e < outline.edgeVertexA.Length; e++)
			{
				int faceB = outline.edgeFaceB[e];
				bool silhouette = faceB < 0 || outline.faceFront[outline.edgeFaceA[e]] != outline.faceFront[faceB];
				if (!silhouette) continue;
				outline.worldSegments[n++] = localToWorld.MultiplyPoint3x4(outline.vertices[outline.edgeVertexA[e]]);
				outline.worldSegments[n++] = localToWorld.MultiplyPoint3x4(outline.vertices[outline.edgeVertexB[e]]);
			}
			outline.pointCount = n;
			if (n == 0) continue;

			// Unused tail of the scratch array collapses to zero-length segments at the last point.
			Vector3 last = outline.worldSegments[n - 1];
			for (int i = n; i < outline.worldSegments.Length; i++)
				outline.worldSegments[i] = last;
		}
	}

	static void DrawOutline(Entry entry)
	{
		foreach (var outline in entry.outlineMeshes)
		{
			if (outline.pointCount == 0) continue;
			Handles.DrawLines(outline.worldSegments);
		}
	}

	static float DistanceToOutline(Entry entry)
	{
		float min = float.MaxValue;
		foreach (var outline in entry.outlineMeshes)
			for (int i = 0; i + 1 < outline.pointCount; i += 2)
				min = Mathf.Min(min, HandleUtility.DistanceToLine(outline.worldSegments[i], outline.worldSegments[i + 1]));
		return min;
	}

	// ─── Scene View hook ─────────────────────────────────────────

	static void UpdateSubscription()
	{
		bool wanted = s_entries.Count > 0;
		if (wanted == s_subscribed) return;
		s_subscribed = wanted;

		if (wanted)
		{
			SceneView.duringSceneGui += OnSceneGUI;
			AssemblyReloadEvents.beforeAssemblyReload += ClearAll;
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		}
		else
		{
			SceneView.duringSceneGui -= OnSceneGUI;
			AssemblyReloadEvents.beforeAssemblyReload -= ClearAll;
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
		}
	}

	// forceRenderingOff would carry into Play mode when domain reload is disabled.
	static void OnPlayModeStateChanged(PlayModeStateChange change)
	{
		if (change == PlayModeStateChange.ExitingEditMode) ClearAll();
	}

	static void OnSceneGUI(SceneView view)
	{
		Event e = Event.current;

		// Control ids must be requested in the same order on every event.
		foreach (var entry in s_entries.Values)
			entry.controlId = GUIUtility.GetControlID(k_ControlHash, FocusType.Passive);

		switch (e.type)
		{
			case EventType.Layout:
				PruneDestroyedRoots();
				foreach (var entry in s_entries.Values)
				{
					UpdateWorldLines(entry, view.camera);
					HandleUtility.AddControl(entry.controlId, DistanceToLines(entry));
				}
				break;

			case EventType.Repaint:
				DrawAll();
				break;

			case EventType.MouseDown:
			case EventType.MouseDrag:
			case EventType.MouseUp:
				HandleMouse(e);
				break;
		}
	}

	// World-space lines are refreshed once per Layout and reused by the Repaint and the picking distance.
	static void UpdateWorldLines(Entry entry, Camera camera)
	{
		switch (entry.state)
		{
			case DisplayState.BoundingBox: UpdateBoundingBoxCorners(entry); break;
			case DisplayState.Greybox: UpdateGreyboxCorners(entry); break;
			case DisplayState.Outline: UpdateOutline(entry, camera); break;
		}
	}

	static float DistanceToLines(Entry entry)
	{
		switch (entry.state)
		{
			case DisplayState.BoundingBox:
			case DisplayState.Greybox:
				return entry.hasCorners ? DistanceToBoxEdges(entry.worldCorners) : float.MaxValue;
			case DisplayState.Outline: return DistanceToOutline(entry);
		}
		return float.MaxValue;
	}

	static void DrawAll()
	{
		CompareFunction prevZTest = Handles.zTest;
		// Visible pass at full opacity, then the occluded pass at reduced opacity.
		Handles.zTest = CompareFunction.LessEqual;
		DrawEntries(1f);
		Handles.zTest = CompareFunction.Greater;
		DrawEntries(k_OccludedAlpha);
		Handles.zTest = prevZTest;
	}

	static void DrawEntries(float alpha)
	{
		foreach (var entry in s_entries.Values)
		{
			bool selected = IsSelected(entry.root);
			Handles.color = (selected ? k_SelectedColor : StateColor(entry.state)) * new Color(1f, 1f, 1f, alpha);
			float boxWidth = k_BoxLineWidth + (selected ? k_SelectedLineWidthAdd : 0f);
			switch (entry.state)
			{
				case DisplayState.BoundingBox:
				case DisplayState.Greybox:
					if (entry.hasCorners) DrawBoxEdges(entry.worldCorners, boxWidth);
					break;
				case DisplayState.Outline: DrawOutline(entry); break;
			}
		}
	}

	static Color StateColor(DisplayState state)
	{
		switch (state)
		{
			case DisplayState.BoundingBox: return k_BoundingBoxColor;
			case DisplayState.Greybox: return k_GreyboxColor;
			default: return k_OutlineColor;
		}
	}

	// Selected like Unity's own outline: the object itself or any ancestor is in the selection.
	static bool IsSelected(GameObject go)
	{
		for (Transform t = go.transform; t != null; t = t.parent)
			if (Selection.Contains(t.gameObject)) return true;
		return false;
	}

	/// <summary>
	/// Click selection on the lines. Capturing hotControl on MouseDown also stops Unity's own
	/// Scene View picking for that click, so a release on a line selects only this object:
	/// plain click sets the selection, Shift adds, Ctrl/Cmd toggles. Alt is left to camera orbit.
	/// </summary>
	static void HandleMouse(Event e)
	{
		foreach (var entry in s_entries.Values)
		{
			int id = entry.controlId;
			switch (e.type)
			{
				case EventType.MouseDown:
					if (e.button != 0 || e.alt || HandleUtility.nearestControl != id) break;
					GUIUtility.hotControl = id;
					e.Use();
					return;

				case EventType.MouseDrag:
					if (GUIUtility.hotControl != id) break;
					e.Use();
					return;

				case EventType.MouseUp:
					if (GUIUtility.hotControl != id || e.button != 0) break;
					GUIUtility.hotControl = 0;
					ApplyClickSelection(entry.root, e);
					e.Use();
					return;
			}
		}
	}

	static void ApplyClickSelection(GameObject root, Event e)
	{
		if (e.shift)
		{
			if (!Selection.Contains(root)) AddToSelection(root);
		}
		else if (EditorGUI.actionKey)
		{
			if (Selection.Contains(root)) RemoveFromSelection(root);
			else AddToSelection(root);
		}
		else
			Selection.activeGameObject = root;
	}

	static void AddToSelection(GameObject go)
	{
		Object[] current = Selection.objects;
		var objects = new Object[current.Length + 1];
		current.CopyTo(objects, 0);
		objects[current.Length] = go;
		Selection.objects = objects;
	}

	static void RemoveFromSelection(GameObject go)
	{
		Object[] current = Selection.objects;
		var objects = new List<Object>(current.Length);
		foreach (var o in current)
			if (o != go) objects.Add(o);
		Selection.objects = objects.ToArray();
	}

	// Roots die on scene reload / delete; their entries go with them (nothing to restore).
	static void PruneDestroyedRoots()
	{
		foreach (var kvp in s_entries)
			if (kvp.Key == null)
				s_destroyedRoots.Add(kvp.Key);
		if (s_destroyedRoots.Count == 0) return;

		foreach (var root in s_destroyedRoots)
			s_entries.Remove(root);
		s_destroyedRoots.Clear();
		UpdateSubscription();
	}
}
