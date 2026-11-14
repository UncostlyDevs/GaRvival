using System.Collections.Generic;
using System.Reflection;
using JunkWars.Client;
using JunkWars.Client.Arena;
using UnityEngine;

namespace GarRevival
{
    // The arena's match timer and its 5-4-3-2-1 start countdown read the match
    // clock from the online server's room data, which no longer exists, so the
    // timer sat on its placeholder text and the countdown never showed. This
    // drives both from the arena's own clock (BaseArena.currenBattleTime:
    // negative during the pre-match countdown, then seconds since the start).
    internal static class BattleHud
    {
        private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo TimerLabel = typeof(UIArenaBattleTimer).GetField("Label_Timer", Hidden);
        private static readonly MethodInfo BeginDigit = typeof(UIArenaCountdownNotificarionController).GetMethod("BeginEffect", Hidden);

        private static OfflineArena _arena;
        private static UIArenaBattleTimer _timer;
        private static object _label;
        private static PropertyInfo _labelText;
        private static PropertyInfo _labelColor;
        private static int _shown;
        private static UIArenaCountdownNotificarionController[] _digits;
        private static readonly HashSet<int> PlayedDigits = new HashSet<int>();

        public static void Update()
        {
            OfflineArena arena = BattleManager.arena as OfflineArena;
            if (arena == null || !arena.GameStarted || arena.GameEnded || BattleManager.battleType != BattleType.OfflineMultyplayer)
            {
                _arena = null;
                return;
            }
            if (_arena != arena)
            {
                _arena = arena;
                _timer = null;
                _label = null;
                _digits = null;
                _shown = int.MinValue;
                PlayedDigits.Clear();
            }
            float time = arena.currenBattleTime;
            UpdateTimer(arena, time);
            if (time < 0f)
            {
                UpdateCountdown(-time);
            }
        }

        // Counts down to the start in red, then shows the time left in the match.
        private static void UpdateTimer(OfflineArena arena, float time)
        {
            if (_timer == null)
            {
                _timer = Object.FindObjectOfType<UIArenaBattleTimer>();
                _label = _timer == null || TimerLabel == null ? null : TimerLabel.GetValue(_timer);
                if (_label != null)
                {
                    _labelText = _label.GetType().GetProperty("text");
                    _labelColor = _label.GetType().GetProperty("color");
                }
            }
            if (_timer == null || _label == null || _labelText == null)
            {
                return;
            }
            bool starting = time < 0f;
            // Whole seconds rounded down, as the original did, so the timer
            // agrees with the big countdown digit.
            int seconds = starting ? Mathf.FloorToInt(-time) : Mathf.Max(0, Mathf.FloorToInt(arena.battleSetupData.gameDuration - time));
            int key = starting ? -seconds - 1 : seconds;
            if (key == _shown)
            {
                return;
            }
            _shown = key;
            _labelText.SetValue(_label, seconds / 60 + ":" + (seconds % 60).ToString("00"), null);
            if (_labelColor != null)
            {
                _labelColor.SetValue(_label, starting ? GlobalConstants.GUIRed : GlobalConstants.GUIWhite, null);
            }
        }

        // Each digit has its own effect object; the original showed digit N
        // while between N and N+1 seconds were left.
        private static void UpdateCountdown(float secondsLeft)
        {
            if (_digits == null || _digits.Length == 0)
            {
                _digits = Object.FindObjectsOfType<UIArenaCountdownNotificarionController>();
            }
            foreach (UIArenaCountdownNotificarionController digit in _digits)
            {
                int number = (int)digit.m_Digit;
                if (digit != null && secondsLeft <= number + 1 && secondsLeft > number && PlayedDigits.Add(number) && BeginDigit != null)
                {
                    BeginDigit.Invoke(digit, null);
                }
            }
        }
    }
}
