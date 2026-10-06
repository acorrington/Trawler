using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Trawler.Configuration;

namespace Trawler;

/// <summary>
/// Trawler plugin identity, dashboard pages and thumbnail.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
{
    public static Plugin Instance { get; private set; }

    public override string Name => "Trawler";

    public override string Description =>
        "Downloads YouTube trailers for your movies and saves them alongside your media so Emby plays them natively.";

    public override Guid Id => new Guid("910C9CE1-C355-48FA-93D5-411EE319D392");

    public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    public Stream GetThumbImage()
    {
        var type = GetType();
        return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.jpg");
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "Trawler",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
            EnableInMainMenu = false
        };
        yield return new PluginPageInfo
        {
            Name = "trawlerjs",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.js"
        };
    }
}
