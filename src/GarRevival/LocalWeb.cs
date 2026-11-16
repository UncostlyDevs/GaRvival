using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace GarRevival
{
    // The game also talked to a website (play-gar.com: friends' online
    // status, nickname search, ladder, news, Steam login and purchases,
    // logging). That domain no longer belongs to the game, so none of it may
    // go there: some requests carry player names and Steam ids. Every web
    // address the game builds is pointed at this small server on 127.0.0.1
    // instead. It answers what the mod can answer and writes every request to
    // web-requests.log, which shows what the game still expects from a
    // website.
    internal static class LocalWeb
    {
        private const int FirstPort = 27080;
        private const float RequestTimeout = 5f;
        private const string LadderPath = "/garphpapi/ladder/index.php";

        private sealed class Pending
        {
            public TcpClient Client;
            public MemoryStream Data = new MemoryStream();
            public float Started;
            public string Method, Target, Body; // set once the whole request is in
            public LadderTable.Job Ladder;     // may wait for the master server
        }

        private static TcpListener _listener;
        private static readonly List<Pending> Open = new List<Pending>();
        private static string _base;

        public static int Port { get; private set; }

        // Call before the game's first web request (the login screen).
        public static void Start()
        {
            if (_listener != null)
            {
                return;
            }
            for (int port = FirstPort; port < FirstPort + 10 && _listener == null; port++)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, port);
                    _listener.Start();
                    Port = port;
                }
                catch (SocketException)
                {
                    _listener = null;
                }
            }
            // Even with no listener the addresses are redirected: a refused
            // connection is better than a request to someone else's site.
            _base = "127.0.0.1:" + (Port == 0 ? 9 : Port);
            Redirect();
            RevivalLog.Info(_listener == null ? "Local web server: no free port; the game's web requests will fail" : "Local web server on http://" + _base);
        }

        // ---- pointing the game at it ----

        private static string Rewrite(string url)
        {
            if (url == null)
            {
                return null;
            }
            return url.Replace("https://s3.amazonaws.com/webplayer.play-gar.com", "http://" + _base + "/s3")
                .Replace("s3.amazonaws.com/webplayer.play-gar.com", _base + "/s3")
                .Replace("https://webplayer.play-gar.com", "http://" + _base + "/webplayer")
                .Replace("http://webplayer.play-gar.com", "http://" + _base + "/webplayer")
                .Replace("webplayer.play-gar.com", _base + "/webplayer")
                .Replace("https://play-gar.com", "http://" + _base)
                .Replace("http://play-gar.com", "http://" + _base)
                .Replace("play-gar.com", _base);
        }

        private static void Redirect()
        {
            try
            {
                GlobalSettings.Instance.WebDomain = _base;
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Could not redirect GlobalSettings.WebDomain", ex);
            }
            // Addresses the game worked out once, at startup.
            foreach (Type type in new[] { typeof(GarWebService), typeof(LadderDataHelper) })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType != typeof(string) || field.IsLiteral)
                    {
                        continue;
                    }
                    try
                    {
                        string before = (string)field.GetValue(null);
                        string after = Rewrite(before);
                        if (after != before)
                        {
                            field.SetValue(null, after);
                        }
                    }
                    catch (Exception ex)
                    {
                        RevivalLog.Error("Could not redirect " + type.Name + "." + field.Name, ex);
                    }
                }
            }
        }

        // ---- serving ----

        public static void Poll()
        {
            if (_listener == null)
            {
                return;
            }
            try
            {
                while (_listener.Pending())
                {
                    Open.Add(new Pending { Client = _listener.AcceptTcpClient(), Started = Time.realtimeSinceStartup });
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Local web server accept failed", ex);
            }
            // The client reads each ladder answer with the tab of its latest
            // request, so ladder answers go out in request order.
            bool ladderAhead = false;
            foreach (Pending p in Open.ToArray())
            {
                try
                {
                    if (Step(p, ladderAhead))
                    {
                        Open.Remove(p);
                        p.Client.Close();
                    }
                    else
                    {
                        ladderAhead |= p.Ladder != null;
                    }
                }
                catch (Exception ex)
                {
                    RevivalLog.Error("Local web request failed", ex);
                    Open.Remove(p);
                    p.Client.Close();
                }
            }
        }

        // True when the request is finished with.
        private static bool Step(Pending p, bool ladderAhead)
        {
            if (p.Method == null && !Read(p))
            {
                return Time.realtimeSinceStartup - p.Started > RequestTimeout;
            }
            if (p.Ladder != null && (ladderAhead || !p.Ladder.Ready))
            {
                return false;
            }
            int status = 200;
            string answer = p.Ladder != null ? p.Ladder.Finish() : Answer(p.Method, p.Target, p.Body, out status);
            Record(p.Method, p.Target, p.Body, status);
            byte[] content = Encoding.UTF8.GetBytes(answer ?? string.Empty);
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\n" +
                "Content-Type: " + ContentType(answer ?? string.Empty) + "; charset=utf-8\r\n" +
                "Content-Length: " + content.Length + "\r\nConnection: close\r\n\r\n");
            NetworkStream stream = p.Client.GetStream();
            stream.Write(head, 0, head.Length);
            stream.Write(content, 0, content.Length);
            stream.Flush();
            return true;
        }

        private static string ContentType(string answer)
        {
            return answer.StartsWith("[") || answer.StartsWith("{") ? "application/json" : answer.StartsWith("\n<") ? "text/xml" : "text/plain";
        }

        // Takes in what has arrived; true once the request is complete (or
        // its body is overdue) and parsed into p.
        private static bool Read(Pending p)
        {
            NetworkStream stream = p.Client.GetStream();
            byte[] buffer = new byte[8192];
            while (p.Client.Available > 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                p.Data.Write(buffer, 0, read);
            }
            string text = Encoding.UTF8.GetString(p.Data.ToArray());
            int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                return false;
            }
            string[] lines = text.Substring(0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            int length = 0;
            foreach (string line in lines.Skip(1))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(15).Trim(), out length);
                }
            }
            if (p.Data.Length < Encoding.UTF8.GetByteCount(text.Substring(0, headerEnd + 4)) + length && Time.realtimeSinceStartup - p.Started <= RequestTimeout)
            {
                return false;
            }
            string[] first = lines[0].Split(' ');
            p.Target = first.Length > 1 ? first[1] : "/";
            p.Body = text.Substring(headerEnd + 4);
            p.Method = first[0];
            if (PathOf(p.Target) == LadderPath)
            {
                p.Ladder = LadderTable.Begin(Query(p.Target));
            }
            return true;
        }

        private static string PathOf(string target)
        {
            return target.Split('?')[0].ToLowerInvariant();
        }

        private static Dictionary<string, string> Query(string target)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            int q = target.IndexOf('?');
            if (q < 0)
            {
                return values;
            }
            foreach (string pair in target.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? string.Empty : pair.Substring(eq + 1);
                values[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return values;
        }

        // What the mod can answer. Anything else gets a 404, which the game
        // treats like the site being down (as it has been since 2021).
        private static string Answer(string method, string target, string body, out int status)
        {
            string path = PathOf(target);
            status = 200;
            if (path == "/garphpapi/utils/usersstatus.php")
            {
                // Friends' and guildmates' online status: online = in my session.
                string users;
                StringBuilder json = new StringBuilder("[");
                foreach (string name in StringsOf(Query(target).TryGetValue("users", out users) ? users : "[]"))
                {
                    json.Append(json.Length > 1 ? "," : string.Empty)
                        .Append("{\"nickname\":\"").Append(name.Replace("\\", "\\\\").Replace("\"", "\\\""))
                        .Append("\",\"loggedIn\":").Append(Session.InSession(name) ? 1 : 0).Append(",\"statusMinutes\":0}");
                }
                return json.Append("]").ToString();
            }
            if (path == "/garphpapi/utils/redeemcode.php")
            {
                string code;
                return RedeemCodes.Redeem(Query(target).TryGetValue("code", out code) ? code : string.Empty);
            }
            status = 404;
            return "Not served by GAR Revival";
        }

        // The strings of a JSON array of strings, e.g. ["a.1234","b.5678"].
        private static List<string> StringsOf(string json)
        {
            List<string> result = new List<string>();
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] != '"')
                {
                    continue;
                }
                StringBuilder value = new StringBuilder();
                for (i++; i < json.Length && json[i] != '"'; i++)
                {
                    if (json[i] == '\\' && i + 1 < json.Length)
                    {
                        i++;
                    }
                    value.Append(json[i]);
                }
                result.Add(value.ToString());
            }
            return result;
        }

        // ---- the request log ----

        private static void Record(string method, string target, string body, int status)
        {
            string line = method + " " + target + (body.Length > 0 ? " body=" + (body.Length > 300 ? body.Substring(0, 300) + "..." : body) : string.Empty) + " -> " + status;
            RevivalLog.Info("Web request: " + line);
            try
            {
                File.AppendAllText(Path.Combine(RevivalLog.Folder, "web-requests" + Instance.Suffix + ".log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }

        public static void Stop()
        {
            foreach (Pending p in Open)
            {
                p.Client.Close();
            }
            Open.Clear();
            if (_listener != null)
            {
                _listener.Stop();
                _listener = null;
            }
        }
    }
}
