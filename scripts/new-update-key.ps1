<#
 ClearDesk update signing key (ECDSA P-256 / SHA-256).
 Creates   <KeyDir>\cleardesk-update-private.key   (SECRET - stays on this PC, never upload, never commit)
           <KeyDir>\cleardesk-update-public.key    (base64 public key, embedded into ClearDesk.exe)
 Usage:  powershell -ExecutionPolicy Bypass -File new-update-key.ps1 [-KeyDir C:\work\update-keys] [-Force]
 -Force replaces an existing key: every installed ClearDesk then rejects updates signed with the new key
 (the public key is compiled into the app), so only do that when the old key is lost or leaked.
#>
param([string]$KeyDir = 'C:\work\update-keys', [switch]$Force)
$ErrorActionPreference = 'Stop'
$priv = Join-Path $KeyDir 'cleardesk-update-private.key'
$pub  = Join-Path $KeyDir 'cleardesk-update-public.key'
if ((Test-Path -LiteralPath $priv) -and -not $Force) { throw "A key already exists in $KeyDir. Use -Force only if you really want to replace it." }
New-Item -ItemType Directory -Path $KeyDir -Force | Out-Null
$p = New-Object Security.Cryptography.CngKeyCreationParameters
$p.ExportPolicy = [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
$key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $p)
try {
    $privBlob = $key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    $pubBlob  = $key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
} finally { $key.Dispose() }
[IO.File]::WriteAllText($priv, [Convert]::ToBase64String($privBlob), [Text.Encoding]::ASCII)
[IO.File]::WriteAllText($pub,  [Convert]::ToBase64String($pubBlob),  [Text.Encoding]::ASCII)
# Only the current Windows user may read the private key.
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $KeyDir /inheritance:r /grant:r ("{0}:(OI)(CI)F" -f $me) | Out-Null
$sha = [Security.Cryptography.SHA256]::Create()
$fp = ([BitConverter]::ToString($sha.ComputeHash($pubBlob)).Replace('-', '')).Substring(0, 16)
Write-Host "Key created in $KeyDir"
Write-Host "Public key (embedded in ClearDesk.exe): $([Convert]::ToBase64String($pubBlob))"
Write-Host "Fingerprint: $fp"
Write-Host "Back up cleardesk-update-private.key somewhere offline; if it is lost you cannot ship updates to installed copies."
