$ErrorActionPreference = 'Stop'

$sourceDirectory = $PSScriptRoot
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Desktop Screen Manager'
$legacyInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Display Guard'
$legacyStartMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Display Guard'
$legacyStartupShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) 'Display Guard.lnk'
$legacyDesktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'Display Guard.lnk'
$startupShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)) 'Desktop Screen Manager.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'Desktop Screen Manager.lnk'
$dotNetDownloadPage = 'https://dotnet.microsoft.com/en-us/download/dotnet-framework'
$requiredFiles = @('Desktop_Screen_Manager.exe', 'install-vdd.cmd', 'uninstall.cmd', 'uninstall.ps1', 'LICENSE')

function Test-NetFramework45OrLater {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = $null
        $key = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            $key = $base.OpenSubKey('SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full')
            if ($key -and [int]$key.GetValue('Release', 0) -ge 378389) { return $true }
        } catch { }
        finally {
            if ($key) { $key.Dispose() }
            if ($base) { $base.Dispose() }
        }
    }
    return $false
}

if (-not (Test-NetFramework45OrLater)) {
    Write-Host '.NET Framework 4.5 or later was not detected. Desktop Screen Manager needs the .NET Framework runtime to start.'
    $openDownload = Read-Host "Open Microsoft's .NET Framework download page now? [Y/n]"
    if ($openDownload -notmatch '^(n|no)$') {
        Start-Process $dotNetDownloadPage
    }
    Write-Host 'Install the latest .NET Framework runtime supported by your Windows version, then run setup.cmd again.'
    exit 1
}

foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $file) -PathType Leaf)) {
        throw "Package is missing required file: $file"
    }
}

if (Get-Process -Name 'DisplayGuard', 'Desktop_Screen_Manager' -ErrorAction SilentlyContinue) {
    Write-Host 'Desktop Screen Manager is already running. Exit it from its tray menu, then run setup again.'
    exit 1
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
foreach ($file in $requiredFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $file) -Destination (Join-Path $installDirectory $file) -Force
}

$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Desktop Screen Manager'
New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcut in @(
    @{ Path = (Join-Path $startMenuDirectory 'Desktop Screen Manager.lnk'); Target = (Join-Path $installDirectory 'Desktop_Screen_Manager.exe'); Description = 'Start Desktop Screen Manager' },
    @{ Path = (Join-Path $startMenuDirectory 'Uninstall Desktop Screen Manager.lnk'); Target = (Join-Path $installDirectory 'uninstall.cmd'); Description = 'Uninstall Desktop Screen Manager' }
)) {
    $link = $shell.CreateShortcut($shortcut.Path)
    $link.TargetPath = $shortcut.Target
    $link.WorkingDirectory = $installDirectory
    $link.Description = $shortcut.Description
    $link.IconLocation = (Join-Path $installDirectory 'Desktop_Screen_Manager.exe') + ',0'
    $link.Save()
}
$desktopAnswer = if (Test-Path -LiteralPath $legacyDesktopShortcut) { 'y' } else { Read-Host 'Add a desktop shortcut? [y/N]' }
if ($desktopAnswer -match '^(y|yes)$') {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'Desktop Screen Manager.lnk'))
    $link.TargetPath = Join-Path $installDirectory 'Desktop_Screen_Manager.exe'
    $link.WorkingDirectory = $installDirectory
    $link.IconLocation = (Join-Path $installDirectory 'Desktop_Screen_Manager.exe') + ',0'
    $link.Save()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($link)
}

if (Test-Path -LiteralPath $legacyStartupShortcut) {
    $link = $shell.CreateShortcut($startupShortcut)
    $link.TargetPath = Join-Path $installDirectory 'Desktop_Screen_Manager.exe'
    $link.WorkingDirectory = $installDirectory
    $link.Description = 'Manage physical and virtual displays.'
    $link.IconLocation = (Join-Path $installDirectory 'Desktop_Screen_Manager.exe') + ',0'
    $link.Save()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($link)
    Remove-Item -LiteralPath $legacyStartupShortcut -Force -ErrorAction SilentlyContinue
}
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
Remove-Item -LiteralPath $legacyStartMenuDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyDesktopShortcut -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $legacyInstallDirectory) {
    $programsDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')).TrimEnd('\') + '\'
    $legacyFullPath = [IO.Path]::GetFullPath($legacyInstallDirectory)
    if ($legacyFullPath.StartsWith($programsDirectory, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($legacyFullPath) -eq 'Display Guard') {
        Remove-Item -LiteralPath $legacyFullPath -Recurse -Force
    }
}

Write-Host "Desktop Screen Manager installed for this Windows user at: $installDirectory"
$vddAnswer = Read-Host 'Install the optional Virtual Display Driver now through WinGet? [y/N]'
if ($vddAnswer -match '^(y|yes)$') {
    & (Join-Path $installDirectory 'install-vdd.cmd')
}

$launchAnswer = Read-Host 'Launch Desktop Screen Manager now? [Y/n]'
if ($launchAnswer -notmatch '^(n|no)$') {
    Start-Process -FilePath (Join-Path $installDirectory 'Desktop_Screen_Manager.exe')
}
