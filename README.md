# NFO Generator

A [Shoko](https://shokoanime.com/) server plugin that generates [Kodi-style](https://kodi.wiki/view/NFO_files) NFO files and artwork sidecars next to your video files whenever Shoko matches them to metadata.

## Features

- Generates `movie.nfo` / `tvshow.nfo` / `episode.nfo` Kodi NFO files directly inside your import folders, next to the video file.
- Writes artwork sidecars (`thumb.jpg`, `poster.jpg`, `fanart.jpg`) from Shoko's cached images.
- Runs automatically when a release is matched on import (`Generate On Import`) and when series metadata is updated (`Generate On Metadata Update`).
- Configurable language fallback for titles and descriptions, including Shoko's preferred title and original-language sources.
- Content-aware writes: files that are already up to date are not rewritten, so media libraries are not rescanned needlessly. Metadata updates force a rewrite regardless.
- On-demand regeneration via plugin API triggers (per series, episode, import folder, or entire library).

## Requirements

- Shoko Server 4.8.0+ (daily build) on .NET 10
- Plugin target runtime must match your Shoko server: `linux-x64`, `linux-arm64`, or `win-x64`

## Installation

### WebUI (manifest)

1. Open Shoko's WebUI and navigate to `Settings > Plugin Management > Repositories`.
2. Click `Add Repository` and configure:
   - **Name:** `Baine Plugins`
   - **Manifest URL:** `https://raw.githubusercontent.com/Baine/shoko-nfo-generator/main/manifest.json`
3. Go to `Settings > Plugin Management > Browse`, find **NFO Generator**, and click `Install`.
4. Restart Shoko Server.

### Manual

1. Navigate to Shoko Server's `plugins` directory (create it if needed) and add a subfolder `NfoGenerator`.
2. Extract [the latest release](https://github.com/Baine/shoko-nfo-generator/releases) ZIP matching your server runtime into `plugins/NfoGenerator`.
3. Restart Shoko Server.

## Configuration

Settings are exposed in the WebUI under `Settings > Plugins > NFO Generator`:

| Setting | Default | Description |
| --- | --- | --- |
| `Title Language` | `shoko` | Priority, comma-separated list of languages for titles. Tokens: language codes (`de-DE`, `en-US`, `ja-JP`, `x-jat`), `shoko` for Shoko's preferred title, `original` for the source default. Falls back to the next token. |
| `Description Language` | `shoko` | Same as above, for descriptions/plots. |
| `Generate On Import` | `true` | Generate NFO files whenever a video file is matched to metadata. |
| `Generate On Metadata Update` | `true` | Regenerate NFO files when series metadata changes. Unchanged files are not rewritten. |

## API

The plugin exposes API endpoints to trigger generation on demand. All endpoints require admin credentials.

See [docs/API.md](docs/API.md) for the full reference.

## What gets generated

See [docs/NFO-FORMAT.md](docs/NFO-FORMAT.md) for the exact files and XML structure emitted.

## Development

Requires the .NET 10 SDK.

```bash
# Build
dotnet build Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator.csproj -c Release

# Run the self-check (unit tests for the language resolver and NFO writer)
dotnet run --project tools/NfoGenerator.SelfCheck/NfoGenerator.SelfCheck.csproj -c Release
```

### Releasing

The `.github/workflows/build-release.yml` workflow handles release builds and manifest maintenance:

1. Tag a release (e.g. `v0.1.0`) and publish it on GitHub (mark as pre-release for the `Dev` channel).
2. The workflow builds and attaches `NfoGenerator-<version>[_dev]_<runtime>.zip` for `linux-arm64`, `linux-x64`, and `win-x64`.
3. It then prepends a matching entry to `manifest.json` (version, source revision, release notes, archive URLs + SHA-256 checksums, and the `Shoko.Abstractions` version) and commits it back to `main`.

You can also run the workflow manually via **Actions → Build & Release → Run workflow** with a tag name.

## License

[GPL-3.0](LICENSE)
