using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Video.Services;
using Shoko.QueueProcessor.Abstractions;
using Shoko.QueueProcessor.Builder;
using Shoko.QueueProcessor.Concurrency;

namespace Shoko.Plugin.NfoGenerator.Jobs;

[DisallowConcurrencyGroup("NfoGenerator")]
public sealed class NfoGenerationJob(NfoGeneratorService service, IMetadataService metadataService, IVideoService videoService) : IQueueJob
{
    public NfoGenerationKind Kind { get; set; }
    public int ID { get; set; }
    public string? PreviousPath { get; set; }
    public bool Force { get; set; }

    public string TypeName => "Generate NFO files";
    public string Title => Kind.ToString();

    public Task Process()
    {
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
                service.GenerateForLibrary(Force);
                break;
            case NfoGenerationKind.Delete when PreviousPath is not null:
                service.DeleteForPath(PreviousPath);
                break;
            case NfoGenerationKind.Relocated when PreviousPath is not null && videoService.GetVideoFileByID(ID) is { } file:
                service.GenerateForRelocatedFile(file, PreviousPath);
                break;
        }
        return Task.CompletedTask;
    }

    internal static void SelfCheck()
    {
        var library = JobKeyBuilder<NfoGenerationJob>.Create().UsingJobData(job => job.Kind = NfoGenerationKind.Library).Build();
        var duplicateLibrary = JobKeyBuilder<NfoGenerationJob>.Create().UsingJobData(job => job.Kind = NfoGenerationKind.Library).Build();
        var video = JobKeyBuilder<NfoGenerationJob>.Create().UsingJobData(job => job.ID = 1).Build();
        if (library != duplicateLibrary || library == video)
            throw new InvalidOperationException("NFO queue job keys must deduplicate libraries without merging target jobs.");
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
