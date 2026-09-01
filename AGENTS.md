# AGENTS.md

## Scope

- This is an umbrella directory, not a solution: `Shoko.Plugin.AniDBWatchHistory/`, `Shoko.Plugin.MovieMissingFilter/`, `Shoko.Plugin.NfoGenerator/`, and `Shoko.Plugin.TmdbLinkFixer/` are independent .NET 10 Shoko plugins. Build and verify only the plugin you change; there is no repository-wide build or test command.
- Plugin entrypoints are `Shoko.Plugin.AniDBWatchHistory/Plugin.cs`, `Shoko.Plugin.MovieMissingFilter/MovieMissingFilterPlugin.cs`, `Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator/NfoGeneratorPlugin.cs`, and `Shoko.Plugin.TmdbLinkFixer/Plugin.cs`; service/startup registrations live alongside them.
- `Shoko.Plugin.NfoGenerator/AGENTS.md` is authoritative additional guidance for that plugin, including its release workflow and NFO invariants.

## Focused build and verification

Run from this directory:

```bash
# AniDB Watch History: requires a ShokoServer source checkout.
./Shoko.Plugin.AniDBWatchHistory/build.sh /absolute/path/to/ShokoServer

# Movie Missing Filter: cleans and recreates its deployable publish output.
./Shoko.Plugin.MovieMissingFilter/build.sh

# TMDB Link Fixer: validation plus explicitly confirmed replacement workflow.
./Shoko.Plugin.TmdbLinkFixer/build.sh

# NFO Generator: run the self-check after resolver, writer, serialization, or comparison changes.
dotnet build Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator.csproj -c Release
dotnet run --project Shoko.Plugin.NfoGenerator/tools/NfoGenerator.SelfCheck/NfoGenerator.SelfCheck.csproj -c Release
```

- `Shoko.Plugin.AniDBWatchHistory` defaults `ShokoServerRoot` to `../ShokoServer`; use `-p:ShokoServerRoot=...` (or the build script) for another checkout. Rebuild it against the exact Shoko Daily version it will run with.
- `Shoko.Plugin.MovieMissingFilter/build.sh` produces the installable contents in `dist/`; use `build-docker.sh` when a local .NET 10 SDK is unavailable.
- The root `manifest.json` is an aggregate array of `{id, type: "manifest", url}` refs pointing to each plugin's manifest in `manifests/`. Each release workflow updates only its own `manifests/<plugin>.json`; do not hand-edit any manifest `releases` array.

## Shoko integration constraints

- Shoko supplies `Shoko.Abstractions` at runtime. Do not copy it with AniDB Watch History, and preserve `ExcludeAssets="runtime"` on the other plugins' server-owned package references.
- All plugin projects use the official `Shoko.BuildTools.Targets` package to stamp source revision, release date, runtime identifier, and release channel into published DLLs. `Directory.Build.targets` supplies the repository's prefixed release tag and prevents the package from treating the root aggregate manifest as a single-plugin manifest. Preserve this metadata so Shoko can match installed plugins to manifest releases.
- Movie Missing Filter's Harmony targets are reflection-based against Shoko Daily internals. Keep `Patching/PatchBootstrap.cs` fail-safe: unavailable or incompatible targets must leave Shoko's native behavior intact.
- Release workflows publish ZIPs for `linux-arm64`, `linux-x64`, and `win-x64`, then update only their matching `manifests/<plugin>.json`. Do not hand-edit any manifest `releases` array.
