using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JunkWars.Common.Guild.ValueObjects;
using JunkWars.Common.Social;
using JunkWars.Common.Utils;
using JunkWars.Common.Utils.Enums;

namespace GarRevival;

// Social state PlayerVO has no field for (pending invitations, owed notices, my copy of my guild),
// saved next to the profile as social.dat. The field order is the file format.
internal static class SocialStore
{
    private const int FormatVersion = 1;

    public sealed class Notice
    {
        public string Target;
        public byte Kind; // a SocialOps message kind
    }

    public static readonly List<SocialMessageData> Inbox = [];
    public static readonly List<string> SentFriendInvites = [];
    public static readonly List<string> SentGuildInvites = [];
    public static readonly List<Notice> Outbox = [];
    public static readonly List<string> DisbandedGuilds = [];

    // My copy of my guild: the roster, the revision it last changed at, and what each member earned for it.
    public static GuildVO Guild;
    public static long GuildRevision;
    public static readonly Dictionary<string, int> GuildExperience = new();
    // What each member spent from the vault on guild benefits (credits, sparks).
    public static readonly List<GuildDonatorVO> GuildBuffSpending = [];
    public static string PendingGuildJoin = string.Empty;

    // The guild as I left it, for members who were away when I left.
    public static string FarewellGuildId = string.Empty;
    public static long FarewellRevision;
    public static byte[] FarewellState = [];

    private static bool _loaded;

    private static string SavePath => Path.Combine(Path.Combine(RevivalLog.Folder, "save" + Instance.Suffix), "social.dat");

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(SavePath)) return;
        try
        {
            Read(new BinaryReader(new MemoryStream(File.ReadAllBytes(SavePath))));
            RevivalLog.Info($"Loaded social data: {Inbox.Count} invitations, guild {(Guild == null ? "none" : Guild.name)}");
        }
        catch (Exception ex)
        {
            string broken = SavePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(SavePath, broken);
            RevivalLog.Error($"Social data could not be read; moved to {broken} and starting empty", ex);
            Reset();
        }
    }

    private static void Reset()
    {
        Inbox.Clear();
        SentFriendInvites.Clear();
        SentGuildInvites.Clear();
        Outbox.Clear();
        DisbandedGuilds.Clear();
        Guild = null;
        GuildRevision = 0;
        GuildExperience.Clear();
        GuildBuffSpending.Clear();
        PendingGuildJoin = string.Empty;
        FarewellGuildId = string.Empty;
        FarewellRevision = 0;
        FarewellState = [];
    }

    private static void Read(BinaryReader r)
    {
        if (r.ReadInt32() != FormatVersion) throw new InvalidDataException("unknown social.dat version");
        for (int i = r.ReadInt32(); i > 0; i--)
        {
            Inbox.Add(new SocialMessageData
            {
                sender = r.ReadString(),
                receiver = r.ReadString(),
                type = (EventMessageType)r.ReadByte(),
                message = r.ReadString(),
                date = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
            });
        }
        ReadNames(r, SentFriendInvites);
        ReadNames(r, SentGuildInvites);
        for (int i = r.ReadInt32(); i > 0; i--)
        {
            Outbox.Add(new Notice { Target = r.ReadString(), Kind = r.ReadByte() });
        }
        ReadNames(r, DisbandedGuilds);
        var guild = r.ReadBytes(r.ReadInt32());
        Guild = guild.Length == 0 ? null : GuildFromBytes(guild);
        GuildRevision = r.ReadInt64();
        for (int i = r.ReadInt32(); i > 0; i--)
        {
            GuildExperience[r.ReadString()] = r.ReadInt32();
        }
        PendingGuildJoin = r.ReadString();
        FarewellGuildId = r.ReadString();
        FarewellRevision = r.ReadInt64();
        FarewellState = r.ReadBytes(r.ReadInt32());
        // Optional tail: older files end here and older builds stop reading here.
        if (r.BaseStream.Position < r.BaseStream.Length)
        {
            GuildBuffSpending.AddRange(ReadTallies(r));
        }
    }

    public static void Save()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(FormatVersion);
        w.Write(Inbox.Count);
        foreach (var message in Inbox)
        {
            w.Write(message.sender ?? string.Empty);
            w.Write(message.receiver ?? string.Empty);
            w.Write((byte)message.type);
            w.Write(message.message ?? string.Empty);
            w.Write(message.date.Ticks);
        }
        WriteNames(w, SentFriendInvites);
        WriteNames(w, SentGuildInvites);
        w.Write(Outbox.Count);
        foreach (var notice in Outbox)
        {
            w.Write(notice.Target);
            w.Write(notice.Kind);
        }
        WriteNames(w, DisbandedGuilds);
        byte[] guild = Guild == null ? [] : GuildToBytes(Guild);
        w.Write(guild.Length);
        w.Write(guild);
        w.Write(GuildRevision);
        w.Write(GuildExperience.Count);
        foreach (var earned in GuildExperience)
        {
            w.Write(earned.Key);
            w.Write(earned.Value);
        }
        w.Write(PendingGuildJoin);
        w.Write(FarewellGuildId);
        w.Write(FarewellRevision);
        w.Write(FarewellState.Length);
        w.Write(FarewellState);
        WriteTallies(w, GuildBuffSpending);
        w.Flush();

        Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
        string temp = SavePath + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        if (File.Exists(SavePath))
        {
            File.Delete(SavePath);
        }
        File.Move(temp, SavePath);
    }

    public static byte[] GuildToBytes(GuildVO guild) => DataSerialization.ProtoSerializeObject(guild);

    public static GuildVO GuildFromBytes(byte[] bytes)
    {
        var guild = DataSerialization.ProtoDeserializeObject<GuildVO>(bytes);
        VoNormalizer.FillNullCollections(guild);
        return guild;
    }

    // Per member: name, credits, sparks.
    public static void WriteTallies(BinaryWriter w, List<GuildDonatorVO> tallies)
    {
        w.Write(tallies.Count);
        foreach (var tally in tallies)
        {
            w.Write(tally.name ?? string.Empty);
            w.Write(tally.credits);
            w.Write(tally.premiumCredits);
        }
    }

    public static List<GuildDonatorVO> ReadTallies(BinaryReader r)
    {
        var tallies = new List<GuildDonatorVO>();
        for (int i = Math.Min(r.ReadInt32(), 1000); i > 0; i--)
        {
            var tally = new GuildDonatorVO { name = r.ReadString(), credits = r.ReadDouble(), premiumCredits = r.ReadDouble() };
            if (tally.name.Length > 0 && !double.IsNaN(tally.credits) && !double.IsNaN(tally.premiumCredits) && !tallies.Any(t => t.name == tally.name))
            {
                tally.credits = Math.Max(0, tally.credits);
                tally.premiumCredits = Math.Max(0, tally.premiumCredits);
                tallies.Add(tally);
            }
        }
        return tallies;
    }

    private static void ReadNames(BinaryReader r, List<string> into)
    {
        for (int i = r.ReadInt32(); i > 0; i--)
        {
            into.Add(r.ReadString());
        }
    }

    private static void WriteNames(BinaryWriter w, List<string> names)
    {
        w.Write(names.Count);
        foreach (string name in names)
        {
            w.Write(name);
        }
    }
}
