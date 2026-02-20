param(
    [string]$Configuration = "Debug",
    [switch]$Full
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fastScript = Join-Path $PSScriptRoot "test-fast.ps1"
$dbScript = Join-Path $PSScriptRoot "test-db.ps1"
$uiScript = Join-Path $PSScriptRoot "test-ui.ps1"
$fullScript = Join-Path $PSScriptRoot "test-full.ps1"

function Get-ChangedFiles {
    param(
        [switch]$Staged
    )

    $args = @("diff", "--name-only")
    if ($Staged) {
        $args += "--staged"
    }

    $lines = & git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @(
        $lines
        | ForEach-Object { "$_".Trim() }
        | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        | ForEach-Object { $_.Replace('\', '/') }
    )
}

function Get-TriggeredFiles {
    param(
        [string[]]$Files,
        [string[]]$RegexRules
    )

    return @(
        $Files | Where-Object {
            $path = $_
            foreach ($rule in $RegexRules) {
                if ($path -imatch $rule) {
                    return $true
                }
            }

            return $false
        }
    )
}

function Invoke-TierScript {
    param(
        [string]$TierName,
        [string]$ScriptPath,
        [hashtable]$Parameters
    )

    Write-Host ""
    Write-Host "--- Invoking $TierName tier ---"
    & $ScriptPath @Parameters
    if ($LASTEXITCODE -ne 0) {
        throw "$TierName tier failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repoRoot
try {
    Write-Host "=== Smart Test Runner ==="
    $unstagedFiles = @(Get-ChangedFiles)
    $stagedFiles = @(Get-ChangedFiles -Staged)
    $changedFiles = @(
        ($unstagedFiles + $stagedFiles)
        | Sort-Object -Unique
    )

    if ($changedFiles.Count -eq 0) {
        Write-Host "Changed files: none (from git diff + git diff --staged)."
    }
    else {
        Write-Host "Changed files (git diff + git diff --staged):"
        foreach ($file in $changedFiles) {
            Write-Host " - $file"
        }
    }

    if ($Full) {
        $fullReasons = @(
            "Full suite forced by -Full."
            "By policy, full suite is reserved for FULL_TESTS_REQUIRED or explicit user request."
        )
        Invoke-TierScript -TierName "FULL" -ScriptPath $fullScript -Parameters @{
            Configuration = $Configuration
            Reason = $fullReasons
        }

        Write-Host ""
        Write-Host "Smart runner finished: FULL tier complete."
        exit 0
    }

    $dbRules = @(
        '^src/.+/Persistence/',
        '^src/.+/Migrations/',
        '^src/.+/Queries/',
        '^src/.+/Jobs/',
        '^src/.+/Ingest/',
        '^src/.+/Parsers/',
        '/[^/]*(DbContext|WorkspaceDbContext)[^/]*$'
    )
    $uiRules = @(
        '\.xaml$',
        '^src/.+/Themes/',
        '^src/.+/Resources/',
        '^src/.+/Views/',
        '^src/.+/App\.xaml($|\.cs$)',
        '^src/.+/MainWindow'
    )

    $dbTriggers = @(Get-TriggeredFiles -Files $changedFiles -RegexRules $dbRules)
    $uiTriggers = @(Get-TriggeredFiles -Files $changedFiles -RegexRules $uiRules)

    $fastReasons = @("FAST tier always runs by policy.")
    if ($changedFiles.Count -eq 0) {
        $fastReasons += "No git-diff changes detected; running baseline verification."
    }
    else {
        $fastReasons += "Detected $($changedFiles.Count) changed file(s)."
    }

    Invoke-TierScript -TierName "FAST" -ScriptPath $fastScript -Parameters @{
        Configuration = $Configuration
        Reason = $fastReasons
    }

    if ($dbTriggers.Count -gt 0) {
        Invoke-TierScript -TierName "DB" -ScriptPath $dbScript -Parameters @{
            Configuration = $Configuration
            NoBuild = $true
            NoRestore = $true
            Reason = $dbTriggers
        }
    }
    else {
        Write-Host ""
        Write-Host "--- Skipping DB tier ---"
        Write-Host "WHY: no persistence/migrations/jobs/ingest/parsers/DbContext trigger files changed."
    }

    if ($uiTriggers.Count -gt 0) {
        Invoke-TierScript -TierName "UI" -ScriptPath $uiScript -Parameters @{
            Configuration = $Configuration
            NoBuild = $true
            NoRestore = $true
            Reason = $uiTriggers
        }
    }
    else {
        Write-Host ""
        Write-Host "--- Skipping UI tier ---"
        Write-Host "WHY: no XAML/resources/views/startup trigger files changed."
    }

    Write-Host ""
    Write-Host "Smart runner finished successfully."
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Pop-Location
}
