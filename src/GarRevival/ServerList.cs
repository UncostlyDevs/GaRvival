using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Steamworks;

namespace GarRevival;

// One way into a game: a TCP address or a Steam lobby.
internal sealed class JoinTarget
{
    public string Label;
    public string Host;
    public int Port;
    public ulong Lobby;

    public void Start()
    {
        if (Lobby != 0)
        {
            Session.JoinSteam(new CSteamID(Lobby));
        }
        else
        {
            Session.JoinTcp(Host, Port);
        }
    }
}

// A server browser row: from a master, the LAN, Steam or the favourites file.
internal sealed class ServerEntry
{
    public string Source = "Internet";
    public string Name = string.Empty;
    public string Mode = string.Empty;
    public string Map = string.Empty;
    public string Version = string.Empty;
    public string Address = string.Empty;
    public int Port;
    public int Players;
    public int MaxPlayers;
    public bool Dedicated;
    public ulong Lobby;
    public string[] Lan = [];

    public string Key => Lobby != 0 ? "steam:" + Lobby : Address + ":" + Port;

    public bool Full => MaxPlayers > 0 && Players >= MaxPlayers;

    // Rows without a version (LAN, Steam, favourites) find out when joining.
    public bool SameVersion => Version.Length == 0 || Version == MasterClient.Version;

    // The host's LAN addresses first: a player behind the same router may not reach its public one.
    public List<JoinTarget> Targets()
    {
        if (Lobby != 0) return [new() { Label = $"{Name} (Steam)", Lobby = Lobby }];
        return Lan.Concat(new[] { Address }).Where(a => a.Length > 0).Distinct()
            .Select(a => new JoinTarget { Label = $"{Name} ({a}:{Port})", Host = a, Port = Port }).ToList();
    }

    public static ServerEntry FromMaster(Dictionary<string, object> o)
    {
        ulong.TryParse(o.Str("steamLobby"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong lobby);
        return new ServerEntry
        {
            Name = o.Str("name"),
            Mode = o.Str("mode"),
            Map = o.Str("map"),
            Version = o.Str("version"),
            Address = o.Str("address"),
            Port = o.Int("port"),
            Players = o.Int("players"),
            MaxPlayers = o.Int("maxPlayers"),
            Dedicated = o.Bool("dedicated"),
            Lobby = o.Str("transport") == "steam" ? lobby : 0,
            Lan = o.List("lan").OfType<string>().Take(4).ToArray(),
        };
    }

    // "host:port" (port 27015 when left out).
    public static bool TryParseAddress(string text, out string host, out int port)
    {
        host = (text ?? string.Empty).Trim();
        port = PlayScreen.DefaultPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(host.Substring(colon + 1), out port) || port < 1 || port > 65535) return false;
            host = host.Substring(0, colon);
        }
        return host.Length > 0 && host.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-');
    }
}

// save/favourites.txt, one per line: "tcp <host:port> <name>" or "steam <lobby id> <name>".
internal static class Favourites
{
    private static List<ServerEntry> _all;

    private static string FilePath => Path.Combine(Path.Combine(RevivalLog.Folder, "save" + Instance.Suffix), "favourites.txt");

    public static List<ServerEntry> All => _all ??= Load();

    public static bool Contains(ServerEntry entry) => All.Any(f => f.Key == entry.Key);

    public static void Toggle(ServerEntry entry)
    {
        if (Contains(entry))
        {
            All.RemoveAll(f => f.Key == entry.Key);
        }
        else
        {
            All.Add(new ServerEntry { Source = "Favourite", Name = entry.Name.Length > 0 ? entry.Name : entry.Key, Address = entry.Address, Port = entry.Port, Lobby = entry.Lobby });
        }
        Save();
    }

    private static List<ServerEntry> Load()
    {
        var list = new List<ServerEntry>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                string[] p = line.Trim().Split(new[] { ' ' }, 3);
                string name = p.Length > 2 ? p[2] : p.Length > 1 ? p[1] : string.Empty;
                if (p.Length >= 2 && p[0] == "tcp" && ServerEntry.TryParseAddress(p[1], out string host, out int port))
                {
                    list.Add(new ServerEntry { Source = "Favourite", Name = name, Address = host, Port = port });
                }
                else if (p.Length >= 2 && p[0] == "steam" && ulong.TryParse(p[1], out ulong lobby))
                {
                    list.Add(new ServerEntry { Source = "Favourite", Name = name, Lobby = lobby });
                }
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not read favourites.txt", ex);
        }
        return list;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, All.Select(f => (f.Lobby != 0 ? "steam " + f.Lobby : "tcp " + f.Address + ":" + f.Port) + " " + f.Name.Replace('\n', ' ')).ToArray());
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not save favourites.txt", ex);
        }
    }
}
