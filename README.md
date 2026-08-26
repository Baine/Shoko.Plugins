# Shoko.Plugins

Independent .NET 10 plugins for Shoko Daily.

## Plugins

- [AniDB Watch History](Shoko.Plugin.AniDBWatchHistory/README.md) — imports watched dates from an AniDB MyList XML export.
- [Movie Missing Filter](Shoko.Plugin.MovieMissingFilter/README.md) — configures Missing Episodes visibility and movie-layout filtering.
- [NFO Generator](Shoko.Plugin.NfoGenerator/README.md) — generates Kodi-style NFO files and artwork sidecars.

## Credits

AniDB Watch History and Movie Missing Filter were created by AnimeNeko and are included here with their permission.

## Shoko manifests

Add only the aggregate manifest in Shoko; it contains all three plugins:

`https://raw.githubusercontent.com/Baine/Shoko.Plugins/main/manifest.json`

## Build and verify

Run these commands from the repository root:

```bash
# AniDB Watch History (requires a ShokoServer source checkout)
./Shoko.Plugin.AniDBWatchHistory/build.sh /absolute/path/to/ShokoServer

# Movie Missing Filter
./Shoko.Plugin.MovieMissingFilter/build.sh

# NFO Generator
dotnet build Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator.csproj -c Release
dotnet run --project Shoko.Plugin.NfoGenerator/tools/NfoGenerator.SelfCheck/NfoGenerator.SelfCheck.csproj -c Release
```

## Releases

Publish a GitHub release with the matching tag; GitHub builds the plugin after the release is published:

- `anidb-watch-history/vX.Y.Z` — `X.Y.Z` must match the AniDB Watch History csproj version.
- `movie-missing-filter/vX.Y.Z` — `X.Y.Z` must match the Movie Missing Filter csproj version.
- `nfo-generator/vX.Y.Z` — NFO Generator release tag.
