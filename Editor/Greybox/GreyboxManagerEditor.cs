using UnityEditor;

[CustomEditor(typeof(GreyboxManager))]
public class GreyboxManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            foreach (var t in targets)
            {
                if (t is GreyboxManager mgr)
                    foreach (var prim in mgr.GetComponentsInChildren<GreyPrimitive>(includeInactive: true))
                        prim.RebuildMesh();
            }
        }
    }
}
