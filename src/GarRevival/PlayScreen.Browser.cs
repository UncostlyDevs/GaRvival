using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GarRevival;

// Server browser: masters, LAN, Steam and favourites, with filters, sorting, ping and direct connect.
// Also the FIND MATCH strip shown over the garage while matchmaking runs.
public sealed partial class PlayScreen
{
    private enum BrowserTab { Internet, Lan, Steam, Favourites }

    private enum Column { Name, Mode, Map, Players, Ping, Version }

    private static readonly float[] ColumnWidths = [236f, 150f, 120f, 100f, 70f, 90f];
    private const float StarWidth = 34f;
    private const float AutoRefresh = 30f;
    private const float DoubleClick = 0.4f;

    private bool _browser;
    private BrowserTab _browserTab = BrowserTab.Internet;
    private List<ServerEntry> _internet = [];
    private string _browserStatus = string.Empty;
    private float _refreshedAt = -100f;
    private Column _sortBy = Column.Players;
    private bool _sortDescending = true;
    private int _modeFilter; // 0 = any, else Modes[_modeFilter - 1]
    private bool _hideFull;
    private bool _hideOtherVersions = true;
    private string _selected;
    private string _lastClicked;
    private float _lastClickAt;
    private string _direct = string.Empty;
    private string _masterUrl = "http://";
    private Vector2 _browserScroll;
    private GUIStyle _cell, _cellOn, _toggle;

    private string ModeFilter => _modeFilter == 0 ? "any" : Modes[_modeFilter - 1].ToString();

    private void OpenBrowser(BrowserTab tab)
    {
        if (!_show) Toggle();
        _browser = true;
        _browserTab = tab;
        RefreshBrowser();
    }

    private void RefreshBrowser()
    {
        _refreshedAt = Time.realtimeSinceStartup;
        switch (_browserTab)
        {
            case BrowserTab.Steam:
                SteamNet.RefreshLobbies();
                break;
            case BrowserTab.Lan:
                LanDiscovery.Listening = true;
                break;
            default:
                if (!MasterClient.Configured)
                {
                    _browserStatus = MasterClient.NoMasterHint;
                    return;
                }
                _browserStatus = "Asking the master servers...";
                MasterClient.FetchServers((servers, error) =>
                {
                    _internet = servers;
                    _browserStatus = error ?? $"{servers.Count} server(s) listed.";
                    RevivalLog.Info($"Server browser: {_browserStatus}");
                });
                break;
        }
    }

    private void UpdateBrowser()
    {
        if (_browserTab != BrowserTab.Lan && Time.realtimeSinceStartup - _refreshedAt > AutoRefresh)
        {
            RefreshBrowser();
        }
    }

    private IEnumerable<ServerEntry> SourceRows() => _browserTab switch
    {
        BrowserTab.Internet => _internet,
        BrowserTab.Lan => LanDiscovery.Games.Select(g => new ServerEntry { Source = "LAN", Name = $"{g.Name}'s game", Address = g.Address, Port = g.Port, Players = g.Players }),
        BrowserTab.Steam => SteamNet.Lobbies.Select(l => new ServerEntry
        {
            Source = "Steam", Name = string.IsNullOrEmpty(l.Name) ? "Unnamed game" : l.Name, Lobby = l.Id.m_SteamID, Players = l.Players,
        }),
        // A favourite that a master also lists shows the live details.
        _ => Favourites.All.Select(f => _internet.FirstOrDefault(i => i.Key == f.Key) ?? f),
    };

    private List<ServerEntry> BrowserRows()
    {
        var rows = SourceRows().Where(e =>
            (_modeFilter == 0 || e.Mode.Length == 0 || e.Mode == ModeFilter) &&
            (!_hideFull || !e.Full) &&
            (!_hideOtherVersions || e.SameVersion));
        Func<ServerEntry, IComparable> key = e => SortKey(e, _sortBy);
        return (_sortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IComparable SortKey(ServerEntry e, Column column) => column switch
    {
        Column.Mode => ModeName(e.Mode),
        Column.Map => MapName(e.Map),
        Column.Players => e.Players,
        Column.Ping => PingOf(e) < 0 ? int.MaxValue : PingOf(e),
        Column.Version => e.Version,
        _ => e.Name.ToLowerInvariant(),
    };

    private static int PingOf(ServerEntry e) => e.Lobby != 0 ? Pinger.Unreachable : Pinger.Get(e.Lan.Length > 0 ? e.Lan[0] : e.Address, e.Port);

    private static string ModeName(string mode)
    {
        int i = Array.FindIndex(Modes, m => m.ToString() == mode);
        return i < 0 ? mode : ModeNames[i];
    }

    private static string MapName(string map)
    {
        int i = Array.FindIndex(MapIds, m => string.Equals(m, map, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? map : MapNames[i];
    }

    private void JoinEntry(ServerEntry entry)
    {
        if (!entry.SameVersion)
        {
            _browserStatus = $"That server runs a different GAR Revival multiplayer version ({entry.Version}, yours {MasterClient.Version}).";
            return;
        }
        Joiner.Start(entry.Targets(), (joined, message) =>
        {
            _browserStatus = message;
            if (joined) _browser = false;
        });
    }

    private void DirectConnect()
    {
        if (ServerEntry.TryParseAddress(_direct, out string host, out int port))
        {
            JoinEntry(new ServerEntry { Name = host, Address = host, Port = port });
        }
        else
        {
            _browserStatus = "Type an address like 203.0.113.7:27015.";
        }
    }

    private void FindMatchButton(float width)
    {
        GUI.enabled = MasterClient.Configured && !Matchmaker.Searching;
        if (GUILayout.Button("FIND MATCH", _start, GUILayout.Width(width), GUILayout.Height(36f)))
        {
            Matchmaker.Begin(ModeFilter);
            _show = false;
        }
        GUI.enabled = true;
    }

    private void BuildBrowserStyles()
    {
        if (_cell != null) return;
        _cell = new GUIStyle(_label) { wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(6, 4, 2, 2) };
        _cell.hover.background = _grey;
        _cell.hover.textColor = Color.white;
        _cellOn = new GUIStyle(_cell);
        _cellOn.normal.background = _cellOn.hover.background = Solid(0.42f, 0.30f, 0.05f, 1f);
        _cellOn.normal.textColor = Color.white;
        _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 15 };
        _toggle.normal.textColor = _toggle.onNormal.textColor = _toggle.hover.textColor = _toggle.onHover.textColor = new Color(0.9f, 0.9f, 0.9f);
    }

    private void DrawBrowser()
    {
        BuildBrowserStyles();
        GUILayout.BeginHorizontal();
        foreach (var tab in (BrowserTab[])Enum.GetValues(typeof(BrowserTab)))
        {
            if (GUILayout.Button(tab == BrowserTab.Lan ? "LAN" : tab.ToString().ToUpperInvariant(), _browserTab == tab ? _tabOn : _tabOff, GUILayout.Width(150f), GUILayout.Height(34f)))
            {
                _browserTab = tab;
                _selected = null;
                RefreshBrowser();
            }
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("REFRESH", _button, GUILayout.Width(110f), GUILayout.Height(34f)))
        {
            RefreshBrowser();
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);
        if (_browserTab == BrowserTab.Internet && !MasterClient.Configured)
        {
            DrawMasterSetup();
        }
        else
        {
            DrawFilters();
            DrawTable();
        }
        GUILayout.FlexibleSpace();
        GUILayout.Label(Joiner.Busy ? Joiner.Status : _browserStatus, _dim);
        DrawBrowserFooter();
    }

    private void DrawMasterSetup()
    {
        GUILayout.Label("No master server set. A master server lists games on the internet; anyone can run one (start-master.cmd).", _label);
        GUILayout.Label("Type its address to save it to settings.cfg (master=):", _dim);
        GUILayout.BeginHorizontal();
        _masterUrl = GUILayout.TextField(_masterUrl, 200, _field, GUILayout.Height(34f));
        if (GUILayout.Button("SAVE", _button, GUILayout.Width(120f), GUILayout.Height(34f)))
        {
            string[] masters = Settings.ParseMasters(_masterUrl);
            if (masters.Length == 0)
            {
                _browserStatus = "A master address starts with http://, e.g. http://master.example.org:27090";
            }
            else
            {
                Settings.Set("master", string.Join(",", masters));
                RefreshBrowser();
            }
        }
        GUILayout.EndHorizontal();
    }

    private void DrawFilters()
    {
        GUILayout.BeginHorizontal(GUILayout.Height(36f));
        if (GUILayout.Button(_modeFilter == 0 ? "Any mode" : ModeNames[_modeFilter - 1], _button, GUILayout.Width(220f), GUILayout.Height(32f)))
        {
            _modeFilter = (_modeFilter + 1) % (Modes.Length + 1);
        }
        GUILayout.Space(16f);
        _hideFull = GUILayout.Toggle(_hideFull, " Hide full", _toggle, GUILayout.Height(32f));
        GUILayout.Space(16f);
        _hideOtherVersions = GUILayout.Toggle(_hideOtherVersions, " Hide other versions", _toggle, GUILayout.Height(32f));
        GUILayout.FlexibleSpace();
        FindMatchButton(180f);
        GUILayout.EndHorizontal();
    }

    private void DrawTable()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(string.Empty, _dim, GUILayout.Width(StarWidth));
        foreach (var column in (Column[])Enum.GetValues(typeof(Column)))
        {
            string arrow = _sortBy == column ? (_sortDescending ? " v" : " ^") : string.Empty;
            if (GUILayout.Button(column.ToString().ToUpperInvariant() + arrow, _tabOff, GUILayout.Width(ColumnWidths[(int)column]), GUILayout.Height(26f)))
            {
                _sortDescending = _sortBy == column ? !_sortDescending : column == Column.Players;
                _sortBy = column;
            }
        }
        GUILayout.EndHorizontal();
        var rows = BrowserRows();
        _browserScroll = GUILayout.BeginScrollView(_browserScroll, GUILayout.Height(222f));
        if (rows.Count == 0)
        {
            GUILayout.Label(_browserTab == BrowserTab.Steam && !SteamNet.Available ? "Steam is not available." : "No games to show.", _dim);
        }
        foreach (var entry in rows)
        {
            DrawRow(entry);
        }
        GUILayout.EndScrollView();
    }

    private void DrawRow(ServerEntry e)
    {
        var style = e.Key == _selected ? _cellOn : _cell;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(Favourites.Contains(e) ? "*" : "+", _button, GUILayout.Width(StarWidth - 4f), GUILayout.Height(24f)))
        {
            Favourites.Toggle(e);
        }
        GUILayout.Space(4f);
        string[] cells =
        [
            e.Name + (e.Dedicated ? "  [dedicated]" : string.Empty),
            ModeName(e.Mode),
            MapName(e.Map),
            e.MaxPlayers > 0 ? $"{e.Players}/{e.MaxPlayers}" : e.Players.ToString(),
            e.Lobby != 0 ? "Steam" : Pinger.Label(PingOf(e)),
            e.Version,
        ];
        bool clicked = false;
        for (int i = 0; i < cells.Length; i++)
        {
            clicked |= GUILayout.Button(cells[i], style, GUILayout.Width(ColumnWidths[i]), GUILayout.Height(24f));
        }
        GUILayout.EndHorizontal();
        if (clicked)
        {
            bool second = _lastClicked == e.Key && Time.realtimeSinceStartup - _lastClickAt < DoubleClick;
            _selected = _lastClicked = e.Key;
            _lastClickAt = Time.realtimeSinceStartup;
            if (second) JoinEntry(e);
        }
    }

    private void DrawBrowserFooter()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("DIRECT CONNECT", _header, GUILayout.Width(150f), GUILayout.Height(36f));
        _direct = GUILayout.TextField(_direct, 64, _field, GUILayout.Width(220f), GUILayout.Height(36f));
        if (GUILayout.Button("CONNECT", _button, GUILayout.Width(110f), GUILayout.Height(36f)))
        {
            DirectConnect();
        }
        if (GUILayout.Button("+ FAVOURITE", _button, GUILayout.Width(130f), GUILayout.Height(36f)) && ServerEntry.TryParseAddress(_direct, out string host, out int port))
        {
            var entry = new ServerEntry { Name = $"{host}:{port}", Address = host, Port = port };
            if (!Favourites.Contains(entry)) Favourites.Toggle(entry);
        }
        GUILayout.FlexibleSpace();
        if (Joiner.Busy)
        {
            if (GUILayout.Button("CANCEL", _button, GUILayout.Width(170f), GUILayout.Height(40f))) Joiner.Cancel();
        }
        else
        {
            var selected = BrowserRows().FirstOrDefault(e => e.Key == _selected);
            GUI.enabled = selected != null;
            if (GUILayout.Button("JOIN", _start, GUILayout.Width(170f), GUILayout.Height(40f)) && selected != null)
            {
                JoinEntry(selected);
            }
            GUI.enabled = true;
        }
        GUILayout.EndHorizontal();
    }

    private static Rect StripRect(float width) => new((width - 720f) / 2f, 720f - 120f, 720f, 64f);

    // Clicks on the strip must not reach the garage behind it.
    private static bool OverStrip()
    {
        float scale = Screen.height / 720f;
        var mouse = new Vector2(Input.mousePosition.x / scale, (Screen.height - Input.mousePosition.y) / scale);
        return StripRect(Screen.width / scale).Contains(mouse);
    }

    private void DrawMatchStrip(float width)
    {
        var r = StripRect(width);
        GUI.DrawTexture(r, _panel);
        GUI.DrawTexture(new Rect(r.x, r.y, 6f, r.height), Matchmaker.Searching ? _amber : _green);
        GUI.Label(new Rect(r.x + 18f, r.y + 4f, r.width - 200f, r.height - 8f), Matchmaker.Text, _label);
        if (GUI.Button(new Rect(r.xMax - 164f, r.y + 12f, 148f, 40f), Matchmaker.Searching ? "CANCEL" : "OK", _button))
        {
            Matchmaker.Cancel();
        }
    }

    // Autorun test hooks.
    public static void ShowBrowser(string tab)
    {
        if (_instance != null) _instance.OpenBrowser(tab == null ? BrowserTab.Internet : (BrowserTab)Enum.Parse(typeof(BrowserTab), tab, true));
    }

    public static bool JoinBrowserRow(int index)
    {
        var rows = _instance == null ? [] : _instance.BrowserRows();
        if (index < 0 || index >= rows.Count) return false;
        _instance.JoinEntry(rows[index]);
        return true;
    }

    public static void LogBrowserRows()
    {
        if (_instance == null) return;
        var rows = _instance.BrowserRows();
        RevivalLog.Info($"Server browser {_instance._browserTab}: {rows.Count} row(s); {_instance._browserStatus}");
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i];
            RevivalLog.Info($"  [{i}] {e.Name} | {e.Mode} | {e.Map} | {e.Players}/{e.MaxPlayers} | " +
                (e.Lobby != 0 ? $"steam {e.Lobby}" : $"{e.Address}:{e.Port} ping {Pinger.Label(PingOf(e))}") + $" | v{e.Version}");
        }
    }
}
