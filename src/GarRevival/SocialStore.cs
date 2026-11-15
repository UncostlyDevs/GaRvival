using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JunkWars.Common.Guild.ValueObjects;
using JunkWars.Common.Social;
using JunkWars.Common.Utils;
using JunkWars.Common.Utils.Enums;

namespace GarRevival
{
    // What the social features must remember between launches and PlayerVO has
    // no field for: invitations waiting in the message box, notices owed to
    // players who were not around, and this player's copy of their guild.
    // Saved next to the profile as social.dat. (The friend list and the guild
    // id are PlayerVO fields and live in the profile.)
    internal static class SocialStore
    {
        private const int FormatVersion = 1;

        public sealed class Notice
        {
            public string Target;
            public byte Kind; // a SocialOps message kind
        }

        public static readonly List<SocialMessageData> Inbox = new List<SocialMessageData>();
        public static readonly List<string> SentFriendInvites = new List<string>();
        public static readonly List<string> SentGuildInvites = new List<string>();
        public static readonly List<Notice> Outbox = new List<Notice>();
        public static readonly List<string> DisbandedGuilds = new List<string>();

        // My copy of my guild: the roster (with the revision it was last
        // changed at) and what each member has earned for the guild.
        public static GuildVO Guild;
        public static long GuildRevision;
        public static readonly Dictionary<string, int> GuildExperience = new Dictionary<string, int>();
        // What each member spent from the vault on guild benefits (credits, sparks).
        public static readonly List<GuildDonatorVO> GuildBuffSpending = new List<GuildDonatorVO>();
        public static string PendingGuildJoin = string.Empty;

        // The guild as I left it, for members who were away when I left.
        public static string FarewellGuildId = string.Empty;
        public static long FarewellRevision;
        public static byte[] FarewellState = new byte[0];

        private static bool _loaded;

        private static string SavePath
        {
            get { return Path.Combine(Path.Combine(RevivalLog.Folder, "save" + Instance.Suffix), "social.dat"); }
        }

        public static void Load()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            if (!File.Exists(SavePath))
            {
                return;
            }
            try
            {
                Read(new BinaryReader(new MemoryStream(File.ReadAllBytes(SavePath))));
                RevivalLog.Info("Loaded social data: " + Inbox.Count + " invitations, guild " + (Guild == null ? "none" : Guild.name));
            }
            catch (Exception ex)
            {
                string broken = SavePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(SavePath, broken);
                RevivalLog.Error("Social data could not be read; moved to " + broken + " and starting empty", ex);
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
            FarewellState = new byte[0];
        }

        private static void Read(BinaryReader r)
        {
            if (r.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("unknown social.dat version");
            }
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
            byte[] guild = r.ReadBytes(r.ReadInt32());
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
            // Added later; older files end here and older builds stop reading here.
            if (r.BaseStream.Position < r.BaseStream.Length)
            {
                GuildBuffSpending.AddRange(ReadTallies(r));
            }
        }

        public static void Save()
        {
            MemoryStream stream = new MemoryStream();
            BinaryWriter w = new BinaryWriter(stream);
            w.Write(FormatVersion);
            w.Write(Inbox.Count);
            foreach (SocialMessageData message in Inbox)
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
            foreach (Notice notice in Outbox)
            {
                w.Write(notice.Target);
                w.Write(notice.Kind);
            }
            WriteNames(w, DisbandedGuilds);
            byte[] guild = Guild == null ? new byte[0] : GuildToBytes(Guild);
            w.Write(guild.Length);
            w.Write(guild);
            w.Write(GuildRevision);
            w.Write(GuildExperience.Count);
            foreach (KeyValuePair<string, int> earned in GuildExperience)
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

            string dir = Path.GetDirectoryName(SavePath);
            Directory.CreateDirectory(dir);
            string temp = SavePath + ".tmp";
            File.WriteAllBytes(temp, stream.ToArray());
            if (File.Exists(SavePath))
            {
                File.Delete(SavePath);
            }
            File.Move(temp, SavePath);
        }

        public static byte[] GuildToBytes(GuildVO guild)
        {
            return DataSerialization.ProtoSerializeObject(guild);
        }

        public static GuildVO GuildFromBytes(byte[] bytes)
        {
            GuildVO guild = DataSerialization.ProtoDeserializeObject<GuildVO>(bytes);
            VoNormalizer.FillNullCollections(guild);
            return guild;
        }

        // Per member: name, credits, sparks.
        public static void WriteTallies(BinaryWriter w, List<GuildDonatorVO> tallies)
        {
            w.Write(tallies.Count);
            foreach (GuildDonatorVO tally in tallies)
            {
                w.Write(tally.name ?? string.Empty);
                w.Write(tally.credits);
                w.Write(tally.premiumCredits);
            }
        }

        public static List<GuildDonatorVO> ReadTallies(BinaryReader r)
        {
            List<GuildDonatorVO> tallies = new List<GuildDonatorVO>();
            for (int i = Math.Min(r.ReadInt32(), 1000); i > 0; i--)
            {
                GuildDonatorVO tally = new GuildDonatorVO { name = r.ReadString(), credits = r.ReadDouble(), premiumCredits = r.ReadDouble() };
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
}
