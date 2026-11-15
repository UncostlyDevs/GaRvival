using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using JunkWars.Client;
using JunkWars.Client.Garage;
using JunkWars.Common.Robot.Enums;
using UnityEngine;

namespace GarRevival
{
    // Lives for the whole session. F12 saves a screenshot to the GARRevival
    // folder. Two optional files drive it for testing:
    //   autoshots.txt - seconds, e.g. "10,20,40": screenshot + state line
    //   autorun.txt   - one "<seconds> <command> [args]" per line:
    //                   buy Part|Module <indent>, state <GarageState>,
    //                   toggle <UI toggle names...>, battle, shot <name>,
    //                   host tcp <port> | host steam, join tcp <host:port>,
    //                   lobbies, leave, overlay, chat <text>,
    //                   skins, skin <indent|first> <index>, skindump <path> <index>
    // With "-garprofile <name>" the files are autorun-<name>.txt etc.
    public sealed class RevivalDriver : MonoBehaviour
    {
        private sealed class Step
        {
            public float At;
            public string[] Words;
        }

        private static RevivalDriver _instance;
        private static PlayScreen _overlay;
        private readonly List<Step> _steps = new List<Step>();

        public static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }
            GameObject go = new GameObject("_GarRevival");
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
                foreach (string s in File.ReadAllText(shots).Split(new[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    float at;
                    if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out at))
                    {
                        _steps.Add(new Step { At = at, Words = new[] { "shot", "auto_" + at.ToString("000", CultureInfo.InvariantCulture) } });
                    }
                }
            }
            string script = Path.Combine(RevivalLog.Folder, "autorun" + Instance.Suffix + ".txt");
            if (File.Exists(script))
            {
                foreach (string line in File.ReadAllLines(script))
                {
                    string[] words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    float at;
                    if (words.Length >= 2 && !line.TrimStart().StartsWith("#") && float.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out at))
                    {
                        _steps.Add(new Step { At = at, Words = words.Skip(1).ToArray() });
                    }
                }
            }
            _steps.Sort((a, b) => a.At.CompareTo(b.At));
        }

        // The client blocks all input (grey screen, spinning gears) while it
        // waits for a server answer. If an answer never comes, say which
        // operation it was and give control back instead of locking the player out.
        private static readonly FieldInfo BlockerQueue = typeof(UserInteractionsManager).GetField("responseQueue", BindingFlags.NonPublic | BindingFlags.Instance);
        private const float BlockerTimeout = 20f;
        private string _blockedOn = string.Empty;
        private float _blockedSince;

        private static List<byte> Blockers()
        {
            List<byte> queue = BlockerQueue == null ? null : BlockerQueue.GetValue(UserInteractionsManager.Instance) as List<byte>;
            return queue ?? new List<byte>();
        }

        private void WatchBlockers()
        {
            List<byte> blockers = Blockers();
            string now = string.Join(",", blockers.Select(b => b.ToString()).ToArray());
            if (now != _blockedOn)
            {
                _blockedOn = now;
                _blockedSince = Time.realtimeSinceStartup;
                return;
            }
            if (blockers.Count > 0 && GameManager.Instance.currentGameState == GameState.Garage && Time.realtimeSinceStartup - _blockedSince > BlockerTimeout)
            {
                RevivalLog.Info("Input was blocked for " + BlockerTimeout + "s waiting on operation(s) " + now + "; releasing. Please report this line.");
                foreach (byte code in blockers.ToArray())
                {
                    UserInteractionsManager.Instance.UnregisterBlocker(code);
                }
            }
        }

        // Sends NGUI's click to the garage's BATTLE button object, so whatever
        // is attached to the real button (including the tutorial) reacts.
        private static void ClickBattleButton()
        {
            GarUIButtonBattleController controller = FindObjectOfType<GarUIButtonBattleController>();
            FieldInfo field = typeof(GarUIButtonBattleController).GetField("currentButton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            UIButton button = controller == null || field == null ? null : field.GetValue(controller) as UIButton;
            if (button == null)
            {
                RevivalLog.Info("click battle: button not found");
                return;
            }
            RevivalLog.Info("click battle: clicking " + button.gameObject.name + " (enabled=" + button.isEnabled + ")");
            button.gameObject.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
        }

        private static float _fireUntil;

        // Stops the player's controller reading the fire buttons and sets the
        // weapon groups' trigger state directly.
        private static void HoldTriggers(bool hold)
        {
            try
            {
                JunkWars.Client.Robot.RobotAvatar robot = JunkWars.Client.Arena.BattleManager.arena == null ? null : JunkWars.Client.Arena.BattleManager.arena.possessedRobot;
                if (robot == null || robot.actionControler == null)
                {
                    return;
                }
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                object controller = robot.actionControler;
                System.Type type = controller.GetType();
                FieldInfo disabled = null;
                for (System.Type t = type; t != null && disabled == null; t = t.BaseType)
                {
                    disabled = t.GetField("isShootingDisabled", any);
                }
                if (disabled != null)
                {
                    disabled.SetValue(controller, hold);
                }
                foreach (string side in new[] { "leftSidedWeapons", "rightSidedWeapons" })
                {
                    FieldInfo group = null;
                    for (System.Type t = type; t != null && group == null; t = t.BaseType)
                    {
                        group = t.GetField(side, any);
                    }
                    object weapons = group == null ? null : group.GetValue(controller);
                    if (weapons != null)
                    {
                        FieldInfo firing = weapons.GetType().GetField("isFireing", any);
                        if (firing != null)
                        {
                            firing.SetValue(weapons, hold);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("fire test aid failed", ex);
            }
        }

        private void OnApplicationQuit()
        {
            RevivalLog.Info("Game is quitting (" + Time.realtimeSinceStartup.ToString("0") + "s after start)");
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
                Step step = _steps[0];
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
                case "buy":
                    ItemType type = (ItemType)Enum.Parse(typeof(ItemType), words[1], true);
                    PhotonNetwork.BuyItem(string.Join(" ", words.Skip(2).ToArray()), (byte)type, false);
                    break;
                case "state":
                    GarageState state = (GarageState)Enum.Parse(typeof(GarageState), words[1], true);
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
                    JunkWars.Client.Chat.ChatManager.Instance.SendGarageChatMessage(string.Join(" ", words.Skip(1).ToArray()));
                    break;
                case "op": // op <code> [<key>=<i|b|s|f|B>:<value> ...]: send a raw operation, as the client would
                    SendRawOp(words);
                    break;
                case "call": // call <Method> [args...]: a PhotonNetwork / networking peer method with simple arguments
                    CallNetworkMethod(words);
                    break;
                case "goto": // goto flag|delivery|bomb|base|enemy: move my robot onto an objective or an enemy robot (test aid)
                    GoTo(words[1]);
                    break;
                case "plant": // plant the carried bomb at the enemy base (test aid)
                    JunkWars.Client.Arena.BattleManager.arena.PlaceBomb(JunkWars.Client.Arena.BattleManager.arena.possessedRobot, 1 - JunkWars.Client.Arena.BattleManager.arena.possessedRobot.teamId);
                    break;
                case "server": // server enter | server exit: apply this game's own server.cfg as if hosting (test aid)
                    if (words[1] == "enter")
                    {
                        ServerRules.Enter(ServerRules.Hosted);
                    }
                    else
                    {
                        ServerRules.Exit();
                    }
                    break;
                case "repair": // repair every robot, as the Repair All button does for the shown one (test aid)
                    PhotonNetwork.RepairItems(PlayerManager.Instance.player.valueObject.ownedRobotAssemblies.Select(r => r.assemblyID).ToList(), new List<int>(), new List<int>());
                    break;
                case "boost": // boost <slot>: use the consumable boost in that slot, as its key would (test aid)
                    JunkWars.Client.Arena.BattleManager.arena.possessedRobot.ConsumableBoostTryActivate(int.Parse(words[1], CultureInfo.InvariantCulture));
                    break;
                case "fire": // fire <seconds>: hold both triggers in a battle (test aid)
                    _fireUntil = Time.realtimeSinceStartup + float.Parse(words[1], CultureInfo.InvariantCulture);
                    break;
                case "click": // click battle: press the real BATTLE button, as the mouse would
                    ClickBattleButton();
                    break;
                case "battle": // the BATTLE button, including its readiness checks
                    Messenger<Message>.Invoke(GarageMainGUI.SHOW_BATTLE_WINDOW, new Message(string.Empty, null));
                    break;
                case "toggle": // garage UI tabs, as clicking them would: toggle ToggleStore ToggleStoreBuy
                    ObjectMap map = ObjectMapsInstances.Get(ObjectMap.MapType.GarageUI);
                    foreach (string name in words.Skip(1))
                    {
                        map.FindObjectComponent<UIToggle>(name).value = true;
                    }
                    break;
                case "buttons": // log everything clickable on screen (UI colliders), with labels and scripts
                    foreach (Collider c in FindObjectsOfType<Collider>().Where(c => c.enabled && c.gameObject.activeInHierarchy && PathOf(c.transform).StartsWith("UI Root")))
                    {
                        UILabel label = c.GetComponentInChildren<UILabel>();
                        RevivalLog.Info("  clickable " + PathOf(c.transform) + " \"" + (label == null ? string.Empty : label.text.Replace("\n", " ")) + "\" [" +
                            string.Join(",", c.GetComponents<MonoBehaviour>().Select(m => m.GetType().Name).ToArray()) + "]");
                    }
                    break;
                case "press": // press <text>: click the first visible button whose name or label contains the text
                    string wanted = string.Join(" ", words.Skip(1).ToArray());
                    Collider hit = FindObjectsOfType<Collider>().FirstOrDefault(c => c.enabled && c.gameObject.activeInHierarchy && PathOf(c.transform).StartsWith("UI Root") &&
                        (PathOf(c.transform).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 || LabelOf(c).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0));
                    RevivalLog.Info(hit == null ? "press: nothing matching \"" + wanted + "\"" : "press: clicking " + PathOf(hit.transform));
                    if (hit != null)
                    {
                        hit.gameObject.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
                    }
                    break;
                case "arcade": // what clicking the garage's arcade machine does
                    PopUpWindowsManager.Instance.RegisterWindow("Fun-O-Matic", "Alienoids ver. 1.0", new string[1] { Localizator.Get("GN_Ok") }, new Action[1], 2, 11);
                    break;
                case "inspect": // inspect <text>: the simple public fields of every script on the matching UI objects
                    string part = string.Join(" ", words.Skip(1).ToArray());
                    foreach (Collider c in FindObjectsOfType<Collider>().Where(c => c.gameObject.activeInHierarchy && PathOf(c.transform).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        RevivalLog.Info("inspect " + PathOf(c.transform));
                        foreach (MonoBehaviour m in c.GetComponents<MonoBehaviour>())
                        {
                            LogFields(m);
                        }
                    }
                    break;
                case "urls": // every web link the UI can open, loaded or not
                    foreach (GarUIOpenBrowserUrl link in Resources.FindObjectsOfTypeAll<GarUIOpenBrowserUrl>())
                    {
                        RevivalLog.Info("  link " + PathOf(link.transform) + " -> " + link.url + (link.localize ? " = " + Localizator.Get(link.url) : string.Empty));
                    }
                    break;
                case "type": // type <text>: put text in the first visible text box
                    UIInput input = FindObjectsOfType<UIInput>().FirstOrDefault(i => i.gameObject.activeInHierarchy);
                    RevivalLog.Info(input == null ? "type: no text box" : "type: into " + PathOf(input.transform));
                    if (input != null)
                    {
                        input.value = string.Join(" ", words.Skip(1).ToArray());
                    }
                    break;
                case "redeem": // redeem <code>: the Redeem Code window's request, through the game's own code
                    RedeemPromoCode.Redeem(words[1], r => RevivalLog.Info("redeem " + words[1] + ": " + r));
                    break;
                case "arcade2": // arcade on|off: the new arcade mode, as clicking the machine / LEAVE ARCADE does
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
                    ArcadeGame_RobotShooter shooter = FindObjectOfType<ArcadeGame_RobotShooter>();
                    if (shooter != null)
                    {
                        if (shooter.paused)
                        {
                            shooter.ResumeGame();
                        }
                        shooter.Shoot();
                        RevivalLog.Info("arcadeplay: running=" + shooter.running + " paused=" + shooter.paused);
                    }
                    break;
                case "queue": // queue on|off: the camera move to and from the arcade machine (the old matchmaking queue view)
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
                case "ammo": // ammo [buy <type> <tier> <qty> | auto on|off | max]: log stock and loads, or what the supply screen does (test aid)
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
                case "log": // log <text>: a marker in the log
                    RevivalLog.Info("---- " + string.Join(" ", words.Skip(1).ToArray()));
                    break;
                default:
                    RevivalLog.Info("autorun: unknown command " + words[0]);
                    break;
            }
        }

        private static void AmmoCommand(string[] words)
        {
            Player player = PlayerManager.Instance.player;
            switch (words.Length > 1 ? words[1] : string.Empty)
            {
                case "buy":
                    DataManager.Instance.BuyAmmo(new[] { new BuyAmmoData { AmmoType = words[2], tier = int.Parse(words[3], CultureInfo.InvariantCulture), quantity = int.Parse(words[4], CultureInfo.InvariantCulture) } }, 0);
                    return;
                case "auto":
                    player.SelectedRobotAssembly.valueObject.autoResupply = words[2] == "on";
                    DataManager.Instance.SetRobotsSlotSupplyMode(player.valueObject.selectedRobotIndex, words[2] == "on");
                    return;
                case "max": // "Max load all", then OK or Buy
                    List<PartToAmmmoDistribLink> links = player.GetPerRobotDistributionData(player.SelectedRobotAssembly) ?? new List<PartToAmmmoDistribLink>();
                    links.ForEach(l => l.distribNode.desiredAmmount = Math.Max(0, l.distribNode.maxAmmount));
                    BuyAmmoData[] order = player.GetAmmoPurcase(links);
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
            JunkWars.Common.Player.ValueObjects.PlayerVO saved = PlayerState.Current;
            RevivalLog.Info("ammo: economy " + (AmmoOps.Economy ? "on" : "off") + ", stock " +
                string.Join("; ", saved.ownedAmmo.Select(a => a.indent + " " + string.Join("/", (a.quantity ?? new int[0]).Select(q => q.ToString()).ToArray())).ToArray()));
            foreach (JunkWars.Common.Robot.ValueObjects.AmmoDistributionNode n in saved.ammoDistribution.nodes)
            {
                RevivalLog.Info("ammo:   part " + n.moudleID + " " + (n.desiredAmmoType.Length == 0 ? "energy" : n.desiredAmmoType + " t" + n.tier) +
                    " current " + n.currentAmmount + " desired " + n.desiredAmmount + " max " + n.maxAmmount);
            }
            JunkWars.Client.Robot.RobotAvatar robot = JunkWars.Client.Arena.BattleManager.arena == null ? null : JunkWars.Client.Arena.BattleManager.arena.possessedRobot;
            foreach (JunkWars.Client.Robot.WeaponBarrel barrel in robot == null || robot.weapons == null ? new JunkWars.Client.Robot.WeaponBarrel[0] : robot.weapons.SelectMany(w => w.weaponBarrels))
            {
                RevivalLog.Info("ammo:   in battle part " + barrel.partVO.partID + " " + (string.IsNullOrEmpty(barrel.currentAmmoType) ? "energy" : barrel.currentAmmoType) +
                    " " + barrel.CurrentAmmo + "/" + barrel.MaxAmmo);
            }
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null && path.Length < 120; p = p.parent)
            {
                path = p.name + "/" + path;
            }
            return path;
        }

        private static void LogFields(object target)
        {
            foreach (FieldInfo f in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = f.GetValue(target);
                if (value == null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum)
                {
                    RevivalLog.Info("    " + target.GetType().Name + "." + f.Name + " = " + value);
                }
                else if (value is System.Collections.IEnumerable)
                {
                    foreach (object item in (System.Collections.IEnumerable)value)
                    {
                        if (item != null && !(item is UnityEngine.Object))
                        {
                            RevivalLog.Info("    " + target.GetType().Name + "." + f.Name + "[]:");
                            LogFields(item);
                        }
                    }
                }
                else if (value is UnityEngine.Object)
                {
                    RevivalLog.Info("    " + target.GetType().Name + "." + f.Name + " -> " + ((UnityEngine.Object)value).name);
                }
            }
        }

        private static string LabelOf(Component clickable)
        {
            UILabel label = clickable.GetComponentInChildren<UILabel>();
            return label == null || label.text == null ? string.Empty : label.text.Replace("\n", " ");
        }

        private static void GoTo(string what)
        {
            JunkWars.Client.Robot.RobotAvatar robot = JunkWars.Client.Arena.BattleManager.arena.possessedRobot;
            int team = robot.teamId;
            Transform target = null;
            switch (what)
            {
                case "flag":
                    target = FindObjectsOfType<TeamFlag>().Where(f => f.OwningTeamID != team).Select(f => f.transform).FirstOrDefault();
                    break;
                case "delivery":
                    target = FindObjectsOfType<FlagDeliveryPoint>().Where(p => p.owningTeamID == team).Select(p => p.transform).FirstOrDefault();
                    break;
                case "bomb":
                    target = FindObjectsOfType<TeamBomb>().Where(b => b.OwningTeamID == team).Select(b => b.transform).FirstOrDefault();
                    break;
                case "enemy":
                    target = JunkWars.Client.Arena.BattleManager.arena.robots.Values.Where(r => r.teamId != team && !r.destroyed && r.capusle != null).Select(r => r.capusle.transform).FirstOrDefault();
                    break;
                case "base":
                    target = FindObjectsOfType<TeamBase>().Where(b => b.owningTeamID != team).Select(b => b.bombPlantPoint != null ? b.bombPlantPoint.transform : b.transform).FirstOrDefault();
                    break;
            }
            if (target == null)
            {
                RevivalLog.Info("goto: no " + what + " in this arena");
                return;
            }
            Rigidbody body = robot.capusle.GetComponent<Rigidbody>();
            body.position = target.position + Vector3.up * 0.5f;
            body.velocity = Vector3.zero;
            RevivalLog.Info("goto " + what + ": moved to " + target.position.ToString("0.0") + " (flag carrier=" +
                JunkWars.Client.Arena.BattleManager.arena.gamemodeManager.IsFlagCarrier(robot) + ", bomb carrier=" +
                JunkWars.Client.Arena.BattleManager.arena.gamemodeManager.IsBombCarrier(robot) + ")");
        }

        private static void SendRawOp(string[] words)
        {
            Dictionary<byte, object> parameters = new Dictionary<byte, object>();
            foreach (string word in words.Skip(2))
            {
                int eq = word.IndexOf('=');
                string value = word.Substring(eq + 3).Replace('_', ' ');
                object boxed;
                switch (word[eq + 1])
                {
                    case 'i': boxed = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case 'b': boxed = byte.Parse(value, CultureInfo.InvariantCulture); break;
                    case 'f': boxed = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case 'B': boxed = bool.Parse(value); break;
                    default: boxed = value; break;
                }
                parameters[byte.Parse(word.Substring(0, eq), CultureInfo.InvariantCulture)] = boxed;
            }
            PhotonNetwork.networkingPeer.OpCustom(byte.Parse(words[1], CultureInfo.InvariantCulture), parameters, true);
        }

        private static void CallNetworkMethod(string[] words)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            string[] args = words.Skip(2).ToArray();
            foreach (object target in new object[] { typeof(PhotonNetwork), PhotonNetwork.networkingPeer })
            {
                System.Type type = target as System.Type ?? target.GetType();
                foreach (MethodInfo method in type.GetMethods(any))
                {
                    ParameterInfo[] ps = method.GetParameters();
                    if (method.Name != words[1] || ps.Length != args.Length || (target is System.Type && !method.IsStatic))
                    {
                        continue;
                    }
                    object[] values = new object[args.Length];
                    try
                    {
                        for (int i = 0; i < args.Length; i++)
                        {
                            System.Type p = ps[i].ParameterType;
                            string text = args[i].Replace('_', ' ');
                            values[i] = p.IsEnum ? Enum.Parse(p, text, true) : Convert.ChangeType(text, p, CultureInfo.InvariantCulture);
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    object result = method.Invoke(method.IsStatic ? null : target, values);
                    RevivalLog.Info("call " + type.Name + "." + method.Name + " -> " + (result ?? "(void)"));
                    return;
                }
            }
            RevivalLog.Info("call: no method " + words[1] + " taking " + args.Length + " simple argument(s)");
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
            RevivalLog.Info("Screenshot " + path + " " + state);
        }
    }
}
