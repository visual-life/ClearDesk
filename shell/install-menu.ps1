# 安装 Windows 11 一级桌面右键菜单“ClearDesk 新建组件”。
# 需要同目录：ClearDesk.exe、ClearDeskShell.dll、ClearDeskShell.msix、ClearDeskShell.cer。
# 证书需要放入本机“受信任人”存储；不是管理员时会弹出一次 UAC。
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$cer = Join-Path $dir 'ClearDeskShell.cer'; $msix = Join-Path $dir 'ClearDeskShell.msix'
foreach ($f in 'ClearDesk.exe','ClearDeskShell.dll','ClearDeskShell.msix','ClearDeskShell.cer') { if (-not (Test-Path (Join-Path $dir $f))) { throw "缺少文件：$f" } }
$thumb = (New-Object Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) {
  $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($admin) { Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null }
  else {
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',"Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) { throw '未能信任证书（UAC 被取消？）。' }
  }
}
Get-AppxPackage -Name ClearDesk.ShellMenu | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path $msix -ExternalLocation $dir
New-Item -Path HKCU:\Software\ClearDesk -Force | Out-Null
Set-ItemProperty HKCU:\Software\ClearDesk -Name ModernMenu -Value 1 -Type DWord
if (-not $Quiet) { Write-Host '已安装。在桌面空白处右键即可看到“ClearDesk 新建组件”。' }
