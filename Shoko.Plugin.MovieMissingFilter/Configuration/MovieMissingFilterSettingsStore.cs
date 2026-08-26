using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shoko.Plugin.MovieMissingFilter.Configuration;

/// <summary>
/// Small persistent settings store. The settings are deliberately kept outside
/// Shoko's database, so removing the plugin immediately restores stock behavior.
/// </summary>
internal static class MovieMissingFilterSettingsStore
{
    private const string SettingsFileName = "MovieMissingFilter.settings.json";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static MovieMissingFilterSettings _current = new();
    private static string? _settingsPath;
    private static ILogger? _logger;

    internal static MovieMissingFilterSettings Current
    {
        get
        {
            lock (Sync)
                return _current.Clone();
        }
    }

    internal static string SettingsPath
    {
        get
        {
            lock (Sync)
                return _settingsPath ?? SettingsFileName;
        }
    }

    internal static void Initialize(object applicationPaths, ILogger logger)
    {
        lock (Sync)
        {
            _logger = logger;
            _settingsPath = ResolveSettingsPath(applicationPaths);
            LoadLocked();
        }
    }

    internal static MovieMissingFilterSettings Update(MovieMissingFilterSettings settings)
    {
        lock (Sync)
        {
            _current = settings.Clone();
            SaveLocked();
            _logger?.LogInformation(
                "[MovieMissingFilter] Settings updated: Normal(E)={Normal}, Specials(S)={Specials}, Other(O)={Others}.",
                _current.IncludeNormalEpisodes,
                _current.IncludeSpecials,
                _current.IncludeOthers);
            return _current.Clone();
        }
    }

    private static void LoadLocked()
    {
        try
        {
            var path = _settingsPath!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (!File.Exists(path))
            {
                _current = new MovieMissingFilterSettings();
                SaveLocked();
            }
            else
            {
                var json = File.ReadAllText(path);
                _current = JsonSerializer.Deserialize<MovieMissingFilterSettings>(json, JsonOptions)
                    ?? new MovieMissingFilterSettings();
            }

            _logger?.LogInformation(
                "[MovieMissingFilter] Settings loaded from {SettingsPath}: Normal(E)={Normal}, Specials(S)={Specials}, Other(O)={Others}.",
                path,
                _current.IncludeNormalEpisodes,
                _current.IncludeSpecials,
                _current.IncludeOthers);
        }
        catch (Exception ex)
        {
            _current = new MovieMissingFilterSettings();
            _logger?.LogWarning(
                ex,
                "[MovieMissingFilter] Settings could not be loaded. Using defaults Normal(E)=true, Specials(S)=true, Other(O)=true.");
        }
    }

    private static void SaveLocked()
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);

            var temp = _settingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_current, JsonOptions));
            File.Move(temp, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MovieMissingFilter] Settings could not be saved to {SettingsPath}.", _settingsPath);
        }
    }

    private static string ResolveSettingsPath(object applicationPaths)
    {
        // Daily/dev has changed path abstractions before. Resolve common writable
        // configuration roots by reflection instead of binding to one concrete
        // Shoko.Server implementation.
        var candidates = new[]
        {
            "PluginConfigurationsPath",
            "ConfigurationPath",
            "DataPath",
            "ProgramDataPath",
            "ApplicationDataPath",
        };

        foreach (var propertyName in candidates)
        {
            try
            {
                var value = applicationPaths.GetType().GetProperty(propertyName)?.GetValue(applicationPaths) as string;
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                // Most Shoko/Jellyfin path properties are directories. If a daily
                // exposes a concrete configuration file path instead, use its
                // containing directory rather than creating a directory below a file.
                var root = value;
                if (File.Exists(value) || (!Directory.Exists(value) && Path.HasExtension(value)))
                    root = Path.GetDirectoryName(value) ?? value;

                return Path.Combine(root, "MovieMissingFilter", SettingsFileName);
            }
            catch
            {
                // Try the next known path property.
            }
        }

        // Fallback: keep the file beside the plugin assembly. This is normally
        // writable for manually installed Shoko plugins.
        var assemblyDirectory = Path.GetDirectoryName(typeof(MovieMissingFilterSettingsStore).Assembly.Location)
            ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDirectory, SettingsFileName);
    }
}
