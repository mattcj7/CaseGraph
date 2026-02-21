using CaseGraph.Core.Diagnostics;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;

namespace CaseGraph.Core.Tests;

[Collection("AppFileLoggerEnvironment")]
public sealed class ObservabilityTests
{
    [Fact]
    public async Task SafeAsyncActionRunner_OnException_LogsFailureAndDoesNotThrow()
    {
        var logsRoot = CreateTempDirectory();
        try
        {
            using var logOverride = new LogDirectoryOverrideScope(logsRoot);
            var runner = new SafeAsyncActionRunner();
            var actionName = $"OpenCase-{Guid.NewGuid():N}";

            var exception = await Record.ExceptionAsync(async () =>
            {
                var result = await runner.ExecuteAsync(
                    actionName,
                    _ => throw new InvalidOperationException("boom"),
                    CancellationToken.None
                );

                Assert.False(result.Succeeded);
                Assert.False(result.Canceled);
                Assert.NotNull(result.Exception);
                Assert.False(string.IsNullOrWhiteSpace(result.CorrelationId));
            });

            Assert.Null(exception);

            var events = ReadStructuredEvents(AppFileLogger.GetCurrentLogPath())
                .Where(e => TryGetString(e, "actionName") == actionName)
                .ToList();

            Assert.Contains(events, e => TryGetString(e, "eventName") == "UiActionStarted");
            var failedEvent = events.Single(e => TryGetString(e, "eventName") == "UiActionFailed");
            Assert.False(string.IsNullOrWhiteSpace(TryGetString(failedEvent, "correlationId")));
            Assert.Contains("InvalidOperationException", TryGetString(failedEvent, "exception"));
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
        }
    }

    [Fact]
    public async Task UiExceptionReporterContextBuild_FromBackgroundThread_DoesNotThrow()
    {
        var state = new AppSessionState
        {
            CurrentCaseId = Guid.NewGuid(),
            CurrentEvidenceId = Guid.NewGuid()
        };
        var correlationId = Guid.NewGuid().ToString("N");

        var context = await Task.Run(() =>
            UiExceptionReportContextBuilder.Build(correlationId, state)
        );

        Assert.Equal(correlationId, context["correlationId"]?.ToString());
        Assert.Equal(state.CurrentCaseId?.ToString("D"), context["caseId"]?.ToString());
        Assert.Equal(state.CurrentEvidenceId?.ToString("D"), context["evidenceId"]?.ToString());
    }

    [Fact]
    public void UnobservedTaskExceptionHandler_DoesNotThrow_AndMarksObserved()
    {
        var logsRoot = CreateTempDirectory();
        try
        {
            using var logOverride = new LogDirectoryOverrideScope(logsRoot);
            var aggregate = new AggregateException(new InvalidOperationException("boom-unobserved"));
            var args = new UnobservedTaskExceptionEventArgs(aggregate);

            var exception = Record.Exception(() =>
            {
                UnobservedTaskExceptionContainment.Handle(
                    args,
                    "Unit test unobserved exception.",
                    scheduleNotification: _ => throw new InvalidOperationException("notify-failed")
                );
            });

            Assert.Null(exception);
            Assert.True(args.Observed);

            var events = ReadStructuredEvents(AppFileLogger.GetCurrentLogPath());
            Assert.Contains(
                events,
                e => TryGetString(e, "eventName") == "UnobservedTaskException"
                    && (TryGetString(e, "exception")?.Contains("boom-unobserved", StringComparison.Ordinal) ?? false)
            );
            Assert.Contains(
                events,
                e => TryGetString(e, "eventName") == "UnobservedTaskExceptionNotificationFailed"
            );
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
        }
    }

    [Fact]
    public void ForgetHelper_LogsExceptions()
    {
        var logsRoot = CreateTempDirectory();
        try
        {
            using var logOverride = new LogDirectoryOverrideScope(logsRoot);
            var correlationId = Guid.NewGuid().ToString("N");
            Task.FromException(new InvalidOperationException("forget-boom"))
                .Forget("UnitTestForget", correlationId);

            var events = ReadStructuredEvents(AppFileLogger.GetCurrentLogPath());
            var failure = events.FirstOrDefault(
                e => TryGetString(e, "eventName") == "FireAndForgetTaskFailed"
                    && TryGetString(e, "correlationId") == correlationId
            );

            Assert.True(failure.ValueKind != JsonValueKind.Undefined);
            Assert.Contains("forget-boom", TryGetString(failure, "exception") ?? string.Empty);
            Assert.Equal("UnitTestForget", TryGetString(failure, "actionName"));
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
        }
    }

    [Fact]
    public void ActionScope_InjectsCorrelationIdIntoStructuredEvents()
    {
        var logsRoot = CreateTempDirectory();
        try
        {
            using var logOverride = new LogDirectoryOverrideScope(logsRoot);
            var correlationId = Guid.NewGuid().ToString("N");
            var actionName = $"CreateCase-{Guid.NewGuid():N}";
            var marker = Guid.NewGuid().ToString("N");

            using (AppFileLogger.BeginActionScope(actionName, correlationId))
            {
                AppFileLogger.LogEvent(
                    eventName: "UnitTestActionScope",
                    level: "INFO",
                    message: marker
                );
            }

            var matchingEvent = ReadStructuredEvents(AppFileLogger.GetCurrentLogPath())
                .Single(e =>
                    TryGetString(e, "eventName") == "UnitTestActionScope"
                    && TryGetString(e, "message") == marker
                );

            Assert.Equal(correlationId, TryGetString(matchingEvent, "correlationId"));
            Assert.Equal(actionName, TryGetString(matchingEvent, "actionName"));
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
        }
    }

    [Fact]
    public async Task DebugBundleBuilder_IncludesRequiredEntries()
    {
        var tempRoot = CreateTempDirectory();
        var workspaceRoot = Path.Combine(tempRoot, "workspace");
        var logsRoot = Path.Combine(tempRoot, "logs");
        var dumpsRoot = Path.Combine(tempRoot, "dumps");
        var sessionRoot = Path.Combine(tempRoot, "session");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(logsRoot);
        Directory.CreateDirectory(dumpsRoot);
        Directory.CreateDirectory(sessionRoot);

        var workspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
        var outputZipPath = Path.Combine(tempRoot, "bundle.zip");
        var configPath = Path.Combine(tempRoot, "settings.json");
        await File.WriteAllTextAsync(Path.Combine(logsRoot, "app-20260219.log"), "{\"event\":\"x\"}");
        await File.WriteAllTextAsync(Path.Combine(dumpsRoot, "crash-001.dmp"), "dump");
        await File.WriteAllTextAsync(Path.Combine(sessionRoot, "session.jsonl"), "{\"event\":\"SessionStarted\"}");
        await CreateWorkspaceDbAsync(workspaceDbPath);
        await File.WriteAllTextAsync(configPath, "{}");

        try
        {
            var builder = new DebugBundleBuilder();
            await builder.BuildAsync(
                new DebugBundleBuildRequest(
                    OutputZipPath: outputZipPath,
                    LogsDirectory: logsRoot,
                    WorkspaceRoot: workspaceRoot,
                    WorkspaceDbPath: workspaceDbPath,
                    AppVersion: "1.2.3",
                    GitCommit: "deadbeef",
                    LastLogLines: ["line-1", "line-2"],
                    ConfigurationFiles: [configPath],
                    DumpsDirectory: dumpsRoot,
                    SessionDirectory: sessionRoot
                ),
                CancellationToken.None
            );

            Assert.True(File.Exists(outputZipPath));

            using var archive = ZipFile.OpenRead(outputZipPath);
            var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("workspace.snapshot.db", entries);
            Assert.Contains("diagnostics.json", entries);
            Assert.Contains("config/settings.json", entries);
            Assert.Contains(entries, entry => entry.StartsWith("logs/", StringComparison.Ordinal));
            Assert.Contains(entries, entry => entry.StartsWith("dumps/", StringComparison.Ordinal));
            Assert.Contains(entries, entry => entry.StartsWith("session/", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task DebugBundleBuilder_WhenWorkspaceDbConnectionOpen_SucceedsAndIncludesSnapshot()
    {
        var tempRoot = CreateTempDirectory();
        var workspaceRoot = Path.Combine(tempRoot, "workspace");
        var logsRoot = Path.Combine(tempRoot, "logs");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(logsRoot);

        var workspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
        var outputZipPath = Path.Combine(tempRoot, "bundle-open-connection.zip");
        await File.WriteAllTextAsync(Path.Combine(logsRoot, "app-20260219.log"), "{\"event\":\"x\"}");
        await CreateWorkspaceDbAsync(workspaceDbPath);

        try
        {
            await using var openConnection = new SqliteConnection($"Data Source={workspaceDbPath}");
            await openConnection.OpenAsync();
            await using (var touch = openConnection.CreateCommand())
            {
                touch.CommandText = "SELECT COUNT(*) FROM TestRecord;";
                await touch.ExecuteScalarAsync();
            }

            var builder = new DebugBundleBuilder();
            await builder.BuildAsync(
                new DebugBundleBuildRequest(
                    OutputZipPath: outputZipPath,
                    LogsDirectory: logsRoot,
                    WorkspaceRoot: workspaceRoot,
                    WorkspaceDbPath: workspaceDbPath,
                    AppVersion: "1.2.3",
                    GitCommit: "deadbeef",
                    LastLogLines: ["line-1", "line-2"],
                    ConfigurationFiles: Array.Empty<string>()
                ),
                CancellationToken.None
            );

            using var archive = ZipFile.OpenRead(outputZipPath);
            var snapshotEntry = archive.GetEntry("workspace.snapshot.db");
            Assert.NotNull(snapshotEntry);
            Assert.True(snapshotEntry.Length > 0);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task DebugBundleBuilder_WhenManyDumpsPresent_IncludesNewestFiveOnly()
    {
        var tempRoot = CreateTempDirectory();
        var workspaceRoot = Path.Combine(tempRoot, "workspace");
        var logsRoot = Path.Combine(tempRoot, "logs");
        var dumpsRoot = Path.Combine(tempRoot, "dumps");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(logsRoot);
        Directory.CreateDirectory(dumpsRoot);

        var workspaceDbPath = Path.Combine(workspaceRoot, "workspace.db");
        var outputZipPath = Path.Combine(tempRoot, "bundle-bounded-dumps.zip");
        await File.WriteAllTextAsync(Path.Combine(logsRoot, "app-20260220.log"), "{\"event\":\"x\"}");
        await CreateWorkspaceDbAsync(workspaceDbPath);

        try
        {
            var baseTime = DateTime.UtcNow.AddMinutes(-30);
            for (var i = 1; i <= 7; i++)
            {
                var dumpPath = Path.Combine(dumpsRoot, $"crash-{i:000}.dmp");
                await File.WriteAllTextAsync(dumpPath, $"dump-{i}");
                File.SetLastWriteTimeUtc(dumpPath, baseTime.AddMinutes(i));
            }

            var builder = new DebugBundleBuilder();
            await builder.BuildAsync(
                new DebugBundleBuildRequest(
                    OutputZipPath: outputZipPath,
                    LogsDirectory: logsRoot,
                    WorkspaceRoot: workspaceRoot,
                    WorkspaceDbPath: workspaceDbPath,
                    AppVersion: "1.2.3",
                    GitCommit: "deadbeef",
                    LastLogLines: ["line-1", "line-2"],
                    ConfigurationFiles: Array.Empty<string>(),
                    DumpsDirectory: dumpsRoot
                ),
                CancellationToken.None
            );

            using var archive = ZipFile.OpenRead(outputZipPath);
            var dumpEntries = archive.Entries
                .Select(entry => entry.FullName)
                .Where(name => name.StartsWith("dumps/", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(5, dumpEntries.Count);
            Assert.Equal(
                [
                    "dumps/crash-003.dmp",
                    "dumps/crash-004.dmp",
                    "dumps/crash-005.dmp",
                    "dumps/crash-006.dmp",
                    "dumps/crash-007.dmp"
                ],
                dumpEntries
            );
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.ToString();
    }

    private static IReadOnlyList<JsonElement> ReadStructuredEvents(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return Array.Empty<JsonElement>();
        }

        var events = new List<JsonElement>();
        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                events.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // Ignore legacy or malformed lines produced outside this test.
            }
        }

        return events;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.Core.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static async Task CreateWorkspaceDbAsync(string workspaceDbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={workspaceDbPath}");
        await connection.OpenAsync();

        await using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS TestRecord (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL
            );
            INSERT INTO TestRecord(Name) VALUES ('alpha');
            """;
        await create.ExecuteNonQueryAsync();
    }

    private sealed class LogDirectoryOverrideScope : IDisposable
    {
        private readonly string? _previous;

        public LogDirectoryOverrideScope(string logsDirectory)
        {
            _previous = Environment.GetEnvironmentVariable("CASEGRAPH_LOG_DIRECTORY");
            Environment.SetEnvironmentVariable("CASEGRAPH_LOG_DIRECTORY", logsDirectory);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CASEGRAPH_LOG_DIRECTORY", _previous);
        }
    }
}

[CollectionDefinition("AppFileLoggerEnvironment", DisableParallelization = true)]
public sealed class AppFileLoggerEnvironmentCollectionDefinition
{
}
