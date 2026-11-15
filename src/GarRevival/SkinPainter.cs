using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JunkWars.Client;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GarRevival
{
    // Builds "<prefabPath>_s<N>" on demand: a copy of the part's prefab whose
    // albedo textures have skin N baked in. The client's Part.BuildPart asks
    // for that path whenever a part wears a skin, so garage, icons, battles and
    // other players' robots all go through here. The paint mask is untouched,
    // so painting keeps working on skinned parts.
    internal static class SkinPainter
    {
        private sealed class Template
        {
            public GameObject Go;
            public float LastUsed;
            public readonly List<Material> Materials = new List<Material>();
            public readonly HashSet<string> TextureKeys = new HashSet<string>();
        }

        private sealed class Baked
        {
            public int Width, Height;
            public Color32[] Source, Result;
        }

        // Pattern strength over painted areas; the rest of the albedo shows through.
        private const float Strength = 0.92f;

        private static readonly Dictionary<string, Template> Templates = new Dictionary<string, Template>();
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static GameObject _holder;
        private static bool _toldRenderless;

        // Hook on ResourcesManager.LoadPartPrefab. A skin path always gets an
        // answer: the skinned copy, or the plain part if the skin cannot be drawn.
        public static bool TryLoad(string name, ref GameObject result)
        {
            string basePath;
            byte index;
            if (!SkinCatalog.TryParsePath(name, out basePath, out index))
            {
                return false;
            }
            GameObject basePrefab = ResourcesManager.LoadPartPrefab(basePath);
            result = basePrefab;
            SkinDef def = SkinCatalog.ByIndex(index);
            if (basePrefab == null || def == null || !CanPaint() || Failed.Contains(name))
            {
                return true;
            }
            try
            {
                result = Cached(name) ?? Build(name, basePrefab, def);
            }
            catch (Exception ex)
            {
                Failed.Add(name);
                RevivalLog.Error("Skin " + name + " could not be built; the part keeps its own look", ex);
            }
            return true;
        }

        // Writes the part's albedo textures before and after skin N as PNGs.
        public static void Dump(string prefabPath, byte index)
        {
            string basePath = prefabPath.StartsWith("prefabs/", StringComparison.OrdinalIgnoreCase) ? prefabPath.Substring("prefabs/".Length) : prefabPath;
            SkinDef def = SkinCatalog.ByIndex(index);
            GameObject basePrefab = ResourcesManager.LoadPartPrefab(basePath);
            if (def == null || basePrefab == null || !CanPaint())
            {
                RevivalLog.Info("skindump: no " + (def == null ? "skin " + index : basePrefab == null ? "part " + basePath : "graphics device"));
                return;
            }
            ResourcesManager.LoadPartPrefab(basePath + "_s" + index);
            string folder = Path.Combine(RevivalLog.Folder, "skins-dump");
            Directory.CreateDirectory(folder);
            Renderer[] renderers = PaintableRenderers(basePrefab).ToArray();
            int size = Settings.SkinTextureSize;
            int tiles = TilesFor(renderers, def, size);
            foreach (Material m in renderers.SelectMany(r => r.sharedMaterials).Where(Paintable).Distinct())
            {
                Baked baked = Bake(m, def, tiles, size);
                string stem = Path.Combine(folder, Path.GetFileName(basePath) + "_" + Safe(m.mainTexture.name));
                WritePng(stem + ".png", baked.Source, baked.Width, baked.Height);
                WritePng(stem + "_s" + index + ".png", baked.Result, baked.Width, baked.Height);
                RevivalLog.Info("skindump: " + stem + "_s" + index + ".png (" + baked.Width + "x" + baked.Height + ", " + tiles + " tiles, shader " + m.shader.name + ")");
            }
        }

        private static bool CanPaint()
        {
            bool device = !GlobalConstants.renderlessMode && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            if (!device && !_toldRenderless)
            {
                _toldRenderless = true;
                RevivalLog.Info("Part skins: no graphics device, skinned parts show their own look");
            }
            return device;
        }

        private static GameObject Holder
        {
            get
            {
                if (_holder == null)
                {
                    // Inactive, so the copies' scripts never wake up; the client's
                    // own Instantiate of a copy gives an active part as usual.
                    _holder = new GameObject("GarRevivalSkinTemplates");
                    _holder.SetActive(false);
                    Object.DontDestroyOnLoad(_holder);
                }
                return _holder;
            }
        }

        private static GameObject Cached(string name)
        {
            Template entry;
            if (!Templates.TryGetValue(name, out entry))
            {
                return null;
            }
            if (entry.Go == null)
            {
                Templates.Remove(name);
                Release(entry);
                return null;
            }
            entry.LastUsed = Time.realtimeSinceStartup;
            return entry.Go;
        }

        private static GameObject Build(string name, GameObject basePrefab, SkinDef def)
        {
            Stopwatch watch = Stopwatch.StartNew();
            GameObject copy = (GameObject)Object.Instantiate(basePrefab, Holder.transform, false);
            copy.name = basePrefab.name;
            Template entry = new Template { Go = copy, LastUsed = Time.realtimeSinceStartup };
            int tiles;
            try
            {
                tiles = Reskin(copy, def, entry);
            }
            catch
            {
                Release(entry);
                throw;
            }
            Templates[name] = entry;
            RevivalLog.Info("Skin template " + name + " built in " + watch.ElapsedMilliseconds + " ms (" + entry.Materials.Count + " materials, " + tiles + " tiles per UV)");
            Evict();
            return copy;
        }

        private static int Reskin(GameObject copy, SkinDef def, Template entry)
        {
            int size = Settings.SkinTextureSize;
            Renderer[] renderers = PaintableRenderers(copy).ToArray();
            int tiles = TilesFor(renderers, def, size);
            Dictionary<Material, Material> skinned = new Dictionary<Material, Material>();
            Func<Material, Material> skin = m => Skinned(m, def, tiles, size, entry, skinned);
            foreach (Renderer r in renderers)
            {
                r.sharedMaterials = r.sharedMaterials.Select(skin).ToArray();
            }
            // Authored material sets (hover, selected...) may hold materials the
            // renderer does not; the client swaps them in on mouse-over.
            foreach (MaterialSet set in renderers.Select(r => r.GetComponent<PartModelElement>())
                .Where(e => e != null && e.materialSets != null)
                .SelectMany(e => e.materialSets)
                .Where(s => s != null && s.materials != null))
            {
                for (int i = 0; i < set.materials.Length; i++)
                {
                    set.materials[i] = skin(set.materials[i]);
                }
            }
            return tiles;
        }

        // Meshes the part shows as its own: not docking-point markers, not
        // excluded meshes, not UV-animated ones (treads, screens).
        private static IEnumerable<Renderer> PaintableRenderers(GameObject root)
        {
            PartPrefab prefab = root.GetComponent<PartPrefab>();
            GameObject[] excluded = prefab == null || prefab.excludeMeshes == null ? new GameObject[0] : prefab.excludeMeshes;
            return root.GetComponentsInChildren<Renderer>(true).Where(r =>
                (r is MeshRenderer || r is SkinnedMeshRenderer) && !excluded.Contains(r.gameObject) &&
                r.GetComponent<DockingPoint>() == null && r.GetComponent<AnimatedUVTexture>() == null);
        }

        // Pattern repeats per UV square, from the part's size, so camo blobs are
        // about the same size on small and large parts. Whole numbers keep the
        // tile seamless where UVs wrap; at least half a texel per pattern texel
        // on the smallest texture keeps it from aliasing.
        private static int TilesFor(Renderer[] renderers, SkinDef def, int size)
        {
            float extent = renderers.Select(MeshSize).DefaultIfEmpty(1f).Max();
            int texels = renderers.SelectMany(r => r.sharedMaterials).Where(Paintable)
                .Select(m => Math.Min(size, Math.Max(m.mainTexture.width, m.mainTexture.height))).DefaultIfEmpty(size).Min();
            return Mathf.Clamp(Mathf.RoundToInt(extent * 2f / def.WorldSize), 1, Math.Max(1, texels / 128));
        }

        private static float MeshSize(Renderer r)
        {
            SkinnedMeshRenderer skinnedMesh = r as SkinnedMeshRenderer;
            MeshFilter filter = r.GetComponent<MeshFilter>();
            Mesh mesh = skinnedMesh != null ? skinnedMesh.sharedMesh : filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                return 0f;
            }
            Vector3 s = Vector3.Scale(mesh.bounds.size, r.transform.lossyScale);
            return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }

        private static bool Paintable(Material m)
        {
            if (m == null || !(m.mainTexture is Texture2D))
            {
                return false;
            }
            string shader = m.shader == null ? string.Empty : m.shader.name;
            return shader.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) < 0 && shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) < 0 &&
                (!shader.StartsWith("Transparent/", StringComparison.Ordinal) || shader.Contains("Cutout"));
        }

        private static Material Skinned(Material m, SkinDef def, int tiles, int size, Template entry, Dictionary<Material, Material> done)
        {
            Material result;
            if (m == null)
            {
                return null;
            }
            if (done.TryGetValue(m, out result))
            {
                return result;
            }
            result = m;
            if (Paintable(m))
            {
                Texture2D texture = SkinnedTexture(m, def, tiles, size, entry);
                result = new Material(m) { name = m.name + "_skin" + def.Index, mainTexture = texture, hideFlags = HideFlags.DontUnloadUnusedAsset };
                entry.Materials.Add(result);
            }
            done[m] = result;
            return result;
        }

        private static Texture2D SkinnedTexture(Material m, SkinDef def, int tiles, int size, Template entry)
        {
            Texture mask = PaintMask(m);
            string key = m.mainTexture.GetInstanceID() + "/" + (mask == null ? 0 : mask.GetInstanceID()) + "/" + def.Index + "/" + tiles + "/" + size;
            entry.TextureKeys.Add(key);
            Texture2D texture;
            if (Textures.TryGetValue(key, out texture) && texture != null)
            {
                return texture;
            }
            Baked baked = Bake(m, def, tiles, size);
            texture = Upload(baked, (Texture2D)m.mainTexture, def);
            Textures[key] = texture;
            return texture;
        }

        private static Texture PaintMask(Material m) => m.HasProperty("_Mask") ? m.GetTexture("_Mask") : null;

        // ---- pixels ----

        private static Baked Bake(Material m, SkinDef def, int tiles, int size)
        {
            Texture source = m.mainTexture;
            Texture mask = PaintMask(m);
            float shrink = Mathf.Min(1f, size / (float)Mathf.Max(source.width, source.height));
            Baked baked = new Baked
            {
                Width = Mathf.Max(4, Mathf.RoundToInt(source.width * shrink)),
                Height = Mathf.Max(4, Mathf.RoundToInt(source.height * shrink)),
            };
            baked.Source = Read(source, baked.Width, baked.Height, false);
            Color32[] maskPixels = mask == null ? null : Read(mask, baked.Width, baked.Height, true);
            baked.Result = Paint(baked.Source, maskPixels, baked.Width, baked.Height, def, tiles);
            return baked;
        }

        // Bundle textures are compressed and not readable: draw them into a
        // render texture and read that back.
        private static Color32[] Read(Texture texture, int w, int h, bool linear)
        {
            RenderTexture target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(readback);
            }
        }

        // The pattern colour takes the albedo's light and dark detail, relative
        // to the painted area's average brightness. Alpha (glow, cutout) is kept.
        private static Color32[] Paint(Color32[] source, Color32[] mask, int w, int h, SkinDef def, int tiles)
        {
            SkinTile tile = SkinPatterns.Tile(def);
            float[] weight = new float[source.Length];
            double sum = 0;
            int counted = 0;
            for (int i = 0; i < source.Length; i++)
            {
                float paint = mask == null ? 1f : Math.Max(mask[i].r, Math.Max(mask[i].g, mask[i].b)) / 255f;
                weight[i] = def.Overall ? 0.5f + 0.5f * paint : paint;
                if (paint > 0.5f)
                {
                    sum += Luma(source[i]);
                    counted++;
                }
            }
            float mean = Mathf.Max(8f, counted > 0 ? (float)(sum / counted) : 128f);
            Color32[] result = (Color32[])source.Clone();
            for (int y = 0; y < h; y++)
            {
                int ty = (int)((y + 0.5f) / h * tiles * SkinTile.Size) & SkinTile.Mask;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int t = ty * SkinTile.Size + ((int)((x + 0.5f) / w * tiles * SkinTile.Size) & SkinTile.Mask);
                    float amount = weight[i] * tile.Cover[t] / 255f * (def.Overall ? 1f : Strength);
                    if (amount <= 0.004f)
                    {
                        continue;
                    }
                    Color32 a = source[i];
                    float detail = Mathf.Clamp(Luma(a) / mean, 0.35f, 1.5f) * tile.Shade[t] / 128f;
                    Color32 c = SkinPatterns.Scale(def.Palette[tile.Class[t]], detail);
                    result[i] = new Color32(Mix(a.r, c.r, amount), Mix(a.g, c.g, amount), Mix(a.b, c.b, amount), a.a);
                }
            }
            return result;
        }

        private static float Luma(Color32 c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

        private static byte Mix(byte from, byte to, float t) => SkinPatterns.ToByte(from + (to - from) * t);

        // Mipmaps are made from the full-quality pixels before compressing;
        // a compressed texture cannot rebuild them.
        private static Texture2D Upload(Baked baked, Texture2D source, SkinDef def)
        {
            bool alpha = baked.Result.Any(p => p.a < 255);
            Texture2D texture = new Texture2D(baked.Width, baked.Height, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, true)
            {
                name = source.name + "_skin" + def.Index,
                wrapMode = source.wrapMode,
                filterMode = source.filterMode,
                anisoLevel = source.anisoLevel,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            texture.SetPixels32(baked.Result);
            texture.Apply(true, false);
            if (Settings.SkinCompress && baked.Width % 4 == 0 && baked.Height % 4 == 0)
            {
                texture.Compress(true);
            }
            texture.Apply(false, true);
            return texture;
        }

        // ---- cache ----

        // Drops the least recently used copies past the limit. Their materials
        // and textures stay alive while robots in the scene still use them;
        // Unity frees them once nothing does.
        private static void Evict()
        {
            while (Templates.Count > Settings.SkinCacheParts)
            {
                KeyValuePair<string, Template> oldest = Templates.OrderBy(p => p.Value.LastUsed).First();
                Templates.Remove(oldest.Key);
                Release(oldest.Value);
            }
        }

        private static void Release(Template entry)
        {
            if (entry.Go != null)
            {
                Object.Destroy(entry.Go);
            }
            entry.Materials.ForEach(m => m.hideFlags = HideFlags.None);
            foreach (string key in entry.TextureKeys.Where(k => !Templates.Values.Any(t => t.TextureKeys.Contains(k))).ToList())
            {
                Texture2D texture;
                if (Textures.TryGetValue(key, out texture) && texture != null)
                {
                    texture.hideFlags = HideFlags.None;
                }
                Textures.Remove(key);
            }
        }

        // ---- dump ----

        private static void WritePng(string path, Color32[] pixels, int w, int h)
        {
            Texture2D image = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                image.SetPixels32(pixels);
                image.Apply(false);
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        private static string Safe(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
    }
}
