using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using StrmInfo.Engine;
using StrmInfo.Tasks;

namespace StrmInfo.Api
{
    [Route("/strminfo/status", "GET", Summary = "Run progress and recent results")]
    [Authenticated(Roles = "admin")]
    public class GetStatus : IReturn<object> { }

    [Route("/strminfo/log", "GET", Summary = "Recent log lines")]
    [Authenticated(Roles = "admin")]
    public class GetLog : IReturn<object> { public long After { get; set; } }

    [Route("/strminfo/libraries", "GET", Summary = "Emby libraries")]
    [Authenticated(Roles = "admin")]
    public class GetLibraries : IReturn<object> { }

    [Route("/strminfo/count", "POST", Summary = "Count items that still need media info")]
    [Authenticated(Roles = "admin")]
    public class StartCount : IReturn<object> { }

    [Route("/strminfo/run", "POST", Summary = "Start a run")]
    [Authenticated(Roles = "admin")]
    public class StartRun : IReturn<object> { }

    [Route("/strminfo/stop", "POST", Summary = "Stop the current run")]
    [Authenticated(Roles = "admin")]
    public class StopRun : IReturn<object> { }

    [Route("/strminfo/failures", "GET", Summary = "Items whose probe failed")]
    [Authenticated(Roles = "admin")]
    public class GetFailures : IReturn<object> { }

    [Route("/strminfo/failures/clear", "POST", Summary = "Forget failed items so they're tried again")]
    [Authenticated(Roles = "admin")]
    public class ClearFailures : IReturn<object> { }

    public class StrmInfoApi : IService, IRequiresRequest
    {
        readonly ITaskManager _tasks;
        readonly IHttpResultFactory _results;
        public IRequest Request { get; set; }

        public StrmInfoApi(ITaskManager taskManager, IHttpResultFactory resultFactory)
        {
            _tasks = taskManager; _results = resultFactory;
        }

        static ProbeEngine Engine => Plugin.Instance.Engine;

        object JsonResult(object value) =>
            _results.GetResult(Request, Json.Write(value).AsSpan(), "application/json", new Dictionary<string, string>());

        IScheduledTaskWorker Worker() => _tasks.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is ProbeTask);

        public object Get(GetStatus request)
        {
            var s = Engine.StatusJson();
            var w = Worker();
            if (w != null)
            {
                s["task_running"] = w.State == TaskState.Running;
                s["has_schedule"] = w.Triggers != null && w.Triggers.Length > 0;
            }
            return JsonResult(s);
        }

        public object Get(GetLog request)
        {
            var (lines, last) = Engine.Log.Since(request.After);
            return JsonResult(new Dictionary<string, object> { ["last"] = last, ["lines"] = lines });
        }

        public object Get(GetLibraries request) =>
            JsonResult(Engine.Libraries().Select(l => (object)new Dictionary<string, object>
            {
                ["id"] = l.Key, ["name"] = l.Name, ["type"] = l.CollectionType, ["locations"] = l.Locations.Cast<object>().ToList(),
            }).ToList());

        public object Post(StartCount request)
        {
            Engine.Count();
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }

        public object Post(StartRun request)
        {
            if (Engine.IsRunning) return JsonResult(new Dictionary<string, object> { ["ok"] = false, ["error"] = "A run is already going." });
            _tasks.QueueScheduledTask<ProbeTask>();
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }

        public object Post(StopRun request)
        {
            Engine.Stop();
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }

        public object Get(GetFailures request) => JsonResult(Engine.Failures());

        public object Post(ClearFailures request)
        {
            Engine.ClearFailures();
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }
    }
}
