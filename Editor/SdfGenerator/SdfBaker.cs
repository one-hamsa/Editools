#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// Pure, edit-time baker that turns a bitmap into a signed distance field.
///
/// Pipeline: GPU-blit readback (handles non-readable textures and resampling in one step)
/// -> per-pixel coverage extraction (alpha or luminance) -> optional source pre-blur
/// -> binarize -> 8SSEDT signed Euclidean distance transform -> asymmetric inside/outside
/// spread encoding -> channel packing.
///
/// The returned Texture2D holds the raw SDF values (0.5 = edge, 1 = fully inside, 0 = fully
/// outside). It is NOT marked readable for runtime; callers encode it to disk immediately.
/// </summary>
static class SdfBaker
{
	/// <summary>Which source channel drives the inside/outside test.</summary>
	public enum ChannelSource { Alpha, Luminance }

	/// <summary>Where the single-channel SDF value is written in the output texture.</summary>
	public enum Packing { Alpha, Rgb }

	public struct Settings
	{
		public ChannelSource source;
		public float threshold;       // 0..1 coverage cutoff: >= threshold counts as inside
		public float spreadInside;    // pixels of falloff mapped from edge (0.5) to fully-inside (1)
		public float spreadOutside;   // pixels of falloff mapped from edge (0.5) to fully-outside (0)
		public int blurRadius;        // source pre-blur radius in px; 0 = off (smooths jagged source edges)
		public bool antiAlias;        // sub-pixel edge reconstruction: straightens aliased edges, keeps corners, kills banding
		public Packing packing;
		public int width;
		public int height;
	}

	/// <summary>
	/// Bakes <paramref name="source"/> into a new SDF Texture2D at the resolution given in
	/// <paramref name="s"/>. Caller owns the returned texture (encode + DestroyImmediate).
	/// </summary>
	public static Texture2D Bake(Texture2D source, Settings s)
	{
		int w = Mathf.Max(1, s.width);
		int h = Mathf.Max(1, s.height);

		Color[] src = ReadResampled(source, w, h);

		// Coverage in 0..1 from the chosen channel.
		float[] coverage = new float[w * h];
		for (int i = 0; i < coverage.Length; i++)
		{
			Color c = src[i];
			coverage[i] = s.source == ChannelSource.Alpha
				? c.a
				: c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
		}

		if (s.blurRadius > 0)
			coverage = BoxBlur(coverage, w, h, s.blurRadius);

		float[] signed = s.antiAlias
			? SignedDistanceAA(coverage, w, h, s.threshold)
			: SignedDistance(coverage, w, h, s.threshold);

		float insideRange = Mathf.Max(s.spreadInside, 1e-4f);
		float outsideRange = Mathf.Max(s.spreadOutside, 1e-4f);

		var result = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
		var outPixels = new Color[w * h];
		for (int i = 0; i < outPixels.Length; i++)
		{
			float d = signed[i]; // + outside, - inside, in pixels
			float v = d <= 0f
				? 0.5f + 0.5f * Mathf.Clamp01(-d / insideRange)
				: 0.5f - 0.5f * Mathf.Clamp01(d / outsideRange);

			outPixels[i] = s.packing == Packing.Alpha
				? new Color(1f, 1f, 1f, v)
				: new Color(v, v, v, 1f);
		}
		result.SetPixels(outPixels);
		result.Apply(false, false);
		return result;
	}

	/// <summary>
	/// Blits the source through a temporary RenderTexture to read its pixels at an arbitrary
	/// resolution. Works regardless of the source's Read/Write import flag, and the GPU blit
	/// does the (bilinear) downscale for free.
	/// </summary>
	static Color[] ReadResampled(Texture2D source, int w, int h)
	{
		var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
		var prevActive = RenderTexture.active;
		var prevFilter = source.filterMode;
		source.filterMode = FilterMode.Bilinear;

		Graphics.Blit(source, rt);
		RenderTexture.active = rt;

		var readable = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
		readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
		readable.Apply(false, false);

		RenderTexture.active = prevActive;
		RenderTexture.ReleaseTemporary(rt);
		source.filterMode = prevFilter;

		Color[] pixels = readable.GetPixels();
		Object.DestroyImmediate(readable);
		return pixels;
	}

	/// <summary>Separable box blur with clamped edges. Used to soften jagged source edges before binarization.</summary>
	static float[] BoxBlur(float[] data, int w, int h, int radius)
	{
		float[] tmp = new float[data.Length];
		float[] outData = new float[data.Length];
		float norm = 1f / (radius * 2 + 1);

		// Horizontal pass.
		for (int y = 0; y < h; y++)
		{
			int row = y * w;
			for (int x = 0; x < w; x++)
			{
				float sum = 0f;
				for (int k = -radius; k <= radius; k++)
					sum += data[row + Mathf.Clamp(x + k, 0, w - 1)];
				tmp[row + x] = sum * norm;
			}
		}

		// Vertical pass.
		for (int x = 0; x < w; x++)
		{
			for (int y = 0; y < h; y++)
			{
				float sum = 0f;
				for (int k = -radius; k <= radius; k++)
					sum += tmp[Mathf.Clamp(y + k, 0, h - 1) * w + x];
				outData[y * w + x] = sum * norm;
			}
		}
		return outData;
	}

	// --- 8SSEDT (8-points Sequential Signed Euclidean Distance Transform) ---
	// Two distance fields are propagated: one measuring distance to the nearest inside pixel,
	// one to the nearest outside pixel. The signed result is their difference, giving
	// positive distances outside the shape and negative inside it. Reference: Danielsson 1980 /
	// Richard Mitton's "Signed Distance Fields".

	struct Cell
	{
		public int dx;
		public int dy;
		public int SqDist => dx * dx + dy * dy;
	}

	const int k_Far = 32000; // large offset standing in for "no edge found yet"

	static float[] SignedDistance(float[] coverage, int w, int h, float threshold)
	{
		var inside = new Cell[w * h];   // distance to nearest inside pixel
		var outside = new Cell[w * h];  // distance to nearest outside pixel

		for (int i = 0; i < coverage.Length; i++)
		{
			bool isInside = coverage[i] >= threshold;
			inside[i] = isInside ? default : new Cell { dx = k_Far, dy = k_Far };
			outside[i] = isInside ? new Cell { dx = k_Far, dy = k_Far } : default;
		}

		Propagate(inside, w, h);
		Propagate(outside, w, h);

		var signed = new float[w * h];
		for (int i = 0; i < signed.Length; i++)
		{
			float dInside = Mathf.Sqrt(inside[i].SqDist);   // 0 inside, grows outside
			float dOutside = Mathf.Sqrt(outside[i].SqDist); // 0 outside, grows inside
			signed[i] = dInside - dOutside;                 // + outside, - inside
		}
		return signed;
	}

	// Sub-pixel anti-aliased distance field.
	//
	// Reads the fractional coverage along anti-aliased edges to recover where the true edge
	// crosses each boundary pixel at sub-pixel precision (sub[]). The vector distance transform
	// then gives every pixel the Euclidean distance to its nearest boundary pixel — corner-correct,
	// because it is a true distance-to-nearest-point, not a projection onto an edge line. The
	// signed value is that Euclidean distance (signed by which side the pixel is on) shifted by
	// the nearest seed's sub-pixel offset. This keeps corners sharp (no bleeding) while still
	// straightening aliasing and producing continuous, band-free distances.
	const float k_MaxSeedOffset = 1.0f; // clamp on a seed's sub-pixel edge distance (px)

	static float[] SignedDistanceAA(float[] coverage, int w, int h, float threshold)
	{
		var sub = new float[w * h]; // seed's signed distance to the edge (+ outside)
		var grid = new Cell[w * h];

		for (int i = 0; i < grid.Length; i++)
			grid[i] = new Cell { dx = k_Far, dy = k_Far };

		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int i = y * w + x;
				bool isInside = coverage[i] >= threshold;

				// Boundary = differs from any 4-neighbour (clamped at image edges).
				bool boundary =
					isInside != (coverage[ClampIdx(x - 1, y, w, h)] >= threshold) ||
					isInside != (coverage[ClampIdx(x + 1, y, w, h)] >= threshold) ||
					isInside != (coverage[ClampIdx(x, y - 1, w, h)] >= threshold) ||
					isInside != (coverage[ClampIdx(x, y + 1, w, h)] >= threshold);
				if (!boundary) continue;

				// Sub-pixel distance to the edge from coverage and its gradient magnitude.
				float gx = (coverage[ClampIdx(x + 1, y, w, h)] - coverage[ClampIdx(x - 1, y, w, h)]) * 0.5f;
				float gy = (coverage[ClampIdx(x, y + 1, w, h)] - coverage[ClampIdx(x, y - 1, w, h)]) * 0.5f;
				float gm = Mathf.Sqrt(gx * gx + gy * gy);
				if (gm < 1e-5f) gm = 1e-5f;

				sub[i] = Mathf.Clamp((threshold - coverage[i]) / gm, -k_MaxSeedOffset, k_MaxSeedOffset);
				grid[i] = default; // seed (0,0)
			}
		}

		Propagate(grid, w, h);

		var signed = new float[w * h];
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				int i = y * w + x;
				Cell cell = grid[i];

				// No boundary anywhere reached this pixel (uniform image): fall back to a sign.
				if (cell.dx >= k_Far || cell.dy >= k_Far)
				{
					signed[i] = coverage[i] >= threshold ? -k_Far : k_Far;
					continue;
				}

				int qx = Mathf.Clamp(x + cell.dx, 0, w - 1);
				int qy = Mathf.Clamp(y + cell.dy, 0, h - 1);
				int q = qy * w + qx;

				// Euclidean distance to the nearest boundary pixel (corner-correct), signed by the
				// side p sits on, then shifted by that seed's sub-pixel edge offset.
				float dEuclid = Mathf.Sqrt(cell.SqDist);
				float sideSign = coverage[i] >= threshold ? -1f : 1f;
				signed[i] = sideSign * dEuclid + sub[q];
			}
		}
		return signed;
	}

	static int ClampIdx(int x, int y, int w, int h) =>
		Mathf.Clamp(y, 0, h - 1) * w + Mathf.Clamp(x, 0, w - 1);

	static void Propagate(Cell[] grid, int w, int h)
	{
		// Forward pass: top-left -> bottom-right.
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				Cell c = grid[y * w + x];
				Compare(grid, ref c, w, h, x, y, -1, 0);
				Compare(grid, ref c, w, h, x, y, 0, -1);
				Compare(grid, ref c, w, h, x, y, -1, -1);
				Compare(grid, ref c, w, h, x, y, 1, -1);
				grid[y * w + x] = c;
			}
			for (int x = w - 1; x >= 0; x--)
			{
				Cell c = grid[y * w + x];
				Compare(grid, ref c, w, h, x, y, 1, 0);
				grid[y * w + x] = c;
			}
		}

		// Backward pass: bottom-right -> top-left.
		for (int y = h - 1; y >= 0; y--)
		{
			for (int x = w - 1; x >= 0; x--)
			{
				Cell c = grid[y * w + x];
				Compare(grid, ref c, w, h, x, y, 1, 0);
				Compare(grid, ref c, w, h, x, y, 0, 1);
				Compare(grid, ref c, w, h, x, y, -1, 1);
				Compare(grid, ref c, w, h, x, y, 1, 1);
				grid[y * w + x] = c;
			}
			for (int x = 0; x < w; x++)
			{
				Cell c = grid[y * w + x];
				Compare(grid, ref c, w, h, x, y, -1, 0);
				grid[y * w + x] = c;
			}
		}
	}

	static void Compare(Cell[] grid, ref Cell c, int w, int h, int x, int y, int ox, int oy)
	{
		int nx = x + ox;
		int ny = y + oy;
		if (nx < 0 || nx >= w || ny < 0 || ny >= h) return;

		Cell other = grid[ny * w + nx];
		other.dx += ox;
		other.dy += oy;
		if (other.SqDist < c.SqDist)
			c = other;
	}
}
#endif
