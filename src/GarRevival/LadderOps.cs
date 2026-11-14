using System;
using System.Collections.Generic;
using JunkWars.Common.Player.ValueObjects;

namespace GarRevival
{
    // The player's own ladder standing: the badge in the garage and the
    // league-change notice (event 139). Ladder points are kept in
    // PlayerVO.ladderRating and seasonBattles. With no other players to rank
    // against, the position is how far through the league the points are
    // (100 on entering it, 1 at the top). The ladder table itself is a web
    // page the client downloads; that is not served here.
    internal static class LadderOps
    {
        private const byte EvUserLadderInfo = 139;

        // -1 until the placement battles are played, then 0 (Recruit) to 4 (Master).
        public static short League(PlayerVO player)
        {
            if (player.seasonBattles < MissionCatalog.LadderPlacementBattles)
            {
                return -1;
            }
            short league = 0;
            for (int i = 0; i < MissionCatalog.LeagueFloors.Length; i++)
            {
                if (player.ladderRating >= MissionCatalog.LeagueFloors[i])
                {
                    league = (short)i;
                }
            }
            return league;
        }

        public static int Position(PlayerVO player)
        {
            short league = League(player);
            if (league < 0)
            {
                return 0;
            }
            double[] floors = MissionCatalog.LeagueFloors;
            double span = league + 1 < floors.Length ? floors[league + 1] - floors[league] : MissionCatalog.MasterSpan;
            double through = Math.Max(0, Math.Min(1, (player.ladderRating - floors[league]) / span));
            return 100 - (int)Math.Floor(through * 99);
        }

        // Adds a battle to the standing; returns the leagues gained or lost.
        public static short Rate(PlayerVO player, BattleFacts facts)
        {
            short before = League(player);
            player.seasonBattles++;
            double change = (facts.Won ? MissionCatalog.LadderWin : facts.Drawn ? MissionCatalog.LadderDraw : MissionCatalog.LadderLoss)
                + Math.Min(MissionCatalog.LadderKillCap, facts.Kills * MissionCatalog.LadderPerKill);
            player.ladderRating = Math.Max(0, player.ladderRating + change);
            short after = League(player);
            // Being placed for the first time is not a promotion.
            return before < 0 ? (short)0 : (short)(after - before);
        }

        // The client casts these exactly: short, int, short.
        public static void Announce(Loopback.VirtualConnection conn, short leagueDelta)
        {
            if (conn == null)
            {
                return;
            }
            PlayerVO player = PlayerState.Current;
            GameServer.Event(conn, EvUserLadderInfo, new Dictionary<byte, object>
            {
                { 168, League(player) },
                { 167, Position(player) },
                { 166, leagueDelta },
            });
        }
    }
}
