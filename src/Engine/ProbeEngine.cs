using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmInfo.Engine
{
    /// <summary>
    /// Reads media info (streams, resolution, codecs, runtime) for items that don't have it,
    /// by running Emby's own probe directly and saving only the results. No metadata
    /// providers, image fetchers or item refreshes are involved.
    /// </summary>
    public sealed class ProbeEngine
    {
        static readonly Regex UrlRe = new Regex(@"\b([a-z][a-z0-9+.-]*://[^/\s""']+)[^\s""']*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        readonly ILibraryManager _lib;
        readonly IItemRepository _repo;
        readonly IMediaProbeManager _probe;
        readonly Func<PluginConfiguration> _config;
        readonly string _dataDir;
        public readonly LogRing Log;

        readonly SemaphoreSlim _running = new SemaphoreSlim(1, 1);
        readonly object _lock = new object();
        CancellationTokenSource _runCts;

        // live status
        string _phase = "idle", _scope = "", _stopReason;
        long _done, _total, _ok, _failed, _timedOut, _skipped;
        long? _started;
        readonly Dictionary<long, string> _current = new Dictionary<long, string>();
        readonly LinkedList<Dictionary<string, object>> _recent = new LinkedList<Dictionary<string, object>>();
        Dictionary<string, object> _last;
        Dictionary<string, object> _counts; // library id -> {total, pending, failed}
        long? _countedAt;
        bool _counting;

        // failures: item id -> {t, n, r, name}
        readonly Dictionary<string, object> _failures;
        readonly HashSet<long> _newItems = new HashSet<long>();
        Timer _newTimer;

        public ProbeEngine(ILibraryManager lib, IItemRepository repo, IMediaProbeManager probe,
                           Func<PluginConfiguration> config, string dataDir, Action<string, string> forwardLog)
        {
            _lib = lib; _repo = repo; _probe = probe; _config = config; _dataDir = dataDir;
            Log = new LogRing(Scrub, forwardLog);
            Directory.CreateDirectory(dataDir);
            _failures = Store.ReadJson(P("failures.json")) as Dictionary<string, object> ?? new Dictionary<string, object>();
            _last = Store.ReadJson(P("last.json")) as Dictionary<string, object>;
        }

        string P(string name) => Path.Combine(_dataDir, name);
        public bool IsRunning => _running.CurrentCount == 0;

        /// <summary>Hide stream URLs (they usually carry the provider login): keep scheme and host only.</summary>
        public static string Scrub(string text) => string.IsNullOrEmpty(text) ? text ?? "" : UrlRe.Replace(text, "$1/…");

        // ---- libraries ------------------------------------------------------------------

        public sealed class Library
        {
            public long Id; public string Key, Name, CollectionType; public string[] Locations;
        }

        public List<Library> Libraries()
        {
            var o = new List<Library>();
            foreach (var f in _lib.GetVirtualFolders())
            {
                long id = 0;
                if (!long.TryParse(f.ItemId, out id) && Guid.TryParse(f.ItemId, out var g)) id = _lib.GetItemById(g)?.InternalId ?? 0;
                if (id == 0) continue;
                o.Add(new Library { Id = id, Key = f.ItemId, Name = f.Name, CollectionType = f.CollectionType ?? "", Locations = f.Locations ?? new string[0] });
            }
            return o;
        }

        List<Library> Selected(PluginConfiguration c)
        {
            var keys = new HashSet<string>((c.LibraryIds ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
            return Libraries().Where(l => keys.Contains(l.Key) || keys.Contains(l.Id.ToString())).ToList();
        }

        static string[] Types(PluginConfiguration c)
        {
            var t = new List<string>();
            if (c.IncludeMovies) t.Add("Movie");
            if (c.IncludeEpisodes) t.Add("Episode");
            if (c.IncludeOtherVideos) { t.Add("Video"); t.Add("MusicVideo"); }
            return t.ToArray();
        }

        InternalItemsQuery Query(PluginConfiguration c, long libraryId, bool withWidthOnly) => new InternalItemsQuery
        {
            Recursive = true,
            IsVirtualItem = false,
            HasPath = true,
            IncludeItemTypes = Types(c),
            AncestorIds = new[] { libraryId },
            MinWidth = withWidthOnly ? 1 : (int?)null,
            OrderBy = new[] { ("DateCreated", c.NewestFirst ? SortOrder.Descending : SortOrder.Ascending) },
        };

        /// <summary>Items in a library with no media info yet, in run order. Probed videos have a
        /// width; anything without one is a candidate (each is checked again before probing).</summary>
        long[] Missing(PluginConfiguration c, long libraryId)
        {
            var have = new HashSet<long>(_lib.GetInternalItemIds(Query(c, libraryId, true)));
            return _lib.GetInternalItemIds(Query(c, libraryId, false)).Where(id => !have.Contains(id)).ToArray();
        }

        bool RecentlyFailed(long id, PluginConfiguration c, long now)
        {
            lock (_failures)
            {
                if (!(Json.Get(_failures, id.ToString()) is Dictionary<string, object> f)) return false;
                if (c.RetryFailedAfterDays <= 0) return false;
                return now - ToLong(Json.Get(f, "t")) < c.RetryFailedAfterDays * 86400L;
            }
        }

        static long ToLong(object v) => long.TryParse(Json.S(v), out var n) ? n : 0;

        // ---- counting ---------------------------------------------------------------------

        /// <summary>How many items each library has, and how many still need media info.</summary>
        public void Count()
        {
            lock (_lock) { if (_counting) return; _counting = true; }
            Task.Run(() =>
            {
                try
                {
                    var c = _config();
                    var now = Store.Now();
                    var counts = new Dictionary<string, object>();
                    foreach (var lib in Libraries())
                    {
                        if (Types(c).Length == 0) break;
                        var all = _lib.GetInternalItemIds(Query(c, lib.Id, false)).Length;
                        var missing = Missing(c, lib.Id);
                        var failed = missing.Count(id => RecentlyFailed(id, c, now));
                        counts[lib.Key] = new Dictionary<string, object>
                        {
                            ["total"] = (long)all, ["pending"] = (long)(missing.Length - failed), ["failed"] = (long)failed,
                        };
                    }
                    lock (_lock) { _counts = counts; _countedAt = Store.Now(); }
                }
                catch (Exception e) { Log.Error("Counting failed: " + e.Message); }
                finally { lock (_lock) _counting = false; }
            });
        }

        // ---- runs ---------------------------------------------------------------------------

        public void Stop()
        {
            lock (_lock) { _stopReason = "stopped from the page"; _runCts?.Cancel(); }
        }

        /// <summary>Probe what needs probing in the selected libraries, or just <paramref name="onlyIds"/>.</summary>
        public async Task RunAsync(IProgress<double> progress, CancellationToken ct, ICollection<long> onlyIds = null)
        {
            if (!await _running.WaitAsync(0).ConfigureAwait(false))
            {
                Log.Info("A run is already going");
                return;
            }
            var c = _config();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_lock)
            {
                _runCts = cts; _stopReason = null; _phase = "listing"; _scope = onlyIds != null ? "new items" : "libraries";
                _done = _total = _ok = _failed = _timedOut = _skipped = 0; _started = Store.Now(); _current.Clear();
            }
            var runStart = DateTime.UtcNow;
            try
            {
                var now = Store.Now();
                var ids = new List<long>();
                if (onlyIds != null) ids.AddRange(onlyIds);
                else
                {
                    var libs = Selected(c);
                    if (libs.Count == 0) { Log.Warn("No libraries selected. Pick them on the Libraries tab."); _stopReason = "no libraries selected"; return; }
                    if (Types(c).Length == 0) { Log.Warn("No item types selected on the Settings tab."); _stopReason = "no item types selected"; return; }
                    var seen = new HashSet<long>();
                    foreach (var lib in libs)
                    {
                        var found = Missing(c, lib.Id);
                        int fresh = 0, held = 0;
                        foreach (var id in found)
                        {
                            if (!seen.Add(id)) continue;
                            if (RecentlyFailed(id, c, now)) { held++; continue; }
                            ids.Add(id); fresh++;
                        }
                        Log.Info($"{lib.Name}: {fresh} items need media info" + (held > 0 ? $" ({held} failed recently, retried after {c.RetryFailedAfterDays} days)" : ""));
                    }
                }
                if (c.MaxItemsPerRun > 0 && ids.Count > c.MaxItemsPerRun) ids = ids.Take(c.MaxItemsPerRun).ToList();
                lock (_lock) { _total = ids.Count; _phase = "probing"; }
                if (ids.Count == 0) { Log.Info("Nothing to probe"); return; }

                var workers = Math.Max(1, Math.Min(10, c.Concurrency));
                var timeout = TimeSpan.FromSeconds(Math.Max(5, c.TimeoutSeconds));
                Log.Info($"Probing {ids.Count} items, {workers} at a time, {timeout.TotalSeconds:0}s timeout");
                var queue = new ConcurrentQueue<long>(ids);
                var deadline = c.MaxRunMinutes > 0 ? runStart.AddMinutes(c.MaxRunMinutes) : (DateTime?)null;
                int streak = 0;
                var lastLog = DateTime.UtcNow;

                async Task Worker()
                {
                    while (!cts.IsCancellationRequested && queue.TryDequeue(out var id))
                    {
                        if (deadline.HasValue && DateTime.UtcNow > deadline.Value)
                        {
                            lock (_lock) _stopReason = $"reached the {c.MaxRunMinutes}-minute limit";
                            cts.Cancel();
                            return;
                        }
                        while (c.PauseDuringLibraryScan && _lib.IsScanRunning && !cts.IsCancellationRequested)
                        {
                            lock (_lock) _phase = "waiting for library scan";
                            await Task.Delay(5000, cts.Token).ContinueWith(_ => { }).ConfigureAwait(false);
                        }
                        lock (_lock) _phase = "probing";
                        if (cts.IsCancellationRequested) return;

                        var (result, name, detail) = await ProbeOne(id, c, timeout, cts.Token).ConfigureAwait(false);
                        if (result == "cancelled") return;
                        bool stopNow = false;
                        lock (_lock)
                        {
                            _current.Remove(id);
                            _done++;
                            switch (result)
                            {
                                case "ok": _ok++; streak = 0; break;
                                case "skipped": _skipped++; break;
                                case "timeout": _timedOut++; streak++; break;
                                default: _failed++; streak++; break;
                            }
                            if (result != "skipped")
                            {
                                _recent.AddFirst(new Dictionary<string, object>
                                {
                                    ["time"] = Store.Now(), ["id"] = id, ["name"] = name, ["result"] = result, ["detail"] = detail,
                                });
                                while (_recent.Count > 60) _recent.RemoveLast();
                            }
                            if (c.StopAfterFailuresInRow > 0 && streak >= c.StopAfterFailuresInRow && _stopReason == null)
                            {
                                _stopReason = $"{streak} probes in a row failed; the provider may be down or at its connection limit";
                                stopNow = true;
                            }
                            progress?.Report(_total > 0 ? 100.0 * _done / _total : 100);
                        }
                        if (result == "ok" || result == "failed" || result == "timeout") RecordResult(id, name, result, detail);
                        if (_done % 100 == 0) SaveFailures();
                        if (stopNow) { Log.Warn("Stopping: " + _stopReason); cts.Cancel(); return; }
                        if ((DateTime.UtcNow - lastLog).TotalSeconds >= 60)
                        {
                            lastLog = DateTime.UtcNow;
                            Log.Info($"Progress: {_done}/{_total}, {_ok} ok, {_failed} failed, {_timedOut} timed out, {_skipped} already had media info");
                        }
                        if (c.DelayMs > 0 && result != "skipped")
                            await Task.Delay(c.DelayMs, cts.Token).ContinueWith(_ => { }).ConfigureAwait(false);
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker())).ConfigureAwait(false);
                if (ct.IsCancellationRequested && _stopReason == null) _stopReason = "cancelled";
            }
            catch (Exception e)
            {
                Log.Error("Run failed: " + Scrub(e.Message));
                lock (_lock) _stopReason = "error: " + Scrub(e.Message);
            }
            finally
            {
                SaveFailures();
                lock (_lock)
                {
                    var secs = (long)(DateTime.UtcNow - runStart).TotalSeconds;
                    _last = new Dictionary<string, object>
                    {
                        ["time"] = Store.Now(), ["scope"] = _scope, ["seconds"] = secs, ["total"] = _total, ["done"] = _done,
                        ["ok"] = _ok, ["failed"] = _failed, ["timed_out"] = _timedOut, ["skipped"] = _skipped,
                        ["stopped"] = _stopReason,
                    };
                    _phase = "idle"; _current.Clear(); _runCts = null;
                    Log.Info($"Run finished in {secs}s: {_ok} ok, {_failed} failed, {_timedOut} timed out, {_skipped} already had media info"
                             + (_stopReason != null ? $" (stopped: {_stopReason})" : ""));
                }
                try { Store.WriteJson(P("last.json"), _last); } catch { }
                cts.Dispose();
                _running.Release();
            }
            ct.ThrowIfCancellationRequested();
        }

        static string ReadStrm(string path)
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length > 0 && !line.StartsWith("#")) return line;
            }
            return "";
        }

        static MediaProtocol ProtocolOf(string url)
        {
            var i = url.IndexOf("://", StringComparison.Ordinal);
            if (i <= 0) return MediaProtocol.File;
            switch (url.Substring(0, i).ToLowerInvariant())
            {
                case "http": case "https": return MediaProtocol.Http;
                case "rtmp": case "rtmps": return MediaProtocol.Rtmp;
                case "rtsp": case "rtsps": return MediaProtocol.Rtsp;
                case "udp": return MediaProtocol.Udp;
                case "rtp": return MediaProtocol.Rtp;
                case "ftp": return MediaProtocol.Ftp;
                case "mms": return MediaProtocol.Mms;
                default: return MediaProtocol.Http;
            }
        }

        static string Describe(MediaInfo info)
        {
            var parts = new List<string>();
            var v = info.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (v != null) parts.Add($"{(v.Codec ?? "video").ToUpperInvariant()} {v.Width}x{v.Height}");
            var audio = info.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
            if (audio.Count > 0)
            {
                var a = audio[0];
                parts.Add($"{(a.Codec ?? "audio").ToUpperInvariant()} {a.Channels}ch" + (audio.Count > 1 ? $" (+{audio.Count - 1})" : ""));
            }
            var subs = info.MediaStreams.Count(s => s.Type == MediaStreamType.Subtitle);
            if (subs > 0) parts.Add($"{subs} subtitle{(subs > 1 ? "s" : "")}");
            if (info.RunTimeTicks > 0) parts.Add(TimeSpan.FromTicks(info.RunTimeTicks.Value).ToString(@"h\:mm\:ss"));
            return string.Join(", ", parts);
        }

        async Task<(string result, string name, string detail)> ProbeOne(long id, PluginConfiguration c, TimeSpan timeout, CancellationToken ct)
        {
            BaseItem item;
            try { item = _lib.GetItemById(id); }
            catch (Exception e) { return ("failed", id.ToString(), "couldn't load the item: " + e.Message); }
            if (item == null || item.IsVirtualItem) return ("skipped", id.ToString(), "item no longer exists");
            var name = item is MediaBrowser.Controller.Entities.TV.Episode ep && !string.IsNullOrEmpty(ep.SeriesName)
                ? $"{ep.SeriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}" : item.Name;
            if (c.StrmOnly && !item.IsShortcut) return ("skipped", name, "not a .strm item");
            if (item.GetMediaStreams().Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio))
                return ("skipped", name, "already has media info");

            string url;
            try { url = item.IsShortcut ? ReadStrm(item.Path) : item.Path; }
            catch (Exception e) { return ("failed", name, "couldn't read the .strm file: " + e.Message); }
            if (string.IsNullOrWhiteSpace(url)) return ("failed", name, "the .strm file is empty");

            var protocol = ProtocolOf(url);
            var source = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"), Path = url, Protocol = protocol, IsRemote = protocol != MediaProtocol.File,
                Type = MediaSourceType.Default, SupportsProbing = true, ItemId = item.Id.ToString("N"),
            };
            if (protocol == MediaProtocol.Http && !string.IsNullOrWhiteSpace(c.UserAgent))
                source.RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = c.UserAgent.Trim() };

            lock (_lock) _current[id] = name;
            MediaInfo info;
            using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                probeCts.CancelAfter(timeout);
                try
                {
                    info = await _probe.GetMediaInfo(new MediaInfoRequest
                    {
                        MediaSource = source, MediaType = DlnaProfileType.Video, ExtractChapters = c.ExtractChapters,
                    }, probeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { lock (_lock) _current.Remove(id); return ("cancelled", name, ""); }
                catch (OperationCanceledException) { return ("timeout", name, $"no answer within {timeout.TotalSeconds:0}s"); }
                catch (Exception e) { return ("failed", name, Friendly(e.InnerException?.Message ?? e.Message)); }
            }

            var streams = info?.MediaStreams ?? new List<MediaStream>();
            if (!streams.Any(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio))
                return ("failed", name, "the stream had no audio or video (the provider may have sent an error page)");

            try
            {
                _repo.SaveMediaStreams(item.InternalId, streams, CancellationToken.None);
                if (c.ExtractChapters && info.Chapters != null && info.Chapters.Length > 0)
                    _repo.SaveChapters(item.InternalId, info.Chapters.ToList());
                if (info.RunTimeTicks > 0) item.RunTimeTicks = info.RunTimeTicks;
                if (info.Bitrate > 0) item.TotalBitrate = info.Bitrate.Value;
                var v = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                if (v?.Width > 0) { item.Width = v.Width.Value; item.Height = v.Height ?? 0; }
                item.UpdateToRepository(ItemUpdateType.MetadataImport);
            }
            catch (Exception e) { return ("failed", name, "couldn't save the results: " + FirstLine(e.Message)); }
            return ("ok", name, Describe(info));
        }

        /// <summary>A short reason instead of ffprobe's whole command line.</summary>
        static string Friendly(string message)
        {
            var m = message ?? "";
            if (m.IndexOf("both null", StringComparison.OrdinalIgnoreCase) >= 0)
                return "no audio or video in the response (the provider may have sent an error page or refused the stream)";
            if (Regex.IsMatch(m, @"\b(403|Forbidden)\b")) return "the provider refused the stream (403)";
            if (Regex.IsMatch(m, @"\b(404|Not Found)\b")) return "the provider says the stream doesn't exist (404)";
            if (Regex.IsMatch(m, @"Connection refused|timed out|Network is unreachable|resolve", RegexOptions.IgnoreCase))
                return "couldn't connect to the provider";
            if (m.IndexOf("Error running ffprobe", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ffprobe couldn't read the stream";
            return FirstLine(Scrub(m));
        }

        static string FirstLine(string s)
        {
            s = (s ?? "").Trim();
            var i = s.IndexOf('\n');
            return i > 0 ? s.Substring(0, i).Trim() : s;
        }

        void RecordResult(long id, string name, string result, string detail)
        {
            var key = id.ToString();
            lock (_failures)
            {
                if (result == "ok") { _failures.Remove(key); return; }
                var n = ToLong(Json.Get(Json.Get(_failures, key), "n")) + 1;
                _failures[key] = new Dictionary<string, object>
                {
                    ["t"] = Store.Now(), ["n"] = n, ["r"] = result == "timeout" ? "timeout: " + detail : detail, ["name"] = name,
                };
            }
        }

        void SaveFailures()
        {
            try { lock (_failures) Store.WriteJson(P("failures.json"), _failures); } catch { }
        }

        public List<object> Failures(int limit = 500)
        {
            lock (_failures)
                return _failures.OrderByDescending(kv => ToLong(Json.Get(kv.Value, "t"))).Take(limit)
                    .Select(kv => (object)new Dictionary<string, object>
                    {
                        ["id"] = kv.Key, ["time"] = Json.Get(kv.Value, "t"), ["count"] = Json.Get(kv.Value, "n"),
                        ["reason"] = Json.Get(kv.Value, "r"), ["name"] = Json.Get(kv.Value, "name"),
                    }).ToList();
        }

        public int FailureCount { get { lock (_failures) return _failures.Count; } }

        public void ClearFailures()
        {
            lock (_failures) _failures.Clear();
            SaveFailures();
            Log.Info("Failed items cleared; they'll be tried again at the next run");
        }

        // ---- new items -----------------------------------------------------------------------

        public void OnItemAdded(BaseItem item)
        {
            var c = _config();
            if (!c.ProbeNewItems || item == null || item.IsVirtualItem || !(item is Video)) return;
            if (c.StrmOnly && !item.IsShortcut) return;
            var type = item.GetType().Name;
            if (!(type == "Movie" && c.IncludeMovies || type == "Episode" && c.IncludeEpisodes
                  || (type == "Video" || type == "MusicVideo") && c.IncludeOtherVideos)) return;
            var path = item.Path ?? "";
            if (!Selected(c).Any(l => l.Locations.Any(loc => path.StartsWith(loc.TrimEnd('/', '\\') + "/", StringComparison.Ordinal)
                                                          || path.StartsWith(loc.TrimEnd('/', '\\') + "\\", StringComparison.Ordinal)))) return;
            lock (_newItems) _newItems.Add(item.InternalId);
            if (_newTimer == null) _newTimer = new Timer(_ => ProbeQueuedNewItems(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        void ProbeQueuedNewItems()
        {
            if (IsRunning) return;
            var c = _config();
            if (c.PauseDuringLibraryScan && _lib.IsScanRunning) return;
            List<long> ids;
            lock (_newItems) { if (_newItems.Count == 0) return; ids = _newItems.ToList(); _newItems.Clear(); }
            Log.Info($"Probing {ids.Count} newly added items");
            Task.Run(async () =>
            {
                try { await RunAsync(null, CancellationToken.None, ids).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception e) { Log.Error("New-item run failed: " + Scrub(e.Message)); }
            });
        }

        public int NewItemsWaiting { get { lock (_newItems) return _newItems.Count; } }

        // ---- status for the page -----------------------------------------------------------------

        public Dictionary<string, object> StatusJson()
        {
            lock (_lock)
            {
                var elapsed = _started.HasValue ? Store.Now() - _started.Value : 0;
                long? eta = null;
                var worked = _done - _skipped;
                if (_phase == "probing" && worked > 0 && _total > _done) eta = (long)((double)elapsed / Math.Max(1, _done) * (_total - _done));
                return new Dictionary<string, object>
                {
                    ["now"] = Store.Now(), ["running"] = IsRunning, ["phase"] = _phase, ["scope"] = _scope,
                    ["started"] = _started, ["total"] = _total, ["done"] = _done, ["ok"] = _ok, ["failed"] = _failed,
                    ["timed_out"] = _timedOut, ["skipped"] = _skipped, ["eta"] = eta, ["stopping"] = _stopReason,
                    ["current"] = _current.Values.Cast<object>().ToList(), ["recent"] = _recent.Cast<object>().ToList(),
                    ["last"] = _last, ["counts"] = _counts, ["counted_at"] = _countedAt, ["counting"] = _counting,
                    ["failures"] = (long)FailureCount, ["new_waiting"] = (long)NewItemsWaiting, ["scan_running"] = _lib.IsScanRunning,
                };
            }
        }
    }
}
