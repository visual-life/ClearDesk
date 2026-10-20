param(
    [ValidateSet('Self','Regression','PopupBenchmark')][string]$Suite = 'Self',
    [string]$BuildDirectory,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $repoRoot 'dist' }
$runDirectory = Join-Path $repoRoot ('artifacts\' + $Suite + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $runDirectory 'preview-data') -Force | Out-Null
foreach ($name in @('ClearDesk.exe','ClearDesk.exe.config')) { Copy-Item -LiteralPath (Join-Path $BuildDirectory $name) -Destination $runDirectory }
$pins = @()
foreach ($folder in @([Environment]::GetFolderPath('DesktopDirectory'),[Environment]::GetFolderPath('CommonDesktopDirectory'))) {
    if (Test-Path -LiteralPath $folder) { $pins += @(Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File | Select-Object -ExpandProperty FullName) }
}
$settings = @{
    ConfigVersion=4; Clock=$false; Performance=$false; Dock=$true; Taskbar=$false; Icons=$false; Animation=$false; Weather=$false; WeatherOnline=$false
    Pins=@($pins | Select-Object -Unique -First 2); LaunchpadItems=@(); Widgets=@{}
    Boxes=@(@{Id='regression-organizer';Title='Desktop test';X=20;Y=20;Width=520;Height=360;Desktop=$true;Enabled=$true;Collapsed=$false;Material='liquid';Items=@();Excluded=@()})
}
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'preview-data\settings.json') -Encoding UTF8
$arguments = @{Self='--self-test';Regression='--regression-test';PopupBenchmark='--popup-benchmark'}
$logs = @{Self='self-test.txt';Regression='regression-test.txt';PopupBenchmark='popup-benchmark.json'}
$process = Start-Process -FilePath (Join-Path $runDirectory 'ClearDesk.exe') -ArgumentList $arguments[$Suite] -WorkingDirectory $runDirectory -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); throw "Test timed out; inspect $runDirectory" }
$process.Refresh()
$logPath = Join-Path $runDirectory $logs[$Suite]
if (-not (Test-Path -LiteralPath $logPath)) { throw "Test did not produce its result file; inspect $runDirectory" }
$result = Get-Content -LiteralPath $logPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or $result -match '(?m)^FAIL:') { throw "Test failed; inspect $logPath" }
if ($Suite -eq 'PopupBenchmark') { $parsed = $result | ConvertFrom-Json; if (@($parsed.Trials).Count -ne 8) { throw 'Incomplete benchmark results.' } }
Write-Host "Passed. Results: $logPath"
