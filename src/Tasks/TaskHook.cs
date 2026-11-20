using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;

namespace StrmInfo.Tasks
{
    /// <summary>Gives the engine a way to queue its task in Emby's task manager once the server
    /// has started, so runs for newly added items show up in Scheduled Tasks like a full run;
    /// and a way to ask for Emby's idle restart when a run paused to keep memory down.</summary>
    public class TaskHook : IServerEntryPoint
    {
        readonly ITaskManager _tasks;
        readonly IServerApplicationHost _app;

        public TaskHook(ITaskManager taskManager, IServerApplicationHost app) { _tasks = taskManager; _app = app; }

        public void Run()
        {
            var engine = Plugin.Instance?.Engine;
            if (engine == null) return;
            engine.QueueTask = () => _tasks.QueueScheduledTask<ProbeTask>();
            engine.RequestRestart = () => _app.NotifyPendingRestart();
            engine.ResumeAfterRestart();
        }

        public void Dispose() { }
    }
}
