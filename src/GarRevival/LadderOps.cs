using System;
using System.Collections.Generic;
using JunkWars.Common.Player.ValueObjects;

namespace GarRevival;

// The player's own ladder standing (garage badge, event 139), kept in PlayerVO.ladderRating and seasonBattles.
// Position is how far through the league the points are: 100 on entering it, 1 at the top.
internal static class LadderOps
{
    private const byte EvUserLadderInfo = 139;

    // Points won or lost in this profile's last rated battle (the table's up/down arrow).
    public static double LastChange { get; private set; }

    static LadderOps()
    {
        PlayerState.ProfileChanged += () => LastChange = 0;
    }

    // -1 until the placement battles are played, then 0 (Recruit) to 4 (Master).
    public static short League(PlayerVO player) => League(player.ladderRating, player.seasonBattles);

    public static short League(double rating, int battles)
    {
        if (battles < MissionCatalog.LadderPlacementBattles) return -1;
        short league = 0;
        for (int i = 0; i < MissionCatalog.LeagueFloors.Length; i++)
        {
            if (rating >= MissionCatalog.LeagueFloors[i])
            {
                league = (short)i;
            }
        }
        return league;
    }

    public static int Position(PlayerVO player) => Position(player.ladderRating, player.seasonBattles);

    public static int Position(double rating, int battles)
    {
        short league = League(rating, battles);
        if (league < 0) return 0;
        var floors = MissionCatalog.LeagueFloors;
        double span = league + 1 < floors.Length ? floors[league + 1] - floors[league] : MissionCatalog.MasterSpan;
        double through = Math.Max(0, Math.Min(1, (rating - floors[league]) / span));
        return 100 - (int)Math.Floor(through * 99);
    }

    // Adds a battle to the standing; returns the leagues gained or lost.
    public static short Rate(PlayerVO player, BattleFacts facts)
    {
        short before = League(player);
        player.seasonBattles++;
        double change = (facts.Won ? MissionCatalog.LadderWin : facts.Drawn ? MissionCatalog.LadderDraw : MissionCatalog.LadderLoss)
            + Math.Min(MissionCatalog.LadderKillCap, facts.Kills * MissionCatalog.LadderPerKill);
        double old = player.ladderRating;
        player.ladderRating = Math.Max(0, player.ladderRating + change);
        LastChange = player.ladderRating - old;
        short after = League(player);
        // Being placed for the first time is not a promotion.
        return before < 0 ? (short)0 : (short)(after - before);
    }

    // The client casts these exactly: short, int, short.
    public static void Announce(Loopback.VirtualConnection conn, short leagueDelta)
    {
        if (conn == null) return;
        var player = PlayerState.Current;
        GameServer.Event(conn, EvUserLadderInfo, new Dictionary<byte, object>
        {
            { 168, League(player) },
            { 167, Position(player) },
            { 166, leagueDelta },
        });
    }
}
