# ClearDesk：关闭并卸载 Windows 11 桌面右键一级菜单，移除本机证书（移除受信任证书需一次 UAC）。
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'ClearDesk\shell'
New-Item -ItemType Directory $data -Force | Out-Null
$statusFile = Join-Path $data 'menu-status.txt'
function Done([int]$code, [string]$message) { [IO.File]::WriteAllText($statusFile, "$code`n$message", (New-Object Text.UTF8Encoding $false)); exit $code }
try {
  $subject = 'CN=ClearDesk Local Shell Menu'
  Get-AppxPackage -Name ClearDesk.ShellMenu | Remove-AppxPackage -ErrorAction SilentlyContinue
  New-Item -Path HKCU:\Software\ClearDesk -Force | Out-Null
  Set-ItemProperty HKCU:\Software\ClearDesk -Name ModernMenu -Value 0 -Type DWord
  $note = ''
  if (Get-ChildItem Cert:\LocalMachine\TrustedPeople | ? Subject -eq $subject) {
    $cmd = "Get-ChildItem Cert:\LocalMachine\TrustedPeople | ? Subject -eq '$subject' | Remove-Item"
    try { Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',$cmd } catch { }
    if (Get-ChildItem Cert:\LocalMachine\TrustedPeople | ? Subject -eq $subject) { $note = '（受信任证书未移除：管理员授权被取消）' }
  }
  Get-ChildItem Cert:\CurrentUser\My | ? Subject -eq $subject | Remove-Item
  Remove-Item (Join-Path $data 'ClearDeskShell.msix'),(Join-Path $data 'ClearDeskShell.cer') -ErrorAction SilentlyContinue
  if (Get-AppxPackage -Name ClearDesk.ShellMenu) { Done 5 '程序包仍在。' }
  Done 0 "已关闭并卸载$note"
} catch { Done 1 $_.Exception.Message }
