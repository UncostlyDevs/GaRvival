using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GarRevival
{
    // Joins the first of several targets that lets us in: a server's LAN
    // addresses before its public one, or matchmaking candidates in order.
    // A refusal (full, other version), a lost connection or no answer moves
    // on to the next.
    internal static class Joiner
    {
        private const float AttemptTimeout = 12f;

        private static List<JoinTarget> _targets;
        private static int _index;
        private static float _deadline;
        private static Action<bool, string> _done;

        public static bool Busy => _targets != null;

        public static string Status { get; private set; } = string.Empty;

        public static void Start(IEnumerable<JoinTarget> targets, Action<bool, string> done)
        {
            Cancel();
            _targets = targets.ToList();
            _done = done;
            _index = -1;
            Next(null);
        }

        public static void Cancel()
        {
            if (_targets == null)
            {
                return;
            }
            _targets = null;
            _done = null;
            Status = "Cancelled.";
            Session.Leave(null);
        }

        public static void Update()
        {
            if (_targets == null)
            {
                return;
            }
            JoinTarget current = _targets[_index];
            if (Session.Welcomed)
            {
                Finish(true, "Joined " + current.Label + ".");
            }
            else if (Session.LastJoinError != null)
            {
                Next(Session.LastJoinError);
            }
            else if (Time.realtimeSinceStartup > _deadline)
            {
                Next("No answer from " + current.Label + ".");
            }
        }

        private static void Next(string lastError)
        {
            if (lastError != null)
            {
                RevivalLog.Info("Join: " + _targets[_index].Label + ": " + lastError);
            }
            if (++_index >= _targets.Count)
            {
                Session.Leave(null);
                Finish(false, lastError ?? "Nothing to join.");
                return;
            }
            JoinTarget target = _targets[_index];
            Status = "Connecting to " + target.Label + "...";
            _deadline = Time.realtimeSinceStartup + AttemptTimeout;
            Session.LastJoinError = null;
            try
            {
                target.Start();
            }
            catch (Exception ex)
            {
                Session.LastJoinError = ex.Message;
            }
        }

        private static void Finish(bool joined, string message)
        {
            Action<bool, string> done = _done;
            _targets = null;
            _done = null;
            Status = message;
            RevivalLog.Info("Join: " + message);
            try
            {
                done?.Invoke(joined, message);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Join result could not be handled", ex);
            }
        }
    }
}
