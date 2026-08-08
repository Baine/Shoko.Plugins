using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        // Fire-and-forget: file I/O must not block the event dispatcher.
        _ = Task.Run(() =>
        {
            try
            {
                GenerateForVideos([e.Video], force: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate NFO files for video {VideoID}", e.Video.ID);
            }
        });
    }

    private void OnSeriesUpdated(object? sender, SeriesInfoUpdatedEventArgs e)
    {
        if (!_settings.Load().GenerateOnMetadataUpdate)
            return;
        if (e.SeriesInfo is not IShokoSeries series)
            return;
        // Fire-and-forget: file I/O must not block the event dispatcher.
        _ = Task.Run(() =>
        {
            try
            {
                // ponytail: metadata updates rewrite even unchanged files so the
                // media library sees a fresh mtime after a metadata change.
                GenerateForSeries(series, force: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate NFO files for series {SeriesID}", series.ID);
            }
        });
    }

    private void OnReleaseDeleted(object? sender, VideoReleaseDeletedEventArgs e)
    {
        if (e.Video is not { } video)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                DeleteNfosForRelease(video);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove NFO files for video {VideoID}", video.ID);
            }
        });
    }

    private void OnVideoFileRelocated(object? sender, VideoFileRelocatedEventArgs e)
    {
        if (!_settings.Load().GenerateOnImport)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                DeleteNfo(e.PreviousPath);
                SweepFolder(Path.GetDirectoryName(e.PreviousPath));
                GenerateForFiles([e.File], force: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate NFO files for relocated file {FilePath}", e.File.Path);
            }
        });
    }

    /// <summary>Generates NFO files for every available video file of a series.</summary>
    public int GenerateForSeries(IShokoSeries series, bool force = false)
        => GenerateForVideos(series.Episodes.OfType<IShokoEpisode>().SelectMany(e => e.VideoList), force);

    /// <summary>Generates NFO files for every available video file of an episode.</summary>
    public int GenerateForEpisode(IShokoEpisode episode, bool force = false)
        => GenerateForVideos(episode.VideoList, force);

    /// <summary>Generates NFO files for every available video file inside an import folder.</summary>
    public int GenerateForFolder(IManagedFolder folder, bool force = false)
        => GenerateForFiles(_videoService.GetVideoFilesInManagedFolder(folder), force);

    /// <summary>Generates NFO files for the entire library, then sweeps orphan NFO/art files.</summary>
    public LibraryCheckResult GenerateForLibrary(bool force = false)
    {
        var seriesList = _metadataService.GetAllShokoSeries().ToList();
        var titleLanguage = _settings.Load().TitleLanguage;
        _logger.LogInformation("Generating NFO files for the entire library: {Count} series", seriesList.Count);
        int count = 0;
        for (int i = 0; i < seriesList.Count; i++)
        {
            var series = seriesList[i];
            _logger.LogInformation("Processing series {Index}/{Total}: {Title} ({SeriesID})", i + 1, seriesList.Count, LanguageResolver.Title(series, titleLanguage), series.ID);
            count += GenerateForSeries(series, force);
        }
        _logger.LogInformation("Library generation finished: {Written} NFO file(s) written", count);
        int removed = SweepLibrary();
        return new LibraryCheckResult(count, removed);
    }

    private int GenerateForVideos(IEnumerable<IVideo> videos, bool force)
        => GenerateForFiles(videos.SelectMany(v => v.Files), force);

    private int GenerateForFiles(IEnumerable<IVideoFile> files, bool force)
    {
        var targets = files.Where(f => f.IsAvailable && f.Video is not null).DistinctBy(f => f.ID).ToList();
        _logger.LogInformation("Generating NFO files for {Count} file(s)", targets.Count);
        var sharedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (WriteForFile(file, force, allowFolderArt: !sharedFolders.Contains(folder)))
                written++;
        }
        _logger.LogInformation("Generation finished: {Written}/{Total} NFO file(s) written", written, targets.Count);
        return written;
    }

    private bool WriteForFile(IVideoFile file, bool force, bool allowFolderArt)
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
            return NfoWriter.WriteMovie(Path.Combine(folder, "movie.nfo"), BuildShowNfo(series, series.Episodes.FirstOrDefault(), SidecarWriter.WriteFolderArt(folder, series), cfg), force);
        }

        if (episode is null)
            return false;

        var thumb = SidecarWriter.WriteThumb(folder, episode);
        bool episodeWritten = NfoWriter.WriteEpisode(Path.ChangeExtension(file.Path, ".nfo"), BuildEpisodeNfo(episode, series, thumb, cfg), force);

        if (!allowFolderArt)
            return episodeWritten;
        bool showWritten = NfoWriter.WriteTvShow(Path.Combine(folder, "tvshow.nfo"), BuildShowNfo(series, episode, SidecarWriter.WriteFolderArt(folder, series), cfg), force);
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

    private static EpisodeNfo BuildEpisodeNfo(IShokoEpisode episode, IShokoSeries series, string? thumb, NfoGeneratorSettings cfg)
        => new()
        {
            Title = LanguageResolver.Title(episode, cfg.TitleLanguage),
            ShowTitle = LanguageResolver.Title(series, cfg.TitleLanguage),
            Plot = LanguageResolver.Description(episode, cfg.DescriptionLanguage) ?? LanguageResolver.Description(series, cfg.DescriptionLanguage),
            Aired = episode.AirDate?.ToString("yyyy-MM-dd"),
            Season = episode.SeasonNumber,
            Episode = episode.EpisodeNumber,
            RuntimeMinutes = RuntimeMinutes(episode.Runtime),
            Rating = PositiveRating(episode.Rating),
            Votes = PositiveVotes(episode.RatingVotes),
            AnidbId = episode.AnidbEpisodeID.ToString(),
            ShokoId = episode.ID.ToString(),
            Thumb = thumb,
        };

    private static ShowNfo BuildShowNfo(IShokoSeries series, IShokoEpisode? episode, IReadOnlyDictionary<string, string> art, NfoGeneratorSettings cfg)
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
            Studios = series.Studios.Select(s => s.Name).ToList(),
            Art = art,
        };
    }

    // ponytail: tvshow/movie folder art is written in the same folder as the
    // video; on delete we only remove the per-file episode NFO. Stale tvshow.nfo
    // / movie.nfo / thumb.jpg may remain when the last file of a folder is
    // removed. Revisit if folder-level cleanup is wanted.
    // ponytail: no scheduled sweep; folder-level artifacts are only cleaned
    // when a delete or relocation leaves the folder with no live video files.
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
