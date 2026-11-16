using System;
using System.Collections;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using JunkWars.Client.Arena;
using JunkWars.Client.Robot;
using JunkWars.Common.Gameplay;
using UnityEngine;

namespace GarRevival
{
    // Entry points called from the game's patched assemblies. The patcher
    // inserts "if (Hooks.X(this, args..., ref result)) return result;" at the
    // top of each target method, so returning true replaces the original.
    // Signatures must match what the patcher emits.
    public static class Hooks
    {
        // ---- Photon3Unity3D: ExitGames.Client.Photon.PhotonPeer ----

        public static bool PeerConnect2(object peer, string serverAddress, string applicationName, ref bool result)
        {
            result = Loopback.Connect((PhotonPeer)peer, serverAddress);
            return true;
        }

        public static bool PeerConnect3(object peer, string serverAddress, string applicationName, byte node, ref bool result)
        {
            result = Loopback.Connect((PhotonPeer)peer, serverAddress);
            return true;
        }

        public static bool PeerDisconnect(object peer)
        {
            Loopback.Disconnect((PhotonPeer)peer);
            return true;
        }

        public static bool PeerStopThread(object peer)
        {
            return true;
        }

        public static bool PeerFetchServerTimestamp(object peer)
        {
            return true;
        }

        public static bool PeerEstablishEncryption(object peer, ref bool result)
        {
            result = Loopback.EstablishEncryption((PhotonPeer)peer);
            return true;
        }

        public static bool PeerSendOutgoingCommands(object peer, ref bool result)
        {
            result = false;
            return true;
        }

        public static bool PeerDispatchIncomingCommands(object peer, ref bool result)
        {
            result = Loopback.Dispatch((PhotonPeer)peer);
            return true;
        }

        public static bool PeerOpCustom4(object peer, byte customOpCode, Dictionary<byte, object> customOpParameters, bool sendReliable, byte channelId, ref bool result)
        {
            result = Loopback.Send((PhotonPeer)peer, customOpCode, customOpParameters);
            return true;
        }

        public static bool PeerOpCustom5(object peer, byte customOpCode, Dictionary<byte, object> customOpParameters, bool sendReliable, byte channelId, bool encrypt, ref bool result)
        {
            result = Loopback.Send((PhotonPeer)peer, customOpCode, customOpParameters);
            return true;
        }

        public static bool PeerOpCustomRequest(object peer, object operationRequest, bool sendReliable, byte channelId, bool encrypt, ref bool result)
        {
            OperationRequest request = (OperationRequest)operationRequest;
            result = Loopback.Send((PhotonPeer)peer, request.OperationCode, request.Parameters);
            return true;
        }

        public static bool PeerGetPeerState(object peer, ref PeerStateValue result)
        {
            result = Loopback.StateOf((PhotonPeer)peer);
            return true;
        }

        public static bool PeerGetIsEncryptionAvailable(object peer, ref bool result)
        {
            result = Loopback.IsEncryptionReady((PhotonPeer)peer);
            return true;
        }

        public static bool PeerGetServerTime(object peer, ref int result)
        {
            result = Environment.TickCount;
            return true;
        }

        // ---- Assembly-CSharp ----

        // The final official build replaced this coroutine with a "servers are
        // being shut down" popup. Run the original login flow instead.
        public static bool SteamDeployLoginStart(object self, ref IEnumerator result)
        {
            LocalWeb.Start();
            MasterLauncher.Start();
            result = OfflineLogin.Run((SteamDeployLogin)self);
            return true;
        }

        // The client's speed-hack detector quits the game when wall-clock time
        // and game time drift apart for a few seconds, which also happens on a
        // stuttering PC or with two copies running. There is no official server
        // left for it to protect, so it is not started.
        public static bool EntryPointStartAntiCheatEngine(object self)
        {
            RevivalLog.Info("Timing check (quits on stutter) not started");
            LocalWeb.Start();
            MasterLauncher.Start();
            return true;
        }

        // Every path through the garage's BATTLE button ends here after its
        // readiness checks. Instead of matchmaking, BATTLE opens the Play
        // screen; its START button comes back through here and starts the match.
        public static bool PhotonNetworkJoinRandomBattle()
        {
            try
            {
                if (!PlayScreen.ConsumeStartRequest() || Session.Role == Session.Roles.Client)
                {
                    // A press of BATTLE: show the match screen and leave the button usable.
                    PlayScreen.Open();
                    Messenger<Message>.Invoke(GarageMainGUI.RESET_BATTLE_BUTTON, new Message(GarageMainGUI.RESET_BATTLE_BUTTON, null));
                }
                else if (Session.Role == Session.Roles.Host && Session.RemoteNames.Count > 0)
                {
                    SessionBattle.HostStart();
                }
                else
                {
                    OfflineBattle.Start();
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Could not start battle", ex);
            }
            return true;
        }

        // The Info Screen (news, events, top players) opens by itself in the
        // garage once the tutorial is done. Its content came from the dead
        // website, so it is left closed; the header button still opens it.
        public static bool GarageInfoScreenSubmenuStart(object self)
        {
            JunkWars.Client.Garage.InfoScreenManager.Instance.showGUIOnStart = false;
            return false;
        }

        public static bool BaseArenaGetSpawingPoint(object self, object actrData, ref Transform result)
        {
            return ArenaSpawns.Overflow((BaseArena)self, (JunkWars.Common.Player.ValueObjects.ActorVO)actrData, ref result);
        }

        // ---- session battles (see SessionBattle.cs) ----

        public static bool OfflineArenaAddRobots(object self)
        {
            if (!SessionBattle.Active)
            {
                return false;
            }
            SessionBattle.AddRobots((OfflineArena)self);
            return true;
        }

        public static bool RobotAvatarAddFullAmmo(object self)
        {
            try
            {
                AmmoOps.OnAddFullAmmo((RobotAvatar)self);
                return true;
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Ammo load failed; using the game's full ammo", ex);
                return false;
            }
        }

        // Robots driven from another PC have no local controller; the offline
        // arena assumes every robot has one.
        public static bool RobotAvatarSetFreeLook(object self, bool freeLook)
        {
            return ((RobotAvatar)self).actionControler == null;
        }

        public static bool CombatantExecuteShoot(object self, int moduleType, int weaponID, int barrelID, short shootIndex, byte? checkForAmmo, byte? additionalEffectId, object targets, Vector3 aimPoint, object[] additionalParameters)
        {
            return SessionBattle.OnShoot((OfflineArenaCombatant)self, moduleType, weaponID, barrelID, shootIndex, checkForAmmo, additionalEffectId, (WeaponHitPoint[])targets, aimPoint, additionalParameters);
        }

        public static bool CombatantExecuteHit(object self, int moduleType, int weaponID, int barrelID, short shootIndex, byte? additionalEffectId, object targets, Vector3 aimPoint, object[] additionalParameters)
        {
            return SessionBattle.OnHit((OfflineArenaCombatant)self, moduleType, weaponID, barrelID, shootIndex, additionalEffectId, (WeaponHitPoint[])targets);
        }

        public static bool CombatantExecuteDeviceActivate(object self, int deviceID, Vector3 position, Quaternion rotation)
        {
            return SessionBattle.OnDeviceActivate((OfflineArenaCombatant)self, deviceID, position, rotation);
        }

        public static bool CombatantExecuteDeviceDeactivate(object self, int objectID, byte code)
        {
            return SessionBattle.OnDeviceDeactivate((OfflineArenaCombatant)self, objectID, code);
        }

        // The offline arena left this unimplemented (it throws).
        // The sparks Buy button. Real-money purchases went through the dead
        // website, and with the Steam overlay off the game opens a payment
        // browser whose library no longer ships: it hides the whole interface
        // and never comes back. Explain where sparks come from instead.
        public static bool SteamPurchaseServiceInitCurrencyPurchase(object self, int purchaseOption)
        {
            try
            {
                RevivalLog.Info("Sparks purchase (option " + purchaseOption + ") not available; told the player");
                PopUpWindowsManager.Instance.RegisterWindow("Sparks",
                    "GAR Revival has no real-money store. Get sparks from the Exchange (credits to sparks), daily login rewards, missions and crates.",
                    new string[1] { Localizator.Get("GN_Ok") }, new Action[1], 2, 11);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Sparks purchase notice failed", ex);
            }
            return true;
        }

        // A click on a garage object: the arcade machine opens its game. A click
        // on the skin screen is its own, not the garage's.
        public static bool AssemblyMouseManagerSelectGrageInteractiveObject(object self, ref bool result)
        {
            try
            {
                if (SkinShop.SwallowGarageClick())
                {
                    result = true;
                    return true;
                }
                return ArcadeMode.OnGarageClick(ref result);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Arcade click failed", ex);
                return false;
            }
        }

        // Every part model load. "<prefabPath>_s<N>" is a part wearing skin N.
        public static bool ResourcesManagerLoadPartPrefab(string name, ref GameObject result)
        {
            try
            {
                return SkinPainter.TryLoad(name, ref result);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Part skin load failed for " + name, ex);
                return false;
            }
        }

        public static bool CombatantExecuteConsumableBoost(object self, int boostSlotIndex)
        {
            try
            {
                BattleUpkeep.Use((OfflineArenaCombatant)self, boostSlotIndex);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Consumable boost failed", ex);
            }
            return true;
        }

        public static bool EmulatorHandlePick(object self, int actorID, int pickableIndex)
        {
            return SessionBattle.OnPick(actorID, pickableIndex);
        }

        public static bool EmulatorHandleGameModeEvent(object self, int actorID, Hashtable data)
        {
            return SessionBattle.OnGameModeEvent(actorID, data);
        }

        public static bool EmulatorPublishStartCountdown(object self, ref bool result)
        {
            if (SessionBattle.SuppressLocalRules())
            {
                result = false;
                return true;
            }
            return false;
        }

        public static bool EmulatorPublishEndGame(object self, object data)
        {
            SessionBattle.OnEndGame(data);
            return false;
        }

        // Runs before the original, which shows the results and returns to the
        // garage.
        public static bool OfflineArenaOnEndGame(object self, object message)
        {
            try
            {
                OfflineBattle.ApplyResult(((DataMessage)message).data as EndBattleData);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Could not apply battle result", ex);
            }
            return false;
        }

        // The hooks below run before the original method and only repair the
        // data (protobuf turns empty collections into null).

        public static bool PlayerSetPlayerData(object self, object playerData)
        {
            VoNormalizer.FillNullCollections(playerData);
            return false;
        }

        public static bool DataManagerOnShopData(object self, object message)
        {
            VoNormalizer.FillNullCollections(((DataMessage)message).data);
            return false;
        }

        public static bool PlayerAddItemValueObjects(object self, object itemToAddPack)
        {
            VoNormalizer.FillNullCollections(itemToAddPack);
            return false;
        }

        public static bool PlayerUpdateItemValueObjects(object self, object itemToUpdatePack)
        {
            VoNormalizer.FillNullCollections(itemToUpdatePack);
            return false;
        }

        public static bool PlayerUpdateRobotValueObjects(object self, object updatedRobot)
        {
            VoNormalizer.FillNullCollections(updatedRobot);
            return false;
        }
    }
}
