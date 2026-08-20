using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Shoko.Plugin.NfoGenerator.Nfo;

/// <summary>Kodi-style episode NFO data.</summary>
public sealed class EpisodeNfo
{
    public string? Title;
    public string? ShowTitle;
    public string? Plot;
    public string? Aired;
    public int? Season;
    public int? Episode;
    public int? RuntimeMinutes;
    public double? Rating;
    public int? Votes;
    public string? AnidbId;
    public string? ShokoId;
    public string? TmdbId;
    public string? Thumb;
}

/// <summary>
/// Kodi-style tvshow/movie NFO data. The root element differs
/// (<c>&lt;tvshow&gt;</c> vs <c>&lt;movie&gt;</c>), everything else is shared.
/// </summary>
public sealed class ShowNfo
{
    public string? Title;
    public string? OriginalTitle;
    public string? Plot;
    public string? Premiered;
    public int? Year;
    public int? RuntimeMinutes;
    public double? Rating;
    public int? Votes;
    public string? AnidbId;
    public string? ShokoId;
    public string? TmdbId;
    public IReadOnlyList<string> Genres = [];
    public IReadOnlyList<string> Studios = [];

    /// <summary>Art sidecar filename per Kodi art key (e.g. "poster" -> "poster.jpg").</summary>
    public IReadOnlyDictionary<string, string> Art = new Dictionary<string, string>();
}

internal static class NfoWriter
{
    internal const string OwnershipMarker = "Shoko.Plugin.NfoGenerator: 5c5482c1-3dd0-49cb-b862-d57e305da353";

    /// <summary>Writes an episode NFO, skipping the write if the content is unchanged. Returns true if the file was written.</summary>
    public static bool WriteEpisode(string path, EpisodeNfo nfo, bool force = false) => Complete(path, WriteEpisodeDetailed(path, nfo, force));

    /// <summary>Writes a tvshow NFO, skipping the write if the content is unchanged. Returns true if the file was written.</summary>
    public static bool WriteTvShow(string path, ShowNfo nfo, bool force = false) => Complete(path, WriteTvShowDetailed(path, nfo, force));

    /// <summary>Writes a movie NFO, skipping the write if the content is unchanged. Returns true if the file was written.</summary>
    public static bool WriteMovie(string path, ShowNfo nfo, bool force = false) => Complete(path, WriteMovieDetailed(path, nfo, force));

    internal static NfoWriteResult WriteEpisodeDetailed(string path, EpisodeNfo nfo, bool force = false)
        => Write(path, BuildEpisode(nfo), force);

    internal static NfoWriteResult WriteTvShowDetailed(string path, ShowNfo nfo, bool force = false)
        => Write(path, BuildShow("tvshow", nfo), force);

    internal static NfoWriteResult WriteMovieDetailed(string path, ShowNfo nfo, bool force = false)
        => Write(path, BuildShow("movie", nfo), force);

    private static bool Complete(string path, NfoWriteResult result)
    {
        if (result.Status is NfoWriteStatus.OwnershipReadFailed
            or NfoWriteStatus.ContentReadFailed
            or NfoWriteStatus.WriteFailed)
            throw new IOException($"Unable to {result.Status} NFO at '{path}'.", result.Error);
        return result.Status == NfoWriteStatus.Written;
    }

    private static XDocument BuildEpisode(EpisodeNfo n)
    {
        var root = new XElement("episodedetails",
            El("title", n.Title),
            El("showtitle", n.ShowTitle),
            El("season", n.Season),
            El("episode", n.Episode),
            El("plot", n.Plot),
            El("aired", n.Aired),
            El("runtime", n.RuntimeMinutes),
            El("rating", n.Rating),
            El("votes", n.Votes),
            UniqueId("tmdb", n.TmdbId, isDefault: n.TmdbId is not null),
            UniqueId("anidb", n.AnidbId, isDefault: n.TmdbId is null),
            UniqueId("shoko", n.ShokoId),
            El("thumb", n.Thumb));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XComment(OwnershipMarker), root);
    }

    private static XDocument BuildShow(string rootTag, ShowNfo n)
    {
        var root = new XElement(rootTag,
            El("title", n.Title),
            El("originaltitle", n.OriginalTitle),
            El("plot", n.Plot),
            El("premiered", n.Premiered),
            El("year", n.Year),
            El("rating", n.Rating),
            El("votes", n.Votes),
            El("runtime", n.RuntimeMinutes),
            n.Studios.Select(s => new XElement("studio", s)),
            n.Genres.Select(g => new XElement("genre", g)),
            UniqueId("tmdb", n.TmdbId, isDefault: n.TmdbId is not null),
            UniqueId("anidb", n.AnidbId, isDefault: n.TmdbId is null),
            UniqueId("shoko", n.ShokoId),
            BuildArt(n.Art));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XComment(OwnershipMarker), root);
    }

    private static XElement? BuildArt(IReadOnlyDictionary<string, string> art)
    {
        if (art.Count == 0)
            return null;
        return new XElement("art", art.Select(kv => new XElement(kv.Key, kv.Value)));
    }

    private static XElement? El(string name, object? value)
        => value switch
        {
            null => null,
            string s when string.IsNullOrEmpty(s) => null,
            string s => new XElement(name, s),
            int i => new XElement(name, i.ToString(CultureInfo.InvariantCulture)),
            long l => new XElement(name, l.ToString(CultureInfo.InvariantCulture)),
            double d => new XElement(name, d.ToString("0.###", CultureInfo.InvariantCulture)),
            _ => new XElement(name, value.ToString()),
        };

    private static XElement? UniqueId(string type, string? value, bool isDefault = false)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var el = new XElement("uniqueid", value);
        el.SetAttributeValue("type", type);
        if (isDefault)
            el.SetAttributeValue("default", true);
        return el;
    }

    // ponytail: the file on disk is the cache. We serialize first, then only
    // write when the content differs, so unchanged NFOs keep their timestamp
    // (no media-library rescans) and repeated triggers cost one small read.
    // Pass force=true to always rewrite (used for metadata updates).
    private static NfoWriteResult Write(string path, XDocument doc, bool force)
    {
        // Serialize via a stream, not a StringBuilder: the latter stamps the
        // declaration with encoding="utf-16".
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
            doc.Save(writer);
        var content = Encoding.UTF8.GetString(ms.ToArray());

        bool exists = File.Exists(path);
        if (exists)
        {
            var ownership = ReadOwnership(path);
            if (ownership.Status == NfoOwnershipStatus.Failed)
                return new(NfoWriteStatus.OwnershipReadFailed, ownership.Error);
            if (ownership.Status != NfoOwnershipStatus.Owned)
                return new(NfoWriteStatus.Unowned, null);
        }
        if (!force && exists)
        {
            try
            {
                if (File.ReadAllText(path) == content)
                    return new(NfoWriteStatus.Unchanged, null);
            }
            catch (IOException ex)
            {
                return new(NfoWriteStatus.ContentReadFailed, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new(NfoWriteStatus.ContentReadFailed, ex);
            }
        }

        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return new(NfoWriteStatus.Written, null);
        }
        catch (IOException ex)
        {
            return new(NfoWriteStatus.WriteFailed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(NfoWriteStatus.WriteFailed, ex);
        }
    }

    /// <summary>True only for NFOs emitted by this plugin.</summary>
    internal static bool IsOwned(string path)
        => ReadOwnership(path).Status == NfoOwnershipStatus.Owned;

    private static NfoOwnershipResult ReadOwnership(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains(OwnershipMarker, StringComparison.Ordinal)
                ? new(NfoOwnershipStatus.Owned, null)
                : new(NfoOwnershipStatus.Unowned, null);
        }
        catch (FileNotFoundException)
        {
            return new(NfoOwnershipStatus.Unowned, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(NfoOwnershipStatus.Unowned, null);
        }
        catch (IOException ex)
        {
            return new(NfoOwnershipStatus.Failed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(NfoOwnershipStatus.Failed, ex);
        }
    }

    internal enum NfoWriteStatus
    {
        Written,
        Unchanged,
        Unowned,
        OwnershipReadFailed,
        ContentReadFailed,
        WriteFailed,
    }

    internal readonly record struct NfoWriteResult(NfoWriteStatus Status, Exception? Error);

    private enum NfoOwnershipStatus
    {
        Owned,
        Unowned,
        Failed,
    }

    private readonly record struct NfoOwnershipResult(NfoOwnershipStatus Status, Exception? Error);

    /// <summary>
    /// Asserts the XML output for each NFO type is well-formed and valid.
    /// Fails (throws) if any of the types break. Used by the self-check runner.
    /// </summary>
    public static void SelfCheck(string outputDir)
    {
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var episode = new EpisodeNfo
        {
            Title = "Re:Zero kara Hajimeru Isekai Seikatsu",
            ShowTitle = "Re:Zero Starting Life in Another World",
            Plot = "A dark fantasy isekai about Subaru Natsuki.",
            Aired = "2016-04-04",
            Season = 1,
            Episode = 1,
            RuntimeMinutes = 25,
            Rating = 8.05,
            Votes = 1200,
            AnidbId = "11294",
            ShokoId = "42",
            TmdbId = "42509",
            Thumb = "thumb.jpg",
        };
        var episodePath = Path.Combine(outputDir, "episode.nfo");
        WriteEpisode(episodePath, episode);
        AssertValid(episodePath, "episodedetails", "Re:Zero kara Hajimeru Isekai Seikatsu", "aired", "2016-04-04", "11294", "42509");

        var show = new ShowNfo
        {
            Title = "Re:Zero Starting Life in Another World",
            OriginalTitle = "Re:Zero kara Hajimeru Isekai Seikatsu",
            Plot = "A dark fantasy isekai about Subaru Natsuki.",
            Premiered = "2016-04-04",
            Year = 2016,
            RuntimeMinutes = 25,
            Rating = 8.05,
            Votes = 1200,
            AnidbId = "11294",
            ShokoId = "42",
            TmdbId = "64251",
            Genres = ["Drama", "Fantasy"],
            Studios = ["White Fox"],
            Art = new Dictionary<string, string> { ["poster"] = "poster.jpg", ["fanart"] = "fanart.jpg" },
        };
        var showPath = Path.Combine(outputDir, "show.nfo");
        WriteTvShow(showPath, show);
        AssertValid(showPath, "tvshow", "Re:Zero Starting Life in Another World", "year", "2016", "11294", "64251");

        var moviePath = Path.Combine(outputDir, "movie.nfo");
        WriteMovie(moviePath, show);
        AssertValid(moviePath, "movie", "Re:Zero Starting Life in Another World", "year", "2016", "11294", "64251");

        // Content check: unchanged data must not rewrite the file, changed data must.
        if (WriteEpisode(episodePath, episode))
            throw new InvalidOperationException($"{episodePath}: identical content was rewritten");
        var unchangedBytes = File.ReadAllBytes(episodePath);
        episode.Rating = 9.0;
        if (!WriteEpisode(episodePath, episode))
            throw new InvalidOperationException($"{episodePath}: changed content was skipped");
        if (File.ReadAllBytes(episodePath).SequenceEqual(unchangedBytes))
            throw new InvalidOperationException($"{episodePath}: changed content was not written");

        var userPath = Path.Combine(outputDir, "user.nfo");
        const string userContent = "<episodedetails><uniqueid type=\"shoko\">user</uniqueid></episodedetails>";
        File.WriteAllText(userPath, userContent);
        if (WriteEpisode(userPath, episode, force: true) || File.ReadAllText(userPath) != userContent)
            throw new InvalidOperationException($"{userPath}: unowned NFO was overwritten");

        var ownedPath = Path.Combine(outputDir, "owned.nfo");
        WriteEpisode(ownedPath, episode);
        episode.Rating = 8.0;
        if (!WriteEpisode(ownedPath, episode, force: true))
            throw new InvalidOperationException($"{ownedPath}: owned NFO was not updated");

        var failedPath = Path.Combine(outputDir, "failed.nfo");
        Directory.CreateDirectory(failedPath);
        var failed = WriteEpisodeDetailed(failedPath, episode, force: true);
        if (failed.Status != NfoWriteStatus.WriteFailed || failed.Error is null)
            throw new InvalidOperationException($"{failedPath}: write failure was not reported");
        Console.WriteLine("OK content-check");
    }

    private static void AssertValid(string path, string rootElement, string title, string dateElement, string expectedDate, string anidbId, string tmdbId)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidOperationException($"{path}: missing root element");
        if (root.Name.LocalName != rootElement)
            throw new InvalidOperationException($"{path}: expected <{rootElement}>, got <{root.Name.LocalName}>");
        if (root.Element("title")?.Value != title)
            throw new InvalidOperationException($"{path}: title mismatch");
        if (root.Element(dateElement)?.Value != expectedDate)
            throw new InvalidOperationException($"{path}: {dateElement} mismatch");
        if (!File.ReadAllText(path).Contains(OwnershipMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"{path}: ownership marker missing");
        var uniqueId = root.Elements("uniqueid").FirstOrDefault(el => (string?)el.Attribute("type") == "anidb");
        if (uniqueId?.Value != anidbId)
            throw new InvalidOperationException($"{path}: anidb uniqueid mismatch");
        var tmdbIdElement = root.Elements("uniqueid").FirstOrDefault(el => (string?)el.Attribute("type") == "tmdb");
        if (tmdbIdElement?.Value != tmdbId || (string?)tmdbIdElement.Attribute("default") != "true")
            throw new InvalidOperationException($"{path}: tmdb uniqueid mismatch");
        Console.WriteLine($"OK {Path.GetFileName(path)}");
    }
}
