#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Re-bakes Boolean results live as the user moves or edits a Subject or Operator in the scene view.
/// Push-based and lazily subscribed: <see cref="GreyPrimitiveEditor"/> acquires it while a grey
/// object is selected and releases it otherwise — so there is no [InitializeOnLoad] and no idle
/// per-frame work. It only inspects the current selection and compares a cheap state signature
/// (world matrix + Greybox corners/faces) to the last seen value, mirroring how GreyboxSeamSolver
/// watches transforms. Mesh-only (no undo/structural work) so a drag stays smooth.
/// </summary>
static class GreyBooleanLiveWatcher
{
    static int s_refCount;
    static readonly Dictionary<int, long> s_lastSig = new Dictionary<int, long>(); // instanceID -> signature

    public static void Acquire()
    {
        if (s_refCount++ == 0)
        {
            SceneView.duringSceneGui += OnSceneGui;
            Selection.selectionChanged += OnSelectionChanged;
        }
    }

    public static void Release()
    {
        if (--s_refCount > 0) return;
        s_refCount = 0;
        SceneView.duringSceneGui -= OnSceneGui;
        Selection.selectionChanged -= OnSelectionChanged;
        s_lastSig.Clear();
    }

    static void OnSelectionChanged() => s_lastSig.Clear();

    static void OnSceneGui(SceneView sv)
    {
        Event e = Event.current;
        if (e.type != EventType.Repaint && e.type != EventType.MouseDrag && e.type != EventType.MouseUp)
            return;

        var sel = Selection.transforms;
        if (sel == null || sel.Length == 0) return;

        foreach (var t in sel)
        {
            if (t == null) continue;
            var prim = t.GetComponent<GreyPrimitive>();
            if (prim == null) continue;

            int key = prim.GetInstanceID();
            long sig = Signature(prim);
            bool known = s_lastSig.TryGetValue(key, out long prev);
            s_lastSig[key] = sig;
            if (!known || prev == sig) continue; // first sighting (baseline) or unchanged

            // The moved/edited primitive feeds a result (its parent wrapper) — re-bake up the chain.
            GreyBooleanOrchestrator.ReBakeFrom(prim);
            sv.Repaint();
        }
    }

    static long Signature(GreyPrimitive prim)
    {
        Matrix4x4 m = prim.transform.localToWorldMatrix;
        long h = 17;
        for (int i = 0; i < 16; i++)
            h = h * 31 + m[i].GetHashCode();

        if (prim is Greybox gb)
        {
            var c = gb.Corners;
            for (int i = 0; i < c.Length; i++)
                h = h * 31 + c[i].GetHashCode();
            var f = gb.ActiveFaces;
            for (int i = 0; i < f.Length; i++)
                h = h * 31 + (f[i] ? 1 : 0);
        }
        return h;
    }
}
#endif
