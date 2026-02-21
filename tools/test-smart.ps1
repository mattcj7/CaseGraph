param(
    [string]$Configuration = "Debug",
    [switch]$Full,
    [switch]$ForceDb
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

function Get-DiffTextForFile {
    param(
        [string]$FilePath
    )

    $unstaged = & git diff -- $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "git diff -- $FilePath failed with exit code $LASTEXITCODE."
    }

    $staged = & git diff --staged -- $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "git diff --staged -- $FilePath failed with exit code $LASTEXITCODE."
    }

    return (($unstaged -join [Environment]::NewLine) + [Environment]::NewLine + ($staged -join [Environment]::NewLine))
}

function Get-KeywordTriggerMatches {
    param(
        [string[]]$Files,
        [string[]]$Keywords
    )

    $matches = New-Object System.Collections.Generic.List[object]
    foreach ($file in $Files) {
        $diffText = Get-DiffTextForFile -FilePath $file
        if ([string]::IsNullOrWhiteSpace($diffText)) {
            continue
        }

        $fileKeywords = New-Object System.Collections.Generic.List[string]
        foreach ($keyword in $Keywords) {
            $pattern = [regex]::Escape($keyword)
            if ($diffText -imatch $pattern) {
                if (-not $fileKeywords.Contains($keyword)) {
                    $fileKeywords.Add($keyword)
                }
            }
        }

        if ($fileKeywords.Count -gt 0) {
            $matches.Add([PSCustomObject]@{
                    File = $file
                    Keywords = @($fileKeywords | Sort-Object -Unique)
                })
        }
    }

    return @($matches)
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
        '^src/.+/WorkspaceDatabaseInitializer[^/]*$',
        '^src/.+/WorkspaceMigrationService[^/]*$',
        '^src/.+/Workspace[^/]*Db[^/]*$',
        '^src/.+/ServiceCollectionExtensions[^/]*$',
        '^src/.+/TimeoutWatchdog[^/]*$',
        '^src/.+/[^/]*DbContext[^/]*$'
    )
    $dbKeywordRules = @(
        'UseSqlite',
        'SqliteConnectionStringBuilder',
        'Database.Migrate',
        '__EFMigrationsHistory',
        'DefaultTimeout',
        'busy_timeout'
    )
    $uiRules = @(
        '\.xaml$',
        '^src/.+/Themes/',
        '^src/.+/Resources/',
        '^src/.+/Views/',
        '^src/.+/App\.xaml($|\.cs$)',
        '^src/.+/MainWindow'
    )

    $dbPathTriggers = @(
        Get-TriggeredFiles -Files $changedFiles -RegexRules $dbRules
        | Sort-Object -Unique
    )
    $keywordEligibleFiles = @(
        $changedFiles | Where-Object { $_ -imatch '^src/' }
    )
    $dbKeywordTriggers = @()
    if ($keywordEligibleFiles.Count -gt 0) {
        $dbKeywordTriggers = Get-KeywordTriggerMatches -Files $keywordEligibleFiles -Keywords $dbKeywordRules
    }

    $uiTriggers = @(Get-TriggeredFiles -Files $changedFiles -RegexRules $uiRules)
    $shouldRunDb = $ForceDb -or $dbPathTriggers.Count -gt 0 -or $dbKeywordTriggers.Count -gt 0
    $dbReasons = @()

    if ($ForceDb) {
        $dbReasons += "Forced by -ForceDb switch."
    }

    foreach ($file in $dbPathTriggers) {
        $dbReasons += "Path trigger: $file"
    }

    foreach ($keywordTrigger in $dbKeywordTriggers) {
        $dbReasons += "Keyword trigger in $($keywordTrigger.File): $($keywordTrigger.Keywords -join ', ')"
    }

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

    if ($shouldRunDb) {
        if ($dbReasons.Count -eq 0) {
            $dbReasons += "DB tier triggered."
        }

        Invoke-TierScript -TierName "DB" -ScriptPath $dbScript -Parameters @{
            Configuration = $Configuration
            NoBuild = $true
            NoRestore = $true
            Reason = $dbReasons
        }
    }
    else {
        Write-Host ""
        Write-Host "--- Skipping DB tier ---"
        Write-Host "WHY: no DB path or keyword triggers matched."
        Write-Host "WHY: checked paths for workspace init/sqlite/DbContext/persistence/migrations/jobs/ingest/parsers."
        Write-Host "WHY: checked diff keywords in src/: $($dbKeywordRules -join ', ')."
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
