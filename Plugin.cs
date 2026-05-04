using Jellyfin.Plugin.LanguageAwareImages.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LanguageAwareImages;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Language-Aware Images";

    public override Guid Id => Guid.Parse("08d643db-becc-42e6-aef9-6c677036ddd5");

    public override string Description =>
        "TMDB images in your library's language with a clean fallback to English, skips the textless posters the built-in provider falls back to.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    };
}
