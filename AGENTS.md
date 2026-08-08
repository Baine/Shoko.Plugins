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
- **Triggers**: `Controllers/NfoGeneratorController.cs` — `POST /api/plugin/NfoGenerator/{series|episode|folder|library}`, `[Authorize(Policy = "admin")]`.
- **WebUI page**: `NfoGeneratorPlugin.GetPages()` exposes a "Settings" page at
  `GET /api/plugin/NfoGenerator/settings` (anonymous, scaffolding only). The WebUI
  embeds it as an iframe under Settings → Plugins. Its JS reads the user's apikey
  from the WebUI's `sessionStorage['state']` / `localStorage['apiSession']` and
  calls Shoko's admin-protected v3 `Configuration/{id}` GET/PUT plus the library
  trigger. Do not put secrets in the page; it is served unauthenticated.
- **Events**: `NfoGeneratorService` subscribes `ReleaseSaved` (gated by
  `GenerateOnImport`), `SeriesUpdated` (gated by `GenerateOnMetadataUpdate`,
  runs with `force: true`), `ReleaseDeleted` (deletes per-file episode NFO).
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
- **Manifest maintenance is automated**: `.github/workflows/build-release.yml`
  prepends a release entry (version, source revision, channel Dev/Stable,
  release notes, archive URLs + SHA-256, `Shoko.Abstractions` version) to
  `manifest.json` and commits it back to `main`. Do not hand-edit the `releases`
  array; bump versions by creating a GitHub release (tag `v<X.Y.Z>`).
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
- **Folder-level artifacts are stale-prone**: on delete only the per-file
  `episode.nfo` is removed; `tvshow.nfo` / `movie.nfo` / sidecars may linger in a
  folder after its last file is removed. Marked `ponytail:` in code.
- **Self-check determinism**: `NfoWriter.SelfCheck` deletes/recreates its output
  dir first so a second run passes. Keep it that way.
- Local reference clones used for API research live outside the repo:
  `C:\Users\Paul\AppData\Local\Temp\opencode\shoko-server` (interfaces,
  PluginManager, ConfigurationService) and `...\ShokoRelay` (manifest/workflow
  patterns, TextHelper).

## Release procedure

1. `gh release create v<X.Y.Z> --title "v<X.Y.Z>" --notes-file notes.md` (omit
   `--prerelease` for Stable channel, include it for Dev).
2. The workflow builds the 3 runtimes, uploads zips, and updates `manifest.json`.
3. `git pull --ff-only` locally afterwards to pick up the manifest commit.
