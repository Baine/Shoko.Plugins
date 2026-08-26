using System.Reflection;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.MovieMissingFilter.Reflection;

namespace Shoko.Plugin.MovieMissingFilter.Patching;

/// <summary>
/// Corrects only the two Missing Episodes counters returned by Dashboard/Stats.
/// Shoko's persisted AnimeSeries.MissingEpisodeCount values are never modified.
/// </summary>
internal static class DashboardStatsPatch
{
    private static ILogger? _logger;
    private static object? _animeEpisodeRepository;
    private static MethodInfo? _getMissingMethod;
    private static int _runtimeErrorLogged;
    private static int _visibilityErrorLogged;

    internal static void Configure(ILogger logger, object animeEpisodeRepository, MethodInfo getMissingMethod)
    {
        _logger = logger;
        _animeEpisodeRepository = animeEpisodeRepository;
        _getMissingMethod = getMissingMethod;
    }

    /// <summary>
    /// PatchBootstrap closes this generic method with Dashboard.CollectionStats so
    /// Harmony receives the exact result type. CollectionStats is a reference type,
    /// so changing its settable properties changes the object serialized by Shoko.
    /// </summary>
    internal static void ResultPostfix<T>(T __result, object __instance) where T : class
    {
        if (__result is null || _animeEpisodeRepository is null || _getMissingMethod is null)
            return;

        try
        {
            var user = GetPropertyFromHierarchy(__instance, "User");
            if (user is null)
            {
                LogVisibilityFailureOnce("Dashboard controller User property could not be resolved");
                return;
            }

            var missingProperty = __result.GetType().GetProperty(
                "MissingEpisodes",
                BindingFlags.Instance | BindingFlags.Public);
            var collectingProperty = __result.GetType().GetProperty(
                "MissingEpisodesCollecting",
                BindingFlags.Instance | BindingFlags.Public);

            if (missingProperty is null || !missingProperty.CanWrite ||
                collectingProperty is null || !collectingProperty.CanWrite)
            {
                LogVisibilityFailureOnce("Dashboard CollectionStats missing-count properties could not be resolved");
                return;
            }

            if (!TryCountVisibleFiltered(user, collecting: false, out var correctedTotal) ||
                !TryCountVisibleFiltered(user, collecting: true, out var correctedCollecting))
            {
                return;
            }

            var originalTotal = Convert.ToInt32(missingProperty.GetValue(__result) ?? 0);
            var originalCollecting = Convert.ToInt32(collectingProperty.GetValue(__result) ?? 0);

            missingProperty.SetValue(__result, correctedTotal);
            collectingProperty.SetValue(__result, correctedCollecting);

            if (originalTotal != correctedTotal || originalCollecting != correctedCollecting)
            {
                _logger?.LogInformation(
                    "[MovieMissingFilter] Dashboard Stats corrected: MissingEpisodes {OriginalTotal} -> {CorrectedTotal}; MissingEpisodesCollecting {OriginalCollecting} -> {CorrectedCollecting}.",
                    originalTotal,
                    correctedTotal,
                    originalCollecting,
                    correctedCollecting);
            }
            else
            {
                _logger?.LogDebug(
                    "[MovieMissingFilter] Dashboard Stats patch active; missing counters required no correction ({Total}/{Collecting}).",
                    correctedTotal,
                    correctedCollecting);
            }
        }
        catch (Exception ex)
        {
            // Fail open. A dashboard failure must never be caused by this plugin.
            if (Interlocked.Exchange(ref _runtimeErrorLogged, 1) == 0)
            {
                _logger?.LogWarning(
                    ex,
                    "[MovieMissingFilter] Dashboard Stats correction failed. Shoko's original dashboard counters are being used.");
            }
        }
    }

    private static bool TryCountVisibleFiltered(object user, bool collecting, out int count)
    {
        count = 0;

        object? result;
        try
        {
            // This calls the same GetMissing(bool, int?) method patched by this plugin.
            // The returned enumerable is therefore already corrected for alternate
            // Complete Movie / Part X of Y layouts.
            result = _getMissingMethod!.Invoke(
                _animeEpisodeRepository,
                new object?[] { collecting, null });
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _runtimeErrorLogged, 1) == 0)
            {
                _logger?.LogWarning(
                    ex,
                    "[MovieMissingFilter] Dashboard could not obtain the filtered Missing Episodes result. Shoko's original dashboard counters are being used.");
            }
            return false;
        }

        MethodInfo? allowedSeriesMethod = null;
        Type? allowedSeriesParameterType = null;

        foreach (var episode in ShokoReflection.Enumerate(result))
        {
            var series = ShokoReflection.Get(episode, "AnimeSeries");
            if (series is null)
                continue;

            if (allowedSeriesMethod is null ||
                allowedSeriesParameterType is null ||
                !allowedSeriesParameterType.IsAssignableFrom(series.GetType()))
            {
                allowedSeriesMethod = FindAllowedSeriesMethod(user, series.GetType());
                allowedSeriesParameterType = allowedSeriesMethod?.GetParameters()[0].ParameterType;

                if (allowedSeriesMethod is null)
                {
                    LogVisibilityFailureOnce("User.AllowedSeries(series) could not be resolved");
                    return false;
                }
            }

            bool allowed;
            try
            {
                allowed = allowedSeriesMethod.Invoke(user, new[] { series }) is true;
            }
            catch
            {
                LogVisibilityFailureOnce("User.AllowedSeries(series) could not be invoked");
                return false;
            }

            if (allowed)
                count++;
        }

        return true;
    }

    private static MethodInfo? FindAllowedSeriesMethod(object user, Type seriesType)
        => user.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "AllowedSeries", StringComparison.Ordinal))
            .Where(method => method.ReturnType == typeof(bool))
            .Select(method => new { Method = method, Parameters = method.GetParameters() })
            .Where(x => x.Parameters.Length == 1 && x.Parameters[0].ParameterType.IsAssignableFrom(seriesType))
            .Select(x => x.Method)
            .FirstOrDefault();

    private static object? GetPropertyFromHierarchy(object instance, string propertyName)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            if (property is not null)
                return property.GetValue(instance);
        }

        return null;
    }

    private static void LogVisibilityFailureOnce(string reason)
    {
        if (Interlocked.Exchange(ref _visibilityErrorLogged, 1) == 0)
        {
            _logger?.LogWarning(
                "[MovieMissingFilter] Dashboard Stats patch could not mirror Shoko's per-user series visibility ({Reason}). Shoko's original dashboard counters are being used.",
                reason);
        }
    }
}
