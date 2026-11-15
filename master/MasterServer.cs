// GAR Revival master server: lists game servers, ranks them for matchmaking
// and keeps an honour-based ladder. Plain HTTP/1.1 + JSON, GET and POST only.
// One file, C# 5, .NET Framework 4 or mono; start-master.cmd compiles and runs it:
//   csc /out:bin\GarRevival.Master.exe MasterServer.cs
//   mcs -out:bin/GarRevival.Master.exe MasterServer.cs && mono bin/GarRevival.Master.exe
// The optional first argument is the folder holding master.cfg (default: current folder).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GarRevivalMaster
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0)
                {
                    Directory.SetCurrentDirectory(args[0]);
                }
                new Master(Config.Load("master.cfg")).Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Master server stopped: " + ex.Message);
                return 1;
            }
        }
    }

    // ---- configuration ----

    internal sealed class Config
    {
        // key, default, comment. The first start writes these to master.cfg.
        private static readonly string[][] Keys =
        {
            new[] { "port", "27090", "TCP port to listen on (allow it through the firewall / forward it on the router)" },
            new[] { "publicAddress", "", "Address handed to players for game servers that register from this PC or LAN\n# (loopback or private address). Empty = the registrant's own address." },
            new[] { "serverTimeout", "60", "Seconds without a heartbeat before a server leaves the list" },
            new[] { "maxServers", "1000", "Most servers listed at once" },
            new[] { "maxServersPerIp", "16", "Most servers listed from one IP address" },
            new[] { "requestsPerMinute", "600", "Requests per minute from one IP address (a LAN party shares one)" },
            new[] { "registersPerMinute", "60", "Server registrations per minute from one IP address" },
            new[] { "resultsPerMinute", "60", "Match results per minute from one IP address" },
            new[] { "maxConnections", "64", "Requests handled at the same time" },
            new[] { "ladderFile", "master-ladder.txt", "Where the ladder is saved" },
        };

        public int Port, ServerTimeout, MaxServers, MaxServersPerIp, RequestsPerMinute, RegistersPerMinute, ResultsPerMinute, MaxConnections;
        public string PublicAddress, LadderFile;

        public static Config Load(string path)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "# GAR Revival master server\n" + string.Join(string.Empty, Keys.Select(k => "# " + k[2] + "\n" + k[0] + "=" + k[1] + "\n").ToArray()));
            }
            Dictionary<string, string> values = Keys.ToDictionary(k => k[0].ToLowerInvariant(), k => k[1]);
            foreach (string raw in File.ReadAllLines(path))
            {
                int hash = raw.IndexOf('#');
                string line = (hash > 0 ? raw.Substring(0, hash) : raw).Trim();
                int eq = line.IndexOf('=');
                if (!line.StartsWith("#") && eq > 0)
                {
                    values[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
                }
            }
            Config c = new Config();
            c.Port = Number(values, "port", 1, 65535);
            c.ServerTimeout = Number(values, "servertimeout", 15, 3600);
            c.MaxServers = Number(values, "maxservers", 1, 100000);
            c.MaxServersPerIp = Number(values, "maxserversperip", 1, 1000);
            c.RequestsPerMinute = Number(values, "requestsperminute", 10, 100000);
            c.RegistersPerMinute = Number(values, "registersperminute", 1, 10000);
            c.ResultsPerMinute = Number(values, "resultsperminute", 1, 10000);
            c.MaxConnections = Number(values, "maxconnections", 1, 1000);
            c.PublicAddress = values["publicaddress"];
            c.LadderFile = values["ladderfile"];
            if (c.PublicAddress.Length > 0 && !Check.IsHost(c.PublicAddress))
            {
                throw new InvalidDataException("master.cfg: publicAddress must be an IPv4 address or a host name");
            }
            if (c.LadderFile.Length == 0 || c.LadderFile.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new InvalidDataException("master.cfg: bad ladderFile");
            }
            return c;
        }

        private static int Number(Dictionary<string, string> values, string key, int min, int max)
        {
            int n;
            if (!int.TryParse(values[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < min || n > max)
            {
                throw new InvalidDataException("master.cfg: " + key + " must be a number from " + min + " to " + max);
            }
            return n;
        }
    }

    // ---- the service ----

    internal sealed class Master
    {
        public static readonly string[] Modes = { "TeamDeadmach", "CaptureTheFlag", "DestoryBase" };
        public static readonly string[] Maps = { "random", "Western", "WesternNight", "Laundry", "Pirates", "Medieval", "DragonTemple", "Pompei", "Dungeon" };
        private const int ResultInterval = 30;

        private readonly Config _config;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Listing> _servers = new Dictionary<string, Listing>();
        private readonly Ladder _ladder;
        private readonly RateLimiter _limiter = new RateLimiter();
        private readonly Log _log = new Log("master.log");
        private Semaphore _slots;
        private TcpListener _listener;
        private volatile bool _stopping;

        public Master(Config config)
        {
            _config = config;
            _ladder = new Ladder(config.LadderFile, _log);
        }

        public void Run()
        {
            _slots = new Semaphore(_config.MaxConnections, _config.MaxConnections);
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start(128);
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Stop();
            };
            Thread tick = new Thread(Tick);
            tick.IsBackground = true;
            tick.Start();
            _log.Write("Listening on TCP " + _config.Port + " (" + _ladder.Count + " ladder rows). Ctrl+C stops.");
            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                if (!_slots.WaitOne(0))
                {
                    client.Close();
                    continue;
                }
                ThreadPool.QueueUserWorkItem(Serve, client);
            }
            lock (_lock)
            {
                _ladder.Save();
            }
            _log.Write("Stopped.");
        }

        private void Stop()
        {
            _stopping = true;
            _listener.Stop();
        }

        private void Tick()
        {
            while (!_stopping)
            {
                Thread.Sleep(5000);
                lock (_lock)
                {
                    foreach (Listing gone in _servers.Values.Where(s => Clock.Now - s.LastBeat > _config.ServerTimeout).ToList())
                    {
                        _servers.Remove(gone.Id);
                        _log.Write("Expired " + gone.Describe());
                    }
                    _ladder.SaveIfChanged(10);
                }
                _limiter.Sweep();
            }
        }

        private void Serve(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                Request request;
                Reply reply = Http.Read(client.Client, ip, out request) ?? Route(request);
                Http.Write(client.Client, reply, request != null && request.Method == "GET");
                _log.Write(ip + " " + (request == null ? "-" : request.Method + " " + Check.Clip(request.Path, 80)) + " " + reply.Status);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is SocketException || ex is ObjectDisposedException))
                {
                    _log.Write("Request failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            finally
            {
                client.Close();
                _slots.Release();
            }
        }

        private Reply Route(Request r)
        {
            int wait = _limiter.Take("all", r.Ip, _config.RequestsPerMinute);
            if (wait > 0)
            {
                return Reply.TooMany(wait);
            }
            string[] p = r.Path.Trim('/').Split('/');
            if (p.Length < 2 || p[0] != "v1")
            {
                return Reply.Error(404, "not_found", null);
            }
            string route = p.Length == 4 && p[1] == "servers" ? "servers/*/" + p[3] : p.Length == 2 ? p[1] : string.Empty;
            switch (route)
            {
                case "info": return r.Method == "GET" ? Info() : Reply.NotAllowed();
                case "servers": return r.Method == "GET" ? List(r) : Limited(r, "register", _config.RegistersPerMinute, Register);
                case "servers/*/beat": return r.Method == "POST" ? Beat(r, p[2]) : Reply.NotAllowed();
                case "servers/*/remove": return r.Method == "POST" ? Remove(r, p[2]) : Reply.NotAllowed();
                case "match": return r.Method == "GET" ? Match(r) : Reply.NotAllowed();
                case "results": return r.Method == "POST" ? Limited(r, "results", _config.ResultsPerMinute, Results) : Reply.NotAllowed();
                case "ladder": return r.Method == "GET" ? LadderTable(r) : Reply.NotAllowed();
                default: return Reply.Error(404, "not_found", null);
            }
        }

        private Reply Limited(Request r, string kind, int perMinute, Func<Request, Reply> handle)
        {
            if (r.Method != "POST")
            {
                return Reply.NotAllowed();
            }
            int wait = _limiter.Take(kind, r.Ip, perMinute);
            return wait > 0 ? Reply.TooMany(wait) : handle(r);
        }

        // ---- routes ----

        private Reply Info()
        {
            lock (_lock)
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["api"] = 1;
                body["servers"] = _servers.Count;
                body["players"] = _servers.Values.Sum(s => s.Players);
                body["ladder"] = _ladder.Count;
                return Reply.Ok(body);
            }
        }

        private Reply Register(Request r)
        {
            Fields f = Fields.FromBody(r.Body);
            if (f == null)
            {
                return Reply.Error(400, "bad_json", null);
            }
            Listing s = new Listing();
            s.Ip = r.Ip;
            s.Port = f.Int("port", 1, 65535, 0, true);
            s.Transport = f.OneOf("transport", new[] { "tcp", "steam" }, "tcp");
            s.SteamLobby = f.Digits("steamLobby", 20, s.Transport == "steam");
            string address = f.Host("address");
            s.Lan = f.List("lan", 4, 15).Where(Check.IsLocal).ToArray();
            s.Dedicated = f.Bool("dedicated", false);
            Apply(s, f, true);
            if (f.Bad != null)
            {
                return Reply.Error(400, "bad_field", f.Bad);
            }
            // A registrant on this PC or LAN cannot be reached at its observed address from outside.
            s.Address = !Check.IsLocal(s.Ip) ? s.Ip : address ?? (_config.PublicAddress.Length > 0 ? _config.PublicAddress : s.Ip);
            s.Id = Secret.Hex(6);
            s.Token = Secret.Hex(16);
            s.LastBeat = Clock.Now;
            lock (_lock)
            {
                foreach (Listing old in _servers.Values.Where(o => o.Address == s.Address && o.Port == s.Port && o.Transport == s.Transport && o.SteamLobby == s.SteamLobby).ToList())
                {
                    _servers.Remove(old.Id);
                }
                if (_servers.Count >= _config.MaxServers || _servers.Values.Count(o => o.Ip == s.Ip) >= _config.MaxServersPerIp)
                {
                    return Reply.Error(503, "full", null);
                }
                _servers[s.Id] = s;
            }
            _log.Write("Registered " + s.Describe());
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["id"] = s.Id;
            body["token"] = s.Token;
            body["address"] = s.Address;
            body["port"] = s.Port;
            body["timeout"] = _config.ServerTimeout;
            return Reply.Ok(body);
        }

        // Fields a registration sets and a heartbeat may change.
        private static void Apply(Listing s, Fields f, bool all)
        {
            if (all || f.Has("name"))
            {
                s.Name = f.Text("name", 40, true, "Unnamed server");
            }
            if (all || f.Has("mode"))
            {
                s.Mode = f.OneOf("mode", Modes, "TeamDeadmach");
            }
            if (all || f.Has("map"))
            {
                s.Map = f.OneOf("map", Maps, "random");
            }
            if (all || f.Has("maxPlayers"))
            {
                s.MaxPlayers = f.Int("maxPlayers", 1, 64, 10, false);
            }
            if (all || f.Has("players"))
            {
                s.Players = f.Int("players", 0, 64, 0, false);
            }
            if (all || f.Has("version"))
            {
                s.Version = f.Token("version", 16, "");
            }
            if (all || f.Has("rules"))
            {
                s.Rules = f.Text("rules", 120, true, string.Empty);
            }
            if (all || f.Has("state"))
            {
                s.State = f.OneOf("state", new[] { "lobby", "battle" }, "lobby");
            }
        }

        private Reply Beat(Request r, string id)
        {
            Fields f = Fields.FromBody(r.Body);
            if (f == null)
            {
                return Reply.Error(400, "bad_json", null);
            }
            lock (_lock)
            {
                Reply problem;
                Listing s = Owned(id, f, out problem);
                if (s == null)
                {
                    return problem;
                }
                Listing changed = s.Copy();
                Apply(changed, f, false);
                if (f.Bad != null)
                {
                    return Reply.Error(400, "bad_field", f.Bad);
                }
                changed.LastBeat = Clock.Now;
                _servers[id] = changed;
            }
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["ok"] = true;
            body["timeout"] = _config.ServerTimeout;
            return Reply.Ok(body);
        }

        private Reply Remove(Request r, string id)
        {
            Fields f = Fields.FromBody(r.Body);
            if (f == null)
            {
                return Reply.Error(400, "bad_json", null);
            }
            lock (_lock)
            {
                Reply problem;
                Listing s = Owned(id, f, out problem);
                if (s == null)
                {
                    return problem;
                }
                _servers.Remove(id);
                _log.Write("Removed " + s.Describe());
            }
            return Reply.Ok(new Dictionary<string, object> { { "ok", true } });
        }

        // The listing with this id, if the request carries its token. Under _lock.
        private Listing Owned(string id, Fields f, out Reply problem)
        {
            string token = f.Token("token", 32, null);
            Listing s;
            problem = null;
            if (!Check.IsHex(id, 12) || !_servers.TryGetValue(id, out s))
            {
                problem = Reply.Error(404, "unknown_server", null);
                return null;
            }
            if (token == null || !Check.SameSecret(token, s.Token))
            {
                problem = Reply.Error(403, "bad_token", null);
                return null;
            }
            return s;
        }

        private Reply List(Request r)
        {
            string mode = r.Query("mode");
            string version = r.Query("version");
            bool notFull = r.Query("notFull") == "1";
            lock (_lock)
            {
                List<Listing> found = _servers.Values
                    .Where(s => (mode == null || mode == "any" || s.Mode == mode) && (version == null || s.Version == version) && (!notFull || s.Players < s.MaxPlayers))
                    .OrderByDescending(s => s.Players).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                return Reply.Ok(Servers(found.Take(500), r.Ip, found.Count));
            }
        }

        // Stateless matchmaking: servers with room, best fit first.
        private Reply Match(Request r)
        {
            string mode = r.Query("mode");
            string version = r.Query("version") ?? string.Empty;
            lock (_lock)
            {
                List<Listing> ranked = _servers.Values
                    .Where(s => (mode == null || mode == "any" || s.Mode == mode) && s.Players < s.MaxPlayers)
                    .OrderByDescending(s => s.Version == version)
                    .ThenByDescending(s => s.Players)
                    .ThenByDescending(s => s.Dedicated)
                    .ThenBy(s => s.State == "battle")
                    .ThenByDescending(s => s.LastBeat)
                    .ToList();
                return Reply.Ok(Servers(ranked.Take(10), r.Ip, ranked.Count));
            }
        }

        private Dictionary<string, object> Servers(IEnumerable<Listing> servers, string requester, int total)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["total"] = total;
            body["servers"] = servers.Select(s => (object)s.ToJson(requester)).ToList();
            return body;
        }

        private Reply Results(Request r)
        {
            Fields f = Fields.FromBody(r.Body);
            if (f == null)
            {
                return Reply.Error(400, "bad_json", null);
            }
            string mode = f.OneOf("mode", Modes, null);
            f.OneOf("map", Maps, "random");
            f.Int("seconds", 1, 7200, 0, true);
            int winningTeam = f.Int("winningTeam", -1, 1, 0, true);
            List<string> winners = f.List("winners", 16, 64);
            List<string> losers = f.List("losers", 16, 64);
            if (f.Bad == null && (winners.Count + losers.Count == 0 || winners.Concat(losers).Distinct().Count() != winners.Count + losers.Count))
            {
                f.Bad = "winners";
            }
            if (f.Bad != null)
            {
                return Reply.Error(400, "bad_field", f.Bad);
            }
            List<Ladder.Rating> ratings;
            lock (_lock)
            {
                Reply problem;
                Listing s = Owned(f.Token("id", 12, string.Empty), f, out problem);
                if (s == null)
                {
                    return problem;
                }
                if (!s.Dedicated)
                {
                    return Reply.Error(403, "not_dedicated", null);
                }
                if (Clock.Now - s.LastResult < ResultInterval)
                {
                    return Reply.TooMany(ResultInterval);
                }
                s.LastResult = Clock.Now;
                ratings = _ladder.Record(mode, winners, losers, winningTeam < 0);
                _log.Write("Result from " + s.Describe() + ": " + winners.Count + " vs " + losers.Count + (winningTeam < 0 ? " (draw)" : string.Empty) + (ratings.Count > 0 ? ", rated" : string.Empty));
            }
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["ok"] = true;
            body["rated"] = ratings.Count > 0;
            body["ratings"] = ratings.Select(x => (object)new Dictionary<string, object> { { "name", x.Name }, { "rating", x.Value }, { "delta", x.Delta } }).ToList();
            return Reply.Ok(body);
        }

        private Reply LadderTable(Request r)
        {
            string mode = r.Query("mode") ?? "any";
            int top;
            if (mode != "any" && Array.IndexOf(Modes, mode) < 0)
            {
                return Reply.Error(400, "bad_field", "mode");
            }
            if (!int.TryParse(r.Query("top") ?? "50", NumberStyles.Integer, CultureInfo.InvariantCulture, out top) || top < 1 || top > 100)
            {
                return Reply.Error(400, "bad_field", "top");
            }
            lock (_lock)
            {
                return Reply.Ok(_ladder.Table(mode, top, r.Query("name")));
            }
        }
    }

    // ---- one listed game server ----

    internal sealed class Listing
    {
        public string Id, Token, Ip, Address, Transport, SteamLobby, Name, Mode, Map, Version, Rules, State;
        public string[] Lan;
        public int Port, Players, MaxPlayers;
        public bool Dedicated;
        public double LastBeat, LastResult = -1000;

        public Listing Copy()
        {
            return (Listing)MemberwiseClone();
        }

        public string Describe()
        {
            return Id + " \"" + Name + "\" " + Address + ":" + Port + " (" + Transport + (Dedicated ? ", dedicated" : string.Empty) + ")";
        }

        public Dictionary<string, object> ToJson(string requester)
        {
            // Players on the master's own LAN reach a LAN-registered server at its LAN address;
            // players behind the server's own router get its LAN addresses (no hairpin NAT needed).
            bool sameLan = Check.IsLocal(Ip) && Check.IsLocal(requester);
            bool sameNetwork = requester == Address;
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["id"] = Id;
            o["name"] = Name;
            o["address"] = sameLan ? Ip : Address;
            o["port"] = Port;
            o["transport"] = Transport;
            o["steamLobby"] = SteamLobby ?? string.Empty;
            o["lan"] = sameNetwork ? Lan.Cast<object>().ToList() : new List<object>();
            o["mode"] = Mode;
            o["map"] = Map;
            o["players"] = Players;
            o["maxPlayers"] = MaxPlayers;
            o["version"] = Version;
            o["rules"] = Rules;
            o["dedicated"] = Dedicated;
            o["state"] = State;
            o["age"] = (int)(Clock.Now - LastBeat);
            return o;
        }
    }

    // ---- ladder: team Elo per full player name, per mode and overall ("any") ----

    internal sealed class Ladder
    {
        public sealed class Rating
        {
            public string Name;
            public double Value, Delta;
        }

        private sealed class Row
        {
            public string Mode, Name;
            public double Rating = 1000;
            public int Matches, Wins, Losses, Draws;
            public long Last;
        }

        private const int MaxRows = 200000;
        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        private readonly string _path;
        private readonly Log _log;
        private bool _changed;
        private double _savedAt;

        public Ladder(string path, Log log)
        {
            _path = path;
            _log = log;
            if (!File.Exists(path))
            {
                return;
            }
            foreach (string line in File.ReadAllLines(path))
            {
                string[] p = line.Split('\t');
                if (line.StartsWith("#") || p.Length != 8)
                {
                    continue;
                }
                Row row = new Row { Mode = p[0], Name = p[1] };
                double rating;
                if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rating))
                {
                    continue;
                }
                row.Rating = rating;
                int.TryParse(p[3], out row.Matches);
                int.TryParse(p[4], out row.Wins);
                int.TryParse(p[5], out row.Losses);
                int.TryParse(p[6], out row.Draws);
                long.TryParse(p[7], out row.Last);
                _rows[row.Mode + "\t" + row.Name] = row;
            }
        }

        public int Count
        {
            get { return _rows.Count; }
        }

        // Rated only when both sides have players; otherwise only counted.
        public List<Rating> Record(string mode, List<string> winners, List<string> losers, bool draw)
        {
            List<Rating> overall = new List<Rating>();
            foreach (string m in new[] { "any", mode })
            {
                List<Row> a = winners.Select(n => Get(m, n)).Where(x => x != null).ToList();
                List<Row> b = losers.Select(n => Get(m, n)).Where(x => x != null).ToList();
                List<Rating> rated = a.Count > 0 && b.Count > 0 ? Rate(a, b, draw) : new List<Rating>();
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                foreach (Row row in a.Concat(b))
                {
                    row.Matches++;
                    row.Last = now;
                }
                a.ForEach(x => { if (draw) { x.Draws++; } else { x.Wins++; } });
                b.ForEach(x => { if (draw) { x.Draws++; } else { x.Losses++; } });
                if (m == "any")
                {
                    overall = rated;
                }
            }
            _changed = true;
            return overall;
        }

        private static List<Rating> Rate(List<Row> a, List<Row> b, bool draw)
        {
            double expectA = 1.0 / (1.0 + Math.Pow(10.0, (b.Average(x => x.Rating) - a.Average(x => x.Rating)) / 400.0));
            double scoreA = draw ? 0.5 : 1.0;
            List<Rating> result = new List<Rating>();
            foreach (Row row in a.Concat(b))
            {
                bool inA = a.Contains(row);
                double delta = Math.Round((row.Matches < 10 ? 40 : 24) * (inA ? scoreA - expectA : expectA - scoreA), 1);
                row.Rating = Math.Round(row.Rating + delta, 1);
                result.Add(new Rating { Name = row.Name, Value = row.Rating, Delta = delta });
            }
            return result;
        }

        private Row Get(string mode, string name)
        {
            Row row;
            string key = mode + "\t" + name;
            if (!_rows.TryGetValue(key, out row) && _rows.Count < MaxRows)
            {
                row = new Row { Mode = mode, Name = name };
                _rows[key] = row;
            }
            return row;
        }

        public Dictionary<string, object> Table(string mode, int top, string name)
        {
            List<Row> sorted = _rows.Values.Where(r => r.Mode == mode).OrderByDescending(r => r.Rating).ThenByDescending(r => r.Matches).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["mode"] = mode;
            body["total"] = sorted.Count;
            body["rows"] = sorted.Take(top).Select((r, i) => (object)Json(r, i + 1)).ToList();
            int you = name == null ? -1 : sorted.FindIndex(r => r.Name == name);
            body["you"] = you < 0 ? null : Json(sorted[you], you + 1);
            return body;
        }

        private static Dictionary<string, object> Json(Row r, int rank)
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["rank"] = rank;
            o["name"] = r.Name;
            o["rating"] = r.Rating;
            o["matches"] = r.Matches;
            o["wins"] = r.Wins;
            o["losses"] = r.Losses;
            o["draws"] = r.Draws;
            o["last"] = r.Last;
            return o;
        }

        public void SaveIfChanged(double interval)
        {
            if (_changed && Clock.Now - _savedAt >= interval)
            {
                Save();
            }
        }

        public void Save()
        {
            if (!_changed)
            {
                return;
            }
            try
            {
                string temp = _path + ".tmp";
                File.WriteAllLines(temp, new[] { "# mode\tname\trating\tmatches\twins\tlosses\tdraws\tlast" }.Concat(_rows.Values.Select(r => string.Join("\t", new[]
                {
                    r.Mode, r.Name, r.Rating.ToString("0.0", CultureInfo.InvariantCulture), r.Matches.ToString(CultureInfo.InvariantCulture),
                    r.Wins.ToString(CultureInfo.InvariantCulture), r.Losses.ToString(CultureInfo.InvariantCulture),
                    r.Draws.ToString(CultureInfo.InvariantCulture), r.Last.ToString(CultureInfo.InvariantCulture),
                }))).ToArray());
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                File.Move(temp, _path);
                _changed = false;
                _savedAt = Clock.Now;
            }
            catch (Exception ex)
            {
                _log.Write("Could not save the ladder: " + ex.Message);
            }
        }
    }

    // ---- HTTP ----

    internal sealed class Request
    {
        public string Method, Path, Ip, Body;
        public Dictionary<string, string> Params = new Dictionary<string, string>();

        public string Query(string key)
        {
            string value;
            return Params.TryGetValue(key, out value) && value.Length > 0 ? value : null;
        }
    }

    internal sealed class Reply
    {
        public int Status;
        public object Body;
        public int RetryAfter;

        public static Reply Ok(object body)
        {
            return new Reply { Status = 200, Body = body };
        }

        public static Reply Error(int status, string code, string field)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["error"] = code;
            if (field != null)
            {
                body["field"] = field;
            }
            return new Reply { Status = status, Body = body };
        }

        public static Reply TooMany(int seconds)
        {
            Reply r = Error(429, "rate_limited", null);
            r.RetryAfter = seconds;
            return r;
        }

        public static Reply NotAllowed()
        {
            return Error(405, "method_not_allowed", null);
        }
    }

    internal static class Http
    {
        private const int MaxHead = 8192;
        private const int MaxBody = 8192;
        private const int Deadline = 5000;

        // Reads one request; a non-null Reply is the error to answer with instead.
        public static Reply Read(Socket socket, string ip, out Request request)
        {
            request = null;
            Stopwatch watch = Stopwatch.StartNew();
            MemoryStream buffer = new MemoryStream();
            int headEnd = -1;
            while (headEnd < 0)
            {
                if (buffer.Length > MaxHead)
                {
                    return Reply.Error(431, "headers_too_large", null);
                }
                if (!Receive(socket, buffer, watch))
                {
                    return Reply.Error(408, "timeout", null);
                }
                headEnd = Find(buffer.GetBuffer(), (int)buffer.Length);
            }
            byte[] bytes = buffer.ToArray();
            string[] lines = Encoding.ASCII.GetString(bytes, 0, headEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.") || first[1].Length > 2048 || !first[1].StartsWith("/"))
            {
                return Reply.Error(400, "bad_request", null);
            }
            Request r = new Request { Method = first[0], Ip = ip, Body = string.Empty };
            request = r;
            if (r.Method != "GET" && r.Method != "POST")
            {
                return Reply.NotAllowed();
            }
            if (!ParseTarget(first[1], r))
            {
                return Reply.Error(400, "bad_query", null);
            }
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || headers.Count >= 32)
                {
                    return Reply.Error(400, "bad_header", null);
                }
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            if (headers.ContainsKey("Transfer-Encoding"))
            {
                return Reply.Error(411, "length_required", null);
            }
            string lengthText;
            int length = 0;
            if (headers.TryGetValue("Content-Length", out lengthText) && (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length)))
            {
                return Reply.Error(400, "bad_length", null);
            }
            if (r.Method == "POST" && lengthText == null)
            {
                return Reply.Error(411, "length_required", null);
            }
            if (length > MaxBody)
            {
                return Reply.Error(413, "too_large", null);
            }
            string expect;
            if (length > 0 && headers.TryGetValue("Expect", out expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                Send(socket, "HTTP/1.1 100 Continue\r\n\r\n");
            }
            int bodyStart = headEnd + 4;
            buffer = new MemoryStream();
            buffer.Write(bytes, bodyStart, bytes.Length - bodyStart);
            while (buffer.Length < length)
            {
                if (!Receive(socket, buffer, watch))
                {
                    return Reply.Error(408, "timeout", null);
                }
            }
            if (buffer.Length > length)
            {
                return Reply.Error(400, "bad_length", null);
            }
            try
            {
                r.Body = new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
            catch (ArgumentException)
            {
                return Reply.Error(400, "bad_utf8", null);
            }
            return null;
        }

        private static bool Receive(Socket socket, MemoryStream into, Stopwatch watch)
        {
            int left = Deadline - (int)watch.ElapsedMilliseconds;
            if (left <= 0)
            {
                return false;
            }
            socket.ReceiveTimeout = left;
            byte[] chunk = new byte[4096];
            int n;
            try
            {
                n = socket.Receive(chunk);
            }
            catch (SocketException)
            {
                return false;
            }
            into.Write(chunk, 0, n);
            return n > 0;
        }

        private static int Find(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool ParseTarget(string target, Request r)
        {
            int q = target.IndexOf('?');
            r.Path = q < 0 ? target : target.Substring(0, q);
            if (q < 0)
            {
                return true;
            }
            string[] pairs = target.Substring(q + 1).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length > 16)
            {
                return false;
            }
            try
            {
                foreach (string pair in pairs)
                {
                    int eq = pair.IndexOf('=');
                    string key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                    string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                    r.Params[key] = Check.Clean(value, 64);
                }
            }
            catch (UriFormatException)
            {
                return false;
            }
            return true;
        }

        public static void Write(Socket socket, Reply reply, bool cors)
        {
            byte[] body = Encoding.UTF8.GetBytes(Json.Write(reply.Body));
            StringBuilder head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(reply.Status).Append(' ').Append(Reason(reply.Status)).Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            if (reply.RetryAfter > 0)
            {
                head.Append("Retry-After: ").Append(reply.RetryAfter).Append("\r\n");
            }
            if (cors)
            {
                head.Append("Access-Control-Allow-Origin: *\r\n");
            }
            head.Append("\r\n");
            socket.SendTimeout = Deadline;
            Send(socket, head.ToString());
            socket.Send(body);
            socket.Shutdown(SocketShutdown.Send);
        }

        private static void Send(Socket socket, string text)
        {
            socket.Send(Encoding.ASCII.GetBytes(text));
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 429: return "Too Many Requests";
                case 431: return "Request Header Fields Too Large";
                default: return "Service Unavailable";
            }
        }
    }

    // ---- validation ----

    // Typed access to a JSON object. A missing optional field gives the
    // fallback; a wrong one records its name in Bad and gives the fallback.
    internal sealed class Fields
    {
        private readonly Dictionary<string, object> _d;
        public string Bad;

        private Fields(Dictionary<string, object> d)
        {
            _d = d;
        }

        public static Fields FromBody(string body)
        {
            Dictionary<string, object> d = Json.Parse(body) as Dictionary<string, object>;
            return d == null ? null : new Fields(d);
        }

        public bool Has(string key)
        {
            return _d.ContainsKey(key);
        }

        private T Fail<T>(string key, T fallback)
        {
            if (Bad == null)
            {
                Bad = key;
            }
            return fallback;
        }

        private object Raw(string key, bool required)
        {
            object value;
            if (_d.TryGetValue(key, out value) && value != null)
            {
                return value;
            }
            if (required)
            {
                Fail(key, 0);
            }
            return null;
        }

        public int Int(string key, int min, int max, int fallback, bool required)
        {
            object raw = Raw(key, required);
            if (raw == null)
            {
                return fallback;
            }
            double n = raw is double ? (double)raw : double.NaN;
            return n >= min && n <= max && n == Math.Floor(n) ? (int)n : Fail(key, fallback);
        }

        public bool Bool(string key, bool fallback)
        {
            object raw = Raw(key, false);
            return raw == null ? fallback : raw is bool ? (bool)raw : Fail(key, fallback);
        }

        // Free text: control characters removed; clipped, or refused when too long.
        public string Text(string key, int max, bool clip, string fallback)
        {
            object raw = Raw(key, false);
            if (raw == null)
            {
                return fallback;
            }
            string s = raw as string;
            if (s == null)
            {
                return Fail(key, fallback);
            }
            s = Check.Clean(s, int.MaxValue);
            if (s.Length > max && !clip)
            {
                return Fail(key, fallback);
            }
            s = Check.Clip(s, max);
            return s.Length == 0 ? fallback : s;
        }

        public string OneOf(string key, string[] allowed, string fallback)
        {
            object raw = Raw(key, fallback == null);
            if (raw == null)
            {
                return fallback;
            }
            return Array.IndexOf(allowed, raw as string) >= 0 ? (string)raw : Fail(key, fallback);
        }

        // Letters, digits, dot, dash, underscore.
        public string Token(string key, int max, string fallback)
        {
            object raw = Raw(key, false);
            if (raw == null)
            {
                return fallback;
            }
            string s = raw as string;
            return s != null && s.Length <= max && s.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) ? s : Fail(key, fallback);
        }

        public string Digits(string key, int max, bool required)
        {
            object raw = Raw(key, required);
            string s = raw as string;
            if (raw == null)
            {
                return null;
            }
            return s != null && s.Length > 0 && s.Length <= max && s.All(c => c >= '0' && c <= '9') ? s : Fail<string>(key, null);
        }

        public string Host(string key)
        {
            object raw = Raw(key, false);
            if (raw == null || (raw as string) == string.Empty)
            {
                return null;
            }
            return Check.IsHost(raw as string) ? (string)raw : Fail<string>(key, null);
        }

        // A list of names: each 1..maxLength characters after cleaning.
        public List<string> List(string key, int maxCount, int maxLength)
        {
            object raw = Raw(key, false);
            List<object> items = raw as List<object>;
            if (raw == null)
            {
                return new List<string>();
            }
            if (items == null || items.Count > maxCount)
            {
                return Fail(key, new List<string>());
            }
            List<string> names = items.Select(i => i is string ? Check.Clean((string)i, int.MaxValue) : string.Empty).ToList();
            return names.All(n => n.Length > 0 && n.Length <= maxLength) ? names : Fail(key, new List<string>());
        }
    }

    internal static class Check
    {
        public static string Clean(string s, int max)
        {
            return Clip(new string(s.Where(c => c >= 0x20 && c != 0x7f && !char.IsControl(c)).ToArray()).Trim(), max);
        }

        public static string Clip(string s, int max)
        {
            return s.Length > max ? s.Substring(0, max) : s;
        }

        public static bool IsHex(string s, int length)
        {
            return s != null && s.Length == length && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }

        public static bool IsHost(string s)
        {
            return s != null && s.Length > 0 && s.Length <= 253 && s.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '-')) && !s.StartsWith("-") && !s.StartsWith(".");
        }

        // Loopback, private, link-local and shared-address (CGNAT) IPv4.
        public static bool IsLocal(string address)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip))
            {
                return false;
            }
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }
            byte[] b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] < 128));
        }

        public static bool SameSecret(string a, string b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }
    }

    // ---- JSON ----

    internal static class Json
    {
        private const int MaxDepth = 10;

        // Objects become Dictionary<string, object>, arrays List<object>, numbers double.
        // Anything malformed gives null.
        public static object Parse(string text)
        {
            try
            {
                int i = 0;
                object value = Value(text, ref i, 0);
                Space(text, ref i);
                return i == text.Length ? value : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static object Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("too deep");
            }
            Space(s, ref i);
            if (i >= s.Length)
            {
                throw new FormatException("end");
            }
            char c = s[i];
            if (c == '{')
            {
                return ParseObject(s, ref i, depth);
            }
            if (c == '[')
            {
                return ParseArray(s, ref i, depth);
            }
            if (c == '"')
            {
                return ParseString(s, ref i);
            }
            if (Word(s, ref i, "true"))
            {
                return true;
            }
            if (Word(s, ref i, "false"))
            {
                return false;
            }
            if (Word(s, ref i, "null"))
            {
                return null;
            }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++;
            Space(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return d;
            }
            while (true)
            {
                Space(s, ref i);
                if (i >= s.Length || s[i] != '"')
                {
                    throw new FormatException("key");
                }
                string key = ParseString(s, ref i);
                Space(s, ref i);
                Expect(s, ref i, ':');
                d[key] = Value(s, ref i, depth + 1);
                Space(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                Expect(s, ref i, '}');
                return d;
            }
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            List<object> list = new List<object>();
            i++;
            Space(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return list;
            }
            while (true)
            {
                list.Add(Value(s, ref i, depth + 1));
                Space(s, ref i);
                if (i < s.Length && s[i] == ',')
                {
                    i++;
                    continue;
                }
                Expect(s, ref i, ']');
                return list;
            }
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder b = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                {
                    return b.ToString();
                }
                if (c < 0x20)
                {
                    throw new FormatException("control character");
                }
                if (c != '\\')
                {
                    b.Append(c);
                    continue;
                }
                if (i >= s.Length)
                {
                    break;
                }
                char e = s[i++];
                switch (e)
                {
                    case '"': case '\\': case '/': b.Append(e); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        int code;
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                        {
                            throw new FormatException("escape");
                        }
                        b.Append((char)code);
                        i += 4;
                        break;
                    default: throw new FormatException("escape");
                }
            }
            throw new FormatException("unterminated string");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
            {
                i++;
            }
            double n;
            if (i == start || i - start > 32 || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out n) || double.IsInfinity(n))
            {
                throw new FormatException("number");
            }
            return n;
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            {
                return false;
            }
            i += word.Length;
            return true;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c)
            {
                throw new FormatException("expected " + c);
            }
            i++;
        }

        private static void Space(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
            {
                i++;
            }
        }

        public static string Write(object value)
        {
            StringBuilder b = new StringBuilder();
            Write(b, value);
            return b.ToString();
        }

        private static void Write(StringBuilder b, object value)
        {
            if (value == null)
            {
                b.Append("null");
            }
            else if (value is string)
            {
                Quote(b, (string)value);
            }
            else if (value is bool)
            {
                b.Append((bool)value ? "true" : "false");
            }
            else if (value is int || value is long)
            {
                b.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            else if (value is double)
            {
                b.Append(((double)value).ToString("0.###", CultureInfo.InvariantCulture));
            }
            else if (value is Dictionary<string, object>)
            {
                b.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in (Dictionary<string, object>)value)
                {
                    b.Append(first ? string.Empty : ",");
                    first = false;
                    Quote(b, pair.Key);
                    b.Append(':');
                    Write(b, pair.Value);
                }
                b.Append('}');
            }
            else
            {
                b.Append('[');
                bool first = true;
                foreach (object item in (System.Collections.IEnumerable)value)
                {
                    b.Append(first ? string.Empty : ",");
                    first = false;
                    Write(b, item);
                }
                b.Append(']');
            }
        }

        private static void Quote(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\')
                {
                    b.Append('\\').Append(c);
                }
                else if (c < 0x20 || c == '<' || c == '>' || c == '&' || c == 0x2028 || c == 0x2029)
                {
                    b.Append("\\u").Append(((int)c).ToString("x4"));
                }
                else
                {
                    b.Append(c);
                }
            }
            b.Append('"');
        }
    }

    // ---- small helpers ----

    internal sealed class RateLimiter
    {
        private sealed class Bucket
        {
            public double Tokens, At, PerMinute;
        }

        private readonly Dictionary<string, Bucket> _buckets = new Dictionary<string, Bucket>();

        // 0 when allowed, otherwise the seconds to wait.
        public int Take(string kind, string ip, int perMinute)
        {
            lock (_buckets)
            {
                Bucket b;
                string key = kind + "|" + ip;
                if (!_buckets.TryGetValue(key, out b))
                {
                    b = new Bucket { Tokens = perMinute, At = Clock.Now, PerMinute = perMinute };
                    _buckets[key] = b;
                }
                b.Tokens = Math.Min(perMinute, b.Tokens + (Clock.Now - b.At) * perMinute / 60.0);
                b.At = Clock.Now;
                if (b.Tokens >= 1)
                {
                    b.Tokens -= 1;
                    return 0;
                }
                return (int)Math.Ceiling((1 - b.Tokens) * 60.0 / perMinute);
            }
        }

        // Full buckets carry no state worth keeping.
        public void Sweep()
        {
            lock (_buckets)
            {
                foreach (string key in _buckets.Where(p => p.Value.Tokens + (Clock.Now - p.Value.At) * p.Value.PerMinute / 60.0 >= p.Value.PerMinute).Select(p => p.Key).ToList())
                {
                    _buckets.Remove(key);
                }
            }
        }
    }

    internal static class Clock
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        public static double Now
        {
            get { return Watch.Elapsed.TotalSeconds; }
        }
    }

    internal static class Secret
    {
        private static readonly RandomNumberGenerator Source = RandomNumberGenerator.Create();

        public static string Hex(int bytes)
        {
            byte[] data = new byte[bytes];
            lock (Source)
            {
                Source.GetBytes(data);
            }
            return string.Concat(data.Select(x => x.ToString("x2")).ToArray());
        }
    }

    internal sealed class Log
    {
        private const long MaxSize = 5 * 1024 * 1024;
        private readonly string _path;

        public Log(string path)
        {
            _path = path;
        }

        public void Write(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + text;
            lock (this)
            {
                Console.WriteLine(line);
                try
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxSize)
                    {
                        File.Copy(_path, _path + ".old", true);
                        File.Delete(_path);
                    }
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
