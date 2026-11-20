using System;
using System.Diagnostics;
using System.IO;

namespace StrmInfo.Engine
{
    /// <summary>
    /// How much memory Emby is using against the most it may use. Inside a container with a memory
    /// limit that's the container's limit (Docker stops the container when it's reached); otherwise
    /// the machine's memory.
    /// </summary>
    public static class Memory
    {
        /// <summary>(used, limit) in bytes, or null when it can't be read. "Used" leaves out file
        /// cache, which the system can give back at any time.</summary>
        public static (long used, long limit)? Read()
        {
            // For testing the pause: STRMINFO_TEST_MEMORY_PERCENT=90 reports 90% of 10 GB.
            var test = Environment.GetEnvironmentVariable("STRMINFO_TEST_MEMORY_PERCENT");
            if (int.TryParse(test, out var tp) && tp > 0) return (10737418240L * tp / 100, 10737418240L);
            try
            {
                // cgroup v2
                var max = ReadText("/sys/fs/cgroup/memory.max");
                var stat = ReadText("/sys/fs/cgroup/memory.stat");
                if (max != null && stat != null)
                {
                    var anon = Stat(stat, "anon") + Stat(stat, "shmem");
                    if (long.TryParse(max.Trim(), out var lim) && lim > 0 && anon > 0) return (anon, lim);
                    if (anon > 0) return (anon, Total());
                }
                // cgroup v1
                var max1 = ReadText("/sys/fs/cgroup/memory/memory.limit_in_bytes");
                var stat1 = ReadText("/sys/fs/cgroup/memory/memory.stat");
                if (max1 != null && stat1 != null)
                {
                    var rss = Stat(stat1, "total_rss") + Stat(stat1, "total_shmem");
                    if (long.TryParse(max1.Trim(), out var lim1) && lim1 > 0 && lim1 < (1L << 60) && rss > 0) return (rss, lim1);
                    if (rss > 0) return (rss, Total());
                }
            }
            catch { }
            try
            {
                using (var p = Process.GetCurrentProcess()) return (p.WorkingSet64, Total());
            }
            catch { return null; }
        }

        static long Total()
        {
            var mem = ReadText("/proc/meminfo");
            if (mem != null)
                foreach (var line in mem.Split('\n'))
                    if (line.StartsWith("MemTotal:"))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb)) return kb * 1024;
                    }
            return 0;   // unknown: no memory pause
        }

        static long Stat(string text, string key)
        {
            foreach (var line in text.Split('\n'))
            {
                var sp = line.IndexOf(' ');
                if (sp > 0 && line.Substring(0, sp) == key && long.TryParse(line.Substring(sp + 1).Trim(), out var v)) return v;
            }
            return 0;
        }

        static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
        }

        public static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
    }
}
