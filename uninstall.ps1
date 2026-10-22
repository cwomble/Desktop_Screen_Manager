param(
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = 'Stop'

$installDirectory = $PSScriptRoot
$appDataDirectory = Join-Path $env:LOCALAPPDATA 'DisplayGuard'
$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Desktop Screen Manager'
$legacyStartMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Display Guard'
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcuts = @(
    (Join-Path $startupDirectory 'Desktop Screen Manager.lnk'),
    (Join-Path $desktopDirectory 'Desktop Screen Manager.lnk'),
    (Join-Path $startupDirectory 'Display Guard.lnk'),
    (Join-Path $desktopDirectory 'Display Guard.lnk')
)

if ($WaitForProcessId -gt 0) {
    try { Wait-Process -Id $WaitForProcessId -Timeout 45 -ErrorAction Stop } catch { }
}

if (Get-Process -Name 'DisplayGuard', 'Desktop_Screen_Manager' -ErrorAction SilentlyContinue) {
    Write-Host 'Exit Desktop Screen Manager from its tray menu first so it can restore the saved display layout.'
    Read-Host 'Press Enter to cancel this uninstall'
    exit 1
}

function Get-VddUninstallEntries {
    $entries = @()
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            try {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                $root = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
                if ($root) {
                    foreach ($name in $root.GetSubKeyNames()) {
                        $key = $root.OpenSubKey($name)
                        if (-not $key) { continue }
                        $displayName = [string]$key.GetValue('DisplayName')
                        $uninstallString = [string]$key.GetValue('UninstallString')
                        if ($displayName -match 'Virtual Display Driver' -and $uninstallString) {
                            $entries += [pscustomobject]@{ DisplayName = $displayName; UninstallString = $uninstallString }
                        }
                        $key.Dispose()
                    }
                    $root.Dispose()
                }
                $base.Dispose()
            } catch { }
        }
    }
    return $entries | Sort-Object DisplayName -Unique
}

function Invoke-VddUninstaller([string]$commandLine) {
    $expanded = [Environment]::ExpandEnvironmentVariables($commandLine.Trim())
    if ($expanded -match '^"(?<exe>[^"]+)"\s*(?<args>.*)$') {
        $exe = $Matches.exe
        $arguments = $Matches.args
    } elseif ($expanded -match '^(?<exe>.+?\.exe)\s*(?<args>.*)$') {
        $exe = $Matches.exe.Trim()
        $arguments = $Matches.args
    } else {
        throw "Could not parse the registered VDD uninstall command: $commandLine"
    }

    if ([IO.Path]::GetFileName($exe) -match '^msiexec(\.exe)?$') {
        $arguments = $arguments -replace '(?i)/I(?=\s*\{)', '/X'
    }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    return $process.ExitCode
}

$vddEntries = @(Get-VddUninstallEntries)
if ($vddEntries.Count -gt 0) {
    Write-Host 'The following VDD installation was found:'
    $vddEntries | ForEach-Object { Write-Host " - $($_.DisplayName)" }
    $removeVdd = Read-Host "Also run VDD's registered uninstaller? [y/N]"
    if ($removeVdd -match '^(y|yes)$') {
        try {
            $exitCode = Invoke-VddUninstaller $vddEntries[0].UninstallString
            if ($exitCode -ne 0) {
                $continue = Read-Host "VDD uninstaller returned code $exitCode. Continue removing Desktop Screen Manager? [y/N]"
                if ($continue -notmatch '^(y|yes)$') { exit 1 }
            }
        } catch {
            Write-Host "Could not finish VDD uninstall: $($_.Exception.Message)"
            $continue = Read-Host 'Continue removing Desktop Screen Manager anyway? [y/N]'
            if ($continue -notmatch '^(y|yes)$') { exit 1 }
        }
    }
} else {
    Write-Host 'No registered Virtual Display Driver uninstaller was found.'
    $openGuide = Read-Host 'Open the official VDD manual removal guide? [y/N]'
    if ($openGuide -match '^(y|yes)$') {
        Start-Process 'https://github.com/VirtualDrivers/Virtual-Display-Driver/wiki/Uninstalling-Virtual-Display-Driver'
    }
}

$removeData = Read-Host 'Also remove Desktop Screen Manager settings, saved resolutions, logs, and recovery data? [y/N]'
if ($removeData -match '^(y|yes)$') {
    Remove-Item -LiteralPath 'HKCU:\Software\DisplayGuard' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $appDataDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($path in $shortcuts) {
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath $startMenuDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyStartMenuDirectory -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Desktop Screen Manager user files and shortcuts have been removed.'
Write-Host 'No Windows services, driver-store packages, or certificates were deleted directly.'
Remove-Item -LiteralPath $installDirectory -Recurse -Force
