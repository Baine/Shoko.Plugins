# Architecture

## Runtime flow

```text
Shoko event or API trigger
  -> NfoGenerationJob (QueueProcessor, one concurrency group)
  -> NfoGeneratorService
  -> NfoWriter + artwork copy
  -> NFO/art sidecars beside managed media
```

`NfoGeneratorService` owns event subscriptions and generation/cleanup rules. `NfoGenerationJob` is the durable queue boundary: its key members define deduplication, and it waits for `ISystemService.WaitForStartupAsync()` before touching Shoko services. The controller only queues work; it does not generate in the HTTP request.

## Output ownership

- Every video file may receive its own `episode.nfo` (or movie NFO).
- A TV show's `tvshow.nfo` and artwork live at the resolved shared show root, never intentionally in a season directory.
- Folder-level output is written only when all direct live video files belong to one series. Mixed folders receive per-file sidecars only.
- Plugin cleanup identifies its own NFOs through embedded Shoko identity data; user-authored NFOs must be left untouched.
- Relocation/delete cleanup walks the old path toward its managed import root. Import-folder and library jobs additionally scan all descendants bottom-up and remove only directories whose entire contents match plugin-owned NFOs or known artwork output names; all configured import roots and folders with foreign content are preserved.

See [NFO format](../NFO-FORMAT.md) for emitted XML and filenames.

## Metadata rules

- TMDB determines movie versus TV when mappings exist. AniDB `Movie` is only a fallback.
- TMDB mappings provide provider IDs and episode numbering when available.
- Language selection follows configured BCP-47 tokens, then `shoko`, then `original`, then the first usable value.

## Content cache

Serialized NFO text is compared with the existing file before writing. This preserves modification time and prevents unnecessary downstream media-library scans. A metadata-update trigger deliberately uses `force: true`.
