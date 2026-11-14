using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Client;
using JunkWars.Common.Init;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Templates;
using UnityEngine;

namespace GarRevival
{
    // Startup data for the cosmetics screens. Skins come from the part prefabs
    // shipped with the game; the palette, loot tables, rewards and store packs
    // are built from CosmeticsDesign because they only ever lived on the server.
    internal static partial class CosmeticsOps
    {
        // ---- part skins (operation 152) ----

        private static Dictionary<string, PartSkinsTemplate> _skins;

        // What each part prefab says about its own skins. A skin is the prefab
        // "<part prefab>_s<index>" in the same bundle.
        public static Dictionary<string, PartSkinsTemplate> PartSkins()
        {
            if (_skins != null)
            {
                return _skins;
            }
            Dictionary<string, PartSkinsTemplate> skins = new Dictionary<string, PartSkinsTemplate>();
            BundleLoader loader = DataFactory.PartsBundle();
            if (loader == null)
            {
                RevivalLog.Info("Part skins asked for before the Parts bundle was loaded");
                return skins;
            }
            Catalog.EnsureBuilt();
            AssetBundle bundle = loader.Bundle;
            foreach (string asset in bundle.GetAllAssetNames())
            {
                GameObject go = bundle.LoadAsset<GameObject>(asset);
                PartPrefab prefab = go == null ? null : go.GetComponent<PartPrefab>();
                if (prefab == null || !prefab.includeInExport || prefab.skinsData == null)
                {
                    continue;
                }
                string indent = string.IsNullOrEmpty(prefab.indent) ? go.name : prefab.indent;
                // Index 0 is the part's own look.
                PartSkinData[] usable = prefab.skinsData.Where(s => s != null && s.index > 0).ToArray();
                if (usable.Length == 0 || !Catalog.Parts.ContainsKey(indent) || skins.ContainsKey(indent))
                {
                    continue;
                }
                skins[indent] = new PartSkinsTemplate { partIndent = indent, skins = usable, lifetimeDays = prefab.partSkinsLifetimeDays };
            }
            RevivalLog.Info("Part skins: " + skins.Count + " parts have skins (" + skins.Values.Sum(s => s.skins.Length) + " skins)");
            _skins = skins;
            return _skins;
        }

        // ---- paint colours (operation 149) ----

        private static Dictionary<int, TintColorTemplate> _colors;

        public static Dictionary<int, TintColorTemplate> TintColors()
        {
            if (_colors == null)
            {
                _colors = new Dictionary<int, TintColorTemplate>();
                foreach (CosmeticsDesign.ColorDef def in CosmeticsDesign.Colors)
                {
                    _colors[def.Index] = new TintColorTemplate
                    {
                        index = def.Index,
                        // The paint screen reads categories[0] and normalBuy[0] of
                        // every colour without checking, so both always exist.
                        categories = new[] { def.Category },
                        data = new TintColorData
                        {
                            indent = ColorIndent(def),
                            r = ((def.Rgb >> 16) & 0xFF) / 255f,
                            g = ((def.Rgb >> 8) & 0xFF) / 255f,
                            b = (def.Rgb & 0xFF) / 255f,
                            a = 1f,
                            uiData = new UIData { name = ColorIndent(def), description = string.Empty, icon = string.Empty },
                            normalBuy = new[] { new PricePackageItem { type = PackageType.PremiumCredits, quantity = CosmeticsDesign.ColorPrice(def.Category) } },
                            purchasable = !def.CrateOnly,
                            showInShop = !def.CrateOnly,
                        },
                    };
                }
                RevivalLog.Info("Paint palette: " + _colors.Count + " colours");
            }
            // This is asked for just before the profile, so the starter colours
            // are in place when the client first sees it. (The profile cannot
            // be loaded before the Parts bundle.)
            if (DataFactory.PartsReady())
            {
                EnsureStarterColors(PlayerState.Current);
            }
            return _colors;
        }

        internal static string ColorIndent(CosmeticsDesign.ColorDef def)
        {
            return "Tint_" + def.Name;
        }

        internal static CosmeticsDesign.ColorDef ColorByIndent(string indent)
        {
            return CosmeticsDesign.Colors.FirstOrDefault(c => ColorIndent(c) == indent);
        }

        internal static CosmeticsDesign.ColorDef ColorByIndex(int index)
        {
            return CosmeticsDesign.Colors.FirstOrDefault(c => c.Index == index);
        }

        internal static void EnsureStarterColors(PlayerVO player)
        {
            if (player.ownedTintColors == null)
            {
                player.ownedTintColors = new List<int>();
            }
            int before = player.ownedTintColors.Count;
            foreach (CosmeticsDesign.ColorDef def in CosmeticsDesign.Colors)
            {
                if (def.Category == CosmeticsDesign.Starter && !player.ownedTintColors.Contains(def.Index))
                {
                    player.ownedTintColors.Add(def.Index);
                }
            }
            if (player.ownedTintColors.Count != before)
            {
                PlayerState.Save();
                RevivalLog.Info("Starter paint colours added to the profile");
            }
        }

        // ---- loot tables (operation 181, key 163) ----

        private static List<LootTemplate> _crateLoot;
        private static LootTemplate _battleLoot;

        // One table per crate type, named the way the client looks them up
        // ("CrateType.Bronze"). It lists them as a crate's possible contents.
        public static List<LootTemplate> Loot()
        {
            if (_crateLoot == null)
            {
                Catalog.EnsureBuilt();
                _crateLoot = CosmeticsDesign.Crates.Select(def => BuildCrateLoot(def)).ToList();
            }
            return _crateLoot;
        }

        internal static LootTemplate CrateLoot(CrateType type)
        {
            string indent = "CrateType." + type;
            return Loot().Find(t => t.indent == indent);
        }

        private static LootTemplate BuildCrateLoot(CosmeticsDesign.CrateDef def)
        {
            LootTemplate template = new LootTemplate
            {
                indent = "CrateType." + def.Type,
                randomItems = new List<LootTemplate.RandomLootItem>
                {
                    new LootTemplate.RandomLootItem
                    {
                        itemType = PackageType.Credits, specificIndent = string.Empty, probability = Number(1f),
                        minCount = Number(def.CreditsMin, def.CreditsMinTopRank), maxCount = Number(def.CreditsMax, def.CreditsMaxTopRank),
                    },
                    RandomItem(PackageType.PremiumCredits, string.Empty, def.SparksChance, def.SparksMin, def.SparksMax),
                    RandomItem(PackageType.PaintComponent, string.Empty, def.PaintChance, def.PaintMin, def.PaintMax),
                    RandomItem(PackageType.Lockpick, string.Empty, def.LockpickChance, 1, 1),
                },
                groups = new List<LootTemplate.LootGroup>(),
            };

            List<LootTemplate.LootGroupItem> colors = new List<LootTemplate.LootGroupItem>();
            foreach (string name in def.Colors)
            {
                CosmeticsDesign.ColorDef color = CosmeticsDesign.Colors.FirstOrDefault(c => c.Name == name);
                if (color == null)
                {
                    RevivalLog.Info(def.Type + " crate: no colour named " + name);
                    continue;
                }
                colors.Add(GroupItem(PackageType.TintColor, ColorIndent(color)));
            }
            AddGroup(template, colors, def.ColorChance);

            List<LootTemplate.LootGroupItem> items = new List<LootTemplate.LootGroupItem>();
            foreach (string id in def.Items)
            {
                if (Catalog.Modules.ContainsKey(id))
                {
                    items.Add(GroupItem(PackageType.Module, id));
                }
                else if (Catalog.Parts.ContainsKey(id))
                {
                    items.Add(GroupItem(PackageType.Part, id));
                }
                else
                {
                    RevivalLog.Info(def.Type + " crate: " + id + " is not in the catalog");
                }
            }
            AddGroup(template, items, def.ItemChance);
            return template;
        }

        // Server-side only: what a finished battle drops.
        internal static LootTemplate BattleLoot()
        {
            if (_battleLoot == null)
            {
                _battleLoot = new LootTemplate { indent = "EndGame", randomItems = new List<LootTemplate.RandomLootItem>() };
                for (int i = 0; i < CosmeticsDesign.BattleCrateChance.Length; i++)
                {
                    _battleLoot.randomItems.Add(RandomItem(PackageType.Crate, ((CrateType)i).ToString(), CosmeticsDesign.BattleCrateChance[i], 1, 1));
                }
                _battleLoot.randomItems.Add(RandomItem(PackageType.PaintComponent, string.Empty, CosmeticsDesign.BattlePaintChance, CosmeticsDesign.BattlePaintMin, CosmeticsDesign.BattlePaintMax));
                _battleLoot.randomItems.Add(RandomItem(PackageType.Lockpick, string.Empty, CosmeticsDesign.BattleLockpickChance, 1, 1));
            }
            return _battleLoot;
        }

        // The client reads these number objects without null checks.
        private static LootNumericProperty Number(float value, float atTopRank = 0f)
        {
            return new LootNumericProperty { baseValue = value, maxScaleValue = atTopRank };
        }

        private static LootTemplate.RandomLootItem RandomItem(PackageType type, string indent, float chance, int min, int max)
        {
            return new LootTemplate.RandomLootItem { itemType = type, specificIndent = indent, probability = Number(chance), minCount = Number(min), maxCount = Number(max) };
        }

        private static LootTemplate.LootGroupItem GroupItem(PackageType type, string indent)
        {
            return new LootTemplate.LootGroupItem { itemType = type, specificIndent = indent, minCount = Number(1f), maxCount = Number(1f), probabilityWeightInt = Number(1f) };
        }

        // The client walks a group's item list without a null check and an
        // empty list arrives as null, so empty groups are left out.
        private static void AddGroup(LootTemplate template, List<LootTemplate.LootGroupItem> items, float chance)
        {
            if (items.Count == 0)
            {
                return;
            }
            template.groups.Add(new LootTemplate.LootGroup
            {
                possibleItems = items,
                minSlotsCount = Number(1f),
                maxSlotsCount = Number(1f),
                allowSameRandomItemSubtype = false,
                probability = Number(chance),
            });
        }

        // ---- daily login and other promotions (operation 181, key 168) ----

        private static PromotionsTemplate _promotions;

        internal static PromotionsTemplate DailyRewardsTemplate()
        {
            if (_promotions == null)
            {
                _promotions = new PromotionsTemplate();
                for (int day = 0; day < CosmeticsDesign.DailyLogin.Length; day++)
                {
                    Package reward = new Package { id = day + 1 };
                    if (CosmeticsDesign.DailyLogin[day][0] > 0)
                    {
                        reward.items.Add(new PackageItem { type = PackageType.Credits, quantity = CosmeticsDesign.DailyLogin[day][0] });
                    }
                    if (CosmeticsDesign.DailyLogin[day][1] > 0)
                    {
                        reward.items.Add(new PackageItem { type = PackageType.PremiumCredits, quantity = CosmeticsDesign.DailyLogin[day][1] });
                    }
                    _promotions.DailyLoginRewards.Add(reward);
                }
            }
            return _promotions;
        }

        public static PromotionsVO Promotions()
        {
            return new PromotionsVO { promotionsTemplate = DailyRewardsTemplate(), dailyLoginRewardActiveIndex = DailyLoginIndex(PlayerState.Current) };
        }

        // Which day of the streak can be claimed now; -1 once today's is taken.
        // A missed day starts the streak again.
        internal static int DailyLoginIndex(PlayerVO player)
        {
            if (player.playerPromotions == null)
            {
                player.playerPromotions = new PlayerPromotionsVO();
            }
            PlayerPromotionsVO promo = player.playerPromotions;
            if (promo.dailyLoginLastDate == DateTime.MinValue)
            {
                return 0;
            }
            // Whole days, so a daylight-saving change does not break the streak.
            int days = (int)Math.Round((DateTime.Today - promo.dailyLoginLastDate.Date).TotalDays);
            if (days <= 0)
            {
                return -1;
            }
            return days > 1 ? 0 : Math.Max(0, promo.dailyLoginCount) % DailyRewardsTemplate().DailyLoginRewards.Count;
        }

        // ---- store packs (operation 181, key 164) ----

        private static List<PackagePriceTemplate.PricePackage> _packages;

        // Paint sprays, multi-tools and crate keys. The crate window looks up
        // the first single-item pack of a key type / of multi-tools for its Buy
        // buttons, so the single ones come first.
        public static List<PackagePriceTemplate.PricePackage> Packages()
        {
            if (_packages == null)
            {
                _packages = new List<PackagePriceTemplate.PricePackage>();
                foreach (int[] pack in CosmeticsDesign.LockpickPacks)
                {
                    AddPack(PackageType.Lockpick, string.Empty, pack[0], PackageType.Credits, pack[1]);
                }
                foreach (int size in CosmeticsDesign.KeyPackSizes)
                {
                    foreach (CosmeticsDesign.CrateDef crate in CosmeticsDesign.Crates)
                    {
                        float price = size == 1 ? crate.KeySparks : (float)Math.Round(crate.KeySparks * size * CosmeticsDesign.KeyBulkDiscount);
                        AddPack(PackageType.CrateKey, crate.Type.ToString(), size, PackageType.PremiumCredits, price);
                    }
                }
                foreach (int[] pack in CosmeticsDesign.PaintPacks)
                {
                    AddPack(PackageType.PaintComponent, string.Empty, pack[0], PackageType.Credits, pack[1]);
                }
            }
            return _packages;
        }

        private static void AddPack(PackageType type, string subType, int quantity, PackageType currency, float price)
        {
            PackagePriceTemplate.PricePackage pack = new PackagePriceTemplate.PricePackage
            {
                indent = "Pack_" + type + (subType.Length == 0 ? string.Empty : "_" + subType) + "_x" + quantity,
                showInShop = true,
                itemPackage = new Package { id = 9000 + _packages.Count },
                normalBuy = new[] { new PricePackageItem { type = currency, quantity = price } },
            };
            pack.itemPackage.items.Add(new PackageItem { type = type, indent = subType, quantity = quantity });
            _packages.Add(pack);
        }

        // Adds the packs above to the store's package list.
        public static void AddPackages(PackagePriceTemplate template)
        {
            if (template.packages == null)
            {
                template.packages = new List<PackagePriceTemplate.PricePackage>();
            }
            foreach (PackagePriceTemplate.PricePackage pack in Packages())
            {
                if (!template.packages.Any(p => p.indent == pack.indent))
                {
                    template.packages.Add(pack);
                }
            }
        }
    }
}
