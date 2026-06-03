#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Extensibility seam for the Editools Trigger Debug view. Lets game-specific systems
/// contribute extra box volumes (with their own tint) to be highlighted alongside the
/// selected colliders, without Editools needing to know what those systems are.
///
/// A provider is invoked every time Trigger Debug rebuilds its volume list (selection
/// change, scene-view drag). Each call hands the provider an <see cref="AddBox"/> sink;
/// the provider pushes a world→unit-cube matrix (interior maps to |xyz| &lt;= 0.5, same
/// convention as a BoxCollider) plus a tint. Boxes pushed later win where they overlap,
/// so a provider should push outer volumes before the inner ones nested inside them.
/// </summary>
public static class TriggerDebugVolumes
{
	/// <summary>Sink a provider uses to contribute one box: world→unit-cube matrix + tint.</summary>
	public delegate void AddBox(Matrix4x4 worldToUnitCube, Color tint);

	/// <summary>Contributes box volumes into the supplied sink when invoked.</summary>
	public delegate void Provider(AddBox addBox);

	static readonly List<Provider> s_providers = new List<Provider>(4);

	/// <summary>
	/// Raised when the set of providers changes (or a provider requests a refresh), so an
	/// active Trigger Debug view can re-upload. Subscribed by TriggerDebug while it's on.
	/// </summary>
	public static event System.Action Changed;

	public static void Register(Provider provider)
	{
		if (provider == null || s_providers.Contains(provider)) return;
		s_providers.Add(provider);
		Changed?.Invoke();
	}

	public static void Unregister(Provider provider)
	{
		if (s_providers.Remove(provider))
			Changed?.Invoke();
	}

	/// <summary>Ask any active Trigger Debug view to re-collect volumes (e.g. after a value edit).</summary>
	public static void RequestRefresh() => Changed?.Invoke();

	/// <summary>Invokes every registered provider, funneling their boxes into the sink.</summary>
	public static void Collect(AddBox addBox)
	{
		for (int i = 0; i < s_providers.Count; i++)
			s_providers[i]?.Invoke(addBox);
	}
}
#endif
