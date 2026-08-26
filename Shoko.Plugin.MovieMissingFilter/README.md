# Shoko.Plugin.MovieMissingFilter v0.12.0

Runtime-only enhancement for Shoko Server daily/dev Missing Episodes.

## Features

- Suppresses redundant AniDB movie layouts:
  - `Complete Movie` owned -> missing `Part X of Y` alternatives are hidden.
  - A complete `Part X of Y` layout owned -> missing `Complete Movie` and other alternative layouts are hidden.
- Independently configurable Missing Episodes visibility for:
  - **Normal episodes (E)**
  - **Specials (S)**
  - **Other (O)**
- Adds aired, non-hidden S/O episodes without files when their setting is enabled.
- Keeps Dashboard `Missing Episodes` aligned with the configured result.
- Keeps the stock series-detail `Missing.Episodes` and `Missing.Specials` counters aligned with the configured result.
- Does **not** modify Shoko database rows, Hidden flags, or Shoko DLL files.

## Settings page

Shoko daily exposes plugin pages through `IPlugin.GetPages()`. After installation, open:

`Settings -> Plugins -> Movie Missing Filter -> Settings`

The page has three independent toggles:

| Setting | Default | Effect |
|---|---:|---|
| Normal episodes (E) | On | Include normal AniDB episodes. Movie alternative suppression applies only to E. |
| Specials (S) | On | Include aired, non-hidden Specials without a local file. |
| Other (O) | On | Include aired, non-hidden Other episodes without a local file. |

All combinations are supported:

- E only
- S only
- O only
- E + S
- E + O
- S + O
- E + S + O
- none (empty Missing Episodes result)

Changes are applied on the next API request. Refresh the Missing Episodes page and Dashboard after saving; a Shoko restart is not required.

The settings are persisted to `MovieMissingFilter.settings.json`. The plugin attempts to place this under Shoko's configuration/data path in a `MovieMissingFilter` directory; if the current daily changes that abstraction, it safely falls back to the plugin directory.

## Series detail limitation for Other (O)

The stock Shoko `SeriesSizes.Missing` API currently exposes only `Episodes` and `Specials`. It has no `Others` field. Therefore O episodes can be visible on the Missing Episodes page and counted by the Dashboard, but the stock anime-detail card cannot display a separate `Others missing` number.

## Collecting mode

Shoko's collecting query is release-group-specific and operates on normal episodes. S/O augmentation remains disabled for `collecting=true`. If Normal episodes (E) are disabled, normal collecting results are also hidden.

## Build

From the repository root with the .NET 10 SDK:

```bash
./Shoko.Plugin.MovieMissingFilter/build.sh
```

PowerShell:

```powershell
./Shoko.Plugin.MovieMissingFilter/build.ps1
```

With Docker:

```bash
./Shoko.Plugin.MovieMissingFilter/build-docker.sh
```

or:

```powershell
./Shoko.Plugin.MovieMissingFilter/build-docker.ps1
```

Copy the contents of `Shoko.Plugin.MovieMissingFilter/dist/` into the Shoko plugin directory and restart Shoko after replacing the plugin DLLs.

## Expected startup log

You should see the settings being loaded plus the runtime patch messages. Example:

```text
[MovieMissingFilter] Settings loaded from ...MovieMissingFilter.settings.json: Normal(E)=True, Specials(S)=True, Other(O)=True.
[MovieMissingFilter] Runtime ref-result patch applied to Shoko.Server.Repositories.Cached.AnimeEpisodeRepository.GetMissing ...
[MovieMissingFilter] Runtime dashboard patch applied ...
[MovieMissingFilter] Runtime series-detail patch applied ...
```

Saving settings produces:

```text
[MovieMissingFilter] Settings updated: Normal(E)=True, Specials(S)=False, Other(O)=True.
```

## Removal

Stop Shoko, remove the plugin, and start Shoko again. All Harmony patches disappear and Shoko immediately returns to stock Missing Episodes behavior. No database rollback is required.
