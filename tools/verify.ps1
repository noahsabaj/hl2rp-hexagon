# Runs the logic tests, then compiles the game against the installed s&box engine.
# The engine build is the only thing that checks components and Razor panels, so it is not optional.
[CmdletBinding()]
param(
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',
    [int] $GenerateTimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $root 'hl2rp.sbproj'
$project = Join-Path $root 'Code\hl2rp.csproj'
$sboxDev = Join-Path $SboxRoot 'sbox-dev.exe'

function Invoke-Checked([string] $Description, [scriptblock] $Command) {
    Write-Host "==> $Description" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

Invoke-Checked 'Running logic tests' {
    dotnet test (Join-Path $root 'Tests\HL2RP.Tests.csproj') --nologo --verbosity quiet
}

if (-not (Test-Path -LiteralPath $sboxDev -PathType Leaf)) { throw "s&box was not found at '$SboxRoot'." }
if (Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue) {
    throw 'Close the s&box editor first: it holds the generated project open.'
}

# s&box writes the C# project when it opens the game. Open it hidden, wait for the file to settle, close it.
Write-Host '==> Generating the s&box project' -ForegroundColor Cyan
if (Test-Path -LiteralPath $project) { Remove-Item -LiteralPath $project -Force }
$editor = Start-Process -FilePath $sboxDev -ArgumentList "-project `"$manifest`"" -PassThru -WindowStyle Hidden
try {
    $deadline = [DateTime]::UtcNow.AddSeconds($GenerateTimeoutSeconds)
    $signature = $null
    $stableSince = [DateTime]::UtcNow
    while ($true) {
        if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) before generating the project." }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for s&box to generate '$project'." }
        if (Test-Path -LiteralPath $project -PathType Leaf) {
            $info = Get-Item -LiteralPath $project
            $current = "$($info.Length):$($info.LastWriteTimeUtc.Ticks)"
            if ($current -ne $signature) { $signature = $current; $stableSince = [DateTime]::UtcNow }
            elseif (([DateTime]::UtcNow - $stableSince).TotalSeconds -ge 2) { break }
        }
        Start-Sleep -Milliseconds 250
    }
}
finally {
    if (-not $editor.HasExited) { Stop-Process -Id $editor.Id -Force }
}

Invoke-Checked 'Compiling the game against the engine, warnings as errors' {
    dotnet build $project --nologo --verbosity quiet -warnaserror
}

Write-Host 'Verified: logic tests pass and the game compiles against s&box.' -ForegroundColor Green
