using System.Linq;
using UnityEngine;

namespace GarRevival;

// FIND MATCH: asks the masters for the best-fitting servers and joins the first that lets us in.
// The garage stays usable while a strip shows progress.
internal static class Matchmaker
{
    private enum Phase { Idle, Asking, Joining, Done }

    private const float ShowResult = 10f;

    private static Phase _phase;
    private static string _text = string.Empty;
    private static float _hideAt;

    public static bool Visible => _phase != Phase.Idle;

    public static bool Searching => _phase == Phase.Asking || _phase == Phase.Joining;

    public static string Text => _phase == Phase.Joining ? $"Finding a match... {Joiner.Status}" : _text;

    // mode: a GameModeType name, or "any".
    public static void Begin(string mode)
    {
        if (Searching) return;
        if (!MasterClient.Configured)
        {
            Finish(MasterClient.NoMasterHint);
            return;
        }
        _phase = Phase.Asking;
        _text = "Finding a match... asking the master server";
        RevivalLog.Info($"Matchmaker: looking for {mode}");
        MasterClient.FetchMatch(mode, (servers, error) =>
        {
            if (_phase != Phase.Asking) return;
            var fits = servers.Where(s => s.SameVersion && !s.Full).ToArray();
            if (fits.Length == 0)
            {
                Finish(error ?? "No match found: no server with room for this mode right now. Host one, or try again later.");
                return;
            }
            _phase = Phase.Joining;
            RevivalLog.Info("Matchmaker: candidates " + string.Join(", ", fits.Select(s => $"{s.Name} ({s.Players}/{s.MaxPlayers})").ToArray()));
            Joiner.Start(fits.SelectMany(s => s.Targets()), (joined, message) => Finish(joined
                ? message + " The server starts the next match; stay in the garage with a complete robot."
                : $"No match found: {fits.Length} server(s) tried, none let us in ({message})"));
        });
    }

    public static void Cancel()
    {
        if (!Searching)
        {
            _phase = Phase.Idle;
            return;
        }
        if (_phase == Phase.Joining) Joiner.Cancel();
        Finish("Matchmaking cancelled.");
    }

    private static void Finish(string text)
    {
        _phase = Phase.Done;
        _text = text;
        _hideAt = Time.realtimeSinceStartup + ShowResult;
        RevivalLog.Info($"Matchmaker: {text}");
    }

    public static void Update()
    {
        if (_phase == Phase.Done && Time.realtimeSinceStartup > _hideAt)
        {
            _phase = Phase.Idle;
        }
        else if (_phase == Phase.Joining && !Joiner.Busy)
        {
            // The server browser started its own join.
            Finish("Matchmaking stopped.");
        }
    }
}
