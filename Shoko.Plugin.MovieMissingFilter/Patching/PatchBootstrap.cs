using System.Reflection;
using HarmonyLib;
using Microsoft.Extensions.Logging;

namespace Shoko.Plugin.MovieMissingFilter.Patching;

internal static class PatchBootstrap
{
    private const string HarmonyId = "local.shoko.movie-missing-filter";
    private static int _applied;

    internal static void Apply(ILogger logger, IServiceProvider services)
    {
        if (Interlocked.Exchange(ref _applied, 1) != 0)
            return;

        MissingEpisodesPatch.ConfigureLogger(logger);

        try
        {
            var repositoryType = AccessTools.TypeByName("Shoko.Server.Repositories.Cached.AnimeEpisodeRepository");
            if (repositoryType is null)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] AnimeEpisodeRepository was not found. No patch was applied; Shoko keeps its original behavior.");
                return;
            }

            var target = repositoryType.GetMethod(
                "GetMissing",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(bool), typeof(int?) },
                modifiers: null);

            if (target is null)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] GetMissing(bool, int?) was not found. No patch was applied; Shoko keeps its original behavior.");
                return;
            }

            var returnType = target.ReturnType;
            if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                logger.LogWarning(
                    "[MovieMissingFilter] Unsupported GetMissing return type {ReturnType}. Expected IEnumerable<T>; no patch was applied.",
                    returnType.FullName);
                return;
            }

            var elementType = returnType.GetGenericArguments()[0];
            var postfixDefinition = typeof(MissingEpisodesPatch).GetMethod(
                nameof(MissingEpisodesPatch.ResultPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            if (postfixDefinition is null || !postfixDefinition.IsGenericMethodDefinition)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] Internal generic ref-result postfix was not found. No patch was applied.");
                return;
            }

            var postfix = postfixDefinition.MakeGenericMethod(elementType);
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));

            logger.LogInformation(
                "[MovieMissingFilter] Runtime ref-result patch applied to {Type}.{Method} with exact return type {ReturnType}. No database or Shoko files are modified.",
                repositoryType.FullName,
                target.Name,
                returnType.FullName);

            ApplyDashboardPatch(harmony, logger, services, repositoryType, target);
            ApplySeriesDetailPatch(harmony, logger, services, repositoryType, target);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[MovieMissingFilter] Runtime patch could not be applied. Shoko keeps its original behavior.");
        }
    }

    private static void ApplyDashboardPatch(
        Harmony harmony,
        ILogger logger,
        IServiceProvider services,
        Type repositoryType,
        MethodInfo getMissingMethod)
    {
        try
        {
            var repository = services.GetService(repositoryType);
            if (repository is null)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] AnimeEpisodeRepository service could not be resolved. Missing Episodes filtering remains active, but Dashboard/Stats will keep Shoko's original counters.");
                return;
            }

            var dashboardControllerType = AccessTools.TypeByName("Shoko.Server.API.v3.Controllers.DashboardController");
            if (dashboardControllerType is null)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] DashboardController was not found. Missing Episodes filtering remains active, but dashboard counters are not patched.");
                return;
            }

            var statsTarget = dashboardControllerType.GetMethod(
                "GetStats",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (statsTarget is null || statsTarget.ReturnType == typeof(void))
            {
                logger.LogWarning(
                    "[MovieMissingFilter] DashboardController.GetStats() was not found or has an unsupported return type. Dashboard counters are not patched.");
                return;
            }

            var dashboardPostfixDefinition = typeof(DashboardStatsPatch).GetMethod(
                nameof(DashboardStatsPatch.ResultPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            if (dashboardPostfixDefinition is null || !dashboardPostfixDefinition.IsGenericMethodDefinition)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] Internal Dashboard Stats postfix was not found. Dashboard counters are not patched.");
                return;
            }

            DashboardStatsPatch.Configure(logger, repository, getMissingMethod);

            var dashboardPostfix = dashboardPostfixDefinition.MakeGenericMethod(statsTarget.ReturnType);
            harmony.Patch(statsTarget, postfix: new HarmonyMethod(dashboardPostfix));

            logger.LogInformation(
                "[MovieMissingFilter] Runtime dashboard patch applied to {Type}.{Method}; MissingEpisodes and MissingEpisodesCollecting will be derived from the filtered Missing Episodes result using Shoko's current User.AllowedSeries visibility.",
                dashboardControllerType.FullName,
                statsTarget.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[MovieMissingFilter] Dashboard Stats patch could not be applied. Missing Episodes filtering remains active; dashboard counters keep Shoko's original behavior.");
        }
    }
    private static void ApplySeriesDetailPatch(
        Harmony harmony,
        ILogger logger,
        IServiceProvider services,
        Type repositoryType,
        MethodInfo getMissingMethod)
    {
        try
        {
            var repository = services.GetService(repositoryType);
            if (repository is null)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] AnimeEpisodeRepository service could not be resolved. Series detail missing sizes are not patched.");
                return;
            }

            var seriesControllerType = AccessTools.TypeByName("Shoko.Server.API.v3.Controllers.SeriesController");
            if (seriesControllerType is null)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] SeriesController was not found. Series detail missing sizes are not patched.");
                return;
            }

            var seriesTarget = seriesControllerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => string.Equals(method.Name, "GetSeries", StringComparison.Ordinal))
                .Select(method => new { Method = method, Parameters = method.GetParameters() })
                .Where(x => x.Parameters.Length == 3)
                .Where(x => x.Parameters[0].ParameterType == typeof(int))
                .Where(x => x.Parameters[1].ParameterType == typeof(bool))
                .Select(x => x.Method)
                .FirstOrDefault();

            if (seriesTarget is null || seriesTarget.ReturnType == typeof(void))
            {
                logger.LogWarning(
                    "[MovieMissingFilter] SeriesController.GetSeries(int, bool, ...) was not found or has an unsupported return type. Series detail missing sizes are not patched.");
                return;
            }

            var postfixDefinition = typeof(SeriesDetailStatsPatch).GetMethod(
                nameof(SeriesDetailStatsPatch.ResultPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);

            if (postfixDefinition is null || !postfixDefinition.IsGenericMethodDefinition)
            {
                logger.LogWarning(
                    "[MovieMissingFilter] Internal Series detail postfix was not found. Series detail missing sizes are not patched.");
                return;
            }

            SeriesDetailStatsPatch.Configure(logger, repository, getMissingMethod);
            var postfix = postfixDefinition.MakeGenericMethod(seriesTarget.ReturnType);
            harmony.Patch(seriesTarget, postfix: new HarmonyMethod(postfix));

            logger.LogInformation(
                "[MovieMissingFilter] Runtime series-detail patch applied to {Type}.{Method}; Missing.Episodes and Missing.Specials will follow the configurable Missing Episodes result. Missing.Others cannot be exposed by Shoko's current SeriesSizes.Missing API.",
                seriesControllerType.FullName,
                seriesTarget.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[MovieMissingFilter] Series detail patch could not be applied. Missing Episodes and dashboard filtering remain active; series detail missing sizes keep Shoko's original behavior.");
        }
    }

}
