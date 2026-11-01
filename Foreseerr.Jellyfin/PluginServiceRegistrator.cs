using Foreseerr.Jellyfin.Sidecar;
using Foreseerr.Jellyfin.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Foreseerr.Jellyfin;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public const string SidecarHttpClient = "foreseerr-sidecar";

    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(SidecarHttpClient, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseCookies = false,
                UseProxy = false,
                AllowAutoRedirect = false,
            });
        serviceCollection.AddSingleton<SidecarLog>();
        serviceCollection.AddSingleton<JellyfinHostBootstrap>();
        serviceCollection.AddSingleton<SidecarSupervisor>();
        serviceCollection.AddSingleton<SidecarSessionService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SidecarSupervisor>());
        serviceCollection.AddHostedService<HeaderButtonRegistration>();
    }
}
