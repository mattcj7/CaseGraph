param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$NoRestore,
    [string[]]$Reason = @("FAST tier always runs by policy.")
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
    Write-Host "=== Running FAST tier ==="
    foreach ($item in $Reason) {
        Write-Host "WHY: $item"
    }

    if (-not $NoBuild) {
        $buildArgs = @("build", "CaseGraph.sln", "-c", $Configuration)
        if ($NoRestore) {
            $buildArgs += "--no-restore"
        }

        Invoke-DotnetStep -StepName "FAST build" -Arguments $buildArgs
    }
    else {
        Write-Host "[FAST build] skipped (--NoBuild)."
    }

    $testArgs = @(
        "test",
        "tests/CaseGraph.Core.Tests/CaseGraph.Core.Tests.csproj",
        "-c", $Configuration,
        "--no-build"
    )

    $skipRestoreForTests = $NoRestore -or (-not $NoBuild)
    if ($skipRestoreForTests) {
        $testArgs += "--no-restore"
    }

    Invoke-DotnetStep -StepName "FAST tests" -Arguments $testArgs
    Write-Host "FAST tier succeeded."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Pop-Location
}
