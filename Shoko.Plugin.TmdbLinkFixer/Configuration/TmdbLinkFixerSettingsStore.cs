using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shoko.Plugin.TmdbLinkFixer.Configuration;

public static class TmdbLinkFixerSettingsStore
{
    private const string SettingsFileName = "TmdbLinkFixer.settings.json";
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private static TmdbLinkFixerSettings _current = new();
    private static string? _settingsPath;
    private static ILogger? _logger;

    public static TmdbLinkFixerSettings Current
    {
        get { lock (Sync) return _current.Clone(); }
    }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Current.ApiCredential);

    public static void Initialize(object applicationPaths, ILogger logger)
    {
        lock (Sync)
        {
            _logger = logger;
            _settingsPath = ResolveSettingsPath(applicationPaths);
            LoadLocked();
        }
    }

    public static TmdbLinkFixerSettingsView GetView()
    {
        lock (Sync)
            return ViewLocked();
    }

    public static TmdbLinkFixerSettingsView Update(UpdateTmdbLinkFixerSettingsRequest request)
    {
        lock (Sync)
        {
            var credential = _current.ApiCredential;
            if (request.ClearApiCredential)
                credential = string.Empty;
            else if (!string.IsNullOrWhiteSpace(request.ApiCredential))
                credential = NormalizeCredential(request.ApiCredential);

            _current = new()
            {
                ApiCredential = credential,
                RequestsPerSecond = Math.Clamp(request.RequestsPerSecond, 1, 10),
            };
            SaveLocked();
            _logger?.LogInformation(
                "TMDB Link Fixer settings updated: credential configured={Configured}, type={Type}, requests/second={Rate}",
                !string.IsNullOrWhiteSpace(_current.ApiCredential), CredentialType(_current.ApiCredential), _current.RequestsPerSecond);
            return ViewLocked();
        }
    }

    internal static bool IsBearerToken(string credential)
        => credential.Contains('.') || credential.Length > 64;

    private static string NormalizeCredential(string credential)
    {
        credential = credential.Trim();
        return credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? credential[7..].Trim()
            : credential;
    }

    private static string CredentialType(string credential)
        => string.IsNullOrWhiteSpace(credential) ? "Not configured" : IsBearerToken(credential) ? "Read access token" : "API key";

    private static TmdbLinkFixerSettingsView ViewLocked()
        => new(
            !string.IsNullOrWhiteSpace(_current.ApiCredential),
            CredentialType(_current.ApiCredential),
            _current.RequestsPerSecond,
            _settingsPath ?? SettingsFileName);

    private static void LoadLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath!)!);
            if (File.Exists(_settingsPath))
            {
                _current = JsonSerializer.Deserialize<TmdbLinkFixerSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new();
                _current.RequestsPerSecond = Math.Clamp(_current.RequestsPerSecond, 1, 10);
                _current.ApiCredential = NormalizeCredential(_current.ApiCredential);
            }
            else
            {
                _current = new();
            }

            _logger?.LogInformation(
                "TMDB Link Fixer settings loaded from {Path}: credential configured={Configured}, requests/second={Rate}",
                _settingsPath, !string.IsNullOrWhiteSpace(_current.ApiCredential), _current.RequestsPerSecond);
        }
        catch (Exception ex)
        {
            _current = new();
            _logger?.LogWarning(ex, "TMDB Link Fixer settings could not be loaded; API validation is disabled until a credential is saved.");
        }
    }

    private static void SaveLocked()
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
            return;
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temp = _settingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_current, JsonOptions));
        TryRestrictPermissions(temp);
        File.Move(temp, _settingsPath, overwrite: true);
        TryRestrictPermissions(_settingsPath);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Could not restrict permissions on {Path}", path); }
    }

    private static string ResolveSettingsPath(object applicationPaths)
    {
        foreach (var propertyName in new[] { "PluginConfigurationsPath", "ConfigurationPath", "DataPath", "ProgramDataPath", "ApplicationDataPath" })
        {
            try
            {
                var value = applicationPaths.GetType().GetProperty(propertyName)?.GetValue(applicationPaths) as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var root = File.Exists(value) || (!Directory.Exists(value) && Path.HasExtension(value))
                    ? Path.GetDirectoryName(value) ?? value
                    : value;
                return Path.Combine(root, "TmdbLinkFixer", SettingsFileName);
            }
            catch { }
        }

        var assemblyDirectory = Path.GetDirectoryName(typeof(TmdbLinkFixerSettingsStore).Assembly.Location) ?? AppContext.BaseDirectory;
        return Path.Combine(assemblyDirectory, SettingsFileName);
    }
}
