using System;
using System.IO;
using UnityEngine;

namespace GarRevival
{
    // Writes GARRevival/revival.log in the game folder, including everything
    // the game itself logs, so problems can be diagnosed without a debugger.
    internal static class RevivalLog
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string Folder
        {
            get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GARRevival"); }
        }

        public static void Init()
        {
            if (_path != null)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Folder);
                _path = Path.Combine(Folder, "revival" + Instance.Suffix + ".log");
                File.WriteAllText(_path, "GAR Revival log started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
                Application.logMessageReceived += OnUnityLog;
            }
            catch (Exception)
            {
                _path = null;
            }
        }

        public static void Info(string message)
        {
            Write("revival", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("revival:ERROR", message + ": " + ex);
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            bool withStack = type == LogType.Exception || type == LogType.Error || type == LogType.Assert;
            Write("unity:" + type, withStack ? condition + Environment.NewLine + stackTrace : condition);
        }

        private static void Write(string tag, string message)
        {
            Init();
            if (_path == null)
            {
                return;
            }
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff") + " [" + tag + "] " + message + Environment.NewLine);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
