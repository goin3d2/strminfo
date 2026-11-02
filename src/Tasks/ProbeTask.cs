using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace StrmInfo.Tasks
{
    /// <summary>Reads media info for items in the selected libraries that don't have it yet.</summary>
    public class ProbeTask : IScheduledTask
    {
        public string Name => "Extract media info (strminfo)";
        public string Key => "StrmInfoProbe";
        public string Description => "Reads resolution, codecs, audio tracks and runtime for items that don't have them, without refreshing metadata.";
        public string Category => "strminfo";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
            Plugin.Instance.Engine.RunAsync(progress, cancellationToken);

        // No schedule by default: run it from the strminfo page, or add a trigger in Scheduled Tasks.
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new TaskTriggerInfo[0];
    }
}
