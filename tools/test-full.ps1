param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$NoRestore,
    [string[]]$Reason = @("Full suite requested.")
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
    Write-Host "=== Running FULL tier ==="
    foreach ($item in $Reason) {
        Write-Host "WHY: $item"
    }

    if (-not $NoBuild) {
        $buildArgs = @("build", "CaseGraph.sln", "-c", $Configuration)
        if ($NoRestore) {
            $buildArgs += "--no-restore"
        }

        Invoke-DotnetStep -StepName "FULL build" -Arguments $buildArgs
    }
    else {
        Write-Host "[FULL build] skipped (--NoBuild)."
    }

    $testArgs = @(
        "test",
        "CaseGraph.sln",
        "-c", $Configuration,
        "--no-build"
    )

    $skipRestoreForTests = $NoRestore -or (-not $NoBuild)
    if ($skipRestoreForTests) {
        $testArgs += "--no-restore"
    }

    Invoke-DotnetStep -StepName "FULL tests" -Arguments $testArgs
    Write-Host "FULL tier succeeded."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Pop-Location
}
