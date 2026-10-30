namespace Shoko.Plugin.NfoGenerator.Config;

/// <summary>
/// Matches media paths against <see cref="NfoGeneratorSettings.ExcludedImportFolders"/>,
/// a comma-separated list of absolute import-folder paths chosen from Shoko's
/// configured import folders.
/// </summary>
public static class ImportFolderExclusions
{
    /// <summary>True when <paramref name="path"/> lies in one of the excluded import folders.</summary>
    public static bool IsExcluded(string path, string? exclusions)
    {
        if (string.IsNullOrWhiteSpace(exclusions))
            return false;
        var full = Path.GetFullPath(path);
        foreach (var token in exclusions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Path.IsPathRooted(token) && IsWithin(full, token))
                return true;
        }
        return false;
    }

    private static bool IsWithin(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), path);
            return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Invalid user-supplied root; ignore it instead of failing generation.
            return false;
        }
    }
}
