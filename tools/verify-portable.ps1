[CmdletBinding()]
param(
    [Parameter()]
    [string] $HexagonRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'hexagon'),

    [Parameter()]
    [switch] $AllowDirty,

    [Parameter()]
    [switch] $AllowUnlockedHexagon
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $root 'hexagon.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ([string]$lock.repository -cnotmatch '^[a-z0-9_.-]+/[a-z0-9_.-]+$') {
    throw "hexagon.lock.json repository must be a lowercase owner/name pair."
}
if ([string]$lock.commit -cnotmatch '^[a-f0-9]{40}$') {
    throw "hexagon.lock.json commit must be a lowercase full 40-character SHA."
}

$HexagonRoot = (Resolve-Path -LiteralPath $HexagonRoot).Path
$actualHead = (& git -C $HexagonRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualHead -cnotmatch '^[a-f0-9]{40}$') {
    throw "Could not resolve a lowercase full Hexagon HEAD for '$HexagonRoot'."
}
if (-not $AllowUnlockedHexagon) {
    if ($actualHead -cne [string]$lock.commit) {
        throw "Hexagon checkout HEAD '$actualHead' does not match locked commit '$($lock.commit)'. " +
            'Pass -AllowUnlockedHexagon for an explicit local cross-HEAD canary run.'
    }
}
else {
    Write-Host "==> Hexagon lock check skipped (-AllowUnlockedHexagon): cross-HEAD run against Hexagon $actualHead" -ForegroundColor Yellow
}
$actualHL2RPHead = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualHL2RPHead -cnotmatch '^[a-f0-9]{40}$') {
    throw "Could not resolve a lowercase full HL2RP HEAD for '$root'."
}

. (Join-Path $HexagonRoot 'tools/worktree-gate.ps1')
. (Join-Path $HexagonRoot 'tools/verify-common.ps1')
$attributed = Assert-CleanWorktree -Repositories @(
    [pscustomobject]@{ Label = 'hexagon'; Root = $HexagonRoot },
    [pscustomobject]@{ Label = 'hl2rp-hexagon'; Root = $root }) -AllowDirty:$AllowDirty

$projects = @(
    (Join-Path $HexagonRoot 'tests/Hexagon.V2.Tests.csproj'),
    (Join-Path $root 'tests/HL2RP.V2.Tests.csproj')
)

foreach ($project in $projects) {
    # The audited restore always runs: a cache-hit locked restore is cheap, and a
    # skipped restore previously left the vulnerability grep as the only (vacuous) gate.
    Invoke-CheckedCommand -Description "Restoring locked and audited dependencies: $project" -FilePath 'dotnet' -Arguments @(
        'restore', $project, '--locked-mode', '--nologo', '-warnaserror',
        '-p:NuGetAudit=true', '-p:NuGetAuditMode=all'
    )
    $auditJson = @(& dotnet package list --project $project --vulnerable --include-transitive --format json --output-version 1 --no-restore)
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet vulnerability inventory failed for '$project' with exit code $LASTEXITCODE."
    }
    Assert-NoVulnerablePackages -Project $project -AuditJson $auditJson
    Invoke-CheckedCommand -Description "Running portable integration tests: $project" -FilePath 'dotnet' -Arguments @(
        'test', $project, '--configuration', 'Release', '--no-restore', '--nologo', '--warnaserror'
    )
}

Write-Host '==> Running portable Hexagon/HL2RP project integration validation' -ForegroundColor Cyan
& (Join-Path $HexagonRoot 'tools/validate-project.ps1') -HexagonRoot $HexagonRoot -SchemaRoot $root -SkipEngineModelValidation

if ($attributed) {
    Write-Host '==> Exercising exact-SHA release-evidence template binding' -ForegroundColor Cyan
    $evidenceDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("hexagon-release-evidence-contract-" + [Guid]::NewGuid().ToString('N'))
    $evidencePath = Join-Path $evidenceDirectory 'remote-acceptance.json'
    try {
        $mismatchRejected = $false
        try {
            & (Join-Path $HexagonRoot 'tools/verify-remote-acceptance.ps1') `
                -EvidencePath $evidencePath `
                -HexagonRoot $HexagonRoot `
                -SchemaRoot $root `
                -HexagonSha ('0' * 40) `
                -HL2RPSha $actualHL2RPHead `
                -WriteTemplate
        }
        catch {
            $mismatchRejected = $_.Exception.Message -match 'checkout HEAD does not match evidence SHA'
        }
        if (-not $mismatchRejected) {
            throw 'Release-evidence template did not reject a mismatched Hexagon SHA.'
        }
        & (Join-Path $HexagonRoot 'tools/verify-remote-acceptance.ps1') `
            -EvidencePath $evidencePath `
            -HexagonRoot $HexagonRoot `
            -SchemaRoot $root `
            -HexagonSha $actualHead `
            -HL2RPSha $actualHL2RPHead `
            -WriteTemplate
        $template = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if ([string]$template.hexagon_sha -cne $actualHead -or
            [string]$template.hl2rp_sha -cne $actualHL2RPHead -or
            [string]$template.source_fingerprint -cnotmatch '^[a-f0-9]{64}$') {
            throw 'Release-evidence template did not bind both exact source SHAs and the tracked-tree fingerprint.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $evidencePath -PathType Leaf) {
            [System.IO.File]::Delete($evidencePath)
        }
        if (Test-Path -LiteralPath $evidenceDirectory -PathType Container) {
            [System.IO.Directory]::Delete($evidenceDirectory, $false)
        }
    }
}
else {
    Write-Host '==> Skipping release-evidence template exercise: uncommitted sources cannot bind exact SHAs.' -ForegroundColor Yellow
}

if (-not $attributed) {
    Write-Host 'HL2RP portable integration passed against UNCOMMITTED sources (no SHA attribution).' -ForegroundColor Yellow
}
elseif ($AllowUnlockedHexagon) {
    Write-Host "HL2RP portable integration passed as a cross-HEAD canary against unlocked Hexagon $actualHead." -ForegroundColor Yellow
}
else {
    Write-Host "HL2RP portable integration passed against Hexagon $actualHead." -ForegroundColor Green
}
