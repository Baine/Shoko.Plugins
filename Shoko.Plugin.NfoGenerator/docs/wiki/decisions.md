# Decisions and Constraints

## Keep Shoko's queue authoritative

The plugin adds jobs to QueueProcessor rather than maintaining its own worker, database table, or semaphore. QueueProcessor supplies persistence, scheduling, deduplication, and WebUI visibility.

## Checkpoint full-library work

Library work is split by series, not by individual file. This gives durable restart progress and useful WebUI status without producing thousands of queue items. Per-file jobs are unnecessary unless a measured recovery or fairness problem requires them.

## Disk output is the cache

Do not add a separate metadata cache for generated NFO content. Comparing the serialized document with the file already provides correct, durable invalidation and stable modification times.

## Cleanup is conservative

NFOs are removed only when their embedded ownership marker identifies the plugin. A generated-only directory sweep may also remove known artwork filenames, but only from managed-folder descendants whose complete direct contents are recognized plugin output and which have no child directories. Any foreign file, user-authored NFO, child directory, or unrelated empty directory blocks removal, and every managed import root (including nested roots) is protected.

## No rename responsibility

This plugin manages metadata and sidecars only. Shoko's relocation/renaming pipeline owns media filenames and directory layout.
