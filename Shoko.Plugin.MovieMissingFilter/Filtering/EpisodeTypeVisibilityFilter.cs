using Shoko.Plugin.MovieMissingFilter.Configuration;
using Shoko.Plugin.MovieMissingFilter.Reflection;

namespace Shoko.Plugin.MovieMissingFilter.Filtering;

internal static class EpisodeTypeVisibilityFilter
{
    internal static IReadOnlyList<object> Apply(
        IReadOnlyList<object> items,
        MovieMissingFilterSettings settings,
        out int removedNormal,
        out int removedSpecials,
        out int removedOthers)
    {
        removedNormal = 0;
        removedSpecials = 0;
        removedOthers = 0;

        var result = new List<object>(items.Count);
        foreach (var item in items)
        {
            var aniDbEpisode = ShokoReflection.Get(item, "AniDB_Episode");
            var type = ShokoReflection.GetString(aniDbEpisode, "EpisodeType");

            if (string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase))
            {
                if (!settings.IncludeNormalEpisodes)
                {
                    removedNormal++;
                    continue;
                }
            }
            else if (string.Equals(type, "Special", StringComparison.OrdinalIgnoreCase))
            {
                if (!settings.IncludeSpecials)
                {
                    removedSpecials++;
                    continue;
                }
            }
            else if (string.Equals(type, "Other", StringComparison.OrdinalIgnoreCase))
            {
                if (!settings.IncludeOthers)
                {
                    removedOthers++;
                    continue;
                }
            }

            result.Add(item);
        }

        return result;
    }
}
