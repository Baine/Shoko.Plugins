using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.MovieMissingFilter.Configuration;
using Shoko.Plugin.MovieMissingFilter.Patching;

namespace Shoko.Plugin.MovieMissingFilter;

/// <summary>
/// Registers the plugin settings page and applies runtime patches after Shoko's
/// service provider is available and before HTTP requests are served.
/// </summary>
public sealed class MovieMissingFilterApplicationRegistration : IPluginApplicationRegistration
{
    public static void RegisterServices(IApplicationBuilder application, IApplicationPaths applicationPaths)
    {
        var loggerFactory = application.ApplicationServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Shoko.Plugin.MovieMissingFilter");

        MovieMissingFilterSettingsStore.Initialize(applicationPaths, logger);
        SettingsDashboardMiddleware.Register(application, logger);
        logger.LogInformation(
            "[MovieMissingFilter] Settings page registered at {DashboardPath}.",
            SettingsDashboardMiddleware.DashboardPath);
        PatchBootstrap.Apply(logger, application.ApplicationServices);
    }
}
