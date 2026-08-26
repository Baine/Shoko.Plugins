using System.Reflection;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.MovieMissingFilter.Configuration;
using Shoko.Plugin.MovieMissingFilter.Reflection;

namespace Shoko.Plugin.MovieMissingFilter.Filtering;

/// <summary>
/// Adds aired AniDB Special (S) and Other (O) episodes without files to
/// Shoko's normal Missing Episodes result. Shoko's stock GetMissing SQL only
/// selects EpisodeType.Episode, so these types never reach that API otherwise.
///
/// Collecting mode is deliberately left untouched because Shoko's collecting
/// query is release-group specific and explicitly operates on normal episodes.
/// </summary>
internal static class AdditionalEpisodeMissingAugmenter
{
    internal readonly record struct AugmentResult(
        IReadOnlyList<object> Items,
        int AddedSpecialCount,
        int AddedOtherCount)
    {
        internal int AddedCount => AddedSpecialCount + AddedOtherCount;
    }

    internal static AugmentResult Augment(
        object repository,
        IReadOnlyList<object> originalItems,
        bool collecting,
        int? animeID,
        ILogger? logger)
    {
        if (collecting)
            return new AugmentResult(originalItems, 0, 0);

        var settings = MovieMissingFilterSettingsStore.Current;
        if (!settings.IncludeSpecials && !settings.IncludeOthers)
            return new AugmentResult(originalItems, 0, 0);

        var getAll = repository.GetType().GetMethod(
            "GetAll",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (getAll is null)
            return new AugmentResult(originalItems, 0, 0);

        List<object> allEpisodes;
        try
        {
            allEpisodes = ShokoReflection.Enumerate(getAll.Invoke(repository, null)).ToList();
        }
        catch
        {
            // Daily/dev signature changed. Fail open and keep Shoko's stock result.
            return new AugmentResult(originalItems, 0, 0);
        }

        var existingIds = originalItems
            .Select(item => ShokoReflection.GetInt(item, "AnimeEpisodeID"))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var added = new List<object>();
        var addedSpecials = 0;
        var addedOthers = 0;

        foreach (var episode in allEpisodes)
        {
            var localId = ShokoReflection.GetInt(episode, "AnimeEpisodeID");
            if (!localId.HasValue || existingIds.Contains(localId.Value))
                continue;

            if (ShokoReflection.Get(episode, "IsHidden") is true)
                continue;

            var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
            if (aniDbEpisode is null)
                continue;

            var episodeType = ShokoReflection.GetString(aniDbEpisode, "EpisodeType");
            var isSpecial = string.Equals(episodeType, "Special", StringComparison.OrdinalIgnoreCase);
            var isOther = string.Equals(episodeType, "Other", StringComparison.OrdinalIgnoreCase);
            if (!isSpecial && !isOther)
                continue;

            if (isSpecial && !settings.IncludeSpecials)
                continue;
            if (isOther && !settings.IncludeOthers)
                continue;

            if (animeID.HasValue)
            {
                var candidateAnimeId = ShokoReflection.GetInt(aniDbEpisode, "AnimeID");
                if (!candidateAnimeId.HasValue || candidateAnimeId.Value != animeID.Value)
                    continue;
            }

            if (!HasAired(aniDbEpisode))
                continue;

            if (IsOwned(episode))
                continue;

            added.Add(episode);
            existingIds.Add(localId.Value);
            if (isSpecial)
                addedSpecials++;
            else
                addedOthers++;
        }

        if (added.Count == 0)
            return new AugmentResult(originalItems, 0, 0);

        var combined = originalItems.Concat(added)
            .OrderBy(GetAnimeId)
            .ThenBy(GetEpisodeTypeSortKey)
            .ThenBy(GetEpisodeNumber)
            .ThenBy(item => ShokoReflection.GetInt(item, "AnimeEpisodeID") ?? int.MaxValue)
            .ToList();

        if (animeID.HasValue)
        {
            logger?.LogInformation(
                "[MovieMissingFilter] Added missing non-normal episodes for animeID={AnimeID}: Specials={Specials}, Others={Others}.",
                animeID.Value,
                addedSpecials,
                addedOthers);
        }
        else
        {
            logger?.LogInformation(
                "[MovieMissingFilter] Added missing non-normal episodes to global Missing Episodes result: Specials={Specials}, Others={Others}.",
                addedSpecials,
                addedOthers);
        }

        return new AugmentResult(combined, addedSpecials, addedOthers);
    }

    private static bool HasAired(object aniDbEpisode)
    {
        try
        {
            return ShokoReflection.Get(aniDbEpisode, "HasAired") is true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwned(object episode)
    {
        // Match Shoko's missing SQL as closely as possible: a file cross-reference
        // means the episode is owned. VideoLocals is only a compatibility fallback.
        var xrefsProperty = episode.GetType().GetProperty(
            "FileCrossReferences",
            BindingFlags.Instance | BindingFlags.Public);

        if (xrefsProperty is not null)
            return ShokoReflection.HasAny(xrefsProperty.GetValue(episode));

        return ShokoReflection.HasAny(ShokoReflection.Get(episode, "VideoLocals"));
    }

    private static int GetAnimeId(object episode)
    {
        var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
        return ShokoReflection.GetInt(aniDbEpisode, "AnimeID") ?? int.MaxValue;
    }

    private static int GetEpisodeNumber(object episode)
    {
        var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
        return ShokoReflection.GetInt(aniDbEpisode, "EpisodeNumber") ?? int.MaxValue;
    }

    private static int GetEpisodeTypeSortKey(object episode)
    {
        var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
        var type = ShokoReflection.GetString(aniDbEpisode, "EpisodeType");
        return string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(type, "Special", StringComparison.OrdinalIgnoreCase) ? 2
            : string.Equals(type, "Credits", StringComparison.OrdinalIgnoreCase) ? 3
            : string.Equals(type, "Trailer", StringComparison.OrdinalIgnoreCase) ? 4
            : string.Equals(type, "Parody", StringComparison.OrdinalIgnoreCase) ? 5
            : string.Equals(type, "Other", StringComparison.OrdinalIgnoreCase) ? 6
            : 99;
    }
}
