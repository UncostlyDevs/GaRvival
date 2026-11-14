using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExitGames.Client.Photon;
using JunkWars.Client;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Utils.Enums;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;

namespace GarRevival
{
    // Answers the game's server operations. Op codes and parameter keys come
    // from the client's own LoadbalancingPeer / NetworkingPeer code. Changes to
    // the player are applied to PlayerState and announced with the same events
    // the original server sent (217 add items, 218 remove, 216 update,
    // 214/215 robot slots, 220 credits).
    internal static class GameServer
    {
        public const string LoginAddress = "garrevival-login";
        public const string GarageAddress = "garrevival-garage";
        public const string SecretUserId = "garrevival-offline";

        private static readonly HashSet<byte> LoggedUnknownOps = new HashSet<byte>();

        public static void Handle(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            RevivalLog.Info("op " + op + " from " + conn.Peer.GetType().Name + " (" + conn.Address + ")");
            // Feature areas with their own files answer first.
            try
            {
                if (MissionsOps.Handle(conn, op, request) || WorkshopOps.Handle(conn, op, request) || CosmeticsOps.Handle(conn, op, request) || SocialOps.Handle(conn, op, request))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("op " + op + " handler failed", ex);
                Fail(conn, op, "The server could not do that (see revival.log)");
                return;
            }
            switch (op)
            {
                // ---- login and startup data ----
                case 230: // Authenticate
                    Reply(conn, op);
                    break;
                case 221: // Login (user name + secret user id)
                    Reply(conn, op, new Dictionary<byte, object>
                    {
                        { 225, "garrevival-session" },
                        { 211, SecretUserId },
                        { 163, DateTime.UtcNow.ToString(CultureInfo.InvariantCulture) }, // server time; the client compares it with UtcNow
                        { 164, Environment.TickCount },
                        { 230, GarageAddress },
                    });
                    break;
                case 229: // Join garage lobby
                case 228: // Leave lobby
                    Reply(conn, op);
                    break;
                case 206: // Prices; built from the Parts bundle, so wait for it
                    ReplyWhenReady(conn, op, DataFactory.PartsReady, () => new Dictionary<byte, object> { { 231, DataSerialization.ProtoSerializeObjectHighCompressed(DataFactory.Prices()) } });
                    break;
                case 202: // Ammo
                    Reply(conn, op, new Dictionary<byte, object> { { 231, DataSerialization.ProtoSerializeObjectHighCompressed(DataFactory.Ammo()) } });
                    break;
                case 219: // Shop templates
                    ReplyWhenReady(conn, op, DataFactory.PartsReady, () => new Dictionary<byte, object>
                    {
                        { 231, DataSerialization.ProtoSerializeObjectHighCompressed(DataFactory.PartTemplates()) },
                        { 232, DataSerialization.ProtoSerializeObjectHighCompressed(DataFactory.ModuleTemplates()) },
                        { 169, DataSerialization.ProtoSerializeObjectHighCompressed(WorkshopData.Upgrades()) },
                        { 235, DataSerialization.ProtoSerializeObjectHighCompressed(WorkshopData.TimedBuffs()) },
                    });
                    break;
                case 161: // Weekly promotions
                    Reply(conn, op, new Dictionary<byte, object> { { 167, new string[0] } });
                    break;
                case 197: // Portraits
                    Reply(conn, op, new Dictionary<byte, object> { { 231, DataSerialization.ProtoSerializeObjectHighCompressed(DataFactory.Portraits()) } });
                    break;
                case 152: // Part skins
                    Reply(conn, op, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObjectHighCompressed(CosmeticsOps.PartSkins()) } });
                    break;
                case 149: // Tint colours
                    Reply(conn, op, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObjectHighCompressed(CosmeticsOps.TintColors()) } });
                    break;
                case 220: // Player profile
                    Reply(conn, op, new Dictionary<byte, object> { { 215, DataSerialization.ProtoSerializeObjectCompressed(DataFactory.Player()) } });
                    break;
                case 181: // Garage init data
                    Reply(conn, op, new Dictionary<byte, object>
                    {
                        { 168, DataSerialization.ProtoSerializeObject(CosmeticsOps.Promotions()) },
                        { 167, DataSerialization.ProtoSerializeObject(MissionsOps.MissionInit()) },
                        { 166, DataSerialization.ProtoSerializeObject(MissionsOps.SelectableMissions()) },
                        { 165, DataSerialization.ProtoSerializeObject(MissionsOps.MissionCategory()) },
                        { 164, DataSerialization.ProtoSerializeObjectHighCompressed(DataFactory.PackagePrices()) },
                        { 163, DataSerialization.ProtoSerializeObject(CosmeticsOps.Loot()) },
                    });
                    break;
                case 169: // Guild buffs
                    Reply(conn, op, new Dictionary<byte, object>
                    {
                        { 232, DataSerialization.ProtoSerializeObjectHighCompressed(SocialGuild.Upgrades()) },
                        { 231, DataSerialization.ProtoSerializeObjectHighCompressed(DataFactory.GuildBuffs()) },
                    });
                    break;
                case 211: // General chat (the other chat types are SocialOps'). 237 sender, 240 text, 168 guild
                    Session.SendChat((string)request[240], request.ContainsKey(168) ? (string)request[168] : null);
                    Reply(conn, op);
                    break;
                case 192: // Garage presence flag; the client has no handler for a reply
                    break;

                // ---- garage actions ----
                case 218:
                    if ((ItemType)(byte)request[234] == ItemType.Package && CosmeticsOps.TryBuyPackage(conn, op, (string)request[233]))
                    {
                        break;
                    }
                    BuyItem(conn, op, (string)request[233], (ItemType)(byte)request[234]);
                    break;
                case 217:
                    SellItem(conn, op, (int)request[233], (ItemType)(byte)request[234]);
                    break;
                case 216:
                    CreateModule(conn, op, DataSerialization.ProtoDeserializeObject<ModuleAssemblyVO>((byte[])request[235]));
                    break;
                case 215:
                    DestroyModule(conn, op, (int)request[233]);
                    break;
                case 214:
                    UpdateModule(conn, op, DataSerialization.ProtoDeserializeObject<ModuleAssemblyVO>((byte[])request[235]));
                    break;
                case 213:
                    CreateRobot(conn, op);
                    break;
                case 212:
                    UpdateRobot(conn, op, DataSerialization.ProtoDeserializeObject<RobotAssemblyVO>((byte[])request[236]));
                    break;
                case 209: // Select robot
                    PlayerState.Current.selectedRobotIndex = (byte)request[236];
                    PlayerState.Save();
                    Reply(conn, op);
                    break;
                case 204: // Auto repair
                case 203: // Auto resupply
                    SetRobotFlag(conn, op, (byte)request[236], (bool)request[218]);
                    break;
                case 200: // Ammo distribution
                    PlayerState.Current.ammoDistribution = DataSerialization.ProtoDeserializeObject<AmmoDistributionVO>((byte[])request[218]);
                    PlayerState.Save();
                    Reply(conn, op);
                    break;
                case 201: // Buy ammo: every weapon is an energy weapon for now
                    Reply(conn, op, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObject(PlayerState.Current.ownedAmmo) } });
                    break;
                case 198: // Set portrait
                case 199: // Unlock portrait (all are free)
                    PlayerState.Current.currentPortraitIndex = op == 198 ? (int)request[233] : PlayerState.Current.currentPortraitIndex;
                    PlayerState.Save();
                    Reply(conn, op, new Dictionary<byte, object> { { 233, request[233] } });
                    break;
                case 194: // Tutorial state
                    PlayerState.Current.tutorialState = (int)request[218];
                    PlayerState.Save();
                    Reply(conn, op, new Dictionary<byte, object> { { 218, request[218] } });
                    break;
                case 195: // License agreement state
                    PlayerState.Current.licenseAgreementState = (int)request[218];
                    PlayerState.Save();
                    Reply(conn, op, new Dictionary<byte, object> { { 218, request[218] } });
                    break;
                default:
                    if (LoggedUnknownOps.Add(op))
                    {
                        RevivalLog.Info("op " + op + " has no handler yet; answering with an empty success");
                    }
                    Reply(conn, op);
                    break;
            }
        }

        // ---- garage actions ----

        private static void BuyItem(Loopback.VirtualConnection conn, byte op, string id, ItemType type)
        {
            PlayerVO player = PlayerState.Current;
            ModuleTemplate moduleTemplate = null;
            PartTemplate partTemplate = null;
            float price;
            if (type == ItemType.Module && Catalog.Modules.TryGetValue(id, out moduleTemplate))
            {
                price = Catalog.ModuleBuyPrice(moduleTemplate);
            }
            else if (type == ItemType.Part && Catalog.Parts.TryGetValue(id, out partTemplate))
            {
                price = Catalog.BuyPrice(id, 0);
            }
            else
            {
                Fail(conn, op, "Unknown item " + id);
                return;
            }
            if (player.credits < price)
            {
                Fail(conn, op, "Not enough credits");
                return;
            }
            List<PartVO> parts = new List<PartVO>();
            List<ModuleAssemblyVO> modules = new List<ModuleAssemblyVO>();
            if (moduleTemplate != null)
            {
                modules.Add(PlayerState.CreateModuleFromTemplate(player, moduleTemplate, parts));
            }
            else
            {
                parts.Add(PlayerState.CreatePart(player, partTemplate));
            }
            RevivalLog.Info("Bought " + type + " " + id + " for " + price);
            MissionsOps.OnItemBought(id, type, price);
            AddItems(conn, parts, modules);
            ChangeCredits(conn, -price, ItemAddedReason.Buy);
            PlayerState.Save();
            Reply(conn, op);
        }

        private static void SellItem(Loopback.VirtualConnection conn, byte op, int id, ItemType type)
        {
            PlayerVO player = PlayerState.Current;
            List<int> partIds = new List<int>();
            List<int> moduleIds = new List<int>();
            if (type == ItemType.Module)
            {
                ModuleAssemblyVO module = PlayerState.FindModule(id);
                if (module == null)
                {
                    Fail(conn, op, "Unknown module");
                    return;
                }
                moduleIds.Add(id);
                partIds.AddRange(PlayerState.PartIdsOf(module));
            }
            else
            {
                partIds.Add(id);
            }
            float refund = partIds.Select(PlayerState.FindPart).Where(p => p != null).Sum(p => Catalog.SellPrice(p.indent, p.currentTier));
            player.ownedModuleAssemblies.RemoveAll(m => moduleIds.Contains(m.moduleID));
            player.ownedParts.RemoveAll(p => partIds.Contains(p.partID));
            RemoveItems(conn, partIds, moduleIds);
            ChangeCredits(conn, refund, ItemAddedReason.Buy);
            PlayerState.Save();
            Reply(conn, op);
        }

        // The garage assembles modules client-side and asks the server to
        // register them; the server assigns the id.
        private static void CreateModule(Loopback.VirtualConnection conn, byte op, ModuleAssemblyVO module)
        {
            VoNormalizer.FillNullCollections(module);
            PlayerVO player = PlayerState.Current;
            module.moduleID = Math.Max(player.lastUsedPartID, player.lastUsedModuleID) + 1;
            player.lastUsedPartID = module.moduleID;
            player.lastUsedModuleID = module.moduleID;
            player.ownedModuleAssemblies.Add(module);
            AddItems(conn, new List<PartVO>(), new List<ModuleAssemblyVO> { module });
            PlayerState.Save();
            Reply(conn, op);
        }

        private static void DestroyModule(Loopback.VirtualConnection conn, byte op, int id)
        {
            PlayerState.Current.ownedModuleAssemblies.RemoveAll(m => m.moduleID == id);
            RemoveItems(conn, new List<int>(), new List<int> { id });
            PlayerState.Save();
            Reply(conn, op);
        }

        private static void UpdateModule(Loopback.VirtualConnection conn, byte op, ModuleAssemblyVO module)
        {
            VoNormalizer.FillNullCollections(module);
            List<ModuleAssemblyVO> modules = PlayerState.Current.ownedModuleAssemblies;
            int index = modules.FindIndex(m => m.moduleID == module.moduleID);
            if (index < 0)
            {
                Fail(conn, op, "Unknown module");
                return;
            }
            modules[index] = module;
            Event(conn, 216, new Dictionary<byte, object> { { 232, DataSerialization.ProtoSerializeObject(new List<ModuleAssemblyVO> { module }) } });
            PlayerState.Save();
            Reply(conn, op);
        }

        private static void CreateRobot(Loopback.VirtualConnection conn, byte op)
        {
            PlayerVO player = PlayerState.Current;
            RobotAssemblyVO robot = PlayerState.EmptyRobot(player);
            player.ownedRobotAssemblies = player.ownedRobotAssemblies.Concat(new[] { robot }).ToArray();
            Event(conn, 215, new Dictionary<byte, object> { { 236, DataSerialization.ProtoSerializeObject(robot) } });
            PlayerState.Save();
            Reply(conn, op);
        }

        private static void UpdateRobot(Loopback.VirtualConnection conn, byte op, RobotAssemblyVO robot)
        {
            VoNormalizer.FillNullCollections(robot);
            RobotAssemblyVO[] robots = PlayerState.Current.ownedRobotAssemblies;
            int index = Array.FindIndex(robots, r => r.assemblyID == robot.assemblyID);
            if (index < 0)
            {
                Fail(conn, op, "Unknown robot");
                return;
            }
            robots[index] = robot;
            Event(conn, 214, new Dictionary<byte, object> { { 236, DataSerialization.ProtoSerializeObject(robot) } });
            PlayerState.Save();
            Reply(conn, op);
        }

        private static void SetRobotFlag(Loopback.VirtualConnection conn, byte op, byte index, bool state)
        {
            RobotAssemblyVO[] robots = PlayerState.Current.ownedRobotAssemblies;
            if (index < robots.Length)
            {
                if (op == 204)
                {
                    robots[index].autoRepair = state;
                }
                else
                {
                    robots[index].autoResupply = state;
                }
                PlayerState.Save();
            }
            Reply(conn, op);
        }

        // ---- events ----

        internal static void AddItems(Loopback.VirtualConnection conn, List<PartVO> parts, List<ModuleAssemblyVO> modules)
        {
            Dictionary<byte, object> data = new Dictionary<byte, object> { { 168, (byte)ItemAddedReason.Buy } };
            if (parts.Count > 0)
            {
                data[231] = DataSerialization.ProtoSerializeObject(parts);
            }
            if (modules.Count > 0)
            {
                data[232] = DataSerialization.ProtoSerializeObject(modules);
            }
            Event(conn, 217, data);
        }

        internal static void RemoveItems(Loopback.VirtualConnection conn, List<int> partIds, List<int> moduleIds)
        {
            Dictionary<byte, object> data = new Dictionary<byte, object>();
            if (partIds.Count > 0)
            {
                data[231] = DataSerialization.ProtoSerializeObject(partIds);
            }
            if (moduleIds.Count > 0)
            {
                data[232] = DataSerialization.ProtoSerializeObject(moduleIds);
            }
            Event(conn, 218, data);
        }

        internal static void ChangeCredits(Loopback.VirtualConnection conn, float delta, ItemAddedReason reason)
        {
            if (delta == 0f)
            {
                return;
            }
            PlayerVO player = PlayerState.Current;
            player.credits += delta;
            Event(conn, 220, new Dictionary<byte, object>
            {
                { 213, player.credits },
                { 214, delta },
                { 234, player.premiumCredits },
                { 218, 0f },
                { 168, (byte)reason },
            });
        }

        // Called by OfflineBattle after the profile has been updated. The
        // client is in the arena; it applies these when its message queue runs.
        public static void AnnounceBattleRewards(float credits, int experience, bool levelChanged)
        {
            Loopback.VirtualConnection conn = Loopback.Get(PhotonNetwork.networkingPeer);
            PlayerVO player = PlayerState.Current;
            ChangeCredits(conn, credits, ItemAddedReason.EndGameLoot);
            if (experience > 0)
            {
                Event(conn, 196, new Dictionary<byte, object> { { 170, player.experience }, { 214, experience } });
            }
            if (levelChanged)
            {
                Event(conn, 187, new Dictionary<byte, object> { { 169, player.level } });
            }
            Event(conn, 186, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObject(player.lifetimeStatistics) } });
        }

        // ---- chat ----

        // Shows a general chat line in the local player's garage chat (event 211).
        public static void DeliverChat(string senderFullName, string text, string guild)
        {
            RevivalLog.Info("chat " + senderFullName + ": " + text);
            Event(Loopback.Get(PhotonNetwork.networkingPeer), 211, new Dictionary<byte, object>
            {
                { 237, senderFullName },
                { 240, text },
                { 168, guild ?? string.Empty },
                { 167, (byte)2 }, // AccountGroupType.User
            });
        }

        // A line from the mod itself. The client drops the last five
        // characters of a sender's name as the account tag.
        public static void Notice(string text)
        {
            DeliverChat("Server.0000", text, string.Empty);
        }

        // ---- plumbing ----

        internal static void Event(Loopback.VirtualConnection conn, byte code, Dictionary<byte, object> parameters)
        {
            EventData data = new EventData { Code = code, Parameters = parameters };
            Loopback.Enqueue(conn, "event " + code, listener => listener.OnEvent(data));
        }

        internal static void Reply(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> parameters = null)
        {
            OperationResponse response = new OperationResponse
            {
                OperationCode = op,
                ReturnCode = 0,
                DebugMessage = string.Empty,
                Parameters = parameters ?? new Dictionary<byte, object>(),
            };
            Loopback.Enqueue(conn, "op " + op + " response", listener => listener.OnOperationResponse(response));
        }

        internal static void Fail(Loopback.VirtualConnection conn, byte op, string message)
        {
            RevivalLog.Info("op " + op + " failed: " + message);
            OperationResponse response = new OperationResponse { OperationCode = op, ReturnCode = 1, DebugMessage = message, Parameters = new Dictionary<byte, object>() };
            Loopback.Enqueue(conn, "op " + op + " failure", listener => listener.OnOperationResponse(response));
        }

        private static void ReplyWhenReady(Loopback.VirtualConnection conn, byte op, Func<bool> isReady, Func<Dictionary<byte, object>> build)
        {
            Loopback.Enqueue(conn, "op " + op + " response", listener =>
            {
                Dictionary<byte, object> parameters = build();
                RevivalLog.Info("op " + op + " payload: " + string.Join(", ", parameters.Select(p => p.Key + "=" + (p.Value is byte[] ? ((byte[])p.Value).Length + " bytes" : p.Value)).ToArray()));
                OperationResponse response = new OperationResponse
                {
                    OperationCode = op,
                    ReturnCode = 0,
                    DebugMessage = string.Empty,
                    Parameters = parameters,
                };
                listener.OnOperationResponse(response);
            }, isReady);
        }
    }
}
