# NFO Generator Maintainer Wiki

| Page | Use it for |
| --- | --- |
| [Architecture](architecture.md) | Component boundaries, data flow, and sidecar ownership. |
| [Operations](operations.md) | Queue behavior, startup, library runs, diagnostics, and releases. |
| [Decisions](decisions.md) | Intentional trade-offs that should not be casually undone. |

## Fast orientation

The plugin subscribes to Shoko metadata/video events, queues durable work in QueueProcessor, and writes Kodi-compatible NFO/art sidecars next to media. The file on disk is the content cache: unchanged output is not rewritten. A full library run is checkpointed one series at a time so it can continue after a server restart.
