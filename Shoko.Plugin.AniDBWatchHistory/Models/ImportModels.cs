namespace Shoko.Plugin.AniDBWatchHistory.Models;

public sealed class ImportResult
{
    public bool DryRun { get; init; }
    public int UserId { get; init; }
    public string UserName { get; init; } = "";
    public int TotalXmlRecords { get; set; }
    public int SkippedNoViewDate { get; set; }
    public int SkippedInvalidViewDate { get; set; }
    public int DuplicateRecords { get; set; }
    public int EligibleRecords { get; set; }
    public int FidNotFound { get; set; }
    public int EpisodeFallbackMatches { get; set; }
    public int EpisodeMismatch { get; set; }
    public int EpisodeNotFound { get; set; }
    public int AlreadyWatched { get; set; }
    public int WouldImport { get; set; }
    public int Imported { get; set; }
    public int SaveVerificationFailed { get; set; }
    public int Errors { get; set; }
    public List<ImportIssue> Issues { get; } = [];
}

public sealed record ImportIssue(int? FileId, int? EpisodeId, string Code, string Message);

public sealed record ShokoUserDto(int Id, string Username, bool IsAdmin);
