using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StrmInfo.Engine
{
    /// <summary>Recent log lines for the dashboard, also passed on to Emby's own log.</summary>
    public sealed class LogRing
    {
        readonly object _lock = new object();
        readonly LinkedList<(long seq, string level, string text)> _lines = new LinkedList<(long, string, string)>();
        long _seq;
        readonly Func<string, string> _scrub;
        readonly Action<string, string> _forward; // (level, message) to Emby's log

        public LogRing(Func<string, string> scrub, Action<string, string> forward)
        {
            _scrub = scrub; _forward = forward;
        }

        public void Info(string m) => Add("INFO", m);
        public void Warn(string m) => Add("WARNING", m);
        public void Error(string m) => Add("ERROR", m);
        /// <summary>Only to Emby's own log (at debug level), not the dashboard.</summary>
        public void Debug(string m) { try { _forward?.Invoke("DEBUG", _scrub(m ?? "")); } catch { } }

        void Add(string level, string message)
        {
            message = _scrub(message ?? "");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
            lock (_lock)
            {
                _seq++;
                _lines.AddLast((_seq, level, line));
                while (_lines.Count > 2000) _lines.RemoveFirst();
            }
            try { _forward?.Invoke(level, message); } catch { }
        }

        public (List<object> lines, long last) Since(long after, int limit = 1000)
        {
            lock (_lock)
            {
                var o = _lines.Where(x => x.seq > after).ToList();
                if (o.Count > limit) o = o.Skip(o.Count - limit).ToList();
                return (o.Select(x => (object)new List<object> { x.seq, x.level, x.text }).ToList(), _seq);
            }
        }
    }

    /// <summary>Small JSON files in the plugin's data folder, written atomically.</summary>
    public static class Store
    {
        public static object ReadJson(string path)
        {
            try { return File.Exists(path) ? Json.Parse(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        public static void WriteJson(string path, object data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Write(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
