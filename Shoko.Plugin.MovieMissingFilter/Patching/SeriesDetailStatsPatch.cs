using System.Reflection;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.MovieMissingFilter.Reflection;

namespace Shoko.Plugin.MovieMissingFilter.Patching;

/// <summary>
/// Corrects the Missing.Episodes and Missing.Specials values returned by
/// GET /api/v3/Series/{seriesID}. The current Shoko SeriesSizes API does not
/// expose a Missing.Others field, so Other/O episodes cannot be represented
/// separately on the stock series detail page.
/// </summary>
internal static class SeriesDetailStatsPatch
{
    private static ILogger? _logger;
    private static object? _animeEpisodeRepository;
    private static MethodInfo? _getMissingMethod;
    private static int _runtimeErrorLogged;

    internal static void Configure(ILogger logger, object animeEpisodeRepository, MethodInfo getMissingMethod)
    {
        _logger = logger;
        _animeEpisodeRepository = animeEpisodeRepository;
        _getMissingMethod = getMissingMethod;
    }

    internal static void ResultPostfix<T>(T __result, int seriesID) where T : class
    {
        if (__result is null || _animeEpisodeRepository is null || _getMissingMethod is null)
            return;

        try
        {
            // GetSeries returns ActionResult<Series>. For a successful direct-value
            // result, ActionResult<T>.Value contains the Series DTO.
            var seriesDto = GetProperty(__result, "Value") ?? __result;
            var ids = GetProperty(seriesDto, "IDs");
            var animeId = ShokoReflection.GetInt(ids, "AniDB");
            if (!animeId.HasValue)
                return;

            var result = _getMissingMethod.Invoke(
                _animeEpisodeRepository,
                new object?[] { false, animeId.Value });

            var missingEpisodes = 0;
            var missingSpecials = 0;
            var missingOthers = 0;

            foreach (var episode in ShokoReflection.Enumerate(result))
            {
                var aniDbEpisode = ShokoReflection.Get(episode, "AniDB_Episode");
                var type = ShokoReflection.GetString(aniDbEpisode, "EpisodeType");

                if (string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase))
                    missingEpisodes++;
                else if (string.Equals(type, "Special", StringComparison.OrdinalIgnoreCase))
                    missingSpecials++;
                else if (string.Equals(type, "Other", StringComparison.OrdinalIgnoreCase))
                    missingOthers++;
            }

            var sizes = GetProperty(seriesDto, "Sizes");
            var missing = GetProperty(sizes, "Missing");
            if (missing is null)
                return;

            var episodesProperty = missing.GetType().GetProperty("Episodes", BindingFlags.Instance | BindingFlags.Public);
            var specialsProperty = missing.GetType().GetProperty("Specials", BindingFlags.Instance | BindingFlags.Public);
            if (episodesProperty is null || !episodesProperty.CanWrite ||
                specialsProperty is null || !specialsProperty.CanWrite)
                return;

            var originalEpisodes = Convert.ToInt32(episodesProperty.GetValue(missing) ?? 0);
            var originalSpecials = Convert.ToInt32(specialsProperty.GetValue(missing) ?? 0);

            episodesProperty.SetValue(missing, missingEpisodes);
            specialsProperty.SetValue(missing, missingSpecials);

            if (originalEpisodes != missingEpisodes || originalSpecials != missingSpecials || missingOthers > 0)
            {
                _logger?.LogInformation(
                    "[MovieMissingFilter] Series detail {SeriesID} missing sizes corrected: Episodes {OriginalEpisodes} -> {Episodes}; Specials {OriginalSpecials} -> {Specials}; Others missing={Others} (not representable by stock SeriesSizes.Missing API).",
                    seriesID,
                    originalEpisodes,
                    missingEpisodes,
                    originalSpecials,
                    missingSpecials,
                    missingOthers);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _runtimeErrorLogged, 1) == 0)
            {
                _logger?.LogWarning(
                    ex,
                    "[MovieMissingFilter] Series detail missing-size correction failed. Shoko's original series detail values are being used.");
            }
        }
    }

    private static object? GetProperty(object? instance, string name)
    {
        if (instance is null)
            return null;

        return instance.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }
}
