using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GarRevival;

// part-overrides.csv (partId,stat,value): tier-0 values that replace the built-in ones at startup.
// Tiered stats (durability, damage, splash, capacity, regen) still grow with the tier curve.
internal static class PartOverrides
{
    public static readonly string[] Stats = ["durability", "weight", "speed", "weightLimit", "damage", "splash", "rateOfFire", "capacity", "regen", "price"];

    private static Dictionary<string, Dictionary<string, float>> _values;

    private static string FilePath => Path.Combine(RevivalLog.Folder, "part-overrides.csv");

    public static float? Get(string partId, string stat)
    {
        _values ??= Load();
        return _values.TryGetValue(partId, out var stats) && stats.TryGetValue(stat.ToLowerInvariant(), out float v) ? v : null;
    }

    public static float Or(string partId, string stat, float value) => Get(partId, stat) ?? value;

    private static Dictionary<string, Dictionary<string, float>> Load()
    {
        var values = new Dictionary<string, Dictionary<string, float>>();
        if (!File.Exists(FilePath)) return values;
        int count = 0;
        foreach (string raw in File.ReadAllLines(FilePath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("partId", StringComparison.OrdinalIgnoreCase)) continue;
            string[] cells = line.Split(',');
            if (cells.Length < 3 || !Stats.Contains(cells[1].Trim(), StringComparer.OrdinalIgnoreCase)
                || !float.TryParse(cells[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || value < 0f)
            {
                RevivalLog.Info("part-overrides.csv: skipped line: " + line);
                continue;
            }
            string id = cells[0].Trim();
            if (!values.TryGetValue(id, out var stats)) values[id] = stats = new();
            stats[cells[1].Trim().ToLowerInvariant()] = value;
            count++;
        }
        RevivalLog.Info($"part-overrides.csv: {count} value(s) for {values.Count} part(s)");
        return values;
    }
}
