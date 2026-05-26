#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Edit-time reflection bridge to the <c>LocalMaterial</c> component in <c>UD.Rendering</c>.
///
/// Editools cannot reference <c>UD.Rendering</c> directly — <c>UD.Rendering</c> already
/// references Editools (for <c>SceneMaterialOverride</c> and friends), so a direct ref
/// would close a cycle. All <c>LocalMaterial</c> interaction from Sticker goes through
/// here. Reflection lookups are cached on first use.
///
/// If <c>UD.Rendering</c> isn't loaded or the API drifts, the bridge logs once and
/// no-ops cleanly — Sticker still renders, only the material binding is lost.
/// </summary>
public static class LocalMaterialBridge
{
    static bool       s_tried;
    static System.Type s_lmType;
    static System.Type s_overrideType;
    static MethodInfo s_ensureSlotCount;
    static MethodInfo s_syncOverrideList;
    static MethodInfo s_getOverrides;
    static MethodInfo s_applyOverrides;
    static FieldInfo  s_fPropertyName;
    static FieldInfo  s_fEnabled;
    static FieldInfo  s_fTextureValue;

    static bool TryInit()
    {
        if (s_tried) return s_lmType != null;
        s_tried = true;

        s_lmType = System.Type.GetType("LocalMaterial, UD.Rendering");
        if (s_lmType == null)
        {
            Debug.LogError("[Sticker] LocalMaterial type not found in UD.Rendering — Sticker MainTex binding disabled.");
            return false;
        }

        s_overrideType     = s_lmType.GetNestedType("PropertyOverride");
        s_ensureSlotCount  = s_lmType.GetMethod("EnsureSlotCount",  new[] { typeof(int) });
        s_syncOverrideList = s_lmType.GetMethod("SyncOverrideList", new[] { typeof(int) });
        s_getOverrides     = s_lmType.GetMethod("GetOverrides",     new[] { typeof(int) });
        s_applyOverrides   = s_lmType.GetMethod("ApplyOverrides",   System.Type.EmptyTypes);

        if (s_overrideType != null)
        {
            s_fPropertyName = s_overrideType.GetField("propertyName");
            s_fEnabled      = s_overrideType.GetField("enabled");
            s_fTextureValue = s_overrideType.GetField("textureValue");
        }

        bool ok = s_overrideType != null
               && s_ensureSlotCount != null && s_syncOverrideList != null
               && s_getOverrides    != null && s_applyOverrides   != null
               && s_fPropertyName   != null && s_fEnabled         != null && s_fTextureValue != null;

        if (!ok)
        {
            Debug.LogError("[Sticker] LocalMaterial reflection lookup failed — API may have drifted. Sticker MainTex binding disabled.");
            s_lmType = null;
        }
        return s_lmType != null;
    }

    public static System.Type Type
    {
        get { TryInit(); return s_lmType; }
    }

    /// <summary>Add a LocalMaterial to <paramref name="go"/> if it doesn't already have one.</summary>
    public static void EnsureOn(GameObject go)
    {
        if (!TryInit()) return;
        if (go.GetComponent(s_lmType) == null)
            go.AddComponent(s_lmType);
    }

    /// <summary>
    /// Push <paramref name="tex"/> into the LocalMaterial's slot-0 <c>_MainTex</c> override.
    /// No-ops if LocalMaterial isn't present, the property doesn't exist on the source shader,
    /// or reflection failed.
    /// </summary>
    public static void PushMainTex(GameObject go, Texture2D tex)
    {
        if (!TryInit()) return;
        var lm = go.GetComponent(s_lmType);
        if (lm == null) return;

        s_ensureSlotCount .Invoke(lm, new object[] { 1 });
        s_syncOverrideList.Invoke(lm, new object[] { 0 });
        var overrides = s_getOverrides.Invoke(lm, new object[] { 0 }) as IList;
        if (overrides == null) return;

        for (int i = 0; i < overrides.Count; i++)
        {
            var ov = overrides[i];
            if (ov == null) continue;
            var name = s_fPropertyName.GetValue(ov) as string;
            if (name != "_MainTex") continue;
            s_fTextureValue.SetValue(ov, tex);
            s_fEnabled     .SetValue(ov, tex != null);
            break;
        }

        s_applyOverrides.Invoke(lm, null);
    }
}
#endif
