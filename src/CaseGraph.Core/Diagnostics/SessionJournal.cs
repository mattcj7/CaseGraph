using System.Text;
using System.Text.Json;

namespace CaseGraph.Core.Diagnostics;

public interface ISessionJournal
{
    bool PreviousSessionEndedUnexpectedly { get; }

    string CurrentSessionId { get; }

    string SessionDirectory { get; }

    string JournalPath { get; }

    SessionStartResult StartNewSession();

    void RecordStartupComplete();

    void WriteEvent(
        string eventType,
        IReadOnlyDictionary<string, object?>? fields = null,
        string? correlationId = null
    );

    void MarkCleanExit(string reason);
}

public sealed class SessionJournal : ISessionJournal
{
    private readonly object _sync = new();
    private readonly string _statePath;
    private readonly int _maxLines;

    private string? _currentSessionId;
    private bool _cleanExitMarked;

    public SessionJournal(
        string sessionDirectory,
        int maxLines = 500,
        string journalFileName = "session.jsonl",
        string stateFileName = "session.state.json"
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }

        SessionDirectory = sessionDirectory;
        JournalPath = Path.Combine(sessionDirectory, journalFileName);
        _statePath = Path.Combine(sessionDirectory, stateFileName);
        _maxLines = maxLines;
    }

    public bool PreviousSessionEndedUnexpectedly { get; private set; }

    public string CurrentSessionId => _currentSessionId ?? string.Empty;

    public string SessionDirectory { get; }

    public string JournalPath { get; }

    public SessionStartResult StartNewSession()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(SessionDirectory);

            var previousState = LoadState();
            PreviousSessionEndedUnexpectedly = previousState is not null && !previousState.CleanExit;

            _currentSessionId = Guid.NewGuid().ToString("N");
            _cleanExitMarked = false;

            PersistState(
                new SessionState(
                    SessionId: _currentSessionId,
                    CleanExit: false,
                    UpdatedAtUtc: DateTimeOffset.UtcNow.ToString("O")
                )
            );

            WriteEventInternal(
                eventType: "SessionStarted",
                fields: new Dictionary<string, object?>
                {
                    ["previousSessionEndedUnexpectedly"] = PreviousSessionEndedUnexpectedly,
                    ["previousSessionId"] = previousState?.SessionId
                },
                correlationId: AppFileLogger.NewCorrelationId()
            );

            return new SessionStartResult(
                _currentSessionId,
                PreviousSessionEndedUnexpectedly,
                JournalPath
            );
        }
    }

    public void RecordStartupComplete()
    {
        WriteEvent("StartupComplete");
    }

    public void WriteEvent(
        string eventType,
        IReadOnlyDictionary<string, object?>? fields = null,
        string? correlationId = null
    )
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_currentSessionId))
            {
                StartNewSession();
            }

            WriteEventInternal(eventType, fields, correlationId);
        }
    }

    public void MarkCleanExit(string reason)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_currentSessionId) || _cleanExitMarked)
            {
                return;
            }

            _cleanExitMarked = true;
            WriteEventInternal(
                eventType: "SessionCleanExit",
                fields: new Dictionary<string, object?>
                {
                    ["reason"] = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim()
                },
                correlationId: AppFileLogger.NewCorrelationId()
            );

            PersistState(
                new SessionState(
                    SessionId: _currentSessionId!,
                    CleanExit: true,
                    UpdatedAtUtc: DateTimeOffset.UtcNow.ToString("O")
                )
            );
        }
    }

    private void WriteEventInternal(
        string eventType,
        IReadOnlyDictionary<string, object?>? fields,
        string? correlationId
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_currentSessionId))
            {
                return;
            }

            var entry = new Dictionary<string, object?>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["sessionId"] = _currentSessionId,
                ["eventType"] = string.IsNullOrWhiteSpace(eventType) ? "Event" : eventType.Trim(),
                ["correlationId"] = string.IsNullOrWhiteSpace(correlationId)
                    ? AppFileLogger.NewCorrelationId()
                    : correlationId.Trim()
            };

            if (fields is not null)
            {
                foreach (var field in fields)
                {
                    entry[field.Key] = field.Value;
                }
            }

            var json = JsonSerializer.Serialize(entry);
            using (var stream = new FileStream(
                JournalPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.WriteThrough
            ))
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                );
                writer.WriteLine(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            TrimToMaxLines();
        }
        catch
        {
            // Journal write failures must never crash app flows.
        }
    }

    private void TrimToMaxLines()
    {
        if (!File.Exists(JournalPath))
        {
            return;
        }

        var lines = File.ReadAllLines(JournalPath, Encoding.UTF8);
        if (lines.Length <= _maxLines)
        {
            return;
        }

        var keep = lines.Skip(lines.Length - _maxLines).ToArray();
        var tempPath = $"{JournalPath}.tmp";
        File.WriteAllLines(tempPath, keep, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, JournalPath, overwrite: true);
    }

    private SessionState? LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }

            var raw = File.ReadAllText(_statePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SessionState>(raw);
        }
        catch
        {
            return null;
        }
    }

    private void PersistState(SessionState state)
    {
        try
        {
            var tempPath = $"{_statePath}.tmp";
            var payload = JsonSerializer.Serialize(state);
            File.WriteAllText(tempPath, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, _statePath, overwrite: true);
        }
        catch
        {
            // Best-effort state persistence.
        }
    }

    private sealed record SessionState(
        string SessionId,
        bool CleanExit,
        string UpdatedAtUtc
    );
}

public sealed record SessionStartResult(
    string SessionId,
    bool PreviousSessionEndedUnexpectedly,
    string JournalPath
);
