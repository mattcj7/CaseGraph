using System.Diagnostics;
using System.Text;

namespace CaseGraph.Infrastructure.Tests;

public sealed class AppSelfTestSmokeTests
{
    [Fact]
    public async Task App_SelfTest_ReturnsSuccess()
    {
        var solutionRoot = ResolveSolutionRoot();
        const string dotnetExecutable = "dotnet";
        var workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "CaseGraph.App.SelfTest",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var appExePath = Path.Combine(
                solutionRoot,
                "src",
                "CaseGraph.App",
                "bin",
                "Debug",
                "net8.0-windows",
                "CaseGraph.App.exe"
            );
            var buildResult = await RunProcessAsync(
                fileName: dotnetExecutable,
                workingDirectory: solutionRoot,
                arguments:
                [
                    "build",
                    "src/CaseGraph.App/CaseGraph.App.csproj",
                    "-c",
                    "Debug",
                    "--no-restore"
                ],
                environment:
                [
                    new KeyValuePair<string, string>("CASEGRAPH_WORKSPACE_ROOT", workspaceRoot)
                ],
                timeout: TimeSpan.FromMinutes(2)
            );
            if (buildResult.ExitCode != 0)
            {
                var failureDetails = new StringBuilder();
                failureDetails.AppendLine($"App build failed with exit code {buildResult.ExitCode}.");
                failureDetails.AppendLine("--- STDOUT ---");
                failureDetails.AppendLine(buildResult.Stdout);
                failureDetails.AppendLine("--- STDERR ---");
                failureDetails.AppendLine(buildResult.Stderr);
                Assert.Fail(failureDetails.ToString());
            }

            Assert.True(File.Exists(appExePath), $"App executable was not found at {appExePath}");

            var selfTestResult = await RunProcessAsync(
                fileName: appExePath,
                workingDirectory: solutionRoot,
                arguments: ["--self-test"],
                environment:
                [
                    new KeyValuePair<string, string>("CASEGRAPH_WORKSPACE_ROOT", workspaceRoot)
                ],
                timeout: TimeSpan.FromMinutes(2)
            );

            if (selfTestResult.ExitCode != 0)
            {
                var failureDetails = new StringBuilder();
                failureDetails.AppendLine(
                    $"Expected self-test exit code 0 but got {selfTestResult.ExitCode}."
                );
                failureDetails.AppendLine("--- STDOUT ---");
                failureDetails.AppendLine(selfTestResult.Stdout);
                failureDetails.AppendLine("--- STDERR ---");
                failureDetails.AppendLine(selfTestResult.Stderr);
                Assert.Fail(failureDetails.ToString());
            }
        }
        finally
        {
            TryDeleteDirectory(workspaceRoot);
        }
    }

    private static string ResolveSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CaseGraph.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate CaseGraph.sln from test runtime path.");
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

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
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        TimeSpan timeout
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment.Remove("DOTNET_HOST_PATH");
        startInfo.Environment.Remove("MSBUILD_EXE_PATH");
        startInfo.Environment.Remove("MSBuildSDKsPath");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new ProcessResult(
                ExitCode: -1,
                Stdout: await process.StandardOutput.ReadToEndAsync(),
                Stderr: "Process timed out."
            );
        }

        return new ProcessResult(
            process.ExitCode,
            await process.StandardOutput.ReadToEndAsync(),
            await process.StandardError.ReadToEndAsync()
        );
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
