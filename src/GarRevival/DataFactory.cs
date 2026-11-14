using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JunkWars.Client;
using JunkWars.Common.Garage.ValueObjects;
using JunkWars.Common.Guild.Templates;
using JunkWars.Common.Init;
using JunkWars.Common.Missions;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using UnityEngine;

namespace GarRevival
{
    // Builds the data the original servers kept in their database. Part
    // identities, types and docking points come from the part prefabs shipped
    // with the game; anything that only ever existed server-side (stats,
    // prices, missions) is a placeholder.
    internal static class DataFactory
    {
        public static BundleLoader PartsBundle()
        {
            BundleLoader loader;
            return BundleDatabase.Instance.TryGetBundleLoader("Parts", out loader) && loader.IsLoaded ? loader : null;
        }

        public static bool PartsReady()
        {
            return PartsBundle() != null;
        }

        public static Dictionary<string, PartTemplate> PartTemplates()
        {
            WritePartsCatalog();
            Catalog.EnsureBuilt();
            return Catalog.Parts;
        }

        public static Dictionary<string, ModuleTemplate> ModuleTemplates()
        {
            Catalog.EnsureBuilt();
            return Catalog.Modules;
        }

        public static Dictionary<string, PriceTemplate> Prices()
        {
            Catalog.EnsureBuilt();
            return Catalog.Prices;
        }

        public static Dictionary<string, AmmoTemplate> Ammo()
        {
            return new Dictionary<string, AmmoTemplate>();
        }

        private static List<int> _portraitIndices;

        public static List<int> PortraitIndices
        {
            get
            {
                Portraits();
                return _portraitIndices;
            }
        }

        // Every portrait icon shipped in the GUI textures, all free.
        public static Dictionary<int, PortraitTemplate> Portraits()
        {
            if (_portraitIndices == null)
            {
                _portraitIndices = new List<int>();
                for (int i = 0, misses = 0; misses < 3; i++)
                {
                    if (GUITextureManager.Instance.GetTextureByPath("Textures/GUI/Garage/Portraits/PortraitIcon_" + i) != null)
                    {
                        _portraitIndices.Add(i);
                        misses = 0;
                    }
                    else
                    {
                        misses++;
                    }
                }
                RevivalLog.Info("Found " + _portraitIndices.Count + " portraits");
            }
            return _portraitIndices.ToDictionary(i => i, i => new PortraitTemplate { indent = i, showInShop = false });
        }

        public static PlayerVO Player()
        {
            WorkshopOps.PrepareProfile();
            return PlayerState.Current;
        }

        public static PackagePriceTemplate PackagePrices()
        {
            PackagePriceTemplate template = WorkshopData.PackagePrices();
            CosmeticsOps.AddPackages(template);
            return template;
        }

        public static Dictionary<string, GuildBuffTemplate> GuildBuffs()
        {
            return new Dictionary<string, GuildBuffTemplate>();
        }

        private static bool _catalogWritten;

        // One line per prefab in the Parts bundle: what the export tooling
        // would have turned into a PartTemplate.
        private static void WritePartsCatalog()
        {
            if (_catalogWritten)
            {
                return;
            }
            _catalogWritten = true;
            try
            {
                AssetBundle bundle = PartsBundle().Bundle;
                string[] names = bundle.GetAllAssetNames();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("assetName | goName | exported | type | indent | keywords | skins | tints | dockingPoints");
                int withPrefab = 0;
                foreach (string name in names.OrderBy(n => n))
                {
                    GameObject go = bundle.LoadAsset<GameObject>(name);
                    if (go == null)
                    {
                        sb.AppendLine(name + " | (not a GameObject)");
                        continue;
                    }
                    PartPrefab pp = go.GetComponent<PartPrefab>();
                    DockingPoint[] points = go.GetComponentsInChildren<DockingPoint>(true);
                    string dockText = string.Join("; ", points.Select(p => p.pointName + (p.isAssemblyRoot ? "*" : "") + "(" + string.Join(",", (p.possibleDockingTypes ?? new LogicalType[0]).Select(t => t.ToString()).ToArray()) + ")").ToArray());
                    if (pp == null)
                    {
                        sb.AppendLine(name + " | " + go.name + " | (no PartPrefab) | | | | | | " + dockText);
                        continue;
                    }
                    withPrefab++;
                    sb.AppendLine(string.Join(" | ", new[]
                    {
                        name, go.name, pp.includeInExport.ToString(), pp.type.ToString(), pp.indent ?? "",
                        string.Join(",", pp.keywords ?? new string[0]),
                        (pp.skinsData == null ? 0 : pp.skinsData.Length).ToString(),
                        pp.tintColorsCount.ToString(), dockText,
                    }));
                }
                File.WriteAllText(Path.Combine(RevivalLog.Folder, "parts-catalog.txt"), sb.ToString());
                RevivalLog.Info("Parts bundle: " + names.Length + " assets, " + withPrefab + " with PartPrefab");
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Could not write parts catalog", ex);
            }
        }
    }
}
