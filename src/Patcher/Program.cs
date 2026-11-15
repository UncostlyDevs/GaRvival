using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GarRevival.Patcher
{
    // Installs the GAR Revival mod into a Guns and Robots install by inserting
    // hook calls into two game assemblies. Originals are kept next to the
    // patched files (*.garrevival-orig) and every install re-patches from
    // them, so running it twice is safe and --uninstall restores the game.
    internal static class Program
    {
        private const string ModAssemblyName = "GarRevival";
        private const string ModDllName = "GarRevival.dll";
        private const string BackupSuffix = ".garrevival-orig";

        private sealed class HookSpec
        {
            public string Type;
            public string Method;
            public string[] Params;
            public string Hook;
        }

        private static HookSpec H(string type, string method, string[] parameters, string hook)
        {
            return new HookSpec { Type = type, Method = method, Params = parameters, Hook = hook };
        }

        private const string Peer = "ExitGames.Client.Photon.PhotonPeer";
        private const string Str = "System.String";
        private const string U8 = "System.Byte";
        private const string Bool = "System.Boolean";
        private const string ParamDict = "System.Collections.Generic.Dictionary`2<System.Byte,System.Object>";

        private static readonly Dictionary<string, HookSpec[]> Targets = new Dictionary<string, HookSpec[]>
        {
            {
                "Photon3Unity3D.dll", new[]
            {
                H(Peer, "Connect", new[] { Str, Str }, "PeerConnect2"),
                H(Peer, "Connect", new[] { Str, Str, U8 }, "PeerConnect3"),
                H(Peer, "Disconnect", new string[0], "PeerDisconnect"),
                H(Peer, "StopThread", new string[0], "PeerStopThread"),
                H(Peer, "FetchServerTimestamp", new string[0], "PeerFetchServerTimestamp"),
                H(Peer, "EstablishEncryption", new string[0], "PeerEstablishEncryption"),
                H(Peer, "SendOutgoingCommands", new string[0], "PeerSendOutgoingCommands"),
                H(Peer, "DispatchIncomingCommands", new string[0], "PeerDispatchIncomingCommands"),
                H(Peer, "OpCustom", new[] { U8, ParamDict, Bool, U8 }, "PeerOpCustom4"),
                H(Peer, "OpCustom", new[] { U8, ParamDict, Bool, U8, Bool }, "PeerOpCustom5"),
                H(Peer, "OpCustom", new[] { "ExitGames.Client.Photon.OperationRequest", Bool, U8, Bool }, "PeerOpCustomRequest"),
                H(Peer, "get_PeerState", new string[0], "PeerGetPeerState"),
                H(Peer, "get_IsEncryptionAvailable", new string[0], "PeerGetIsEncryptionAvailable"),
                H(Peer, "get_ServerTimeInMilliSeconds", new string[0], "PeerGetServerTime"),
            }
            },
            {
                "Assembly-CSharp.dll", new[]
            {
                H("SteamDeployLogin", "Start", new string[0], "SteamDeployLoginStart"),
                H("JunkWars.Client.Player", "SetPlayerData", new[] { "JunkWars.Common.Player.ValueObjects.PlayerVO" }, "PlayerSetPlayerData"),
                H("JunkWars.Client.Player", "AddItemValueObjects", new[] { "JunkWars.Client.ItemToAddPack" }, "PlayerAddItemValueObjects"),
                H("JunkWars.Client.Player", "UpdateItemValueObjects", new[] { "JunkWars.Client.ItemToUpdatePack" }, "PlayerUpdateItemValueObjects"),
                H("JunkWars.Client.Player", "UpdateRobotValueObjects", new[] { "JunkWars.Common.Robot.ValueObjects.RobotAssemblyVO" }, "PlayerUpdateRobotValueObjects"),
                H("JunkWars.Client.DataManager", "OnShopData", new[] { "DataMessage" }, "DataManagerOnShopData"),
                H("PhotonNetwork", "JoinRandomBattle", new string[0], "PhotonNetworkJoinRandomBattle"),
                H("EntryPoint", "StartAntiCheatEngine", new string[0], "EntryPointStartAntiCheatEngine"),
                H("JunkWars.Client.Arena.BaseArena", "GetSpawingPoint", new[] { "JunkWars.Common.Player.ValueObjects.ActorVO" }, "BaseArenaGetSpawingPoint"),
                H("JunkWars.Client.Garage.UI.GarageInfoScreenSubmenu", "Start", new string[0], "GarageInfoScreenSubmenuStart"),
                H("JunkWars.Client.Arena.OfflineArena", "AddRobots", new string[0], "OfflineArenaAddRobots"),
                H("JunkWars.Client.Robot.RobotAvatar", "SetFreeLook", new[] { "System.Boolean" }, "RobotAvatarSetFreeLook"),
                H("OfflineArenaCombatant", "ExecuteShoot", new[] { "System.Int32", "System.Int32", "System.Int32", "System.Int16", "System.Nullable`1<System.Byte>", "System.Nullable`1<System.Byte>", "JunkWars.Client.Robot.WeaponHitPoint[]", "UnityEngine.Vector3", "System.Object[]" }, "CombatantExecuteShoot"),
                H("OfflineArenaCombatant", "ExecuteHit", new[] { "System.Int32", "System.Int32", "System.Int32", "System.Int16", "System.Nullable`1<System.Byte>", "JunkWars.Client.Robot.WeaponHitPoint[]", "UnityEngine.Vector3", "System.Object[]" }, "CombatantExecuteHit"),
                H("OfflineArenaCombatant", "ExecuteDeviceActivate", new[] { "System.Int32", "UnityEngine.Vector3", "UnityEngine.Quaternion" }, "CombatantExecuteDeviceActivate"),
                H("OfflineArenaCombatant", "ExecuteDeviceDeactivate", new[] { "System.Int32", "System.Byte" }, "CombatantExecuteDeviceDeactivate"),
                H("OfflineArenaCombatant", "ExecuteConsumableBoost", new[] { "System.Int32" }, "CombatantExecuteConsumableBoost"),
                H("PlaformIntegration.Steam.SteamPurchaseService", "InitCurrencyPurchase", new[] { "System.Int32" }, "SteamPurchaseServiceInitCurrencyPurchase"),
                H("AssemblyMouseManager", "SelectGrageInteractiveObject", new string[0], "AssemblyMouseManagerSelectGrageInteractiveObject"),
                H("JunkWars.Client.Arena.Offline.OfflineServerEmulator", "HandlePickPickableOperation", new[] { "System.Int32", "System.Int32" }, "EmulatorHandlePick"),
                H("JunkWars.Client.Arena.Offline.OfflineServerEmulator", "HandleGameModeEvent", new[] { "System.Int32", "System.Collections.Hashtable" }, "EmulatorHandleGameModeEvent"),
                H("JunkWars.Client.Arena.Offline.OfflineServerEmulator", "PublishStartCountdownEvent", new string[0], "EmulatorPublishStartCountdown"),
                H("JunkWars.Client.Arena.Offline.OfflineServerEmulator", "PublishEndGameEvent", new[] { "System.Object" }, "EmulatorPublishEndGame"),
                H("JunkWars.Client.Arena.OfflineArena", "OnEndGame", new[] { "DataMessage" }, "OfflineArenaOnEndGame"),
            }
            },
        };

        private static int Main(string[] args)
        {
            bool uninstall = args.Contains("--uninstall");
            string gameDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindGameDir();
            if (gameDir == null || !File.Exists(Path.Combine(gameDir, "GAR.exe")))
            {
                Console.Error.WriteLine("Could not find GAR.exe. Pass the Guns and Robots install folder as an argument.");
                return 1;
            }

            string managed = Path.Combine(gameDir, "GAR_Data", "Managed");
            try
            {
                if (uninstall)
                {
                    Uninstall(managed);
                }
                else
                {
                    Install(managed);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed: " + ex.Message);
                return 1;
            }
        }

        private static string FindGameDir()
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (DirectoryInfo dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "GAR.exe")))
                    {
                        return dir.FullName;
                    }
                }
            }
            return null;
        }

        private static void Install(string managed)
        {
            string modSource = Path.Combine(AppContext.BaseDirectory, ModDllName);
            if (!File.Exists(modSource))
            {
                throw new FileNotFoundException(ModDllName + " must sit next to the patcher.", modSource);
            }

            foreach (KeyValuePair<string, HookSpec[]> target in Targets)
            {
                string path = Path.Combine(managed, target.Key);
                string backup = path + BackupSuffix;
                if (!File.Exists(backup))
                {
                    if (ReferencesMod(path))
                    {
                        throw new InvalidOperationException(target.Key + " is already patched but its backup is missing. Use Steam's \"Verify integrity of game files\" and run the patcher again.");
                    }
                    File.Copy(path, backup);
                    Console.WriteLine("Backed up " + target.Key);
                }
                Patch(managed, backup, path, target.Value);
                Console.WriteLine("Patched " + target.Key + " (" + target.Value.Length + " hooks)");
            }

            File.Copy(modSource, Path.Combine(managed, ModDllName), overwrite: true);
            Console.WriteLine("Installed " + ModDllName);
            Console.WriteLine("Done. Start Guns and Robots from Steam as usual.");
        }

        private static void Uninstall(string managed)
        {
            foreach (string file in Targets.Keys)
            {
                string path = Path.Combine(managed, file);
                string backup = path + BackupSuffix;
                if (File.Exists(backup))
                {
                    File.Copy(backup, path, overwrite: true);
                    File.Delete(backup);
                    Console.WriteLine("Restored " + file);
                }
            }
            string mod = Path.Combine(managed, ModDllName);
            if (File.Exists(mod))
            {
                File.Delete(mod);
                Console.WriteLine("Removed " + ModDllName);
            }
            Console.WriteLine("Done. The game is back to its original files.");
        }

        private static bool ReferencesMod(string path)
        {
            using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(path))
            {
                return asm.MainModule.AssemblyReferences.Any(r => r.Name == ModAssemblyName);
            }
        }

        private static void Patch(string managed, string source, string destination, HookSpec[] hooks)
        {
            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managed);
            using (MemoryStream input = new MemoryStream(File.ReadAllBytes(source)))
            using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
            {
                ModuleDefinition module = asm.MainModule;
                AssemblyNameReference modRef = new AssemblyNameReference(ModAssemblyName, new Version(1, 0, 0, 0));
                module.AssemblyReferences.Add(modRef);
                TypeReference hooksType = new TypeReference(ModAssemblyName, "Hooks", module, modRef);

                foreach (HookSpec spec in hooks)
                {
                    HookSpec s = spec;
                    TypeDefinition type = module.GetType(s.Type);
                    if (type == null)
                    {
                        throw new InvalidOperationException("Type not found: " + s.Type);
                    }
                    MethodDefinition method = type.Methods.SingleOrDefault(m => m.Name == s.Method && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(s.Params));
                    if (method == null)
                    {
                        throw new InvalidOperationException("Method not found: " + s.Type + "." + s.Method + "(" + string.Join(", ", s.Params) + ")");
                    }
                    InsertPrefix(module, method, hooksType, spec.Hook);
                }

                asm.Write(destination);
            }
        }

        // Prepends: if (Hooks.<hook>(this, args..., ref result)) return result;
        // Game-defined class types are passed as object so the mod's hook
        // signatures stay simple; mscorlib types and value types keep their type.
        private static void InsertPrefix(ModuleDefinition module, MethodDefinition method, TypeReference hooksType, string hookName)
        {
            MethodReference hook = new MethodReference(hookName, module.TypeSystem.Boolean, hooksType) { HasThis = false };
            if (method.HasThis)
            {
                hook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            }
            foreach (ParameterDefinition p in method.Parameters)
            {
                hook.Parameters.Add(new ParameterDefinition(MapToHookType(module, p.ParameterType)));
            }

            bool hasResult = method.ReturnType.MetadataType != MetadataType.Void;
            VariableDefinition result = null;
            if (hasResult)
            {
                result = new VariableDefinition(method.ReturnType);
                method.Body.Variables.Add(result);
                hook.Parameters.Add(new ParameterDefinition(new ByReferenceType(method.ReturnType)));
            }
            method.Body.InitLocals = true;

            ILProcessor il = method.Body.GetILProcessor();
            Instruction original = method.Body.Instructions[0];
            List<Instruction> prefix = new List<Instruction>();
            if (method.HasThis)
            {
                prefix.Add(il.Create(OpCodes.Ldarg_0));
            }
            foreach (ParameterDefinition p in method.Parameters)
            {
                prefix.Add(il.Create(OpCodes.Ldarg, p));
            }
            if (hasResult)
            {
                prefix.Add(il.Create(OpCodes.Ldloca, result));
            }
            prefix.Add(il.Create(OpCodes.Call, hook));
            prefix.Add(il.Create(OpCodes.Brfalse, original));
            if (hasResult)
            {
                prefix.Add(il.Create(OpCodes.Ldloc, result));
            }
            prefix.Add(il.Create(OpCodes.Ret));

            foreach (Instruction ins in prefix)
            {
                il.InsertBefore(original, ins);
            }
        }

        private static TypeReference MapToHookType(ModuleDefinition module, TypeReference type)
        {
            TypeDefinition def = type.Resolve();
            if (def != null && !def.IsValueType && def.Module.Assembly.Name.Name != "mscorlib")
            {
                return module.TypeSystem.Object;
            }
            return type;
        }
    }
}
