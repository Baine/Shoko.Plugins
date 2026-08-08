using Shoko.Abstractions.Plugin;

namespace Shoko.Plugin.NfoGenerator;

/// <summary>
/// Plugin identity.
/// </summary>
public sealed class NfoGeneratorPlugin : IPlugin
{
    public static readonly Guid StaticID = Guid.Parse("5c5482c1-3dd0-49cb-b862-d57e305da353");

    public Guid ID => StaticID;

    public string Name => "NFO Generator";

    public string? Description =>
        "Generates Kodi-style NFO files and artwork sidecars next to your video files whenever a release is matched.";
}
