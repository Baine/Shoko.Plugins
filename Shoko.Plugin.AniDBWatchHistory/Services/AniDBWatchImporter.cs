using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.User.Enums;
using Shoko.Abstractions.User.Services;
using Shoko.Abstractions.Video.Release;
using Shoko.Abstractions.Video.Services;
using Shoko.Plugin.AniDBWatchHistory.Models;

namespace Shoko.Plugin.AniDBWatchHistory.Services;

public sealed class AniDBWatchImporter(
    IVideoReleaseService releaseService,
    IMetadataService metadataService,
    IUserService userService,
    IUserDataService userDataService,
    AniDBMyListParser parser,
    ILogger<AniDBWatchImporter> logger)
{
    private const int MaxIssues = 2_000;

    public ShokoUserDto GetAniDBUser()
    {
        var users = userService.GetUsers().Where(u => u.IsAnidbUser).ToList();
        return users.Count switch
        {
            1 => new(users[0].ID, users[0].Username, users[0].IsAdmin),
            0 => throw new InvalidOperationException(
                "No Shoko user is linked to the AniDB login. Enable AniDB for a user in Shoko's user settings."),
            _ => throw new InvalidOperationException(
                "Multiple Shoko users are marked as AniDB user. Correct the user settings before importing.")
        };
    }

    public async Task<ImportResult> ImportAsync(
        Stream xml,
        bool dryRun,
        bool verifyEpisodeId,
        bool allowEpisodeIdFallback,
        CancellationToken cancellationToken)
    {
        var anidbUser = GetAniDBUser();
        var user = userService.GetUserByID(anidbUser.Id)
                   ?? throw new InvalidOperationException("The Shoko AniDB user disappeared during the import.");
        var parsed = await parser.ParseAsync(xml, cancellationToken).ConfigureAwait(false);
        var result = new ImportResult
        {
            DryRun = dryRun,
            UserId = user.ID,
            UserName = user.Username,
            TotalXmlRecords = parsed.Total,
            SkippedNoViewDate = parsed.NoDate,
            SkippedInvalidViewDate = parsed.InvalidDate
        };

        var releasesByFid = releaseService.GetAllReleases(["AniDB"])
            .Where(r => TryGetFid(r, out _))
            .GroupBy(r => GetFid(r))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var record in parsed.Records
                     .GroupBy(r => (r.FileId, r.EpisodeId))
                     .Select(g => g.OrderByDescending(r => r.ViewDate).First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.EligibleRecords++;
            var usedEpisodeIdFallback = false;
            if (!releasesByFid.TryGetValue(record.FileId, out var releases))
            {
                if (!allowEpisodeIdFallback)
                {
                    result.FidNotFound++;
                    AddIssue(result, record, "FidNotFound", $"AniDB FID {record.FileId} is not present in Shoko releases.");
                    continue;
                }

                usedEpisodeIdFallback = true;
            }
            else
            {
                var linkedEids = releases.SelectMany(r => r.CrossReferences)
                    .Select(x => x.ProviderIDs.TryGetValue(CrossReferenceIDs.AniDB_Episode, out var text) && int.TryParse(text, out var eid) ? eid : 0)
                    .Where(eid => eid > 0).Distinct().ToHashSet();
                if (verifyEpisodeId && !linkedEids.Contains(record.EpisodeId))
                {
                    result.EpisodeMismatch++;
                    AddIssue(result, record, "EpisodeMismatch",
                        $"FID {record.FileId} maps to EID(s) [{string.Join(", ", linkedEids)}], not XML EID {record.EpisodeId}.");
                    continue;
                }
            }

            var episode = metadataService.GetShokoEpisodeByAnidbID(record.EpisodeId);
            if (episode is null)
            {
                result.EpisodeNotFound++;
                AddIssue(result, record, usedEpisodeIdFallback ? "EpisodeFallbackNotFound" : "EpisodeNotFound",
                    usedEpisodeIdFallback
                        ? $"FID {record.FileId} is missing and AniDB EID {record.EpisodeId} has no Shoko episode for fallback matching."
                        : $"AniDB EID {record.EpisodeId} has no Shoko episode.");
                continue;
            }

            if (usedEpisodeIdFallback)
                result.EpisodeFallbackMatches++;

            if (userDataService.GetEpisodeUserData(episode, user).IsWatched)
            {
                result.AlreadyWatched++;
                continue;
            }

            if (dryRun)
            {
                result.WouldImport++;
                continue;
            }

            try
            {
                var savedUserData = await userDataService.SetEpisodeWatchedStatus(
                    episode, user, isWatched: true, lastPlayedAt: record.ViewDate,
                    videoReason: VideoUserDataSaveReason.None,
                    noVideoPropagation: false, updateStatsNow: true).ConfigureAwait(false);

                var persistedUserData = userDataService.GetEpisodeUserData(episode, user);
                if (!savedUserData.IsWatched || !persistedUserData.IsWatched)
                {
                    result.SaveVerificationFailed++;
                    result.Errors++;
                    logger.LogError(
                        "Shoko did not persist watched status for AniDB FID {Fid}, EID {Eid}, user {UserId}",
                        record.FileId, record.EpisodeId, user.ID);
                    AddIssue(result, record, "SaveVerificationFailed",
                        $"Shoko returned without an error, but EID {record.EpisodeId} is still unwatched for user {user.Username} (ID {user.ID}).");
                    continue;
                }

                result.Imported++;
            }
            catch (Exception ex)
            {
                result.Errors++;
                logger.LogError(ex, "Failed importing AniDB FID {Fid}, EID {Eid} for AniDB user {UserId}", record.FileId, record.EpisodeId, user.ID);
                AddIssue(result, record, "ImportError", ex.Message);
            }
        }

        result.DuplicateRecords = parsed.Records.Count - result.EligibleRecords;
        return result;
    }

    private static bool TryGetFid(IReleaseInfo release, out int fid)
    {
        fid = 0;
        var hasAniDBProvider = release.ProviderName
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name => name.Equals("AniDB", StringComparison.OrdinalIgnoreCase));
        if (!hasAniDBProvider || !Uri.TryCreate(release.ReleaseURI, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Equals("anidb.net", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith(".anidb.net", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2
               && segments[0].Equals("file", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(segments[1], out fid)
               && fid > 0;
    }

    private static int GetFid(IReleaseInfo release) => TryGetFid(release, out var fid) ? fid : 0;

    private static void AddIssue(ImportResult result, AniDBWatchRecord record, string code, string message)
    {
        if (result.Issues.Count < MaxIssues)
            result.Issues.Add(new(record.FileId, record.EpisodeId, code, message));
    }
}
