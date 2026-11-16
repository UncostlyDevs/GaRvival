using System;
using System.Collections.Generic;
using JunkWars.Client;
using JunkWars.Common.Arena;
using UnityEngine;

namespace GarRevival;

// The match screen (BATTLE in the garage, or F9): bot match, host a LAN/Steam lobby, or join a game.
// In a lobby it shows the players; the host sets up the match and starts it.
public sealed partial class PlayScreen : MonoBehaviour
{
    private enum Tab { Bots, Host, Join }

    internal const int DefaultPort = 27015;
    // Robots per team: the arenas were built for 5 against 5.
    private const int MaxTeam = 5;
    private const float PanelWidth = 860f;
    private const float PanelHeight = 540f;
    internal static readonly string[] MapIds = ["random", "Western", "WesternNight", "Laundry", "Pirates", "Medieval", "DragonTemple", "Pompei", "Dungeon"];
    internal static readonly string[] MapNames = ["Random", "Western", "Western (night)", "Suburbia", "Pirates", "Medieval", "Dragon Temple", "Pompeii", "Dungeon"];
    internal static readonly GameModeType[] Modes = [GameModeType.TeamDeadmach, GameModeType.CaptureTheFlag, GameModeType.DestoryBase];
    internal static readonly string[] ModeNames = ["Team Deathmatch", "Capture the Batteries", "Base Destruction"];

    private static PlayScreen _instance;
    private static float _startRequestedAt = -100f;

    private bool _show;
    private Tab _tab = Tab.Bots;
    private OfflineBattle.Settings _settings;
    private string _address = $"127.0.0.1:{DefaultPort}";
    private Vector2 _listScroll;
    private Vector2 _rosterScroll;
    private bool _uiBlocked;
    private float _labelWidth = 270f;
    private float _valueWidth = 230f;

    private GUIStyle _title, _header, _label, _dim, _button, _tabOn, _tabOff, _start, _field, _value;
    private Texture2D _panel, _bar, _dark, _green, _greenHover, _amber, _grey, _greyHover;

    private void Awake() => _instance = this;

    public static void Open()
    {
        if (_instance != null && !_instance._show) _instance.Toggle();
    }

    public static bool Showing => _instance != null && _instance._show;

    public void Toggle()
    {
        _show = !_show;
        if (_show)
        {
            _browser = false;
            _settings = OfflineBattle.LoadSettings();
            LanDiscovery.Listening = true;
            if (SteamNet.Available && Session.Role == Session.Roles.None) SteamNet.RefreshLobbies();
        }
    }

    // The garage's BATTLE handler ends in PhotonNetwork.JoinRandomBattle; true when that call comes from
    // this screen's START passing the garage's robot checks, not from a fresh press of BATTLE.
    public static bool ConsumeStartRequest()
    {
        bool requested = Time.realtimeSinceStartup - _startRequestedAt < 15f;
        _startRequestedAt = -100f;
        return requested;
    }

    // Runs the garage's own checks (complete robot, energy, repairs), which end in the BATTLE hook.
    private void RequestStart()
    {
        OfflineBattle.SaveSettings(_settings);
        _show = false;
        _startRequestedAt = Time.realtimeSinceStartup;
        Messenger<Message>.Invoke(GarageMainGUI.RESET_BATTLE_BUTTON, new Message(GarageMainGUI.RESET_BATTLE_BUTTON, null));
        Messenger<Message>.Invoke(GarageMainGUI.SHOW_BATTLE_WINDOW, new Message(string.Empty, null));
    }

    // Autorun test hooks.
    public void SelectTab(string name) => _tab = (Tab)Enum.Parse(typeof(Tab), name, true);

    public void PressStart()
    {
        _settings ??= OfflineBattle.LoadSettings();
        RequestStart();
    }

    public void HostLan() => Session.HostTcp(DefaultPort);

    public bool JoinFirstLanGame()
    {
        if (LanDiscovery.Games.Count == 0) return false;
        Session.JoinTcp(LanDiscovery.Games[0].Address, LanDiscovery.Games[0].Port);
        return true;
    }

    public static void JoinAddress(string address)
    {
        if (ServerEntry.TryParseAddress(address, out string host, out int port))
        {
            Session.JoinTcp(host, port);
        }
        else
        {
            Session.Status = $"Not an address: {address}";
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9)) Toggle();
        if (_show && Input.GetKeyDown(KeyCode.Escape)) _show = false;
        if (_show && _browser) UpdateBrowser();
        bool visible = InGarage && (_show || (Matchmaker.Visible && OverStrip()));
        if (visible != _uiBlocked)
        {
            // Clicks on this screen must not also land on the garage UI behind it.
            _uiBlocked = visible;
            for (int i = 0; i < UICamera.list.size; i++)
            {
                UICamera.list[i].enabled = !visible;
            }
        }
    }

    private static bool InGarage => GameManager.Instance.currentGameState == GameState.Garage;

    private void OnGUI()
    {
        if (!InGarage || (!_show && !Matchmaker.Visible)) return;
        BuildStyles();
        GUI.depth = -1000;
        float scale = Screen.height / 720f;
        var old = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float width = Screen.width / scale;
        if (!_show)
        {
            DrawMatchStrip(width);
            GUI.matrix = old;
            return;
        }
        var panel = new Rect((width - PanelWidth) / 2f, (720f - PanelHeight) / 2f, PanelWidth, PanelHeight);
        GUI.DrawTexture(new Rect(0f, 0f, width, 720f), _dark);
        GUI.DrawTexture(panel, _panel);
        GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, 48f), _bar);
        GUI.Label(new Rect(panel.x + 20f, panel.y + 6f, 400f, 40f), _browser ? "SERVER BROWSER" : Session.Role == Session.Roles.None ? "PLAY" : "LOBBY", _title);
        if (GUI.Button(new Rect(panel.xMax - 44f, panel.y + 8f, 32f, 32f), "X", _tabOff))
        {
            if (_browser)
            {
                _browser = false;
            }
            else
            {
                _show = false;
            }
        }
        GUILayout.BeginArea(new Rect(panel.x + 20f, panel.y + 60f, panel.width - 40f, panel.height - 76f));
        if (_browser)
        {
            DrawBrowser();
        }
        else if (Session.Role == Session.Roles.None)
        {
            DrawTabs();
            switch (_tab)
            {
                case Tab.Bots: DrawBotMatch(); break;
                case Tab.Host: DrawHost(); break;
                default: DrawJoin(); break;
            }
        }
        else
        {
            DrawLobby();
        }
        GUILayout.EndArea();
        GUI.matrix = old;
    }

    private void DrawTabs()
    {
        GUILayout.BeginHorizontal();
        TabButton(Tab.Bots, "BOT MATCH");
        TabButton(Tab.Host, "HOST GAME");
        TabButton(Tab.Join, "JOIN GAME");
        GUILayout.EndHorizontal();
        GUILayout.Space(14f);
    }

    private void TabButton(Tab tab, string text)
    {
        if (GUILayout.Button(text, _tab == tab ? _tabOn : _tabOff, GUILayout.Height(38f)))
        {
            _tab = tab;
        }
    }

    private void DrawBotMatch()
    {
        GUILayout.Label("Play a match against bots on this PC.", _dim);
        GUILayout.Space(8f);
        DrawSettings(false);
        GUILayout.FlexibleSpace();
        StartButton("START MATCH");
    }

    private void DrawHost()
    {
        GUILayout.Label("Open a lobby. Friends join it, then you set up the match and start it.", _dim);
        GUILayout.Space(16f);
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(390f));
        GUILayout.Label("LOCAL NETWORK", _header);
        GUILayout.Label("Players on the same network see your game under JOIN GAME. Others can join by your address.", _label);
        GUILayout.Space(8f);
        if (GUILayout.Button("HOST LAN GAME", _button, GUILayout.Height(40f)))
        {
            Session.HostTcp(DefaultPort);
        }
        GUILayout.EndVertical();
        GUILayout.Space(30f);
        GUILayout.BeginVertical();
        GUILayout.Label("STEAM", _header);
        GUILayout.Label("Anyone with the game and this mod can find your game. No network setup needed.", _label);
        GUILayout.Space(8f);
        GUI.enabled = SteamNet.Available;
        if (GUILayout.Button("HOST ON STEAM", _button, GUILayout.Height(40f)))
        {
            Session.HostSteam();
        }
        GUI.enabled = true;
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        GUILayout.Space(16f);
        if (Session.Status != "Not in a session")
        {
            GUILayout.Label(Session.Status, _dim);
        }
    }

    private void DrawJoin()
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("SERVER BROWSER", _button, GUILayout.Width(200f), GUILayout.Height(36f)))
        {
            OpenBrowser(BrowserTab.Internet);
        }
        GUILayout.Space(10f);
        FindMatchButton(200f);
        GUILayout.Space(10f);
        GUILayout.Label(MasterClient.Configured ? "Internet servers, filters, favourites; or join the best server for you." : MasterClient.NoMasterHint, _dim);
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);
        _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(250f));
        GUILayout.Label("LOCAL NETWORK GAMES", _header);
        if (LanDiscovery.Games.Count == 0)
        {
            GUILayout.Label("Searching... no games found yet.", _dim);
        }
        foreach (var game in LanDiscovery.Games.ToArray())
        {
            GameRow($"{game.Name}'s game", $"{game.Players} player{(game.Players == 1 ? "" : "s")}   {game.Address}", () => Session.JoinTcp(game.Address, game.Port));
        }
        GUILayout.Space(12f);
        GUILayout.BeginHorizontal();
        GUILayout.Label("STEAM GAMES", _header);
        GUILayout.FlexibleSpace();
        GUI.enabled = SteamNet.Available;
        if (GUILayout.Button("Refresh", _button, GUILayout.Width(100f), GUILayout.Height(28f)))
        {
            SteamNet.RefreshLobbies();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        if (SteamNet.Lobbies.Count == 0)
        {
            GUILayout.Label(SteamNet.Available ? (string.IsNullOrEmpty(SteamNet.ListStatus) ? "No games found." : SteamNet.ListStatus) : "Steam is not available.", _dim);
        }
        foreach (var lobby in SteamNet.Lobbies.ToArray())
        {
            GameRow(string.IsNullOrEmpty(lobby.Name) ? "Unnamed game" : lobby.Name, $"{lobby.Players} in lobby", () => Session.JoinSteam(lobby.Id));
        }
        GUILayout.EndScrollView();
        GUILayout.FlexibleSpace();
        GUILayout.Label("JOIN BY ADDRESS", _header);
        GUILayout.BeginHorizontal();
        _address = GUILayout.TextField(_address, 64, _field, GUILayout.Height(34f));
        if (GUILayout.Button("JOIN", _button, GUILayout.Width(120f), GUILayout.Height(34f)))
        {
            JoinAddress(_address);
        }
        GUILayout.EndHorizontal();
        if (Session.Status != "Not in a session")
        {
            GUILayout.Label(Session.Status, _dim);
        }
    }

    private void GameRow(string name, string detail, Action join)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(name, _label, GUILayout.Width(330f));
        GUILayout.Label(detail, _dim);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("JOIN", _button, GUILayout.Width(100f), GUILayout.Height(30f)))
        {
            join();
        }
        GUILayout.EndHorizontal();
    }

    private void DrawLobby()
    {
        bool host = Session.Role == Session.Roles.Host;
        GUILayout.Label(Session.Status, _dim);
        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();

        GUILayout.BeginVertical(GUILayout.Width(280f));
        GUILayout.Label($"PLAYERS ({Session.Roster.Count})", _header);
        _rosterScroll = GUILayout.BeginScrollView(_rosterScroll, GUILayout.Height(210f));
        foreach (string name in new List<string>(Session.Roster))
        {
            GUILayout.Label(Session.DisplayName(name) + (name == Session.LocalName ? "  (you)" : string.Empty), _label);
        }
        GUILayout.EndScrollView();
        if (host && Session.TcpPort > 0)
        {
            GUILayout.Label("Your address:", _dim);
            foreach (string address in LanDiscovery.LocalAddresses())
            {
                GUILayout.Label($"{address}:{Session.TcpPort}", _label);
            }
        }
        if (host && MasterClient.Status.Length > 0)
        {
            GUILayout.Label(MasterClient.Status, _dim);
        }
        GUILayout.EndVertical();

        GUILayout.Space(24f);
        GUILayout.BeginVertical();
        if (host)
        {
            GUILayout.Label("MATCH", _header);
            DrawSettings(true);
            GUILayout.Label("Bots fill the empty places on each team.", _dim);
        }
        else
        {
            GUILayout.Label("MATCH", _header);
            GUILayout.Label("The host chooses the match and starts it. Stay in the garage with a complete robot selected.", _label);
            GUILayout.Label("You can close this screen (X) and keep working on your robot.", _dim);
        }
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(host ? "CLOSE LOBBY" : "LEAVE", _button, GUILayout.Width(160f), GUILayout.Height(44f)))
        {
            Session.Leave(null);
        }
        GUILayout.FlexibleSpace();
        if (host && GUILayout.Button("START MATCH", _start, GUILayout.Width(260f), GUILayout.Height(44f)))
        {
            RequestStart();
        }
        GUILayout.EndHorizontal();
    }

    private void StartButton(string text)
    {
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(text, _start, GUILayout.Width(260f), GUILayout.Height(44f)))
        {
            RequestStart();
        }
        GUILayout.EndHorizontal();
    }

    private void DrawSettings(bool lobby)
    {
        var s = _settings;
        // The lobby shares the panel with the player list.
        _labelWidth = lobby ? 170f : 270f;
        _valueWidth = lobby ? 190f : 230f;
        int map = Mathf.Max(0, Array.FindIndex(MapIds, m => string.Equals(m, s.Map, StringComparison.OrdinalIgnoreCase)));
        int picked = Cycle("Arena", MapNames[map], map, MapIds.Length);
        s.Map = MapIds[picked];
        int mode = Mathf.Max(0, Array.IndexOf(Modes, s.Mode));
        s.Mode = Modes[Cycle("Game mode", ModeNames[mode], mode, Modes.Length)];
        s.Enemies = Stepper("Enemy bots", s.Enemies, 1, MaxTeam, null);
        s.Allies = Mathf.Min(s.Allies, MaxTeam - 1);
        s.Allies = Stepper(lobby ? "Your team size" : "Ally bots", lobby ? s.Allies + 1 : s.Allies, lobby ? 1 : 0, lobby ? MaxTeam : MaxTeam - 1, null) - (lobby ? 1 : 0);
        s.Minutes = Stepper("Match length", s.Minutes, 1, 30, " min");
        s.WeakEnemies = Choice("Bot difficulty", s.WeakEnemies ? 1 : 0, "Normal", "Easy") == 1;
        if (lobby)
        {
            s.HumansSplit = Choice("Players", s.HumansSplit ? 1 : 0, "One team", "Two teams") == 1;
        }
    }

    private void Row(string label)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(38f));
        GUILayout.Label(label, _label, GUILayout.Width(_labelWidth), GUILayout.Height(34f));
    }

    private int Cycle(string label, string value, int index, int count)
    {
        Row(label);
        if (GUILayout.Button("<", _button, GUILayout.Width(40f), GUILayout.Height(34f)))
        {
            index = (index + count - 1) % count;
        }
        GUILayout.Label(value, _value, GUILayout.Width(_valueWidth), GUILayout.Height(34f));
        if (GUILayout.Button(">", _button, GUILayout.Width(40f), GUILayout.Height(34f)))
        {
            index = (index + 1) % count;
        }
        GUILayout.EndHorizontal();
        return index;
    }

    private int Stepper(string label, int value, int min, int max, string unit)
    {
        value = Mathf.Clamp(value, min, max);
        Row(label);
        if (GUILayout.Button("-", _button, GUILayout.Width(40f), GUILayout.Height(34f)))
        {
            value = Mathf.Max(min, value - 1);
        }
        GUILayout.Label(value + (unit ?? string.Empty), _value, GUILayout.Width(_valueWidth), GUILayout.Height(34f));
        if (GUILayout.Button("+", _button, GUILayout.Width(40f), GUILayout.Height(34f)))
        {
            value = Mathf.Min(max, value + 1);
        }
        GUILayout.EndHorizontal();
        return value;
    }

    private int Choice(string label, int selected, string first, string second)
    {
        Row(label);
        if (GUILayout.Button(first, selected == 0 ? _tabOn : _tabOff, GUILayout.Width(_valueWidth / 2f + 40f), GUILayout.Height(34f)))
        {
            selected = 0;
        }
        if (GUILayout.Button(second, selected == 1 ? _tabOn : _tabOff, GUILayout.Width(_valueWidth / 2f + 40f), GUILayout.Height(34f)))
        {
            selected = 1;
        }
        GUILayout.EndHorizontal();
        return selected;
    }

    private static Texture2D Solid(float r, float g, float b, float a)
    {
        var texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, new Color(r, g, b, a));
        texture.Apply();
        return texture;
    }

    private GUIStyle Text(int size, Color color, FontStyle weight, TextAnchor anchor)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = weight, alignment = anchor, wordWrap = true };
        style.normal.textColor = color;
        return style;
    }

    private GUIStyle Button(Texture2D normal, Texture2D hover, Color text, int size)
    {
        var style = new GUIStyle(GUI.skin.button) { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.normal.background = normal;
        style.hover.background = hover;
        style.active.background = hover;
        style.focused.background = normal;
        style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = text;
        return style;
    }

    private void BuildStyles()
    {
        if (_title != null && _panel != null) return;
        _panel = Solid(0.09f, 0.09f, 0.11f, 0.98f);
        _bar = Solid(0.55f, 0.10f, 0.10f, 1f);
        _dark = Solid(0f, 0f, 0f, 0.55f);
        _green = Solid(0.20f, 0.55f, 0.16f, 1f);
        _greenHover = Solid(0.27f, 0.68f, 0.22f, 1f);
        _amber = Solid(0.90f, 0.66f, 0.10f, 1f);
        _grey = Solid(0.22f, 0.22f, 0.26f, 1f);
        _greyHover = Solid(0.32f, 0.32f, 0.38f, 1f);
        var white = new Color(0.95f, 0.95f, 0.95f);
        _title = Text(26, Color.white, FontStyle.Bold, TextAnchor.MiddleLeft);
        _header = Text(17, new Color(1f, 0.78f, 0.2f), FontStyle.Bold, TextAnchor.MiddleLeft);
        _label = Text(16, white, FontStyle.Normal, TextAnchor.MiddleLeft);
        _dim = Text(15, new Color(0.72f, 0.72f, 0.76f), FontStyle.Normal, TextAnchor.MiddleLeft);
        _value = Text(17, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
        _button = Button(_grey, _greyHover, white, 16);
        _tabOff = Button(_grey, _greyHover, white, 16);
        _tabOn = Button(_amber, _amber, new Color(0.1f, 0.08f, 0.02f), 16);
        _start = Button(_green, _greenHover, Color.white, 20);
        _field = new GUIStyle(GUI.skin.textField) { fontSize = 16, alignment = TextAnchor.MiddleLeft };
    }
}
