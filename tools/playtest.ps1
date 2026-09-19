# Plays the game in the real s&box editor and checks what happens.
#
# Nothing here is faked: the editor boots the project, enters play mode, and a small editor-only
# driver component calls the same client requests the HUD calls. Each one travels the real RPC path
# and is judged by the real host rules. The run uses a throwaway data folder and deletes it after.
[CmdletBinding()]
param(
    [string] $SboxRoot = 'C:\Program Files (x86)\Steam\steamapps\common\sbox',
    [int] $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $root 'hl2rp.sbproj'
$sboxDev = Join-Path $SboxRoot 'sbox-dev.exe'
$endpoint = 'http://127.0.0.1:7269/mcp'
$hud = 'b7200000-0000-4000-8000-000000000001'
$dataRoot = "playtest-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$description = "A tired resident of the city."

if (Get-Process -Name 'sbox-dev' -ErrorAction SilentlyContinue) { throw 'Close the s&box editor first.' }

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(60)
$http.DefaultRequestHeaders.Accept.ParseAdd('application/json, text/event-stream')
$script:requestId = 0
$script:failures = 0

function Invoke-Editor([string] $Method, $Params) {
    $script:requestId++
    $body = [ordered]@{ jsonrpc = '2.0'; id = $script:requestId; method = $Method; params = $Params } |
        ConvertTo-Json -Depth 12 -Compress
    $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $http.PostAsync($endpoint, $content).GetAwaiter().GetResult()
        $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "HTTP $([int]$response.StatusCode): $payload" }
        return $payload | ConvertFrom-Json
    }
    finally { $content.Dispose() }
}

function Invoke-Tool([string] $Name, $Arguments = @{}) {
    $reply = Invoke-Editor 'tools/call' ([ordered]@{ name = 'call_tool'; arguments = [ordered]@{ name = $Name; arguments = $Arguments } })
    $text = ($reply.result.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
    if ($reply.result.PSObject.Properties['isError'] -and $reply.result.isError) { throw "$Name failed: $text" }
    return $text
}

function Wait-Until([string] $What, [scriptblock] $Condition) {
    while ([DateTime]::UtcNow -lt $script:deadline) {
        if ($editor.HasExited) { throw "s&box exited with code $($editor.ExitCode) while waiting for $What." }
        try { if (& $Condition) { return } } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $What."
}

# Sends one driver command and returns what the game reported back through the console.
function Send([string] $Command) {
    [void](Invoke-Tool 'set_component' ([ordered]@{ id = $hud; type = 'DevDriver'; properties = @{ Command = $Command } }))
    Start-Sleep -Milliseconds 700
    $lines = (Invoke-Tool 'read_console' @{ limit = 40; filter = '[dev]' }) -split "`n" | Where-Object { $_ -match '\[dev\]' }
    return (($lines | Select-Object -Last 1) -replace '^.*?=> ', '')
}

function Expect([string] $What, [string] $State, [string] $Pattern) {
    if ($State -match $Pattern) { Write-Host "  ok   $What" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "  FAIL $What" -ForegroundColor Red
        Write-Host "       wanted /$Pattern/ in: $State" -ForegroundColor DarkGray
    }
}

function Start-Play {
    # The asset system may still be indexing right after the assembly loads.
    Wait-Until 'the scene asset' { [void](Invoke-Tool 'open_scene' @{ path = 'scenes/main.scene' }); $true }
    Wait-Until 'play mode' { (Invoke-Tool 'play_start') -match '"IsPlaying":true' }
    Wait-Until 'the local player' {
        [void](Invoke-Tool 'add_component' @{ id = $hud; type = 'DevDriver' })
        (Send 'state') -match '^has='
    }
}

Write-Host "==> Starting the editor (data folder '$dataRoot')" -ForegroundColor Cyan
$editor = Start-Process -FilePath $sboxDev -WorkingDirectory $SboxRoot -PassThru -WindowStyle Hidden `
    -ArgumentList "-project `"$manifest`" +hl2rp_data_root $dataRoot"
$script:deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
try {
    Wait-Until 'the editor control endpoint' {
        (Invoke-Editor 'initialize' ([ordered]@{
            protocolVersion = '2025-03-26'; capabilities = @{}; clientInfo = @{ name = 'hl2rp-playtest'; version = '1' }
        })).result.protocolVersion
    }
    Wait-Until 'the game assembly' { (Invoke-Tool 'get_component_type' @{ name = 'DevDriver' }) -match 'HL2RP' }

    Write-Host '==> Characters' -ForegroundColor Cyan
    Start-Play
    Expect 'a new city has no characters' (Send 'state') 'has=False .*characters=\[\]'
    [void](Send "create|John Doe|$description|citizen")
    [void](Send "create|J0hn Doe|$description|citizen")
    [void](Send "create|Officer Kane|$description|civil_protection")
    $state = Send 'state'
    Expect 'the character was created, its look-alike was not' $state 'characters=\[John Doe\]'
    Expect 'the look-alike name was refused with a reason' $state 'reads like it'
    Expect 'a whitelisted faction refused an unlisted account' $state 'not whitelisted for Civil Protection'

    Write-Host '==> Entering the city, chat, inventory' -ForegroundColor Cyan
    [void](Send 'enter|John Doe')
    [void](Send 'say|Hello there')
    [void](Send 'say|/me waves')
    [void](Send 'say|//brb')
    [void](Send 'move|0|4|3')
    $state = Send 'state'
    Expect 'public identity replicated from the host' $state "has=True name='John Doe' faction='Citizen'"
    Expect 'the faction asset granted starting items' $state 'Water@'
    Expect 'an item moved to the requested cell' $state 'Ration@4,3'
    Expect 'speech, emote and out-of-character lines were delivered' $state 'says "Hello there" / \*\* John Doe waves / \[OOC\] John Doe: brb'

    Write-Host '==> Doors' -ForegroundColor Cyan
    [void](Send 'goto|240|-150|900')
    [void](Send 'door|Door 1|use')
    Expect 'a door out of reach was refused' (Send 'state') 'Door 1:open=False.*too far from the door'
    [void](Send 'goto|240|-150|60')
    Start-Sleep -Seconds 1
    [void](Send 'door|Door 1|use')
    [void](Send 'door|Door 2|use')
    [void](Send 'door|Door 1|lock')
    $state = Send 'state'
    Expect 'a door in reach opened' $state 'Door 1:open=True,locked=False'
    Expect 'a door authored as locked stayed shut' $state 'Door 2:open=False,locked=True.*The door is locked'
    Expect 'a citizen could not lock a door' $state 'Your faction cannot lock doors'

    Write-Host '==> Faction gate' -ForegroundColor Cyan
    $steamId = Send 'steamid'
    [void](Send 'leave')
    [void](Send "console|hl2rp_whitelist $steamId civil_protection")
    [void](Send "create|Officer Kane|$description|civil_protection")
    [void](Send 'enter|Officer Kane')
    [void](Send 'goto|240|-150|60')
    Start-Sleep -Seconds 1
    [void](Send 'door|Door 1|lock')
    $state = Send 'state'
    Expect 'the console whitelist unlocked the faction' $state "name='Officer Kane' faction='Civil Protection'"
    Expect 'Civil Protection locked the door, which also shut it' $state 'Door 1:open=False,locked=True'

    Write-Host '==> Restart' -ForegroundColor Cyan
    [void](Send 'leave')
    [void](Invoke-Tool 'play_stop')
    Start-Sleep -Seconds 3
    Start-Play
    $state = Send 'state'
    Expect 'both characters survived the restart' $state 'characters=\[John Doe, Officer Kane\]'
    Expect 'the locked door survived the restart' $state 'Door 1:open=False,locked=True'
    Expect 'a new session starts with an empty chat log' $state 'chat=\[\]'
    [void](Send 'enter|John Doe')
    $state = Send 'state'
    Expect 'the moved item survived the restart' $state 'Ration@4,3'
    Expect 'the character returned to where it stood' $state 'pos=2[34]\d\.?\d*,-1[45]\d'

    $problems = @((Invoke-Tool 'read_console' @{ limit = 500; minimumLevel = 'Warn' }) -split "`n" |
        Where-Object { $_ -match 'HL2RP|Exception|\[store\]|Whitelist violation|hl2rp\.' -and $_ -notmatch 'Bad texture|Error loading resource' })
    Expect 'the session logged no game warnings or errors' "$($problems.Count) problem lines: $($problems -join ' | ')" '^0 problem'
    [void](Invoke-Tool 'play_stop')
}
finally {
    if (-not $editor.HasExited) { Stop-Process -Id $editor.Id -Force }
    $http.Dispose()
    Get-ChildItem -LiteralPath (Join-Path $SboxRoot 'data') -Recurse -Directory -Filter $dataRoot -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
}

if ($script:failures -gt 0) { throw "$($script:failures) play-test expectation(s) failed." }
Write-Host 'Play test passed in the real engine.' -ForegroundColor Green
