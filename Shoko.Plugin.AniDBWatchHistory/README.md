# AniDB Watch History Import — Shoko Daily

Plugin for the current Shoko Daily/dev branch. It imports **only** XML `<file>` records with a valid `viewdate`.
`viewdate="-"`, empty dates and invalid dates are skipped and can never make an episode unwatched.

## Exact Shoko Daily mappings

- FID: `IVideoReleaseService.GetAllReleases(["AniDB"])`, parsed from `IReleaseInfo.ReleaseURI == "https://anidb.net/file/{fid}"`. In current Daily builds, `IReleaseInfo.ID` contains `anidb://{ED2K}+{fileSize}` and is therefore deliberately not treated as an FID.
- EID verification: `IReleaseInfo.CrossReferences[].ProviderIDs[CrossReferenceIDs.AniDB_Episode]`.
- Episode: `IMetadataService.GetShokoEpisodeByAnidbID(eid)`.
- User: the single Shoko user for which `IUser.IsAnidbUser` is enabled. This is the user connected to Shoko's configured AniDB login; no `UserId` can be supplied by the caller.
- Watched state: `IUserDataService.SetEpisodeWatchedStatus(episode, user, true, viewDate, ...)`.
- Optional missing-FID fallback: when explicitly enabled, a record whose historical FID is no longer present is matched by its globally unique AniDB EID through `IMetadataService.GetShokoEpisodeByAnidbID(eid)`.
- Save verification: after each non-dry-run update, the returned and freshly fetched `IEpisodeUserData.IsWatched` values must both be true before the record is counted as imported.

The endpoints require an authenticated Shoko administrator.

## Shoko WebUI

After the plugin is built, installed and Shoko is restarted, open:

`Settings → Plugins → AniDB Watch History Import → AniDB Watch History`

The embedded page uses the active Shoko WebUI session. Select the AniDB MyList XML, run **Analyze XML** first, inspect the result, and then use **Import watched status**. The import button stays disabled until a successful analysis of that exact file.
Uploads up to 512 MiB are accepted. The GUI supports both remembered WebUI sessions (`localStorage`) and tab-only sessions (`sessionStorage`).

Version 1.0.2 fixes streaming XML field traversal so adjacent fields such as `crc` and `viewdate` are both read instead of every second element being skipped.

Version 1.0.3 matches AniDB FIDs using `IReleaseInfo.ReleaseURI`; Shoko Daily uses `IReleaseInfo.ID` for the ED2K-and-size release identifier.

Version 1.0.4 replaces the browser `confirm()` dialog with an in-page confirmation panel because Shoko embeds plugin pages in an iframe where browser modals are sandboxed.

Version 1.0.5 adds an explicit EID fallback for historical FIDs that are not present in Shoko and verifies every persisted watched status before counting it as imported. Exact FID matching remains the default.

Version 1.0.6 adds complete Shoko package identity metadata to release builds so the plugin manager can match an installed DLL to its manifest release.

## Build

Requirements: the .NET SDK used by the checked-out Shoko Daily source (currently .NET 10).
Run the build from the repository root:

```bash
./Shoko.Plugin.AniDBWatchHistory/build.sh /absolute/path/to/ShokoServer
```

Copy `Shoko.Plugin.AniDBWatchHistory/bin/Release/net10.0/Shoko.Plugin.AniDBWatchHistory.dll` to a separate directory below Shoko's user plugin directory, then restart Shoko.
Do not copy `Shoko.Abstractions.dll`; the server supplies it.

## API

Show the Shoko user connected to the AniDB login:

```http
GET /api/plugin/anidb-watch-history/anidb-user
```

Dry-run:

```bash
curl -X POST 'http://SHOKO:8111/api/plugin/anidb-watch-history/analyze' \
  -H 'apikey: YOUR_API_KEY' \
  -F 'XmlFile=@mylist.xml;type=application/xml' \
  -F 'VerifyEpisodeId=true' \
  -F 'AllowEpisodeIdFallback=false'
```

Real import (same body):

```http
POST /api/plugin/anidb-watch-history/import
```

Always run `analyze` first. Import is additive only: it does not contain an unwatched operation.
If Shoko has no user with the AniDB login option enabled, the endpoint returns HTTP `409 Conflict` and changes nothing.

## Compatibility pin

The source was verified against ShokoServer commit `1d1f6d57420d035d2c1b70936fccaff35eb6dab7`.
Daily can introduce breaking abstraction changes; rebuild the plugin against the exact Shoko Daily checkout you run.
