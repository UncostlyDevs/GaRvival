using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using JunkWars.Client;
using JunkWars.Client.Arena;
using JunkWars.Client.Garage;
using JunkWars.Client.Robot;
using JunkWars.Common.Robot.Enums;
using UnityEngine;

namespace GarRevival;

// Session-long driver: polls the subsystems, F12 saves a screenshot. Test files in GARRevival:
// autoshots.txt ("10,20,40": screenshot + state line) and autorun.txt ("<seconds> <command> [args]" per line, see Run).
// With "-garprofile <name>" they are autorun-<name>.txt etc.
public sealed class RevivalDriver : MonoBehaviour
{
    private sealed class Step
    {
        public float At;
        public string[] Words;
    }

    // The client blocks all input while it waits for a server answer; one that never comes is released after this.
    private const float BlockerTimeout = 20f;
    private static readonly FieldInfo BlockerQueue = typeof(UserInteractionsManager).GetField("responseQueue", BindingFlags.NonPublic | BindingFlags.Instance);

    private static RevivalDriver _instance;
    private static PlayScreen _overlay;
    private static float _fireUntil;
    private readonly List<Step> _steps = [];
    private string _blockedOn = string.Empty;
    private float _blockedSince;

    // "_" is a space, "__" a literal underscore.
    private static string Spaces(string word) => word.Replace("__", "\u0001").Replace('_', ' ').Replace('\u0001', '_');

    private static string Rest(string[] words, int from = 1) => string.Join(" ", words.Skip(from).ToArray());

    public static void Ensure()
    {
        if (_instance != null) return;
        var go = new GameObject("_GarRevival");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<RevivalDriver>();
        _overlay = go.AddComponent<PlayScreen>();
        go.AddComponent<ArcadeMode>();
        go.AddComponent<SkinShop>();
        SteamNet.Init();
    }

    private void Start()
    {
        string shots = Path.Combine(RevivalLog.Folder, "autoshots" + Instance.Suffix + ".txt");
        if (File.Exists(shots))
        {
            foreach (string s in File.ReadAllText(shots).Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float at))
                {
                    _steps.Add(new Step { At = at, Words = ["shot", "auto_" + at.ToString("000", CultureInfo.InvariantCulture)] });
                }
            }
        }
        string script = Path.Combine(RevivalLog.Folder, "autorun" + Instance.Suffix + ".txt");
        if (File.Exists(script))
        {
            foreach (string line in File.ReadAllLines(script))
            {
                var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2 && !line.TrimStart().StartsWith("#") && float.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float at))
                {
                    _steps.Add(new Step { At = at, Words = words.Skip(1).ToArray() });
                }
            }
        }
        _steps.Sort((a, b) => a.At.CompareTo(b.At));
    }

    private static List<byte> Blockers() => BlockerQueue?.GetValue(UserInteractionsManager.Instance) as List<byte> ?? [];

    private void WatchBlockers()
    {
        var blockers = Blockers();
        string now = string.Join(",", blockers.Select(b => b.ToString()).ToArray());
        if (now != _blockedOn)
        {
            _blockedOn = now;
            _blockedSince = Time.realtimeSinceStartup;
            return;
        }
        if (blockers.Count > 0 && GameManager.Instance.currentGameState == GameState.Garage && Time.realtimeSinceStartup - _blockedSince > BlockerTimeout)
        {
            RevivalLog.Info($"Input was blocked for {BlockerTimeout}s waiting on operation(s) {now}; releasing. Please report this line.");
            foreach (byte code in blockers.ToArray())
            {
                UserInteractionsManager.Instance.UnregisterBlocker(code);
            }
        }
    }

    // Sends NGUI's click to the BATTLE button object, so everything attached to it (including the tutorial) reacts.
    private static void ClickBattleButton()
    {
        var controller = FindObjectOfType<GarUIButtonBattleController>();
        var field = typeof(GarUIButtonBattleController).GetField("currentButton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var button = controller == null || field == null ? null : field.GetValue(controller) as UIButton;
        if (button == null)
        {
            RevivalLog.Info("click battle: button not found");
            return;
        }
        RevivalLog.Info($"click battle: clicking {button.gameObject.name} (enabled={button.isEnabled})");
        button.gameObject.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
    }

    // Stops the player's controller reading the fire buttons and sets the weapon groups' trigger state directly.
    private static void HoldTriggers(bool hold)
    {
        try
        {
            var robot = BattleManager.arena?.possessedRobot;
            if (robot == null || robot.actionControler == null) return;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            object controller = robot.actionControler;
            var type = controller.GetType();
            FieldInfo Field(string name)
            {
                for (var t = type; t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, any);
                    if (f != null) return f;
                }
                return null;
            }
            Field("isShootingDisabled")?.SetValue(controller, hold);
            foreach (string side in new[] { "leftSidedWeapons", "rightSidedWeapons" })
            {
                object weapons = Field(side)?.GetValue(controller);
                weapons?.GetType().GetField("isFireing", any)?.SetValue(weapons, hold);
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("fire test aid failed", ex);
        }
    }

    private void OnApplicationQuit()
    {
        RevivalLog.Info($"Game is quitting ({Time.realtimeSinceStartup.ToString("0")}s after start)");
        MasterClient.UnlistAll();
        Session.Leave(null);
        LocalWeb.Stop();
    }

    private void Update()
    {
        WatchBlockers();
        Session.Poll();
        LanDiscovery.Poll();
        SessionBattle.Update();
        BattleHud.Update();
        ServerRules.Update();
        WorkshopOps.Poll();
        DedicatedServer.Update();
        LocalWeb.Poll();
        MasterHttp.Poll();
        MasterClient.Update();
        Joiner.Update();
        Matchmaker.Update();
        Pinger.Poll();
        if (_fireUntil > 0f)
        {
            bool hold = Time.realtimeSinceStartup < _fireUntil;
            HoldTriggers(hold);
            if (!hold)
            {
                _fireUntil = 0f;
            }
        }
        if (Input.GetKeyDown(KeyCode.F12))
        {
            Shot("manual_" + DateTime.Now.ToString("HHmmss"));
        }
        while (_steps.Count > 0 && Time.realtimeSinceStartup >= _steps[0].At)
        {
            var step = _steps[0];
            _steps.RemoveAt(0);
            try
            {
                Run(step.Words);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("autorun step failed: " + string.Join(" ", step.Words), ex);
            }
        }
    }

    private static void Run(string[] words)
    {
        RevivalLog.Info("autorun: " + string.Join(" ", words));
        switch (words[0])
        {
            case "shot":
                Shot(words.Length > 1 ? words[1] : DateTime.Now.ToString("HHmmss"));
                break;
            case "buy": // buy Part|Module <indent>
                var type = (ItemType)Enum.Parse(typeof(ItemType), words[1], true);
                PhotonNetwork.BuyItem(Rest(words, 2), (byte)type, false);
                break;
            case "state": // state <GarageState>
                var state = (GarageState)Enum.Parse(typeof(GarageState), words[1], true);
                Messenger<GarageStateChangeMessage>.Invoke(GarageMainGUI.STATE_CHANGE, new GarageStateChangeMessage(state, null));
                break;
            case "host": // host tcp <port> | host steam
                if (words[1] == "steam")
                {
                    Session.HostSteam();
                }
                else
                {
                    Session.HostTcp(int.Parse(words[2], CultureInfo.InvariantCulture));
                }
                break;
            case "join": // join tcp <host:port>
                PlayScreen.JoinAddress(words[2]);
                break;
            case "lobbies":
                SteamNet.RefreshLobbies();
                break;
            case "leave":
                Session.Leave(null);
                break;
            case "overlay":
            case "play": // open or close the Play screen
                _overlay.Toggle();
                break;
            case "tab": // tab bots|host|join
                _overlay.SelectTab(words[1]);
                break;
            case "start": // the Play screen's START MATCH button
                _overlay.PressStart();
                break;
            case "hostlan":
                _overlay.HostLan();
                break;
            case "joinlan": // join the first game found on the local network
                if (!_overlay.JoinFirstLanGame())
                {
                    RevivalLog.Info("autorun: no LAN game found");
                }
                break;
            case "chat": // as typing in the garage chat would
                JunkWars.Client.Chat.ChatManager.Instance.SendGarageChatMessage(Rest(words));
                break;
            case "op": // op <code> [<key>=<i|b|s|f|B>:<value> ...]: send a raw operation, as the client would
                SendRawOp(words);
                break;
            case "call": // call <Method> [args...]: a PhotonNetwork / networking peer method with simple arguments
                CallNetworkMethod(words);
                break;
            case "goto": // goto flag|delivery|bomb|base|enemy: move my robot onto an objective or an enemy robot
                GoTo(words[1]);
                break;
            case "plant": // plant the carried bomb at the enemy base
                BattleManager.arena.PlaceBomb(BattleManager.arena.possessedRobot, 1 - BattleManager.arena.possessedRobot.teamId);
                break;
            case "server": // server enter | server exit: apply this game's own server.cfg as if hosting
                if (words[1] == "enter")
                {
                    ServerRules.Enter(ServerRules.Hosted);
                }
                else
                {
                    ServerRules.Exit();
                }
                break;
            case "repair": // repair every robot, as Repair All does for the shown one
                PhotonNetwork.RepairItems(PlayerManager.Instance.player.valueObject.ownedRobotAssemblies.Select(r => r.assemblyID).ToList(), [], []);
                break;
            case "boost": // boost <slot>: use the consumable boost in that slot, as its key would
                BattleManager.arena.possessedRobot.ConsumableBoostTryActivate(int.Parse(words[1], CultureInfo.InvariantCulture));
                break;
            case "fire": // fire <seconds>: hold both triggers in a battle
                _fireUntil = Time.realtimeSinceStartup + float.Parse(words[1], CultureInfo.InvariantCulture);
                break;
            case "click": // click battle: press the real BATTLE button, as the mouse would
                ClickBattleButton();
                break;
            case "battle": // the BATTLE button, including its readiness checks
                Messenger<Message>.Invoke(GarageMainGUI.SHOW_BATTLE_WINDOW, new Message(string.Empty, null));
                break;
            case "toggle": // garage UI tabs, as clicking them would: toggle ToggleStore ToggleStoreBuy
                var map = ObjectMapsInstances.Get(ObjectMap.MapType.GarageUI);
                foreach (string name in words.Skip(1))
                {
                    map.FindObjectComponent<UIToggle>(name).value = true;
                }
                break;
            case "buttons": // log everything clickable on screen (UI colliders), with labels and scripts
                foreach (var c in FindObjectsOfType<Collider>().Where(c => c.enabled && c.gameObject.activeInHierarchy && PathOf(c.transform).StartsWith("UI Root")))
                {
                    var label = c.GetComponentInChildren<UILabel>();
                    RevivalLog.Info("  clickable " + PathOf(c.transform) + " \"" + (label == null ? string.Empty : label.text.Replace("\n", " ")) + "\" [" +
                        string.Join(",", c.GetComponents<MonoBehaviour>().Select(m => m.GetType().Name).ToArray()) + "]");
                }
                break;
            case "press": // press <text>: click the first visible button whose name or label contains the text
                string wanted = Rest(words);
                var hit = FindObjectsOfType<Collider>().FirstOrDefault(c => c.enabled && c.gameObject.activeInHierarchy && PathOf(c.transform).StartsWith("UI Root") &&
                    (PathOf(c.transform).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 || LabelOf(c).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0));
                RevivalLog.Info(hit == null ? "press: nothing matching \"" + wanted + "\"" : "press: clicking " + PathOf(hit.transform));
                if (hit != null)
                {
                    hit.gameObject.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
                }
                break;
            case "arcade": // what clicking the garage's arcade machine does
                PopUpWindowsManager.Instance.RegisterWindow("Fun-O-Matic", "Alienoids ver. 1.0", [Localizator.Get("GN_Ok")], new Action[1], 2, 11);
                break;
            case "inspect": // inspect <text>: the simple public fields of every script on the matching UI objects
                string part = Rest(words);
                foreach (var c in FindObjectsOfType<Collider>().Where(c => c.gameObject.activeInHierarchy && PathOf(c.transform).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    RevivalLog.Info("inspect " + PathOf(c.transform));
                    foreach (var m in c.GetComponents<MonoBehaviour>())
                    {
                        LogFields(m);
                    }
                }
                break;
            case "urls": // every web link the UI can open, loaded or not
                foreach (var link in Resources.FindObjectsOfTypeAll<GarUIOpenBrowserUrl>())
                {
                    RevivalLog.Info("  link " + PathOf(link.transform) + " -> " + link.url + (link.localize ? " = " + Localizator.Get(link.url) : string.Empty));
                }
                break;
            case "type": // type <text>: put text in the first visible text box
                var input = FindObjectsOfType<UIInput>().FirstOrDefault(i => i.gameObject.activeInHierarchy);
                RevivalLog.Info(input == null ? "type: no text box" : "type: into " + PathOf(input.transform));
                if (input != null)
                {
                    input.value = Rest(words);
                }
                break;
            case "redeem": // redeem <code>: the Redeem Code window's request, through the game's own code
                RedeemPromoCode.Redeem(words[1], r => RevivalLog.Info($"redeem {words[1]}: {r}"));
                break;
            case "ladder": // ladder mine|master: the Stats > Ladder table request, through the game's own code
                LadderDataHelper.SendLadderRequest(null, 0, LadderDataHelper.LadderSortOrder.ByPosition, 0, 100, words.Length > 1 && words[1] == "master");
                break;
            case "arcade2": // arcade2 on|off: as clicking the machine / LEAVE ARCADE does
                if (words[1] == "on")
                {
                    ArcadeMode.Enter();
                }
                else
                {
                    ArcadeMode.Leave();
                }
                break;
            case "arcadeplay": // resume the arcade game and fire a shot, as a click on its screen would
                var shooter = FindObjectOfType<ArcadeGame_RobotShooter>();
                if (shooter != null)
                {
                    if (shooter.paused)
                    {
                        shooter.ResumeGame();
                    }
                    shooter.Shoot();
                    RevivalLog.Info($"arcadeplay: running={shooter.running} paused={shooter.paused}");
                }
                break;
            case "queue": // queue on|off: the camera move to and from the arcade machine
                if (words[1] == "on")
                {
                    GameManager.Instance.garage.DoRobotAssembyToBattleQueueTransition();
                }
                else
                {
                    GameManager.Instance.garage.DoBattleQueueToRobotAssembyTransition();
                }
                break;
            case "browser": // browser [internet|lan|steam|favourites]: open the server browser on that tab
                PlayScreen.ShowBrowser(words.Length > 1 ? words[1] : null);
                break;
            case "rows": // log the server browser's rows (as filtered and sorted), with ping
                PlayScreen.LogBrowserRows();
                break;
            case "joinrow": // joinrow <index>: join a row of the server browser, as double-clicking it does
                if (!PlayScreen.JoinBrowserRow(int.Parse(words[1], CultureInfo.InvariantCulture)))
                {
                    RevivalLog.Info("joinrow: no row " + words[1]);
                }
                break;
            case "findmatch": // findmatch [mode|any|cancel]: the FIND MATCH button
                if (words.Length > 1 && words[1] == "cancel")
                {
                    Matchmaker.Cancel();
                }
                else
                {
                    Matchmaker.Begin(words.Length > 1 ? words[1] : "any");
                }
                break;
            case "ammo": // ammo [buy <type> <tier> <qty> | auto on|off | max]: log stock and loads, or what the supply screen does
                AmmoCommand(words);
                break;
            case "skins": // open or close the skin screen (focused part, else the first one on a robot)
                SkinShop.Toggle();
                break;
            case "skin": // skin <indent|first> <index>: buy if needed and wear it, 0 = original look
                SkinShop.Wear(words[1], byte.Parse(words[2], CultureInfo.InvariantCulture));
                break;
            case "skindump": // skindump <prefabPath> <index>: the part's albedo before/after as PNGs in skins-dump/
                SkinPainter.Dump(words[1], byte.Parse(words[2], CultureInfo.InvariantCulture));
                break;
            case "quit": // close the game normally (OnApplicationQuit runs)
                Application.Quit();
                break;
            case "log": // log <text>: a marker in the log
                RevivalLog.Info("---- " + Rest(words));
                break;
            default:
                RevivalLog.Info("autorun: unknown command " + words[0]);
                break;
        }
    }

    private static void AmmoCommand(string[] words)
    {
        var player = PlayerManager.Instance.player;
        switch (words.Length > 1 ? words[1] : string.Empty)
        {
            case "buy":
                DataManager.Instance.BuyAmmo([new BuyAmmoData { AmmoType = words[2], tier = int.Parse(words[3], CultureInfo.InvariantCulture), quantity = int.Parse(words[4], CultureInfo.InvariantCulture) }], 0);
                return;
            case "auto":
                player.SelectedRobotAssembly.valueObject.autoResupply = words[2] == "on";
                DataManager.Instance.SetRobotsSlotSupplyMode(player.valueObject.selectedRobotIndex, words[2] == "on");
                return;
            case "max": // "Max load all", then OK or Buy
                var links = player.GetPerRobotDistributionData(player.SelectedRobotAssembly) ?? [];
                links.ForEach(l => l.distribNode.desiredAmmount = Math.Max(0, l.distribNode.maxAmmount));
                var order = player.GetAmmoPurcase(links);
                if (order.Length > 0)
                {
                    DataManager.Instance.BuyAmmo(order, 0);
                }
                else
                {
                    DataManager.Instance.DoUpdateAmmoDistrib();
                }
                return;
        }
        var saved = PlayerState.Current;
        RevivalLog.Info("ammo: economy " + (AmmoOps.Economy ? "on" : "off") + ", stock " +
            string.Join("; ", saved.ownedAmmo.Select(a => a.indent + " " + string.Join("/", (a.quantity ?? []).Select(q => q.ToString()).ToArray())).ToArray()));
        foreach (var n in saved.ammoDistribution.nodes)
        {
            RevivalLog.Info("ammo:   part " + n.moudleID + " " + (n.desiredAmmoType.Length == 0 ? "energy" : n.desiredAmmoType + " t" + n.tier) +
                " current " + n.currentAmmount + " desired " + n.desiredAmmount + " max " + n.maxAmmount);
        }
        var robot = BattleManager.arena?.possessedRobot;
        foreach (var barrel in robot == null || robot.weapons == null ? new WeaponBarrel[0] : robot.weapons.SelectMany(w => w.weaponBarrels))
        {
            RevivalLog.Info("ammo:   in battle part " + barrel.partVO.partID + " " + (string.IsNullOrEmpty(barrel.currentAmmoType) ? "energy" : barrel.currentAmmoType) +
                " " + barrel.CurrentAmmo + "/" + barrel.MaxAmmo);
        }
    }

    private static string PathOf(Transform t)
    {
        string path = t.name;
        for (var p = t.parent; p != null && path.Length < 120; p = p.parent)
        {
            path = p.name + "/" + path;
        }
        return path;
    }

    private static void LogFields(object target)
    {
        foreach (var f in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object value = f.GetValue(target);
            if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum)
            {
                RevivalLog.Info($"    {target.GetType().Name}.{f.Name} = {value}");
            }
            else if (value is System.Collections.IEnumerable items)
            {
                foreach (object item in items)
                {
                    if (item != null && item is not UnityEngine.Object)
                    {
                        RevivalLog.Info($"    {target.GetType().Name}.{f.Name}[]:");
                        LogFields(item);
                    }
                }
            }
            else if (value is UnityEngine.Object)
            {
                RevivalLog.Info($"    {target.GetType().Name}.{f.Name} -> {((UnityEngine.Object)value).name}");
            }
        }
    }

    private static string LabelOf(Component clickable)
    {
        var label = clickable.GetComponentInChildren<UILabel>();
        return label == null || label.text == null ? string.Empty : label.text.Replace("\n", " ");
    }

    private static void GoTo(string what)
    {
        var robot = BattleManager.arena.possessedRobot;
        int team = robot.teamId;
        var target = what switch
        {
            "flag" => FindObjectsOfType<TeamFlag>().Where(f => f.OwningTeamID != team).Select(f => f.transform).FirstOrDefault(),
            "delivery" => FindObjectsOfType<FlagDeliveryPoint>().Where(p => p.owningTeamID == team).Select(p => p.transform).FirstOrDefault(),
            "bomb" => FindObjectsOfType<TeamBomb>().Where(b => b.OwningTeamID == team).Select(b => b.transform).FirstOrDefault(),
            "enemy" => BattleManager.arena.robots.Values.Where(r => r.teamId != team && !r.destroyed && r.capusle != null).Select(r => r.capusle.transform).FirstOrDefault(),
            "base" => FindObjectsOfType<TeamBase>().Where(b => b.owningTeamID != team).Select(b => b.bombPlantPoint != null ? b.bombPlantPoint.transform : b.transform).FirstOrDefault(),
            _ => null,
        };
        if (target == null)
        {
            RevivalLog.Info($"goto: no {what} in this arena");
            return;
        }
        var body = robot.capusle.GetComponent<Rigidbody>();
        body.position = target.position + Vector3.up * 0.5f;
        body.velocity = Vector3.zero;
        var modes = BattleManager.arena.gamemodeManager;
        RevivalLog.Info($"goto {what}: moved to {target.position.ToString("0.0")} (flag carrier={modes.IsFlagCarrier(robot)}, bomb carrier={modes.IsBombCarrier(robot)})");
    }

    private static void SendRawOp(string[] words)
    {
        var parameters = new Dictionary<byte, object>();
        foreach (string word in words.Skip(2))
        {
            int eq = word.IndexOf('=');
            string value = Spaces(word.Substring(eq + 3));
            object boxed = word[eq + 1] switch
            {
                'i' => int.Parse(value, CultureInfo.InvariantCulture),
                'b' => byte.Parse(value, CultureInfo.InvariantCulture),
                'f' => float.Parse(value, CultureInfo.InvariantCulture),
                'B' => bool.Parse(value),
                _ => value,
            };
            parameters[byte.Parse(word.Substring(0, eq), CultureInfo.InvariantCulture)] = boxed;
        }
        PhotonNetwork.networkingPeer.OpCustom(byte.Parse(words[1], CultureInfo.InvariantCulture), parameters, true);
    }

    private static void CallNetworkMethod(string[] words)
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var args = words.Skip(2).ToArray();
        foreach (object target in new object[] { typeof(PhotonNetwork), PhotonNetwork.networkingPeer })
        {
            var type = target as Type ?? target.GetType();
            foreach (var method in type.GetMethods(any))
            {
                var ps = method.GetParameters();
                if (method.Name != words[1] || ps.Length != args.Length || (target is Type && !method.IsStatic)) continue;
                var values = new object[args.Length];
                try
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        var p = ps[i].ParameterType;
                        string text = args[i].Replace('_', ' ');
                        values[i] = p.IsEnum ? Enum.Parse(p, text, true) : Convert.ChangeType(text, p, CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception)
                {
                    continue;
                }
                object result = method.Invoke(method.IsStatic ? null : target, values);
                RevivalLog.Info($"call {type.Name}.{method.Name} -> {result ?? "(void)"}");
                return;
            }
        }
        RevivalLog.Info($"call: no method {words[1]} taking {args.Length} simple argument(s)");
    }

    private static void Shot(string name)
    {
        string path = Path.Combine(RevivalLog.Folder, "shot_" + name + ".png");
        Application.CaptureScreenshot(path);
        string state;
        try
        {
            state = "gameState=" + GameManager.Instance.currentGameState + " peer=" + PhotonNetwork.connectionStateDetailed +
                " popup=" + PopUpWindowsManager.Instance.IsActive + " credits=" + PlayerManager.Instance.player.credits +
                " parts=" + PlayerManager.Instance.player.valueObject.ownedParts.Count +
                " blockers=[" + string.Join(",", Blockers().Select(b => b.ToString()).ToArray()) + "]" +
                " tutorial=" + PlayerManager.Instance.player.valueObject.tutorialState;
        }
        catch (Exception ex)
        {
            state = "state unavailable: " + ex.Message;
        }
        RevivalLog.Info($"Screenshot {path} {state}");
    }
}
