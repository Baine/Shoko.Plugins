using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Plugin.TmdbLinkFixer.Configuration;
using Shoko.Plugin.TmdbLinkFixer.Models;

namespace Shoko.Plugin.TmdbLinkFixer.Services;

public sealed class TmdbLinkFixerService(
    IMetadataService metadataService,
    IMetadataRefreshService refreshService,
    IMetadataLinkingService linkingService,
    TmdbLinkProbe probe,
    ILogger<TmdbLinkFixerService> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CheckedLink> _checks = new(StringComparer.Ordinal);
    private Task<List<LinkSnapshot>>? _snapshotTask;
    private Task? _scanTask;
    private ScanState _scanState = new(false, 0, 0, 0, 0, 0, 0, null, null);

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

    public bool StartScan(bool ignoreCache = false)
    {
        lock (_gate)
        {
            if (_scanTask is { IsCompleted: false })
                return false;
            _snapshotTask = null;
            _scanTask = Task.Run(() => ScanAllAsync(ignoreCache));
            return true;
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length < 2)
            return [];

        var options = new MetadataSearchOptions { Query = query, IncludeRestricted = true, PageSize = 8 };
        var movieTask = linkingService.SearchMovies(MetadataSource.TMDB, options, cancellationToken);
        var showTask = linkingService.SearchSeries(MetadataSource.TMDB, options, cancellationToken);
        await Task.WhenAll(movieTask, showTask).WaitAsync(cancellationToken).ConfigureAwait(false);

        return movieTask.Result.Item1.Select(x => ToSearchResult(x, null, "TMDB search"))
            .Concat(showTask.Result.Item1.Select(x => ToSearchResult(x, null, "TMDB search")))
            .OfType<SearchResult>()
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

    public async Task<ShowMappingOptions?> GetShowMappingOptionsAsync(string key, int targetId, CancellationToken cancellationToken)
    {
        var source = BuildSnapshots().SingleOrDefault(x => x.Key == key);
        if (source is null || targetId <= 0)
            return null;
        return await probe.GetShowMappingOptionsAsync(targetId, cancellationToken).ConfigureAwait(false);
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
        var mappingOnly = source.Kind == TmdbMediaKind.Show &&
            request.TargetKind == TmdbMediaKind.Show &&
            source.TmdbId == request.TargetId;
        if (source.Kind == request.TargetKind && source.TmdbId == request.TargetId && !mappingOnly)
            return new(false, "The existing and proposed links are identical.");

        var targetProbe = await probe.ProbeAsync(request.TargetKind, request.TargetId, cancellationToken).ConfigureAwait(false);
        if (targetProbe.Health != LinkHealth.Valid)
            return new(false, targetProbe.Message ?? "The proposed TMDB target could not be validated. No link was changed.");

        var series = metadataService.GetShokoSeriesByAnidbID(source.AnidbAnimeId);
        if (series is null)
            return new(false, "The Shoko series no longer exists. No link was changed.");

        var episodeMappings = request.EpisodeMappings
            .Where(x => x.AnidbEpisodeId > 0 && x.TmdbEpisodeId > 0)
            .Distinct()
            .ToList();
        // Preserve user mappings that belong to other shows, but never the mappings of the show
        // that is being removed: they are deleted with its link and would become orphaned.
        var dropSourceShow = source.Kind == TmdbMediaKind.Show && !mappingOnly;
        var preservedXrefs = series.Episodes
            .SelectMany(episode => episode.MetadataEpisodeCrossReferences
                .Where(xref => xref is not null && xref.Source == MetadataSource.TMDB && xref.ProviderID!.IsNumericID &&
                    xref.ProviderParentID!.IsNumericID &&
                    int.TryParse(xref.ProviderParentID.ID, out var showId) && showId != request.TargetId &&
                    !(dropSourceShow && showId == source.TmdbId))
                .Select(xref => (Episode: episode.AnidbEpisodeID,
                    ShowId: int.Parse(xref.ProviderParentID!.ID), EpisodeId: int.Parse(xref.ProviderID!.ID))))
            .Distinct()
            .ToList();
        if (request.TargetKind == TmdbMediaKind.Show)
        {
            if (episodeMappings.Count == 0)
                return new(false, "Configure and confirm at least one AniDB to TMDB episode mapping. No link was changed.");
            if (episodeMappings.Select(x => x.AnidbEpisodeId).Distinct().Count() != episodeMappings.Count ||
                episodeMappings.Select(x => x.TmdbEpisodeId).Distinct().Count() != episodeMappings.Count)
                return new(false, "Each AniDB and TMDB episode may appear only once in the confirmed mapping. No link was changed.");
            var sourceEpisodeIds = series.Episodes.Select(x => x.AnidbEpisodeID).ToHashSet();
            if (episodeMappings.Any(x => !sourceEpisodeIds.Contains(x.AnidbEpisodeId)))
                return new(false, "The confirmed mapping contains an AniDB episode outside this series. No link was changed.");
        }

        try
        {
            if (request.TargetKind == TmdbMediaKind.Show)
            {
                var targetShowId = TmdbGuid(MetadataEntityType.Series, request.TargetId);
                await refreshService.RefreshEntry(targetShowId, force: true, new MetadataRefreshOptions
                {
                    DownloadImages = true,
                    DownloadCrewAndCast = false,
                    DownloadAlternateOrdering = false,
                    DownloadNetworks = false,
                    QuickRefresh = false,
                    Reason = MetadataRefreshReason.Requested,
                }, immediate: true, prioritize: true, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);

                var targetShow = metadataService.GetSeries(targetShowId);
                var targetEpisodeIds = targetShow?.Episodes
                    .Where(x => x.ID.IsNumericID)
                    .Select(x => int.Parse(x.ID.ID)).ToHashSet() ?? [];
                if (targetEpisodeIds.Count == 0 || episodeMappings.Any(x => !targetEpisodeIds.Contains(x.TmdbEpisodeId)))
                    return new(false, "One or more confirmed TMDB episodes do not belong to the selected show. No link was changed.");

                if (!mappingOnly)
                {
                    // Remove the old link before adding the replacement: Shoko's RemoveShowLink
                    // historically deleted the episode links of surviving shows when an anime had
                    // multiple show links, so the anime must never hold both links at once.
                    await RemoveSourceAsync(source).WaitAsync(cancellationToken).ConfigureAwait(false);
                    await linkingService.AddSeriesLink(new MetadataSeriesLinkRequest
                    {
                        Source = MetadataSource.TMDB,
                        EntityType = MetadataEntityType.Series,
                        ProviderID = targetShowId,
                        AnidbAnimeID = source.AnidbAnimeId,
                        Additive = true,
                        MatchRating = MatchRating.UserVerified,
                    }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                // AddShowLink invokes Shoko's automatic episode matcher. Replace provisional links
                // for the selected show with the administrator-confirmed mapping, while restoring
                // user mappings belonging to any other show linked to this AniDB anime.
                //
                // A cross-reference can outlive its TMDB episode row (TMDB removed the episode and
                // Shoko purged it). The reset below deletes such xrefs anyway and SetEpisodeLink
                // cannot restore them, so drop them instead of failing the accept.
                var existingEpisodeIds = new Dictionary<int, HashSet<int>>();
                HashSet<int> ExistingEpisodeIds(int showId)
                {
                    if (!existingEpisodeIds.TryGetValue(showId, out var ids))
                    {
                        var show = metadataService.GetSeries(TmdbGuid(MetadataEntityType.Series, showId));
                        ids = existingEpisodeIds[showId] = show?.Episodes
                            .Where(x => x.ID.IsNumericID).Select(x => int.Parse(x.ID.ID)).ToHashSet() ?? [];
                    }
                    return ids;
                }

                var preservedEpisodeMappings = preservedXrefs
                    .Where(x => ExistingEpisodeIds(x.ShowId).Contains(x.EpisodeId))
                    .Select(x => new EpisodeMappingRequest(x.Episode, x.EpisodeId))
                    .Distinct()
                    .ToList();
                var droppedPreserved = preservedXrefs.Count - preservedEpisodeMappings.Count;
                if (droppedPreserved > 0)
                    logger.LogWarning(
                        "Dropped {Count} preserved episode mapping(s) for {LinkKey}: their TMDB episode no longer exists in Shoko.",
                        droppedPreserved, request.Key);

                await linkingService.ResetEpisodeLinks(MetadataSource.TMDB, source.AnidbAnimeId, allowAutoMatch: false, cancellationToken)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                foreach (var group in episodeMappings.Concat(preservedEpisodeMappings).GroupBy(x => x.AnidbEpisodeId))
                {
                    var index = 0;
                    foreach (var mapping in group.DistinctBy(x => x.TmdbEpisodeId))
                    {
                        if (!await linkingService.SetEpisodeLink(
                                MetadataSource.TMDB,
                                mapping.AnidbEpisodeId,
                                TmdbGuid(MetadataEntityType.Episode, mapping.TmdbEpisodeId),
                                additive: index > 0,
                                ordering: index,
                                providerSeriesID: TmdbGuid(MetadataEntityType.Series, request.TargetId),
                                cancellationToken).ConfigureAwait(false))
                            throw new InvalidOperationException($"Could not set the confirmed episode mapping for AniDB episode {mapping.AnidbEpisodeId}.");
                        index++;
                    }
                }
                VerifyConfirmedEpisodeMappings(source.AnidbAnimeId, episodeMappings);
            }
            else
            {
                var episodeId = request.AnidbEpisodeId ?? source.AnidbEpisodeId;
                if (episodeId is null || series.Episodes.All(x => x.AnidbEpisodeID != episodeId.Value))
                    return new(false, "Select an AniDB episode from this series for the movie link. No link was changed.");

                var targetMovieId = TmdbGuid(MetadataEntityType.Movie, request.TargetId);
                await refreshService.RefreshEntry(targetMovieId, force: true, new MetadataRefreshOptions
                {
                    DownloadImages = true,
                    DownloadCrewAndCast = false,
                    DownloadCollections = false,
                    Reason = MetadataRefreshReason.Requested,
                }, immediate: true, prioritize: true, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                await linkingService.AddMovieLink(new MetadataEpisodeLinkRequest
                {
                    Source = MetadataSource.TMDB,
                    EntityType = MetadataEntityType.Movie,
                    ProviderID = targetMovieId,
                    ProviderSeriesID = targetMovieId,
                    AnidbEpisodeID = episodeId.Value,
                    AnidbAnimeID = source.AnidbAnimeId,
                    Additive = true,
                    MatchRating = MatchRating.UserVerified,
                }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                await RemoveSourceAsync(source).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
                _checks.Remove(source.Key);
            logger.LogInformation(
                "User-confirmed TMDB link replacement: {SourceKind} {SourceId} to {TargetKind} {TargetId} for AniDB anime {AnimeId}",
                source.Kind, source.TmdbId, request.TargetKind, request.TargetId, source.AnidbAnimeId);
            return new(true, request.TargetKind == TmdbMediaKind.Show
                ? mappingOnly
                    ? $"The existing TMDB show link was kept and {episodeMappings.Count} confirmed episode mappings were saved."
                    : $"The explicitly selected TMDB link and {episodeMappings.Count} confirmed episode mappings were accepted; the old link was removed."
                : "The explicitly selected TMDB link was accepted and the old link was removed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed applying user-confirmed TMDB replacement for {LinkKey}", request.Key);
            return new(false, "The confirmed operation failed. Link or episode mappings may have been partially applied; refresh the page and check the Shoko log.");
        }
        finally
        {
            lock (_gate)
                _snapshotTask = null;
        }
    }

    private async Task ScanAllAsync(bool ignoreCache)
    {
        var started = DateTimeOffset.UtcNow;
        SetState(new(true, 0, 0, 0, 0, 0, 0, started, null));

        try
        {
            var links = BuildSnapshots();
            SetState(new(true, links.Count, 0, 0, 0, 0, 0, started, null));
            var remoteChecks = new Dictionary<(TmdbMediaKind Kind, int Id), ProbeResult>();
            var completed = 0;
            var valid = 0;
            var problems = 0;
            var errors = 0;
            var cached = 0;

            foreach (var link in links)
            {
                SetCheck(link.Key, new(LinkHealth.Checking, null, null));
                if (!remoteChecks.TryGetValue((link.Kind, link.TmdbId), out var result))
                {
                    if (!ignoreCache && TmdbValidationCache.TryGet(link.Kind, link.TmdbId, out result, out _))
                    {
                        cached++;
                    }
                    else
                    {
                        result = await probe.ProbeAsync(link.Kind, link.TmdbId).ConfigureAwait(false);
                        TmdbValidationCache.Store(link.Kind, link.TmdbId, result);
                    }
                    remoteChecks[(link.Kind, link.TmdbId)] = result;
                }

                SetCheck(link.Key, new(result.Health, result.Message, DateTimeOffset.UtcNow));
                completed++;
                if (result.Health == LinkHealth.Valid) valid++;
                else if (result.Health == LinkHealth.Error) errors++;
                else problems++;
                if (result.Fatal)
                {
                    SetState(new(false, links.Count, completed, valid, problems, errors, cached, started, DateTimeOffset.UtcNow));
                    logger.LogWarning("TMDB link scan stopped after a fatal API validation error: {Message}", result.Message);
                    return;
                }
                SetState(new(true, links.Count, completed, valid, problems, errors, cached, started, null));
            }

            SetState(new(false, links.Count, completed, valid, problems, errors, cached, started, DateTimeOffset.UtcNow));
            logger.LogInformation("TMDB link scan completed: {Total} links, {Valid} valid, {Problems} problems, {Errors} errors, {Cached} cache hits", links.Count, valid, problems, errors, cached);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TMDB link scan failed");
            var state = GetScanState();
            SetState(state with { Running = false, Errors = state.Errors + 1, FinishedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            TmdbValidationCache.Flush();
        }
    }

    private List<LinkSnapshot> BuildSnapshots()
    {
        var result = new List<LinkSnapshot>();
        var allSeries = metadataService.GetAllShokoSeries();
        foreach (var series in allSeries)
        {
            var showRefs = series.MetadataSeriesCrossReferences
                .Where(x => x is not null && x.Source == MetadataSource.TMDB && x.EntityType == MetadataEntityType.Series &&
                    x.AnidbAnimeID > 0 && x.ProviderID!.IsNumericID && int.TryParse(x.ProviderID.ID, out _))
                .Select(x => (x.AnidbAnimeID, TmdbId: int.Parse(x.ProviderID!.ID), Provider: x.Provider))
                .DistinctBy(x => (x.AnidbAnimeID, x.TmdbId)).ToList();
            var movieRefs = series.MetadataMovieCrossReferences
                .Where(x => x is not null && x.Source == MetadataSource.TMDB && x.EntityType == MetadataEntityType.Movie &&
                    x.AnidbEpisodeID > 0 && x.ProviderID!.IsNumericID && int.TryParse(x.ProviderID.ID, out _))
                .Select(x => (x.AnidbAnimeID, x.AnidbEpisodeID, TmdbId: int.Parse(x.ProviderID!.ID), Provider: x.Provider))
                .DistinctBy(x => (x.AnidbEpisodeID, x.TmdbId)).ToList();
            if (showRefs.Count is 0 && movieRefs.Count is 0)
                continue;

            var rawEpisodes = series.Episodes;
            var anidbPosterUrl = ImageUrl(series.AnidbAnime.PrimaryImage);

            foreach (var xref in showRefs)
            {
                var episodes = BuildEpisodeOptions(rawEpisodes, xref.TmdbId);
                result.Add(new(
                    ShowKey(xref.AnidbAnimeID, xref.TmdbId), series.LocalID, xref.AnidbAnimeID, null, [],
                    series.Title, null, null, anidbPosterUrl, TmdbMediaKind.Show, xref.TmdbId,
                    ImageUrl((xref.Provider as IWithPrimaryImage)?.PrimaryImage), episodes));
            }

            foreach (var group in movieRefs.GroupBy(x => (x.AnidbAnimeID, x.TmdbId)))
            {
                var linkedEpisodeIds = group.Select(x => x.AnidbEpisodeID).Distinct().Order().ToList();
                var linkedEpisodes = rawEpisodes
                    .Where(x => linkedEpisodeIds.Contains(x.AnidbEpisodeID))
                    .OrderBy(x => x.Type)
                    .ThenBy(x => x.EpisodeNumber)
                    .ToList();
                var firstEpisode = linkedEpisodes.FirstOrDefault();
                var grouped = linkedEpisodeIds.Count > 1;
                result.Add(new(
                    MovieKey(group.Key.AnidbAnimeID, group.Key.TmdbId),
                    series.LocalID,
                    group.Key.AnidbAnimeID,
                    firstEpisode?.AnidbEpisodeID ?? linkedEpisodeIds[0],
                    linkedEpisodeIds,
                    series.Title,
                    grouped ? string.Join(", ", linkedEpisodes.Select(x => $"{EpisodePrefix(x)}{x.EpisodeNumber}")) : firstEpisode?.Title,
                    grouped ? $"{linkedEpisodeIds.Count} linked AniDB episodes" : firstEpisode is null ? $"AniDB EID {linkedEpisodeIds[0]}" : $"{EpisodePrefix(firstEpisode)}{firstEpisode.EpisodeNumber}",
                    anidbPosterUrl,
                    TmdbMediaKind.Movie,
                    group.Key.TmdbId,
                    ImageUrl((group.Select(x => x.Provider).FirstOrDefault(x => x is not null) as IWithPrimaryImage)?.PrimaryImage),
                    BuildEpisodeOptions(rawEpisodes, null)));
            }
        }
        return result;
    }

    private TmdbLinkItem ToItem(LinkSnapshot link)
    {
        var check = _checks.GetValueOrDefault(link.Key);
        if (check is null && TmdbValidationCache.TryGet(link.Kind, link.TmdbId, out var cached, out var checkedAt))
            check = new(cached.Health, cached.Message, checkedAt);
        check ??= new CheckedLink(LinkHealth.NotChecked, null, null);
        return new(link.Key, link.ShokoSeriesId, link.AnidbAnimeId, link.AnidbEpisodeId, link.SourceAnidbEpisodeIds, link.SeriesTitle,
            link.EpisodeTitle, link.EpisodeLabel, $"https://anidb.net/anime/{link.AnidbAnimeId}", link.AnidbPosterUrl,
            link.Kind, link.TmdbId, TmdbLinkProbe.BuildUri(link.Kind, link.TmdbId).ToString(), link.OldPosterUrl,
            check.Health, check.Message, check.CheckedAt,
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
            var results = await linkingService.PreviewAutoLink(MetadataSource.TMDB, series.AnidbAnimeID, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            candidates.AddRange(results
                .Select(x => ToSearchResult(x.Result, x.AnidbEpisodeID, x.MatchRating.ToString()))
                .OfType<SearchResult>());
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

    private static SearchResult? ToSearchResult(MetadataSearchResult result, int? anidbEpisodeId, string matchReason)
    {
        var kind = result is MetadataMovieSearchResult ? TmdbMediaKind.Movie : TmdbMediaKind.Show;
        if (!int.TryParse(result.ID.ID, out var id))
            return null;
        var partialDate = result switch
        {
            MetadataMovieSearchResult movie => movie.ReleasedAt,
            MetadataSeriesSearchResult show => show.FirstAiredAt,
            _ => null,
        };
        var date = partialDate is { } value && value.Year > 0
            ? new DateOnly(value.Year, value.Month ?? 1, value.Day ?? 1)
            : (DateOnly?)null;
        return new(kind, id, result.Title ?? $"TMDB {id}", result.OriginalTitle ?? result.Title ?? $"TMDB {id}", date,
            result.PosterUrl, result.Overview ?? string.Empty, (double)(result.UserRating ?? 0m),
            TmdbLinkProbe.BuildUri(kind, id).ToString(), anidbEpisodeId, matchReason);
    }

    private async Task RemoveSourceAsync(LinkSnapshot source)
    {
        if (source.Kind == TmdbMediaKind.Show)
        {
            await linkingService.RemoveSeriesLink(new MetadataSeriesLinkRequest
            {
                Source = MetadataSource.TMDB,
                EntityType = MetadataEntityType.Series,
                ProviderID = TmdbGuid(MetadataEntityType.Series, source.TmdbId),
                AnidbAnimeID = source.AnidbAnimeId,
                Purge = false,
            }).ConfigureAwait(false);
            return;
        }

        var movieID = TmdbGuid(MetadataEntityType.Movie, source.TmdbId);
        await Task.WhenAll(source.SourceAnidbEpisodeIds.Select(episodeId =>
            linkingService.RemoveMovieLink(new MetadataEpisodeLinkRequest
            {
                Source = MetadataSource.TMDB,
                EntityType = MetadataEntityType.Movie,
                ProviderID = movieID,
                ProviderSeriesID = movieID,
                AnidbEpisodeID = episodeId,
                AnidbAnimeID = source.AnidbAnimeId,
                Purge = false,
            }))).ConfigureAwait(false);
    }

    // Reads the episode cross-references back after saving. SetEpisodeLink reports success even
    // when a later write (for example a show-link removal inside Shoko) deletes the xref again,
    // so the confirmed mapping must be verified against the actual database state.
    private void VerifyConfirmedEpisodeMappings(int anidbAnimeId, IReadOnlyList<EpisodeMappingRequest> expected)
    {
        var series = metadataService.GetShokoSeriesByAnidbID(anidbAnimeId);
        if (series is null)
            throw new InvalidOperationException("The Shoko series no longer exists after saving episode mappings.");

        var linked = series.Episodes
            .SelectMany(episode => episode.MetadataEpisodeCrossReferences
                .Where(xref => xref is not null && xref.Source == MetadataSource.TMDB && xref.ProviderID!.IsNumericID &&
                    int.TryParse(xref.ProviderID.ID, out _))
                .Select(xref => (Episode: episode.AnidbEpisodeID, TmdbEpisodeID: int.Parse(xref.ProviderID!.ID))))
            .ToHashSet();
        var missing = expected
            .Where(mapping => !linked.Contains((mapping.AnidbEpisodeId, mapping.TmdbEpisodeId)))
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Confirmed episode mappings are missing after saving: {string.Join(", ", missing.Select(m => $"AniDB {m.AnidbEpisodeId} → TMDB {m.TmdbEpisodeId}"))}");
    }

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
    private static MetadataGuid TmdbGuid(MetadataEntityType entityType, int id)
        => new(MetadataSource.TMDB, entityType, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static IReadOnlyList<EpisodeOption> BuildEpisodeOptions(IReadOnlyList<IShokoEpisode> episodes, int? tmdbShowId)
        => episodes
            .OrderBy(x => x.Type)
            .ThenBy(x => x.EpisodeNumber)
            .Select(x => new EpisodeOption(
                x.AnidbEpisodeID,
                $"{EpisodePrefix(x)}{x.EpisodeNumber}: {x.Title}",
                x.Type == EpisodeType.Episode,
                x.EpisodeNumber,
                tmdbShowId.HasValue
                    ? x.MetadataEpisodeCrossReferences
                        .Where(y => y is not null && y.Source == MetadataSource.TMDB && y.ProviderParentID!.IsNumericID &&
                            y.ProviderID!.IsNumericID && y.ProviderParentID.ID == tmdbShowId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .OrderBy(y => y.Ordering)
                        .Select(y => int.Parse(y.ProviderID!.ID))
                        .Distinct()
                        .ToList()
                    : []))
            .ToList();
    private static string ShowKey(int animeId, int tmdbId) => $"show:{animeId}:{tmdbId}";
    private static string MovieKey(int animeId, int tmdbId) => $"movie:{animeId}:{tmdbId}";

    private sealed record LinkSnapshot(
        string Key, int ShokoSeriesId, int AnidbAnimeId, int? AnidbEpisodeId, IReadOnlyList<int> SourceAnidbEpisodeIds, string SeriesTitle,
        string? EpisodeTitle, string? EpisodeLabel, string? AnidbPosterUrl, TmdbMediaKind Kind, int TmdbId,
        string? OldPosterUrl, IReadOnlyList<EpisodeOption> Episodes);
    private sealed record CheckedLink(
        LinkHealth Health, string? Message, DateTimeOffset? CheckedAt);
}
