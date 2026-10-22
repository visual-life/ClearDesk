# ClearDesk：启用 Windows 11 桌面右键一级菜单（本机生成证书，运行时签名，外部位置注册）。
# 由设置里的“一键启用”调用；-Repair 只在文件夹移动后重新注册，不弹 UAC。
param([switch]$Repair)
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$data = Join-Path $env:LOCALAPPDATA 'ClearDesk\shell'
New-Item -ItemType Directory $data -Force | Out-Null
$statusFile = Join-Path $data 'menu-status.txt'
function Done([int]$code, [string]$message) { [IO.File]::WriteAllText($statusFile, "$code`n$message", (New-Object Text.UTF8Encoding $false)); exit $code }
try {
  foreach ($f in 'ClearDesk.exe','ClearDeskShell.dll','ClearDeskShell.msix','ClearDeskSign.exe') { if (-not (Test-Path (Join-Path $dir $f))) { Done 6 "缺少文件：$f" } }
  $subject = 'CN=ClearDesk Local Shell Menu'; $friendly = 'ClearDesk shell menu (this PC)'
  $cert = Get-ChildItem Cert:\CurrentUser\My | ? { $_.Subject -eq $subject -and $_.FriendlyName -eq $friendly -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
  if (-not $cert) {
    if ($Repair) { Done 3 '本机证书不存在，请在设置里点“一键启用”。' }
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName $friendly -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -KeyAlgorithm RSA -KeyLength 2048 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
  }
  $thumb = $cert.Thumbprint
  $signed = Join-Path $data 'ClearDeskShell.msix'
  Copy-Item (Join-Path $dir 'ClearDeskShell.msix') $signed -Force
  $out = & (Join-Path $dir 'ClearDeskSign.exe') $signed $thumb 2>&1
  if ($LASTEXITCODE -ne 0) { Done 4 "签名失败：$out" }
  if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) {
    if ($Repair) { Done 3 '证书尚未被信任，请在设置里点“一键启用”。' }
    $cer = Join-Path $data 'ClearDeskShell.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    $cmd = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    try { Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',$cmd }
    catch { Done 2 '已取消管理员授权，未启用。' }
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) { Done 2 '证书未导入（管理员授权被取消或失败）。' }
  }
  Get-AppxPackage -Name ClearDesk.ShellMenu | Remove-AppxPackage -ErrorAction SilentlyContinue
  Add-AppxPackage -Path $signed -ExternalLocation $dir
  New-Item -Path HKCU:\Software\ClearDesk -Force | Out-Null
  Set-ItemProperty HKCU:\Software\ClearDesk -Name ModernMenu -Value 1 -Type DWord
  $p = Get-AppxPackage -Name ClearDesk.ShellMenu
  if (-not $p) { Done 5 '注册后未找到程序包。' }
  Done 0 "已启用：$dir"
} catch { Done 1 $_.Exception.Message }
