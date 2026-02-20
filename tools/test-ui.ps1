param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$NoRestore,
    [string[]]$Reason = @("UI tier triggered by WPF/resources/startup changes.")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Invoke-DotnetStep {
    param(
        [string]$StepName,
        [string[]]$Arguments
    )

    Write-Host "[$StepName] dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed for '$StepName' with exit code $LASTEXITCODE."
    }
}

Push-Location $repoRoot
try {
    Write-Host "=== Running UI tier ==="
    foreach ($item in $Reason) {
        Write-Host "WHY: $item"
    }

    if (-not $NoBuild) {
        $buildArgs = @("build", "CaseGraph.sln", "-c", $Configuration)
        if ($NoRestore) {
            $buildArgs += "--no-restore"
        }

        Invoke-DotnetStep -StepName "UI build" -Arguments $buildArgs
    }
    else {
        Write-Host "[UI build] skipped (--NoBuild)."
    }

    $uiFilter = "FullyQualifiedName~AppSelfTestSmokeTests"
    $testArgs = @(
        "test",
        "tests/CaseGraph.Infrastructure.Tests/CaseGraph.Infrastructure.Tests.csproj",
        "-c", $Configuration,
        "--no-build",
        "--filter", $uiFilter
    )

    $skipRestoreForTests = $NoRestore -or (-not $NoBuild)
    if ($skipRestoreForTests) {
        $testArgs += "--no-restore"
    }

    Invoke-DotnetStep -StepName "UI tests" -Arguments $testArgs
    Write-Host "UI tier succeeded."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Pop-Location
}
