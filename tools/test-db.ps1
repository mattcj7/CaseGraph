param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$NoRestore,
    [string[]]$Reason = @("DB tier triggered by persistence/migration/query/ingest changes.")
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
    Write-Host "=== Running DB tier ==="
    foreach ($item in $Reason) {
        Write-Host "WHY: $item"
    }

    if (-not $NoBuild) {
        $buildArgs = @("build", "CaseGraph.sln", "-c", $Configuration)
        if ($NoRestore) {
            $buildArgs += "--no-restore"
        }

        Invoke-DotnetStep -StepName "DB build" -Arguments $buildArgs
    }
    else {
        Write-Host "[DB build] skipped (--NoBuild)."
    }

    $dbFilter = "FullyQualifiedName!~AppSelfTestSmokeTests"
    $testArgs = @(
        "test",
        "tests/CaseGraph.Infrastructure.Tests/CaseGraph.Infrastructure.Tests.csproj",
        "-c", $Configuration,
        "--no-build",
        "--filter", $dbFilter
    )

    $skipRestoreForTests = $NoRestore -or (-not $NoBuild)
    if ($skipRestoreForTests) {
        $testArgs += "--no-restore"
    }

    Invoke-DotnetStep -StepName "DB tests" -Arguments $testArgs
    Write-Host "DB tier succeeded."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Pop-Location
}
