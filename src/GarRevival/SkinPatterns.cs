using System;
using System.Collections.Generic;
using UnityEngine;

namespace GarRevival
{
    // One 256x256 tile of a skin, seamless in both directions. Per texel:
    // Class picks the palette colour, Shade scales it (128 = as is), Cover is
    // how much of the part's own look it replaces (255 = all).
    internal sealed class SkinTile
    {
        public const int Size = 256;
        public const int Mask = Size - 1;
        public readonly byte[] Class = new byte[Size * Size];
        public readonly byte[] Shade = new byte[Size * Size];
        public readonly byte[] Cover = new byte[Size * Size];

        public void Set(int x, int y, int cls, float shade, float cover = 255f)
        {
            int i = (y & Mask) * Size + (x & Mask);
            Class[i] = (byte)cls;
            Shade[i] = (byte)Mathf.Clamp(shade, 0f, 255f);
            Cover[i] = (byte)Mathf.Clamp(cover, 0f, 255f);
        }
    }

    // Pure maths with its own hash, so every PC draws the same skin from the
    // same catalog entry. All noise repeats every 256 texels.
    internal static class SkinPatterns
    {
        private const int N = SkinTile.Size;
        private static readonly Dictionary<byte, SkinTile> Tiles = new Dictionary<byte, SkinTile>();

        public static SkinTile Tile(SkinDef def)
        {
            SkinTile tile;
            if (!Tiles.TryGetValue(def.Index, out tile))
            {
                tile = Build(def);
                Tiles[def.Index] = tile;
            }
            return tile;
        }

        // A small preview, the pattern over plain grey metal.
        public static Texture2D Swatch(SkinDef def, int size)
        {
            SkinTile tile = Tile(def);
            Color32 metal = new Color32(122, 125, 128, 255);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * N / size) * N + x * N / size;
                    Color32 c = Scale(def.Palette[tile.Class[i]], tile.Shade[i] / 128f);
                    pixels[y * size + x] = Color32.Lerp(metal, c, tile.Cover[i] / 255f);
                }
            }
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        public static Color32 Scale(Color32 c, float f)
        {
            return new Color32(ToByte(c.r * f), ToByte(c.g * f), ToByte(c.b * f), c.a);
        }

        public static byte ToByte(float v) => (byte)(v < 0f ? 0f : v > 255f ? 255f : v);

        private static SkinTile Build(SkinDef def)
        {
            SkinTile t = new SkinTile();
            uint seed = def.Seed;
            switch (def.Pattern)
            {
                case SkinPattern.Camo: Camo(t, seed, 1); break;
                case SkinPattern.Digital: Camo(t, seed, 4); break;
                case SkinPattern.Tiger: Tiger(t, seed); break;
                case SkinPattern.Hazard: Hazard(t, seed); break;
                case SkinPattern.Carbon: Carbon(t, seed); break;
                case SkinPattern.Rust: Rust(t, seed); break;
                case SkinPattern.Brushed: Brushed(t, seed); break;
                case SkinPattern.Circuit: Circuit(t, seed); break;
                case SkinPattern.Flames: Flames(t, seed); break;
            }
            return t;
        }

        // ---- patterns ----

        // Two independent blob fields so the third colour overlaps both others.
        // cell > 1 quantises to square pixels (digital camo).
        private static void Camo(SkinTile t, uint seed, int cell)
        {
            float[] a = Field((x, y) => Warped(x, y, 4, seed), cell);
            float[] b = Field((x, y) => Warped(x, y, 4, seed + 1000), cell);
            float ta = Quantile(a, 0.5f), tb = Quantile(b, 0.74f);
            Each((x, y, i) => t.Set(x, y, b[i] > tb ? 2 : a[i] > ta ? 1 : 0, 128f + (Rand(x / cell, y / cell, seed + 7) - 0.5f) * 10f));
        }

        private static void Tiger(SkinTile t, uint seed)
        {
            Each((x, y, i) =>
            {
                float phase = x / (float)N * 7f + (Fbm(x, y, 4, 4, 3, seed) - 0.5f) * 1.8f;
                float thickness = 0.18f + 0.3f * Fbm(x, y, 2, 4, 2, seed + 5);
                bool stripe = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * phase) > 1f - thickness;
                t.Set(x, y, stripe ? 1 : 0, 128f + (Fbm(x, y, 16, 16, 2, seed + 9) - 0.5f) * 24f);
            });
        }

        // Diagonal warning stripes with chipped paint showing the part beneath.
        private static void Hazard(SkinTile t, uint seed)
        {
            float[] wear = Field((x, y) => Fbm(x, y, 8, 8, 3, seed), 1);
            float chip = Quantile(wear, 0.88f);
            Each((x, y, i) =>
            {
                float phase = (x + y) * 6f / N;
                t.Set(x, y, phase - Mathf.Floor(phase) < 0.5f ? 0 : 1, 128f + (Rand(x, y, seed) - 0.5f) * 12f, wear[i] > chip ? 60f : 255f);
            });
        }

        // 2x2 twill: each tow passes over two and under two, shifted one per row.
        private static void Carbon(SkinTile t, uint seed)
        {
            const int cell = 8;
            Each((x, y, i) =>
            {
                bool weft = (((x / cell) + (y / cell)) & 3) < 2;
                float across = (weft ? y : x) % cell;
                float bulge = Mathf.Sin(Mathf.PI * (across + 0.5f) / cell);
                t.Set(x, y, weft ? 0 : 1, 100f + 60f * bulge + (Rand(x, y, seed) - 0.5f) * 8f);
            });
        }

        private static void Rust(SkinTile t, uint seed)
        {
            float[] amount = Field((x, y) => Warped(x, y, 4, seed), 1);
            float start = Quantile(amount, 0.42f);
            Each((x, y, i) =>
            {
                float fine = Fbm(x, y, 16, 16, 3, seed + 9);
                float cover = Mathf.Clamp01((amount[i] - start) / 0.1f);
                if (Rand(x, y, seed + 3) > 0.985f)
                {
                    cover = Mathf.Max(cover, 0.8f);
                }
                t.Set(x, y, fine > 0.6f ? 2 : fine < 0.42f ? 1 : 0, 128f + (fine - 0.5f) * 70f, cover * 255f);
            });
        }

        // Fine streaks along one axis plus a broad sheen, for a polished-metal look.
        private static void Brushed(SkinTile t, uint seed)
        {
            Each((x, y, i) =>
            {
                float streak = Fbm(x, y, 2, 128, 3, seed);
                float sheen = Mathf.Sin(2f * Mathf.PI * (y / (float)N + 0.2f * Fbm(x, y, 2, 2, 2, seed + 4)));
                t.Set(x, y, 0, 128f + (streak - 0.5f) * 60f + sheen * 34f);
            });
        }

        // Traces between the centres of 16x16 cells, with pads and vias.
        private static void Circuit(SkinTile t, uint seed)
        {
            const int cell = 16, cells = N / cell, half = cell / 2;
            Each((x, y, i) => t.Set(x, y, 0, 120f + (Fbm(x, y, 32, 32, 2, seed + 2) - 0.5f) * 24f));
            for (int cy = 0; cy < cells; cy++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    uint h = Hash(cx, cy, seed);
                    int ox = cx * cell + half, oy = cy * cell + half;
                    bool right = (h & 3) != 0, down = (h & 12) == 12;
                    if (right)
                    {
                        Fill(t, ox, oy - 1, cell + 1, 3, 1, 140f);
                    }
                    if (down)
                    {
                        Fill(t, ox - 1, oy, 3, cell + 1, 1, 140f);
                    }
                    if ((h & 0x30) == 0x30 || (right && down))
                    {
                        Disc(t, ox, oy, 3.5f, 2, 150f);
                        Disc(t, ox, oy, 1.2f, 0, 70f);
                    }
                }
            }
        }

        // Two rows of flame tongues per tile.
        private static void Flames(SkinTile t, uint seed)
        {
            Each((x, y, i) =>
            {
                float rise = (y & 127) / 128f;
                float wobble = (Fbm(x, y, 4, 4, 2, seed + 1) - 0.5f) * 30f;
                float heat = (1f - rise) * 1.25f + (Fbm(x + wobble, y, 6, 4, 4, seed) - 0.5f) * 1.2f - 0.15f;
                int cls = heat > 0.85f ? 2 : heat > 0.5f ? 1 : 0;
                t.Set(x, y, cls, cls == 0 ? 128f : 112f + (heat - 0.5f) * 50f);
            });
        }

        // ---- drawing helpers ----

        private static void Each(Action<int, int, int> texel)
        {
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    texel(x, y, y * N + x);
                }
            }
        }

        private static float[] Field(Func<float, float, float> f, int cell)
        {
            float[] values = new float[N * N];
            Each((x, y, i) => values[i] = f((x / cell) * cell + cell * 0.5f, (y / cell) * cell + cell * 0.5f));
            return values;
        }

        private static float Quantile(float[] values, float q)
        {
            float[] sorted = (float[])values.Clone();
            Array.Sort(sorted);
            return sorted[(int)(q * (sorted.Length - 1))];
        }

        private static void Fill(SkinTile t, int x0, int y0, int w, int h, int cls, float shade)
        {
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    t.Set(x, y, cls, shade);
                }
            }
        }

        private static void Disc(SkinTile t, int cx, int cy, float r, int cls, float shade)
        {
            int ir = Mathf.CeilToInt(r);
            for (int dy = -ir; dy <= ir; dy++)
            {
                for (int dx = -ir; dx <= ir; dx++)
                {
                    if (dx * dx + dy * dy <= r * r)
                    {
                        t.Set(cx + dx, cy + dy, cls, shade);
                    }
                }
            }
        }

        // ---- periodic noise ----

        private static uint Hash(int x, int y, uint seed)
        {
            uint h = seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)y * 0xC2B2AE3Du;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }

        private static float Rand(int x, int y, uint seed) => (Hash(x & SkinTile.Mask, y & SkinTile.Mask, seed) & 0xFFFFFF) / 16777216f;

        private static int Wrap(int v, int period) => ((v % period) + period) % period;

        // Value noise on a lattice of px by py cells per tile.
        private static float Value(float x, float y, int px, int py, uint seed)
        {
            float fx = x / N * px, fy = y / N * py;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            int xa = Wrap(x0, px), xb = Wrap(x0 + 1, px), ya = Wrap(y0, py), yb = Wrap(y0 + 1, py);
            float top = Mathf.Lerp(Lattice(xa, ya, seed), Lattice(xb, ya, seed), tx);
            float bottom = Mathf.Lerp(Lattice(xa, yb, seed), Lattice(xb, yb, seed), tx);
            return Mathf.Lerp(top, bottom, ty);
        }

        private static float Lattice(int x, int y, uint seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777216f;

        private static float Fbm(float x, float y, int px, int py, int octaves, uint seed)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Value(x, y, px << o, py << o, seed + (uint)o * 101u);
                norm += amp;
                amp *= 0.5f;
            }
            return sum / norm;
        }

        // Domain-warped fBm: organic blobs rather than round ones.
        private static float Warped(float x, float y, int period, uint seed)
        {
            float wx = (Fbm(x, y, period, period, 2, seed + 11) - 0.5f) * 48f;
            float wy = (Fbm(x, y, period, period, 2, seed + 23) - 0.5f) * 48f;
            return Fbm(x + wx, y + wy, period, period, 4, seed);
        }
    }
}
