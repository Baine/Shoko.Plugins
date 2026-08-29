using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Tmdb.CrossReferences;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Events;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;
using Shoko.Plugin.NfoGenerator.Config;
using Shoko.Plugin.NfoGenerator.Jobs;
using Shoko.Plugin.NfoGenerator.Nfo;
using Shoko.QueueProcessor.Abstractions;

namespace Shoko.Plugin.NfoGenerator;

/// <summary>
/// Writes Kodi-style NFO files and artwork sidecars next to video files. Runs
/// automatically on matched releases and on demand per series/episode/import
/// folder/library via <see cref="NfoGeneratorController"/>.
/// </summary>
public sealed class NfoGeneratorService : IHostedService
{
    private readonly IVideoReleaseService _releaseService;
    private readonly ConfigurationProvider<NfoGeneratorSettings> _settings;
    private readonly IMetadataService _metadataService;
    private readonly IVideoService _videoService;
    private readonly IQueueScheduler _queueScheduler;
    private readonly ILogger<NfoGeneratorService> _logger;
    private LibraryRunState? _libraryRun;
    private readonly object _burstCacheGate = new();
    private long _burstCacheVersion;
    private readonly Dictionary<ShowScope, BurstScopeCache> _burstScopes = [];
    private readonly Dictionary<string, BurstValue<bool>> _burstFolderSharing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ShowScope, BurstValue<bool>> _burstShowSharing = [];
    private readonly Dictionary<SweepKey, long> _burstSweeps = [];

    public NfoGeneratorService(
        IVideoReleaseService releaseService,
        ConfigurationProvider<NfoGeneratorSettings> settings,
        IMetadataService metadataService,
        IVideoService videoService,
        IQueueScheduler queueScheduler,
        ILogger<NfoGeneratorService> logger)
    {
        _releaseService = releaseService;
        _settings = settings;
        _metadataService = metadataService;
        _videoService = videoService;
        _queueScheduler = queueScheduler;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _releaseService.ReleaseSaved += OnReleaseSaved;
        _releaseService.ReleaseDeleted += OnReleaseDeleted;
        _metadataService.SeriesUpdated += OnSeriesUpdated;
        _videoService.VideoFileRelocated += OnVideoFileRelocated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _releaseService.ReleaseSaved -= OnReleaseSaved;
        _releaseService.ReleaseDeleted -= OnReleaseDeleted;
        _metadataService.SeriesUpdated -= OnSeriesUpdated;
        _videoService.VideoFileRelocated -= OnVideoFileRelocated;
        InvalidateTopology();
        return Task.CompletedTask;
    }

    private void OnReleaseSaved(object? sender, VideoReleaseSavedEventArgs e)
    {
        InvalidateTopologyForPaths(e.Video.Files.Select(f => f.Path));
        if (!_settings.Load().GenerateOnImport)
            return;
        Queue(job => { job.Kind = NfoGenerationKind.Video; job.ID = e.Video.ID; });
    }

    private void OnSeriesUpdated(object? sender, SeriesInfoUpdatedEventArgs e)
    {
        if (e.SeriesInfo is IShokoSeries series)
            InvalidateTopologyForPaths(series.Episodes.OfType<IShokoEpisode>().SelectMany(e => e.VideoList).SelectMany(v => v.Files).Select(f => f.Path));
        if (!_settings.Load().GenerateOnMetadataUpdate)
            return;
        if (e.SeriesInfo is not IShokoSeries seriesToQueue)
            return;
        // Metadata updates rewrite even unchanged files so the media library
        // sees a fresh mtime after a metadata change.
        Queue(job => { job.Kind = NfoGenerationKind.Series; job.ID = seriesToQueue.ID; job.Force = true; });
    }

    private void OnReleaseDeleted(object? sender, VideoReleaseDeletedEventArgs e)
    {
        if (e.Video is not { } video)
        {
            InvalidateTopology();
            return;
        }
        InvalidateTopologyForPaths(video.Files.Select(f => f.Path));
        foreach (var path in video.Files.Select(f => f.Path))
            Queue(job => { job.Kind = NfoGenerationKind.Delete; job.PreviousPath = path; });
    }

    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs e)
    {
        InvalidateTopologyForPaths([e.PreviousPath, e.File.Path]);
        if (!_settings.Load().GenerateOnImport)
            return;
        Queue(job => { job.Kind = NfoGenerationKind.Relocated; job.ID = e.File.ID; job.PreviousPath = e.PreviousPath; });
    }

    /// <summary>Generates NFO files for every available video file of a series.</summary>
    public int GenerateForSeries(IShokoSeries series, bool force = false)
        => GenerateForSeriesCore(series, force);

    private int GenerateForSeriesCore(IShokoSeries series, bool force, GenerationPass? pass = null, bool sweep = true, LibraryIndex? libraryIndex = null)
        => GenerateForVideos(series.Episodes.OfType<IShokoEpisode>().SelectMany(e => e.VideoList), force, pass, sweep, libraryIndex);

    /// <summary>Generates NFO files for every available video file of an episode.</summary>
    public int GenerateForEpisode(IShokoEpisode episode, bool force = false)
        => GenerateForVideos(episode.VideoList, force);

    /// <summary>Generates NFO files for every available video file of a video.</summary>
    public int GenerateForVideo(IVideo video, bool force = false)
        => GenerateForFiles(video.Files, force);

    /// <summary>Generates NFO files for every available video file inside an import folder.</summary>
    public int GenerateForFolder(IManagedFolder folder, bool force = false)
    {
        var files = _videoService.GetVideoFilesInManagedFolder(folder).ToList();
        int written = GenerateForFiles(files, force);
        try
        {
            var managedRoots = ManagedRootKeys(_videoService.GetAllManagedFolders().Append(folder));
            var cleanup = SweepManagedFolder(folder, IndexAvailableFiles(_videoService.GetAllVideoFiles()), managedRoots);
            _logger.LogInformation(
                "Import folder sweep finished: {Removed} orphan plugin file(s) and {Directories} generated-only folder(s) removed from {Folder}",
                cleanup.Files, cleanup.Directories, folder.Path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Unable to sweep import folder {Folder}", folder.Path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unable to sweep import folder {Folder}", folder.Path);
        }
        return written;
    }

    /// <summary>Generates one library series and returns the next persisted cursor, if any.</summary>
    internal LibraryStepResult GenerateLibraryStep(int seriesIndex, bool force = false)
    {
        var run = _libraryRun ?? CreateLibraryRun();
        if (seriesIndex < run.Series.Count)
        {
            var series = run.Series[seriesIndex];
            _logger.LogInformation("Processing series {Index}/{Total}: {Title} ({SeriesID})", seriesIndex + 1, run.Series.Count, LanguageResolver.Title(series, run.TitleLanguage), series.ID);
            run.Written += GenerateForSeriesCore(series, force, run.Pass, sweep: false, libraryIndex: run.Index);
            if (seriesIndex + 1 < run.Series.Count)
            {
                var next = run.Series[seriesIndex + 1];
                return new(seriesIndex + 1, run.Series.Count, LanguageResolver.Title(next, run.TitleLanguage));
            }
        }
        SweepMisplacedShowNfos(run.Pass);
        _logger.LogInformation("Library generation finished: {Written} NFO file(s) written", run.Written);
        int removed = SweepLibrary();
        _logger.LogInformation("Library generation finished: {Removed} orphan plugin file(s) removed", removed);
        _libraryRun = null;
        return new(null, run.Series.Count, null);
    }

    private LibraryRunState CreateLibraryRun()
    {
        var series = _metadataService.GetAllShokoSeries().ToList();
        _logger.LogInformation("Building library NFO index for {Count} series", series.Count);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pass = new GenerationPass(useBurstCache: false);
        var index = BuildLibraryIndex(series, pass);
        _logger.LogInformation("Library NFO index built in {Elapsed}", System.Diagnostics.Stopwatch.GetElapsedTime(started));
        _logger.LogInformation("Generating NFO files for the entire library: {Count} series", series.Count);
        return _libraryRun = new LibraryRunState(series, index, _settings.Load().TitleLanguage, pass);
    }

    private void Queue(Action<NfoGenerationJob> configure)
        => _ = QueueAsync(configure);

    private async Task QueueAsync(Action<NfoGenerationJob> configure)
    {
        try
        {
            await _queueScheduler.Enqueue(configure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to queue NFO generation");
        }
    }

    private int GenerateForVideos(IEnumerable<IVideo> videos, bool force, GenerationPass? pass = null, bool sweep = true, LibraryIndex? libraryIndex = null)
        => GenerateForFiles(videos.SelectMany(v => v.Files), force, pass, sweep, libraryIndex);

    private int GenerateForFiles(IEnumerable<IVideoFile> files, bool force, GenerationPass? pass = null, bool sweep = true, LibraryIndex? libraryIndex = null, RelocationTiming? timing = null)
    {
        var targets = files.Where(f => f.IsAvailable && f.Video is not null).DistinctBy(f => f.ID).ToList();
        _logger.LogInformation("Generating NFO files for {Count} file(s)", targets.Count);
        pass ??= new GenerationPass();
        foreach (var file in targets)
            RegisterCanonicalSeries(file, pass, libraryIndex);
        timing?.Lap("CanonicalResolution");
        int written = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var file = targets[i];
            _logger.LogInformation("Processing {Index}/{Total} ({Percent}%): {FilePath}", i + 1, targets.Count, (i + 1) * 100 / targets.Count, file.Path);
            var folder = Path.GetDirectoryName(file.Path);
            if (folder is null)
                continue;
            bool folderShared = GetDirectFolderShared(folder, pass);
            timing?.Lap("DirectSharing");
            if (WriteForFile(file, force, allowFolderArt: !folderShared, pass, libraryIndex, timing))
                written++;
        }
        if (sweep)
        {
            var sweepResult = SweepMisplacedShowNfos(pass);
            timing?.MisplacedSweep(sweepResult.Performed, sweepResult.Skipped, sweepResult.Failed);
            timing?.Lap("MisplacedSweep");
        }
        _logger.LogInformation("Generation finished: {Written}/{Total} NFO file(s) written", written, targets.Count);
        return written;
    }

    private bool WriteForFile(IVideoFile file, bool force, bool allowFolderArt, GenerationPass pass, LibraryIndex? libraryIndex, RelocationTiming? timing = null)
    {
        var video = file.Video!;
        var episode = video.Episodes.FirstOrDefault();
        var series = episode?.Series ?? video.Series.FirstOrDefault();
        if (series is null)
            return false;

        var folder = Path.GetDirectoryName(file.Path);
        if (folder is null)
            return false;

        var cfg = _settings.Load();

        if (IsMovie(series, episode))
        {
            if (!allowFolderArt)
                return false;
            var moviePath = Path.Combine(folder, "movie.nfo");
            var movieResult = NfoWriter.WriteMovieDetailed(moviePath, BuildShowNfo(series, episode, SidecarWriter.WriteFolderArt(folder, series, LogSidecarFailure), cfg, ResolveTmdbMovieId(series, episode)), force);
            ThrowOnNfoWriteFailure(moviePath, movieResult);
            timing?.Lap("RootIO");
            return movieResult.Status == NfoWriter.NfoWriteStatus.Written;
        }

        if (episode is null)
            return false;

        var showId = ResolveTmdbShowId(series, episode);
        var scope = showId is { } resolvedShowId
            ? new ShowScope(resolvedShowId, ScopeFolder(folder, libraryIndex))
            : new ShowScope(0, DirectoryKey(folder));
        bool scopeHit = pass.ShowFolders.TryGetValue(scope, out var cachedShowFolder);
        long scopeVersion = pass.ScopeVersions.GetValueOrDefault(scope, CaptureBurstVersion());
        pass.ScopeVersions[scope] = scopeVersion;
        string showFolder;
        if (scopeHit)
            showFolder = cachedShowFolder!;
        else
        {
            showFolder = showId is null ? folder : ResolveShowFolder(folder, showId.Value, libraryIndex, pass.LinkedShowFolders.GetValueOrDefault(scope));
            pass.ShowFolders[scope] = showFolder;
        }
        if (showId is not null && pass.UseBurstCache)
            CacheBurstScope(scope, pass, showFolder, scopeVersion);
        timing?.ScopeCache(scopeHit);
        timing?.Lap("ScopeResolution");
        var thumb = SidecarWriter.WriteThumb(folder, episode, file.ID, LogSidecarFailure);
        var episodePath = Path.ChangeExtension(file.Path, ".nfo");
        var episodeResult = NfoWriter.WriteEpisodeDetailed(episodePath, BuildEpisodeNfo(episode, series, thumb, cfg), force);
        ThrowOnNfoWriteFailure(episodePath, episodeResult);
        bool episodeWritten = episodeResult.Status == NfoWriter.NfoWriteStatus.Written;
        timing?.Lap("EpisodeIO");

        bool showFolderShared = false;
        if (showId is not null)
        {
            long sharingVersion = CaptureBurstVersion();
            if (pass.SharedShowFolders.TryGetValue(scope, out var cachedSharing) && cachedSharing.Version == sharingVersion)
                showFolderShared = cachedSharing.Value;
            else
            {
                showFolderShared = GetShowFolderShared(scope, showFolder, showId.Value, libraryIndex, pass.UseBurstCache);
                if (sharingVersion == CaptureBurstVersion())
                    pass.SharedShowFolders[scope] = new(sharingVersion, showFolderShared);
            }
        }
        timing?.Lap("ShowSharing");
        if (!allowFolderArt || showFolderShared)
        {
            timing?.Lap("RootIO");
            return episodeWritten;
        }
        if (pass.WrittenShowRoots.Contains(scope))
        {
            timing?.Lap("RootIO");
            return episodeWritten;
        }
        var canonicalSeries = pass.CanonicalSeries.GetValueOrDefault(scope) ?? series;
        var showPath = Path.Combine(showFolder, "tvshow.nfo");
        var showResult = NfoWriter.WriteTvShowDetailed(showPath, BuildShowNfo(canonicalSeries, null, SidecarWriter.WriteFolderArt(showFolder, canonicalSeries, LogSidecarFailure), cfg, showId), force);
        ThrowOnNfoWriteFailure(showPath, showResult);
        pass.WrittenShowRoots.Add(scope);
        timing?.Lap("RootIO");
        return episodeWritten || showResult.Status == NfoWriter.NfoWriteStatus.Written;
    }

    private void ThrowOnNfoWriteFailure(string path, NfoWriter.NfoWriteResult result)
    {
        if (result.Status is NfoWriter.NfoWriteStatus.OwnershipReadFailed
            or NfoWriter.NfoWriteStatus.ContentReadFailed
            or NfoWriter.NfoWriteStatus.WriteFailed)
        {
            _logger.LogError(result.Error, "Unable to {Status} NFO at {Path}; retrying", result.Status, path);
            throw new IOException($"Unable to {result.Status} NFO at '{path}'.", result.Error);
        }
    }

    private void LogSidecarFailure(string sourcePath, string targetPath, Exception error)
        => _logger.LogWarning(error, "Unable to copy artwork sidecar from {SourcePath} to {TargetPath}", sourcePath, targetPath);

    /// <summary>
    /// TMDB data decides whether an entry is a movie. OVAs and specials that
    /// AniDB types as TV shows or specials are treated as movies when linked to
    /// a TMDB movie. Falls back to the AniDB "Movie" type when TMDB has no
    /// links for the entry.
    /// </summary>
    private static bool IsMovie(IShokoSeries series, IShokoEpisode? episode)
    {
        if (episode?.TmdbMovieCrossReferences.Count > 0)
            return true;
        if (series.TmdbMovieCrossReferences.Count > 0)
            return true;
        if (series.TmdbShowCrossReferences.Count > 0)
            return false;
        return series.Type == AnimeType.Movie;
    }

    private void RegisterCanonicalSeries(IVideoFile file, GenerationPass pass, LibraryIndex? libraryIndex)
    {
        var episode = file.Video?.Episodes.FirstOrDefault();
        var series = episode?.Series ?? file.Video?.Series.FirstOrDefault();
        var folder = Path.GetDirectoryName(file.Path);
        var showId = ResolveTmdbShowId(file);
        if (episode is null || series is null || folder is null || showId is null || IsMovie(series, episode))
            return;
        var scope = new ShowScope(showId.Value, ScopeFolder(folder, libraryIndex));
        long capturedVersion = CaptureBurstVersion();
        pass.ScopeVersions.TryAdd(scope, capturedVersion);
        if (libraryIndex is null && pass.UseBurstCache && TryApplyBurstScope(scope, pass))
            return;
        RegisterCanonicalSeries(series, episode, file, scope, pass, libraryIndex);
        if (libraryIndex is not null)
            return;
        var linkedSeries = SelectLinkedTmdbSeries(series, episode).ToList();
        if (linkedSeries.Count > 0)
        {
            if (linkedSeries.All(candidate => candidate.ID != series.ID))
                linkedSeries.Add(series);
            foreach (var candidate in linkedSeries)
                foreach (var candidateEpisode in candidate.Episodes.OfType<IShokoEpisode>())
                    foreach (var candidateFile in candidateEpisode.VideoList.SelectMany(v => v.Files).Where(f => f.IsAvailable))
                        RegisterCanonicalSeries(candidate, candidateEpisode, candidateFile, scope, pass, libraryIndex);
            pass.CanonicalScopesDiscovered.Add(scope);
            if (pass.UseBurstCache)
                CacheBurstScope(scope, pass, null, capturedVersion);
            return;
        }
        if (!pass.CanonicalScopesDiscovered.Add(scope))
            return;
        foreach (var candidate in _metadataService.GetAllShokoSeries())
            foreach (var candidateEpisode in candidate.Episodes.OfType<IShokoEpisode>())
                foreach (var candidateFile in candidateEpisode.VideoList.SelectMany(v => v.Files).Where(f => f.IsAvailable))
                    RegisterCanonicalSeries(candidate, candidateEpisode, candidateFile, scope, pass, libraryIndex);
        if (pass.UseBurstCache)
            CacheBurstScope(scope, pass, null, capturedVersion);
    }

    private void RegisterCanonicalSeries(IShokoSeries series, IShokoEpisode episode, IVideoFile file, ShowScope scope, GenerationPass pass, LibraryIndex? libraryIndex)
    {
        var folder = Path.GetDirectoryName(file.Path);
        var fileShowId = ResolveTmdbShowId(file);
        if (folder is null || fileShowId != scope.TmdbShowId || IsMovie(series, episode) || !PathsEqual(ScopeFolder(folder, libraryIndex), scope.ManagedFolderPath))
            return;
        if (!pass.LinkedShowFolders.TryGetValue(scope, out var folders))
            pass.LinkedShowFolders[scope] = folders = [];
        if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            folders.Add(folder);
        if (!pass.CanonicalSeries.TryGetValue(scope, out var canonical) || series.ID < canonical.ID)
            pass.CanonicalSeries[scope] = series;
    }

    private static ITmdbEpisodeCrossReference? SelectTmdbEpisodeCrossReference(IShokoEpisode episode)
        => episode.TmdbEpisodeCrossReferences
            .OrderBy(x => x.MatchRating == MatchRating.UserVerified ? 0 : 1)
            .ThenBy(x => x.Ordering)
            .ThenBy(x => x.TmdbEpisodeID)
            .FirstOrDefault();

    private static ITmdbMovieCrossReference? SelectTmdbMovieCrossReference(IEnumerable<ITmdbMovieCrossReference> references)
        => references
            .OrderBy(x => x.MatchRating == MatchRating.UserVerified ? 0 : 1)
            .ThenBy(x => x.TmdbMovieID)
            .FirstOrDefault();

    private static ITmdbShowCrossReference? SelectTmdbShowCrossReference(IEnumerable<ITmdbShowCrossReference> references)
        => references
            .OrderBy(x => x.MatchRating == MatchRating.UserVerified ? 0 : 1)
            .ThenBy(x => x.TmdbShowID)
            .FirstOrDefault();

    private static IEnumerable<IShokoSeries> SelectLinkedTmdbSeries(IShokoSeries series, IShokoEpisode episode)
    {
        // The selected TMDB show implements ISeries, whose ShokoSeries property
        // is the complete linked-series set. Do not combine it with direct video
        // links or a second cross-reference.
        var episodeCrossReference = SelectTmdbEpisodeCrossReference(episode);
        var tmdbShow = episodeCrossReference is not null
            ? episodeCrossReference.TmdbShow
            : SelectTmdbShowCrossReference(series.TmdbShowCrossReferences)?.TmdbShow;
        return tmdbShow?.ShokoSeries?
            .DistinctBy(x => x.ID)
            ?? [];
    }

    private static int? ResolveTmdbShowId(IShokoSeries series, IShokoEpisode episode)
        => SelectTmdbEpisodeCrossReference(episode)?.TmdbShowID
            ?? SelectTmdbShowCrossReference(series.TmdbShowCrossReferences)?.TmdbShowID;

    private static int? ResolveTmdbShowId(IVideoFile file)
    {
        var episode = file.Video?.Episodes.FirstOrDefault();
        var series = episode?.Series ?? file.Video?.Series.FirstOrDefault();
        return episode is null || series is null ? null : ResolveTmdbShowId(series, episode);
    }

    private static int? ResolveTmdbMovieId(IShokoSeries series, IShokoEpisode? episode)
        => (episode is null ? null : SelectTmdbMovieCrossReference(episode.TmdbMovieCrossReferences)?.TmdbMovieID)
            ?? SelectTmdbMovieCrossReference(series.TmdbMovieCrossReferences)?.TmdbMovieID;

    /// <summary>
    /// Resolves a conventional show root without moving media. Multiple local
    /// Shoko series may represent seasons of the same TMDB show, so their file
    /// directories are considered together, but only inside the same managed
    /// folder as the current file.
    /// </summary>
    private string ResolveShowFolder(string fileFolder, int tmdbShowId, LibraryIndex? libraryIndex = null, IReadOnlyList<string>? linkedFolders = null)
    {
        var managedFolder = ResolveManagedFolder(fileFolder, libraryIndex);
        if (managedFolder is null)
            return fileFolder;

        var scope = new ShowScope(tmdbShowId, DirectoryKey(managedFolder.Path));
        IEnumerable<string> knownFolders = linkedFolders?.AsEnumerable()
            ?? libraryIndex?.ShowFolders.GetValueOrDefault(scope)?.AsEnumerable()
            ?? _metadataService.GetAllShokoSeries()
            .SelectMany(s => s.Episodes.OfType<IShokoEpisode>().Where(e => ResolveTmdbShowId(s, e) == tmdbShowId)
                .SelectMany(e => e.VideoList)
                .SelectMany(v => v.Files)
                .Where(f => f.IsAvailable && Path.GetDirectoryName(f.Path) is not null)
                .Select(f => Path.GetDirectoryName(f.Path)!));
        List<string> folders = knownFolders
            .Where(path => IsPathWithin(path, managedFolder.Path))
            .Append(fileFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var common = CommonDirectory(folders);
        if (folders.Count > 1 && common is not null && !PathsEqual(common, managedFolder.Path))
            return common;

        // A lone populated season folder cannot yield a common parent. Apply a
        // narrow, conventional-name heuristic rather than promoting the media
        // library root to a show root.
        if (LooksLikeSeasonFolder(fileFolder) && Path.GetDirectoryName(fileFolder) is { } parent && !PathsEqual(parent, managedFolder.Path))
            return parent;
        return fileFolder;
    }

    private IManagedFolder? ResolveManagedFolder(string path, LibraryIndex? libraryIndex)
        => (libraryIndex?.ManagedFolders ?? _videoService.GetAllManagedFolders())
            .Where(f => IsPathWithin(path, f.Path))
            .OrderByDescending(f => f.Path.Length)
            .FirstOrDefault();

    private string ScopeFolder(string fileFolder, LibraryIndex? libraryIndex)
        => DirectoryKey(ResolveManagedFolder(fileFolder, libraryIndex)?.Path ?? fileFolder);

    private bool TryGetBurstFolderShared(string folder, out bool shared)
    {
        lock (_burstCacheGate)
        {
            if (_burstFolderSharing.TryGetValue(folder, out var cached) && cached.Version == _burstCacheVersion)
            {
                shared = cached.Value;
                return true;
            }
            shared = false;
            return false;
        }
    }

    private void CacheBurstFolderShared(string folder, bool shared, long capturedVersion)
    {
        lock (_burstCacheGate)
        {
            if (capturedVersion != _burstCacheVersion)
                return;
            TrimBurstCache();
            if (capturedVersion == _burstCacheVersion)
                _burstFolderSharing[folder] = new(capturedVersion, shared);
        }
    }

    private bool TryGetBurstShowShared(ShowScope scope, out bool shared)
    {
        lock (_burstCacheGate)
        {
            if (_burstShowSharing.TryGetValue(scope, out var cached) && cached.Version == _burstCacheVersion)
            {
                shared = cached.Value;
                return true;
            }
            shared = false;
            return false;
        }
    }

    private void CacheBurstShowShared(ShowScope scope, bool shared, long capturedVersion)
    {
        lock (_burstCacheGate)
        {
            if (capturedVersion != _burstCacheVersion)
                return;
            TrimBurstCache();
            if (capturedVersion == _burstCacheVersion)
                _burstShowSharing[scope] = new(capturedVersion, shared);
        }
    }

    private bool TryGetBurstSweep(SweepKey key)
    {
        lock (_burstCacheGate)
            return _burstSweeps.TryGetValue(key, out var version) && version == _burstCacheVersion;
    }

    private void CacheBurstSweep(SweepKey key, long capturedVersion)
    {
        lock (_burstCacheGate)
        {
            if (capturedVersion != _burstCacheVersion)
                return;
            TrimBurstCache();
            if (capturedVersion == _burstCacheVersion)
                _burstSweeps[key] = capturedVersion;
        }
    }

    private long CaptureBurstVersion()
    {
        lock (_burstCacheGate)
            return _burstCacheVersion;
    }

    private bool TryApplyBurstScope(ShowScope scope, GenerationPass pass)
    {
        lock (_burstCacheGate)
        {
            if (!_burstScopes.TryGetValue(scope, out var cached) || cached.Version != _burstCacheVersion)
                return false;
            if (cached.Root is not null)
                pass.ShowFolders[scope] = cached.Root;
            pass.LinkedShowFolders[scope] = cached.Folders.ToList();
            if (cached.Canonical is not null)
                pass.CanonicalSeries[scope] = cached.Canonical;
            pass.CanonicalScopesDiscovered.Add(scope);
            return true;
        }
    }

    private void CacheBurstScope(ShowScope scope, GenerationPass pass, string? root, long capturedVersion)
    {
        lock (_burstCacheGate)
        {
            if (capturedVersion != _burstCacheVersion)
                return;
            TrimBurstCache();
            if (capturedVersion != _burstCacheVersion)
                return;
            if (!_burstScopes.TryGetValue(scope, out var cached) || cached.Version != capturedVersion)
                _burstScopes[scope] = cached = new BurstScopeCache(capturedVersion);
            if (root is not null)
                cached.Root = root;
            if (pass.LinkedShowFolders.TryGetValue(scope, out var folders))
                foreach (var folder in folders.Where(folder => !cached.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
                    cached.Folders.Add(folder);
            if (pass.CanonicalSeries.TryGetValue(scope, out var canonical) && (cached.Canonical is null || canonical.ID < cached.Canonical.ID))
                cached.Canonical = canonical;
        }
    }

    private void TrimBurstCache()
    {
        if (_burstScopes.Count + _burstFolderSharing.Count + _burstShowSharing.Count + _burstSweeps.Count < 512)
            return;
        _burstScopes.Clear();
        _burstFolderSharing.Clear();
        _burstShowSharing.Clear();
        _burstSweeps.Clear();
        _burstCacheVersion++;
    }

    private void InvalidateTopology()
    {
        lock (_burstCacheGate)
        {
            _burstCacheVersion++;
            _burstScopes.Clear();
            _burstFolderSharing.Clear();
            _burstShowSharing.Clear();
            _burstSweeps.Clear();
        }
    }

    private void InvalidateTopologyForPaths(IEnumerable<string> paths)
    {
        var affected = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToList();
        if (affected.Count == 0)
        {
            InvalidateTopology();
            return;
        }
        lock (_burstCacheGate)
        {
            _burstCacheVersion++;
            foreach (var key in _burstScopes.Keys.Where(key => affected.Any(path => PathAffects(path, key.ManagedFolderPath))).ToList())
                _burstScopes.Remove(key);
            foreach (var key in _burstFolderSharing.Keys.Where(key => affected.Any(path => PathAffects(path, key))).ToList())
                _burstFolderSharing.Remove(key);
            foreach (var key in _burstShowSharing.Keys.Where(key => affected.Any(path => PathAffects(path, key.ManagedFolderPath))).ToList())
                _burstShowSharing.Remove(key);
            foreach (var key in _burstSweeps.Keys.Where(key => affected.Any(path => PathAffects(path, key.Root))).ToList())
                _burstSweeps.Remove(key);
        }
    }

    private static bool PathAffects(string path, string root)
        => IsPathWithin(path, root) || IsPathWithin(root, path);

    private static string? CommonDirectory(IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
            return null;
        var common = Path.GetFullPath(directories[0]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var directory in directories.Skip(1))
        {
            var candidate = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!IsPathWithin(candidate, common))
            {
                common = Path.GetDirectoryName(common) ?? "";
                if (string.IsNullOrEmpty(common))
                    return null;
            }
        }
        return common;
    }

    private static bool LooksLikeSeasonFolder(string path)
        => Path.GetFileName(path).StartsWith("season", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^s\\d{1,2}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != "..");
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static EpisodeNfo BuildEpisodeNfo(IShokoEpisode episode, IShokoSeries series, string? thumb, NfoGeneratorSettings cfg)
    {
        var tmdbEpisode = SelectTmdbEpisodeCrossReference(episode);
        var ordering = tmdbEpisode?.TmdbEpisode?.PreferredOrdering ?? tmdbEpisode?.TmdbEpisode?.Ordering;
        return new()
        {
            Title = LanguageResolver.Title(episode, cfg.TitleLanguage),
            ShowTitle = LanguageResolver.Title(series, cfg.TitleLanguage),
            Plot = LanguageResolver.Description(episode, cfg.DescriptionLanguage) ?? LanguageResolver.Description(series, cfg.DescriptionLanguage),
            Aired = episode.AirDate?.ToString("yyyy-MM-dd"),
            Season = ordering?.SeasonNumber ?? episode.SeasonNumber,
            Episode = ordering?.EpisodeNumber ?? episode.EpisodeNumber,
            RuntimeMinutes = RuntimeMinutes(episode.Runtime),
            Rating = PositiveRating(episode.Rating),
            Votes = PositiveVotes(episode.RatingVotes),
            AnidbId = episode.AnidbEpisodeID.ToString(),
            ShokoId = episode.ID.ToString(),
            TmdbId = tmdbEpisode?.TmdbEpisodeID.ToString(),
            Thumb = thumb,
        };
    }

    private static ShowNfo BuildShowNfo(IShokoSeries series, IShokoEpisode? episode, IReadOnlyDictionary<string, string> art, NfoGeneratorSettings cfg, int? tmdbId)
    {
        var airDate = series.AirDate;
        return new ShowNfo
        {
            Title = LanguageResolver.Title(series, cfg.TitleLanguage),
            OriginalTitle = series.DefaultTitle.Value,
            Plot = LanguageResolver.Description(series, cfg.DescriptionLanguage) ?? (episode is null ? null : LanguageResolver.Description(episode, cfg.DescriptionLanguage)),
            Premiered = airDate?.ToString(),
            Year = airDate?.Year,
            RuntimeMinutes = episode is null ? null : RuntimeMinutes(episode.Runtime),
            Rating = PositiveRating(series.Rating),
            Votes = PositiveVotes(series.RatingVotes),
            AnidbId = series.AnidbAnimeID.ToString(),
            ShokoId = series.ID.ToString(),
            TmdbId = tmdbId?.ToString(),
            Studios = series.Studios.Select(s => s.Name).ToList(),
            Art = art,
        };
    }

    // Folder-level NFOs are cleaned when a delete or relocation leaves a
    // folder without any available descendant video. Legacy plugin tvshow.nfo
    // files inside season directories are additionally swept after a TMDB
    // show-root NFO is generated.
    public void DeleteForPath(string videoPath)
    {
        bool nfoRemoved = DeleteNfo(videoPath);
        SweepFolder(Path.GetDirectoryName(videoPath), nfoRemoved);
    }

    public void GenerateForRelocatedFile(IVideoFile file, string previousPath)
    {
        var timing = new RelocationTiming(_logger);
        try
        {
            DeleteForPath(previousPath);
            timing.Lap("OldPathCleanup");
            GenerateForFiles([file], force: false, timing: timing);
        }
        finally
        {
            timing.Finish();
        }
    }

    private bool DeleteNfo(string videoPath)
    {
        var nfoPath = Path.ChangeExtension(videoPath, ".nfo");
        if (FolderNfos.Contains(Path.GetFileName(nfoPath), StringComparer.OrdinalIgnoreCase))
            return false;
        var ownership = ProbeOwnership(nfoPath);
        if (ownership.Status == OwnershipProbe.Failed)
            _logger.LogWarning(ownership.Error, "Unable to inspect NFO ownership: {Path}", nfoPath);
        else if (ownership.Status == OwnershipProbe.Owned)
        {
            var deletion = TryDelete(nfoPath);
            if (deletion.Status == DeleteStatus.Failed)
                _logger.LogWarning(deletion.Error, "Unable to delete plugin-owned NFO: {Path}", nfoPath);
            return deletion.Status == DeleteStatus.Deleted;
        }
        return false;
    }

    private static readonly string[] FolderNfos = ["tvshow.nfo", "movie.nfo"];

    /// <summary>
    /// Removes stale plugin output at the old path and walks towards the owning
    /// import folder. Generated-only subfolders are removed, but the import
    /// folder itself is never deleted.
    /// </summary>
    private void SweepFolder(string? folder, bool directNfoRemoved)
    {
        if (folder is null)
            return;
        try
        {
            var managedFolder = ResolveManagedFolder(folder, null);
            if (managedFolder is null)
            {
                if (!FolderHasAvailableVideoFiles(folder))
                    DeleteFolderNfos(folder);
                return;
            }

            for (var current = folder; current is not null && IsPathWithin(current, managedFolder.Path); current = Path.GetDirectoryName(current))
            {
                if (FolderHasAvailableVideoFiles(current))
                    break;
                if (!Directory.Exists(current))
                    continue;

                int removedFolderNfos = DeleteFolderNfos(current);
                if (PathsEqual(current, managedFolder.Path))
                    break;
                bool removedOutput = removedFolderNfos > 0
                    || (directNfoRemoved && PathsEqual(current, folder));
                _ = TryDeleteGeneratedOnlyDirectory(current, removedOutput);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Unable to sweep folder NFOs at {Folder}", folder);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unable to sweep folder NFOs at {Folder}", folder);
        }
    }

    /// <summary>
    /// Full-library orphan sweep: walks every managed folder, removes per-file
    /// episode NFOs whose direct video file is gone and folder-level plugin NFOs
    /// in folders with no available descendant video files left. A bottom-up
    /// pass then deletes non-root folders containing only recognized plugin
    /// output.
    /// </summary>
    private int SweepLibrary()
    {
        var filesByDir = IndexAvailableFiles(_videoService.GetAllVideoFiles());
        var managedFolders = _videoService.GetAllManagedFolders().ToList();
        var managedRoots = ManagedRootKeys(managedFolders);
        var total = new DirectoryCleanupResult();
        foreach (var managedFolder in managedFolders)
        {
            try
            {
                total += SweepManagedFolder(managedFolder, filesByDir, managedRoots);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Unable to sweep managed folder {Folder}", managedFolder.Path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unable to sweep managed folder {Folder}", managedFolder.Path);
            }
        }
        _logger.LogInformation(
            "Library sweep finished: {Removed} orphan plugin file(s) and {Directories} generated-only folder(s) removed",
            total.Files, total.Directories);
        return total.Files;
    }

    private static Dictionary<string, List<string>> IndexAvailableFiles(IEnumerable<IVideoFile> files)
        => files
            .Where(f => f.IsAvailable)
            .GroupBy(f => Path.GetDirectoryName(f.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList(), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ManagedRootKeys(IEnumerable<IManagedFolder> managedFolders)
        => managedFolders.Select(folder => DirectoryKey(folder.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private DirectoryCleanupResult SweepManagedFolder(
        IManagedFolder managedFolder,
        IReadOnlyDictionary<string, List<string>> filesByDir,
        IReadOnlySet<string> managedRoots)
    {
        var directories = EnumerateFolders(managedFolder.Path).ToList();
        int removedFiles = 0;
        var directoriesWithRemovedOutput = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories)
        {
            filesByDir.TryGetValue(dir, out var live);
            int removedInDirectory = SweepDirectory(dir, live);
            removedFiles += removedInDirectory;
            if (removedInDirectory > 0)
                directoriesWithRemovedOutput.Add(DirectoryKey(dir));
        }

        var result = new DirectoryCleanupResult(removedFiles, 0);
        foreach (var dir in directories
            .Where(dir => !managedRoots.Contains(DirectoryKey(dir)))
            .OrderByDescending(dir => dir.Length))
            result += TryDeleteGeneratedOnlyDirectory(dir, directoriesWithRemovedOutput.Contains(DirectoryKey(dir)));
        return result;
    }

    private static IEnumerable<string> EnumerateFolders(string root)
    {
        yield return root;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            yield return dir;
    }

    private int SweepDirectory(string dir, IReadOnlyList<string>? liveVideoPaths)
    {
        int removed = 0;
        var live = liveVideoPaths ?? [];
        var liveNfoPaths = live.Select(p => Path.ChangeExtension(p, ".nfo")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasAvailableVideo = FolderHasAvailableVideoFiles(dir);
        foreach (var nfoPath in Directory.GetFiles(dir, "*.nfo"))
        {
            bool folderNfo = FolderNfos.Contains(Path.GetFileName(nfoPath), StringComparer.OrdinalIgnoreCase);
            if (folderNfo && hasAvailableVideo)
                continue;
            if (!liveNfoPaths.Contains(nfoPath))
            {
                var ownership = ProbeOwnership(nfoPath);
                if (ownership.Status == OwnershipProbe.Failed)
                    _logger.LogWarning(ownership.Error, "Unable to inspect NFO ownership during library sweep: {Path}", nfoPath);
                else if (ownership.Status == OwnershipProbe.Owned)
                {
                    var deletion = TryDelete(nfoPath);
                    if (deletion.Status == DeleteStatus.Deleted)
                        removed++;
                    else if (deletion.Status == DeleteStatus.Failed)
                        _logger.LogWarning(deletion.Error, "Unable to delete plugin-owned NFO during library sweep: {Path}", nfoPath);
                }
            }
        }
        return removed;
    }

    private int DeleteFolderNfos(string dir)
    {
        int removed = 0;
        foreach (var pattern in FolderNfos)
            foreach (var path in Directory.GetFiles(dir, pattern))
            {
                var ownership = ProbeOwnership(path);
                if (ownership.Status == OwnershipProbe.Failed)
                    _logger.LogWarning(ownership.Error, "Unable to inspect NFO ownership during folder sweep: {Path}", path);
                else if (ownership.Status == OwnershipProbe.Owned)
                {
                    var deletion = TryDelete(path);
                    if (deletion.Status == DeleteStatus.Deleted)
                        removed++;
                    else if (deletion.Status == DeleteStatus.Failed)
                        _logger.LogWarning(deletion.Error, "Unable to delete plugin-owned NFO during folder sweep: {Path}", path);
                }
            }
        return removed;
    }

    /// <summary>
    /// Deletes a directory only when it has no child directories and every
    /// direct file is recognizable as plugin output. NFO ownership is verified
    /// through the embedded marker; artwork is constrained to the exact
    /// filenames produced by <see cref="SidecarWriter"/>. Empty directories
    /// are eligible only when this sweep just removed plugin-owned output from
    /// them, so unrelated empty directory structures are retained.
    /// </summary>
    private DirectoryCleanupResult TryDeleteGeneratedOnlyDirectory(string dir, bool deleteIfEmpty = false)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(dir);
            if (!directoryInfo.Exists || directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || Directory.EnumerateDirectories(dir).Any())
                return new();

            var files = Directory.GetFiles(dir);
            if (files.Length == 0 && !deleteIfEmpty)
                return new();

            foreach (var path in files)
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    return new();
                if (!Path.GetExtension(path).Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                {
                    if (!SidecarWriter.IsGeneratedSidecarName(path))
                        return new();
                    continue;
                }

                var ownership = ProbeOwnership(path);
                if (ownership.Status == OwnershipProbe.Failed)
                {
                    _logger.LogWarning(ownership.Error, "Unable to inspect NFO ownership before generated-only folder cleanup: {Path}", path);
                    return new();
                }
                if (ownership.Status != OwnershipProbe.Owned)
                    return new();
            }

            int removed = 0;
            foreach (var path in files)
            {
                var deletion = TryDelete(path);
                if (deletion.Status == DeleteStatus.Deleted)
                    removed++;
                else if (deletion.Status == DeleteStatus.Failed)
                {
                    _logger.LogWarning(deletion.Error, "Unable to delete plugin output during generated-only folder cleanup: {Path}", path);
                    return new(removed, 0);
                }
            }

            try
            {
                Directory.Delete(dir, recursive: false);
                return new(removed, 1);
            }
            catch (DirectoryNotFoundException)
            {
                return new(removed, 0);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Unable to delete generated-only folder {Folder}", dir);
                return new(removed, 0);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unable to delete generated-only folder {Folder}", dir);
                return new(removed, 0);
            }
        }
        catch (DirectoryNotFoundException)
        {
            return new();
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Unable to inspect folder for generated-only cleanup: {Folder}", dir);
            return new();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unable to inspect folder for generated-only cleanup: {Folder}", dir);
            return new();
        }
    }

    private bool FolderHasAvailableVideoFiles(string folder)
        => _videoService.GetVideoFilesByAbsolutePath(folder)
            .Any(f => f.IsAvailable
                && Path.GetDirectoryName(f.Path) is { } fileFolder
                && IsPathWithin(fileFolder, folder));

    /// <summary>True when the folder holds live files of more than one series; folder-level NFOs/art must not be written there.</summary>
    private bool IsFolderShared(string folder)
    {
        var seriesIds = _videoService.GetVideoFilesByAbsolutePath(folder)
            .Where(f => string.Equals(Path.GetDirectoryName(f.Path), folder, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Video?.Episodes.FirstOrDefault()?.Series?.ID)
            .Where(id => id is not null)
            .Distinct()
            .ToList();
        return seriesIds.Count > 1;
    }

    private bool GetDirectFolderShared(string folder, GenerationPass pass)
    {
        var key = DirectoryKey(folder);
        bool shared;
        long currentVersion = CaptureBurstVersion();
        if (pass.FolderShared.TryGetValue(key, out var cached) && cached.Version == currentVersion)
            return cached.Value;
        if (pass.UseBurstCache && TryGetBurstFolderShared(key, out shared))
        {
            pass.FolderShared[key] = new(CaptureBurstVersion(), shared);
            return shared;
        }
        long capturedVersion = CaptureBurstVersion();
        shared = IsFolderShared(folder);
        if (capturedVersion == CaptureBurstVersion())
            pass.FolderShared[key] = new(capturedVersion, shared);
        if (pass.UseBurstCache)
            CacheBurstFolderShared(key, shared, capturedVersion);
        return shared;
    }

    /// <summary>
    /// A show root may contain only season folders, so the older direct-child
    /// guard is insufficient there. Do not write root-level metadata when any
    /// live file below it belongs to another (or unmapped) TMDB show.
    /// </summary>
    private bool IsShowFolderShared(string folder, int tmdbShowId, LibraryIndex? libraryIndex = null)
    {
        if (libraryIndex?.FolderContents.TryGetValue(DirectoryKey(folder), out var contents) == true)
            return contents.FileCount != contents.ShowFileCounts.GetValueOrDefault(tmdbShowId);
        return _videoService.GetVideoFilesByAbsolutePath(folder)
            .Where(f => f.IsAvailable && Path.GetDirectoryName(f.Path) is { } fileFolder && IsPathWithin(fileFolder, folder))
            .Any(f => ResolveTmdbShowId(f) != tmdbShowId);
    }

    private bool GetShowFolderShared(ShowScope scope, string folder, int tmdbShowId, LibraryIndex? libraryIndex, bool useBurstCache)
    {
        if (useBurstCache && TryGetBurstShowShared(scope, out var shared))
            return shared;
        long capturedVersion = CaptureBurstVersion();
        shared = IsShowFolderShared(folder, tmdbShowId, libraryIndex);
        if (useBurstCache)
            CacheBurstShowShared(scope, shared, capturedVersion);
        return shared;
    }

    private LibraryIndex BuildLibraryIndex(IReadOnlyList<IShokoSeries> seriesList, GenerationPass pass)
    {
        var managedFolders = _videoService.GetAllManagedFolders().ToList();
        var showFolders = new Dictionary<ShowScope, List<string>>();
        var folderContents = new Dictionary<string, FolderContents>(StringComparer.OrdinalIgnoreCase);
        var index = new LibraryIndex(managedFolders, showFolders, folderContents);
        foreach (var file in seriesList.SelectMany(s => s.Episodes.OfType<IShokoEpisode>().SelectMany(e => e.VideoList).SelectMany(v => v.Files)))
        {
            if (!file.IsAvailable || Path.GetDirectoryName(file.Path) is null || file.Video is null)
                continue;
            RegisterCanonicalSeries(file, pass, index);
            var episode = file.Video.Episodes.FirstOrDefault();
            var series = episode?.Series ?? file.Video.Series.FirstOrDefault();
            var showId = ResolveTmdbShowId(file);
            if (showId is null || series is null || IsMovie(series, episode))
                continue;
            var managedFolder = managedFolders.Where(m => IsPathWithin(file.Path, m.Path)).OrderByDescending(m => m.Path.Length).FirstOrDefault();
            if (managedFolder is null)
                continue;
            var scope = new ShowScope(showId.Value, DirectoryKey(managedFolder.Path));
            if (!showFolders.TryGetValue(scope, out var destinations))
                showFolders[scope] = destinations = [];
            destinations.Add(Path.GetDirectoryName(file.Path)!);
        }

        foreach (var file in _videoService.GetAllVideoFiles().Where(f => f.IsAvailable))
        {
            var directory = Path.GetDirectoryName(file.Path);
            if (directory is null)
                continue;
            var managedFolder = managedFolders.Where(f => IsPathWithin(directory, f.Path)).OrderByDescending(f => f.Path.Length).FirstOrDefault();
            if (managedFolder is null)
                continue;
            var showId = ResolveTmdbShowId(file);
            for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            {
                var key = DirectoryKey(current);
                if (!folderContents.TryGetValue(key, out var contents))
                    folderContents[key] = contents = new FolderContents();
                contents.FileCount++;
                if (showId is not null)
                    contents.ShowFileCounts[showId.Value] = contents.ShowFileCounts.GetValueOrDefault(showId.Value) + 1;
                if (PathsEqual(current, managedFolder.Path))
                    break;
            }
        }
        _logger.LogInformation("Indexed {ShowCount} TMDB show(s) and {FolderCount} media folder(s) for library generation", showFolders.Count, folderContents.Count);
        return index;
    }

    private static string DirectoryKey(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private readonly record struct ShowScope(int TmdbShowId, string ManagedFolderPath);
    private readonly record struct SweepKey(int TmdbShowId, string Root);
    private readonly record struct MisplacedSweepResult(int Performed, int Skipped, int Failed);
    private readonly record struct BurstValue<T>(long Version, T Value);

    private enum OwnershipProbe
    {
        NotCandidate,
        Owned,
        Failed,
    }

    private readonly record struct OwnershipProbeResult(OwnershipProbe Status, Exception? Error);
    private enum DeleteStatus
    {
        Deleted,
        Missing,
        Failed,
    }

    private readonly record struct DeleteOutcome(DeleteStatus Status, Exception? Error);
    private readonly record struct SweepOutcome(SweepStatus Status, Exception? Error);

    private sealed class BurstScopeCache(long version)
    {
        public long Version { get; } = version;
        public string? Root { get; set; }
        public List<string> Folders { get; } = [];
        public IShokoSeries? Canonical { get; set; }
    }

    private sealed class GenerationPass(bool useBurstCache = true)
    {
        public bool UseBurstCache { get; } = useBurstCache;
        public Dictionary<ShowScope, string> ShowFolders { get; } = [];
        public Dictionary<ShowScope, List<string>> LinkedShowFolders { get; } = [];
        public Dictionary<ShowScope, BurstValue<bool>> SharedShowFolders { get; } = [];
        public Dictionary<string, BurstValue<bool>> FolderShared { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<ShowScope, long> ScopeVersions { get; } = [];
        public Dictionary<ShowScope, IShokoSeries> CanonicalSeries { get; } = [];
        public HashSet<ShowScope> CanonicalScopesDiscovered { get; } = [];
        public HashSet<ShowScope> WrittenShowRoots { get; } = [];
        public Dictionary<SweepKey, long> CompletedMisplacedSweeps { get; } = [];
    }

    private sealed record LibraryIndex(IReadOnlyList<IManagedFolder> ManagedFolders, Dictionary<ShowScope, List<string>> ShowFolders, Dictionary<string, FolderContents> FolderContents);

    private sealed class LibraryRunState(IReadOnlyList<IShokoSeries> series, LibraryIndex index, string titleLanguage, GenerationPass pass)
    {
        public IReadOnlyList<IShokoSeries> Series { get; } = series;
        public LibraryIndex Index { get; } = index;
        public string TitleLanguage { get; } = titleLanguage;
        public GenerationPass Pass { get; } = pass;
        public int Written { get; set; }
    }

    internal readonly record struct LibraryStepResult(int? NextSeriesIndex, int TotalSeries, string? NextSeriesTitle);

    private sealed class FolderContents
    {
        public int FileCount { get; set; }
        public Dictionary<int, int> ShowFileCounts { get; } = [];
    }

    private sealed class RelocationTiming(ILogger logger)
    {
        private readonly ILogger _logger = logger;
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        private long _last = System.Diagnostics.Stopwatch.GetTimestamp();
        private readonly Dictionary<string, long> _laps = [];
        private int _scopeHits;
        private int _scopeMisses;
        private int _misplacedPerformed;
        private int _misplacedSkipped;
        private int _misplacedFailed;

        public void ScopeCache(bool hit)
        {
            if (hit)
                _scopeHits++;
            else
                _scopeMisses++;
        }

        public void MisplacedSweep(int performed, int skipped, int failed)
        {
            _misplacedPerformed += performed;
            _misplacedSkipped += skipped;
            _misplacedFailed += failed;
        }

        public void Lap(string name)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _laps[name] = _laps.GetValueOrDefault(name) + now - _last;
            _last = now;
        }

        public void Finish()
        {
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(_started);
            if (total < TimeSpan.FromSeconds(1))
                return;
            double Ms(string name) => TimeSpan.FromSeconds(_laps.GetValueOrDefault(name) / (double)System.Diagnostics.Stopwatch.Frequency).TotalMilliseconds;
            _logger.LogInformation(
                "Relocated NFO generation timing: {TotalMs}ms; old-path cleanup {OldPathCleanupMs}ms; canonical resolution {CanonicalResolutionMs}ms; direct sharing {DirectSharingMs}ms; scope resolution {ScopeResolutionMs}ms (cache hits {ScopeCacheHits}, misses {ScopeCacheMisses}); episode I/O {EpisodeIoMs}ms; show sharing {ShowSharingMs}ms; root I/O {RootIoMs}ms; misplaced sweep {MisplacedSweepMs}ms (performed {MisplacedPerformed}, skipped {MisplacedSkipped}, failed {MisplacedFailed})",
                total.TotalMilliseconds, Ms("OldPathCleanup"), Ms("CanonicalResolution"), Ms("DirectSharing"), Ms("ScopeResolution"), _scopeHits, _scopeMisses, Ms("EpisodeIO"), Ms("ShowSharing"), Ms("RootIO"), Ms("MisplacedSweep"), _misplacedPerformed, _misplacedSkipped, _misplacedFailed);
        }
    }

    /// <summary>
    /// Removes legacy plugin-generated tvshow.nfo files below a resolved show
    /// root. A generated root tvshow.nfo for this TMDB show is required before
    /// descendants are considered; it only touches directories whose directly
    /// contained live videos all map to this TMDB show.
    /// </summary>
    private MisplacedSweepResult SweepMisplacedShowNfos(GenerationPass pass)
    {
        int performed = 0;
        int skipped = 0;
        int failed = 0;
        foreach (var (scope, showFolder) in pass.ShowFolders)
        {
            if (scope.TmdbShowId == 0)
            {
                skipped++;
                continue;
            }
            var key = new SweepKey(scope.TmdbShowId, DirectoryKey(showFolder));
            long currentVersion = CaptureBurstVersion();
            if ((pass.CompletedMisplacedSweeps.TryGetValue(key, out var completedVersion) && completedVersion == currentVersion)
                || (pass.UseBurstCache && TryGetBurstSweep(key)))
            {
                skipped++;
                continue;
            }
            long capturedVersion = CaptureBurstVersion();
            var result = SweepMisplacedShowNfos(showFolder, scope.TmdbShowId);
            if (result.Status == SweepStatus.Complete)
            {
                if (capturedVersion == CaptureBurstVersion())
                    pass.CompletedMisplacedSweeps[key] = capturedVersion;
                if (pass.UseBurstCache)
                    CacheBurstSweep(key, capturedVersion);
                performed++;
            }
            else if (result.Status == SweepStatus.Failed)
            {
                failed++;
                _logger.LogWarning(result.Error, "Misplaced NFO sweep failed for TMDB show {TmdbShowId} at {Root}; it will be retried", scope.TmdbShowId, showFolder);
            }
            else
            {
                skipped++;
            }
        }
        return new(performed, skipped, failed);
    }

    private enum SweepStatus
    {
        NotCandidate,
        Complete,
        Failed,
    }

    private SweepOutcome SweepMisplacedShowNfos(string showFolder, int tmdbShowId)
    {
        try
        {
            var rootStatus = ProbePluginShowNfo(Path.Combine(showFolder, "tvshow.nfo"), tmdbShowId);
            if (rootStatus.Status == OwnershipProbe.Failed)
                return new(SweepStatus.Failed, rootStatus.Error);
            if (rootStatus.Status != OwnershipProbe.Owned)
                return new(SweepStatus.NotCandidate, null);
            foreach (var nfoPath in Directory.EnumerateFiles(showFolder, "tvshow.nfo", SearchOption.AllDirectories))
            {
                var ownership = ProbeOwnership(nfoPath);
                if (ownership.Status == OwnershipProbe.Failed)
                    return new(SweepStatus.Failed, ownership.Error);
                if (ownership.Status != OwnershipProbe.Owned)
                    continue;
                var folder = Path.GetDirectoryName(nfoPath);
                if (folder is null || PathsEqual(folder, showFolder))
                    continue;
                var directFiles = _videoService.GetVideoFilesByAbsolutePath(folder)
                    .Where(f => string.Equals(Path.GetDirectoryName(f.Path), folder, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (directFiles.Count > 0 && directFiles.All(f => ResolveTmdbShowId(f) == tmdbShowId))
                {
                    var deletion = TryDelete(nfoPath);
                    if (deletion.Status == DeleteStatus.Failed)
                        return new(SweepStatus.Failed, deletion.Error);
                }
            }
            return new(SweepStatus.Complete, null);
        }
        catch (IOException ex)
        {
            return new(SweepStatus.Failed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(SweepStatus.Failed, ex);
        }
    }

    private static OwnershipProbeResult ProbePluginShowNfo(string nfoPath, int tmdbShowId)
    {
        try
        {
            var content = File.ReadAllText(nfoPath);
            if (!content.Contains(NfoWriter.OwnershipMarker, StringComparison.Ordinal))
                return new(OwnershipProbe.NotCandidate, null);
            var root = XDocument.Parse(content).Root;
            return root?.Name.LocalName == "tvshow"
                && root.Elements("uniqueid").Any(x => (string?)x.Attribute("type") == "tmdb"
                    && x.Value == tmdbShowId.ToString(CultureInfo.InvariantCulture))
                ? new(OwnershipProbe.Owned, null)
                : new(OwnershipProbe.NotCandidate, null);
        }
        catch (FileNotFoundException)
        {
            return new(OwnershipProbe.NotCandidate, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(OwnershipProbe.NotCandidate, null);
        }
        catch (IOException ex)
        {
            return new(OwnershipProbe.Failed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(OwnershipProbe.Failed, ex);
        }
        catch (System.Xml.XmlException ex)
        {
            return new(OwnershipProbe.Failed, ex);
        }
    }

    private static OwnershipProbeResult ProbeOwnership(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains(NfoWriter.OwnershipMarker, StringComparison.Ordinal)
                ? new(OwnershipProbe.Owned, null)
                : new(OwnershipProbe.NotCandidate, null);
        }
        catch (FileNotFoundException)
        {
            return new(OwnershipProbe.NotCandidate, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(OwnershipProbe.NotCandidate, null);
        }
        catch (IOException ex)
        {
            return new(OwnershipProbe.Failed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(OwnershipProbe.Failed, ex);
        }
    }

    private static DeleteOutcome TryDelete(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return new(DeleteStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(DeleteStatus.Missing, null);
        }
        catch (IOException ex)
        {
            return new(DeleteStatus.Failed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(DeleteStatus.Failed, ex);
        }

        try
        {
            File.Delete(path);
            return new(DeleteStatus.Deleted, null);
        }
        catch (FileNotFoundException)
        {
            return new(DeleteStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(DeleteStatus.Missing, null);
        }
        catch (IOException ex)
        {
            return new(DeleteStatus.Failed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(DeleteStatus.Failed, ex);
        }
    }

    private static int? RuntimeMinutes(TimeSpan runtime)
        => runtime.TotalMinutes > 0 ? (int)Math.Round(runtime.TotalMinutes) : null;

    private static double? PositiveRating(double rating)
        => rating > 0 ? rating : null;

    private static int? PositiveVotes(int votes)
        => votes > 0 ? votes : null;

    private readonly record struct DirectoryCleanupResult(int Files = 0, int Directories = 0)
    {
        public static DirectoryCleanupResult operator +(DirectoryCleanupResult left, DirectoryCleanupResult right)
            => new(left.Files + right.Files, left.Directories + right.Directories);
    }
}
