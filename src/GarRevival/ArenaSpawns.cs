using System;
using System.Collections.Generic;
using System.Reflection;
using JunkWars.Client.Arena;
using JunkWars.Common.Player.ValueObjects;
using UnityEngine;

namespace GarRevival
{
    // Every arena has a fixed number of spawn points per team, and the game
    // indexes them without checking: one robot too many and the arena fails to
    // build. When a team is larger than its spawn points, the extra robots are
    // placed beside existing points instead.
    internal static class ArenaSpawns
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo TeamA = typeof(BaseArena).GetField("spawnPoints_teamA", Hidden);
        private static readonly FieldInfo TeamB = typeof(BaseArena).GetField("spawnPoints_teamB", Hidden);
        private static BaseArena _reported;

        // Hook on BaseArena.GetSpawingPoint. True = result is set, skip the original.
        public static bool Overflow(BaseArena arena, ActorVO actor, ref Transform result)
        {
            Array a = TeamA == null ? null : TeamA.GetValue(arena) as Array;
            Array b = TeamB == null ? null : TeamB.GetValue(arena) as Array;
            if (a == null || b == null)
            {
                return false;
            }
            if (_reported != arena)
            {
                _reported = arena;
                RevivalLog.Info("Arena " + arena.mapID + " has " + a.Length + " + " + b.Length + " spawn points");
            }
            Array points = actor.team == 0 ? a : actor.team == 1 ? b : null;
            if (points == null || points.Length == 0)
            {
                return false;
            }
            // Same ordering as the original: position among the team's actor ids.
            List<int> team = new List<int>();
            foreach (KeyValuePair<int, ActorVO> other in arena.actors)
            {
                if (other.Value.team == actor.team)
                {
                    team.Add(other.Key);
                }
            }
            team.Sort();
            int index = team.IndexOf(actor.id);
            if (index < points.Length)
            {
                return false;
            }
            Transform near = ((Component)points.GetValue(index % points.Length)).transform;
            int ring = index / points.Length;
            GameObject extra = new GameObject("OverflowSpawn_" + actor.id);
            extra.transform.position = near.position - near.forward * (5f * ring) + Vector3.up * 0.5f;
            extra.transform.rotation = near.rotation;
            result = extra.transform;
            RevivalLog.Info("Team " + actor.team + " has more robots than spawn points; actor " + actor.id + " placed behind spawn point " + (index % points.Length));
            return true;
        }
    }
}
