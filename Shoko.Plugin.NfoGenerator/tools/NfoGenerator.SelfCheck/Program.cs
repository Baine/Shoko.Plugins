using System.Reflection;
using System.Xml.Linq;
using Shoko.Abstractions.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Tmdb;
using Shoko.Abstractions.Metadata.Tmdb.CrossReferences;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;
using Shoko.Plugin.NfoGenerator;
using Shoko.Plugin.NfoGenerator.Config;
using Shoko.Plugin.NfoGenerator.Jobs;
using Shoko.Plugin.NfoGenerator.Nfo;

var outputDir = Path.Combine(Path.GetTempPath(), "nfo-generator-selfcheck");
NfoWriter.SelfCheck(outputDir);
LanguageResolverCheck.SelfCheck();
NfoGenerationJob.SelfCheck();
NfoCleanupCheck.SelfCheck(Path.Combine(outputDir, "cleanup"));
GenerationCheck.SelfCheck(Path.Combine(outputDir, "generation"));
Console.WriteLine($"Self-check passed. Output written to {outputDir}");

internal static class LanguageResolverCheck
{
    private sealed class FakeTitle(string value, string languageCode) : ITitle
    {
        public string Value { get; set; } = value;
        public string LanguageCode { get; set; } = languageCode;
        public string? CountryCode { get; set; }
        public TitleLanguage Language { get; set; }
        public TitleType Type { get; set; }
        public DataSource Source { get; set; }
        public bool Equals(ITitle? other) => other is not null && other.Value == Value;
        public bool Equals(IText? other) => other is not null && other.Value == Value;
    }

    private sealed class FakeTitled : IWithTitles
    {
        public ITitle DefaultTitle { get; set; } = new FakeTitle("", "");
        public ITitle? PreferredTitle { get; set; }
        public IReadOnlyList<ITitle> Titles { get; set; } = [];
        public string Title => PreferredTitle?.Value ?? DefaultTitle.Value;
    }

    private sealed class FakeDescribed : IWithDescriptions
    {
        public IText? DefaultDescription { get; set; }
        public IText? PreferredDescription { get; set; }
        public IReadOnlyList<IText> Descriptions { get; set; } = [];
    }

    public static void SelfCheck()
    {
        var entity = new FakeTitled
        {
            PreferredTitle = new FakeTitle("Shoko Preferred", "en-US"),
            DefaultTitle = new FakeTitle("オリジナル", "ja-JP"),
            Titles =
            [
                new FakeTitle("Die Original", "de-DE"),
                new FakeTitle("The Original", "en-US"),
                new FakeTitle("オリジナル", "ja-JP"),
                new FakeTitle("Genroku Hanami Ondo", "x-jat"),
            ],
        };

        Assert(LanguageResolver.Title(entity, "de-DE") == "Die Original", "first language wins");
        Assert(LanguageResolver.Title(entity, "de-de") == "Die Original", "language codes match case-insensitively");
        Assert(LanguageResolver.Title(entity, "fr-FR, en-US") == "The Original", "falls back to next language");
        Assert(LanguageResolver.Title(entity, "shoko") == "Shoko Preferred", "shoko token uses preferred title");
        Assert(LanguageResolver.Title(entity, "original") == "オリジナル", "original token uses default title");
        Assert(LanguageResolver.Title(entity, "x-jat, original") == "Genroku Hanami Ondo", "custom x- codes match");
        Assert(LanguageResolver.Title(entity, "fr-FR") == "Shoko Preferred", "no match falls back to preferred");
        Assert(LanguageResolver.Title(entity, "") == "Shoko Preferred", "empty chain falls back to preferred");

        var described = new FakeDescribed
        {
            PreferredDescription = new FakeTitle("English plot", "en-US"),
            DefaultDescription = new FakeTitle("Deutsche Handlung", "de-DE"),
            Descriptions =
            [
                new FakeTitle("Deutsche Handlung", "de-DE"),
                new FakeTitle("English plot", "en-US"),
            ],
        };
        Assert(LanguageResolver.Description(described, "de-DE, en-US") == "Deutsche Handlung", "description first language wins");
        Assert(LanguageResolver.Description(described, "fr-FR") == "English plot", "description falls back to preferred");
        Assert(LanguageResolver.Description(described, "original") == "Deutsche Handlung", "description original token works");

        Console.WriteLine("OK LanguageResolver");
    }

    private static void Assert(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"LanguageResolver: {what}");
    }
}

internal static class NfoCleanupCheck
{
    public static void SelfCheck(string outputDir)
    {
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
        var root = Path.Combine(outputDir, "show");
        var season = Path.Combine(root, "Season 1");
        Directory.CreateDirectory(season);

        var rootVideoPath = Path.Combine(root, "root.mkv");
        var seasonVideoPath = Path.Combine(season, "episode.mkv");
        var files = new List<IVideoFile>
        {
            MappedVideoFile(rootVideoPath, 42),
            MappedVideoFile(seasonVideoPath, 42),
        };
        var videoService = DispatchProxy.Create<IVideoService, VideoServiceProxy>();
        ((VideoServiceProxy)(object)videoService).Files = files;
        var service = new NfoGeneratorService(null!, null!, null!, videoService, null!, NullLogger<NfoGeneratorService>.Instance);

        var imageSource = Path.Combine(outputDir, "source.jpg");
        Write(imageSource, "source artwork");
        var image = Proxy<IImage>(method => method.Name == "get_LocalPath" ? imageSource : null);
        var imageEntity = Proxy<IWithImages>(method => method.Name == "GetBestImageForType" ? image : null);
        var artFolder = Path.Combine(root, "art");
        Directory.CreateDirectory(artFolder);
        Write(Path.Combine(artFolder, "poster.jpg"), "user poster");
        var art = SidecarWriter.WriteFolderArt(artFolder, imageEntity);
        AssertEqual("poster.jpg", art["poster"], "pre-existing poster name changed");
        AssertEqual("user poster", File.ReadAllText(Path.Combine(artFolder, "poster.jpg")), "pre-existing poster was overwritten");
        Write(Path.Combine(artFolder, "thumb-7.jpg"), "user thumb");
        var thumb7 = SidecarWriter.WriteThumb(artFolder, imageEntity, 7);
        var thumb8 = SidecarWriter.WriteThumb(artFolder, imageEntity, 8);
        AssertEqual("thumb-7.jpg", thumb7, "existing thumb name changed");
        AssertEqual("thumb-8.jpg", thumb8, "unique thumb name was not generated");
        AssertEqual("user thumb", File.ReadAllText(Path.Combine(artFolder, "thumb-7.jpg")), "pre-existing thumb was overwritten");
        var missingSource = Path.Combine(outputDir, "missing-source.jpg");
        var missingImage = Proxy<IImage>(method => method.Name == "get_LocalPath" ? missingSource : null);
        var missingImageEntity = Proxy<IWithImages>(method => method.Name == "GetBestImageForType" ? missingImage : null);
        var missingTarget = Path.Combine(artFolder, "thumb-100.jpg");
        Write(missingTarget, "existing target");
        var missingThumb = SidecarWriter.WriteThumb(artFolder, missingImageEntity, 100);
        if (missingThumb is not null || File.ReadAllText(missingTarget) != "existing target")
            throw new InvalidOperationException("NFO cleanup: missing image source reused an existing sidecar");
        var unreadableSource = Path.Combine(outputDir, "unreadable-source.jpg");
        Directory.CreateDirectory(unreadableSource);
        var unreadableImage = Proxy<IImage>(method => method.Name == "get_LocalPath" ? unreadableSource : null);
        var unreadableImageEntity = Proxy<IWithImages>(method => method.Name == "GetBestImageForType" ? unreadableImage : null);
        var unreadableTarget = Path.Combine(artFolder, "thumb-101.jpg");
        Write(unreadableTarget, "existing target");
        string? unreadableSourcePath = null;
        string? unreadableTargetPath = null;
        Exception? unreadableError = null;
        var unreadableThumb = SidecarWriter.WriteThumb(artFolder, unreadableImageEntity, 101, (source, target, error) => { unreadableSourcePath = source; unreadableTargetPath = target; unreadableError = error; });
        if (unreadableThumb is not null || unreadableSourcePath != unreadableSource || unreadableTargetPath != unreadableTarget || unreadableError is null || File.ReadAllText(unreadableTarget) != "existing target")
            throw new InvalidOperationException("NFO cleanup: unreadable image source was reused or not reported");
        Directory.Delete(unreadableSource);
        var failedThumbTarget = Path.Combine(artFolder, "thumb-99.jpg");
        Directory.CreateDirectory(failedThumbTarget);
        var failedThumbSource = imageSource;
        string? failedThumbPath = null;
        string? failedThumbSourcePath = null;
        Exception? failedThumbError = null;
        var failedThumb = SidecarWriter.WriteThumb(artFolder, imageEntity, 99, (source, target, error) => { failedThumbSourcePath = source; failedThumbPath = target; failedThumbError = error; });
        if (failedThumb is not null || failedThumbSourcePath != failedThumbSource || failedThumbPath != failedThumbTarget || failedThumbError is null)
            throw new InvalidOperationException("NFO cleanup: sidecar copy failure was not reported");
        Directory.Delete(failedThumbTarget);
        var thumbNfoPath = Path.Combine(artFolder, "episode.nfo");
        NfoWriter.WriteEpisode(thumbNfoPath, new EpisodeNfo { Title = "Thumb", Thumb = thumb8 });
        AssertEqual(thumb8, XDocument.Load(thumbNfoPath).Root?.Element("thumb")?.Value, "episode NFO did not reference returned thumb");

        WritePluginNfo(Path.Combine(root, "tvshow.nfo"));
        WritePluginNfo(Path.Combine(season, "tvshow.nfo"));
        File.Delete(Path.Combine(root, "tvshow.nfo"));
        InvokeMisplacedSweep(service, root, 42);
        AssertExists(Path.Combine(season, "tvshow.nfo"), "descendant NFO was swept without a plugin-owned root");
        WritePluginNfo(Path.Combine(root, "tvshow.nfo"), tmdbId: "99");
        InvokeMisplacedSweep(service, root, 42);
        AssertExists(Path.Combine(season, "tvshow.nfo"), "descendant NFO was swept by a mismatched root");
        WritePluginNfo(Path.Combine(root, "tvshow.nfo"));
        InvokeMisplacedSweep(service, root, 42);
        AssertExists(Path.Combine(root, "tvshow.nfo"), "resolved show root NFO was removed");
        AssertMissing(Path.Combine(season, "tvshow.nfo"), "descendant plugin NFO was retained after a plugin-owned root existed");
        WritePluginNfo(Path.Combine(season, "tvshow.nfo"));
        files.Add(MappedVideoFile(Path.Combine(season, "episode-first.mkv"), 99, 42));
        InvokeMisplacedSweep(service, root, 42);
        AssertMissing(Path.Combine(season, "tvshow.nfo"), "episode-first TMDB show mapping was ignored during misplaced cleanup");
        files.RemoveAt(files.Count - 1);

        files.RemoveAt(0);
        WritePluginNfo(Path.Combine(root, "movie.nfo"), movie: true);
        WriteUserNfo(Path.Combine(root, "user.nfo"));
        Write(Path.Combine(root, "poster.jpg"), "user artwork");
        service.DeleteForPath(Path.Combine(root, "tvshow.mkv"));
        service.DeleteForPath(Path.Combine(root, "movie.mkv"));
        AssertExists(Path.Combine(root, "tvshow.nfo"), "tvshow.mkv deletion removed folder metadata");
        AssertExists(Path.Combine(root, "movie.nfo"), "movie.mkv deletion removed folder metadata");
        InvokeDirectorySweep(service, root, []);
        AssertExists(Path.Combine(root, "tvshow.nfo"), "show NFO with a season video was removed");
        AssertExists(Path.Combine(root, "movie.nfo"), "movie NFO with a season video was removed");
        AssertExists(Path.Combine(root, "user.nfo"), "user NFO was removed");
        AssertExists(Path.Combine(root, "poster.jpg"), "user artwork was removed");

        WritePluginNfo(Path.Combine(season, "episode.nfo"));
        WritePluginNfo(Path.Combine(season, "orphan.nfo"));
        WriteUserNfo(Path.Combine(season, "notes.nfo"));
        Write(Path.Combine(season, "fanart.jpg"), "user artwork");
        var seasonRemoved = InvokeDirectorySweep(service, season, [seasonVideoPath]);
        AssertEqual(1, seasonRemoved, "folder sweep counted a non-deletion or missed the orphan");
        AssertExists(Path.Combine(season, "episode.nfo"), "live direct episode NFO was removed");
        AssertMissing(Path.Combine(season, "orphan.nfo"), "orphan plugin NFO was retained");
        AssertExists(Path.Combine(season, "notes.nfo"), "season user NFO was removed");
        AssertExists(Path.Combine(season, "fanart.jpg"), "season artwork was removed");

        WriteUserNfo(Path.Combine(root, "deleted.nfo"));
        service.DeleteForPath(Path.Combine(root, "deleted.mkv"));
        AssertExists(Path.Combine(root, "deleted.nfo"), "delete sweep removed a user NFO");
        AssertExists(Path.Combine(root, "tvshow.nfo"), "delete sweep removed metadata with a season video");
        AssertExists(Path.Combine(root, "poster.jpg"), "delete sweep removed artwork");

        WritePluginNfo(Path.Combine(root, "removed.nfo"));
        service.DeleteForPath(Path.Combine(root, "removed.mkv"));
        AssertMissing(Path.Combine(root, "removed.nfo"), "delete sweep retained a plugin NFO");

        ((VideoServiceProxy)(object)videoService).Files = [];
        var rootRemoved = InvokeDirectorySweep(service, root, []);
        AssertEqual(2, rootRemoved, "empty-folder sweep deletion count changed");
        AssertMissing(Path.Combine(root, "tvshow.nfo"), "empty folder retained plugin show NFO");
        AssertMissing(Path.Combine(root, "movie.nfo"), "empty folder retained plugin movie NFO");
        AssertExists(Path.Combine(root, "user.nfo"), "empty folder removed a user NFO");
        AssertExists(Path.Combine(root, "poster.jpg"), "empty folder removed artwork");

        WritePluginNfo(Path.Combine(root, "tvshow.nfo"));
        service.DeleteForPath(Path.Combine(root, "gone.mkv"));
        AssertMissing(Path.Combine(root, "tvshow.nfo"), "delete sweep retained an orphan plugin show NFO");
        AssertExists(Path.Combine(root, "poster.jpg"), "delete sweep removed artwork from an empty folder");
        CheckDeleteFailureOutcome(service, outputDir);
        CheckMissingRelocationCleanup(service, videoService, outputDir);
        Console.WriteLine("OK NFO cleanup");
    }

    private static int InvokeDirectorySweep(NfoGeneratorService service, string folder, IReadOnlyList<string> livePaths)
        => (int)typeof(NfoGeneratorService)
            .GetMethod("SweepDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [folder, livePaths])!;

    private static void CheckDeleteFailureOutcome(NfoGeneratorService service, string outputDir)
    {
        var missingPath = Path.Combine(outputDir, "delete-missing.nfo");
        var missing = typeof(NfoGeneratorService)
            .GetMethod("TryDelete", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [missingPath])!;
        if (missing.GetType().GetProperty("Status")!.GetValue(missing)!.ToString() != "Missing"
            || missing.GetType().GetProperty("Error")!.GetValue(missing) is not null)
            throw new InvalidOperationException("NFO cleanup: missing delete outcome was not quiet");

        var directoryPath = Path.Combine(outputDir, "delete-failure.nfo");
        Directory.CreateDirectory(directoryPath);
        var result = typeof(NfoGeneratorService)
            .GetMethod("TryDelete", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [directoryPath])!;
        var status = result.GetType().GetProperty("Status")!.GetValue(result)!.ToString();
        var error = result.GetType().GetProperty("Error")!.GetValue(result);
        if (status != "Failed" || error is not Exception)
            throw new InvalidOperationException("NFO cleanup: failed delete outcome was not retained");
        Directory.Delete(directoryPath);
    }

    private static void CheckMissingRelocationCleanup(NfoGeneratorService service, IVideoService videoService, string outputDir)
    {
        var previousPath = Path.Combine(outputDir, "relocation", "missing.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(previousPath)!);
        NfoWriter.WriteEpisode(Path.ChangeExtension(previousPath, ".nfo"), new EpisodeNfo { Title = "Relocation" });
        var systemService = Proxy<ISystemService>(method => method.Name == "WaitForStartupAsync" ? Task.CompletedTask : null);
        var job = new NfoGenerationJob(service, null!, videoService, null!, systemService)
        {
            Kind = NfoGenerationKind.Relocated,
            ID = 404,
            PreviousPath = previousPath,
        };
        job.Process().GetAwaiter().GetResult();
        AssertMissing(Path.ChangeExtension(previousPath, ".nfo"), "relocation cleanup skipped a missing current target");
    }

    private static void InvokeMisplacedSweep(NfoGeneratorService service, string folder, int showId)
        => typeof(NfoGeneratorService)
            .GetMethod("SweepMisplacedShowNfos", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(int)], null)!
            .Invoke(service, [folder, showId]);

    private static IVideoFile MappedVideoFile(string path, int tmdbShowId, int? episodeShowId = null)
    {
        var crossReference = Proxy<ITmdbShowCrossReference>(method => method.Name switch
        {
            "get_TmdbShowID" => tmdbShowId,
            "get_MatchRating" => default(MatchRating),
            _ => null,
        });
        var series = Proxy<IShokoSeries>(method => method.Name == "get_TmdbShowCrossReferences" ? new[] { crossReference } : null);
        var episodeReferences = episodeShowId is { } episodeId
            ? new[] { Proxy<ITmdbEpisodeCrossReference>(method => method.Name switch
            {
                "get_TmdbShowID" => episodeId,
                "get_MatchRating" => default(MatchRating),
                "get_Ordering" => 1,
                "get_TmdbEpisodeID" => 1,
                _ => null,
            }) }
            : Array.Empty<ITmdbEpisodeCrossReference>();
        var episode = Proxy<IShokoEpisode>(method => method.Name switch
        {
            "get_Series" => series,
            "get_TmdbEpisodeCrossReferences" => episodeReferences,
            _ => null,
        });
        var video = Proxy<IVideo>(method => method.Name == "get_Episodes" ? new[] { episode } : null);
        return Proxy<IVideoFile>(method => method.Name switch
        {
            "get_Path" => path,
            "get_IsAvailable" => true,
            "get_Video" => video,
            _ => null,
        });
    }

    private static T Proxy<T>(Func<MethodInfo, object?> value) where T : class
    {
        var proxy = DispatchProxy.Create<T, ValueProxy<T>>();
        ((ValueProxy<T>)(object)proxy).Value = value;
        return proxy;
    }

    private static void WritePluginNfo(string path, bool movie = false, string tmdbId = "42")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var nfo = new ShowNfo { Title = "Plugin", ShokoId = "42", TmdbId = tmdbId };
        if (movie)
            NfoWriter.WriteMovie(path, nfo, force: true);
        else
            NfoWriter.WriteTvShow(path, nfo, force: true);
    }

    private static void WriteUserNfo(string path) => Write(path, "<tvshow><uniqueid type=\"shoko\">user</uniqueid><title>User</title></tvshow>");

    private static void Write(string path, string content) => File.WriteAllText(path, content);

    private static void AssertExists(string path, string message)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"NFO cleanup: {message}");
    }

    private static void AssertMissing(string path, string message)
    {
        if (File.Exists(path))
            throw new InvalidOperationException($"NFO cleanup: {message}");
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"NFO cleanup: {message}");
    }

    private class ValueProxy<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?> Value { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod is null ? null : Value(targetMethod);
    }

    private class VideoServiceProxy : DispatchProxy
    {
        public IReadOnlyList<IVideoFile> Files { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetAllVideoFiles")
                return Files;
            if (targetMethod?.Name == "GetVideoFilesByAbsolutePath")
            {
                var folder = (string)args![0]!;
                return Files.Where(f => Path.GetDirectoryName(f.Path) is { } fileFolder && IsWithin(fileFolder, folder)).ToList();
            }
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }

        private static bool IsWithin(string path, string root)
        {
            var relative = Path.GetRelativePath(root, path);
            return relative == "." || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != "..");
        }
    }
}

internal static class GenerationCheck
{
    private static readonly Dictionary<IShokoSeries, List<IShokoEpisode>> SeriesEpisodes = [];

    public static void SelfCheck(string outputDir)
    {
        SeriesEpisodes.Clear();
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
        var libraryA = Path.Combine(outputDir, "library-a");
        var libraryB = Path.Combine(outputDir, "library-b");
        Directory.CreateDirectory(libraryA);
        Directory.CreateDirectory(libraryB);

        var canonical = CreateSeries(10, "Canonical", "Canonical plot", 888);
        var other = CreateSeries(20, "Other", "Other plot", 888);
        var entries = new[]
        {
            CreateEntry(Path.Combine(libraryA, "Show", "Season 1", "other.mkv"), 201, other, 1),
            CreateEntry(Path.Combine(libraryA, "Show", "Season 2", "canonical.mkv"), 101, canonical, 2),
            CreateEntry(Path.Combine(libraryB, "Show", "Season 1", "other.mkv"), 202, other, 3),
            CreateEntry(Path.Combine(libraryB, "Show", "Season 2", "canonical.mkv"), 102, canonical, 4),
        };
        var files = entries.Select(e => e.File).ToList();
        var managedFolders = new List<IManagedFolder> { ManagedFolder(libraryA), ManagedFolder(libraryB) };
        var videoService = DispatchProxy.Create<IVideoService, GenerationVideoServiceProxy>();
        var videoProxy = (GenerationVideoServiceProxy)(object)videoService;
        videoProxy.Files = files;
        videoProxy.ManagedFolders = managedFolders;
        var allSeries = new List<IShokoSeries> { canonical, other };
        var metadataService = Proxy<IMetadataService>(method => method.Name == "GetAllShokoSeries" ? allSeries : Default(method));
        var configurationService = Proxy<IConfigurationService>(method => method.Name == "Load" ? new NfoGeneratorSettings() : Default(method));
        var settings = new ConfigurationProvider<NfoGeneratorSettings>(configurationService);
        var service = new NfoGeneratorService(null!, settings, metadataService, videoService, null!, NullLogger<NfoGeneratorService>.Instance);

        int written = InvokeGenerateForFiles(service, files);
        AssertEqual(4, written, "episode NFO generation count changed");
        AssertEqual(6, Directory.EnumerateFiles(outputDir, "*.nfo", SearchOption.AllDirectories).Count(), "TV roots were not generated once per managed-folder scope");
        AssertRoot(Path.Combine(libraryA, "Show", "tvshow.nfo"));
        AssertRoot(Path.Combine(libraryB, "Show", "tvshow.nfo"));

        var linkedRoot = Path.Combine(outputDir, "linked");
        managedFolders.Add(ManagedFolder(linkedRoot));
        var linkedOther = CreateEntry(Path.Combine(linkedRoot, "Show", "Season 1", "other.mkv"), 601, other, 10, linkedSeries: [other], tmdbShowSeries: [other, canonical]);
        var linkedCanonical = CreateEntry(Path.Combine(linkedRoot, "Show", "Season 2", "canonical.mkv"), 602, canonical, 11, linkedSeries: [canonical], tmdbShowSeries: [other, canonical]);
        AssertEqual(1, linkedOther.File.Video!.Series.Count, "linked fixture incorrectly exposed the episode-only series as a direct video link");
        AssertEqual(other.ID, linkedOther.File.Video.Series.Single().ID, "linked fixture direct series changed");
        videoProxy.Files = [linkedOther.File, linkedCanonical.File];
        videoProxy.ThrowOnAllVideoFiles = true;
        var linkedMetadata = Proxy<IMetadataService>(method => method.Name == "GetAllShokoSeries" ? throw new InvalidOperationException("Generation: linked path used metadata fallback") : Default(method));
        var linkedSettings = new ConfigurationProvider<NfoGeneratorSettings>(configurationService);
        var linkedService = new NfoGeneratorService(null!, linkedSettings, linkedMetadata, videoService, null!, NullLogger<NfoGeneratorService>.Instance);
        InvokeGenerateForFiles(linkedService, [linkedOther.File]);
        AssertRoot(Path.Combine(linkedRoot, "Show", "tvshow.nfo"));
        videoProxy.ThrowOnAllVideoFiles = false;

        var fallbackCalls = 0;
        videoProxy.Files = [entries[0].File, entries[1].File];
        videoProxy.ManagedFolders = [ManagedFolder(libraryA)];
        var fallbackMetadata = Proxy<IMetadataService>(method =>
        {
            if (method.Name == "GetAllShokoSeries")
            {
                fallbackCalls++;
                return allSeries;
            }
            return Default(method);
        });
        var fallbackSettings = new ConfigurationProvider<NfoGeneratorSettings>(configurationService);
        var fallbackService = new NfoGeneratorService(null!, fallbackSettings, fallbackMetadata, videoService, null!, NullLogger<NfoGeneratorService>.Instance);
        Invalidate(fallbackService, [libraryA]);
        InvokeGenerateForFiles(fallbackService, [entries[0].File, entries[1].File]);
        AssertEqual(1, fallbackCalls, "metadata fallback was not bounded by pass/scope");

        CheckDirectSharingBurst(fallbackService, videoProxy, libraryA, other, canonical);
        CheckNullReleaseDeletionInvalidates(fallbackService, videoProxy, libraryA);
        CheckMisplacedSweepBurst(fallbackService, videoProxy, outputDir, other);

        var singleRoot = Path.Combine(outputDir, "single");
        videoProxy.ManagedFolders = managedFolders;
        managedFolders.Add(ManagedFolder(singleRoot));
        var singleOther = CreateEntry(Path.Combine(singleRoot, "Show", "Season 1", "other.mkv"), 301, other, 5, linkedSeries: [other, canonical]);
        var singleCanonical = CreateEntry(Path.Combine(singleRoot, "Show", "Season 2", "canonical.mkv"), 302, canonical, 6, linkedSeries: [other, canonical]);
        AssertEqual(2, singleOther.File.Video!.Series.Count, "linked series fixture was not exposed");
        videoProxy.Files = files.Concat([singleOther.File, singleCanonical.File]).ToList();
        InvokeGenerateForFiles(service, [singleOther.File]);
        AssertRoot(Path.Combine(singleRoot, "Show", "tvshow.nfo"));

        var unmappedA = CreateSeries(30, "Unmapped A", "Unmapped A plot", null);
        var unmappedB = CreateSeries(40, "Unmapped B", "Unmapped B plot", null);
        allSeries.AddRange([unmappedA, unmappedB]);
        var unmappedEntryA = CreateEntry(Path.Combine(libraryA, "Unmapped A", "episode.mkv"), 401, unmappedA, 7, episodeShowId: null);
        var unmappedEntryB = CreateEntry(Path.Combine(libraryA, "Unmapped B", "episode.mkv"), 402, unmappedB, 8, episodeShowId: null);
        videoProxy.Files = files.Concat([singleOther.File, singleCanonical.File, unmappedEntryA.File, unmappedEntryB.File]).ToList();
        InvokeGenerateForFiles(service, [unmappedEntryA.File, unmappedEntryB.File]);
        AssertExists(Path.Combine(libraryA, "Unmapped A", "tvshow.nfo"), "unmapped series A shared the managed-folder scope");
        AssertExists(Path.Combine(libraryA, "Unmapped B", "tvshow.nfo"), "unmapped series B shared the managed-folder scope");

        RetryFailedRootWrite(service, outputDir, canonical);
        Console.WriteLine("OK generation state");
    }

    private static void CheckDirectSharingBurst(NfoGeneratorService service, object videoProxy, string root, IShokoSeries other, IShokoSeries canonical)
    {
        var proxy = (GenerationVideoServiceProxy)videoProxy;
        var method = typeof(NfoGeneratorService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == "GetDirectFolderShared");
        var folder = Path.Combine(root, "Show", "Season 1");
        Invalidate(service, [folder]);
        proxy.AbsolutePathCalls = 0;
        AssertEqual(false, (bool)method.Invoke(service, [folder, NewPass()])!, "false direct sharing result changed");
        int firstCalls = proxy.AbsolutePathCalls;
        AssertEqual(false, (bool)method.Invoke(service, [folder, NewPass()])!, "false direct sharing cache changed");
        AssertEqual(firstCalls, proxy.AbsolutePathCalls, "false direct sharing result was not cached");
        Invalidate(service, [folder]);
        AssertEqual(false, (bool)method.Invoke(service, [folder, NewPass()])!, "invalidated false direct sharing result changed");
        AssertEqual(firstCalls + 1, proxy.AbsolutePathCalls, "false direct sharing cache was not invalidated");

        var sharedFolder = Path.Combine(root, "Shared");
        var sharedOther = CreateEntry(Path.Combine(sharedFolder, "other.mkv"), 701, other, 12);
        var sharedCanonical = CreateEntry(Path.Combine(sharedFolder, "canonical.mkv"), 702, canonical, 13);
        proxy.Files = [sharedOther.File, sharedCanonical.File];
        Invalidate(service, [sharedFolder]);
        proxy.AbsolutePathCalls = 0;
        AssertEqual(true, (bool)method.Invoke(service, [sharedFolder, NewPass()])!, "true direct sharing result changed");
        firstCalls = proxy.AbsolutePathCalls;
        AssertEqual(true, (bool)method.Invoke(service, [sharedFolder, NewPass()])!, "true direct sharing cache changed");
        AssertEqual(firstCalls, proxy.AbsolutePathCalls, "true direct sharing result was not cached");
    }

    private static void CheckNullReleaseDeletionInvalidates(NfoGeneratorService service, object videoProxy, string root)
    {
        var proxy = (GenerationVideoServiceProxy)videoProxy;
        var folder = Path.Combine(root, "Show", "Season 2");
        var method = typeof(NfoGeneratorService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == "GetDirectFolderShared");
        proxy.AbsolutePathCalls = 0;
        method.Invoke(service, [folder, NewPass()]);
        int firstCalls = proxy.AbsolutePathCalls;
        var deleted = new VideoReleaseDeletedEventArgs { Video = null, ReleaseInfo = null!, NewReleaseInfo = null! };
        typeof(NfoGeneratorService).GetMethod("OnReleaseDeleted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [null, deleted]);
        method.Invoke(service, [folder, NewPass()]);
        AssertEqual(firstCalls + 1, proxy.AbsolutePathCalls, "release deletion without video did not invalidate topology");
    }

    private static void CheckMisplacedSweepBurst(NfoGeneratorService service, object videoProxy, string outputDir, IShokoSeries series)
    {
        var proxy = (GenerationVideoServiceProxy)videoProxy;
        var root = Path.Combine(outputDir, "sweep", "Show");
        var season = Path.Combine(root, "Season 1");
        var entry = CreateEntry(Path.Combine(season, "episode.mkv"), 801, series, 14);
        proxy.Files = [entry.File];
        WritePluginShow(Path.Combine(root, "tvshow.nfo"));
        WritePluginShow(Path.Combine(season, "tvshow.nfo"));
        var pass = NewPass();
        AddShowScope(pass, root, 777);
        proxy.AbsolutePathCalls = 0;
        InvokeSweep(service, pass);
        AssertMissing(Path.Combine(season, "tvshow.nfo"), "successful misplaced sweep did not remove the descendant");
        int firstCalls = proxy.AbsolutePathCalls;
        InvokeSweep(service, pass);
        AssertEqual(firstCalls, proxy.AbsolutePathCalls, "successful misplaced sweep was not deduplicated");
        InvokeSweep(service, NewPass());
        AssertEqual(firstCalls, proxy.AbsolutePathCalls, "successful misplaced sweep was not reused across the burst");

        var failedPass = NewPass();
        AddShowScope(failedPass, Path.Combine(outputDir, "missing-root"), 777);
        InvokeSweep(service, failedPass);
        InvokeSweep(service, failedPass);
        var completed = failedPass.GetType().GetProperty("CompletedMisplacedSweeps")!.GetValue(failedPass)!;
        AssertEqual(0, (int)completed.GetType().GetProperty("Count")!.GetValue(completed)!, "failed misplaced sweep was cached as complete");
    }

    private static void Invalidate(NfoGeneratorService service, IEnumerable<string> paths)
        => typeof(NfoGeneratorService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "InvalidateTopologyForPaths")
            .Invoke(service, [paths]);

    private static object NewPass()
        => Activator.CreateInstance(typeof(NfoGeneratorService).GetNestedType("GenerationPass", BindingFlags.NonPublic)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [true], null)!;

    private static void AddShowScope(object pass, string root, int showId)
    {
        var scopeType = typeof(NfoGeneratorService).GetNestedType("ShowScope", BindingFlags.NonPublic)!;
        var scope = Activator.CreateInstance(scopeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [showId, root], null)!;
        var folders = (System.Collections.IDictionary)pass.GetType().GetProperty("ShowFolders")!.GetValue(pass)!;
        folders.Add(scope, root);
    }

    private static void InvokeSweep(NfoGeneratorService service, object pass)
        => typeof(NfoGeneratorService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "SweepMisplacedShowNfos" && m.GetParameters().Length == 1)
            .Invoke(service, [pass]);

    private static void WritePluginShow(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        NfoWriter.WriteTvShow(path, new ShowNfo { Title = "Sweep", ShokoId = "42", TmdbId = "777" }, force: true);
    }

    private static int InvokeGenerateForFiles(NfoGeneratorService service, IReadOnlyList<IVideoFile> files)
        => (int)typeof(NfoGeneratorService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "GenerateForFiles")
            .Invoke(service, [files, true, null, false, null, null])!;

    private static void AssertRoot(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Generation: missing root {path}");
        var root = XDocument.Load(path).Root ?? throw new InvalidOperationException($"Generation: missing XML root {path}");
        AssertEqual("Canonical", root.Element("title")?.Value, $"non-deterministic canonical title in {path}");
        AssertEqual("Canonical plot", root.Element("plot")?.Value, $"episode fallback plot leaked into {path}");
        if (root.Element("runtime") is not null)
            throw new InvalidOperationException($"Generation: episode runtime leaked into {path}");
        AssertEqual("777", root.Elements("uniqueid").FirstOrDefault(e => (string?)e.Attribute("type") == "tmdb")?.Value, $"episode-first TMDB mapping failed in {path}");
    }

    private static void RetryFailedRootWrite(NfoGeneratorService service, string outputDir, IShokoSeries series)
    {
        var folder = Path.Combine(outputDir, "retry", "Show", "Season 1");
        var entry = CreateEntry(Path.Combine(folder, "episode.mkv"), 501, series, 9);
        var rootPath = Path.Combine(folder, "tvshow.nfo");
        Directory.CreateDirectory(rootPath);
        var passType = typeof(NfoGeneratorService).GetNestedType("GenerationPass", BindingFlags.NonPublic)!;
        var pass = Activator.CreateInstance(passType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [true], null)!;
        var write = typeof(NfoGeneratorService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(method => method.Name == "WriteForFile");
        try
        {
            write.Invoke(service, [entry.File, true, true, pass, null, null]);
            throw new InvalidOperationException("Generation: failed root write unexpectedly succeeded");
        }
        catch (TargetInvocationException)
        {
        }
        Directory.Delete(rootPath);
        write.Invoke(service, [entry.File, true, true, pass, null, null]);
        AssertExists(rootPath, "failed root write was not retried");
    }

    private static void AssertExists(string path, string message)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Generation: {message}");
    }

    private static void AssertMissing(string path, string message)
    {
        if (File.Exists(path))
            throw new InvalidOperationException($"Generation: {message}");
    }

    private sealed record GenerationEntry(IVideoFile File);

    private static GenerationEntry CreateEntry(string path, int fileId, IShokoSeries series, int episodeNumber, int? episodeShowId = 777, IReadOnlyList<IShokoSeries>? linkedSeries = null, IReadOnlyList<IShokoSeries>? tmdbShowSeries = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var episodeTitle = Proxy<ITitle>(method => method.Name switch
        {
            "get_Value" => $"Episode {episodeNumber}",
            "get_LanguageCode" => "en-US",
            _ => Default(method),
        });
        var episodeReferences = episodeShowId is { } resolvedShowId
            ? new[] { Proxy<ITmdbEpisodeCrossReference>(method => method.Name switch
            {
                "get_TmdbShowID" => resolvedShowId,
                "get_TmdbShow" when linkedSeries is not null => Proxy<ITmdbShow>(showMethod => showMethod.Name == "get_ShokoSeries"
                    ? tmdbShowSeries ?? linkedSeries
                    : Default(showMethod)),
                "get_MatchRating" => default(MatchRating),
                "get_Ordering" => episodeNumber,
                "get_TmdbEpisodeID" => episodeNumber,
                _ => Default(method),
            }) }
            : Array.Empty<ITmdbEpisodeCrossReference>();
        IVideo? video = null;
        var episode = Proxy<IShokoEpisode>(method => method.Name switch
        {
            "get_Series" => series,
            "get_DefaultTitle" => episodeTitle,
            "get_PreferredTitle" => episodeTitle,
            "get_Titles" => new[] { episodeTitle },
            "get_DefaultDescription" => episodeTitle,
            "get_PreferredDescription" => episodeTitle,
            "get_Descriptions" => new[] { (IText)episodeTitle },
            "get_TmdbEpisodeCrossReferences" => episodeReferences,
            "get_TmdbMovieCrossReferences" => Array.Empty<ITmdbMovieCrossReference>(),
            "get_VideoList" => video is null ? Array.Empty<IVideo>() : new[] { video },
            "get_SeasonNumber" => 1,
            "get_EpisodeNumber" => episodeNumber,
            "get_Runtime" => TimeSpan.FromMinutes(24),
            "get_ID" => fileId + 1000,
            "get_AnidbEpisodeID" => fileId + 2000,
            _ => Default(method),
        });
        SeriesEpisodes[series].Add(episode);
        video = Proxy<IVideo>(method => method.Name switch
        {
            "get_Episodes" => new[] { episode },
            "get_Series" => linkedSeries ?? new[] { series },
            "get_Files" => new[] { CreateFile(path, fileId, video!) },
            _ => Default(method),
        });
        return new(CreateFile(path, fileId, video));
    }

    private static IVideoFile CreateFile(string path, int fileId, IVideo video)
        => Proxy<IVideoFile>(method => method.Name switch
        {
            "get_ID" => fileId,
            "get_Path" => path,
            "get_IsAvailable" => true,
            "get_Video" => video,
            _ => Default(method),
        });

    private static IShokoSeries CreateSeries(int id, string titleValue, string descriptionValue, int? seriesShowId)
    {
        var title = Proxy<ITitle>(method => method.Name switch
        {
            "get_Value" => titleValue,
            "get_LanguageCode" => "en-US",
            _ => Default(method),
        });
        var description = Proxy<IText>(method => method.Name switch
        {
            "get_Value" => descriptionValue,
            "get_LanguageCode" => "en-US",
            _ => Default(method),
        });
        var showReferences = seriesShowId is { } resolvedShowId
            ? new[] { Proxy<ITmdbShowCrossReference>(method => method.Name switch
            {
                "get_TmdbShowID" => resolvedShowId,
                "get_MatchRating" => default(MatchRating),
                _ => Default(method),
            }) }
            : Array.Empty<ITmdbShowCrossReference>();
        var episodes = new List<IShokoEpisode>();
        var series = Proxy<IShokoSeries>(method => method.Name switch
        {
            "get_ID" => id,
            "get_DefaultTitle" => title,
            "get_PreferredTitle" => title,
            "get_Titles" => new[] { title },
            "get_DefaultDescription" => description,
            "get_PreferredDescription" => description,
            "get_Descriptions" => new[] { description },
            "get_TmdbShowCrossReferences" => showReferences,
            "get_Type" => AnimeType.TV,
            "get_TmdbMovieCrossReferences" => Array.Empty<ITmdbMovieCrossReference>(),
            "get_Episodes" => episodes,
            "get_Studios" => Empty(method),
            _ => Default(method),
        });
        SeriesEpisodes[series] = episodes;
        return series;
    }

    private static IManagedFolder ManagedFolder(string path)
        => Proxy<IManagedFolder>(method => method.Name == "get_Path" ? path : Default(method));

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Generation: {message} (expected {expected}, got {actual})");
    }

    private static object? Default(MethodInfo method)
    {
        if (method.ReturnType == typeof(void))
            return null;
        if (method.ReturnType.IsValueType)
            return Activator.CreateInstance(method.ReturnType);
        return Empty(method);
    }

    private static object? Empty(MethodInfo method)
    {
        if (method.ReturnType.IsArray)
            return Array.CreateInstance(method.ReturnType.GetElementType()!, 0);
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(method.ReturnType) && method.ReturnType.IsGenericType)
            return Array.CreateInstance(method.ReturnType.GetGenericArguments()[0], 0);
        return null;
    }

    private static T Proxy<T>(Func<MethodInfo, object?> value) where T : class
    {
        var proxy = DispatchProxy.Create<T, GenerationValueProxy<T>>();
        ((GenerationValueProxy<T>)(object)proxy).Value = value;
        return proxy;
    }

    private class GenerationValueProxy<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?> Value { get; set; } = _ => null;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod is null ? null : Value(targetMethod);
    }

    private class GenerationVideoServiceProxy : DispatchProxy
    {
        public IReadOnlyList<IVideoFile> Files { get; set; } = [];
        public IReadOnlyList<IManagedFolder> ManagedFolders { get; set; } = [];
        public bool ThrowOnAllVideoFiles { get; set; }
        public int AbsolutePathCalls { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetAllVideoFiles")
            {
                if (ThrowOnAllVideoFiles)
                    throw new InvalidOperationException("Generation: unexpected whole-library video scan");
                return Files;
            }
            if (targetMethod?.Name == "GetAllManagedFolders")
                return ManagedFolders;
            if (targetMethod?.Name == "GetVideoFilesByAbsolutePath")
            {
                AbsolutePathCalls++;
                var folder = (string)args![0]!;
                return Files.Where(file => Path.GetDirectoryName(file.Path) is { } fileFolder && IsWithin(fileFolder, folder)).ToList();
            }
            return targetMethod is null ? null : Default(targetMethod);
        }

        private static bool IsWithin(string path, string root)
        {
            var relative = Path.GetRelativePath(root, path);
            return relative == "." || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != "..");
        }
    }
}
