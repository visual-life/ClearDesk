# 卸载 Windows 11 一级桌面右键菜单；证书移除需要管理员（可选）。
param([switch]$RemoveCertificate)
$ErrorActionPreference = 'Continue'
Get-AppxPackage -Name ClearDesk.ShellMenu | Remove-AppxPackage
Remove-ItemProperty HKCU:\Software\ClearDesk -Name ModernMenu -ErrorAction SilentlyContinue
if ($RemoveCertificate) {
  $cer = Join-Path $PSScriptRoot 'ClearDeskShell.cer'
  $thumb = (New-Object Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
  Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile','-Command',"Remove-Item 'Cert:\LocalMachine\TrustedPeople\$thumb'"
}
Write-Host '已卸载 Windows 11 一级菜单（经典菜单仍由 ClearDesk 设置控制）。'
