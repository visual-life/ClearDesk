param(
    [string]$SourceDirectory,
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repoRoot 'dist\taskbar' }
$metadata = Join-Path $repoRoot 'third_party\translucenttb'
$sourceHash = '73AC414E17710DE5765F2305BBBC1AAA4D88628C49D9B47A3159795D05F2F257'
if (-not $SourceDirectory) {
    $cache = Join-Path $repoRoot 'tools\translucenttb-2026.2'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $archive = Join-Path $cache 'portable.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/TranslucentTB/TranslucentTB/releases/download/2026.2/TranslucentTB-portable-x64.zip' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '0DBE8E0255C20E131CDE536DCD0AE490D45989A7D360E26D0150DFF1922AC420') { throw 'Official taskbar archive checksum mismatch.' }
    $SourceDirectory = Join-Path $cache 'expanded'
    Expand-Archive -LiteralPath $archive -DestinationPath $SourceDirectory -Force
    Invoke-WebRequest -UseBasicParsing -Uri 'https://codeload.github.com/TranslucentTB/TranslucentTB/zip/refs/tags/2026.2' -OutFile (Join-Path $SourceDirectory 'TranslucentTB-2026.2-source.zip')
}
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
foreach ($line in Get-Content -LiteralPath (Join-Path $metadata 'verified.sha256')) {
    if (-not $line.Trim()) { continue }
    $expected, $name = $line -split '  ', 2
    if (-not $name -or $name.IndexOfAny([char[]]'/\:') -ge 0) { throw 'Invalid manifest filename.' }
    if ((Get-FileHash -LiteralPath (Join-Path $SourceDirectory $name) -Algorithm SHA256).Hash -ne $expected) { throw "Component checksum mismatch: $name" }
}
$sourceArchive = Join-Path $SourceDirectory 'TranslucentTB-2026.2-source.zip'
if ((Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Upstream source archive checksum mismatch.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$files = @('ExplorerHooks.dll','ExplorerTAP.dll','ProgramLog.dll','TranslucentTB.exe','Xaml.dll','resources.pri','TranslucentTB-2026.2-source.zip')
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination $Destination -Force }
if (Test-Path -LiteralPath (Join-Path $SourceDirectory 'Assets')) { Copy-Item -LiteralPath (Join-Path $SourceDirectory 'Assets') -Destination $Destination -Recurse -Force }
foreach ($name in @('LICENSE.md','settings.json','verified.sha256')) { Copy-Item -LiteralPath (Join-Path $metadata $name) -Destination $Destination -Force }
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $Destination -Force
Write-Host "Verified optional taskbar component prepared in $Destination"
