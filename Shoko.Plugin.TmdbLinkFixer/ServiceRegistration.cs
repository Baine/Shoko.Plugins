using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.TmdbLinkFixer.Configuration;
using Shoko.Plugin.TmdbLinkFixer.Services;

namespace Shoko.Plugin.TmdbLinkFixer;

public sealed class ServiceRegistration : IPluginServiceRegistration
{
    public static void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
    {
        TmdbLinkFixerSettingsStore.Initialize(applicationPaths, NullLogger.Instance);
        services.AddHttpClient(TmdbLinkProbe.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Shoko-TMDB-Link-Fixer/0.1");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        services.AddSingleton<TmdbLinkProbe>();
        services.AddSingleton<TmdbLinkFixerService>();
    }
}
