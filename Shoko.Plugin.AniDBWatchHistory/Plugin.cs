using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;

namespace Shoko.Plugin.AniDBWatchHistory;

public sealed class Plugin : IPlugin
{
    public Guid ID => Guid.Parse("0d89b27b-d4e7-4e60-97ca-696a59f9fd5e");
    public string Name => "AniDB Watch History Import";
    public string Description => "Imports only AniDB MyList records with a valid viewdate into a selected Shoko user.";
    public IReadOnlyList<PluginPage> GetPages() =>
    [
        new()
        {
            Name = "AniDB Watch History",
            Url = "/api/plugin/anidb-watch-history/page",
            CanEmbed = true
        }
    ];
}
