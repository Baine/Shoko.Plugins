using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.TmdbLinkFixer.Services;

namespace Shoko.Plugin.TmdbLinkFixer;

public sealed class ServiceRegistration : IPluginServiceRegistration
{
    public static void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
    {
        services.AddHttpClient(TmdbLinkProbe.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://www.themoviedb.org/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Shoko-TMDB-Link-Fixer/0.1");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });

        services.AddSingleton<TmdbLinkProbe>();
        services.AddSingleton<TmdbLinkFixerService>();
    }
}
