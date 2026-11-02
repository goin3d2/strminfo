using MediaBrowser.Model.Plugins;

namespace StrmInfo
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Comma-separated Emby library ids in scope. Empty = none selected yet.</summary>
        public string LibraryIds { get; set; } = "";

        public bool IncludeMovies { get; set; } = true;
        public bool IncludeEpisodes { get; set; } = true;
        /// <summary>Also other videos (music videos, home videos) in the selected libraries.</summary>
        public bool IncludeOtherVideos { get; set; }

        /// <summary>Only .strm items. Off = any video in scope without media info.</summary>
        public bool StrmOnly { get; set; } = true;

        /// <summary>Probes at the same time. Keep within the provider's connection limit.</summary>
        public int Concurrency { get; set; } = 1;
        /// <summary>Give up on a stream after this long (the probe is stopped).</summary>
        public int TimeoutSeconds { get; set; } = 30;
        /// <summary>Pause after each probe, per worker.</summary>
        public int DelayMs { get; set; } = 500;
        /// <summary>User agent sent with the probe (some providers only accept known players).</summary>
        public string UserAgent { get; set; } = "VLC/3.0.20 LibVLC/3.0.20";

        /// <summary>Newest items first (otherwise oldest first).</summary>
        public bool NewestFirst { get; set; } = true;
        /// <summary>Try a failed item again after this many days. 0 = every run.</summary>
        public int RetryFailedAfterDays { get; set; } = 7;
        /// <summary>Stop a run after this many items. 0 = no limit.</summary>
        public int MaxItemsPerRun { get; set; }
        /// <summary>Stop a run after this many minutes. 0 = no limit.</summary>
        public int MaxRunMinutes { get; set; }
        /// <summary>Stop a run when this many probes in a row fail (provider down or limit hit). 0 = never.</summary>
        public int StopAfterFailuresInRow { get; set; } = 25;

        /// <summary>Probe items soon after Emby adds them to a selected library.</summary>
        public bool ProbeNewItems { get; set; }
        /// <summary>Also read chapter markers.</summary>
        public bool ExtractChapters { get; set; }
        /// <summary>Don't start a run while a library scan is going (and pause between items while one runs).</summary>
        public bool PauseDuringLibraryScan { get; set; }
    }
}
