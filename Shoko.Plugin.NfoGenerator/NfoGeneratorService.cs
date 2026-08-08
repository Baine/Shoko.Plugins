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
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _releaseService.ReleaseSaved -= OnReleaseSaved;
        _releaseService.ReleaseDeleted -= OnReleaseDeleted;
        _metadataService.SeriesUpdated -= OnSeriesUpdated;
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

    /// <summary>Generates NFO files for every available video file of a series.</summary>
    public int GenerateForSeries(IShokoSeries series, bool force = false)
        => GenerateForVideos(series.Episodes.OfType<IShokoEpisode>().SelectMany(e => e.VideoList), force);

    /// <summary>Generates NFO files for every available video file of an episode.</summary>
    public int GenerateForEpisode(IShokoEpisode episode, bool force = false)
        => GenerateForVideos(episode.VideoList, force);

    /// <summary>Generates NFO files for every available video file inside an import folder.</summary>
    public int GenerateForFolder(IManagedFolder folder, bool force = false)
        => GenerateForFiles(_videoService.GetVideoFilesInManagedFolder(folder), force);

    /// <summary>Generates NFO files for the entire library.</summary>
    public int GenerateForLibrary(bool force = false)
    {
        int count = 0;
        foreach (var series in _metadataService.GetAllShokoSeries())
            count += GenerateForSeries(series, force);
        return count;
    }

    private int GenerateForVideos(IEnumerable<IVideo> videos, bool force)
        => GenerateForFiles(videos.SelectMany(v => v.Files), force);

    private int GenerateForFiles(IEnumerable<IVideoFile> files, bool force)
    {
        int written = 0;
        foreach (var file in files.Where(f => f.IsAvailable && f.Video is not null).DistinctBy(f => f.ID))
        {
            if (WriteForFile(file, force))
                written++;
        }
        return written;
    }

    private bool WriteForFile(IVideoFile file, bool force)
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

        if (series.Type == AnimeType.Movie)
            return NfoWriter.WriteMovie(Path.Combine(folder, "movie.nfo"), BuildShowNfo(series, series.Episodes.FirstOrDefault(), SidecarWriter.WriteFolderArt(folder, series), cfg), force);

        if (episode is null)
            return false;

        var thumb = SidecarWriter.WriteThumb(folder, episode);
        bool episodeWritten = NfoWriter.WriteEpisode(Path.ChangeExtension(file.Path, ".nfo"), BuildEpisodeNfo(episode, series, thumb, cfg), force);

        // The folder holding the episodes is treated as the show folder.
        bool showWritten = NfoWriter.WriteTvShow(Path.Combine(folder, "tvshow.nfo"), BuildShowNfo(series, episode, SidecarWriter.WriteFolderArt(folder, series), cfg), force);
        return episodeWritten || showWritten;
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
    private static void DeleteNfosForRelease(IVideo video)
    {
        foreach (var file in video.Files.Where(f => f.IsAvailable))
        {
            var nfoPath = Path.ChangeExtension(file.Path, ".nfo");
            try
            {
                if (File.Exists(nfoPath))
                    File.Delete(nfoPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static int? RuntimeMinutes(TimeSpan runtime)
        => runtime.TotalMinutes > 0 ? (int)Math.Round(runtime.TotalMinutes) : null;

    private static double? PositiveRating(double rating)
        => rating > 0 ? rating : null;

    private static int? PositiveVotes(int votes)
        => votes > 0 ? votes : null;
}
