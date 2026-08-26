using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.AniDBWatchHistory.Services;

namespace Shoko.Plugin.AniDBWatchHistory;

public sealed class ServiceRegistration : IPluginServiceRegistration
{
    public static void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
    {
        services.AddSingleton<AniDBMyListParser>();
        services.AddScoped<AniDBWatchImporter>();
    }
}
