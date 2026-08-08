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
using Shoko.Plugin.NfoGenerator.Nfo;

namespace Shoko.Plugin.NfoGenerator;

/// <summary>
/// Writes Kodi-style NFO files and artwork sidecars next to video files. Runs
/// automatically on matched releases and on demand per series/episode/import
/// folder/library via <see cref="NfoGeneratorController"/>.
/// </summary>
public sealed class NfoGeneratorService : IHostedService
{
    public readonly record struct LibraryCheckResult(int Written, int Removed);

    private readonly IVideoReleaseService _releaseService;
    private readonly ConfigurationProvider<NfoGeneratorSettings> _settings;
    private readonly IMetadataService _metadataService;
    private readonly IVideoService _videoService;
    private readonly ILogger<NfoGeneratorService> _logger;
    // ponytail: one global gate; use keyed queues only if serialized generation
    // becomes a measurable throughput problem after imports have settled.
    private readonly SemaphoreSlim _generationGate = new(1, 1);

    public NfoGeneratorService(
        IVideoReleaseService releaseService,
        ConfigurationProvider<NfoGeneratorSettings> settings,
        IMetadataService metadataService,
        IVideoService videoService,
        ILogger<NfoGeneratorService> logger)
    {
        _releaseService = releaseService;
        _settings = settings;
        _metadataService = metadataService;
        _videoService = videoService;
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
        return Task.CompletedTask;
    }

    private void OnReleaseSaved(object? sender, VideoReleaseSavedEventArgs e)
    {
        if (!_settings.Load().GenerateOnImport)
            return;
        RunFromEvent(() => GenerateForVideos([e.Video], force: false), "generate NFO files for video {VideoID}", e.Video.ID);
    }

    private void OnSeriesUpdated(object? sender, SeriesInfoUpdatedEventArgs e)
    {
        if (!_settings.Load().GenerateOnMetadataUpdate)
            return;
        if (e.SeriesInfo is not IShokoSeries series)
            return;
        // Metadata updates rewrite even unchanged files so the media library
        // sees a fresh mtime after a metadata change.
        RunFromEvent(() => GenerateForSeriesCore(series, force: true), "generate NFO files for series {SeriesID}", series.ID);
    }

    private void OnReleaseDeleted(object? sender, VideoReleaseDeletedEventArgs e)
    {
        if (e.Video is not { } video)
            return;
        RunFromEvent(() => DeleteNfosForRelease(video), "remove NFO files for video {VideoID}", video.ID);
    }

    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs e)
    {
        if (!_settings.Load().GenerateOnImport)
            return;
        RunFromEvent(() =>
        {
            DeleteNfo(e.PreviousPath);
            SweepFolder(Path.GetDirectoryName(e.PreviousPath));
            GenerateForFiles([e.File], force: false);
        }, "generate NFO files for relocated file {FilePath}", e.File.Path);
    }

    /// <summary>Generates NFO files for every available video file of a series.</summary>
    public int GenerateForSeries(IShokoSeries series, bool force = false)
        => RunExclusive(() => GenerateForSeriesCore(series, force));

    private int GenerateForSeriesCore(IShokoSeries series, bool force, Dictionary<int, string>? showFolders = null, Dictionary<int, bool>? sharedShowFolders = null, bool sweep = true, LibraryIndex? libraryIndex = null)
        => GenerateForVideos(series.Episodes.OfType<IShokoEpisode>().SelectMany(e => e.VideoList), force, showFolders, sharedShowFolders, sweep, libraryIndex);

    /// <summary>Generates NFO files for every available video file of an episode.</summary>
    public int GenerateForEpisode(IShokoEpisode episode, bool force = false)
        => RunExclusive(() => GenerateForVideos(episode.VideoList, force));

    /// <summary>Generates NFO files for every available video file inside an import folder.</summary>
    public int GenerateForFolder(IManagedFolder folder, bool force = false)
        => RunExclusive(() => GenerateForFiles(_videoService.GetVideoFilesInManagedFolder(folder), force));

    /// <summary>Generates NFO files for the entire library, then sweeps orphan NFO/art files.</summary>
    public LibraryCheckResult GenerateForLibrary(bool force = false)
        => RunExclusive(() => GenerateForLibraryCore(force));

    private LibraryCheckResult GenerateForLibraryCore(bool force)
    {
        var seriesList = _metadataService.GetAllShokoSeries().ToList();
        var libraryIndex = BuildLibraryIndex(seriesList);
        var titleLanguage = _settings.Load().TitleLanguage;
        var showFolders = new Dictionary<int, string>();
        var sharedShowFolders = new Dictionary<int, bool>();
        _logger.LogInformation("Generating NFO files for the entire library: {Count} series", seriesList.Count);
        int count = 0;
        for (int i = 0; i < seriesList.Count; i++)
        {
            var series = seriesList[i];
            _logger.LogInformation("Processing series {Index}/{Total}: {Title} ({SeriesID})", i + 1, seriesList.Count, LanguageResolver.Title(series, titleLanguage), series.ID);
            count += GenerateForSeriesCore(series, force, showFolders, sharedShowFolders, sweep: false, libraryIndex: libraryIndex);
        }
        SweepMisplacedShowNfos(showFolders);
        _logger.LogInformation("Library generation finished: {Written} NFO file(s) written", count);
        int removed = SweepLibrary();
        return new LibraryCheckResult(count, removed);
    }

    private T RunExclusive<T>(Func<T> action)
    {
        _generationGate.Wait();
        try
        {
            return action();
        }
        finally
        {
            _generationGate.Release();
        }
    }

    private void RunFromEvent(Action action, string failureMessage, params object[] args)
    {
        _ = Task.Run(() =>
        {
            if (!_generationGate.Wait(0))
            {
                _logger.LogDebug("Skipping NFO event because a generation is already in progress");
                return;
            }
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, failureMessage, args);
            }
            finally
            {
                _generationGate.Release();
            }
        });
    }

    private int GenerateForVideos(IEnumerable<IVideo> videos, bool force, Dictionary<int, string>? showFolders = null, Dictionary<int, bool>? sharedShowFolders = null, bool sweep = true, LibraryIndex? libraryIndex = null)
        => GenerateForFiles(videos.SelectMany(v => v.Files), force, showFolders, sharedShowFolders, sweep, libraryIndex);

    private int GenerateForFiles(IEnumerable<IVideoFile> files, bool force, Dictionary<int, string>? showFolders = null, Dictionary<int, bool>? sharedShowFolders = null, bool sweep = true, LibraryIndex? libraryIndex = null)
    {
        var targets = files.Where(f => f.IsAvailable && f.Video is not null).DistinctBy(f => f.ID).ToList();
        _logger.LogInformation("Generating NFO files for {Count} file(s)", targets.Count);
        var sharedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        showFolders ??= [];
        sharedShowFolders ??= [];
        int written = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var file = targets[i];
            _logger.LogInformation("Processing {Index}/{Total} ({Percent}%): {FilePath}", i + 1, targets.Count, (i + 1) * 100 / targets.Count, file.Path);
            var folder = Path.GetDirectoryName(file.Path);
            if (folder is null)
                continue;
            if (!sharedFolders.Contains(folder) && IsFolderShared(folder))
                sharedFolders.Add(folder);
            if (WriteForFile(file, force, allowFolderArt: !sharedFolders.Contains(folder), showFolders, sharedShowFolders, libraryIndex))
                written++;
        }
        if (sweep)
            SweepMisplacedShowNfos(showFolders);
        _logger.LogInformation("Generation finished: {Written}/{Total} NFO file(s) written", written, targets.Count);
        return written;
    }

    private bool WriteForFile(IVideoFile file, bool force, bool allowFolderArt, Dictionary<int, string> showFolders, Dictionary<int, bool> sharedShowFolders, LibraryIndex? libraryIndex)
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
            return NfoWriter.WriteMovie(Path.Combine(folder, "movie.nfo"), BuildShowNfo(series, episode, SidecarWriter.WriteFolderArt(folder, series), cfg, ResolveTmdbMovieId(series, episode)), force);
        }

        if (episode is null)
            return false;

        var showId = ResolveTmdbShowId(series, episode);
        var showFolder = showId is null
            ? folder
            : showFolders.GetValueOrDefault(showId.Value) ?? (showFolders[showId.Value] = ResolveShowFolder(folder, showId.Value, libraryIndex));
        var thumb = SidecarWriter.WriteThumb(folder, episode);
        bool episodeWritten = NfoWriter.WriteEpisode(Path.ChangeExtension(file.Path, ".nfo"), BuildEpisodeNfo(episode, series, thumb, cfg), force);

        bool showFolderShared = false;
        if (showId is not null && !sharedShowFolders.TryGetValue(showId.Value, out showFolderShared))
        {
            showFolderShared = IsShowFolderShared(showFolder, showId.Value, libraryIndex);
            sharedShowFolders[showId.Value] = showFolderShared;
        }
        if (!allowFolderArt || showFolderShared)
            return episodeWritten;
        bool showWritten = NfoWriter.WriteTvShow(Path.Combine(showFolder, "tvshow.nfo"), BuildShowNfo(series, episode, SidecarWriter.WriteFolderArt(showFolder, series), cfg, showId), force);
        return episodeWritten || showWritten;
    }

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

    private static int? ResolveTmdbShowId(IShokoSeries series, IShokoEpisode episode)
        => SelectTmdbEpisodeCrossReference(episode)?.TmdbShowID
            ?? series.TmdbShowCrossReferences
                .OrderBy(x => x.MatchRating == MatchRating.UserVerified ? 0 : 1)
                .ThenBy(x => x.TmdbShowID)
                .Select(x => (int?)x.TmdbShowID)
                .FirstOrDefault();

    private static int? ResolveTmdbMovieId(IShokoSeries series, IShokoEpisode? episode)
        => (episode is null ? null : SelectTmdbMovieCrossReference(episode.TmdbMovieCrossReferences)?.TmdbMovieID)
            ?? SelectTmdbMovieCrossReference(series.TmdbMovieCrossReferences)?.TmdbMovieID;

    /// <summary>
    /// Resolves a conventional show root without moving media. Multiple local
    /// Shoko series may represent seasons of the same TMDB show, so their file
    /// directories are considered together, but only inside the same managed
    /// folder as the current file.
    /// </summary>
    private string ResolveShowFolder(string fileFolder, int tmdbShowId, LibraryIndex? libraryIndex = null)
    {
        var managedFolder = (libraryIndex?.ManagedFolders ?? _videoService.GetAllManagedFolders())
            .Where(f => IsPathWithin(fileFolder, f.Path))
            .OrderByDescending(f => f.Path.Length)
            .FirstOrDefault();
        if (managedFolder is null)
            return fileFolder;

        var folders = (libraryIndex?.ShowFolders.GetValueOrDefault(tmdbShowId) ?? _metadataService.GetAllShokoSeries()
            .Where(s => s.TmdbShowCrossReferences.Any(x => x.TmdbShowID == tmdbShowId))
            .SelectMany(s => s.Episodes.OfType<IShokoEpisode>())
            .SelectMany(e => e.VideoList)
            .SelectMany(v => v.Files)
            .Where(f => f.IsAvailable && Path.GetDirectoryName(f.Path) is not null)
            .Select(f => Path.GetDirectoryName(f.Path)!))
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

    // Folder-level artifacts are cleaned when a delete or relocation leaves a
    // folder empty. Legacy plugin tvshow.nfo files inside season directories
    // are additionally swept after a TMDB show-root NFO is generated.
    private void DeleteNfosForRelease(IVideo video)
    {
        foreach (var file in video.Files.Where(f => f.IsAvailable))
        {
            DeleteNfo(file.Path);
            SweepFolder(Path.GetDirectoryName(file.Path));
        }
    }

    private static void DeleteNfo(string videoPath)
        => TryDelete(Path.ChangeExtension(videoPath, ".nfo"));

    // Art sidecars carry the source extension, so sweep by wildcard.
    private static readonly string[] FolderArtifacts = ["tvshow.nfo", "movie.nfo", "poster.*", "fanart.*", "banner.*", "logo.*", "disc.*", "thumb.*"];

    /// <summary>Removes folder-level NFOs and art once no live video file remains directly in the folder.</summary>
    private void SweepFolder(string? folder)
    {
        if (folder is null || FolderHasVideoFiles(folder))
            return;
        try
        {
            DeleteFolderArtifacts(folder);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Full-library orphan sweep: walks every managed folder, removes per-file
    /// episode NFOs whose video file is gone and folder-level artifacts in
    /// folders with no live video files left.
    /// </summary>
    private int SweepLibrary()
    {
        int removed = 0;
        var filesByDir = _videoService.GetAllVideoFiles()
            .Where(f => f.IsAvailable)
            .GroupBy(f => Path.GetDirectoryName(f.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var managedFolder in _videoService.GetAllManagedFolders())
        {
            try
            {
                foreach (var dir in EnumerateFolders(managedFolder.Path))
                {
                    filesByDir.TryGetValue(dir, out var live);
                    removed += SweepDirectory(dir, live);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        _logger.LogInformation("Library sweep finished: {Removed} orphan NFO/art file(s) removed", removed);
        return removed;
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
        foreach (var nfoPath in Directory.GetFiles(dir, "*.nfo"))
        {
            if (Path.GetFileName(nfoPath) is "tvshow.nfo" or "movie.nfo")
                continue;
            if (!liveNfoPaths.Contains(nfoPath) && IsPluginNfo(nfoPath))
            {
                TryDelete(nfoPath);
                removed++;
            }
        }
        if (live.Count == 0)
            removed += DeleteFolderArtifacts(dir);
        return removed;
    }

    /// <summary>Only plugin-written NFOs embed a Shoko uniqueid; leave user-authored NFOs alone.</summary>
    private static bool IsPluginNfo(string nfoPath)
    {
        try
        {
            return File.ReadAllText(nfoPath).Contains("<uniqueid type=\"shoko\"", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int DeleteFolderArtifacts(string dir)
    {
        int removed = 0;
        foreach (var pattern in FolderArtifacts)
            foreach (var path in Directory.GetFiles(dir, pattern))
            {
                TryDelete(path);
                removed++;
            }
        return removed;
    }

    private bool FolderHasVideoFiles(string folder)
        => _videoService.GetVideoFilesByAbsolutePath(folder)
            .Any(f => string.Equals(Path.GetDirectoryName(f.Path), folder, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// A show root may contain only season folders, so the older direct-child
    /// guard is insufficient there. Do not write root-level metadata when any
    /// live file below it belongs to another (or unmapped) TMDB show.
    /// </summary>
    private bool IsShowFolderShared(string folder, int tmdbShowId, LibraryIndex? libraryIndex = null)
    {
        if (libraryIndex?.FolderContents.TryGetValue(DirectoryKey(folder), out var contents) == true)
            return contents.FileCount != contents.ShowFileCounts.GetValueOrDefault(tmdbShowId);
        return _videoService.GetAllVideoFiles()
            .Where(f => f.IsAvailable && IsPathWithin(f.Path, folder))
            .Any(f => f.Video?.Episodes.FirstOrDefault()?.Series is not { } series
                || !series.TmdbShowCrossReferences.Any(x => x.TmdbShowID == tmdbShowId));
    }

    private LibraryIndex BuildLibraryIndex(IReadOnlyList<IShokoSeries> seriesList)
    {
        var managedFolders = _videoService.GetAllManagedFolders().ToList();
        var showFolders = new Dictionary<int, List<string>>();
        foreach (var series in seriesList)
        {
            var folders = series.Episodes.OfType<IShokoEpisode>()
                .SelectMany(e => e.VideoList)
                .SelectMany(v => v.Files)
                .Where(f => f.IsAvailable && Path.GetDirectoryName(f.Path) is not null)
                .Select(f => Path.GetDirectoryName(f.Path)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var showId in series.TmdbShowCrossReferences.Select(x => x.TmdbShowID).Distinct())
            {
                if (!showFolders.TryGetValue(showId, out var destinations))
                    showFolders[showId] = destinations = [];
                destinations.AddRange(folders);
            }
        }

        var folderContents = new Dictionary<string, FolderContents>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _videoService.GetAllVideoFiles().Where(f => f.IsAvailable))
        {
            var directory = Path.GetDirectoryName(file.Path);
            if (directory is null)
                continue;
            var managedFolder = managedFolders.Where(f => IsPathWithin(directory, f.Path)).OrderByDescending(f => f.Path.Length).FirstOrDefault();
            if (managedFolder is null)
                continue;
            var showIds = file.Video?.Episodes.FirstOrDefault()?.Series?.TmdbShowCrossReferences.Select(x => x.TmdbShowID).Distinct() ?? [];
            for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            {
                var key = DirectoryKey(current);
                if (!folderContents.TryGetValue(key, out var contents))
                    folderContents[key] = contents = new FolderContents();
                contents.FileCount++;
                foreach (var showId in showIds)
                    contents.ShowFileCounts[showId] = contents.ShowFileCounts.GetValueOrDefault(showId) + 1;
                if (PathsEqual(current, managedFolder.Path))
                    break;
            }
        }
        _logger.LogInformation("Indexed {ShowCount} TMDB show(s) and {FolderCount} media folder(s) for library generation", showFolders.Count, folderContents.Count);
        return new LibraryIndex(managedFolders, showFolders, folderContents);
    }

    private static string DirectoryKey(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record LibraryIndex(IReadOnlyList<IManagedFolder> ManagedFolders, Dictionary<int, List<string>> ShowFolders, Dictionary<string, FolderContents> FolderContents);

    private sealed class FolderContents
    {
        public int FileCount { get; set; }
        public Dictionary<int, int> ShowFileCounts { get; } = [];
    }

    /// <summary>
    /// Removes legacy plugin-generated tvshow.nfo files below a resolved show
    /// root. It only touches directories whose directly contained live videos
    /// all map to this TMDB show; user-authored and unrelated NFOs are left
    /// intact.
    /// </summary>
    private void SweepMisplacedShowNfos(IReadOnlyDictionary<int, string> showFolders)
    {
        foreach (var (showId, showFolder) in showFolders)
            SweepMisplacedShowNfos(showFolder, showId);
    }

    private void SweepMisplacedShowNfos(string showFolder, int tmdbShowId)
    {
        try
        {
            foreach (var nfoPath in Directory.EnumerateFiles(showFolder, "tvshow.nfo", SearchOption.AllDirectories))
            {
                if (!IsPluginNfo(nfoPath))
                    continue;
                var folder = Path.GetDirectoryName(nfoPath);
                if (folder is null)
                    continue;
                var directFiles = _videoService.GetVideoFilesByAbsolutePath(folder)
                    .Where(f => string.Equals(Path.GetDirectoryName(f.Path), folder, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (directFiles.Count > 0 && directFiles.All(f => f.Video?.Episodes.FirstOrDefault()?.Series?.TmdbShowCrossReferences.Any(x => x.TmdbShowID == tmdbShowId) == true))
                    TryDelete(nfoPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int? RuntimeMinutes(TimeSpan runtime)
        => runtime.TotalMinutes > 0 ? (int)Math.Round(runtime.TotalMinutes) : null;

    private static double? PositiveRating(double rating)
        => rating > 0 ? rating : null;

    private static int? PositiveVotes(int votes)
        => votes > 0 ? votes : null;
}
