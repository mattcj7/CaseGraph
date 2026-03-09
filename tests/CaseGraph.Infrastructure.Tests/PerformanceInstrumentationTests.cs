using CaseGraph.Core.Diagnostics;
using CaseGraph.Infrastructure.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CaseGraph.Infrastructure.Tests;

[Collection("PerformanceInstrumentationLogEnvironment")]
public sealed class PerformanceInstrumentationTests
{
    [Fact]
    public async Task TrackAsync_CompletedOperation_LogsDeterministicElapsedTime()
    {
        var logsRoot = CreateTempDirectory();

        try
        {
            using var logOverride = new LogDirectoryOverrideScope(logsRoot);
            var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));
            var instrumentation = new PerformanceInstrumentation(
                new PerformanceBudgetOptions(),
                timeProvider
            );

            await instrumentation.TrackAsync(
                new PerformanceOperationContext(
                    PerformanceOperationKinds.FeatureQuery,
                    "Query",
                    FeatureName: "Search",
                    CaseId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    CorrelationId: "corr-search-001"
                ),
                _ =>
                {
                    timeProvider.Advance(TimeSpan.FromMilliseconds(1234));
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

            var events = ReadStructuredEvents(AppFileLogger.GetCurrentLogPath());
            var started = events.Single(e => TryGetString(e, "eventName") == "PerformanceOperationStarted");
            var completed = events.Single(e =>
                TryGetString(e, "eventName") == "PerformanceOperationCompleted"
            );

            Assert.Equal("corr-search-001", TryGetString(started, "correlationId"));
            Assert.Equal("FeatureQuery", TryGetString(started, "operationKind"));
            Assert.Equal("Search", TryGetString(started, "feature"));
            Assert.Equal("1234", TryGetString(completed, "elapsedMs"));
            Assert.Equal("Succeeded", TryGetString(completed, "outcome"));
            Assert.Equal("False", TryGetString(completed, "slowPath"));
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
        }
    }

    [Fact]
    public async Task TrackAsync_WhenThresholdExceeded_LogsSlowPathWarning()
    {
        var logsRoot = CreateTempDirectory();

        try
        {
            using var logOverride = new LogDirectoryOverrideScope(logsRoot);
            var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));
            var budgets = new PerformanceBudgetOptions
            {
                FeatureQueryThresholdMs = 100
            };
            var instrumentation = new PerformanceInstrumentation(budgets, timeProvider);

            await instrumentation.TrackAsync(
                new PerformanceOperationContext(
                    PerformanceOperationKinds.FeatureQuery,
                    "Query",
                    FeatureName: "Search",
                    CorrelationId: "corr-search-slow-001"
                ),
                _ =>
                {
                    timeProvider.Advance(TimeSpan.FromMilliseconds(250));
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

            var events = ReadStructuredEvents(AppFileLogger.GetCurrentLogPath());
            var warning = events.Single(e =>
                TryGetString(e, "eventName") == "PerformanceSlowPathWarning"
            );

            Assert.Equal("Search", TryGetString(warning, "feature"));
            Assert.Equal("Query", TryGetString(warning, "operationName"));
            Assert.Equal("250", TryGetString(warning, "elapsedMs"));
            Assert.Equal("100", TryGetString(warning, "thresholdMs"));
            Assert.Equal("True", TryGetString(warning, "slowPath"));
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
        }
    }

    [Fact]
    public async Task TrackAsync_WritesOperationLabelsAndBudgetKey()
    {
        var logsRoot = CreateTempDirectory();

        try
        {
            using var logOverride = new LogDirectoryOverrideScope(logsRoot);
            var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));
            var instrumentation = new PerformanceInstrumentation(
                new PerformanceBudgetOptions(),
                timeProvider
            );
            var caseId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

            await instrumentation.TrackAsync(
                new PerformanceOperationContext(
                    PerformanceOperationKinds.FeatureReadiness,
                    "EnsureReady",
                    FeatureName: "Timeline",
                    CaseId: caseId,
                    Fields: new Dictionary<string, object?>
                    {
                        ["requiresIndex"] = false
                    }
                ),
                _ =>
                {
                    timeProvider.Advance(TimeSpan.FromMilliseconds(50));
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

            var completed = ReadStructuredEvents(AppFileLogger.GetCurrentLogPath())
                .Single(e => TryGetString(e, "eventName") == "PerformanceOperationCompleted");

            Assert.Equal("FeatureReadiness", TryGetString(completed, "operationKind"));
            Assert.Equal("EnsureReady", TryGetString(completed, "operationName"));
            Assert.Equal("Timeline", TryGetString(completed, "feature"));
            Assert.Equal(caseId.ToString("D"), TryGetString(completed, "caseId"));
            Assert.Equal("FeatureReadiness", TryGetString(completed, "budgetKey"));
            Assert.Equal("False", TryGetString(completed, "requiresIndex"));
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
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

            try
            {
                using var document = JsonDocument.Parse(line);
                events.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        }

        return events;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.PerformanceInstrumentationTests",
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
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private long _timestamp;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow = _utcNow.Add(elapsed);
            _timestamp += elapsed.Ticks;
        }
    }
}

[CollectionDefinition("PerformanceInstrumentationLogEnvironment", DisableParallelization = true)]
public sealed class PerformanceInstrumentationLogEnvironmentCollectionDefinition
{
}
