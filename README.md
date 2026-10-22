# Desktop Screen Manager

A small Windows notification-area utility that watches DDC/CI monitor power state, hides powered-down monitor paths from the active Windows desktop, and restores the saved layout when those monitors report on again.

This was created to solve a couple of common issues I was facing with the way Windows manages connected monitors.

Problem 1: Some monitors when powered off still display as an active screen in Windows Display settings, and due to this windows can incorrectly open up on the turned off screen causing them to be difficult to find and causing general annoyance. 
Solution : Desktop_Screen_Manager watches the connected monitors for changes to DDC/CI power-mode, if a screen changes to show it is powered off, the app will disable it from Windows display settings while preserving the prior configurations. When the screen is turned back on it will restore the prior Windows display settings. 

Problem 2: When all active displays are turned off some remote desktop applications such as Steam Link may not work properly showing up as a black screen. 
Solution : Desktop_Screen_Manager provides an easy way to configure a virtual display (VDD - https://github.com/VirtualDrivers/Virtual-Display-Driver) and can be setup to automatically turn the virtual display on/off depending on if the physical monitors are turned on or off. Applications like Steam Link can use the configured virtual display driver so it will still work when all physical monitors are turned off.  

## Features

- Discovers connected physical monitors through Windows DisplayConfig and attempts to read the MCCS VCP `0xD6` power-mode feature over DDC/CI.
- Hides paths reporting standby, suspend, or off; restores the saved topology as monitors report on. Monitors that do not expose a readable DDC/CI power state are left alone.
- Optional VDD by MTT integration: automatic mode enables one virtual display when no usable physical displays remain. Manual mode can follow automatic behavior, force one virtual display on, or force it off regardless of physical-screen state. The driver is always asked for a count of zero or one, preventing duplicate virtual outputs.
- When VDD is unavailable, the tray reports that virtual display features are disabled and offers an install action.
- Resolution presets: 720p, Steam Deck (1280 x 800), 1080p, 1440p, 4K, ultrawide and super ultrawide modes, and custom dimensions. Named custom resolutions can be saved and removed.
- Common refresh-rate choices plus a custom rate. VDD and graphics hardware limits still apply.
- Optional per-user Startup shortcut and a pause control.

## Requirements and limits

- Windows 10 or later with .NET Framework 4.5 or later. Setup checks for the runtime and opens Microsoft's download page if it is missing.
- Monitor firmware, connection path, and graphics driver must expose DDC/CI power-mode reads for automatic monitor handling. DDC/CI over HDMI/DisplayPort is common but not universal. The app does not install a monitor-specific driver.
- The VDD controls require VDD by MTT to be installed and its local `MTTVirtualDisplayPipe` control endpoint to respond. If the driver is absent or not responding, virtual-display controls stay disabled. The default settings location is `C:\VirtualDisplayDriver\vdd_settings.xml`.
- Hiding the final powered-off physical path requires the virtual display to become active. If VDD is unavailable or automatic/manual virtual display is disabled, Desktop Screen Manager leaves the last path in place rather than applying an empty display topology.
- Automatic fallback is on by default. A manual force-on or force-off choice overrides automatic behavior. The target VDD count is always zero or one.

## Tray menu

Right-click the monitor icon to access status, pause, the automatic fallback toggle, manual virtual-display modes, resolution and refresh choices, Startup, and Exit. Manual mode has three choices: follow the automatic setting, force on, or force off. Resolution and virtual-display preferences are per-user. Saved custom resolutions and logs are stored under `%LOCALAPPDATA%\Desktop_Screen_Manager`.

The VDD resolution controls update the VDD settings file and send `SETDISPLAYCOUNT` with the desired current count to reload its modes. Set `DESKTOP_SCREEN_MANAGER_VDD_SETTINGS` to override the default settings-file path. This modifies the VDD configuration file, so users should keep a backup if they have made custom VDD changes.

## Build

Run `build.ps1` from a Windows PowerShell prompt. It uses the .NET Framework C# compiler bundled with Windows and writes `dist\Desktop_Screen_Manager.exe`. No NuGet dependencies are required.

## Install and package

For a normal install, download and extract `Desktop_Screen_Manager.zip` from a project release, then run `setup.cmd`. It checks the registry for .NET Framework 4.5 or later before copying files. If missing, it offers to open Microsoft's [Framework download page](https://dotnet.microsoft.com/en-us/download/dotnet-framework); install a compatible runtime and rerun setup. Setup installs per-user, adds Start Menu shortcuts, and asks whether to add a desktop shortcut, install optional VDD, and launch Desktop Screen Manager. It does not need administrator access unless the user chooses VDD, whose own installer may request it. The app's Start with Windows setting remains opt-in from the tray menu.

VDD is optional and remains a separate install. The setup prompt and tray's Install Virtual Display Driver action run `install-vdd.cmd`, which calls the official WinGet package and leaves its normal install/elevation prompts visible. Or download from the [official VDD project page](https://github.com/VirtualDrivers/Virtual-Display-Driver). Desktop Screen Manager does not bundle the driver.

To create a release package, run `package.ps1`. It builds the executable into `dist\package`, copies the setup and uninstall scripts, and creates `dist\Desktop_Screen_Manager.zip`. No installer-builder dependency is needed.

Choose **Uninstall Desktop Screen Manager** from the tray icon's right-click menu, or run `uninstall.cmd` from the Start Menu or install folder. The tray action restores the saved display layout and exits before opening the script. The script optionally runs VDD's registered uninstaller, removes the app's shortcuts and installed files, and asks whether to delete per-user settings, saved resolutions, logs, and recovery data. Declining preserves that data.

If VDD removal is chosen, Desktop Screen Manager runs VDD's own registered uninstaller. It does not issue `sc delete` or remove driver-store packages/certificates itself. VDD's INF uses the shared Windows Driver Foundation reflector service (`WUDFRd`), so deleting a service by name could affect other devices; the VDD uninstaller should own driver/device cleanup. If VDD was installed manually and no registered uninstaller is found, use the [official VDD removal guide](https://github.com/VirtualDrivers/Virtual-Display-Driver/wiki/Uninstalling-Virtual-Display-Driver). The project is MIT licensed; see `LICENSE`. VDD is a separate third-party project with its own license and release process.

## Logs and recovery

The log and recovery snapshot are stored in `%LOCALAPPDATA%\Desktop_Screen_Manager`. If Desktop Screen Manager exits unexpectedly while a monitor path is hidden, it attempts to restore the saved layout at the next launch. Use Safe Mode or Windows display recovery options if a third-party display driver prevents normal sign-in.

