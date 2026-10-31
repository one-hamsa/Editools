#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

/// <summary>
/// FBX Export: bakes the selected renderers to one binary FBX file through
/// <see cref="FbxMeshExporter"/>. The toolbar button / settings-menu row opens
/// <see cref="FbxExportPopup"/>, where topology (triangles or quads) and the quad merge angle are
/// chosen before exporting. Those options live in <see cref="EditoolsSettings"/>.
/// </summary>
static class FbxExport
{
	internal static bool Enabled => EditoolsSettings.Current.fbxExportEnabled;

	/// <summary>Runs the export with the options stored in <see cref="EditoolsSettings"/>.</summary>
	internal static void ExportSelection()
	{
		var s = EditoolsSettings.Current;
		FbxMeshExporter.ExportSelection(s.fbxExportQuads, s.fbxExportQuadMaxAngle);
	}
}

/// <summary>
/// Popup behind the FBX Export toolbar dropdown: topology, quad merge angle and the Export button.
/// Fields are drawn through a <see cref="SerializedObject"/> of the settings (undo, tooltips, drag handles).
/// </summary>
class FbxExportPopup : PopupWindowContent
{
	static readonly string[] k_TopologyLabels = { "Triangles", "Quads" };

	SerializedObject _settings;

	public override Vector2 GetWindowSize() => new Vector2(260, 92);

	public override void OnOpen() => _settings = new SerializedObject(EditoolsSettings.Current);

	public override void OnGUI(Rect rect)
	{
		_settings.Update();

		var quads = _settings.FindProperty(nameof(EditoolsSettings.fbxExportQuads));
		int topology = EditorGUILayout.Popup(new GUIContent("Topology", quads.tooltip), quads.boolValue ? 1 : 0, k_TopologyLabels);
		quads.boolValue = topology == 1;

		using (new EditorGUI.DisabledScope(!quads.boolValue))
		{
			var angle = _settings.FindProperty(nameof(EditoolsSettings.fbxExportQuadMaxAngle));
			EditorGUILayout.PropertyField(angle, new GUIContent("Quad Max Angle", angle.tooltip));
		}

		if (_settings.ApplyModifiedProperties())
			EditoolsSettings.Current.SaveAndNotify();

		EditorGUILayout.Space(4f);
		using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
		{
			if (GUILayout.Button("Export Selection..."))
			{
				editorWindow.Close();
				FbxExport.ExportSelection();
			}
		}
	}
}

/// <summary>Toolbar dropdown that opens <see cref="FbxExportPopup"/> under the button.</summary>
class EditoolsFbxExportButton : EditorToolbarDropdown
{
	public EditoolsFbxExportButton()
	{
		icon = EditoolsIcons.Builtin("d_PrefabModel Icon");
		tooltip = "FBX Export - bake the selected renderers to one FBX file";
		clicked += () => UnityEditor.PopupWindow.Show(worldBound, new FbxExportPopup());
	}
}
#endif
