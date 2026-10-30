#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Toolbar icons for Editools functions. Built-in editor icons are used where one fits; the
/// others are PNGs in <c>Editor/Icons/</c> (Material Design Icons, Apache 2.0), stored as black
/// glyphs and recolored to white in memory for the dark editor skin.
/// </summary>
static class EditoolsIcons
{
	const string k_FilePrefix = "editools-icon-";

	static readonly Dictionary<string, Texture2D> s_cache = new Dictionary<string, Texture2D>();

	internal static Texture2D Builtin(string name) => EditorGUIUtility.IconContent(name).image as Texture2D;

	/// <summary>Loads <c>Editor/Icons/editools-icon-&lt;name&gt;.png</c>, tinted for the current skin. Null when the file is missing.</summary>
	internal static Texture2D Custom(string name)
	{
		if (s_cache.TryGetValue(name, out var cached) && cached != null) return cached;

		string[] guids = AssetDatabase.FindAssets(k_FilePrefix + name + " t:Texture2D");
		if (guids.Length == 0)
		{
			Debug.LogError($"[Editools] Icon '{k_FilePrefix}{name}.png' not found under the Editools Editor/Icons folder.");
			return null;
		}

		var source = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guids[0]));
		var icon = EditorGUIUtility.isProSkin ? RecolorToWhite(source) : source;
		s_cache[name] = icon;
		return icon;
	}

	// The source PNGs are black glyphs with alpha; only the alpha channel is kept.
	static Texture2D RecolorToWhite(Texture2D source)
	{
		if (!source.isReadable)
		{
			Debug.LogError($"[Editools] Icon '{source.name}' is not readable; set Read/Write on its importer. Using the black glyph.");
			return source;
		}

		Color32[] pixels = source.GetPixels32();
		for (int i = 0; i < pixels.Length; i++)
			pixels[i] = new Color32(255, 255, 255, pixels[i].a);

		var result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, mipChain: true)
		{
			name = source.name + " (white)",
			hideFlags = HideFlags.HideAndDontSave,
			filterMode = FilterMode.Trilinear,
			wrapMode = TextureWrapMode.Clamp
		};
		result.SetPixels32(pixels);
		result.Apply(updateMipmaps: true, makeNoLongerReadable: true);
		return result;
	}
}
#endif
