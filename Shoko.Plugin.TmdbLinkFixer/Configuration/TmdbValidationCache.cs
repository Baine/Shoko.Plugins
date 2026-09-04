using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shoko.Plugin.TmdbLinkFixer.Models;
using Shoko.Plugin.TmdbLinkFixer.Services;

namespace Shoko.Plugin.TmdbLinkFixer.Configuration;

internal static class TmdbValidationCache
{
    private const string CacheFileName = "TmdbLinkFixer.validation-cache.json";
    private const int SaveBatchSize = 100;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(4);
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private static Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private static string? _cachePath;
    private static ILogger? _logger;
    private static int _dirtyEntries;

    public static void Initialize(object applicationPaths, ILogger logger)
    {
        lock (Sync)
        {
            _logger = logger;
            _cachePath = TmdbLinkFixerSettingsStore.ResolvePluginFilePath(applicationPaths, CacheFileName);
            LoadLocked();
        }
    }

    public static bool TryGet(TmdbMediaKind kind, int id, out ProbeResult result, out DateTimeOffset checkedAt)
    {
        lock (Sync)
        {
            var key = Key(kind, id);
            if (!_entries.TryGetValue(key, out var entry))
            {
                result = default!;
                checkedAt = default;
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (entry.CheckedAt < now - Lifetime || entry.CheckedAt > now + TimeSpan.FromMinutes(5))
            {
                _entries.Remove(key);
                _dirtyEntries++;
                result = default!;
                checkedAt = default;
                return false;
            }

            result = new(entry.Health, entry.Message, false);
            checkedAt = entry.CheckedAt;
            return true;
        }
    }

    public static void Store(TmdbMediaKind kind, int id, ProbeResult result)
    {
        if (result.Health is LinkHealth.Error or LinkHealth.Checking or LinkHealth.NotChecked)
            return;

        lock (Sync)
        {
            _entries[Key(kind, id)] = new(
                result.Health,
                result.Message,
                DateTimeOffset.UtcNow);
            _dirtyEntries++;
            if (_dirtyEntries >= SaveBatchSize)
                SaveLocked();
        }
    }

    public static void Flush()
    {
        lock (Sync)
        {
            if (_dirtyEntries > 0)
                SaveLocked();
        }
    }

    private static void LoadLocked()
    {
        _entries = new(StringComparer.Ordinal);
        _dirtyEntries = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath!)!);
            if (File.Exists(_cachePath))
            {
                var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(_cachePath), JsonOptions);
                if (document is { SchemaVersion: 2 } and { Entries: not null })
                    _entries = new(document.Entries, StringComparer.Ordinal);
            }

            var cutoff = DateTimeOffset.UtcNow - Lifetime;
            var expired = _entries.Where(x => x.Value.CheckedAt < cutoff).Select(x => x.Key).ToList();
            foreach (var key in expired)
                _entries.Remove(key);
            if (expired.Count > 0)
            {
                _dirtyEntries = expired.Count;
                SaveLocked();
            }

            _logger?.LogInformation("TMDB Link Fixer validation cache loaded from {Path}: {Count} valid entries", _cachePath, _entries.Count);
        }
        catch (Exception ex)
        {
            _entries = new(StringComparer.Ordinal);
            _dirtyEntries = 0;
            _logger?.LogWarning(ex, "TMDB Link Fixer validation cache could not be loaded; links will be checked normally.");
        }
    }

    private static void SaveLocked()
    {
        if (string.IsNullOrWhiteSpace(_cachePath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new CacheDocument { Entries = _entries }, JsonOptions));
            TmdbLinkFixerSettingsStore.TryRestrictPermissions(temp);
            File.Move(temp, _cachePath, overwrite: true);
            TmdbLinkFixerSettingsStore.TryRestrictPermissions(_cachePath);
            _dirtyEntries = 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "TMDB Link Fixer validation cache could not be saved to {Path}.", _cachePath);
        }
    }

    private static string Key(TmdbMediaKind kind, int id)
        => $"{(kind == TmdbMediaKind.Movie ? "movie" : "show")}:{id}";

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; init; } = 2;
        public Dictionary<string, CacheEntry> Entries { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record CacheEntry(
        LinkHealth Health,
        string? Message,
        DateTimeOffset CheckedAt);
}
