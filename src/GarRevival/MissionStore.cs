using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GarRevival;

// Mission figures PlayerVO has no field for: counters (parts bought, credits spent, ...) and login
// days, saved in missions.cfg next to the profile. Mission progress itself lives in the profile.
internal static class MissionStore
{
    private const int LoginDaysKept = 120;

    private static Dictionary<string, double> _counters;
    private static List<DateTime> _logins;

    private static string SavePath => Path.Combine(PlayerState.Folder, "missions.cfg");

    // A server profile has its own mission state.
    static MissionStore()
    {
        PlayerState.ProfileChanged += () =>
        {
            _counters = null;
            _logins = null;
        };
    }

    public static double Get(string counter)
    {
        EnsureLoaded();
        return _counters.TryGetValue(counter, out double value) ? value : 0;
    }

    public static void Add(string counter, double amount)
    {
        EnsureLoaded();
        _counters[counter] = Get(counter) + amount;
        Save();
    }

    // Keeps the highest value seen.
    public static void Raise(string counter, double value)
    {
        if (value > Get(counter))
        {
            _counters[counter] = value;
            Save();
        }
    }

    public static void NoteLogin(DateTime day)
    {
        EnsureLoaded();
        if (_logins.Contains(day.Date)) return;
        _logins.Add(day.Date);
        _logins.Sort();
        if (_logins.Count > LoginDaysKept)
        {
            _logins.RemoveRange(0, _logins.Count - LoginDaysKept);
        }
        Save();
    }

    // The most login days that fall inside any window of this many days.
    public static int BestLoginWindow(int days)
    {
        EnsureLoaded();
        int best = 0;
        for (int i = 0; i < _logins.Count; i++)
        {
            var end = _logins[i].AddDays(days);
            best = Math.Max(best, _logins.Skip(i).TakeWhile(d => d < end).Count());
        }
        return best;
    }

    private static void EnsureLoaded()
    {
        if (_counters != null) return;
        _counters = new();
        _logins = [];
        try
        {
            if (!File.Exists(SavePath)) return;
            foreach (string raw in File.ReadAllLines(SavePath))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key == "logins")
                {
                    foreach (string text in value.Split([','], StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (DateTime.TryParseExact(text.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && !_logins.Contains(day))
                        {
                            _logins.Add(day);
                        }
                    }
                    _logins.Sort();
                }
                else if (key.StartsWith("counter.") && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    _counters[key.Substring("counter.".Length)] = number;
                }
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error($"Missions: could not read {SavePath}; counters start from zero", ex);
        }
    }

    private static void Save()
    {
        try
        {
            List<string> lines = ["# GAR Revival mission counters. Mission progress itself is in profile.dat."];
            lines.AddRange(_counters.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => "counter." + c.Key + "=" + c.Value.ToString("R", CultureInfo.InvariantCulture)));
            lines.Add("logins=" + string.Join(",", _logins.Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).ToArray()));
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
            string temp = SavePath + ".tmp";
            File.WriteAllText(temp, string.Join("\n", lines.ToArray()) + "\n");
            if (File.Exists(SavePath))
            {
                File.Delete(SavePath);
            }
            File.Move(temp, SavePath);
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Missions: could not save " + SavePath, ex);
        }
    }
}
