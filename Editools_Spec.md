# Editools — System Spec

## Overview

A git submodule (`Assets/Standard Assets/Editools/`) providing a collection of
Unity editor productivity tools. Core infrastructure includes a centralized material
override system used by multiple debug modes across the project, a toolbar overlay
with screenshot capture, and a set of scene editing accelerators.

---

## File Map

### Runtime
- `Runtime/MaterialOverride/SceneMaterialOverride.cs` — centralized scene material swapping
- `Runtime/Editools.asmdef` — runtime assembly, all platforms

### Editor
- `Editor/Overlay/EditoolsOverlay.cs` — toolbar overlay, screenshot capture, button strip
- `Editor/HierarchyHeatmap/HierarchyHeatmap.cs` — recent-selection heatmap in Hierarchy
- `Editor/QuickTransform/QuickTransform.cs` — hold W/E/R + drag transform tool
- `Editor/QuickTransform/QuickTransformConfig.cs` — per-project config SO for QuickTransform
- `Editor/SnapToSurface/SnapToSurface.cs` — Alt+A snap-to-surface mode
- `Editor/SceneCameraUndo/SceneCameraUndo.cs` — Shift+Z/Y scene camera undo/redo
- `Editor/CopyPasteTransform/CopyPasteTransformComponent.cs` — Alt+Ctrl+C/V/X transform clipboard
- `Editor/MassRename/MassRename.cs` — F2 batch rename for multiple selected objects/assets
- `Editor/SelectMaterial/SelectMaterial.cs` — I+click to select material under cursor
- `Editor/SceneViewFpsCounter/SceneViewFpsCounter.cs` — per-SceneView FPS overlay
- `Editor/Editools.Editor.asmdef` — editor assembly, references Editools runtime

### Integration (outside submodule)
- `Assets/Scripts/Rendering/Editor/EditoolsDefineSync.cs` — auto-syncs `HAS_EDITOOLS` define

---

## Components

### 1. SceneMaterialOverride (Static Class, Runtime)

Centralized system for swapping all scene materials to a debug shader. Used by
LocalGlobals Edit Mode, AO Debug Mode, and the Editools toolbar "Material Check" button.

**API:**
- `Enter(Material mat, string modeName, Action exitCallback)` — activates override
- `Exit()` — self-exit (no callback invoked)
- `SwapMaterial(Material newMat)` — hot-swap the override material while staying in the same mode

**Mutual Exclusivity:** Only one override mode can be active at a time. Calling `Enter`
while another mode is active will force-exit the previous mode (invoking its callback),
then enter the new one. This prevents debug mode conflicts.

**Material Backup:**
- On enter: iterates all `MeshRenderer` and `SkinnedMeshRenderer` in the scene
- Stores each renderer's `sharedMaterials` array in a dictionary keyed by renderer instance
- Skips renderers with `shadowCastingMode == ShadowsOnly` or `renderQueue >= 3000` (transparent)
- Replaces all material slots with the debug material

**Safety Events:**
- `playModeStateChanged` → restores materials before entering/exiting play mode
- `beforeAssemblyReload` → restores before domain reload
- `sceneSaving` → restores before save, `sceneSaved` → re-applies after save
- These ensure scene files are never saved with debug materials baked in

**Consumers (via `HAS_EDITOOLS` guard):**
- `LocalGlobalsEditMode` → enters with `Hidden/LocalGlobals/Debug`
- `AODebugMode` → enters with `Hidden/AO/Debug`
- `EditoolsMaterialCheckButton` → enters with user-dragged material ("MaterialCheck" mode)

### 2. EditoolsOverlay (Editor, ToolbarOverlay)

A `ToolbarOverlay` docked in SceneView. Contains multiple toolbar elements:

**EditoolsLayoutCleaner** (`[InitializeOnLoad]`):
- Runs on startup, removes stale/duplicate overlay entries from `.dwlt` layout files
- Prevents `ToolbarOverlay` from accumulating duplicate instances across editor restarts
- Ensures the toolbar displays in toolbar mode (not panel/popup)

**Screenshot Capture System:**
- Captures scene view screenshots (up to 10 stored)
- Persists across domain reloads via Library/ PNG files + `SessionState` metadata
- Screenshot strip shows thumbnails; click to view full-size comparison
- View/Compare popup: side-by-side current view vs captured screenshot
- Comment system: optional text annotations on screenshots

**EditoolsMaterialCheckButton:**
- Drag-and-drop material slot + On/Off toggle
- When toggled on: calls `SceneMaterialOverride.Enter` with "MaterialCheck" mode
- Allows artists to preview any material applied to all scene geometry

**EditoolsHeatmapButton:**
- Toggle + dropdown for Hierarchy Heatmap settings

**EditoolsQuickTransformButton:**
- Toggle + dropdown popup for QuickTransform settings
- Shows current configuration (edge hover threshold, rotation snap, linear mode)

### 3. HierarchyHeatmap (Editor, `[InitializeOnLoad]`)

Color-codes Hierarchy and Project window items based on recency of selection.

**Heat Tiers:** 5 tiers with configurable colors (orange → red gradient by default).
Most recently selected items are brightest.

**Mark System:**
- `Alt+D` — toggle mark on selected item (persistent highlight)
- `Alt+Shift+D` — clear all marks
- Marks persist via `EditorPrefs` (keyed by object instance ID or asset GUID)

**Parent Propagation:**
- Uses reflection into Unity's internal `TreeViewController` to access collapsed state
- When a child item has heat/marks but its parent is collapsed, the heat color
  propagates up to the parent row so hot items remain visible

**Settings:** `HeatmapSettingsWindow` provides configuration for colors, tier
durations, and enable/disable toggles. All stored in `EditorPrefs`.

### 4. QuickTransform (Editor, `[InitializeOnLoad]`)

Hold W/E/R + drag anywhere in Scene View to move/rotate/scale without gizmo handles.
Standard tool switching (tap W/E/R) still works normally — the hold-and-drag only
activates after a configurable delay (`ModeKeyDelaySec = 0.1s`).

**Bounding Box:**
- Single selection: OBB aligned to object's local axes (encompasses all child MeshRenderers
  and SkinnedMeshRenderers via mesh-local bounds, not inflated renderer.bounds)
- Multi-selection: AABB in world space
- Minimum extent clamped to 0.05 to prevent degenerate boxes

**W = Move:**

| Hover | LMB | RMB |
|---|---|---|
| Outside box | World XZ plane movement | — |
| Face | Movement locked to face plane | Movement locked along face normal |

**E = Rotate:**

| Hover | Behavior |
|---|---|
| Outside box | Rotate around world Y, center pivot |
| Face | Rotate around face normal, pivot = face center |
| Edge | Rotate around edge direction, pivot = grab point on edge |

- Ctrl held → snap to configurable increment (default 15°)
- Two rotation modes: **Radial** (default, angle follows mouse around pivot) and
  **Linear** (horizontal screen-space delta maps to degrees)
- Edge rotation warps cursor to circle perimeter (Windows only, via Win32 `SetCursorPos`)

**R = Scale:**

| Hover | Behavior |
|---|---|
| Side face (by screen quadrant) | Scale along face normal, anchor = opposite face |
| Y face (top/bottom) | Scale along Y, anchor = opposite Y face |

- Opposite face stays pinned via post-scale position correction
- Scale mapped to best-matching local axis via `GetBestLocalAxis`

**Shift + W/E/R:** Duplicate selection first, then transform the duplicates.

**Settings (EditorPrefs-backed):**

| Setting | Key | Default |
|---|---|---|
| Enabled | `QuickTransform_Enabled` | true |
| Edge hover threshold | `QuickTransform_EdgeHoverPx` | 10px |
| Circle radius (rotation gizmo) | `QuickTransform_CircleRadius` | 1.2 |
| Linear rotation mode | `QuickTransform_LinearRot` | false |
| Linear rotation sensitivity | `QuickTransform_LinearRotSens` | 0.5 |
| Rotation snap angle | `QuickTransform_RotSnapAngle` | 15° |

**QuickTransformConfig (ScriptableObject):**
- `CreateAssetMenu`: "BlockBuster/Quick Transform Config"
- Only holds `upAxis` (Vector3, default `Vector3.up`)
- Looked up via `AssetDatabase.FindAssets("t:QuickTransformConfig")` with 1s retry cooldown

**State Machine:** `Phase.Idle` → `Phase.Ready` (mouse down, waiting for drag threshold of 3px) → `Phase.Dragging` (actively transforming)

**Undo:** Full undo support via `Undo.RegisterCompleteObjectUndo` + `CollapseUndoOperations`.
Mid-operation undo/redo resets to `Phase.Idle` to prevent stale snapshot application.

**Visual Feedback:**
- Wireframe box with semi-transparent face highlights (color-coded by mode)
- Scale faces colored by axis (R/G/B for X/Y/Z)
- Rotation: circle gizmo + reference line + dynamic angle line, degree label when Ctrl-snapping
- World-space gizmo preview when hovering outside the box (shows XZ plane for move, Y disc for rotate)

### 5. SnapToSurface (Editor, `Alt+A`)

Enters a modal snap mode for placing objects on surfaces.

**Workflow:**
1. Select object(s), press `Alt+A`
2. Mouse tracking begins — cursor raycasts against all scene `MeshFilter` geometry
3. Uses Möller–Trumbore triangle intersection (not Physics raycasts) for precision
4. Object preview follows the mouse, aligned to surface normal
5. Left-click confirms placement
6. Right-click cancels (performs `Undo.PerformUndo` to revert)

### 6. SelectMaterial (Editor, `I+Click`)

Hold I and left-click in the Scene View to select the material of the renderer
under the cursor (first material slot). The material is pinged in the Project window.

- `I + LMB` — select material (replaces selection)
- `I + Shift + LMB` — add material to current selection
- Custom white eyedropper cursor with black outline shown while I is held
- Uses `HandleUtility.PickGameObject` (GPU picking, no mesh raycasting)
- Toggle on/off via Editools Settings checkbox (`EditorPrefs`: `Editools_SelectMaterial_Enabled`, default true)

### 7. SceneCameraUndo (Editor, `Shift+Z` / `Shift+Y`)

Undo/redo for Scene View camera position.

- `Shift+Z` — undo camera move
- `Shift+Y` — redo camera move
- Per-SceneView history stacks (max 32 entries)
- Settle delay of 0.15s before committing camera state (avoids recording every frame during orbit)
- Stores: pivot, rotation, size, orthographic mode

### 8. CopyPasteTransformComponent (Editor)

Transform clipboard operations:
- `Alt+Ctrl+C` — copy local position, rotation, scale
- `Alt+Ctrl+V` — paste (with undo support)
- `Alt+Ctrl+X` — reset transform to identity (with undo support)

### 9. MassRename (Editor, `F2`)

Batch-rename multiple selected objects (Hierarchy) or assets (Project view).
Triggered by `F2` when two or more items are selected. Single-item F2 falls
through to Unity's default inline rename.

**Workflow:**
1. Select multiple items in Hierarchy or Project view
2. Press F2 → Mass Rename window opens
3. Configure rename settings → live preview updates
4. Click Rename → changes applied with full Undo support

**Settings (applied in order on original alphabetical sort):**

| Setting | Description | Default |
|---|---|---|
| Replace A with B | Find/replace with glob wildcard support (`*` = any chars) | Auto-detected shared prefix |
| Remove Prefix | Trim N characters from start | 0 |
| Remove Suffix | Trim N characters from end | 0 |
| Add Numbering | Append zero-padded index (by count of items) | Off |

**Order of operations:**
1. Sort items alphabetically (original names)
2. Apply find/replace (step 1)
3. Apply prefix removal (step 2)
4. Apply suffix removal (step 3)
5. Re-sort alphabetically by new names
6. Apply numbering (step 4) — zero-padded to fit item count (e.g. 001 for 453 items)

**Auto-prefix detection:** On open, scans selected names for the longest shared
prefix, trimmed to the last word boundary (space, underscore, dash). Pre-fills
both find and replace fields. If no shared prefix exists, both fields start empty.

**Undo:** All renames (both hierarchy and asset) are grouped into a single undo
operation via `Undo.CollapseUndoOperations`. Asset renames use
`AssetDatabase.RenameAsset` wrapped in `StartAssetEditing/StopAssetEditing`.

**EditorPrefs:** `MassRename_Enabled` (default: true). Toggle in Editools overlay.

### 10. QuickAccess (EditorWindow)

User-curated quick-access panel for frequently used scene objects and project assets.
Opens via `Tools > Editools > Quick Access`.

**Two sections:**
- **Scene** — references to GameObjects in the active scene. Saved per-scene, local.
- **Project** — references to files, folders, and assets. Saved per-project, local.

**Interactions:**
- Drag from Hierarchy/Project → drops anywhere in the window, auto-classified into the
  appropriate section (bottom of list). Root-level handler catches external drops;
  section handlers only handle internal reorder (with `StopPropagation`).
  Multiple objects dragged together → single multi-object item ("N Objects").
- Click → selects and pings the object. Multi-object items: selects all objects.
  Folders: navigates the Project window (selects folder in left tree with yellow ping,
  shows contents in right column) via reflection into
  `ProjectBrowser.ShowFolderContents` + `m_FolderTree.Frame(id, true, ping: true)`.
  `PingObject` is avoided — it navigates to the parent folder instead.
- Double-click → opens the asset (`AssetDatabase.OpenAsset`)
- Right-click → removes from list (undoable). Also removes associated selection group.
- Drag within section → swaps position with hovered item (two-item swap, not list shift)
- Drag outside window → standard Unity `DragAndDrop` (like dragging from Hierarchy/Project)
- Ctrl/Cmd+click → additive selection (multi items: toggle all objects in/out)

**Multi-Object Items:**
- ID format: `multi:subid1|subid2|subid3` where each sub-id uses standard encoding
- Display: italic "N Objects" label with first object's icon; tooltip lists first 5 names
- Created when: dragging multiple objects to QuickAccess, or saving a selection group (Ctrl+N)

**Selection Groups (0-9):**
QuickAccess owns its own selection groups — no interaction with Unity's built-in
Save/Load Selection. Groups are stored directly in EditorPrefs (no probing, no undo pollution).
- **Ctrl+1..0** → save current `Selection.objects` to group slot 1-10
  - Creates multi-object item if multiple objects selected
  - Mixed selections (scene + project) split into two items, both get same badge
  - Automatically adds item(s) to QuickAccess lists
  - Clearing an empty selection on a slot removes the group
- **1..0** → recall group (sets `Selection.objects`). Suppressed when editing text fields.
- Both shortcuts work globally via `[Shortcut]` attributes, even when QuickAccess is closed.
- Badge display: slot 0→"1", slot 8→"9", slot 9→"0" (matches keyboard layout).
- Deleting a QuickAccess item also removes its selection group.

**Persistence:**
- `EditorPrefs` keyed with `Application.dataPath` prefix (per-project, per-machine, git-ignored)
- Scene list key: `Application.dataPath + "QuickAccess_Scene_" + sceneName`
- Project list key: `Application.dataPath + "QuickAccess_Project"`
- Selection groups key: `Application.dataPath + "QuickAccess_SelectionGroups_V2"`
  Format: `slot=itemId1\titemId2;slot=itemId3` (tab separates items per slot, semicolon between slots)
- Scene objects stored via `GlobalObjectId` (survives renames — uses scene GUID + local file ID).
  Falls back to path-based `gameObject:` format for unserialized objects. Project assets use
  `guid:` prefix (same as LRUAssets). Legacy `gameObject:` entries still resolve for backward compat.

**Scene lifecycle:** Scene list reloads on `EditorSceneManager.activeSceneChangedInEditMode`.
On scene change, old scene data is defensively saved under its explicit key before clearing,
and `Undo.ClearUndo(this)` is called to prevent stale undo records from writing old-scene
items to the new scene's prefs key.

**Design constraints:**
- `IsSceneObject` must reject prefab stage objects (same guard as `ObjectToID`) —
  otherwise they get `instance:` IDs that die on domain reload.
- Static code paths (e.g. `SaveSelectionGroupWithoutWindow`) must capture `PrefKeyScene`
  once at entry — the computed property depends on the active scene at call time, which
  can change mid-frame in edge cases.

**Files:**
- `Editor/QuickAccess/QuickAccess.cs`
- `Editor/QuickAccess/Resources/QuickAccess.uss`

### 11. SceneViewFpsCounter (Editor, `[InitializeOnLoad]`)

Live FPS overlay in the top-left corner of each Scene View.

- Hooks into `SceneView.duringSceneGui`, samples delta time on `EventType.Repaint` only
- Per-SceneView state via `Dictionary<int, FrameData>` keyed by `sceneView.GetInstanceID()`
- Rolling average over 60 samples, displays average FPS and frame time (ms)
- Rendered as IMGUI label inside `Handles.BeginGUI()` with semi-transparent black background
- Toggle on/off via Editools Settings checkbox (`EditorPrefs`: `SceneViewFpsCounter_Enabled`, default false)
- Continuously calls `sceneView.Repaint()` to keep the counter updating

---

## Integration: HAS_EDITOOLS Define

`Assets/Scripts/Rendering/Editor/EditoolsDefineSync.cs` (`[InitializeOnLoad]`):
- Checks if the "Editools" assembly exists via `CompilationPipeline.GetAssemblies()`
- If found and `HAS_EDITOOLS` is not in scripting defines → adds it
- If not found and `HAS_EDITOOLS` is present → removes it
- This allows project code to use `#if HAS_EDITOOLS` guards for Editools-dependent
  features (like `SceneMaterialOverride` calls in `LocalGlobalsEditMode` and `AODebugMode`)
  without hard assembly references that would break if the submodule is absent

---

## Key Implementation Details

**Submodule boundaries:** The Editools assemblies (`Editools` runtime + `Editools.Editor`)
are self-contained. Project code never directly references Editools types — instead it
uses `#if HAS_EDITOOLS` conditional compilation. This means the submodule can be
removed without breaking the build.

**SceneMaterialOverride is the critical shared system.** Both LocalGlobals and AO debug
modes depend on it for safe material swapping. Its mutual-exclusivity guarantee prevents
two debug modes from fighting over scene materials.

**Scene save safety:** `SceneMaterialOverride` hooks into `sceneSaving`/`sceneSaved` to
temporarily restore original materials during save. Without this, debug materials would
be serialized into the scene file.

**QuickTransform key handling:** The tool uses `[InitializeOnLoad]` + `SceneView.duringSceneGui`
to intercept W/E/R key events. It distinguishes between tap (tool switch, let through)
and hold+drag (QuickTransform operation, consumed). The `ModeKeyDelaySec` of 0.1s prevents
accidental activation during normal tool switching. The `suppressKeyUpFor` field prevents
the key-up event from switching Unity's active tool after a QuickTransform operation.

**Heatmap reflection:** `HierarchyHeatmap` accesses Unity's internal `TreeViewController`
via reflection to determine collapsed state. This is fragile across Unity versions but
necessary for parent-row heat propagation.

**QuickAccess folder navigation:** Uses reflection into `ProjectBrowser.ShowFolderContents`
to navigate the two-column Project window (select in tree + show contents). The yellow ping
is triggered via `m_FolderTree.Frame(id, true, true)` directly on the tree controller.
`EditorGUIUtility.PingObject` cannot be used for folders — it navigates to the parent
folder, overriding the contents view.

**Cursor warp (Windows only):** QuickTransform's edge rotation mode warps the OS cursor
to the rotation circle perimeter via Win32 P/Invoke (`SetCursorPos`). This provides
natural 1:1 rotation feel regardless of where the user clicked the edge. Uses delta-based
warping to avoid DPI/window-offset conversion issues.

---

## What This System Does NOT Do

- Does not modify gameplay code — purely editor tooling
- Does not persist any state in scene files (all state in EditorPrefs/SessionState)
- SceneMaterialOverride does not handle particles, UI, or terrain — only MeshRenderer
  and SkinnedMeshRenderer
- QuickTransform does not support custom pivot points — uses selection center (single)
  or average position (multi)
- SnapToSurface does not use Physics colliders — uses direct mesh triangle intersection
- No runtime assembly or runtime behavior beyond the SceneMaterialOverride backup system

---

## Code Style & Patterns

Conventions observed across Editools tools. Follow these when adding new features.

### EditorWindows

- **UIElements over IMGUI.** New windows use `rootVisualElement` + UIToolkit, not `OnGUI()`.
  (See `QuickAccess`, and `LRUAssets` in One Humus for the established pattern.)
- USS stylesheets live in a `Resources/` subfolder next to the script, loaded via
  `Resources.Load<StyleSheet>("SheetName")` in `OnEnable()`.
- Open windows via `[MenuItem("Tools/Editools/...")]` + `GetWindow<T>()`.
- Set `titleContent` with icon: `new GUIContent("Name", EditorGUIUtility.IconContent("d_Icon").image)`.

### Static Tools (non-window)

- Register via `[InitializeOnLoad]` on the class or `[InitializeOnLoadMethod]` on a static method.
- Hook into `SceneView.duringSceneGui` for scene-view input handling.
- Keyboard shortcuts via `[Shortcut("Editools/Tool Name", KeyCode.X, ShortcutModifiers.Y)]`.

### Local Persistence

- **EditorPrefs** for all local state. Key with `Application.dataPath` prefix for per-project
  isolation: `Application.dataPath + "ToolName_Setting"`.
- Complex lists stored as comma-separated (or semicolon-separated) ID strings.
- **SessionState** for transient state that should survive domain reloads but not editor restarts.

### Object ID Encoding

Shared pattern (originated in LRUAssets, extended in QuickAccess) for serializing references
to both project assets and scene objects as strings:

| Prefix | Format | Survives |
|---|---|---|
| `guid:` | `guid:{assetGUID}` | Renames, domain reloads, editor restarts |
| `globalid:` | `globalid:{GlobalObjectId}` | Renames, domain reloads (scene GUID + local file ID) |
| `gameObject:` | `gameObject:{sceneName}:{hierarchy/path}` | Domain reloads (if scene loaded, breaks on rename) |
| `instance:` | `instance:{instanceID}` | Current session only (fallback) |

QuickAccess uses `globalid:` for scene objects (rename-proof). LRUAssets still uses
`gameObject:` (auto-populated list, so stale entries naturally cycle out).

Classification: `AssetDatabase.Contains(obj)` → project asset. `GameObject` with valid
loaded scene → scene object. Components unwrap to their GameObject.

### Row-Based List UI

- Each row is a `VisualElement` with class `.row`, containing `.title` (`.icon` Image + Label).
- `row.userData` stores the string ID for persistence and lookup.
- Alternating row colors via `.variant1` / `.variant2` classes, updated on list mutation.
- Icon via `EditorGUIUtility.ObjectContent(obj, obj.GetType()).image`.

### Drag and Drop

**Drag initiation (from custom UI):**
1. `PointerDownEvent` → store drag candidate
2. `PointerMoveEvent` → `DragAndDrop.PrepareStartDrag()`, set `objectReferences` + `paths`,
   call `DragAndDrop.StartDrag(label)`
3. `PointerUpEvent` → clear candidate

**Drop acceptance (onto custom UI):**
1. `DragEnterEvent` / `DragUpdatedEvent` → validate, set `DragAndDrop.visualMode`
2. `DragPerformEvent` → `DragAndDrop.AcceptDrag()`, process dropped objects
3. `DragLeaveEvent` → remove visual feedback
