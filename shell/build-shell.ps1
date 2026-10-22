# Builds the Windows 11 modern context-menu parts: ClearDeskShell.dll, an UNSIGNED ClearDeskShell.msix
# and ClearDeskSign.exe. End users sign the package on their own PC with a per-machine certificate
# (Settings > 一键启用 / enable-menu.ps1); no certificate or private key ships in the zip.
# Needs: Visual Studio C++ tools (vcvars64.bat) and Windows SDK 10.0.22000+ (makeappx) on the build PC only.
param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'), [string]$Version = '2.0.4.0')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot; $repo = Split-Path -Parent $here
$work = Join-Path $repo 'artifacts\shell-build'; if (Test-Path $work) { Remove-Item $work -Recurse -Force }; New-Item -ItemType Directory $work | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$sdkBin = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Directory | ? { $_.Name -match '^10\.0\.\d+\.0$' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdkBin) { throw 'Windows SDK makeappx not found.' }
$makeappx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
$cmd = "`"$vcvars`" >nul && cl /nologo /O2 /MT /LD /EHsc /DUNICODE /D_UNICODE /W3 `"$here\ClearDeskShell.cpp`" /Fe:`"$work\ClearDeskShell.dll`" /Fo:`"$work\\`" /link /DEF:`"$here\ClearDeskShell.def`" shlwapi.lib ole32.lib advapi32.lib && cl /nologo /O2 /MT /DUNICODE /D_UNICODE /W3 `"$here\ClearDeskSign.c`" /Fe:`"$work\ClearDeskSign.exe`" /Fo:`"$work\\`" /link crypt32.lib"
cmd /c $cmd; if ($LASTEXITCODE -ne 0) { throw 'native build failed.' }
$pkg = Join-Path $work 'pkg'; New-Item -ItemType Directory (Join-Path $pkg 'Assets') -Force | Out-Null
(Get-Content (Join-Path $here 'AppxManifest.xml') -Raw -Encoding UTF8) -creplace '(<Identity [^>]*? )Version="[0-9.]+"', ('$1Version="' + $Version + '"') | Set-Content (Join-Path $pkg 'AppxManifest.xml') -Encoding UTF8
Add-Type -AssemblyName System.Drawing
$src = [Drawing.Image]::FromFile((Join-Path $repo 'assets\ClearDesk.png'))
foreach ($p in @(@('StoreLogo',50),@('Square150x150Logo',150),@('Square44x44Logo',44))) {
  $b = New-Object Drawing.Bitmap $p[1],$p[1]; $g = [Drawing.Graphics]::FromImage($b); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($src,0,0,$p[1],$p[1]); $g.Dispose(); $b.Save((Join-Path $pkg "Assets\$($p[0]).png")); $b.Dispose() }
$src.Dispose()
$msix = Join-Path $work 'ClearDeskShell.msix'
& $makeappx pack /o /nv /d $pkg /p $msix | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }
New-Item -ItemType Directory $OutputDirectory -Force | Out-Null
foreach ($old in 'ClearDeskShell.cer','install-menu.ps1','uninstall-menu.ps1') { Remove-Item (Join-Path $OutputDirectory $old) -ErrorAction SilentlyContinue }
Copy-Item (Join-Path $work 'ClearDeskShell.dll'), (Join-Path $work 'ClearDeskSign.exe'), $msix, (Join-Path $here 'enable-menu.ps1'), (Join-Path $here 'disable-menu.ps1') $OutputDirectory -Force
Write-Host "Built shell menu parts (unsigned package) in $OutputDirectory"
