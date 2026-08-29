# Operations

## Queue and startup

All work runs through `Shoko.QueueProcessor` as `NfoGenerationJob`, marked long-running and assigned to the `NfoGenerator` concurrency group. This keeps event bursts and full-library work serialized without inventing a second queue.

QueueProcessor can resume persisted jobs before Shoko's repositories are ready after a restart. Jobs therefore await `ISystemService.WaitForStartupAsync()`; do not replace this with arbitrary delays or retry-on-null behavior.

## Library generation

1. Build an in-memory index of series, available files, show roots, and shared folder contents.
2. Process one series per queue job.
3. Schedule the next cursor with `RunAfterCurrent`.
4. After the final series, sweep stale plugin-owned sidecars and generated-only orphan directories.

The first job displays `Preparing library index (1/0, 0%)`. It changes to a real `x/y` value only after the index is complete. On a large active server, index construction can be CPU-heavy and competes with hashing/relocation work. The cursor survives a restart; the in-memory index is rebuilt before continuing.

## Diagnose a slow run

1. Confirm `Startup Complete` precedes NFO processing.
2. Look for `Building library NFO index`, then `Library NFO index built`, then `Processing series x/y` in the server log.
3. Check QueueProcessor backlog, CPU, memory, and disk I/O. A large hash or relocation queue can delay NFO work materially.

## Verify changes

```powershell
dotnet build Shoko.Plugin.NfoGenerator/Shoko.Plugin.NfoGenerator.csproj -c Release
dotnet run --project tools/NfoGenerator.SelfCheck/NfoGenerator.SelfCheck.csproj -c Release
```

## Release

Create a GitHub release/tag with a notes file. The release workflow publishes runtime archives and prepends the manifest entry; pull the resulting manifest commit afterwards. See `AGENTS.md` for the exact command and escaping rule.
