<#
 ClearDesk update publisher - run on the maintainer's PC (the signing key never leaves it).

 Publish a release:
   powershell -ExecutionPolicy Bypass -File publish-update.ps1 -Zip C:\work\ClearDesk-2.1.1.zip -Notes C:\work\readme-2.1.1.md
 Optional: -Version 2.1.1  -Date 2026-11-01  -UpdateUrl https://cleardesk.example/  -Mirrors https://m1/,https://m2/
           -KeyDir C:\work\update-keys  -OutDir C:\work\server-setup\publish\2.1.1  -Github https://github.com/visual-life/ClearDesk/releases
 What it does: checks the zip layout, computes size + SHA-256, signs (ECDSA P-256 / SHA-256, private key from -KeyDir),
 verifies its own signature, and writes a ready-to-upload folder:
     <OutDir>\downloads\ClearDesk-<ver>.zip, ClearDesk-latest.zip
     <OutDir>\release-notes\<ver>.html
     <OutDir>\versions.json                (upload this LAST)
 It does NOT upload anything. It prints the scp / upload steps.

 Check what is live (no key needed):
   powershell -ExecutionPolicy Bypass -File publish-update.ps1 -Verify http://101.200.206.248
#>
param(
    [string]$Zip, [string]$Notes, [string]$Version, [string]$Date, [string]$UpdateUrl = '', [string[]]$Mirrors = @(),
    [string]$KeyDir = 'C:\work\update-keys', [string]$OutDir, [string]$Github = 'https://github.com/visual-life/ClearDesk/releases',
    [string]$Verify, [string]$PublicKeyFile, [string]$Server = 'root@101.200.206.248', [string]$Root = '/var/www/cleardesk'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem, System.Core
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-Sha256([string]$path) { $s = [IO.File]::OpenRead($path); try { ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)).Replace('-', '')) } finally { $s.Dispose() } }
function Get-Payload($m) {
    # must stay byte-identical to UpdateManifest.SignedPayload() in ClearDesk.exe
    "cleardesk-update-v1`nversion=$($m.latest)`nfilename=$($m.filename)`nsize=$($m.size)`nsha256=$($m.sha256.ToLowerInvariant())`nupdateUrl=$($m.updateUrl)`nmirrors=$(($m.mirrors) -join ',')`n"
}
function Test-Signature($m, [string]$pubB64) {
    $key = [Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($pubB64), [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    try { $ec = New-Object Security.Cryptography.ECDsaCng($key); $ec.VerifyData($utf8.GetBytes((Get-Payload $m)), [Convert]::FromBase64String($m.signature), [Security.Cryptography.HashAlgorithmName]::SHA256) } finally { $key.Dispose() }
}
function Esc([string]$s) {
    $sb = New-Object Text.StringBuilder
    foreach ($c in $s.ToCharArray()) {
        switch ($c) { '"' { [void]$sb.Append('\"') } '\' { [void]$sb.Append('\\') } "`n" { [void]$sb.Append('\n') } "`r" { } "`t" { [void]$sb.Append('\t') } default { if ([int]$c -lt 32) { [void]$sb.Append(('\u{0:x4}' -f [int]$c)) } else { [void]$sb.Append($c) } } }
    }
    $sb.ToString()
}

# ------------------------------------------------------------------ verify mode
if ($Verify) {
    $base = $Verify.TrimEnd('/')
    $pubFile = if ($PublicKeyFile) { $PublicKeyFile } else { Join-Path $KeyDir 'cleardesk-update-public.key' }
    $pub = (Get-Content -LiteralPath $pubFile -Raw).Trim()
    $m = Invoke-RestMethod -Uri "$base/versions.json" -UseBasicParsing
    if (-not $m.mirrors) { $m | Add-Member -Force NoteProperty mirrors @() }
    if (-not $m.updateUrl) { $m | Add-Member -Force NoteProperty updateUrl '' }
    $okSig = $false; try { $okSig = Test-Signature $m $pub } catch { }
    Write-Host ("versions.json : latest {0}, size {1}, signature {2}" -f $m.latest, $m.size, $(if ($okSig) { 'VALID' } else { 'INVALID / missing' }))
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('cd-verify-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest -Uri "$base/$($m.file)" -OutFile $tmp -UseBasicParsing
        $h = Get-Sha256 $tmp; $len = (Get-Item $tmp).Length
        Write-Host ("zip           : {0} bytes, sha256 {1} -> {2}" -f $len, $h, $(if ($h -eq $m.sha256.ToUpperInvariant() -and $len -eq $m.size) { 'MATCHES manifest' } else { 'DOES NOT MATCH' }))
        if (-not $okSig -or $h -ne $m.sha256.ToUpperInvariant() -or $len -ne $m.size) { exit 1 }
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    exit 0
}

# ------------------------------------------------------------------ publish mode
if (-not $Zip) { throw 'Usage: publish-update.ps1 -Zip <ClearDesk-x.y.z.zip> [-Notes notes.md] ...  (see the header of this script)' }
$Zip = (Resolve-Path -LiteralPath $Zip).Path
$name = Split-Path $Zip -Leaf
if (-not $Version) { if ($name -match '^ClearDesk-(\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.\-]+)?)\.zip$') { $Version = $Matches[1] } else { throw "Cannot read the version from '$name'. Name the file ClearDesk-<version>.zip or pass -Version." } }
if ($name -ne "ClearDesk-$Version.zip") { throw "File name must be ClearDesk-$Version.zip (found $name)." }
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.\-]+)?$') { throw "Bad version '$Version'." }
if (-not $Date) { $Date = (Get-Date).ToString('yyyy-MM-dd') }
if (-not $OutDir) { $OutDir = "C:\work\server-setup\publish\$Version" }
$privFile = Join-Path $KeyDir 'cleardesk-update-private.key'; $pubFile = Join-Path $KeyDir 'cleardesk-update-public.key'
if (-not (Test-Path -LiteralPath $privFile)) { throw "No signing key in $KeyDir. Run new-update-key.ps1 first." }

# layout check: one top-level folder (or none) that holds ClearDesk.exe and SHA256SUMS.txt, forward-slash entries
$za = [IO.Compression.ZipFile]::OpenRead($Zip)
try {
    $names = @($za.Entries | ForEach-Object { $_.FullName })
    if ($names | Where-Object { $_.Contains('\') }) { throw 'The zip has backslash entries; rebuild it with forward slashes (pack script).' }
    $exe = @($names | Where-Object { $_ -match '^([^/]+/)?ClearDesk\.exe$' })
    if ($exe.Count -ne 1) { throw 'The zip must contain exactly one ClearDesk.exe, at the top level or in a single top-level folder.' }
    $prefix = $exe[0].Substring(0, $exe[0].Length - 'ClearDesk.exe'.Length)
    if ($names -notcontains ($prefix + 'SHA256SUMS.txt')) { throw 'The zip has no SHA256SUMS.txt next to ClearDesk.exe (the in-app updater requires it).' }
    if ($names | Where-Object { $_ -match '(^|/)\.\.(/|$)' -or $_.StartsWith('/') -or $_ -match '^[A-Za-z]:' }) { throw 'The zip contains unsafe paths.' }
    Write-Host "zip layout OK: $($names.Count) entries, payload folder '$prefix'"
} finally { $za.Dispose() }

$size = (Get-Item -LiteralPath $Zip).Length
$sha = Get-Sha256 $Zip
if ($Mirrors) { $Mirrors = @($Mirrors | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$m = [pscustomobject]@{ latest = $Version; filename = $name; size = $size; sha256 = $sha; updateUrl = $UpdateUrl.Trim(); mirrors = @($Mirrors) }

# changes (shown in the update dialog) = the top-level bullets of the notes file
$changes = @()
if ($Notes) {
    $md = Get-Content -LiteralPath $Notes -Raw -Encoding UTF8
    foreach ($line in ($md -split "`r?`n")) {
        if ($line -match '^[-*]\s+(.+)$') {
            $t = ($Matches[1] -replace '\*\*', '' -replace '`', '').Trim()
            if ($t.Length -gt 150) { $t = $t.Substring(0, 150) + '...' }
            if ($t) { $changes += $t }
        }
        if ($changes.Count -ge 12) { break }
    }
}

# sign
$privBlob = [Convert]::FromBase64String((Get-Content -LiteralPath $privFile -Raw).Trim())
$key = [Security.Cryptography.CngKey]::Import($privBlob, [Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
try { $ec = New-Object Security.Cryptography.ECDsaCng($key); $sig = [Convert]::ToBase64String($ec.SignData($utf8.GetBytes((Get-Payload $m)), [Security.Cryptography.HashAlgorithmName]::SHA256)) } finally { $key.Dispose(); [Array]::Clear($privBlob, 0, $privBlob.Length) }
$m | Add-Member NoteProperty signature $sig
if (-not (Test-Signature $m ((Get-Content -LiteralPath $pubFile -Raw).Trim()))) { throw 'Self-check failed: the signature does not verify with the public key.' }

# output folder
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path "$OutDir\downloads", "$OutDir\release-notes" -Force | Out-Null
Copy-Item -LiteralPath $Zip -Destination "$OutDir\downloads\$name"
Copy-Item -LiteralPath $Zip -Destination "$OutDir\downloads\ClearDesk-latest.zip"

$j = New-Object Text.StringBuilder
[void]$j.AppendLine('{')
[void]$j.AppendLine('  "latest": "' + (Esc $Version) + '",')
[void]$j.AppendLine('  "date": "' + $Date + '",')
[void]$j.AppendLine('  "filename": "' + (Esc $name) + '",')
[void]$j.AppendLine('  "file": "downloads/' + (Esc $name) + '",')
[void]$j.AppendLine('  "alias": "downloads/ClearDesk-latest.zip",')
[void]$j.AppendLine('  "size": ' + $size + ',')
[void]$j.AppendLine('  "sha256": "' + $sha + '",')
[void]$j.AppendLine('  "notes": "release-notes/' + (Esc $Version) + '.html",')
[void]$j.AppendLine('  "github": "' + (Esc $Github) + '",')
if ($UpdateUrl) { [void]$j.AppendLine('  "updateUrl": "' + (Esc $UpdateUrl.Trim()) + '",') }
if ($Mirrors.Count -gt 0) { [void]$j.AppendLine('  "mirrors": [' + (($Mirrors | ForEach-Object { '"' + (Esc $_) + '"' }) -join ', ') + '],') }
if ($changes.Count -gt 0) { [void]$j.AppendLine('  "changes": [' + (($changes | ForEach-Object { '"' + (Esc $_) + '"' }) -join ', ') + '],') }
[void]$j.AppendLine('  "signature": "' + $sig + '"')
[void]$j.AppendLine('}')
[IO.File]::WriteAllText("$OutDir\versions.json", $j.ToString().Replace("`r`n", "`n"), $utf8)

# release-notes html in the site's style (very small markdown subset: # ## - **bold** `code`)
function Html([string]$s) { $e = [Net.WebUtility]::HtmlEncode($s); $e = [regex]::Replace($e, '\*\*(.+?)\*\*', '<strong>$1</strong>'); [regex]::Replace($e, '`(.+?)`', '<code>$1</code>') }
$body = New-Object Text.StringBuilder; $inList = $false
if ($Notes) {
    foreach ($line in ((Get-Content -LiteralPath $Notes -Raw -Encoding UTF8) -split "`r?`n")) {
        if ($line -match '^\s*[-*]\s+(.+)$') { if (-not $inList) { [void]$body.AppendLine('<ul>'); $inList = $true }; [void]$body.AppendLine('<li>' + (Html $Matches[1]) + '</li>'); continue }
        if ($inList) { [void]$body.AppendLine('</ul>'); $inList = $false }
        if ($line -match '^#\s+') { continue }
        elseif ($line -match '^##+\s+(.+)$') { [void]$body.AppendLine('<h2>' + (Html $Matches[1]) + '</h2>') }
        elseif ($line.Trim()) { [void]$body.AppendLine('<p>' + (Html $line.Trim()) + '</p>') }
    }
    if ($inList) { [void]$body.AppendLine('</ul>') }
} else { [void]$body.AppendLine('<p>本次更新的详细说明稍后补充。</p>') }
$html = @"
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>ClearDesk $Version 发布说明</title>
<link rel="icon" href="../favicon.ico">
<link rel="stylesheet" href="../css/style.css">
</head>
<body>
<header class="nav scrolled"><div class="nav-inner"><a class="brand" href="../index.html"><img src="../img/icon-256.png" width="26" height="26" alt=""><span>ClearDesk</span></a><a class="btn btn-sm" style="margin-left:auto" href="../downloads/ClearDesk-latest.zip" download="ClearDesk-$Version.zip">下载</a></div></header>
<main class="rn">
<p class="fine"><a href="../index.html#history">← 下载版本历史</a></p>
<h1>ClearDesk $Version 发布说明</h1>
<p class="fine">发布日期：$Date</p>
$($body.ToString())<p class="fine" style="margin-top:40px"><a href="../index.html#install">安装与使用</a></p>
</main>
</body>
</html>
"@
[IO.File]::WriteAllText("$OutDir\release-notes\$Version.html", $html, $utf8)

Write-Host ''
Write-Host "Signed release $Version  size=$size  sha256=$sha"
Write-Host "Self-check: signature verifies with $pubFile"
Write-Host "Output folder: $OutDir"
Write-Host ''
Write-Host 'Upload (zips first, versions.json LAST so clients never see a manifest whose zip is missing):'
Write-Host "  scp `"$OutDir\downloads\$name`" `"$OutDir\downloads\ClearDesk-latest.zip`" ${Server}:$Root/downloads/"
Write-Host "  scp `"$OutDir\release-notes\$Version.html`" ${Server}:$Root/release-notes/"
Write-Host "  scp `"$OutDir\versions.json`" ${Server}:$Root/versions.json"
Write-Host "  ssh $Server `"chmod a+r $Root/downloads/* $Root/release-notes/* $Root/versions.json`""
Write-Host '  (no ssh? upload the same three things with the Alibaba Cloud Workbench file manager, or any SFTP client, into the same folders)'
Write-Host ''
Write-Host 'Then check what is live (from this PC):'
Write-Host "  powershell -ExecutionPolicy Bypass -File $PSCommandPath -Verify http://101.200.206.248"
