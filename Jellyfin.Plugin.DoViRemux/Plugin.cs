using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DoViRemux;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Guid OurGuid = Guid.Parse("b3f8e7a4-9c5d-4f2b-8a6e-1d3c9f7a2e4f");
    public override string Name => "DoVi Remux";
    public override Guid Id => OurGuid;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
    {
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return [
            new()
            {
                Name = Name,
                EmbeddedResourcePath = "Jellyfin.Plugin.DoViRemux.Configuration.config.html"
            }
        ];
    }
}