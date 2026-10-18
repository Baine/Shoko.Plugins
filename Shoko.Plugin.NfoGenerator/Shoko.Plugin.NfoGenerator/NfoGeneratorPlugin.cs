using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Plugin.NfoGenerator.Config;

namespace Shoko.Plugin.NfoGenerator;

/// <summary>
/// Plugin identity, declaring <see cref="NfoGeneratorSettings"/> as the
/// plugin's main configuration so the API reports it alongside the plugin.
/// </summary>
public sealed class NfoGeneratorPlugin : IPlugin<NfoGeneratorSettings>
{
    public static readonly Guid StaticID = Guid.Parse("5c5482c1-3dd0-49cb-b862-d57e305da353");

    public Guid ID => StaticID;

    public string Name => "NFO Generator";

    public string? Description =>
        "Generates Kodi-style NFO files and artwork sidecars next to your video files whenever a release is matched.";

    /// <summary>
    /// Exposes the settings page to the WebUI, embedded under Settings → Plugins.
    /// </summary>
    public IReadOnlyList<PluginPage> GetPages() =>
        [
            new() { Name = "Settings", Url = "/api/plugin/NfoGenerator/settings" },
        ];
}
