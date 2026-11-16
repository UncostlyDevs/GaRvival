using System;
using System.Collections.Generic;
using System.Reflection;
using JunkWars.Client.Arena;
using JunkWars.Common.Player.ValueObjects;
using UnityEngine;

namespace GarRevival;

// The game indexes each team's spawn points without a bounds check, so one robot too many breaks
// the arena build. Extra robots are placed behind existing points instead.
internal static class ArenaSpawns
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo TeamA = typeof(BaseArena).GetField("spawnPoints_teamA", Hidden);
    private static readonly FieldInfo TeamB = typeof(BaseArena).GetField("spawnPoints_teamB", Hidden);
    private static BaseArena _reported;

    // Hook on BaseArena.GetSpawingPoint. True = result is set, skip the original.
    public static bool Overflow(BaseArena arena, ActorVO actor, ref Transform result)
    {
        var a = TeamA?.GetValue(arena) as Array;
        var b = TeamB?.GetValue(arena) as Array;
        if (a == null || b == null) return false;
        if (_reported != arena)
        {
            _reported = arena;
            RevivalLog.Info($"Arena {arena.mapID} has {a.Length} + {b.Length} spawn points");
        }
        var points = actor.team switch { 0 => a, 1 => b, _ => null };
        if (points == null || points.Length == 0) return false;
        // Same ordering as the game: position among the team's actor ids.
        List<int> team = [];
        foreach (var other in arena.actors)
        {
            if (other.Value.team == actor.team)
            {
                team.Add(other.Key);
            }
        }
        team.Sort();
        int index = team.IndexOf(actor.id);
        if (index < points.Length) return false;
        var near = ((Component)points.GetValue(index % points.Length)).transform;
        int ring = index / points.Length;
        var extra = new GameObject("OverflowSpawn_" + actor.id);
        extra.transform.position = near.position - near.forward * (5f * ring) + Vector3.up * 0.5f;
        extra.transform.rotation = near.rotation;
        result = extra.transform;
        RevivalLog.Info($"Team {actor.team} has more robots than spawn points; actor {actor.id} placed behind spawn point {index % points.Length}");
        return true;
    }
}
