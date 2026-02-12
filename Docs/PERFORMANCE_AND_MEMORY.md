Performance & Memory Rules
Non-negotiable

No UI thread blocking for imports, indexing, or long searches.

Use async I/O for file operations.

Stream large files; do not load entire UFDR/ZIP/HTML blobs into memory unless required.

Dispose all IDisposable resources deterministically:

streams, readers, ZipArchive, DbContext, file handles

Support CancellationToken for long operations:

ingest, indexing, AI calls, rebuild/reindex

Parser stability

Schema tolerant: unknown fields are skipped with structured logs.

One corrupted artifact must not crash the entire ingest job.

WPF leak prevention

Avoid event-handler leaks where publisher outlives subscriber:

unsubscribe explicitly OR use weak event patterns when appropriate

Prefer binding and commands over manual event subscriptions.

Reference: Microsoft WPF weak event patterns

https://learn.microsoft.com/en-us/dotnet/desktop/wpf/events/weak-event-patterns

Logging guidance

Use structured logs.

Include a correlation ID per ingest run.

Log “skipped” reasons for unknown nodes/fields.
