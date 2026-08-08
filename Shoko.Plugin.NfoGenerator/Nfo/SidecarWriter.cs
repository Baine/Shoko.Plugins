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
    {
        var art = new Dictionary<string, string>();
        foreach (var (type, name) in TypeToName)
            if (WriteImage(folder, name, entity.GetBestImageForType(type)) is { } filename)
                art[name] = filename;
        return art;
    }

    /// <summary>Writes thumb.jpg for an episode/movie entity. Returns the filename or null.</summary>
    public static string? WriteThumb(string folder, IWithImages entity)
        => WriteImage(folder, "thumb", entity.GetBestImageForType(ImageEntityType.Primary));

    private static string? WriteImage(string folder, string name, IImage? image)
    {
        if (image is null || string.IsNullOrEmpty(image.LocalPath) || !File.Exists(image.LocalPath))
            return null;

        var extension = Path.GetExtension(image.LocalPath);
        if (string.IsNullOrEmpty(extension))
            return null;

        var target = Path.Combine(folder, name + extension);
        try
        {
            File.Copy(image.LocalPath, target, overwrite: true);
            return Path.GetFileName(target);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
