using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GreyPrimitive), true)]
[CanEditMultipleObjects]
public class GreyPrimitiveEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var prim = (GreyPrimitive)target;
        bool isLive = prim.MeshIsLive;

        var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
        var label = isLive ? "Mesh: Live" : "Mesh: Serialized";
        var color = isLive ? new Color(1f, 0.75f, 0.3f) : new Color(0.4f, 0.8f, 0.4f);

        var style = new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = color }
        };
        EditorGUI.LabelField(rect, label, style);

        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var t in targets)
                if (t is GreyPrimitive p) p.RebuildMesh();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Rebuild Mesh"))
        {
            foreach (var t in targets)
                if (t is GreyPrimitive p) p.RebuildMesh();
        }
    }
}
