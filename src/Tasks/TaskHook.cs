using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;

namespace StrmInfo.Tasks
{
    /// <summary>Gives the engine a way to queue its task in Emby's task manager once the server
    /// has started, so runs for newly added items show up in Scheduled Tasks like a full run.</summary>
    public class TaskHook : IServerEntryPoint
    {
        readonly ITaskManager _tasks;

        public TaskHook(ITaskManager taskManager) { _tasks = taskManager; }

        public void Run()
        {
            var engine = Plugin.Instance?.Engine;
            if (engine != null) engine.QueueTask = () => _tasks.QueueScheduledTask<ProbeTask>();
        }

        public void Dispose() { }
    }
}
