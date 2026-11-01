using Foreseerr.Jellyfin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Foreseerr.Jellyfin;

public class ForeseerrPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static ForeseerrPlugin? Instance { get; private set; }

    public const string PluginGuid = "a7c3e2f1-9b4d-4e8a-8c1f-2b6d9e0f4a11";

    public ForeseerrPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DropStoredSecrets();
    }

    /// <summary>
    /// Earlier plugin versions kept the sidecar secret and the Jellyfin API
    /// key in the settings file. Saving once rewrites it with the settings only.
    /// </summary>
    private void DropStoredSecrets()
    {
        try
        {
            if (!File.Exists(ConfigurationFilePath)) return;
            var xml = File.ReadAllText(ConfigurationFilePath);
            if (xml.Contains("<PluginSecret", StringComparison.Ordinal) || xml.Contains("<ApiKeyToken", StringComparison.Ordinal))
                SaveConfiguration();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A constructor must not throw, or Jellyfin disables the plugin. The next save rewrites the file.
        }
    }

    public override string Name => "Foreseerr";

    public override string Description =>
        "Runs Foreseerr as a localhost sidecar and opens it from Jellyfin Web with SSO.";

    public override Guid Id => Guid.Parse(PluginGuid);

    /// <summary>
    /// Validates the page's values before Jellyfin saves them. Jellyfin then
    /// raises <see cref="BasePlugin{T}.ConfigurationChanged"/>, which the
    /// supervisor uses to restart Foreseerr when a setting it reads changed.
    /// </summary>
    /// <exception cref="ArgumentException">A value is invalid; Jellyfin answers 400.</exception>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration incoming)
            throw new ArgumentException("Unexpected configuration type.", nameof(configuration));
        incoming.Validate();
        base.UpdateConfiguration(incoming);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "visibility",
            },
            new PluginPageInfo
            {
                // The page's controller, loaded through data-controller="__plugin/ForeseerrConfigPage.js".
                Name = "ForeseerrConfigPage.js",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js",
            },
        ];
    }
}
