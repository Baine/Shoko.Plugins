using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Video.Services;
using Shoko.QueueProcessor.Abstractions;
using Shoko.QueueProcessor.Builder;
using Shoko.QueueProcessor.Concurrency;

namespace Shoko.Plugin.NfoGenerator.Jobs;

[LongRunning]
[DisallowConcurrencyGroup("NfoGenerator")]
public sealed class NfoGenerationJob(NfoGeneratorService service, IMetadataService metadataService, IVideoService videoService, IQueueScheduler queueScheduler, ISystemService systemService) : IQueueJob
{
    [JobKeyMember]
    public NfoGenerationKind Kind { get; set; }

    [JobKeyMember]
    public int ID { get; set; }

    [JobKeyMember]
    public string? PreviousPath { get; set; }

    [JobKeyMember]
    public bool Force { get; set; }

    public int Total { get; set; }
    public string? SeriesTitle { get; set; }

    public string TypeName => "Generate NFO files";
    public string Title => Kind switch
    {
        NfoGenerationKind.Library => $"Library: {SeriesTitle ?? "preparing"} ({ID + 1}/{Total}, {Progress}%)",
        NfoGenerationKind.Relocated => $"Relocated file {ID}",
        NfoGenerationKind.Delete => "Remove stale sidecars",
        _ => $"{Kind} {ID}",
    };
    public Dictionary<string, object> Details
    {
        get
        {
            var details = new Dictionary<string, object>
            {
                ["Action"] = Kind.ToString(),
                ["Target"] = Kind == NfoGenerationKind.Library ? SeriesTitle ?? "Preparing library index" : ID.ToString(),
            };
            if (Kind == NfoGenerationKind.Library)
                details["Progress"] = $"{ID + 1}/{Total} ({Progress}%)";
            if (PreviousPath is not null)
                details["Previous Path"] = PreviousPath;
            return details;
        }
    }

    private int Progress => Total > 0 ? (ID + 1) * 100 / Total : 0;

    public async Task Process()
    {
        await systemService.WaitForStartupAsync();

        switch (Kind)
        {
            case NfoGenerationKind.Video when videoService.GetVideoByID(ID) is { } video:
                service.GenerateForVideo(video, Force);
                break;
            case NfoGenerationKind.Series when metadataService.GetShokoSeriesByID(ID) is { } series:
                service.GenerateForSeries(series, Force);
                break;
            case NfoGenerationKind.Episode when metadataService.GetShokoEpisodeByID(ID) is { } episode:
                service.GenerateForEpisode(episode, Force);
                break;
            case NfoGenerationKind.Folder when videoService.GetManagedFolderByID(ID) is { } folder:
                service.GenerateForFolder(folder, Force);
                break;
            case NfoGenerationKind.Library:
                var step = service.GenerateLibraryStep(ID, Force);
                if (step.NextSeriesIndex is { } next)
                    await queueScheduler.RunAfterCurrent<NfoGenerationJob>(job =>
                    {
                        job.Kind = NfoGenerationKind.Library;
                        job.ID = next;
                        job.Force = Force;
                        job.Total = step.TotalSeries;
                        job.SeriesTitle = step.NextSeriesTitle;
                    });
                break;
            case NfoGenerationKind.Delete when PreviousPath is not null:
                service.DeleteForPath(PreviousPath);
                break;
            case NfoGenerationKind.Relocated when PreviousPath is not null && videoService.GetVideoFileByID(ID) is { } file:
                service.GenerateForRelocatedFile(file, PreviousPath);
                break;
        }
    }

    internal static void SelfCheck()
    {
        var library = JobKeyBuilder<NfoGenerationJob>.Create().UsingJobData(job => job.Kind = NfoGenerationKind.Library).Build();
        var duplicateLibrary = JobKeyBuilder<NfoGenerationJob>.Create().UsingJobData(job => job.Kind = NfoGenerationKind.Library).Build();
        var video = JobKeyBuilder<NfoGenerationJob>.Create().UsingJobData(job => job.ID = 1).Build();
        var series = JobKeyBuilder<NfoGenerationJob>.Create().UsingJobData(job => { job.Kind = NfoGenerationKind.Series; job.ID = 1; }).Build();
        if (library != duplicateLibrary || library == video || video == series)
            throw new InvalidOperationException("NFO queue job keys must deduplicate only equivalent work.");
    }
}

public enum NfoGenerationKind
{
    Video,
    Series,
    Episode,
    Folder,
    Library,
    Delete,
    Relocated,
}
