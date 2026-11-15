# Compiles master\MasterServer.cs with Windows' own C# 5 compiler, runs it in a
# throwaway folder on a spare port and exercises every endpoint with curl.
#   .\tools\test-master.ps1 [-Port 27191]
param([int]$Port = 27191)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) ("garrevival-master-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$exe = Join-Path $work 'GarRevival.Master.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc /nologo /optimize+ /target:exe "/out:$exe" (Join-Path $root 'master\MasterServer.cs')
if ($LASTEXITCODE -ne 0) { throw 'master does not compile' }
Set-Content (Join-Path $work 'master.cfg') "port=$Port   # spare port`npublicAddress=203.0.113.9`nrequestsPerMinute=60`nserverTimeout=30" -Encoding ascii

$base = "http://127.0.0.1:$Port"
$script:failed = 0
function Check([string]$what, [bool]$ok, $detail) {
    Write-Host ("{0,-5} {1}{2}" -f $(if ($ok) { 'OK' } else { 'FAIL' }), $what, $(if ($detail) { "  -> $detail" }))
    if (-not $ok) { $script:failed++ }
}
function Call([string]$method, [string]$path, [string]$body, [string[]]$extra = @()) {
    $out = Join-Path $work 'out.txt'
    $head = Join-Path $work 'head.txt'
    $a = @('-s', '-o', $out, '-D', $head, '-w', '%{http_code}', '-X', $method, "$base$path") + $extra
    if ($null -ne $body) {
        $in = Join-Path $work 'in.json'
        [IO.File]::WriteAllText($in, $body)
        $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$in")
    }
    $code = [int](& curl.exe @a)
    $text = if (Test-Path $out) { Get-Content $out -Raw } else { '' }
    $json = $null
    try { $json = $text | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Code = $code; Json = $json; Text = $text; Head = (Get-Content $head -Raw -ErrorAction SilentlyContinue) }
}
function Start-Master {
    $p = Start-Process -FilePath $exe -ArgumentList "`"$work`"" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 50; $i++) {
        try { $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $Port); $c.Close(); return $p } catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'master did not start'
}

$proc = Start-Master
try {
    $r = Call GET '/v1/info' $null
    Check 'GET /v1/info' ($r.Code -eq 200 -and $r.Json.api -eq 1) $r.Text

    $ded = Call POST '/v1/servers' '{"name":"Dedi\u0001 one","port":27015,"mode":"TeamDeadmach","map":"Western","players":1,"maxPlayers":4,"version":"3","rules":"profiles=own","dedicated":true,"lan":["192.168.1.20","8.8.8.8"]}'
    Check 'register dedicated server' ($ded.Code -eq 200 -and $ded.Json.id.Length -eq 12 -and $ded.Json.token.Length -eq 32) $ded.Text
    Check 'loopback registrant gets publicAddress' ($ded.Json.address -eq '203.0.113.9') $ded.Json.address
    $host2 = Call POST '/v1/servers' '{"name":"Full host","port":27016,"mode":"CaptureTheFlag","players":4,"maxPlayers":4,"version":"3"}'
    Check 'register player host' ($host2.Code -eq 200) $host2.Text
    $steam = Call POST '/v1/servers' '{"name":"Steam host","port":1,"transport":"steam","steamLobby":"109775241000000001","mode":"TeamDeadmach","players":0,"maxPlayers":8,"version":"2"}'
    Check 'register steam host' ($steam.Code -eq 200) $steam.Text

    $r = Call GET '/v1/servers' $null
    Check 'list all' ($r.Code -eq 200 -and $r.Json.total -eq 3) $r.Json.total
    $d = $r.Json.servers | Where-Object { $_.id -eq $ded.Json.id }
    Check 'control characters stripped from name' ($d.name -eq 'Dedi one') $d.name
    Check 'local requester gets the LAN address' ($d.address -eq '127.0.0.1') $d.address
    $r = Call GET '/v1/servers?mode=CaptureTheFlag' $null
    Check 'list mode filter' ($r.Json.total -eq 1 -and $r.Json.servers[0].name -eq 'Full host') $r.Json.total
    $r = Call GET '/v1/servers?notFull=1&version=3' $null
    Check 'list notFull + version filter' ($r.Json.total -eq 1 -and $r.Json.servers[0].id -eq $ded.Json.id) $r.Json.total

    $r = Call POST "/v1/servers/$($ded.Json.id)/beat" "{`"token`":`"$($ded.Json.token)`",`"players`":3,`"state`":`"battle`"}"
    Check 'heartbeat' ($r.Code -eq 200 -and $r.Json.ok) $r.Text
    $r = Call POST "/v1/servers/$($ded.Json.id)/beat" '{"token":"00000000000000000000000000000000"}'
    Check 'heartbeat with a wrong token -> 403' ($r.Code -eq 403) $r.Text
    $r = Call POST '/v1/servers/0123456789ab/beat' "{`"token`":`"$($ded.Json.token)`"}"
    Check 'heartbeat for an unknown id -> 404' ($r.Code -eq 404 -and $r.Json.error -eq 'unknown_server') $r.Text
    $r = Call POST "/v1/servers/$($ded.Json.id)/beat" "{`"token`":`"$($ded.Json.token)`",`"players`":-1}"
    Check 'heartbeat with bad players -> 400' ($r.Code -eq 400 -and $r.Json.field -eq 'players') $r.Text

    $r = Call GET '/v1/match?mode=any&version=3' $null
    Check 'match: full servers left out, same version first' ($r.Code -eq 200 -and $r.Json.servers.Count -eq 2 -and $r.Json.servers[0].id -eq $ded.Json.id) (($r.Json.servers | ForEach-Object name) -join ', ')
    $r = Call GET '/v1/match?mode=CaptureTheFlag&version=3' $null
    Check 'match: nothing with room in that mode' ($r.Code -eq 200 -and $r.Json.servers.Count -eq 0) $r.Text

    $result = "{`"id`":`"$($ded.Json.id)`",`"token`":`"$($ded.Json.token)`",`"mode`":`"TeamDeadmach`",`"map`":`"Western`",`"seconds`":600,`"winningTeam`":1,`"winners`":[`"Alice.1a2b`",`"Bob.3c4d`"],`"losers`":[`"Carol.5e6f`"]}"
    $r = Call POST '/v1/results' $result
    Check 'results rated' ($r.Code -eq 200 -and $r.Json.rated -and $r.Json.ratings.Count -eq 3) $r.Text
    $r = Call POST '/v1/results' $result
    Check 'second result within 30 s -> 429' ($r.Code -eq 429) $r.Text
    $r = Call POST '/v1/results' "{`"id`":`"$($host2.Json.id)`",`"token`":`"$($host2.Json.token)`",`"mode`":`"CaptureTheFlag`",`"seconds`":60,`"winningTeam`":-1,`"winners`":[`"A.1111`"],`"losers`":[`"B.2222`"]}"
    Check 'results from a non-dedicated server -> 403' ($r.Code -eq 403 -and $r.Json.error -eq 'not_dedicated') $r.Text
    $r = Call POST '/v1/results' "{`"id`":`"$($ded.Json.id)`",`"token`":`"$($ded.Json.token)`",`"mode`":`"TeamDeadmach`",`"seconds`":60,`"winningTeam`":0,`"winners`":[`"A.1111`"],`"losers`":[`"A.1111`"]}"
    Check 'results with a name on both sides -> 400' ($r.Code -eq 400) $r.Text
    $r = Call GET '/v1/ladder?mode=any&top=10&name=Carol.5e6f' $null
    Check 'ladder' ($r.Code -eq 200 -and $r.Json.total -eq 3 -and $r.Json.rows[0].rating -gt 1000 -and $r.Json.you.name -eq 'Carol.5e6f' -and $r.Json.you.rating -lt 1000) $r.Text
    $r = Call GET '/v1/ladder?mode=TeamDeadmach' $null
    Check 'ladder per mode' ($r.Code -eq 200 -and $r.Json.total -eq 3) $r.Json.total
    $r = Call GET '/v1/ladder?top=1000' $null
    Check 'ladder top out of range -> 400' ($r.Code -eq 400) $r.Text

    $r = Call POST '/v1/servers' '{"name":"x","port":'
    Check 'malformed JSON -> 400' ($r.Code -eq 400 -and $r.Json.error -eq 'bad_json') $r.Text
    $r = Call POST '/v1/servers' '{"name":"x","port":70000}'
    Check 'port out of range -> 400' ($r.Code -eq 400 -and $r.Json.field -eq 'port') $r.Text
    $r = Call POST '/v1/servers' '{"name":"x","port":27015,"map":"../../etc"}'
    Check 'unknown map -> 400' ($r.Code -eq 400 -and $r.Json.field -eq 'map') $r.Text
    $r = Call POST '/v1/servers' ('[' * 30 + ']' * 30)
    Check 'deep nesting -> 400' ($r.Code -eq 400) $r.Text
    $r = Call POST '/v1/servers' ('{"name":"' + ('x' * 9000) + '","port":1}')
    Check 'oversize body -> 413' ($r.Code -eq 413) $r.Text
    $r = Call POST '/v1/servers' '{"port":1}' @('-H', 'Transfer-Encoding: chunked')
    Check 'chunked body -> 411' ($r.Code -eq 411) $r.Text
    $r = Call DELETE "/v1/servers/$($ded.Json.id)" $null
    Check 'other methods -> 405' ($r.Code -eq 405) $r.Text
    $r = Call GET '/etc/passwd' $null
    Check 'unknown path -> 404' ($r.Code -eq 404) $r.Text

    $r = Call POST "/v1/servers/$($host2.Json.id)/remove" "{`"token`":`"$($host2.Json.token)`"}"
    Check 'remove' ($r.Code -eq 200) $r.Text
    $r = Call GET '/v1/servers' $null
    Check 'removed server is gone' ($r.Json.total -eq 2) $r.Json.total

    # The ladder is saved within about 10 s of a change.
    $ladder = Join-Path $work 'master-ladder.txt'
    for ($i = 0; $i -lt 40 -and -not (Test-Path $ladder); $i++) { Start-Sleep -Milliseconds 500 }
    Check 'ladder saved to master-ladder.txt' (Test-Path $ladder) $null

    # A client that stops sending is answered 408 and dropped after 5 s.
    $slow = New-Object Net.Sockets.TcpClient('127.0.0.1', $Port)
    $stream = $slow.GetStream()
    $partial = [Text.Encoding]::ASCII.GetBytes("GET /v1/info HTTP/1.1`r`n")
    $stream.Write($partial, 0, $partial.Length)
    $stream.ReadTimeout = 10000
    $buffer = New-Object byte[] 512
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $n = try { $stream.Read($buffer, 0, $buffer.Length) } catch { 0 }
    $slow.Close()
    $answer = [Text.Encoding]::ASCII.GetString($buffer, 0, [Math]::Max(0, $n))
    Check 'slow client -> 408 after 5 s' ($answer.StartsWith('HTTP/1.1 408') -and $watch.Elapsed.TotalSeconds -ge 4.5 -and $watch.Elapsed.TotalSeconds -lt 7) ("{0:0.0} s" -f $watch.Elapsed.TotalSeconds)

    # requestsPerMinute=60: a burst runs out.
    $codes = 1..70 | ForEach-Object { (Call GET '/v1/info' $null).Code }
    $limited = Call GET '/v1/info' $null
    Check 'rate limit -> 429 with Retry-After' (($codes -contains 429) -and $limited.Code -eq 429 -and $limited.Head -match 'Retry-After: \d+') (($codes | Group-Object | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ')
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
}

$proc = Start-Master
try {
    $r = Call GET '/v1/ladder?mode=any' $null
    Check 'ladder reloaded after a restart' ($r.Code -eq 200 -and $r.Json.total -eq 3) $r.Text
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host $(if ($failed -eq 0) { 'All master tests passed.' } else { "$failed master test(s) failed." })
exit $failed
