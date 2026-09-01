using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Metadata.Tmdb.Services;
using Shoko.Plugin.TmdbLinkFixer.Configuration;
using Shoko.Plugin.TmdbLinkFixer.Models;

namespace Shoko.Plugin.TmdbLinkFixer.Services;

public sealed class TmdbLinkFixerService(
    IMetadataService metadataService,
    ITmdbMetadataService tmdbMetadataService,
    ITmdbLinkingService linkingService,
    ITmdbSearchService searchService,
    TmdbLinkProbe probe,
    ILogger<TmdbLinkFixerService> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CheckedLink> _checks = new(StringComparer.Ordinal);
    private Task<List<LinkSnapshot>>? _snapshotTask;
    private Task? _scanTask;
    private ScanState _scanState = new(false, 0, 0, 0, 0, 0, null, null);

    public bool ApiCredentialConfigured => TmdbLinkFixerSettingsStore.IsConfigured;

    public bool TryGetLinks(out IReadOnlyList<TmdbLinkItem> links)
    {
        lock (_gate)
        {
            if (_snapshotTask is { IsCompletedSuccessfully: true } completed)
            {
                links = completed.Result.Select(ToItem).OrderByDescending(x => ProblemOrder(x.Health)).ThenBy(x => x.SeriesTitle).ThenBy(x => x.EpisodeLabel).ToList();
                return true;
            }
            if (_snapshotTask is { IsFaulted: true } failed)
            {
                _snapshotTask = null;
                throw new InvalidOperationException("TMDB link snapshot build failed.", failed.Exception);
            }
            _snapshotTask ??= Task.Run(BuildSnapshots);
            links = [];
            return false;
        }
    }

    public ScanState GetScanState()
    {
        lock (_gate)
            return _scanState;
    }

    public bool StartScan()
    {
        lock (_gate)
        {
            if (_scanTask is { IsCompleted: false })
                return false;
            _snapshotTask = null;
            _scanTask = Task.Run(ScanAllAsync);
            return true;
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length < 2)
            return [];

        var movieTask = probe.SearchAsync(TmdbMediaKind.Movie, query, cancellationToken: cancellationToken);
        var showTask = probe.SearchAsync(TmdbMediaKind.Show, query, cancellationToken: cancellationToken);
        await Task.WhenAll(movieTask, showTask).WaitAsync(cancellationToken).ConfigureAwait(false);

        var movieResponse = movieTask.Result;
        var showResponse = showTask.Result;
        if (movieResponse.Error is not null)
            logger.LogWarning("Manual TMDB movie search failed: {Error}", movieResponse.Error);
        if (showResponse.Error is not null)
            logger.LogWarning("Manual TMDB show search failed: {Error}", showResponse.Error);
        if (movieResponse.Error is not null && showResponse.Error is not null)
            throw new InvalidOperationException($"TMDB search failed. Movie search: {movieResponse.Error} Show search: {showResponse.Error}");

        return movieResponse.Results.Concat(showResponse.Results)
            .OrderByDescending(x => x.Rating)
            .ThenBy(x => x.Title)
            .ToList();
    }

    public async Task<IReadOnlyList<SearchResult>> FindSuggestionsAsync(string key, CancellationToken cancellationToken)
    {
        var source = BuildSnapshots().SingleOrDefault(x => x.Key == key);
        if (source is null)
            return [];
        return await FindAutomaticCandidatesAsync(source, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> AcceptAsync(AcceptLinkRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmed)
            return new(false, "Explicit confirmation is required. No link was changed.");
        if (request.TargetId <= 0)
            return new(false, "The target TMDB ID must be greater than zero.");

        var source = BuildSnapshots().SingleOrDefault(x => x.Key == request.Key);
        if (source is null)
            return new(false, "The existing link no longer exists. Refresh the page before trying again.");
        if (source.Kind == request.TargetKind && source.TmdbId == request.TargetId)
            return new(false, "The existing and proposed links are identical.");

        var targetProbe = await probe.ProbeAsync(request.TargetKind, request.TargetId, cancellationToken).ConfigureAwait(false);
        if (targetProbe.Health != LinkHealth.Valid)
            return new(false, targetProbe.Message ?? "The proposed TMDB target could not be validated. No link was changed.");

        var series = metadataService.GetShokoSeriesByAnidbID(source.AnidbAnimeId);
        if (series is null)
            return new(false, "The Shoko series no longer exists. No link was changed.");

        try
        {
            if (request.TargetKind == TmdbMediaKind.Show)
            {
                await tmdbMetadataService.UpdateShow(new TmdbShowUpdateOptions
                {
                    ShowId = request.TargetId,
                    ForceRefresh = true,
                    DownloadImages = true,
                    DownloadCrewAndCast = false,
                    DownloadAlternateOrdering = false,
                    DownloadNetworks = false,
                    QuickRefresh = false,
                }).WaitAsync(cancellationToken).ConfigureAwait(false);
                await linkingService.AddShowLink(
                    source.AnidbAnimeId,
                    request.TargetId,
                    additiveLink: true,
                    matchRating: MatchRating.UserVerified).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var episodeId = request.AnidbEpisodeId ?? source.AnidbEpisodeId;
                if (episodeId is null || series.Episodes.All(x => x.AnidbEpisodeID != episodeId.Value))
                    return new(false, "Select an AniDB episode from this series for the movie link. No link was changed.");

                await tmdbMetadataService.UpdateMovie(new TmdbMovieUpdateOptions
                {
                    MovieId = request.TargetId,
                    ForceRefresh = true,
                    DownloadImages = true,
                    DownloadCrewAndCast = false,
                    DownloadCollections = false,
                }).WaitAsync(cancellationToken).ConfigureAwait(false);
                await linkingService.AddMovieLinkForEpisode(
                    episodeId.Value,
                    request.TargetId,
                    additiveLink: true,
                    matchRating: MatchRating.UserVerified).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await RemoveSourceAsync(source).WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
                _checks.Remove(source.Key);
            logger.LogInformation(
                "User-confirmed TMDB link replacement: {SourceKind} {SourceId} to {TargetKind} {TargetId} for AniDB anime {AnimeId}",
                source.Kind, source.TmdbId, request.TargetKind, request.TargetId, source.AnidbAnimeId);
            return new(true, "The explicitly selected TMDB link was accepted and the old link was removed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed applying user-confirmed TMDB replacement for {LinkKey}", request.Key);
            return new(false, "The confirmed replacement failed. The new link may have been added alongside the old link; refresh the page and check the Shoko log.");
        }
        finally
        {
            lock (_gate)
                _snapshotTask = null;
        }
    }

    private async Task ScanAllAsync()
    {
        var started = DateTimeOffset.UtcNow;
        SetState(new(true, 0, 0, 0, 0, 0, started, null));

        try
        {
            var links = BuildSnapshots();
            SetState(new(true, links.Count, 0, 0, 0, 0, started, null));
            var remoteChecks = new Dictionary<(TmdbMediaKind Kind, int Id), ProbeResult>();
            var completed = 0;
            var valid = 0;
            var problems = 0;
            var errors = 0;

            foreach (var link in links)
            {
                SetCheck(link.Key, new(LinkHealth.Checking, null, null, null, null, null));
                if (!remoteChecks.TryGetValue((link.Kind, link.TmdbId), out var result))
                {
                    result = await probe.ProbeAsync(link.Kind, link.TmdbId).ConfigureAwait(false);
                    remoteChecks[(link.Kind, link.TmdbId)] = result;
                }

                SetCheck(link.Key, new(
                    result.Health, result.Message, result.RedirectKind, result.RedirectId,
                    result.RedirectPosterUrl, DateTimeOffset.UtcNow));
                completed++;
                if (result.Health == LinkHealth.Valid) valid++;
                else if (result.Health == LinkHealth.Error) errors++;
                else problems++;
                if (result.Fatal)
                {
                    SetState(new(false, links.Count, completed, valid, problems, errors, started, DateTimeOffset.UtcNow));
                    logger.LogWarning("TMDB link scan stopped after a fatal API validation error: {Message}", result.Message);
                    return;
                }
                SetState(new(true, links.Count, completed, valid, problems, errors, started, null));
            }

            SetState(new(false, links.Count, completed, valid, problems, errors, started, DateTimeOffset.UtcNow));
            logger.LogInformation("TMDB link scan completed: {Total} links, {Valid} valid, {Problems} problems, {Errors} errors", links.Count, valid, problems, errors);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TMDB link scan failed");
            var state = GetScanState();
            SetState(state with { Running = false, Errors = state.Errors + 1, FinishedAt = DateTimeOffset.UtcNow });
        }
    }

    private List<LinkSnapshot> BuildSnapshots()
    {
        var result = new List<LinkSnapshot>();
        var allSeries = metadataService.GetAllShokoSeries();
        foreach (var series in allSeries)
        {
            var showRefs = series.TmdbShowCrossReferences
                .Where(x => x.AnidbAnimeID > 0 && x.TmdbShowID > 0)
                .DistinctBy(x => (x.AnidbAnimeID, x.TmdbShowID)).ToList();
            var movieRefs = series.TmdbMovieCrossReferences
                .Where(x => x.AnidbEpisodeID > 0 && x.TmdbMovieID > 0)
                .DistinctBy(x => (x.AnidbEpisodeID, x.TmdbMovieID)).ToList();
            if (showRefs.Count is 0 && movieRefs.Count is 0)
                continue;

            var rawEpisodes = series.Episodes;
            var episodes = rawEpisodes
                .OrderBy(x => x.Type)
                .ThenBy(x => x.EpisodeNumber)
                .Select(x => new EpisodeOption(x.AnidbEpisodeID, $"{EpisodePrefix(x)}{x.EpisodeNumber}: {x.Title}"))
                .ToList();
            var anidbPosterUrl = ImageUrl(series.AnidbAnime.PrimaryImage);

            foreach (var xref in showRefs)
                result.Add(new(
                    ShowKey(xref.AnidbAnimeID, xref.TmdbShowID), series.ID, xref.AnidbAnimeID, null,
                    series.Title, null, null, anidbPosterUrl, TmdbMediaKind.Show, xref.TmdbShowID,
                    ImageUrl(xref.TmdbShow?.PrimaryImage), episodes));

            foreach (var xref in movieRefs)
            {
                var episode = rawEpisodes.FirstOrDefault(x => x.AnidbEpisodeID == xref.AnidbEpisodeID);
                result.Add(new(
                    MovieKey(xref.AnidbEpisodeID, xref.TmdbMovieID), series.ID, xref.AnidbAnimeID, xref.AnidbEpisodeID,
                    series.Title, episode?.Title, episode is null ? $"AniDB EID {xref.AnidbEpisodeID}" : $"{EpisodePrefix(episode)}{episode.EpisodeNumber}",
                    anidbPosterUrl, TmdbMediaKind.Movie, xref.TmdbMovieID,
                    ImageUrl(xref.TmdbMovie?.PrimaryImage), episodes));
            }
        }
        return result;
    }

    private TmdbLinkItem ToItem(LinkSnapshot link)
    {
        var check = _checks.GetValueOrDefault(link.Key) ?? new CheckedLink(LinkHealth.NotChecked, null, null, null, null, null);
        return new(link.Key, link.ShokoSeriesId, link.AnidbAnimeId, link.AnidbEpisodeId, link.SeriesTitle,
            link.EpisodeTitle, link.EpisodeLabel, $"https://anidb.net/anime/{link.AnidbAnimeId}", link.AnidbPosterUrl,
            link.Kind, link.TmdbId, TmdbLinkProbe.BuildUri(link.Kind, link.TmdbId).ToString(), link.OldPosterUrl,
            check.Health, check.Message, check.RedirectKind, check.RedirectId, check.RedirectPosterUrl, check.CheckedAt,
            link.Episodes);
    }

    private async Task<IReadOnlyList<SearchResult>> FindAutomaticCandidatesAsync(LinkSnapshot source, CancellationToken cancellationToken)
    {
        var series = metadataService.GetShokoSeriesByAnidbID(source.AnidbAnimeId);
        if (series is null)
            return [];

        var candidates = new List<SearchResult>();
        try
        {
            var results = await searchService.SearchForAutoMatch(series.AnidbAnime).WaitAsync(cancellationToken).ConfigureAwait(false);
            candidates.AddRange(results.Select(x => x.IsMovie
                    ? ToSearchResult(x.TmdbMovie!, x.AnidbEpisode?.ID, x.MatchRating.ToString())
                    : ToSearchResult(x.TmdbShow!, x.MatchRating.ToString())));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shoko automatic TMDB candidate search failed for AniDB anime {AnimeId}", source.AnidbAnimeId);
        }

        // Shoko's automatic matcher intentionally filters candidates to the Animation genre.
        // A broad, inert title search is added here because TMDB entries can be missing genre
        // metadata. It includes adult results and still requires explicit administrator review.
        try
        {
            var broadResults = await SearchAsync(source.SeriesTitle, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(broadResults.Select(x => x with { MatchReason = "Broad title search (adult results included)" }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep any candidates returned by Shoko even when the supplemental API search fails.
            logger.LogWarning(ex, "Broad TMDB candidate search failed for AniDB anime {AnimeId}", source.AnidbAnimeId);
        }

        return candidates
            .DistinctBy(x => (x.Kind, x.Id, x.AnidbEpisodeId))
            .ToList();
    }

    private static SearchResult ToSearchResult(ITmdbMovieSearchResult movie, int? anidbEpisodeId, string matchReason)
        => new(
            TmdbMediaKind.Movie, movie.ID, movie.Title, movie.OriginalTitle, movie.ReleasedAt,
            Poster(movie.PosterPath), movie.Overview, (double)movie.UserRating,
            TmdbLinkProbe.BuildUri(TmdbMediaKind.Movie, movie.ID).ToString(), anidbEpisodeId, matchReason);

    private static SearchResult ToSearchResult(ITmdbShowSearchResult show, string matchReason)
        => new(
            TmdbMediaKind.Show, show.ID, show.Title, show.OriginalTitle, show.FirstAiredAt,
            Poster(show.PosterPath), show.Overview, (double)show.UserRating,
            TmdbLinkProbe.BuildUri(TmdbMediaKind.Show, show.ID).ToString(), null, matchReason);

    private Task RemoveSourceAsync(LinkSnapshot source)
        => source.Kind == TmdbMediaKind.Show
            ? linkingService.RemoveShowLink(source.AnidbAnimeId, source.TmdbId, purge: false)
            : linkingService.RemoveMovieLinkForEpisode(source.AnidbEpisodeId!.Value, source.TmdbId, purge: false);

    private void SetCheck(string key, CheckedLink check)
    {
        lock (_gate) _checks[key] = check;
    }

    private void SetState(ScanState state)
    {
        lock (_gate) _scanState = state;
    }

    private void Forget(string key)
    {
        lock (_gate) _checks.Remove(key);
    }

    private static int ProblemOrder(LinkHealth health) => health switch
    {
        LinkHealth.Invalid => 5,
        LinkHealth.Redirected => 4,
        LinkHealth.Error => 3,
        LinkHealth.Checking => 2,
        LinkHealth.NotChecked => 1,
        _ => 0,
    };

    private static string EpisodePrefix(IShokoEpisode episode) => episode.Type switch
    {
        EpisodeType.Episode => "E",
        EpisodeType.Special => "S",
        EpisodeType.Credits => "C",
        EpisodeType.Trailer => "T",
        EpisodeType.Parody => "P",
        EpisodeType.Other => "O",
        _ => "?",
    };

    private static string? Poster(string? path) => string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/w185{path}";
    private static string? ImageUrl(Shoko.Abstractions.Metadata.Image.IImage? image)
        => image is { IsAvailable: true } ? $"/api/v3/Image/{image.ID}" : null;
    private static string ShowKey(int animeId, int tmdbId) => $"show:{animeId}:{tmdbId}";
    private static string MovieKey(int episodeId, int tmdbId) => $"movie:{episodeId}:{tmdbId}";

    private sealed record LinkSnapshot(
        string Key, int ShokoSeriesId, int AnidbAnimeId, int? AnidbEpisodeId, string SeriesTitle,
        string? EpisodeTitle, string? EpisodeLabel, string? AnidbPosterUrl, TmdbMediaKind Kind, int TmdbId,
        string? OldPosterUrl, IReadOnlyList<EpisodeOption> Episodes);
    private sealed record CheckedLink(
        LinkHealth Health, string? Message, TmdbMediaKind? RedirectKind, int? RedirectId,
        string? RedirectPosterUrl, DateTimeOffset? CheckedAt);
}
