using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;

namespace Shoko.Plugin.NfoGenerator.Nfo;

/// <summary>
/// Copies artwork from Shoko's local image cache into the media folder as
/// Kodi-style sidecar files.
/// </summary>
internal static class SidecarWriter
{
    private static readonly Dictionary<ImageEntityType, string> TypeToName = new()
    {
        [ImageEntityType.Primary] = "poster",
        [ImageEntityType.Backdrop] = "fanart",
        [ImageEntityType.Banner] = "banner",
        [ImageEntityType.Logo] = "logo",
        [ImageEntityType.Disc] = "disc",
    };

    /// <summary>
    /// Writes the folder-level art (poster, fanart, banner, logo, disc) for the
    /// entity and returns the Kodi art key -> filename mapping.
    /// </summary>
    public static Dictionary<string, string> WriteFolderArt(string folder, IWithImages entity)
        => WriteFolderArt(folder, entity, null);

    internal static Dictionary<string, string> WriteFolderArt(string folder, IWithImages entity, Action<string, string, Exception>? reportFailure)
    {
        var art = new Dictionary<string, string>();
        foreach (var (type, name) in TypeToName)
            if (WriteImage(folder, name, entity.GetBestImageForType(type), reportFailure) is { } filename)
                art[name] = filename;
        return art;
    }

    /// <summary>Writes a stable per-file thumb sidecar. Returns the filename or null.</summary>
    public static string? WriteThumb(string folder, IWithImages entity, int fileId)
        => WriteThumb(folder, entity, fileId, null);

    internal static string? WriteThumb(string folder, IWithImages entity, int fileId, Action<string, string, Exception>? reportFailure)
        => WriteImage(folder, $"thumb-{fileId}", entity.GetBestImageForType(ImageEntityType.Primary), reportFailure);

    private static string? WriteImage(string folder, string name, IImage? image, Action<string, string, Exception>? reportFailure)
    {
        if (image is null || string.IsNullOrEmpty(image.LocalPath))
            return null;

        var extension = Path.GetExtension(image.LocalPath);
        if (string.IsNullOrEmpty(extension))
            return null;

        var filename = name + extension;
        var target = Path.Combine(folder, filename);
        var sourceStatus = ValidateSource(image.LocalPath);
        if (sourceStatus.Status == SourceStatus.Missing)
            return null;
        if (sourceStatus.Status == SourceStatus.Failed)
        {
            reportFailure?.Invoke(image.LocalPath, target, sourceStatus.Error!);
            return null;
        }
        try
        {
            if (File.Exists(target))
                return filename;

            File.Copy(image.LocalPath, target, overwrite: false);
            return filename;
        }
        catch (FileNotFoundException ex)
        {
            if (ValidateSource(image.LocalPath).Status == SourceStatus.Missing)
                return null;
            reportFailure?.Invoke(image.LocalPath, target, ex);
            return null;
        }
        catch (DirectoryNotFoundException ex)
        {
            if (ValidateSource(image.LocalPath).Status == SourceStatus.Missing)
                return null;
            reportFailure?.Invoke(image.LocalPath, target, ex);
            return null;
        }
        catch (IOException ex)
        {
            if (File.Exists(target))
                return filename;
            reportFailure?.Invoke(image.LocalPath, target, ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            reportFailure?.Invoke(image.LocalPath, target, ex);
            return null;
        }
    }

    private static SourceValidation ValidateSource(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new(SourceStatus.Valid, null);
        }
        catch (FileNotFoundException)
        {
            return new(SourceStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(SourceStatus.Missing, null);
        }
        catch (IOException ex)
        {
            return new(SourceStatus.Failed, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(SourceStatus.Failed, ex);
        }
    }

    private enum SourceStatus
    {
        Valid,
        Missing,
        Failed,
    }

    private readonly record struct SourceValidation(SourceStatus Status, Exception? Error);
}
