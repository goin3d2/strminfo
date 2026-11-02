using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using StrmInfo.Engine;

namespace StrmInfo
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }
        public ProbeEngine Engine { get; }

        static readonly Guid PluginId = new Guid("3f9b6a1e-5c2d-4e8f-a7b4-91d0c6e2f853");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager,
                      ILibraryManager libraryManager, IItemRepository itemRepository, IMediaProbeManager probeManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            var logger = logManager.GetLogger("strminfo");
            Engine = new ProbeEngine(libraryManager, itemRepository, probeManager, () => Configuration,
                Path.Combine(applicationPaths.DataPath, "strminfo"),
                (level, message) =>
                {
                    // Pass the text as an argument so braces in titles aren't read as format codes.
                    if (level == "ERROR") logger.Error("{0}", message);
                    else if (level == "WARNING") logger.Warn("{0}", message);
                    else if (level == "DEBUG") logger.Debug("{0}", message);
                    else logger.Info("{0}", message);
                });
            libraryManager.ItemAdded += (s, e) =>
            {
                try { Engine.OnItemAdded(e.Item); } catch { }
            };
        }

        public override string Name => "strminfo";
        public override Guid Id => PluginId;
        public override string Description =>
            "Reads media info (resolution, codecs, audio tracks, runtime) for .strm items without " +
            "refreshing metadata, with library scope, concurrency and a live status page.";

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "strminfo", DisplayName = "strminfo",
                EmbeddedResourcePath = GetType().Namespace + ".Web.strminfo.html",
                EnableInMainMenu = true, MenuIcon = "info", IsMainConfigPage = true,
            },
            new PluginPageInfo
            {
                Name = "strminfojs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.strminfo.js",
            },
        };

        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Web.thumb.png");
        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
