# Debugging Guide

## Log Locations
- Default logs directory:
  - `%LOCALAPPDATA%\CaseGraphOffline\logs`
- Current active log file:
  - `app-YYYYMMDD.log` (JSON lines)
- Optional override for local testing:
  - `CASEGRAPH_LOG_DIRECTORY`

## Crash Diagnostics
- Global exception handlers capture:
  - `Application.DispatcherUnhandledException`
  - `TaskScheduler.UnobservedTaskException`
  - `AppDomain.CurrentDomain.UnhandledException`
- Fatal dialogs include:
  - `CorrelationId`
  - Log file path
  - Copyable diagnostics block

## Export Debug Bundle
1. Open **Diagnostics** page in the app.
2. Click **Export Debug Bundle**.
3. Choose a `.zip` output location.

Bundle contents include:
- Logs folder
- `workspace.db`
- `workspace.db-wal` (if present)
- `workspace.db-shm` (if present)
- Config files (if present)
- `diagnostics.json` with version/runtime/workspace/log-tail metadata

## What To Attach For Bug Reports
- CorrelationId shown in UI (if available)
- Debug bundle `.zip`
- Repro steps with exact case/evidence action
- Timestamp (UTC if possible)
