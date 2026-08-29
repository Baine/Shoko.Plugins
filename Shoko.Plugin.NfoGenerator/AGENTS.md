# AGENTS.md

Guidance for AI agents working on this repository.

## Project

`Shoko.Plugin.NfoGenerator` is a Shoko server plugin that writes Kodi-style NFO
files (`movie.nfo`, `tvshow.nfo`, `episode.nfo`) and artwork sidecars next to
video files. Plugin identity: GUID `5c5482c1-3dd0-49cb-b862-d57e305da353`.

- Target: `net10.0`, `Shoko.Abstractions` `6.0.0-alpha.77`, `Asp.Versioning.Mvc.ApiExplorer` `10.0.0` (both refs use `ExcludeAssets="runtime"`).
- The plugin ships the config POCO and API controllers; Shoko auto-registers
  `IConfiguration` types and plugin controllers (see observations).

## Build & verify

```bash
dotnet build Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator.csproj -c Release
dotnet run --project tools/NfoGenerator.SelfCheck/NfoGenerator.SelfCheck.csproj -c Release
```

The self-check covers the language resolver and NFO writer (incl. content-cache
behavior). It runs from `C:\Users\Paul\AppData\Local\Temp\nfo-generator-selfcheck`
and clears that dir first. Run it after touching the resolver, writer, or any
serialization/compare logic.

## Architecture (approach taken)

- **Config**: `Config/NfoGeneratorSettings.cs` is a public `IConfiguration` POCO
  (auto-surfaced in the WebUI). Read via `ConfigurationProvider<NfoGeneratorSettings>`
  in `NfoGeneratorServiceRegistration.cs`.
- **Triggers**: `Controllers/NfoGeneratorController.cs` — `POST /api/plugin/NfoGenerator/{series|episode|folder|library}`, `[Authorize(Policy = "admin")]`. The `folder` and `library` triggers regenerate content-stale NFOs, then run their scoped orphan sweeps. Full-library cleanup is a distinct `LibraryCleanup` queue phase with one persisted step per managed folder, so the final series no longer appears stuck at 100%.
- **WebUI page**: `NfoGeneratorPlugin.GetPages()` exposes a "Settings" page at
  `GET /api/plugin/NfoGenerator/settings` (anonymous, scaffolding only). The WebUI
  embeds it as an iframe under Settings → Plugins. Its JS reads the user's apikey
  from the WebUI's `sessionStorage['state']` / `localStorage['apiSession']` and
  calls Shoko's admin-protected v3 `Configuration/{id}` GET/PUT plus the library
  trigger. Do not put secrets in the page; it is served unauthenticated.
- **Events**: `NfoGeneratorService` subscribes `ReleaseSaved` (gated by
  `GenerateOnImport`), `SeriesUpdated` (gated by `GenerateOnMetadataUpdate`,
  runs with `force: true`), `ReleaseDeleted` (removes the per-file episode NFO
  and sweeps generated-only directories from the old path toward its import
  root), `VideoFileRelocated` (gated by `GenerateOnImport`: removes stale
  output and generated-only directories at the old path, then regenerates at
  the new path).
- **Language fallback**: `Config/LanguageResolver.cs` — comma-separated tokens:
  BCP-47 codes (case-insensitive), `shoko` (preferred), `original` (default).
  Falls back to preferred → original → first non-empty.
- **Movie vs TV decision**: TMDB decides, not AniDB. `NfoGeneratorService.IsMovie`:
  episode linked to a TMDB movie → movie; else series linked to a TMDB movie →
  movie (covers OVAs); else series linked to a TMDB show → TV; else fall back to
  AniDB `AnimeType.Movie`. Uses `IShokoEpisode.TmdbMovieCrossReferences` /
  `IShokoSeries.TmdbMovieCrossReferences` / `.TmdbShowCrossReferences`.
- **Content cache**: the NFO file on disk is the cache. Serialize first, compare
  with `File.ReadAllText`, write only when different (keeps mtime → no rescans).
  `force: true` bypasses.
- **Embedded identity metadata**: Shoko reads `AssemblyMetadata` attributes off
  the plugin DLL (`PackageID`, `PackageName`, `PackageOverview`,
  `RuntimeIdentifier`, `ReleaseChannel`, `ReleaseDate`, `SourceRevision`,
  `ReleaseTag`, `RepositoryUrl`). Without `PackageID` it warns
  "Plugin does not have embedded identity metadata". The csproj stamps the
  static identity via `<AssemblyMetadata>` items and git provenance via the
  `StampGitMetadata` target (mirrors ShokoRelay). **Gotcha**: the target must
  hook `BeforeTargets="GetAssemblyVersion;GenerateAssemblyInfo"` — hooking
  `CoreGenerateAssemblyInfo` is too late, the SDK has already captured
  `@(AssemblyMetadata)`. `RuntimeIdentifier` and `ReleaseChannel` are
  conditional on `$(RuntimeIdentifier)` / `$(RELEASE_CHANNEL)`, which the
  workflow's Publish step sets.

## Caveats & observations

- **UTF-8 declaration gotcha**: `XmlWriter.Create(StringBuilder, …)` stamps the
  declaration `encoding="utf-16"`. Always serialize via `MemoryStream` with
  `UTF8Encoding(false)`. See `NfoWriter.Write`.
- **`ExcludeAssets="runtime"`** on `Shoko.Abstractions` keeps the server's copy
  authoritative and keeps release zips small. The self-check project needs a
  direct `PackageReference` to `Shoko.Abstractions` so it runs standalone.
- **Release zips are `dotnet publish` output** (`--no-self-contained`), not the
  bare DLL. Named `NfoGenerator-v<ver>[_dev]_<rid>.zip` for `linux-arm64` /
  `linux-x64` / `win-x64`.
- **Manifest maintenance is automated**: root `.github/workflows/nfo-generator.yml`
  prepends a release entry (version, source revision, channel Dev/Stable,
  release notes, archive URLs + SHA-256, `Shoko.Abstractions` version) to
  the root `manifest.json` and commits it back to `main`. Do not hand-edit the `releases`
  array; bump versions by creating a GitHub release (tag `nfo-generator/v<X.Y.Z>`).
- **Release notes escaping (the last step's lesson)**: the workflow copies the
  GitHub release body verbatim into `manifest.json` → `release_notes`. Passing
  `--notes` with `\n`/`\t` through PowerShell double-quotes corrupts both the
  release body and the manifest (literal backslash sequences, stray tabs).
  Always pass release notes via `gh release edit/create --notes-file <file>`,
  never inline via shell quoting. If a body ever lands corrupted, fix the
  release body first, then repair `manifest.json`'s `release_notes` in a commit.
- **Multiple video files can map to one Shoko episode**; the writer is keyed by
  file ID (`DistinctBy(f => f.ID)`). A multi-episode file writes NFOs for its
  first episode only (ponytail: accepted limitation).
- **Staleness definition**: an NFO is *content-stale* when its bytes differ from
  what the plugin would serialize from current Shoko metadata — detected lazily
  at write time by `NfoWriter.Write` (rewrites only when the text differs;
  `force: true` always rewrites). An NFO is *orphan-stale* when its owning video
  file no longer exists at that path: the per-file `episode.nfo` is removed on
  release delete / relocation. Folder-level `tvshow.nfo` / `movie.nfo` files
  are removed when no live descendant video remains. There is no scheduled
  sweep; staleness is only acted on when a trigger fires. The scoped
  import-folder and library sweeps walk managed-folder descendants bottom-up
  and delete a directory only when it has at least one file, no child
  directories, and every file is an ownership-marked plugin NFO or a known
  artwork output name (`poster.*`, `fanart.*`, `banner.*`, `logo.*`, `disc.*`,
  `thumb.*`). Any foreign file or user NFO protects the directory; all managed
  import roots (including nested roots) and unrelated empty directories are
  retained.
- **Cleanup performance invariant**: full-library preparation builds live-NFO,
  descendant-video, direct-show, and managed-root indices once. Each managed
  folder is then enumerated once and processed bottom-up from that snapshot.
  Do not reintroduce `GetVideoFilesByAbsolutePath` per directory or recursive
  filesystem enumeration per TMDB show root; both caused the queue to sit on
  the final series at 100% for a long time on large libraries.
- **Shared-folder guard**: folder-level NFOs and art are only written when every
  live file directly in the folder belongs to the same series. Mixed-series
  folders get per-file `episode.nfo` only, so one series' metadata cannot
  clobber another series' `tvshow.nfo` / `movie.nfo` / posters (movies in mixed
  folders get no NFO at all). `IsFolderShared`/`FolderHasVideoFiles` use
  `IVideoService.GetVideoFilesByAbsolutePath`, filtering to direct children.
- **Self-check determinism**: `NfoWriter.SelfCheck` deletes/recreates its output
  dir first so a second run passes. Keep it that way.
- Local reference clones used for API research live outside the repo:
  `C:\Users\Paul\AppData\Local\Temp\opencode\shoko-server` (interfaces,
  PluginManager, ConfigurationService) and `...\ShokoRelay` (manifest/workflow
  patterns, TextHelper).

## Release procedure

1. `gh release create nfo-generator/v<X.Y.Z> --title "nfo-generator/v<X.Y.Z>"
   --notes-file notes.md` (omit `--prerelease` for Stable channel, include it for
   Dev).
2. The workflow builds the 3 runtimes, uploads zips, and updates `manifest.json`.
3. `git pull --ff-only` locally afterwards to pick up the manifest commit.
