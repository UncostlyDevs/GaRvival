using System.Linq;
using JunkWars.Common.Packages;
using JunkWars.Common.Robot.Templates;
using UnityEngine;

namespace GarRevival
{
    internal enum SkinPattern { Camo, Digital, Tiger, Hazard, Carbon, Rust, Brushed, Circuit, Flames }

    internal sealed class SkinDef
    {
        public byte Index;
        public string Name;
        public SkinPattern Pattern;
        // Metres one pattern tile covers on a part.
        public float WorldSize;
        // Also covers the unpainted areas (at half strength), as rust does.
        public bool Overall;
        public Color32[] Palette;

        public uint Seed => Index * 2654435761u;
        public string Rarity => Index < 50 ? "Common" : Index < 100 ? "Rare" : "Epic";
    }

    // The skins sold for every part. Indexes are saved in profiles (PartVO.skinIndex,
    // ownedPartSkins) and sent to other players: never renumber, only add.
    // 1-49 cost credits, 50-99 and 100+ cost sparks.
    internal static class SkinCatalog
    {
        public static readonly SkinDef[] All =
        {
            D(1, "Woodland", SkinPattern.Camo, 1.2f, 0x5E6B34, 0x2F3A1C, 0x4E3B26),
            D(2, "Desert", SkinPattern.Camo, 1.2f, 0xC8AD7F, 0x9C7A4D, 0x6E5A3C),
            D(3, "Urban", SkinPattern.Camo, 1.2f, 0x8A8D91, 0x4C4F54, 0xD0D2D4),
            D(4, "Arctic", SkinPattern.Camo, 1.2f, 0xE6EBEE, 0xA9B4BC, 0x5E6A73),
            D(5, "Digital", SkinPattern.Digital, 1.0f, 0x7A8458, 0x4B5535, 0x2B3021),
            D(6, "Hazard", SkinPattern.Hazard, 0.8f, 0xE8B416, 0x1E1E1E, 0x1E1E1E),
            D(50, "Tiger", SkinPattern.Tiger, 1.2f, 0xD9781E, 0x1A1410, 0xF2E3C6),
            D(51, "Carbon Fibre", SkinPattern.Carbon, 0.6f, 0x2A2C30, 0x1A1B1E, 0x3A3D42),
            D(52, "Rust", SkinPattern.Rust, 1.0f, 0x8A3B12, 0x5A2A10, 0xB8621E, true),
            D(53, "Brushed Steel", SkinPattern.Brushed, 0.8f, 0xC9CED4, 0xC9CED4, 0xC9CED4),
            D(100, "Circuit", SkinPattern.Circuit, 0.6f, 0x0F3B23, 0xC07A2C, 0xE0C05A),
            D(101, "Flames", SkinPattern.Flames, 1.0f, 0x1A1210, 0xE2541A, 0xF6C33A),
        };

        private const float CommonCredits = 3000f;
        private const float RareSparks = 25f;
        private const float EpicSparks = 60f;

        private static PartSkinData[] _data;

        public static SkinDef ByIndex(int index) => All.FirstOrDefault(d => d.Index == index);

        public static PricePackageItem Price(SkinDef def)
        {
            switch (def.Rarity)
            {
                case "Common": return new PricePackageItem { type = PackageType.Credits, quantity = CommonCredits, indent = string.Empty };
                case "Rare": return new PricePackageItem { type = PackageType.PremiumCredits, quantity = RareSparks, indent = string.Empty };
                default: return new PricePackageItem { type = PackageType.PremiumCredits, quantity = EpicSparks, indent = string.Empty };
            }
        }

        public static string PriceText(SkinDef def)
        {
            PricePackageItem price = Price(def);
            return price.quantity.ToString("#,0") + (price.type == PackageType.Credits ? " credits" : " sparks");
        }

        // What op 152 sends for each part template. Bought skins never run out.
        public static PartSkinData[] SkinData()
        {
            return _data ?? (_data = All.Select(d => new PartSkinData
            {
                index = d.Index,
                uiData = new UIData { name = "Skin_" + d.Name, description = string.Empty, icon = string.Empty },
                normalBuy = new[] { Price(d) },
                purchasable = true,
                showInShop = true,
            }).ToArray());
        }

        // "<prefabPath>_s<N>" is how the client asks for a part's skin N.
        public static bool TryParsePath(string path, out string basePath, out byte index)
        {
            basePath = null;
            index = 0;
            int at = path == null ? -1 : path.LastIndexOf("_s", System.StringComparison.Ordinal);
            if (at <= 0 || at + 2 >= path.Length || path.Length - at - 2 > 3 || !path.Skip(at + 2).All(char.IsDigit))
            {
                return false;
            }
            int number = int.Parse(path.Substring(at + 2));
            if (number < 1 || number > 255)
            {
                return false;
            }
            basePath = path.Substring(0, at);
            index = (byte)number;
            return true;
        }

        private static SkinDef D(byte index, string name, SkinPattern pattern, float worldSize, int c0, int c1, int c2, bool overall = false)
        {
            return new SkinDef
            {
                Index = index,
                Name = name,
                Pattern = pattern,
                WorldSize = worldSize,
                Overall = overall,
                Palette = new[] { Rgb(c0), Rgb(c1), Rgb(c2) },
            };
        }

        private static Color32 Rgb(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
    }
}
