[CmdletBinding()]
param(
    [Parameter()]
    [string] $HexagonRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'hexagon'),

    [Parameter()]
    [switch] $SkipRestore
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
if ($LASTEXITCODE -ne 0 -or $actualHead -cne [string]$lock.commit) {
    throw "Hexagon checkout HEAD '$actualHead' does not match locked commit '$($lock.commit)'."
}
$actualHL2RPHead = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualHL2RPHead -cnotmatch '^[a-f0-9]{40}$') {
    throw "Could not resolve a lowercase full HL2RP HEAD for '$root'."
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    Write-Host "==> $Description" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

$projects = @(
    (Join-Path $HexagonRoot 'tests/Hexagon.V2.Tests.csproj'),
    (Join-Path $root 'tests/HL2RP.V2.Tests.csproj')
)

foreach ($project in $projects) {
    if (-not $SkipRestore) {
        Invoke-CheckedCommand -Description "Restoring locked and audited dependencies: $project" -FilePath 'dotnet' -Arguments @(
            'restore', $project, '--locked-mode', '--nologo', '-warnaserror',
            '-p:NuGetAudit=true', '-p:NuGetAuditMode=all'
        )
    }
    $auditJson = & dotnet package list --project $project --vulnerable --include-transitive --format json --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet vulnerability inventory failed for '$project' with exit code $LASTEXITCODE."
    }
    if (($auditJson -join "`n") -match '"severity"\s*:') {
        throw "NuGet reported a vulnerable direct or transitive dependency for '$project'."
    }
    Invoke-CheckedCommand -Description "Running portable integration tests: $project" -FilePath 'dotnet' -Arguments @(
        'test', $project, '--configuration', 'Release', '--no-restore', '--nologo', '--warnaserror'
    )
}

Write-Host '==> Running portable Hexagon/HL2RP project integration validation' -ForegroundColor Cyan
& (Join-Path $HexagonRoot 'tools/validate-project.ps1') -HexagonRoot $HexagonRoot -SchemaRoot $root -SkipEngineModelValidation

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

Write-Host "HL2RP portable integration passed against Hexagon $actualHead." -ForegroundColor Green
