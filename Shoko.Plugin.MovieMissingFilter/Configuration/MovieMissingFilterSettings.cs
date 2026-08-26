namespace Shoko.Plugin.MovieMissingFilter.Configuration;

/// <summary>
/// User-configurable episode types included in the enhanced Missing Episodes result.
/// Defaults preserve the v0.11 behavior.
/// </summary>
public sealed class MovieMissingFilterSettings
{
    public bool IncludeNormalEpisodes { get; set; } = true;
    public bool IncludeSpecials { get; set; } = true;
    public bool IncludeOthers { get; set; } = true;

    internal MovieMissingFilterSettings Clone()
        => new()
        {
            IncludeNormalEpisodes = IncludeNormalEpisodes,
            IncludeSpecials = IncludeSpecials,
            IncludeOthers = IncludeOthers,
        };
}
