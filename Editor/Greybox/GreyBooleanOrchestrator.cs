#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-side glue for the Boolean feature. The result is the PARENT of its inputs: a Subject with
/// an Operator is wrapped in a new <see cref="GreyBooleanResult"/> that takes the Subject's place in
/// the hierarchy, with the Subject and Operator re-parented under it as movable inputs. The Result
/// therefore has its own free transform (the "final object"), and moving/editing either input just
/// re-bakes the chain — the result no longer rides the Subject's transform.
///
/// Two entry points:
///  • <see cref="Sync"/> — reconcile a Subject's boolean (create/teardown the wrapper, settings, bake).
///    Structural work is deferred to <c>delayCall</c> so the hierarchy is never edited mid-inspector-GUI.
///  • <see cref="ReBakeFrom"/> — mesh-only re-bake up the chain from a moved input. Called by the live
///    watcher during scene moves; no structural/undo work, so a drag stays smooth.
/// </summary>
static class GreyBooleanOrchestrator
{
    const string k_ResultName = "Boolean Result";
    const int k_MaxChainDepth = 32; // backstop against an operator cycle

    // ─── Reconcile (deferred so we never edit the hierarchy during OnInspectorGUI) ───

    public static void Sync(GreyPrimitive prim)
    {
        var p = prim;
        EditorApplication.delayCall += () => Reconcile(p, 0);
    }

    static void Reconcile(GreyPrimitive prim, int depth)
    {
        if (prim == null || depth > k_MaxChainDepth) return;

        var result = FindResultFor(prim);
        if (IsActiveSubject(prim))
            Setup(prim, result, depth);
        else if (result != null)
            Teardown(result);
    }

    static bool IsActiveSubject(GreyPrimitive prim)
    {
        var op = prim.BooleanOperator;
        if (op == null || op == prim) return false;
        if (prim.transform.IsChildOf(op.transform)) return false; // op is an ancestor -> nesting would cycle
        return true;
    }

    // The result that wraps this Subject — now its PARENT.
    static GreyBooleanResult FindResultFor(GreyPrimitive subject)
    {
        var p = subject.transform.parent;
        var r = p != null ? p.GetComponent<GreyBooleanResult>() : null;
        return (r != null && r.Subject == subject) ? r : null;
    }

    // The result that consumes this primitive as Subject or Operator (its parent wrapper).
    static GreyBooleanResult OwningResult(GreyPrimitive prim)
    {
        if (prim == null) return null;
        var p = prim.transform.parent;
        var r = p != null ? p.GetComponent<GreyBooleanResult>() : null;
        return (r != null && (r.Subject == prim || r.Operator == prim)) ? r : null;
    }

    static void Setup(GreyPrimitive subject, GreyBooleanResult result, int depth)
    {
        var op = subject.BooleanOperator;

        if (result == null)
            result = CreateResultWrapper(subject, op);
        else if (result.Operator != null && result.Operator != op)
            RestoreInput(result.Operator, result.transform.parent); // release the operator we previously held

        if (op.transform.parent != result.transform)
            Undo.SetTransformParent(op.transform, result.transform, "Boolean: nest operator");

        result.Configure(subject, op);
        CopySettings(subject, result);
        SetRendererEnabled(subject, false);
        SetRendererEnabled(op, false);
        NameOperator(op, result.transform);

        result.RebuildMesh();
        EditorUtility.SetDirty(result);

        // If this result is itself an input to a further boolean, propagate the change up the chain.
        ReBakeChain(OwningResult(result), depth + 1);
    }

    static GreyBooleanResult CreateResultWrapper(GreyPrimitive subject, GreyPrimitive op)
    {
        var go = new GameObject(k_ResultName);
        Undo.RegisterCreatedObjectUndo(go, "Create Boolean Result");

        var rt = go.transform;
        var st = subject.transform;

        // Take the Subject's exact place + transform so the baked mesh lands identically and the
        // Subject becomes a local-identity child.
        rt.SetParent(st.parent, worldPositionStays: false);
        rt.localPosition = st.localPosition;
        rt.localRotation = st.localRotation;
        rt.localScale    = st.localScale;
        rt.SetSiblingIndex(st.GetSiblingIndex());

        var result = Undo.AddComponent<GreyBooleanResult>(go);

        Undo.SetTransformParent(st,          rt, "Boolean: nest subject");
        Undo.SetTransformParent(op.transform, rt, "Boolean: nest operator");
        return result;
    }

    static void Teardown(GreyBooleanResult result)
    {
        var host = result.transform.parent;
        int sibling = result.transform.GetSiblingIndex();

        RestoreInput(result.Subject, host);
        RestoreInput(result.Operator, host);
        if (result.Subject != null)
            result.Subject.transform.SetSiblingIndex(sibling);

        Undo.DestroyObjectImmediate(result.gameObject);
    }

    // Move an input back out of a (doomed/reconfigured) result and re-show it.
    static void RestoreInput(GreyPrimitive input, Transform host)
    {
        if (input == null) return;
        Undo.SetTransformParent(input.transform, host, "Boolean: release input");
        SetRendererEnabled(input, true);
    }

    // ─── Mesh-only re-bake up the chain (live) ────────────────────────────────

    public static void ReBakeFrom(GreyPrimitive moved) => ReBakeChain(OwningResult(moved), 0);

    static void ReBakeChain(GreyBooleanResult result, int depth)
    {
        while (result != null && depth <= k_MaxChainDepth)
        {
            result.RebuildMesh();
            EditorUtility.SetDirty(result);
            result = OwningResult(result);
            depth++;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    static void NameOperator(GreyPrimitive op, Transform parent)
    {
        if (op.name.StartsWith("Operator (")) return; // already named under a result

        int n = 1;
        while (HasChildNamed(parent, $"Operator ({n})")) n++;
        op.name = $"Operator ({n})";
        EditorUtility.SetDirty(op);
    }

    static bool HasChildNamed(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
            if (parent.GetChild(i).name == name) return true;
        return false;
    }

    static void CopySettings(GreyPrimitive subject, GreyBooleanResult result)
    {
        var sr = subject.GetComponent<MeshRenderer>();
        var rr = result.GetComponent<MeshRenderer>();
        if (sr != null && rr != null)
        {
            Undo.RecordObject(rr, "Sync Boolean Result settings");
            rr.sharedMaterial    = sr.sharedMaterial;
            rr.shadowCastingMode = sr.shadowCastingMode;
        }

        var go = result.gameObject;
        go.isStatic = subject.gameObject.isStatic;
        go.layer    = subject.gameObject.layer;
    }

    static void SetRendererEnabled(GreyPrimitive prim, bool enabled)
    {
        if (prim == null) return; // ref cleared or destroyed — nothing to restore

        var mr = prim.GetComponent<MeshRenderer>();
        if (mr == null) return; // legitimately rendererless Grey type

        Undo.RecordObject(mr, "Toggle Greybox renderer");
        mr.enabled = enabled;
    }
}
#endif
