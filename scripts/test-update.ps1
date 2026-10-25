<#
 End-to-end test of the online updater against a local server, in scratch folders (never the user's installs).
   scripts\test-update.ps1 [-BuildDirectory dist] [-Keep]
 Builds a fake 2.1.1 from the same sources (version bumped), signs packages with a throw-away key through
 publish-update.ps1, serves them with `ClearDesk.exe --update-test-server`, and drives the real check -> download ->
 verify -> hand over -> replace -> restart flow of a scratch 2.1.0 install (`--preview --update-e2e`).
#>
param([string]$BuildDirectory, [switch]$Keep)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $repo 'dist' }
$run = Join-Path $repo ('artifacts\UpdateE2E-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$results = New-Object Collections.Generic.List[string]
function Pass($m) { $results.Add("PASS: $m"); Write-Host "PASS: $m" }
function Check($ok, $m) { if ($ok) { Pass $m } else { $results.Add("FAIL: $m"); Write-Host "FAIL: $m"; throw "FAILED: $m" } }
function Hash($p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$startedAt = Get-Date
# the version under test, and a fake "next" release one patch above it (plus one more for the replay/lie scenario)
$curVer = [regex]::Match([IO.File]::ReadAllText((Join-Path $repo 'src\SettingsWindow.cs')), 'Version = "([0-9.]+)"').Groups[1].Value
$vp = $curVer.Split('.'); $newVer = "$($vp[0]).$($vp[1]).$([int]$vp[2] + 1)"; $lieVer = "$($vp[0]).$($vp[1]).$([int]$vp[2] + 2)"
$curRx = [regex]::Escape($curVer); $newRx = [regex]::Escape($newVer)
$runKey = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).ClearDesk   # must never change

try {
    # ---- keys (throw-away) and the app's trust override (honoured only with --preview)
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'new-update-key.ps1') -KeyDir "$run\keys" | Out-Null
    $pubKey = (Get-Content "$run\keys\cleardesk-update-public.key" -Raw).Trim()
    $env:CLEARDESK_UPDATE_PUBKEY = $pubKey
    Check ($pubKey.Length -gt 60) 'throw-away signing key generated'

    # ---- fake 2.1.1 built from the same sources with the version bumped
    $fake = "$run\fake-src"; New-Item -ItemType Directory $fake | Out-Null
    foreach ($d in 'src', 'tests', 'third_party', 'assets', 'config') { Copy-Item -LiteralPath (Join-Path $repo $d) -Destination "$fake\$d" -Recurse }
    Copy-Item (Join-Path $repo 'build.ps1') $fake
    $sw = Join-Path $fake 'src\SettingsWindow.cs'; $txt = [IO.File]::ReadAllText($sw)
    if ($txt -notmatch ('Version = "' + $curRx + '"')) { throw 'SettingsWindow.Version not found.' }
    [IO.File]::WriteAllText($sw, ($txt -replace ('Version = "' + $curRx + '"'), ('Version = "' + $newVer + '"')), $utf8)
    $csc = Join-Path $repo 'tools\roslyn\tasks\net472\csc.exe'
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$fake\build.ps1" -Compiler $csc -OutputDirectory "$run\fake-dist" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'fake next-version build failed' }
    Check ((Hash "$run\fake-dist\ClearDesk.exe") -ne (Hash "$BuildDirectory\ClearDesk.exe")) 'fake next-version build differs from the current build'

    $sideFiles = 'ClearDesk.exe.config', 'ClearDeskShell.dll', 'ClearDeskShell.msix', 'ClearDeskSign.exe', 'enable-menu.ps1', 'disable-menu.ps1'
    function New-Pkg([string]$version, [string]$exeSource, [hashtable]$extra, [string]$work, [string]$tamper) {
        $pk = Join-Path $work "ClearDesk-$version"; New-Item -ItemType Directory $pk -Force | Out-Null
        Copy-Item $exeSource "$pk\ClearDesk.exe"
        foreach ($f in $sideFiles) { if (Test-Path "$BuildDirectory\$f") { Copy-Item "$BuildDirectory\$f" $pk } }
        if (Test-Path "$BuildDirectory\taskbar") { Copy-Item "$BuildDirectory\taskbar" "$pk\taskbar" -Recurse }
        foreach ($k in $extra.Keys) { $p = Join-Path $pk $k; New-Item -ItemType Directory (Split-Path $p) -Force | Out-Null; [IO.File]::WriteAllText($p, $extra[$k], $utf8) }
        $rels = Get-ChildItem $pk -Recurse -File | ForEach-Object { $_.FullName.Substring($pk.Length + 1).Replace('\', '/') } | Where-Object { $_ -ne 'SHA256SUMS.txt' }
        $sorted = [string[]]$rels; [Array]::Sort($sorted, [StringComparer]::Ordinal)
        $lines = foreach ($r in $sorted) { (Hash (Join-Path $pk $r.Replace('/', '\'))).ToLowerInvariant() + '  ' + $r }
        [IO.File]::WriteAllText("$pk\SHA256SUMS.txt", ($lines -join "`r`n") + "`r`n", $utf8)
        if ($tamper) { [IO.File]::WriteAllText((Join-Path $pk $tamper), 'TAMPERED AFTER CHECKSUMS', $utf8) }
        $zip = Join-Path $work "ClearDesk-$version.zip"; if (Test-Path $zip) { Remove-Item $zip }
        $fs = [IO.File]::Open($zip, 'CreateNew'); $a = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
        Get-ChildItem $pk -Recurse -File | ForEach-Object { $rel = "ClearDesk-$version/" + $_.FullName.Substring($pk.Length + 1).Replace('\', '/'); [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($a, $_.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) }
        $a.Dispose(); $fs.Dispose()
        return @{ Dir = $pk; Zip = $zip }
    }
    function Publish($zip, $out, $version) {
        $notes = "$run\notes-$version.md"; [IO.File]::WriteAllText($notes, ("# ClearDesk $version`r`n`r`n## test`r`n`r`n- change one **bold** " + '`code`' + "`r`n- change two`r`n"), $utf8)
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish-update.ps1') -Zip $zip -Notes $notes -KeyDir "$run\keys" -OutDir $out -Date 2026-11-01 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "publish-update.ps1 failed for $zip" }
    }

    # the 2.1.0 package a user would have unzipped
    $old = New-Pkg $curVer "$BuildDirectory\ClearDesk.exe" @{ 'old-only.txt' = 'only in the old release'; 'a-first.txt' = 'old first'; 'data-file.txt' = 'old data' } "$run\old"
    $srvDir = "$run\srv"; New-Item -ItemType Directory $srvDir | Out-Null
    Copy-Item "$BuildDirectory\ClearDesk.exe", "$BuildDirectory\ClearDesk.exe.config" $srvDir        # the server runs from its own copy so the scratch install can be replaced

    $script:servers = @()
    function Free-Port { $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }
    function Start-Server($www, $cut) {
        $port = Free-Port; $args2 = @('--update-test-server', $www, $port); if ($cut) { $args2 += $cut }
        $p = Start-Process "$srvDir\ClearDesk.exe" -ArgumentList $args2 -WindowStyle Hidden -PassThru
        $script:servers += $p
        for ($i = 0; $i -lt 100 -and -not (Test-Path "$www\server-ready.txt"); $i++) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path "$www\server-ready.txt")) { throw 'test server did not start' }
        return @{ Port = $port; Proc = $p; Log = "$(Split-Path $www)\server-log-$port.txt" }
    }
    function Stop-All { foreach ($p in $script:servers) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }; $script:servers = @()
        Get-Process ClearDesk -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$run*" } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue }

    # one scenario: fresh scratch install of 2.1.0, optional sources, run the app, wait for its report
    function Run-App($name, $sources, [scriptblock]$beforeRun, [int]$timeout = 90) {
        $root = "$run\$name"; $inst = "$root\ClearDesk-$curVer"; New-Item -ItemType Directory $root | Out-Null
        Copy-Item $old.Dir $inst -Recurse
        New-Item -ItemType Directory "$inst\preview-data" -Force | Out-Null
        $cfg = @{ ConfigVersion = 5; Clock = $false; Performance = $false; Dock = $false; Taskbar = $false; Icons = $false; Animation = $false; Weather = $false; WeatherOnline = $false; Pins = @(); LaunchpadItems = @(); Widgets = @{}; Boxes = @(@{ Id = 'e2e'; Title = 'e2e'; X = 20; Y = 20; Width = 300; Height = 200; Desktop = $true; Enabled = $false; Collapsed = $false; Material = 'liquid'; Items = @(); Excluded = @() }) }
        $cfg | ConvertTo-Json -Depth 8 | Set-Content "$inst\preview-data\settings.json" -Encoding UTF8
        @{ sources = @($sources); github = $false } | ConvertTo-Json | Set-Content "$inst\preview-data\update-source.json" -Encoding UTF8
        [IO.File]::WriteAllText("$inst\my-notes.txt", 'user file', $utf8)
        $before = @{ Settings = (Hash "$inst\preview-data\settings.json"); Exe = (Hash "$inst\ClearDesk.exe") }
        $hook = $null; if ($beforeRun) { $hook = & $beforeRun $inst }
        $p = Start-Process "$inst\ClearDesk.exe" -ArgumentList '--preview', '--update-e2e' -WorkingDirectory $inst -WindowStyle Hidden -PassThru
        $done = "$inst\preview-data\e2e-done.txt"; $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt $timeout -and -not (Test-Path $done)) { Start-Sleep -Milliseconds 300 }
        $text = if (Test-Path $done) { [IO.File]::ReadAllText($done) } else { 'TIMEOUT' }
        Start-Sleep -Milliseconds 1500
        if ($hook) { try { $hook.Dispose() } catch { } }
        Get-Process ClearDesk -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$run\$name*" } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue
        return @{ Inst = $inst; Text = $text; Before = $before; Root = $root }
    }
    function Leftovers($inst) { @(Get-ChildItem $inst -Recurse -Filter '*.cdold-*' -ErrorAction SilentlyContinue).Count }

    # ================= S1: happy path 2.1.0 -> 2.1.1, first source dead, download cut once and resumed
    $new = New-Pkg $newVer "$run\fake-dist\ClearDesk.exe" @{ 'new-only.txt' = 'added by 2.1.1'; 'a-first.txt' = 'new first'; 'data-file.txt' = 'new data' } "$run\new"
    $www1 = "$run\s1www"; Publish $new.Zip $www1 $newVer
    $zipSize = (Get-Item "$www1\downloads\ClearDesk-$newVer.zip").Length
    Check ((Get-Content "$www1\versions.json" -Raw) -match '"signature": "[A-Za-z0-9+/=]{80,}"') 'publish-update.ps1 wrote a signed versions.json'
    Check ((Get-Content "$www1\release-notes\$newVer.html" -Raw -Encoding UTF8) -match '<li>change one <strong>bold</strong> <code>code</code>') 'publish-update.ps1 wrote the release-notes page'
    $srvV = Start-Server $www1 0
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish-update.ps1') -Verify "http://127.0.0.1:$($srvV.Port)" -PublicKeyFile "$run\keys\cleardesk-update-public.key" | Out-Null
    Check ($LASTEXITCODE -eq 0) 'publish-update.ps1 -Verify confirms the served manifest signature, size and SHA-256'
    $srvV.Proc.Kill()
    $srv1 = Start-Server $www1 ([int]($zipSize / 3))
    $deadPort = Free-Port
    $s1 = Run-App 's1' @("http://127.0.0.1:$deadPort/versions.json", "http://127.0.0.1:$($srv1.Port)/versions.json")
    $i = $s1.Inst
    Check ($s1.Text -match 'phase=after' -and $s1.Text -match ('version=' + $newRx)) "S1 the app restarted as 2.1.1 after the update ($(($s1.Text -split "`n" | Select -First 2) -join ' '))"
    Check ((Hash "$i\ClearDesk.exe") -eq (Hash "$run\fake-dist\ClearDesk.exe")) 'S1 ClearDesk.exe is the new build'
    Check ((Get-Content "$i\new-only.txt" -Raw) -eq 'added by 2.1.1' -and (Get-Content "$i\data-file.txt" -Raw) -eq 'new data' -and (Get-Content "$i\a-first.txt" -Raw) -eq 'new first') 'S1 changed and new files were installed'
    Check (-not (Test-Path "$i\old-only.txt")) 'S1 a file removed in the new release was removed'
    # the app may still be saving settings.json (write-then-replace) right after it quits, so poll briefly
    $userOk = $false
    for ($w = 0; $w -lt 20 -and -not $userOk; $w++) {
        try { $userOk = ((Get-Content "$i\my-notes.txt" -Raw -ErrorAction Stop) -eq 'user file') -and ((Get-Content "$i\preview-data\settings.json" -Raw -ErrorAction Stop | ConvertFrom-Json).Boxes[0].Id -eq 'e2e') -and (Test-Path "$i\preview-data\update-source.json") } catch { $userOk = $false }
        if (-not $userOk) { Start-Sleep -Milliseconds 500 }
    }
    Check $userOk 'S1 user files and settings were not touched'
    Check ((Leftovers $i) -eq 0 -and -not (Test-Path "$i\preview-data\update\journal.json")) 'S1 no leftover .cdold files or journal'
    Check ((Hash "$i\preview-data\update\backup\$curVer\ClearDesk.exe") -eq $s1.Before.Exe) 'S1 the old ClearDesk.exe was backed up'
    $rf = "$i\preview-data\update-result.json"; for ($w = 0; $w -lt 40 -and -not (Test-Path $rf); $w++) { Start-Sleep -Milliseconds 500 }
    Check ((Test-Path $rf) -and ([IO.File]::ReadAllText($rf) -match '"ok":true')) 'S1 updater result reports success'
    $log1 = Get-Content $srv1.Log -Raw
    Check ($log1 -match ('GET downloads/ClearDesk-' + $newRx + '\.zip \[bytes=\d+-') -and ($log1 -split "`n" | ? { $_ -match 'GET downloads' }).Count -ge 2) 'S1 the interrupted download resumed with an HTTP Range request'
    Check ((Get-Content "$i\preview-data\e2e-handover.txt" -Raw) -match 'newer=True') 'S1 the first run found the update through the second source after the dead one'
    $newExeHash = Hash "$i\ClearDesk.exe"
    Stop-All

    # ================= S2: package corrupted in transit (manifest signed for the good file)
    $www2 = "$run\s2www"; Publish $new.Zip $www2 $newVer
    foreach ($z in "$www2\downloads\ClearDesk-$newVer.zip", "$www2\downloads\ClearDesk-latest.zip") { $b = [IO.File]::ReadAllBytes($z); $b[[int]($b.Length / 2)] = $b[[int]($b.Length / 2)] -bxor 0xFF; [IO.File]::WriteAllBytes($z, $b) }
    $srv2 = Start-Server $www2 0
    $s2 = Run-App 's2' @("http://127.0.0.1:$($srv2.Port)/versions.json")
    Check ($s2.Text -match 'phase=install-failed' -and $s2.Text -match 'SHA256') "S2 a corrupted download is refused ($(($s2.Text -split "`n" | ? { $_ -like 'install-error*' }) -join ''))"
    Check ((Hash "$($s2.Inst)\ClearDesk.exe") -eq $s2.Before.Exe -and (Leftovers $s2.Inst) -eq 0 -and -not (Test-Path "$($s2.Inst)\new-only.txt")) 'S2 the install folder is untouched'
    Check (-not (Test-Path "$($s2.Inst)\preview-data\update\download\ClearDesk-$newVer.zip")) 'S2 the corrupted file was discarded'
    Stop-All

    # ================= S3: manifest tampered / unsigned / replayed older release
    $www3 = "$run\s3www"; Publish $new.Zip $www3 $newVer
    $vj = Get-Content "$www3\versions.json" -Raw
    [IO.File]::WriteAllText("$www3\versions.json", ($vj -replace ('"latest": "' + $newRx + '"'), ('"latest": "' + $lieVer + '"')), $utf8)   # lie about the version, keep the old signature
    $srv3 = Start-Server $www3 0
    $s3 = Run-App 's3a' @("http://127.0.0.1:$($srv3.Port)/versions.json")
    Check ($s3.Text -match 'phase=check-only' -and $s3.Text -match 'newer=False' -and $s3.Text -match '签名无效') 'S3 a manifest edited after signing (version bumped) is rejected'
    Check ((Hash "$($s3.Inst)\ClearDesk.exe") -eq $s3.Before.Exe) 'S3 nothing was installed'
    Stop-All
    [IO.File]::WriteAllText("$www3\versions.json", ($vj -replace '"signature": "[^"]+"', '"signature": ""'), $utf8)
    $srv3 = Start-Server $www3 0
    $s3b = Run-App 's3b' @("http://127.0.0.1:$($srv3.Port)/versions.json")
    Check ($s3b.Text -match 'newer=False' -and $s3b.Text -match '没有签名') 'S3 an unsigned manifest is rejected'
    Stop-All
    $www3c = "$run\s3cwww"; $oldPkg = New-Pkg '2.0.9' "$BuildDirectory\ClearDesk.exe" @{ 'x.txt' = 'x' } "$run\older"; Publish $oldPkg.Zip $www3c '2.0.9'
    $srv3 = Start-Server $www3c 0
    $s3c = Run-App 's3c' @("http://127.0.0.1:$($srv3.Port)/versions.json")
    Check ($s3c.Text -match 'newer=False' -and $s3c.Text -match 'latest=2\.0\.9') 'S3 a validly signed but OLDER release is never offered (no downgrade)'
    Stop-All

    # ================= S4: validly signed zip whose content does not match its own SHA256SUMS
    $bad = New-Pkg $newVer "$run\fake-dist\ClearDesk.exe" @{ 'new-only.txt' = 'added by 2.1.1'; 'a-first.txt' = 'new first'; 'data-file.txt' = 'new data' } "$run\bad" 'new-only.txt'
    $www4 = "$run\s4www"; Publish $bad.Zip $www4 $newVer
    $srv4 = Start-Server $www4 0
    $s4 = Run-App 's4' @("http://127.0.0.1:$($srv4.Port)/versions.json")
    Check ($s4.Text -match 'phase=install-failed' -and $s4.Text -match '校验失败') 'S4 a package whose files do not match its checksum list is refused before anything is replaced'
    Check ((Hash "$($s4.Inst)\ClearDesk.exe") -eq $s4.Before.Exe -and (Leftovers $s4.Inst) -eq 0 -and -not (Test-Path "$($s4.Inst)\new-only.txt")) 'S4 the install folder is untouched'
    Stop-All

    # ================= S5: a file is locked while replacing -> rollback, old version restarts
    $srv5 = Start-Server $www1 0
    $s5 = Run-App 's5' @("http://127.0.0.1:$($srv5.Port)/versions.json") { param($inst) [IO.File]::Open("$inst\data-file.txt", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
    Check ($s5.Text -match 'phase=after' -and $s5.Text -match ('version=' + $curRx)) 'S5 after a failed swap the OLD version is running again'
    Check ($s5.Text -match 'rolledBack\\?":true' -and $s5.Text -match 'ok\\?":false') "S5 the updater reports a rolled-back failure ($(($s5.Text -split "`n" | ? { $_ -like 'result*' }) -join '' | % { $_.Substring(0, [Math]::Min(160, $_.Length)) }))"
    Check ((Hash "$($s5.Inst)\ClearDesk.exe") -eq $s5.Before.Exe -and (Get-Content "$($s5.Inst)\a-first.txt" -Raw) -eq 'old first' -and (Get-Content "$($s5.Inst)\data-file.txt" -Raw) -eq 'old data' -and (Test-Path "$($s5.Inst)\old-only.txt") -and -not (Test-Path "$($s5.Inst)\new-only.txt")) 'S5 every file is exactly as before the attempt'
    Check ((Leftovers $s5.Inst) -eq 0 -and -not (Test-Path "$($s5.Inst)\preview-data\update\journal.json")) 'S5 no leftovers after the rollback'
    Stop-All

    # ================= S6: the new version crashes right after start -> rollback
    $crash = New-Pkg $newVer "$env:WINDIR\System32\find.exe" @{ 'new-only.txt' = 'crashy' } "$run\crash"
    $www6 = "$run\s6www"; Publish $crash.Zip $www6 $newVer
    $srv6 = Start-Server $www6 0
    $s6 = Run-App 's6' @("http://127.0.0.1:$($srv6.Port)/versions.json")
    Check ($s6.Text -match 'phase=after' -and $s6.Text -match ('version=' + $curRx) -and $s6.Text -match '立即退出') 'S6 a new version that exits immediately is rolled back and the old one restarts'
    Check ((Hash "$($s6.Inst)\ClearDesk.exe") -eq $s6.Before.Exe -and (Leftovers $s6.Inst) -eq 0 -and -not (Test-Path "$($s6.Inst)\new-only.txt")) 'S6 files restored'
    Stop-All

    # ================= S7: signed updateUrl in the manifest redirects later checks to a new domain
    $www7 = "$run\s7www"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish-update.ps1') -Zip $old.Zip -KeyDir "$run\keys" -OutDir $www7 -UpdateUrl 'http://cleardesk.example.test/' -Mirrors 'http://mirror.example.test/' | Out-Null
    $srv7 = Start-Server $www7 0
    $s7 = Run-App 's7' @("http://127.0.0.1:$($srv7.Port)/versions.json")
    Check ($s7.Text -match 'phase=check-only' -and $s7.Text -match 'redirect=http://cleardesk\.example\.test/versions\.json') 'S7 updateUrl from a signed manifest is remembered as the new update source'
    Check ((Get-Content "$($s7.Inst)\preview-data\settings.json" -Raw) -match 'cleardesk\.example\.test') 'S7 the new source is saved in the settings (no new build needed)'
    Stop-All

    # ---- nothing outside the scratch folders changed
    $runKeyAfter = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).ClearDesk
    Check ($runKeyAfter -eq $runKey) 'the real autostart entry was not touched by any test'
    $results.Add("INFO: scenarios run in $run on loopback only; signing used a throw-away key; the user's installs and the real update key were not used.")
}
catch { if (-not ($results | Where-Object { $_ -like 'FAIL:*' })) { $results.Add("FAIL: $($_.Exception.Message)") }; Write-Host "ERROR: $($_.Exception.Message)"; $failed = $true }
finally {
    try { Stop-All } catch { }
    Remove-Item Env:CLEARDESK_UPDATE_PUBKEY -ErrorAction SilentlyContinue
    Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'ClearDeskUpdate-*' -ErrorAction SilentlyContinue | Where-Object { $_.CreationTime -ge $startedAt } | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    $out = Join-Path $run 'update-e2e-test.txt'; [IO.File]::WriteAllLines($out, $results, $utf8)
    if (-not $Keep -and -not $failed) { foreach ($d in Get-ChildItem $run -Directory) { Remove-Item $d.FullName -Recurse -Force -ErrorAction SilentlyContinue } }
}
if ($failed) { throw "Update end-to-end test failed; see $out" }
Write-Host "Passed. Results: $out"
