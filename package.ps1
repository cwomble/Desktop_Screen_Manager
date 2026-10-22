$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$distDirectory = Join-Path $projectRoot 'dist'
$packageDirectory = Join-Path $distDirectory 'package'
$expectedParent = [IO.Path]::GetFullPath($distDirectory).TrimEnd('\') + '\'
$resolvedPackage = [IO.Path]::GetFullPath($packageDirectory)
if (-not $resolvedPackage.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output escaped the dist directory: $resolvedPackage"
}

if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

& (Join-Path $projectRoot 'build.ps1') -OutputPath (Join-Path $packageDirectory 'Desktop_Screen_Manager.exe')

$files = @(
    'setup.cmd', 'setup.ps1', 'uninstall.cmd', 'uninstall.ps1',
    'install-vdd.cmd', 'LICENSE', 'README.md'
)
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $packageDirectory $file)
}

$archivePath = Join-Path $distDirectory 'Desktop_Screen_Manager.zip'
Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -Force
Write-Output "Created release package: $archivePath"
