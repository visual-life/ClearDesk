param(
    [string]$Compiler,
    [string]$WindowsMetadata,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $framework)) { throw 'Windows x64 and .NET Framework 4.8 are required.' }
if (-not $Compiler) {
    $toolRoot = Join-Path $PSScriptRoot 'tools\roslyn'
    $Compiler = Join-Path $toolRoot 'tasks\net472\csc.exe'
    if (-not (Test-Path -LiteralPath $Compiler)) {
        New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
        $archive = Join-Path $toolRoot 'compiler.zip'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.8.0/microsoft.net.compilers.toolset.4.8.0.nupkg' -OutFile $archive
        $expected = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scripts\compiler.sha256') -Raw).Trim()
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Compiler package hash mismatch.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force
    }
}
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$refs = @('Accessibility.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xml.dll','Microsoft.CSharp.dll','System.Net.Http.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$refs += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll','UIAutomationClient.dll','UIAutomationTypes.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $framework 'WPF') $_) }
if (-not $WindowsMetadata) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
    $WindowsMetadata = Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.0\.\d+\.0$' -and [version]$_.Name -ge [version]'10.0.17763.0' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $WindowsMetadata) { throw 'Install Windows 10/11 SDK (17763 or later), or pass -WindowsMetadata with the path to Windows.winmd.' }
$refs += '/reference:' + $WindowsMetadata
foreach ($name in @('System.Runtime.dll','System.Runtime.WindowsRuntime.dll')) {
    $ref = Join-Path $framework $name
    if (-not (Test-Path -LiteralPath $ref)) { $ref = Join-Path (Join-Path $framework 'Facades') $name }
    $refs += '/reference:' + $ref
}
$sources = @('src','tests','third_party\lunar') | ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $_) -Filter '*.cs' -Recurse | Select-Object -ExpandProperty FullName }
& $Compiler /nologo /target:winexe /platform:x64 /optimize+ /deterministic+ /utf8output ('/out:' + (Join-Path $OutputDirectory 'ClearDesk.exe')) ('/win32manifest:' + (Join-Path $PSScriptRoot 'config\app.manifest')) ('/win32icon:' + (Join-Path $PSScriptRoot 'assets\ClearDesk.ico')) ('/resource:' + (Join-Path $PSScriptRoot 'assets\ClearDesk.ico') + ',ClearDesk.AppIcon.ico') $refs $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config\ClearDesk.exe.config') -Destination $OutputDirectory -Force
Write-Host "Built $OutputDirectory\ClearDesk.exe"
