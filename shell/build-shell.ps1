# Builds the Windows 11 modern context-menu package (ClearDeskShell.dll + ClearDeskShell.msix).
# Needs: Visual Studio C++ tools (vcvars64.bat) and Windows SDK 10.0.22000+ (makeappx, signtool).
param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'), [string]$Version = '2.0.1.0')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot; $repo = Split-Path -Parent $here
$work = Join-Path $repo 'artifacts\shell-build'; if (Test-Path $work) { Remove-Item $work -Recurse -Force }; New-Item -ItemType Directory $work | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$sdkBin = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Directory | ? { $_.Name -match '^10\.0\.\d+\.0$' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdkBin) { throw 'Windows SDK makeappx not found.' }
$makeappx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'; $signtool = Join-Path $sdkBin.FullName 'x64\signtool.exe'
$cmd = "`"$vcvars`" >nul && cl /nologo /O2 /MT /LD /EHsc /DUNICODE /D_UNICODE /W3 `"$here\ClearDeskShell.cpp`" /Fe:`"$work\ClearDeskShell.dll`" /Fo:`"$work\\`" /link /DEF:`"$here\ClearDeskShell.def`" shlwapi.lib ole32.lib advapi32.lib"
cmd /c $cmd; if ($LASTEXITCODE -ne 0) { throw 'DLL build failed.' }
# package layout
$pkg = Join-Path $work 'pkg'; New-Item -ItemType Directory (Join-Path $pkg 'Assets') -Force | Out-Null
(Get-Content (Join-Path $here 'AppxManifest.xml') -Raw -Encoding UTF8).Replace('Version="2.0.1.0"', "Version=`"$Version`"") | Set-Content (Join-Path $pkg 'AppxManifest.xml') -Encoding UTF8
Add-Type -AssemblyName System.Drawing
$src = [Drawing.Image]::FromFile((Join-Path $repo 'assets\ClearDesk.png'))
foreach ($p in @(@('StoreLogo',50),@('Square150x150Logo',150),@('Square44x44Logo',44))) {
  $b = New-Object Drawing.Bitmap $p[1],$p[1]; $g = [Drawing.Graphics]::FromImage($b); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($src,0,0,$p[1],$p[1]); $g.Dispose(); $b.Save((Join-Path $pkg "Assets\$($p[0]).png")); $b.Dispose() }
$src.Dispose()
$msix = Join-Path $work 'ClearDeskShell.msix'
& $makeappx pack /o /nv /d $pkg /p $msix | Out-Host; if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }
# signing certificate (kept in the builder's CurrentUser\My; only the public .cer ships)
$subject = 'CN=ClearDesk Local Shell Menu'
$cert = Get-ChildItem Cert:\CurrentUser\My | ? { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
if (-not $cert) { $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'ClearDesk shell menu (self-signed)' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') }
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Host; if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }
New-Item -ItemType Directory $OutputDirectory -Force | Out-Null
Copy-Item (Join-Path $work 'ClearDeskShell.dll'), $msix $OutputDirectory -Force
Export-Certificate -Cert $cert -FilePath (Join-Path $OutputDirectory 'ClearDeskShell.cer') | Out-Null
Copy-Item (Join-Path $here 'install-menu.ps1'), (Join-Path $here 'uninstall-menu.ps1') $OutputDirectory -Force
Write-Host "Built shell menu package in $OutputDirectory"
