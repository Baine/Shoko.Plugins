namespace Shoko.Plugin.AniDBWatchHistory.Models;

public sealed record AniDBWatchRecord(
    int FileId,
    int EpisodeId,
    int? AnimeId,
    string? AnimeTitle,
    string? Crc,
    DateTime ViewDate);
