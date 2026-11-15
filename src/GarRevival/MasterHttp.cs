using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GarRevival
{
    // JSON requests to master servers through Unity's WWW, checked once a
    // frame from the main thread; the answer arrives as a callback there.
    internal static class MasterHttp
    {
        internal sealed class Response
        {
            public int Status;
            public Dictionary<string, object> Json;
            public string Error;

            public bool Ok => Status == 200 && Json != null;
        }

        private sealed class Pending
        {
            public WWW Www;
            public float Deadline;
            public Action<Response> Done;
            public Response Failed; // set when the request could not even start
        }

        private const float Timeout = 8f;
        private const int MaxPending = 32;
        private static readonly List<Pending> Requests = new List<Pending>();

        public static void Get(string url, Action<Response> done)
        {
            Start(() => new WWW(url), done);
        }

        public static void Post(string url, object body, Action<Response> done)
        {
            byte[] data = Encoding.UTF8.GetBytes(MiniJson.Write(body));
            Start(() => new WWW(url, data, new Dictionary<string, string> { { "Content-Type", "application/json" } }), done);
        }

        private static void Start(Func<WWW> open, Action<Response> done)
        {
            Pending p = new Pending { Deadline = Time.realtimeSinceStartup + Timeout, Done = done };
            try
            {
                p.Www = Requests.Count < MaxPending ? open() : null;
                if (p.Www == null)
                {
                    p.Failed = new Response { Error = "too many requests at once" };
                }
            }
            catch (Exception ex)
            {
                p.Failed = new Response { Error = ex.Message };
            }
            // Answered from Poll even when it failed here, so callers never re-enter themselves.
            Requests.Add(p);
        }

        public static void Poll()
        {
            if (Requests.Count == 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            foreach (Pending p in Requests.Where(r => r.Failed != null || r.Www.isDone || now > r.Deadline).ToList())
            {
                Requests.Remove(p);
                Response response = p.Failed ?? (p.Www.isDone ? Read(p.Www) : new Response { Error = "no answer" });
                if (p.Www != null)
                {
                    p.Www.Dispose();
                }
                try
                {
                    p.Done?.Invoke(response);
                }
                catch (Exception ex)
                {
                    RevivalLog.Error("Master server answer could not be handled", ex);
                }
            }
        }

        private static Response Read(WWW www)
        {
            Response r = new Response();
            try
            {
                r.Status = StatusOf(www);
                r.Json = MiniJson.Parse(www.text) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                r.Json = null;
            }
            if (r.Status == 200 && r.Json == null)
            {
                r.Error = "not a master server answer";
            }
            else if (r.Status != 200)
            {
                string code = r.Json.Str("error");
                r.Error = code.Length > 0 ? code : string.IsNullOrEmpty(www.error) ? "HTTP " + r.Status : www.error;
            }
            return r;
        }

        // "HTTP/1.1 404 Not Found" in Unity's STATUS header; without it, any error counts as no answer.
        private static int StatusOf(WWW www)
        {
            if (www.responseHeaders != null && www.responseHeaders.TryGetValue("STATUS", out string line))
            {
                string[] parts = line.Split(' ');
                if (parts.Length > 1 && int.TryParse(parts[1], out int status))
                {
                    return status;
                }
            }
            return string.IsNullOrEmpty(www.error) ? 200 : 0;
        }
    }
}
