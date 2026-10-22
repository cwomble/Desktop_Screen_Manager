$ErrorActionPreference = 'Stop'

$sourceDirectory = $PSScriptRoot
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Desktop Screen Manager'
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

if (Get-Process -Name 'Desktop_Screen_Manager' -ErrorAction SilentlyContinue) {
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
$desktopAnswer = Read-Host 'Add a desktop shortcut? [y/N]'
if ($desktopAnswer -match '^(y|yes)$') {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'Desktop Screen Manager.lnk'))
    $link.TargetPath = Join-Path $installDirectory 'Desktop_Screen_Manager.exe'
    $link.WorkingDirectory = $installDirectory
    $link.IconLocation = (Join-Path $installDirectory 'Desktop_Screen_Manager.exe') + ',0'
    $link.Save()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($link)
}

[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

Write-Host "Desktop Screen Manager installed for this Windows user at: $installDirectory"
$vddAnswer = Read-Host 'Install the optional Virtual Display Driver now through WinGet? [y/N]'
if ($vddAnswer -match '^(y|yes)$') {
    & (Join-Path $installDirectory 'install-vdd.cmd')
}

$launchAnswer = Read-Host 'Launch Desktop Screen Manager now? [Y/n]'
if ($launchAnswer -notmatch '^(n|no)$') {
    Start-Process -FilePath (Join-Path $installDirectory 'Desktop_Screen_Manager.exe') -WorkingDirectory $installDirectory
}
