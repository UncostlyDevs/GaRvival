using System;
using System.Globalization;
using JunkWars.Client;
using UnityEngine;

namespace GarRevival
{
    // "GAR.exe -garserver" runs the game as a server that hosts without
    // playing: it hosts as soon as it reaches the garage, starts a match on
    // its own once enough players are in the session, and fields no robot of
    // its own (it watches one of the bots, which the arena needs). It uses its
    // own profile ("server", or the -garprofile name), dedicated.cfg (host,
    // port, when to start, rewards) and dedicated-battle.cfg (map, mode, bots).
    // Players can type !start in the chat to start the next match at once.
    internal static class DedicatedServer
    {
        private const float HostRetry = 30f;

        private static float _nextHostTry;
        private static float _startAt;
        private static bool _waitingNoted;
        private static bool _set;

        public static bool Enabled
        {
            get { return Instance.Dedicated; }
        }

        private static float Now
        {
            get { return Time.realtimeSinceStartup; }
        }

        public static void Update()
        {
            if (!Enabled)
            {
                return;
            }
            if (!_set)
            {
                // Nobody watches the server: keep running in the background,
                // make no sound, and don't spin the CPU (with -batchmode there
                // is no vsync to hold the frame rate down).
                _set = true;
                Application.runInBackground = true;
                Application.targetFrameRate = 60;
                AudioListener.volume = 0f;
                RevivalLog.Info("Dedicated server mode" + (Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-batchmode", StringComparison.OrdinalIgnoreCase)) ? " (headless)" : string.Empty));
            }
            if (GameManager.Instance == null || GameManager.Instance.currentGameState != GameState.Garage)
            {
                return;
            }
            ServerRules.Rules rules = ServerRules.Active;
            if (Session.Role == Session.Roles.None)
            {
                if (Now >= _nextHostTry)
                {
                    _nextHostTry = Now + HostRetry;
                    Host();
                }
                return;
            }
            if (Session.Role != Session.Roles.Host || rules == null || SessionBattle.Busy)
            {
                _startAt = 0f;
                return;
            }
            int players = Session.RemoteNames.Count;
            if (players < rules.MinPlayers)
            {
                if (_startAt > 0f)
                {
                    Say("Not enough players any more; the match waits.");
                }
                else if (!_waitingNoted)
                {
                    RevivalLog.Info("Dedicated server: waiting for " + rules.MinPlayers + " player(s)");
                }
                _waitingNoted = true;
                _startAt = 0f;
                return;
            }
            _waitingNoted = false;
            if (_startAt <= 0f)
            {
                _startAt = Now + rules.MatchDelay;
                Say("Next match in " + rules.MatchDelay.ToString("0", CultureInfo.InvariantCulture) + " seconds. Be in the garage with a complete robot to take part. Type !start to start now.");
            }
            else if (Now >= _startAt)
            {
                _startAt = 0f;
                RevivalLog.Info("Dedicated server: starting a match with " + players + " player(s)");
                SessionBattle.HostStart();
            }
        }

        private static void Host()
        {
            ServerRules.Rules rules = ServerRules.Hosted;
            RevivalLog.Info("Dedicated server \"" + rules.Name + "\": hosting on " + (rules.Steam ? "Steam" : "TCP port " + rules.Port));
            if (rules.Steam)
            {
                Session.HostSteam();
            }
            else
            {
                Session.HostTcp(rules.Port);
            }
        }

        // Host: every chat line passes here.
        public static void OnChat(string sender, string text)
        {
            if (!Enabled || Session.Role != Session.Roles.Host || sender == Session.LocalName)
            {
                return;
            }
            if (string.Equals(text.Trim(), "!start", StringComparison.OrdinalIgnoreCase))
            {
                if (SessionBattle.Busy)
                {
                    Say("A match is already on.");
                }
                else if (_startAt > 0f)
                {
                    _startAt = Now;
                }
                else
                {
                    Say("Waiting for more players before a match can start.");
                }
            }
        }

        private static void Say(string text)
        {
            RevivalLog.Info("Dedicated server says: " + text);
            Session.SendChat(text, null);
        }
    }
}
