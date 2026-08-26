using Microsoft.Extensions.Logging;
using Shoko.Plugin.MovieMissingFilter.Configuration;
using Shoko.Plugin.MovieMissingFilter.Filtering;
using Shoko.Plugin.MovieMissingFilter.Reflection;

namespace Shoko.Plugin.MovieMissingFilter.Patching;

internal static class MissingEpisodesPatch
{
    private static ILogger? _logger;
    private static int _runtimeErrorLogged;

    internal static void ConfigureLogger(ILogger logger) => _logger = logger;

    internal static void ResultPostfix<T>(
        ref IEnumerable<T> __result,
        object __instance,
        bool collecting,
        int? animeID)
    {
        try
        {
            var settings = MovieMissingFilterSettingsStore.Current;
            var originalTyped = (__result ?? Enumerable.Empty<T>()).ToList();
            var original = originalTyped.Cast<object>().ToList();

            // Shoko stock GetMissing supplies normal E episodes. In non-collecting
            // mode, optionally augment it with S and/or O based on plugin settings.
            var augmented = AdditionalEpisodeMissingAugmenter.Augment(
                __instance,
                original,
                collecting,
                animeID,
                _logger);

            // Movie alternative suppression only has meaning when normal episodes
            // are enabled. It never suppresses S or O entries.
            MovieAlternativeFilter.FilterResult movieFiltered;
            if (settings.IncludeNormalEpisodes)
            {
                movieFiltered = MovieAlternativeFilter.Filter(__instance, augmented.Items, _logger);
            }
            else
            {
                movieFiltered = new MovieAlternativeFilter.FilterResult(augmented.Items, 0);
            }

            var finalItems = EpisodeTypeVisibilityFilter.Apply(
                movieFiltered.Items,
                settings,
                out var removedNormalBySetting,
                out var removedSpecialsBySetting,
                out var removedOthersBySetting);

            var changed = augmented.AddedCount > 0
                || movieFiltered.RemovedCount > 0
                || removedNormalBySetting > 0
                || removedSpecialsBySetting > 0
                || removedOthersBySetting > 0;

            __result = changed ? finalItems.Cast<T>().ToList() : originalTyped;

            var finalCount = finalItems.Count;
            var shokoSeriesId = finalItems.Count > 0
                ? ShokoReflection.GetInt(finalItems[0], "AnimeSeriesID")
                : originalTyped.Count > 0
                    ? ShokoReflection.GetInt(originalTyped[0], "AnimeSeriesID")
                    : null;

            if (animeID.HasValue)
            {
                _logger?.LogInformation(
                    "[MovieMissingFilter] Detail GetMissing result: ShokoSeries={ShokoSeriesID}, animeID={AnimeID}, collecting={Collecting}, {OriginalCount} -> {FinalCount}, addedSpecials={AddedSpecials}, addedOthers={AddedOthers}, removedMovieAlternatives={MovieRemoved}, disabledBySettings(E/S/O)={RemovedE}/{RemovedS}/{RemovedO}.",
                    shokoSeriesId?.ToString() ?? "unknown",
                    animeID.Value,
                    collecting,
                    originalTyped.Count,
                    finalCount,
                    augmented.AddedSpecialCount,
                    augmented.AddedOtherCount,
                    movieFiltered.RemovedCount,
                    removedNormalBySetting,
                    removedSpecialsBySetting,
                    removedOthersBySetting);

                if (settings.IncludeNormalEpisodes && movieFiltered.RemovedCount == 0 && finalItems.Count > 0)
                    MovieAlternativeFilter.LogDetailDiagnostics(__instance, finalItems, animeID.Value, _logger);
            }
            else if (changed)
            {
                _logger?.LogInformation(
                    "[MovieMissingFilter] Global GetMissing result replaced: {OriginalCount} -> {FinalCount} episode(s), addedSpecials={AddedSpecials}, addedOthers={AddedOthers}, removedMovieAlternatives={MovieRemoved}, disabledBySettings(E/S/O)={RemovedE}/{RemovedS}/{RemovedO} (collecting={Collecting}).",
                    originalTyped.Count,
                    finalCount,
                    augmented.AddedSpecialCount,
                    augmented.AddedOtherCount,
                    movieFiltered.RemovedCount,
                    removedNormalBySetting,
                    removedSpecialsBySetting,
                    removedOthersBySetting,
                    collecting);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _runtimeErrorLogged, 1) == 0)
            {
                _logger?.LogWarning(
                    ex,
                    "[MovieMissingFilter] Filtering failed at runtime. The original Shoko result is being used.");
            }
        }
    }
}
