using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.MovieMissingFilter.Reflection;

namespace Shoko.Plugin.MovieMissingFilter.Filtering;

internal static class MovieAlternativeFilter
{
    private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PartRegex = new(
        @"^Part\s+(?<part>\d+)\s+of\s+(?<total>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal readonly record struct FilterResult(IReadOnlyList<object> Items, int RemovedCount);

    internal static FilterResult Filter(object repository, IReadOnlyList<object> missingItems, ILogger? logger)
    {
        var getBySeriesId = repository.GetType().GetMethod(
            "GetBySeriesID",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null);

        if (getBySeriesId is null)
            return new FilterResult(missingItems, 0);

        var removeIds = new HashSet<int>();

        foreach (var seriesGroup in missingItems
                     .Select(item => new { Item = item, SeriesId = ShokoReflection.GetInt(item, "AnimeSeriesID") })
                     .Where(x => x.SeriesId.HasValue)
                     .GroupBy(x => x.SeriesId!.Value))
        {
            IEnumerable<object> allEpisodes;
            try
            {
                allEpisodes = ShokoReflection.Enumerate(getBySeriesId.Invoke(repository, new object[] { seriesGroup.Key })).ToList();
            }
            catch
            {
                // A daily/dev signature or implementation changed. Leave this series untouched.
                continue;
            }

            var candidates = allEpisodes
                .Select(ParseEpisode)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!.Value)
                .ToList();

            if (candidates.Count == 0 || !IsMovie(candidates))
                continue;

            var recognized = candidates.Where(c => c.Kind != MovieEpisodeKind.Other).ToList();
            if (recognized.Count == 0)
                continue;

            var completeOwned = recognized.Any(c => c.Kind == MovieEpisodeKind.Complete && c.Owned);
            var completePartLayout = FindCompleteOwnedPartLayout(recognized);

            // Only suppress alternatives when one representation is definitely complete.
            if (!completeOwned && completePartLayout is null)
                continue;

            var missingInSeries = seriesGroup
                .Select(x => ParseEpisode(x.Item))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!.Value)
                .Where(candidate => candidate.Kind != MovieEpisodeKind.Other)
                .ToList();

            var removedHere = 0;
            foreach (var candidate in missingInSeries)
            {
                // Once the whole movie is satisfied by Complete Movie or by one entire
                // Part X of N layout, every recognized alternate representation is redundant.
                if (removeIds.Add(candidate.AnimeEpisodeId))
                    removedHere++;
            }

            if (removedHere > 0)
            {
                var reason = completeOwned
                    ? "Complete Movie is owned"
                    : $"all parts of the {completePartLayout}-part layout are owned";

                logger?.LogDebug(
                    "[MovieMissingFilter] Filtered {Count} alternate missing episode(s) for Shoko series {SeriesId}: {Reason}.",
                    removedHere,
                    seriesGroup.Key,
                    reason);
            }
        }

        if (removeIds.Count == 0)
            return new FilterResult(missingItems, 0);

        var output = new List<object>(missingItems.Count - removeIds.Count);
        foreach (var item in missingItems)
        {
            var id = ShokoReflection.GetInt(item, "AnimeEpisodeID");
            if (!id.HasValue || !removeIds.Contains(id.Value))
                output.Add(item);
        }

        return new FilterResult(output, missingItems.Count - output.Count);
    }

    internal static void LogDetailDiagnostics(
        object repository,
        IReadOnlyList<object> missingItems,
        int animeID,
        ILogger? logger)
    {
        if (logger is null || missingItems.Count == 0)
            return;

        var seriesId = ShokoReflection.GetInt(missingItems[0], "AnimeSeriesID");
        if (!seriesId.HasValue)
            return;

        var getBySeriesId = repository.GetType().GetMethod(
            "GetBySeriesID",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null);

        if (getBySeriesId is null)
            return;

        List<object> allEpisodes;
        try
        {
            allEpisodes = ShokoReflection.Enumerate(getBySeriesId.Invoke(repository, new object[] { seriesId.Value })).ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "[MovieMissingFilter] Unable to enumerate diagnostic episodes for ShokoSeries={ShokoSeriesID}, animeID={AnimeID}.",
                seriesId.Value,
                animeID);
            return;
        }

        if (allEpisodes.Count == 0)
            return;

        var firstAniDbEpisode = ShokoReflection.Get(allEpisodes[0], "AniDB_Episode");
        var anime = ShokoReflection.Get(firstAniDbEpisode, "AniDB_Anime");
        var rawAnimeType = ShokoReflection.GetString(anime, "RawAnimeType") ?? "<null>";
        var animeType = ShokoReflection.GetString(anime, "AnimeType") ?? "<null>";
        var isMovie = anime is not null && IsMovieAnime(anime);

        var missingIds = missingItems
            .Select(item => ShokoReflection.GetInt(item, "AnimeEpisodeID"))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        logger.LogInformation(
            "[MovieMissingFilter] Diagnostic series: ShokoSeries={ShokoSeriesID}, animeID={AnimeID}, RawAnimeType={RawAnimeType}, AnimeType={AnimeType}, IsMovie={IsMovie}, totalEpisodes={TotalEpisodes}, missingNormalEpisodes={MissingCount}.",
            seriesId.Value,
            animeID,
            rawAnimeType,
            animeType,
            isMovie,
            allEpisodes.Count,
            missingItems.Count);

        foreach (var episode in allEpisodes
                     .OrderBy(ep => ShokoReflection.GetInt(ShokoReflection.Get(ep, "AniDB_Episode"), "EpisodeNumber") ?? int.MaxValue)
                     .ThenBy(ep => ShokoReflection.GetInt(ep, "AnimeEpisodeID") ?? int.MaxValue))
        {
            var localEpisodeId = ShokoReflection.GetInt(episode, "AnimeEpisodeID");
            var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
            if (aniDbEpisode is null)
                continue;

            var aniDbEpisodeId = ShokoReflection.GetInt(aniDbEpisode, "EpisodeID");
            var episodeNumber = ShokoReflection.GetInt(aniDbEpisode, "EpisodeNumber");
            var episodeType = ShokoReflection.GetString(aniDbEpisode, "EpisodeType") ?? "<null>";
            var defaultTitleObject = ShokoReflection.Get(aniDbEpisode, "DefaultTitle");
            var defaultTitle = NormalizeTitle(ShokoReflection.GetString(defaultTitleObject, "Value") ?? string.Empty);
            var effectiveTitle = NormalizeTitle(ShokoReflection.GetString(aniDbEpisode, "Title") ?? string.Empty);
            var preferredTitle = GetPreferredTitle(aniDbEpisode);
            var parsedKind = ParseKind(preferredTitle, out var part, out var total);
            var owned = IsOwned(episode);
            var isMissing = localEpisodeId.HasValue && missingIds.Contains(localEpisodeId.Value);
            var parseLabel = parsedKind switch
            {
                MovieEpisodeKind.Complete => "Complete",
                MovieEpisodeKind.Part => $"Part {part} of {total}",
                _ => "Other",
            };

            logger.LogInformation(
                "[MovieMissingFilter] Diagnostic episode: ShokoSeries={ShokoSeriesID}, EP={EpisodeNumber}, AnimeEpisodeID={AnimeEpisodeID}, AniDBEpisodeID={AniDBEpisodeID}, type={EpisodeType}, owned={Owned}, missing={Missing}, parsed={ParsedKind}, preferredTitle=\"{PreferredTitle}\", defaultTitle=\"{DefaultTitle}\", effectiveTitle=\"{EffectiveTitle}\".",
                seriesId.Value,
                episodeNumber?.ToString() ?? "?",
                localEpisodeId?.ToString() ?? "?",
                aniDbEpisodeId?.ToString() ?? "?",
                episodeType,
                owned,
                isMissing,
                parseLabel,
                preferredTitle,
                defaultTitle,
                effectiveTitle);
        }
    }

    private static int? FindCompleteOwnedPartLayout(IReadOnlyList<MovieEpisodeCandidate> candidates)
    {
        foreach (var group in candidates
                     .Where(c => c.Kind == MovieEpisodeKind.Part && c.Total >= 2)
                     .GroupBy(c => c.Total)
                     .OrderBy(g => g.Key))
        {
            var complete = true;
            for (var part = 1; part <= group.Key; part++)
            {
                if (!group.Any(c => c.Part == part && c.Owned))
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
                return group.Key;
        }

        return null;
    }


    /// <summary>
    /// Returns how many visible, aired, normal AniDB movie episodes are missing
    /// only because they are alternate Complete Movie / Part X of Y
    /// representations while another complete representation is already owned.
    /// This mirrors the normal-episode portion of IShokoSeries.MissingEpisodeCounts.
    /// </summary>
    internal static int GetRawNormalMissingSuppressionCount(object series)
    {
        if (!IsMovieSeries(series))
            return 0;

        // AnimeSeries.AnimeEpisodes intentionally excludes hidden episodes, which
        // matches the source used by Shoko's MissingEpisodeCounts implementation.
        var candidates = ShokoReflection.Enumerate(ShokoReflection.Get(series, "AnimeEpisodes"))
            .Select(ParseEpisode)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.Value)
            .ToList();

        if (candidates.Count == 0)
            return 0;

        var recognized = candidates.Where(c => c.Kind != MovieEpisodeKind.Other).ToList();
        if (recognized.Count == 0)
            return 0;

        var completeOwned = recognized.Any(c => c.Kind == MovieEpisodeKind.Complete && c.Owned);
        var completePartLayout = FindCompleteOwnedPartLayout(recognized);
        if (!completeOwned && completePartLayout is null)
            return 0;

        return recognized.Count(candidate => !candidate.Owned && HasAired(candidate.Episode));
    }

    private static bool HasAired(object episode)
    {
        var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
        var value = ShokoReflection.Get(aniDbEpisode, "HasAired");
        if (value is bool result)
            return result;

        try
        {
            return value is not null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsMovieSeries(object series)
    {
        var anime = ShokoReflection.Get(series, "AniDB_Anime");
        return anime is not null && IsMovieAnime(anime);
    }

    private static bool IsMovie(IReadOnlyList<MovieEpisodeCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var anime = ShokoReflection.Get(candidate.Episode, "AniDB_Anime");
            if (anime is null)
                continue;

            return IsMovieAnime(anime);
        }

        return false;
    }

    private static bool IsMovieAnime(object anime)
    {
        var rawType = ShokoReflection.GetString(anime, "RawAnimeType");
        if (string.Equals(rawType, "movie", StringComparison.OrdinalIgnoreCase))
            return true;

        var animeType = ShokoReflection.GetString(anime, "AnimeType");
        return string.Equals(animeType, "Movie", StringComparison.OrdinalIgnoreCase);
    }

    private static MovieEpisodeCandidate? ParseEpisode(object episode)
    {
        var id = ShokoReflection.GetInt(episode, "AnimeEpisodeID");
        if (!id.HasValue)
            return null;

        var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
        if (aniDbEpisode is null)
            return null;

        // Match Shoko's current missing query: EpisodeType = normal episode only.
        var episodeType = ShokoReflection.GetString(aniDbEpisode, "EpisodeType");
        if (!string.Equals(episodeType, "Episode", StringComparison.OrdinalIgnoreCase))
            return null;

        var title = GetPreferredTitle(aniDbEpisode);
        var kind = ParseKind(title, out var part, out var total);

        return new MovieEpisodeCandidate(
            episode,
            id.Value,
            kind,
            part,
            total,
            IsOwned(episode));
    }

    private static string GetPreferredTitle(object aniDbEpisode)
    {
        // Shoko daily/dev may expose DefaultTitle.Value as an unresolved placeholder
        // such as "<AniDB Episode 18174>", while AniDB_Episode.Title already contains
        // the effective title used by the WebUI (e.g. "Complete Movie").
        // Prefer the effective title and only fall back to DefaultTitle.Value.
        var effectiveTitle = NormalizeTitle(ShokoReflection.GetString(aniDbEpisode, "Title") ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(effectiveTitle) && !IsAniDbPlaceholderTitle(effectiveTitle))
            return effectiveTitle;

        var defaultTitle = ShokoReflection.Get(aniDbEpisode, "DefaultTitle");
        var fallbackTitle = NormalizeTitle(ShokoReflection.GetString(defaultTitle, "Value") ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(fallbackTitle) && !IsAniDbPlaceholderTitle(fallbackTitle))
            return fallbackTitle;

        return !string.IsNullOrWhiteSpace(effectiveTitle) ? effectiveTitle : fallbackTitle;
    }

    private static bool IsAniDbPlaceholderTitle(string title)
        => title.StartsWith("<AniDB Episode ", StringComparison.OrdinalIgnoreCase) && title.EndsWith(">", StringComparison.Ordinal);

    private static MovieEpisodeKind ParseKind(string title, out int part, out int total)
    {
        part = 0;
        total = 0;

        if (string.Equals(title, "Complete Movie", StringComparison.OrdinalIgnoreCase))
            return MovieEpisodeKind.Complete;

        var match = PartRegex.Match(title);
        if (!match.Success ||
            !int.TryParse(match.Groups["part"].Value, out part) ||
            !int.TryParse(match.Groups["total"].Value, out total) ||
            total < 2 ||
            part < 1 ||
            part > total)
        {
            part = 0;
            total = 0;
            return MovieEpisodeKind.Other;
        }

        return MovieEpisodeKind.Part;
    }

    private static string NormalizeTitle(string title)
        => SpaceRegex.Replace(title.Trim(), " ");

    private static bool IsOwned(object episode)
    {
        // CrossRef_File_Episode is what Shoko's current Missing Episodes SQL checks.
        // Prefer it when available so the plugin uses the same ownership definition.
        var xrefsProperty = episode.GetType().GetProperty("FileCrossReferences", BindingFlags.Instance | BindingFlags.Public);
        if (xrefsProperty is not null)
            return ShokoReflection.HasAny(xrefsProperty.GetValue(episode));

        // Compatibility fallback in case a daily build changes the public shape.
        return ShokoReflection.HasAny(ShokoReflection.Get(episode, "VideoLocals"));
    }

    private enum MovieEpisodeKind
    {
        Other,
        Complete,
        Part,
    }

    private readonly record struct MovieEpisodeCandidate(
        object Episode,
        int AnimeEpisodeId,
        MovieEpisodeKind Kind,
        int Part,
        int Total,
        bool Owned);
}
