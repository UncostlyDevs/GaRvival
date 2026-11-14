using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Player.ValueObjects;

namespace GarRevival
{
    // Menu > Redeem Code. The codes used to be checked by the website; now
    // they come from GARRevival/codes.cfg, one per line:
    //     CODE = credits 5000
    //     CODE = sparks 50
    //     CODE = credits 2000, sparks 10
    // Anyone can add codes (a server owner handing out starter money, say).
    // Each code works once per profile; the used ones are kept in the
    // profile's folder (redeemed.txt).
    internal static class RedeemCodes
    {
        private const string DefaultFile =
            "# GAR Revival redeem codes (Menu > Redeem Code). One per line:\n" +
            "#   CODE = credits 5000\n" +
            "#   CODE = sparks 50\n" +
            "#   CODE = credits 2000, sparks 10\n" +
            "# Codes are not case-sensitive and work once per profile.\n" +
            "REVIVAL = credits 5000, sparks 25\n";

        private static string CodesPath
        {
            get { return Path.Combine(RevivalLog.Folder, "codes.cfg"); }
        }

        private static string UsedPath
        {
            get { return Path.Combine(PlayerState.Folder, "redeemed.txt"); }
        }

        // The website's answers: "OK", "code not found", "already used".
        public static string Redeem(string code)
        {
            code = (code ?? string.Empty).Trim().ToUpperInvariant();
            if (!File.Exists(CodesPath))
            {
                File.WriteAllText(CodesPath, DefaultFile);
            }
            float credits = 0f, sparks = 0f;
            bool found = false;
            foreach (string raw in File.ReadAllLines(CodesPath))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq <= 0 || line.Substring(0, eq).Trim().ToUpperInvariant() != code)
                {
                    continue;
                }
                found = true;
                foreach (string reward in line.Substring(eq + 1).Split(','))
                {
                    string[] words = reward.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    float amount;
                    if (words.Length == 2 && float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
                    {
                        if (words[0].Equals("credits", StringComparison.OrdinalIgnoreCase))
                        {
                            credits += amount;
                        }
                        else if (words[0].Equals("sparks", StringComparison.OrdinalIgnoreCase))
                        {
                            sparks += amount;
                        }
                    }
                }
                break;
            }
            if (!found || code.Length == 0)
            {
                RevivalLog.Info("Redeem code \"" + code + "\": not found");
                return "code not found";
            }
            List<string> used = File.Exists(UsedPath) ? File.ReadAllLines(UsedPath).Select(u => u.Trim().ToUpperInvariant()).ToList() : new List<string>();
            if (used.Contains(code))
            {
                RevivalLog.Info("Redeem code \"" + code + "\": already used on this profile");
                return "already used";
            }
            File.AppendAllText(UsedPath, code + Environment.NewLine);

            PlayerVO player = PlayerState.Current;
            player.credits += credits;
            player.premiumCredits += sparks;
            PlayerState.Save();
            GameServer.Event(Loopback.Get(PhotonNetwork.networkingPeer), 220, new Dictionary<byte, object>
            {
                { 213, player.credits },
                { 214, credits },
                { 234, player.premiumCredits },
                { 218, sparks },
                { 168, (byte)ItemAddedReason.TimedMissionReward },
            });
            RevivalLog.Info("Redeem code \"" + code + "\": +" + credits + " credits, +" + sparks + " sparks");
            return "OK";
        }
    }
}
