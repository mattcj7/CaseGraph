param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

$ticketsPath = Join-Path $RepoRoot "Docs/TICKETS.md"
if (-not (Test-Path -Path $ticketsPath -PathType Leaf)) {
    $failures.Add("Missing required ticket index: Docs/TICKETS.md")
}

$agentsPath = Join-Path $RepoRoot "AGENTS.md"
if (-not (Test-Path -Path $agentsPath -PathType Leaf)) {
    $failures.Add("Missing required agent rules file: AGENTS.md")
}

$rootTicketFiles = Get-ChildItem -Path $RepoRoot -File | Where-Object {
    $_.Name -match '^(?i)tickets?\.md$'
}

if ($rootTicketFiles.Count -gt 0) {
    $names = ($rootTicketFiles | Select-Object -ExpandProperty Name) -join ", "
    $failures.Add("Root-level ticket files are not allowed: $names")
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure
    }

    exit 1
}

Write-Host "Repository validation passed."
