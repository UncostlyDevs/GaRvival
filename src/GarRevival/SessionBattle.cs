using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using JunkWars.Client;
using JunkWars.Client.Arena;
using JunkWars.Client.Arena.Offline;
using JunkWars.Client.Robot;
using JunkWars.Common.Arena;
using JunkWars.Common.Gameplay;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Utils;
using UnityEngine;

namespace GarRevival;

// A session battle on the game's offline mode; every player's game runs the arena.
// Each game drives its own robot (the host also drives the bots); other robots are puppets moved by position updates.
// The rules (OfflineServerEmulator) run only on the host: clients send their actions, the host's rule events replay on clients.
internal static class SessionBattle
{
    // First byte of a battle message.
    private const byte MsgPrepare = 1; // host -> clients: a battle is starting, send your robot
    private const byte MsgRobot = 2;   // client -> host: ready flag + ActorVO (protobuf)
    private const byte MsgSetup = 3;   // host -> client: map, mode, duration, your id, all players
    private const byte MsgState = 4;   // either way: positions of the sender's robots
    private const byte MsgAction = 5;  // client -> host: something my robot did
    private const byte MsgFire = 6;    // host -> clients: show a robot firing
    private const byte MsgEvent = 7;   // host -> clients: a rule event to replay
    private const byte MsgEndGame = 8; // host -> client: your EndBattleData (protobuf)
    private const byte MsgWear = 9;    // host -> client: what is left of each of your robot's parts
    private const byte MsgAmmo = 10;   // host -> client: rounds left in each of your robot's ammo barrels

    private const byte ActShoot = 1, ActHit = 2, ActPick = 3, ActGameMode = 4, ActDevice = 5, ActUndeploy = 6, ActBoost = 7;
    private const byte EvStat = 1, EvDestruction = 2, EvGameMode = 3, EvDeviceActivate = 4, EvUndeploy = 5,
        EvSpawn = 6, EvDespawn = 7, EvCountdown = 8, EvStart = 9, EvBoost = 10;

    private const float StateInterval = 0.066f;
    private const float PrepareTimeout = 5f;
    private static readonly string[] BotNames = ["Blick", "Flick", "Glick", "Plick", "Quee", "Snick", "Whick", "Grumbles", "Lionel", "Rusty"];

    public static bool Active;
    public static int LocalActorId;
    public static int LocalTeam = 1;

    private static readonly Dictionary<string, int> ActorOf = new(); // host: member -> actor
    private static readonly Dictionary<string, int> TeamOf = new();  // host: member -> team
    private static string _matchMap;
    private static GameModeType _matchMode;
    private static float _matchStartedAt;
    private static readonly Queue<Action> Pending = new();            // client: until the arena is up
    private static MessengerListenerContainer _listeners;
    private static bool _applyingRemote;
    private static bool _sawArena;
    private static float _nextState;
    private static float _nextReport;
    private static int _statesSent, _statesReceived, _eventsReplayed, _firesShown;

    private static bool _preparing;
    private static float _prepareDeadline;
    private static List<string> _waiting;
    private static Dictionary<string, ActorVO> _replies;

    public static bool Busy => _preparing || Active;

    private static bool IsClient => Active && Session.Role == Session.Roles.Client;

    private static OfflineArena Arena => BattleManager.arena as OfflineArena;

    private static bool ArenaReady => Arena != null && Arena.gameLoaded && Arena.robots.Count > 0;

    // The host pressed BATTLE with other players in the session.
    public static void HostStart()
    {
        if (_preparing || Active) return;
        _replies = new();
        _waiting = Session.RemoteNames;
        _preparing = true;
        _prepareDeadline = Time.realtimeSinceStartup + PrepareTimeout;
        Session.BroadcastBattle([MsgPrepare], null);
        GameServer.Notice("Starting a battle...");
    }

    private static void FinishPrepare()
    {
        _preparing = false;
        var settings = OfflineBattle.LoadSettings();
        var players = new List<PlayerStartData>();
        ActorOf.Clear();
        TeamOf.Clear();

        // Humans first: the host is actor 0 on team 1, like a solo battle.
        int nextId = 0;
        int humanIndex = 0;
        PlayerStartData AddHuman(ActorVO vo)
        {
            int team = settings.HumansSplit ? (humanIndex % 2 == 0 ? 1 : 0) : 1;
            humanIndex++;
            return Add(players, vo, nextId++, team, false);
        }
        // A dedicated server fields no robot of its own.
        if (!DedicatedServer.Enabled) AddHuman(OfflineBattle.BuildLocalActor());
        foreach (var reply in _replies)
        {
            // The host's ammo rules decide what each robot is loaded with.
            AmmoOps.NormalizeActor(reply.Value, false);
            var human = AddHuman(reply.Value);
            ActorOf[reply.Key] = human.playerID;
            TeamOf[reply.Key] = human.vo.team;
        }
        if (DedicatedServer.Enabled && ActorOf.Count == 0)
        {
            RevivalLog.Info("Session battle: no player was ready; not starting");
            Session.SendChat("Nobody was ready (in the garage with a complete robot), so the match did not start.", null);
            return;
        }

        // Bots fill each team up to its configured size.
        int botName = 0;
        int[] target = [settings.Enemies, settings.Allies + 1];
        for (int team = 0; team < 2; team++)
        {
            for (int have = players.Count(p => p.vo.team == team); have < target[team]; have++)
            {
                int id = nextId++;
                var bot = OfflineServerEmulator.GenerateActorVO(BotNames[botName++ % BotNames.Length], 1, 1, id, team, string.Empty, team == 0 && settings.WeakEnemies);
                AmmoOps.NormalizeActor(bot, true);
                Add(players, bot, id, team, true);
            }
        }

        string map = OfflineBattle.PickMap(settings);
        _matchMap = map;
        _matchMode = settings.Mode;
        _matchStartedAt = Time.realtimeSinceStartup;
        object[] roster = players.Select(p => (object)new object[] { p.playerID, p.vo.team, p.isBot, DataSerialization.ProtoSerializeObject(p.vo) }).ToArray();
        foreach (var member in ActorOf)
        {
            Session.SendBattleTo(member.Key, Message(MsgSetup, new object[] { map, (int)settings.Mode, settings.Minutes * 60, member.Value, roster }));
        }
        // The arena needs a robot to follow; a dedicated server watches a bot.
        int watched = DedicatedServer.Enabled ? (players.FirstOrDefault(p => p.isBot && p.vo.team == 1) ?? players.First(p => p.isBot)).playerID : 0;
        RevivalLog.Info($"Session battle: {map}, {settings.Mode}, {players.Count(p => !p.isBot)} players, {players.Count(p => p.isBot)} bots" +
            (DedicatedServer.Enabled ? $", server watches bot {watched}" : string.Empty));
        Begin(map, settings.Mode, settings.Minutes * 60, players, watched);
    }

    private static PlayerStartData Add(List<PlayerStartData> players, ActorVO vo, int id, int team, bool isBot)
    {
        vo.id = id;
        vo.team = team;
        var player = new PlayerStartData { isBot = isBot, playerID = id, partyIndex = 0, vo = vo };
        players.Add(player);
        return player;
    }

    private static void Begin(string map, GameModeType mode, int seconds, List<PlayerStartData> players, int localId)
    {
        foreach (var player in players)
        {
            player.possesed = player.playerID == localId;
        }
        LocalActorId = localId;
        LocalTeam = players.First(p => p.playerID == localId).vo.team;
        AmmoOps.RecordStart(players.Where(p => p.playerID == localId && !p.isBot).Select(p => p.vo).FirstOrDefault());
        Active = true;
        _sawArena = false;
        _statesSent = _statesReceived = _eventsReplayed = _firesShown = 0;
        Pending.Clear();
        if (Session.Role == Session.Roles.Host) ListenToRules();
        BattleManager.offlineBattleData = new OfflineBattleData
        {
            mapID = map,
            gameMode = mode,
            teamsCount = 2,
            gameDuration = seconds,
            players = players.ToArray(),
        };
        GameManager.Instance.ChangeGameState(GameState.OfflineArena);
    }

    private static void End(string why)
    {
        if (!Active) return;
        RevivalLog.Info($"Session battle over ({why}): sent {_statesSent} / received {_statesReceived} position updates, " +
            $"{_eventsReplayed} rule events, {_firesShown} remote shots shown");
        Active = false;
        Pending.Clear();
        ActorOf.Clear();
        _listeners?.Dispose();
        _listeners = null;
    }

    public static void OnSessionEnded()
    {
        _preparing = false;
        if (Active && Arena != null && GameManager.Instance.currentGameState == GameState.OfflineArena)
        {
            End("session closed");
            Arena.LeaveBattle();
        }
        else
        {
            End("session closed");
        }
    }

    // Replaces OfflineArena.AddRobots.
    public static void AddRobots(OfflineArena arena)
    {
        var build = typeof(BaseArena).GetMethod("BuildRobotAs", BindingFlags.NonPublic | BindingFlags.Instance);
        var players = arena.battleSetupData.players;
        arena.actors = new();
        foreach (var player in players)
        {
            player.vo.partyNmbr = player.partyIndex;
            arena.actors.Add(player.playerID, player.vo);
        }
        bool host = Session.Role == Session.Roles.Host;
        foreach (var player in players)
        {
            // Mine to drive: my own robot, and the bots if I am the host.
            bool local = player.possesed || (player.isBot && host);
            var robot = (RobotAvatar)build.Invoke(arena, new object[] { player.possesed, player.vo, player.playerID, local, player.isBot });
            robot.AddFullAmmo();
            var holder = new GameObject();
            var combatant = holder.AddComponent<OfflineArenaCombatant>();
            holder.AddComponent<RobotAvatarTicker>();
            combatant.LinkToRobot(player.playerID);
            if (!local) robot.StartNetSync();
        }
        RevivalLog.Info($"Session battle arena: {players.Length} robots, {arena.robots.Values.Count(r => r.localyControlled)} driven here");
    }

    public static void Update()
    {
        if (_preparing && (_waiting.Count == 0 || Time.realtimeSinceStartup > _prepareDeadline))
        {
            FinishPrepare();
        }
        if (!Active) return;
        if (!ArenaReady)
        {
            if (_sawArena && GameManager.Instance.currentGameState == GameState.Garage) End("back in the garage");
            return;
        }
        _sawArena = true;
        while (Pending.Count > 0)
        {
            Guard("replay", Pending.Dequeue());
        }
        if (Time.realtimeSinceStartup >= _nextState)
        {
            _nextState = Time.realtimeSinceStartup + StateInterval;
            SendState();
        }
        if (Time.realtimeSinceStartup >= _nextReport)
        {
            _nextReport = Time.realtimeSinceStartup + 10f;
            var last = Arena.robots.Values.OrderBy(r => r.actorId).Last();
            RevivalLog.Info($"Session battle: sent {_statesSent} / received {_statesReceived} position updates, {_eventsReplayed} rule events, " +
                $"{_firesShown} remote shots; robot {last.actorId} at " + (last.capusle == null ? "-" : last.capusle.transform.position.ToString("0.0")));
        }
    }

    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            RevivalLog.Error($"Session battle {what} failed", ex);
        }
    }

    private static void SendState()
    {
        byte[] message = Session.Bytes(w =>
        {
            w.Write(MsgState);
            w.Write(Session.ClockMs);
            var mine = Arena.robots.Values.Where(r => r.localyControlled && r.capusle != null && r.movementControler != null).ToList();
            w.Write((byte)mine.Count);
            foreach (var robot in mine)
            {
                var position = robot.capusle.GetComponent<Rigidbody>().position;
                var look = robot.movementControler.controllerScript.inputLook;
                var aim = robot.movementControler.desiredLookAtLocation;
                w.Write((byte)robot.actorId);
                w.Write((byte)(robot.sprinting ? 1 : 0));
                w.Write(position.x); w.Write(position.y); w.Write(position.z);
                w.Write(look.x); w.Write(look.y);
                w.Write(aim.x); w.Write(aim.y); w.Write(aim.z);
            }
        });
        _statesSent++;
        if (Session.Role == Session.Roles.Host)
        {
            Session.BroadcastBattle(message, null);
        }
        else
        {
            Session.SendBattleToHost(message);
        }
    }

    private static void ApplyState(byte[] message)
    {
        if (!ArenaReady) return;
        var r = new BinaryReader(new MemoryStream(message));
        r.ReadByte();
        // The sender's timestamp on this game's clock.
        double age = Mathf.Clamp((Session.ClockMs - r.ReadInt32()) / 1000f, 0f, 1f);
        int stamp = (int)(uint)((GameNetwork.time - age) * 1000.0);
        for (int count = r.ReadByte(); count > 0; count--)
        {
            int id = r.ReadByte();
            bool sprinting = r.ReadByte() != 0;
            var position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var look = new Vector2(r.ReadSingle(), r.ReadSingle());
            var aim = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (Arena.robots.TryGetValue(id, out var robot) && !robot.localyControlled && robot.playerSyncScript != null && robot.movementControler != null)
            {
                robot.playerSyncScript.OnSyncData(new PhotonMessageInfo(null, stamp, null), position, look);
                robot.movementControler.desiredLookAtLocation = aim;
                robot.ChangeSprintStateAsRemote(sprinting); // speed effect and sprint animation
            }
        }
        _statesReceived++;
    }

    private static byte[] Message(byte type, object payload) => Framed(type, Protocol.Serialize(payload));

    private static byte[] Framed(byte type, byte[] body)
    {
        var message = new byte[body.Length + 1];
        message[0] = type;
        Buffer.BlockCopy(body, 0, message, 1, body.Length);
        return message;
    }

    private static object Body(byte[] message) => Protocol.Deserialize(Tail(message, 1));

    private static byte[] Tail(byte[] message, int skip)
    {
        var tail = new byte[message.Length - skip];
        Buffer.BlockCopy(message, skip, tail, 0, tail.Length);
        return tail;
    }

    public static void FromHost(byte[] message)
    {
        Guard("message from host", () =>
        {
            switch (message[0])
            {
                case MsgPrepare:
                    AnswerPrepare();
                    break;
                case MsgSetup:
                    ReceiveSetup((object[])Body(message));
                    break;
                case MsgState:
                    ApplyState(message);
                    break;
                case MsgFire:
                    var fire = (object[])Body(message);
                    Pending.Enqueue(() => ShowFire(fire));
                    break;
                case MsgEvent:
                    var ev = (object[])Body(message);
                    Pending.Enqueue(() => Replay(Convert.ToByte(ev[0]), ev[1]));
                    break;
                case MsgWear:
                    var wear = (object[])Body(message);
                    var left = new Dictionary<int, float>();
                    var partIds = (int[])wear[0];
                    var durability = (float[])wear[1];
                    for (int i = 0; i < partIds.Length; i++)
                    {
                        left[partIds[i]] = durability[i];
                    }
                    BattleUpkeep.ApplyWear(left);
                    break;
                case MsgAmmo:
                    var ammo = (object[])Body(message);
                    var barrels = (int[])ammo[0];
                    var rounds = (int[])ammo[1];
                    AmmoOps.ApplyBattleUse(Enumerable.Range(0, Math.Min(barrels.Length, rounds.Length)).ToDictionary(i => barrels[i], i => rounds[i]));
                    break;
                case MsgEndGame:
                    var result = DataSerialization.ProtoDeserializeObject<EndBattleData>(Tail(message, 1));
                    Pending.Enqueue(() => Messenger<DataMessage>.Invoke("offline_arena_endgame", new DataMessage(result, null)));
                    break;
            }
        });
    }

    public static void FromClient(string name, byte[] message)
    {
        Guard($"message from {name}", () =>
        {
            switch (message[0])
            {
                case MsgRobot:
                    if (_preparing && _waiting.Remove(name) && message[1] == 1)
                    {
                        var vo = DataSerialization.ProtoDeserializeObject<ActorVO>(Tail(message, 2));
                        VoNormalizer.FillNullCollections(vo);
                        vo.activeGuildBuffs = SocialGuild.ActorBuffs(vo.activeGuildBuffs);
                        OfflineBattle.LogGuildBuffs(vo);
                        _replies[name] = vo;
                    }
                    break;
                case MsgState:
                    if (Active)
                    {
                        ApplyState(message);
                        Session.BroadcastBattle(message, name);
                    }
                    break;
                case MsgAction:
                    if (Active && ArenaReady && ActorOf.ContainsKey(name))
                    {
                        HandleAction(name, (object[])Body(message));
                    }
                    break;
            }
        });
    }

    // Client: the host is about to start a battle.
    private static void AnswerPrepare()
    {
        bool ready = !Active && GameManager.Instance.currentGameState == GameState.Garage && GameManager.Instance.garage != null
            && GameManager.Instance.garage.currentRobotAssembly != null && GameManager.Instance.garage.currentRobotAssembly.rootModule != null;
        var stream = new MemoryStream();
        stream.WriteByte(MsgRobot);
        stream.WriteByte((byte)(ready ? 1 : 0));
        if (ready)
        {
            byte[] vo = DataSerialization.ProtoSerializeObject(OfflineBattle.BuildLocalActor());
            stream.Write(vo, 0, vo.Length);
        }
        else
        {
            GameServer.Notice("The host started a battle, but you need to be in the garage with a complete robot to join.");
        }
        Session.SendBattleToHost(stream.ToArray());
    }

    private static void ReceiveSetup(object[] setup)
    {
        var players = new List<PlayerStartData>();
        foreach (object[] entry in (object[])setup[4])
        {
            var vo = DataSerialization.ProtoDeserializeObject<ActorVO>((byte[])entry[3]);
            VoNormalizer.FillNullCollections(vo);
            Add(players, vo, Convert.ToInt32(entry[0]), Convert.ToInt32(entry[1]), (bool)entry[2]);
        }
        RevivalLog.Info($"Session battle: joining on {setup[0]} as actor {setup[3]} of {players.Count}");
        Begin((string)setup[0], (GameModeType)Convert.ToInt32(setup[1]), Convert.ToInt32(setup[2]), players, Convert.ToInt32(setup[3]));
    }

    private static object[] PackTargets(WeaponHitPoint[] targets) =>
        targets?.Select(t => (object)WeaponHitPoint.GetAsSerializableObjectArray(t)).ToArray();

    private static WeaponHitPoint[] UnpackTargets(object packed) =>
        (packed as object[])?.Select(t => WeaponHitPoint.ObjectArrayToWeaponHitPoint((object[])t)).ToArray();

    private static byte? NullableByte(object value) => value == null ? null : Convert.ToByte(value);

    private static void SendAction(byte kind, int actor, params object[] args)
    {
        var payload = new object[args.Length + 2];
        payload[0] = kind;
        payload[1] = actor;
        Array.Copy(args, 0, payload, 2, args.Length);
        Session.SendBattleToHost(Message(MsgAction, payload));
    }

    // Hook on OfflineArenaCombatant.ExecuteShoot. True = skip the original.
    public static bool OnShoot(OfflineArenaCombatant combatant, int moduleType, int weaponId, int barrelId, short shootIndex, byte? checkForAmmo, byte? effectId, WeaponHitPoint[] targets, Vector3 aim, object[] extra)
    {
        if (!Active) return false;
        var robot = combatant.theAvatar;
        if (robot == null || !robot.localyControlled) return true;
        object[] shot = [ moduleType, weaponId, barrelId, shootIndex, checkForAmmo, effectId, PackTargets(targets), new[] { aim.x, aim.y, aim.z }, extra];
        if (IsClient)
        {
            SendAction(ActShoot, robot.actorId, (object)shot);
            return true;
        }
        Session.BroadcastBattle(Message(MsgFire, new object[] { robot.actorId, shot }), null);
        return false;
    }

    // Hook on OfflineArenaCombatant.ExecuteHit.
    public static bool OnHit(OfflineArenaCombatant combatant, int moduleType, int weaponId, int barrelId, short shootIndex, byte? effectId, WeaponHitPoint[] targets)
    {
        if (!Active) return false;
        var robot = combatant.theAvatar;
        if (robot == null || !robot.localyControlled) return true;
        if (IsClient)
        {
            SendAction(ActHit, robot.actorId, moduleType, weaponId, barrelId, shootIndex, effectId, PackTargets(targets));
            return true;
        }
        return false;
    }

    // Hook on OfflineArenaCombatant.ExecuteDeviceActivate.
    public static bool OnDeviceActivate(OfflineArenaCombatant combatant, int deviceId, Vector3 position, Quaternion rotation)
    {
        if (!Active) return false;
        var robot = combatant.theAvatar;
        if (robot == null || !robot.localyControlled) return true;
        if (IsClient)
        {
            SendAction(ActDevice, robot.actorId, deviceId, new[] { position.x, position.y, position.z }, new[] { rotation.x, rotation.y, rotation.z, rotation.w });
            return true;
        }
        return false;
    }

    // Hook on OfflineArenaCombatant.ExecuteDeviceDeactivate.
    public static bool OnDeviceDeactivate(OfflineArenaCombatant combatant, int objectId, byte code)
    {
        if (!IsClient) return false;
        if (combatant.theAvatar != null && combatant.theAvatar.possesedByLocalPlayer)
        {
            SendAction(ActUndeploy, combatant.theAvatar.actorId, objectId, code);
        }
        return true;
    }

    // Hooks on OfflineServerEmulator.HandlePickPickableOperation / HandleGameModeEvent.
    public static bool OnPick(int actorId, int index)
    {
        if (!IsClient || _applyingRemote) return false;
        if (actorId == LocalActorId) SendAction(ActPick, actorId, index);
        return true;
    }

    public static bool OnGameModeEvent(int actorId, Hashtable data)
    {
        if (!IsClient || _applyingRemote) return false;
        if (actorId == LocalActorId) SendAction(ActGameMode, actorId, data);
        return true;
    }

    // Host: a client's robot did something.
    private static void HandleAction(string name, object[] a)
    {
        byte kind = Convert.ToByte(a[0]);
        int actor = Convert.ToInt32(a[1]);
        if (ActorOf[name] != actor) return;
        var rules = Arena.offlineServerEmulator;
        _applyingRemote = true;
        try
        {
            switch (kind)
            {
                case ActShoot:
                    var shot = (object[])a[2];
                    object[] fire = [actor, shot];
                    ShowFire(fire);
                    Session.BroadcastBattle(Message(MsgFire, fire), name);
                    rules.HandleShoot(actor, Convert.ToInt32(shot[0]), Convert.ToInt32(shot[1]), Convert.ToInt32(shot[2]), Convert.ToInt16(shot[3]),
                        NullableByte(shot[4]), NullableByte(shot[5]), UnpackTargets(shot[6]));
                    break;
                case ActHit:
                    rules.HandleHit(actor, Convert.ToInt32(a[2]), Convert.ToInt32(a[3]), Convert.ToInt32(a[4]), Convert.ToInt16(a[5]), NullableByte(a[6]), UnpackTargets(a[7]));
                    break;
                case ActPick:
                    rules.HandlePickPickableOperation(actor, Convert.ToInt32(a[2]));
                    break;
                case ActGameMode:
                    rules.HandleGameModeEvent(actor, (Hashtable)a[2]);
                    break;
                case ActDevice:
                    rules.HandleDeviceActivationEvent(actor, Convert.ToInt32(a[2]), (float[])a[3], (float[])a[4]);
                    break;
                case ActUndeploy:
                    rules.DisableDeployedObject(actor, Convert.ToInt32(a[2]), Convert.ToByte(a[3]));
                    break;
                case ActBoost:
                    BattleUpkeep.ApplyOnRules(actor, Convert.ToInt32(a[2]), Convert.ToByte(a[3]));
                    break;
            }
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    // Shows a robot driven elsewhere firing.
    private static void ShowFire(object[] fire)
    {
        int actor = Convert.ToInt32(fire[0]);
        var shot = (object[])fire[1];
        if (!Arena.robots.TryGetValue(actor, out var robot) || robot.localyControlled) return;
        var aim = (float[])shot[7];
        var aimPoint = new Vector3(aim[0], aim[1], aim[2]);
        int moduleType = Convert.ToInt32(shot[0]);
        if (moduleType == 0)
        {
            robot.ExecuteFireEffectAsRemotePlayer(Convert.ToInt16(shot[3]), Convert.ToInt32(shot[1]), Convert.ToInt32(shot[2]), UnpackTargets(shot[6]), aimPoint, shot[8] as object[]);
        }
        else if (moduleType == 1)
        {
            robot.ExecuteDeviceFireEffectAsRemotePlayer(Convert.ToInt16(shot[3]), Convert.ToInt32(shot[1]), Convert.ToInt32(shot[2]), UnpackTargets(shot[6]), aimPoint);
        }
        _firesShown++;
    }

    // Host: rule events are replayed on clients.
    private static void ListenToRules()
    {
        _listeners = new MessengerListenerContainer();
        _listeners.AddListener<DataMessage>("offline_arena_statistic_change", m => SendEvent(EvStat, DataSerialization.ProtoSerializeObject((StatisticChangeData)m.data)));
        _listeners.AddListener<DataMessage>("offline_arena_destruction_data", m => SendEvent(EvDestruction, PackDestruction((DestructionData)m.data)));
        _listeners.AddListener<DataMessage>("offline_arena_gamemode_event", m => SendEvent(EvGameMode, m.data));
        _listeners.AddListener<DataMessage>("offline_arena_actor_deviceactivate", m => SendEvent(EvDeviceActivate, m.data));
        _listeners.AddListener<DataMessage>("offline_arena_actor_depolyable_deactivate", m => SendEvent(EvUndeploy, m.data));
        _listeners.AddListener<DataMessage>("offline_arena_spawn_pickables", m => SendEvent(EvSpawn, m.data));
        _listeners.AddListener<DataMessage>("offline_arena_despawn_pickables", m => SendEvent(EvDespawn, new object[] { ((int[][])m.data)[0], ((int[][])m.data)[1] }));
        _listeners.AddListener<DataMessage>("offline_arena_startcountdown", m => SendEvent(EvCountdown, Convert.ToInt32(m.data)));
        _listeners.AddListener<Message>("offline_arena_startgame", m => SendEvent(EvStart, 0));
    }

    private static void SendEvent(byte kind, object payload)
    {
        if (Active && Session.Role == Session.Roles.Host)
        {
            Guard($"event {kind}", () => Session.BroadcastBattle(Message(MsgEvent, new object[] { kind, payload }), null));
        }
    }

    private static void Replay(byte kind, object payload)
    {
        _eventsReplayed++;
        switch (kind)
        {
            case EvStat:
                Raise("offline_arena_statistic_change", DataSerialization.ProtoDeserializeObject<StatisticChangeData>((byte[])payload));
                break;
            case EvDestruction:
                Raise("offline_arena_destruction_data", UnpackDestruction((object[])payload));
                break;
            case EvGameMode:
                Raise("offline_arena_gamemode_event", payload);
                break;
            case EvDeviceActivate:
                Raise("offline_arena_actor_deviceactivate", payload);
                break;
            case EvUndeploy:
                Raise("offline_arena_actor_depolyable_deactivate", payload);
                break;
            case EvSpawn:
                Raise("offline_arena_spawn_pickables", payload);
                break;
            case EvDespawn:
                var pair = (object[])payload;
                Raise("offline_arena_despawn_pickables", new[] { (int[])pair[0], (int[])pair[1] });
                break;
            case EvCountdown:
                Raise("offline_arena_startcountdown", Convert.ToInt32(payload));
                break;
            case EvStart:
                Messenger<Message>.Invoke("offline_arena_startgame", new Message("start", null));
                break;
            case EvBoost:
                var boost = (object[])payload;
                BattleUpkeep.Show(Convert.ToInt32(boost[0]), Convert.ToInt32(boost[1]));
                break;
        }
    }

    private static void Raise(string name, object data) => Messenger<DataMessage>.Invoke(name, new DataMessage(data, null));

    // Client: my robot used a consumable boost.
    public static void SendBoost(int actorId, int slotIndex, byte type) => SendAction(ActBoost, actorId, slotIndex, type);

    // Host: a boost took effect; every other screen shows it.
    public static void AnnounceBoost(int actorId, int slotIndex) => SendEvent(EvBoost, new object[] { actorId, slotIndex });

    // Hook on OfflineServerEmulator.PublishEndGameEvent (host): every player gets their own result.
    public static void OnEndGame(object data)
    {
        if (!Active || Session.Role != Session.Roles.Host || data is not Dictionary<int, EndBattleData> results) return;
        foreach (var member in ActorOf)
        {
            // Before the result, so the wear is saved by the time the player's game applies its rewards.
            var left = BattleUpkeep.EndDurability(member.Value);
            if (left.Count > 0)
            {
                Session.SendBattleTo(member.Key, Message(MsgWear, new object[] { left.Keys.ToArray(), left.Values.ToArray() }));
            }
            var rounds = AmmoOps.Economy ? AmmoOps.EndAmmo(member.Value) : new Dictionary<int, int>();
            if (rounds.Count > 0)
            {
                Session.SendBattleTo(member.Key, Message(MsgAmmo, new object[] { rounds.Keys.ToArray(), rounds.Values.ToArray() }));
            }
            if (results.TryGetValue(member.Value, out var result))
            {
                Session.SendBattleTo(member.Key, Framed(MsgEndGame, DataSerialization.ProtoSerializeObject(result)));
            }
        }
        if (DedicatedServer.Enabled && results.Count > 0 && TeamOf.Count > 0) ReportResult(results.Values.First().winningTeam);
    }

    // Dedicated server: for the masters' ladder. -1 is a draw.
    private static void ReportResult(int winningTeam)
    {
        int first = winningTeam < 0 ? 1 : winningTeam;
        var winners = TeamOf.Where(t => t.Value == first).Select(t => t.Key).ToList();
        var losers = TeamOf.Where(t => t.Value != first).Select(t => t.Key).ToList();
        MasterClient.ReportResult(_matchMode.ToString(), _matchMap, (int)(Time.realtimeSinceStartup - _matchStartedAt), winningTeam, winners, losers);
    }

    // Hook on OfflineServerEmulator.PublishStartCountdownEvent: a client's own rules never start their clock.
    public static bool SuppressLocalRules() => IsClient;

    // DestructionData is not a protobuf type.
    private static object[] PackMap(Dictionary<int, List<int>> map) =>
        map == null ? [] : map.Select(e => (object)new object[] { e.Key, e.Value.ToArray() }).ToArray();

    private static Dictionary<int, List<int>> UnpackMap(object packed)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (object[] entry in (object[])packed)
        {
            map[Convert.ToInt32(entry[0])] = new List<int>((int[])entry[1]);
        }
        return map;
    }

    private static object[] PackDestruction(DestructionData d) =>
    [
        d.originActorID, PackMap(d.destroyedModules),
        (d.destroyedModuleTypes ?? []).Select(t => (int)t).ToArray(),
        (d.destroyedRobots ?? []).ToArray(), PackMap(d.destroyedDeployables),
    ];

    private static DestructionData UnpackDestruction(object[] p) => new()
    {
        originActorID = Convert.ToInt32(p[0]),
        destroyedModules = UnpackMap(p[1]),
        destroyedModuleTypes = ((int[])p[2]).Select(t => (LogicalType)t).ToList(),
        destroyedRobots = new List<int>((int[])p[3]),
        destroyedDeployables = UnpackMap(p[4]),
    };
}
