[CmdletBinding()]
param([switch]$Check, [switch]$UseLocalProfiles, [string]$Preview, [switch]$PreviewMods, [string]$OutputName = 'Quper TruckKit.exe')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $csc)) { throw 'The .NET Framework 4.x Windows compiler is required.' }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName -or $OutputName -notlike '*.exe') { throw 'Specify an executable filename.' }
$out = Join-Path $dist $OutputName
$icon = Join-Path $root 'assets\truckkit.ico'
$references = @('System.Web.Extensions.dll', 'System.Windows.Forms.dll')
$references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'System.Xaml.dll') | ForEach-Object {
    $wpf = Join-Path (Join-Path $framework 'WPF') $_
    if (Test-Path -LiteralPath $wpf) { $wpf } else { Join-Path $framework $_ }
}
$resources = @(
    ('/resource:' + (Join-Path $root 'src\Theme.xaml') + ',Quper.Theme'),
    ('/resource:' + (Join-Path $root 'assets\truckkit-logo.png') + ',Quper.Logo'),
    ('/resource:' + $icon + ',Quper.Icon'),
    ('/resource:' + (Join-Path $root 'data\catalog.json') + ',Quper.Catalog'),
    ('/resource:' + (Join-Path $root 'data\strings.en.json') + ',Quper.English')
)
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName
$argsList = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:' + $out),('/win32icon:' + $icon))
$argsList += @($references | ForEach-Object { '/reference:' + $_ })
$argsList += $resources
$argsList += $sources
& $csc @argsList
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Get-Item -LiteralPath $out | Select-Object FullName, Length
if ($Check) {
    $test = Join-Path ([IO.Path]::GetTempPath()) ('quper-test-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        $testArgs = @('/nologo','/target:exe','/platform:x64','/codepage:65001','/main:QuperDesk.Smoke',('/out:' + $test))
        $testArgs += @($references | ForEach-Object { '/reference:' + $_ })
        $testArgs += $resources
        $testArgs += $sources
        $testArgs += Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object FullName
        & $csc @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
        $testSwitches = @()
        if ($UseLocalProfiles) { $testSwitches += '--local' }
        if ($Preview) { $testSwitches += $Preview }
        if ($PreviewMods) { $testSwitches += '--preview-mods' }
        & $test @testSwitches
        if ($LASTEXITCODE -ne 0) { throw 'Smoke test failed.' }
    } finally {
        if (Test-Path -LiteralPath $test) { Remove-Item -LiteralPath $test }
    }
}
