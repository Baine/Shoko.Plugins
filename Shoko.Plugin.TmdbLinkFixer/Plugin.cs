using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;

namespace Shoko.Plugin.TmdbLinkFixer;

public sealed class Plugin : IPlugin
{
    public Guid ID => Guid.Parse("d346ad56-9ec6-47ca-a6dc-476b8d9fbab8");
    public string Name => "TMDB Link Fixer";
    public string Description => "Checks TMDB links, compares possible replacements, and applies only replacements explicitly confirmed by an administrator.";

    public IReadOnlyList<PluginPage> GetPages() =>
    [
        new()
        {
            Name = "TMDB Link Fixer",
            Url = "/api/plugin/tmdb-link-fixer/page",
            CanEmbed = true,
        },
    ];
}
