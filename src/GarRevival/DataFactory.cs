using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JunkWars.Client;
using JunkWars.Common.Garage.ValueObjects;
using JunkWars.Common.Init;
using JunkWars.Common.Missions;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using UnityEngine;

namespace GarRevival;

// Startup data the client asks the server for: templates, prices, portraits and the profile.
internal static class DataFactory
{
    private static List<int> _portraitIndices;
    private static bool _catalogWritten;

    public static BundleLoader PartsBundle() =>
        BundleDatabase.Instance.TryGetBundleLoader("Parts", out var loader) && loader.IsLoaded ? loader : null;

    public static bool PartsReady() => PartsBundle() != null;

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

    public static Dictionary<string, AmmoTemplate> Ammo() => AmmoDesign.Templates();

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
            _portraitIndices = [];
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
            RevivalLog.Info($"Found {_portraitIndices.Count} portraits");
        }
        return _portraitIndices.ToDictionary(i => i, i => new PortraitTemplate { indent = i, showInShop = false });
    }

    public static PlayerVO Player()
    {
        WorkshopOps.PrepareProfile();
        AmmoOps.Resync(null);
        return PlayerState.Current;
    }

    public static PackagePriceTemplate PackagePrices()
    {
        var template = WorkshopData.PackagePrices();
        CosmeticsOps.AddPackages(template);
        return template;
    }

    // Diagnostic dump: one line per prefab in the Parts bundle.
    private static void WritePartsCatalog()
    {
        if (_catalogWritten) return;
        _catalogWritten = true;
        try
        {
            var bundle = PartsBundle().Bundle;
            string[] names = bundle.GetAllAssetNames();
            var sb = new StringBuilder();
            sb.AppendLine("assetName | goName | exported | type | indent | keywords | skins | tints | dockingPoints");
            int withPrefab = 0;
            foreach (string name in names.OrderBy(n => n))
            {
                var go = bundle.LoadAsset<GameObject>(name);
                if (go == null)
                {
                    sb.AppendLine(name + " | (not a GameObject)");
                    continue;
                }
                var pp = go.GetComponent<PartPrefab>();
                var points = go.GetComponentsInChildren<DockingPoint>(true);
                string dockText = string.Join("; ", points.Select(p =>
                    p.pointName + (p.isAssemblyRoot ? "*" : "") + "(" + string.Join(",", (p.possibleDockingTypes ?? []).Select(t => t.ToString()).ToArray()) + ")").ToArray());
                if (pp == null)
                {
                    sb.AppendLine($"{name} | {go.name} | (no PartPrefab) | | | | | | {dockText}");
                    continue;
                }
                withPrefab++;
                sb.AppendLine(string.Join(" | ", new[]
                {
                    name, go.name, pp.includeInExport.ToString(), pp.type.ToString(), pp.indent ?? "",
                    string.Join(",", pp.keywords ?? []),
                    (pp.skinsData?.Length ?? 0).ToString(),
                    pp.tintColorsCount.ToString(), dockText,
                }));
            }
            File.WriteAllText(Path.Combine(RevivalLog.Folder, "parts-catalog.txt"), sb.ToString());
            RevivalLog.Info($"Parts bundle: {names.Length} assets, {withPrefab} with PartPrefab");
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not write parts catalog", ex);
        }
    }
}
