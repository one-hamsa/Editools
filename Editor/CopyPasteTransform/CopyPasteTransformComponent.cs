using UnityEngine;
using UnityEditor;
using UnityEditor.ShortcutManagement;

public static class CopyPasteTransformComponent {
    struct TransformData {
        public Vector3      localPosition;
        public Quaternion   localRotation;
        public Vector3      localScale;

        public TransformData(Vector3 localPosition, Quaternion localRotation, Vector3 localScale) {
            this.localPosition  = localPosition;
            this.localRotation  = localRotation;
            this.localScale     = localScale;
        }
    }

    private static TransformData _data;

    [Shortcut("Editools/Copy Transform", KeyCode.C, ShortcutModifiers.Alt | ShortcutModifiers.Action)]
    public static void CopyTransformValues() {
        if(Selection.gameObjects.Length == 0) return;
        var selectionTr = Selection.gameObjects[0].transform;
        _data = new TransformData(selectionTr.localPosition, selectionTr.localRotation, selectionTr.localScale);
    }

    [Shortcut("Editools/Paste Transform", KeyCode.V, ShortcutModifiers.Alt | ShortcutModifiers.Action)]
    public static void PasteTransformValues() {
        foreach(var selection in Selection.gameObjects) {
            Transform selectionTr = selection.transform;
            Undo.RecordObject(selectionTr, "Paste Transform Values");
            selectionTr.localPosition = _data.localPosition;
            selectionTr.localRotation = _data.localRotation;
            selectionTr.localScale = _data.localScale;
        }
    }

    [Shortcut("Editools/Reset Transform", KeyCode.X, ShortcutModifiers.Alt | ShortcutModifiers.Action)]
    public static void ResetTransformValues() {
        if(Selection.gameObjects.Length == 0) return;
        foreach(var selection in Selection.gameObjects) {
            Transform selectionTr = selection.transform;
            Undo.RecordObject(selectionTr, "Reset Transform");
            selectionTr.localPosition = Vector3.zero;
            selectionTr.localRotation = Quaternion.identity;
            selectionTr.localScale = Vector3.one;
        }
    }
}
