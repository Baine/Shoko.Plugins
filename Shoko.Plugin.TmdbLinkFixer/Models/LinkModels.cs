using System.Text.Json.Serialization;

namespace Shoko.Plugin.TmdbLinkFixer.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TmdbMediaKind
{
    Movie,
    Show,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LinkHealth
{
    NotChecked,
    Checking,
    Valid,
    Redirected,
    Invalid,
    Error,
}

public sealed record EpisodeOption(
    int AnidbEpisodeId,
    string Label,
    bool IsRegular,
    int Number,
    IReadOnlyList<int> CurrentTmdbEpisodeIds);

public sealed record TmdbEpisodeOption(
    int TmdbEpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    DateOnly? AirDate,
    string? StillUrl);

public sealed record ShowMappingOptions(
    int TmdbShowId,
    string ShowTitle,
    IReadOnlyList<TmdbEpisodeOption> Episodes);

public sealed record EpisodeMappingRequest(int AnidbEpisodeId, int TmdbEpisodeId);

public sealed record TmdbLinkItem(
    string Key,
    int ShokoSeriesId,
    int AnidbAnimeId,
    int? AnidbEpisodeId,
    IReadOnlyList<int> SourceAnidbEpisodeIds,
    string SeriesTitle,
    string? EpisodeTitle,
    string? EpisodeLabel,
    string AnidbUrl,
    string? AnidbPosterUrl,
    TmdbMediaKind Kind,
    int TmdbId,
    string TmdbUrl,
    string? OldPosterUrl,
    LinkHealth Health,
    string? Message,
    TmdbMediaKind? RedirectKind,
    int? RedirectId,
    string? RedirectPosterUrl,
    DateTimeOffset? CheckedAt,
    IReadOnlyList<EpisodeOption> Episodes);

public sealed record ScanState(
    bool Running,
    int Total,
    int Completed,
    int Valid,
    int Problems,
    int Errors,
    int Cached,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record SearchResult(
    TmdbMediaKind Kind,
    int Id,
    string Title,
    string OriginalTitle,
    DateOnly? Date,
    string? PosterUrl,
    string Overview,
    double Rating,
    string TmdbUrl,
    int? AnidbEpisodeId = null,
    string? MatchReason = null);

public sealed class AcceptLinkRequest
{
    public required string Key { get; init; }
    public required TmdbMediaKind TargetKind { get; init; }
    public int TargetId { get; init; }
    public int? AnidbEpisodeId { get; init; }
    public IReadOnlyList<EpisodeMappingRequest> EpisodeMappings { get; init; } = [];
    public bool Confirmed { get; init; }
}

public sealed record OperationResult(bool Success, string Message);
