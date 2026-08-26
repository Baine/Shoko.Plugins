using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Plugin.MovieMissingFilter.Configuration;

namespace Shoko.Plugin.MovieMissingFilter;

/// <summary>
/// Metadata entry point for the plugin.
/// The runtime patch itself is applied by MovieMissingFilterApplicationRegistration.
/// </summary>
public sealed class MovieMissingFilterPlugin : IPlugin
{
    private static readonly Guid PluginGuid = new("4e525337-8c50-4bd2-a8ec-feb3f202d9f7");

    public Guid ID => PluginGuid;
    public string Name => "Movie Missing Filter";
    public string? Description => "Configurable Missing Episodes enhancement for E/S/O episode types plus alternate movie-layout suppression.";

    public IReadOnlyList<PluginPage> GetPages()
        =>
        [
            new PluginPage
            {
                Name = "Settings",
                Url = SettingsDashboardMiddleware.DashboardPath,
                CanEmbed = true,
            },
        ];

    public MovieMissingFilterPlugin()
    {
    }
}
