using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using UnityEngine;

namespace GarRevival;

// Stats > Ladder: XML served at /garphpapi/ladder/index.php, read by the client into LadderDataHelper.LadderData.
// The own league lists me and my session's players; the Master tab shows the master server's ladder when configured.
internal static class LadderTable
{
    private const string Unranked = "LD_003";
    private const string Broken = "LD_000";
    private const short MasterLeague = 4;
    private const int MaxRows = 100;
    private const float MasterWait = 4f;   // the garage UI stays blocked until the table arrives
    private const float MasterRetry = 60f; // after a failed fetch, answer locally for this long

    private sealed class Standing
    {
        public double Rating, Change;
        public int Battles;
        public string Profile;
    }

    private sealed class Row
    {
        public int Position, Move, Portrait, Level;
        public string Name, Guild;
        public double Points;
    }

    // One table request; it may wait for the master server.
    internal sealed class Job
    {
        public string Mode, Local, Master;
        public int LocalRows, MasterRows;
        public bool Waiting;
        public float Deadline;

        public bool Ready => !Waiting || Time.realtimeSinceStartup >= Deadline;

        public string Finish()
        {
            bool master = Master != null;
            if (Waiting)
            {
                _masterFailedAt = Time.realtimeSinceStartup;
                RevivalLog.Info($"Ladder table: no answer from the master within {MasterWait} s");
            }
            RevivalLog.Info($"Ladder table: {Mode} rows={(master ? MasterRows : LocalRows)} source={(master ? "master" : "local")}");
            return master ? Master : Local;
        }
    }

    private static readonly Dictionary<string, Standing> Players = new();
    private static float _masterFailedAt = -MasterRetry;

    // Server profiles have their own ratings: only standings from the same profile are compared.
    public static string Profile => PlayerState.ServerProfile ?? string.Empty;

    public static void Note(string fullName, double rating, int battles, double change, string profile)
    {
        if (string.IsNullOrEmpty(fullName) || fullName == Session.LocalName || double.IsNaN(rating) || double.IsInfinity(rating)) return;
        Players[fullName] = new Standing { Rating = Math.Max(0, rating), Battles = Math.Max(0, battles), Change = change, Profile = profile ?? string.Empty };
    }

    public static void Forget(string fullName) => Players.Remove(fullName);

    public static Job Begin(Dictionary<string, string> query)
    {
        bool masterTab = query.ContainsKey("leagueinstance") || !query.ContainsKey("name");
        var job = new Job { Mode = masterTab ? "master" : "own" };
        try
        {
            int rows = query.TryGetValue("rowsperpage", out string asked) && int.TryParse(asked, out int n) ? Mathf.Clamp(n, 1, MaxRows) : MaxRows;
            var me = MyRow();
            short league = LadderOps.League(PlayerState.Current);
            short shown = masterTab ? MasterLeague : league;
            if (!masterTab && league < 0)
            {
                job.Local = Xml(Unranked, -1, 0, null, null);
                return job;
            }
            var local = LocalRows(shown, me).Take(rows).ToList();
            job.LocalRows = local.Count;
            job.Local = Xml(string.Empty, shown, rows, me, local);
            // A Master league player has no Master tab: the own league shows the master's ladder.
            if (shown == MasterLeague && MasterClient.Configured && Time.realtimeSinceStartup - _masterFailedAt >= MasterRetry)
            {
                FetchMaster(job, shown, rows, me);
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Ladder table failed", ex);
            job.Local = Xml(Broken, -1, 0, null, null, true);
            job.Waiting = false;
        }
        return job;
    }

    private static Row MyRow()
    {
        var player = PlayerState.Current;
        if (LadderOps.League(player) < 0) return null;
        var card = SocialOps.MyCard();
        return new Row
        {
            Name = player.fullName, Level = card.Level, Portrait = card.Portrait, Guild = card.Guild,
            Points = player.ladderRating, Position = LadderOps.Position(player), Move = Math.Sign(LadderOps.LastChange),
        };
    }

    // Me and the session's players placed in that league, best first.
    private static IEnumerable<Row> LocalRows(short league, Row me)
    {
        string profile = Profile;
        var others = Players
            .Where(p => p.Value.Profile == profile && LadderOps.League(p.Value.Rating, p.Value.Battles) == league)
            .Select(p => OtherRow(p.Key, p.Value));
        Row[] mine = me != null && LadderOps.League(PlayerState.Current) == league ? [me] : [];
        return mine.Concat(others).OrderByDescending(r => r.Points).ThenBy(r => r.Name, StringComparer.Ordinal);
    }

    private static Row OtherRow(string fullName, Standing s)
    {
        var card = SocialOps.CardOf(fullName);
        return new Row
        {
            Name = fullName, Level = card.Level, Portrait = card.Portrait, Guild = card.Guild,
            Points = s.Rating, Position = LadderOps.Position(s.Rating, s.Battles), Move = Math.Sign(s.Change),
        };
    }

    private static void FetchMaster(Job job, short league, int rows, Row me)
    {
        job.Waiting = true;
        job.Deadline = Time.realtimeSinceStartup + MasterWait;
        string url = Settings.Masters[0];
        MasterHttp.Get(url + "/v1/ladder?mode=any&top=" + rows, r =>
        {
            job.Waiting = false;
            if (!r.Ok)
            {
                _masterFailedAt = Time.realtimeSinceStartup;
                RevivalLog.Info($"Ladder table: master {url} did not answer ({r.Error}), showing the local table");
                return;
            }
            var table = r.Json.List("rows").OfType<Dictionary<string, object>>().Select(MasterRow).Take(rows).ToList();
            job.MasterRows = table.Count;
            job.Master = Xml(string.Empty, league, rows, me, table);
        });
    }

    // The master knows names and ratings; level, portrait and tag come from players I have met.
    private static Row MasterRow(Dictionary<string, object> o)
    {
        string name = o.Str("name");
        bool mine = name == Session.LocalName;
        var card = mine ? SocialOps.MyCard() : SocialOps.CardOf(name);
        bool known = mine || Session.InSession(name);
        return new Row
        {
            Name = name, Position = o.Int("rank"), Points = o.Num("rating"),
            Level = known ? card.Level : 1, Portrait = known ? card.Portrait : DataFactory.PortraitIndices.FirstOrDefault(), Guild = known ? card.Guild : string.Empty,
        };
    }

    // The client drops the first character before parsing, hence the leading newline.
    // An empty <a> crashes the client (it reads a[0]), so empty lists are left out.
    private static string Xml(string message, short league, int rows, Row me, IList<Row> table, bool failed = false)
    {
        var x = new StringBuilder("\n<LadderData>");
        Element(x, "m", message);
        Element(x, "n", Mathf.Clamp(league, -1, MasterLeague).ToString(CultureInfo.InvariantCulture));
        Element(x, "li", "-1");
        Element(x, "s", "0");
        Element(x, "c", (table == null ? 0 : table.Count).ToString(CultureInfo.InvariantCulture));
        Element(x, "p", "0");
        Element(x, "r", rows.ToString(CultureInfo.InvariantCulture));
        Element(x, "e", "0");
        Element(x, "f", failed ? "1" : "0");
        if (me != null)
        {
            x.Append("<a>");
            Info(x, me, true);
            x.Append("</a>");
        }
        if (table != null && table.Count > 0)
        {
            x.Append("<b>");
            foreach (var row in table)
            {
                Info(x, row, false);
            }
            x.Append("</b>");
        }
        return x.Append("</LadderData>").ToString();
    }

    private static void Info(StringBuilder x, Row row, bool own)
    {
        x.Append("<LadderInfo>");
        Element(x, "p", Mathf.Clamp(row.Position, 1, own ? 100 : 99999).ToString(CultureInfo.InvariantCulture));
        Element(x, "d", Math.Sign(row.Move).ToString(CultureInfo.InvariantCulture));
        Element(x, "i", SocialOps.SafePortrait(row.Portrait).ToString(CultureInfo.InvariantCulture));
        Element(x, "f", NameText(row.Name));
        // 0 points reads as "not ranked" on my own row.
        Element(x, "r", (own ? Math.Max(0.01, row.Points) : Math.Max(0, row.Points)).ToString("0.##", CultureInfo.InvariantCulture));
        Element(x, "l", Mathf.Clamp(row.Level, 1, 999).ToString(CultureInfo.InvariantCulture));
        Element(x, "ga", Plain(row.Guild));
        x.Append("</LadderInfo>");
    }

    // The client cuts the last 5 characters (".1a2b") off a full name.
    private static string NameText(string fullName)
    {
        string name = Plain(fullName);
        return name.Length > 5 ? name : name + ".----";
    }

    // Names are shown inside NGUI markup, so brackets would restyle the table.
    private static string Plain(string text) =>
        new((text ?? string.Empty).Where(c => c >= ' ' && c != '[' && c != ']').Take(64).ToArray());

    private static void Element(StringBuilder x, string name, string value) =>
        x.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
}
