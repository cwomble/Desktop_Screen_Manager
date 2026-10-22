using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipes;
using System.Diagnostics;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace DesktopScreenManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\Desktop_Screen_Manager.Singleton", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GuardContext());
            }
        }
    }

    internal sealed class GuardContext : ApplicationContext
    {
        private const string ProductName = "Desktop Screen Manager";
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem virtualModeSummaryItem;
        private readonly ToolStripMenuItem pauseItem;
        private readonly ToolStripMenuItem autoVirtualItem;
        private readonly ToolStripMenuItem manualVirtualMenu;
        private readonly ToolStripMenuItem automaticVirtualModeItem;
        private readonly ToolStripMenuItem forceVirtualOnItem;
        private readonly ToolStripMenuItem forceVirtualOffItem;
        private readonly ToolStripMenuItem installVddItem;
        private readonly ToolStripMenuItem uninstallItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem resolutionMenu;
        private readonly ToolStripMenuItem refreshMenu;
        private readonly System.Windows.Forms.Timer timer;
        private readonly string logPath;
        private readonly string statePath;
        private readonly string customResolutionPath;
        private readonly Dictionary<string, ManagedMonitor> monitors = new Dictionary<string, ManagedMonitor>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ResolutionPreset> customPresets = new List<ResolutionPreset>();
        private DisplaySnapshot savedDisplay;
        private bool paused;
        private bool busy;
        private bool changingStartup;
        private bool changingVirtualOptions;
        private string status = "Starting";
        private bool autoVirtualEnabled;
        private int manualVirtualMode;
        private int lastVirtualDisplayCount = -1;
        private int virtualControlFailures;
        private DateTime nextVddProbeUtc = DateTime.MinValue;
        private bool vddAvailable;

        public GuardContext()
        {
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Desktop_Screen_Manager");
            Directory.CreateDirectory(dataDir);
            logPath = Path.Combine(dataDir, "guard.log");
            statePath = Path.Combine(dataDir, "display-state.bin");
            customResolutionPath = Path.Combine(dataDir, "custom-resolutions.xml");
            LoadCustomPresets();
            LoadVirtualOptions();

            statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
            virtualModeSummaryItem = new ToolStripMenuItem("Virtual mode: reading settings…") { Enabled = false };
            pauseItem = new ToolStripMenuItem("Pause automatic switching") { CheckOnClick = true };
            autoVirtualItem = new ToolStripMenuItem("Automatically use virtual display when no physical screens are active") { CheckOnClick = true, Checked = autoVirtualEnabled };
            manualVirtualMenu = new ToolStripMenuItem("Manual virtual display");
            automaticVirtualModeItem = new ToolStripMenuItem("Follow automatic setting");
            forceVirtualOnItem = new ToolStripMenuItem("Force on");
            forceVirtualOffItem = new ToolStripMenuItem("Force off");
            manualVirtualMenu.DropDownItems.AddRange(new ToolStripItem[] { automaticVirtualModeItem, forceVirtualOnItem, forceVirtualOffItem });
            SyncVirtualModeMenu();
            installVddItem = new ToolStripMenuItem("Install Virtual Display Driver…");
            installVddItem.Click += delegate { StartVddInstaller(); };
            uninstallItem = new ToolStripMenuItem("Uninstall Desktop Screen Manager…");
            uninstallItem.Visible = File.Exists(Path.Combine(Application.StartupPath, "uninstall.ps1"));
            uninstallItem.Click += delegate { StartUninstaller(); };
            startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
            startupItem.Checked = StartupLinkExists();
            resolutionMenu = new ToolStripMenuItem("Virtual display resolution");
            refreshMenu = new ToolStripMenuItem("Virtual display refresh rate");
            AddResolutionItems();
            AddRefreshItems();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(virtualModeSummaryItem);
            menu.Items.Add(installVddItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(pauseItem);
            menu.Items.Add(autoVirtualItem);
            menu.Items.Add(manualVirtualMenu);
            menu.Items.Add(resolutionMenu);
            menu.Items.Add(refreshMenu);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(uninstallItem);
            menu.Items.Add("Exit", null, delegate { ExitApplication(); });
            menu.Opening += delegate { UpdateConfiguredModeSummary(); };

            tray = new NotifyIcon();
            tray.Icon = CreateIcon();
            tray.Text = ProductName;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowStatus(); };

            pauseItem.CheckedChanged += delegate
            {
                paused = pauseItem.Checked;
                if (paused)
                {
                    RestoreDisplay();
                    SetStatus("Paused");
                }
                else
                {
                    SetStatus("Watching monitor");
                    Poll();
                }
            };

            autoVirtualItem.CheckedChanged += delegate { UpdateVirtualOptions(); };
            automaticVirtualModeItem.Click += delegate { SetManualVirtualMode(0); };
            forceVirtualOnItem.Click += delegate { SetManualVirtualMode(1); };
            forceVirtualOffItem.Click += delegate { SetManualVirtualMode(2); };

            startupItem.CheckedChanged += delegate
            {
                if (changingStartup) return;
                try
                {
                    SetStartup(startupItem.Checked);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not update the Startup folder shortcut.\r\n\r\n" + ex.Message,
                        ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    changingStartup = true;
                    startupItem.Checked = !startupItem.Checked;
                    changingStartup = false;
                }
            };
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1800;
            timer.Tick += delegate { Poll(); };
            timer.Start();
            SystemEvents.SessionEnding += OnSessionEnding;
            Log("Started; startup shortcut=" + StartupLinkExists() + "; automatic virtual display=" + autoVirtualEnabled + "; manual mode=" + manualVirtualMode);
            RestoreInterruptedState();
            Poll();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            RestoreDisplay();
        }

        private void Poll()
        {
            if (busy || paused) return;
            busy = true;
            try
            {
                DisplaySnapshot active = DisplaySnapshot.QueryActive();
                DiscoverMonitors(active);
                foreach (ManagedMonitor monitor in monitors.Values.ToArray())
                {
                    byte type;
                    uint power, maximum;
                    if (!Native.GetVCPFeatureAndVCPFeatureReply(monitor.Handle, 0xD6, out type, out power, out maximum))
                    {
                        monitor.ReadFailures++;
                        if (monitor.ReadFailures == 1 || monitor.ReadFailures % 20 == 0)
                            Log("DDC/CI VCP D6 read failed for " + monitor.Description + " (" + monitor.ReadFailures + " consecutive): " + Marshal.GetLastWin32Error());
                        continue;
                    }
                    monitor.ReadFailures = 0;
                    int value = (int)power;
                    if (monitor.PowerState != value)
                    {
                        Log("Monitor " + monitor.Description + " power VCP D6=" + value + " (type=" + type + ")");
                        monitor.PowerState = value;
                    }
                }

                HashSet<string> offPaths = new HashSet<string>(monitors.Values.Where(m => IsPoweredOff(m.PowerState)).Select(m => m.DevicePath), StringComparer.OrdinalIgnoreCase);
                int usablePhysical = CountUsablePhysical(active, offPaths);
                int desiredVirtualCount = DesiredVirtualDisplayCount(usablePhysical);
                ApplyVirtualDisplayCount(desiredVirtualCount);
                UpdateVddMenuState();
                ReconcileOffMonitors(active, offPaths);

                int offCount = offPaths.Count;
                string monitorStatus = offCount > 0
                    ? offCount + " monitor" + (offCount == 1 ? "" : "s") + " off; hiding powered-off display paths"
                    : (desiredVirtualCount == 1 && vddAvailable ? "Virtual display active" : "Watching connected monitors");
                SetStatus(vddAvailable ? monitorStatus : monitorStatus + "; VDD unavailable, virtual display disabled");
            }
            catch (Exception ex)
            {
                SetStatus("Error; see log");
                Log("Poll error: " + ex);
            }
            finally { busy = false; }
        }

        private static bool IsPoweredOff(int powerState) { return powerState >= 2 && powerState <= 5; }

        private void DiscoverMonitors(DisplaySnapshot active)
        {
            List<MonitorTarget> targets = active.Paths.Select(p => Native.GetTargetDetails(p.targetInfo, Native.GetSourceName(p.sourceInfo)))
                .Where(t => !Native.IsIndirectDisplay(t.OutputTechnology)).ToList();
            HashSet<string> activePaths = new HashSet<string>(targets.Select(t => t.DevicePath), StringComparer.OrdinalIgnoreCase);
            foreach (string key in monitors.Keys.ToArray())
            {
                ManagedMonitor existing;
                if (!activePaths.Contains(key) && monitors.TryGetValue(key, out existing) && !IsPoweredOff(existing.PowerState))
                {
                    existing.Dispose();
                    monitors.Remove(key);
                }
            }
            Native.MonitorEnumProc callback = delegate(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                Native.MONITORINFOEX info = new Native.MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX));
                if (!Native.GetMonitorInfo(hMonitor, ref info)) return true;
                List<MonitorTarget> matching = targets.Where(t => String.Equals(t.SourceName, info.szDevice, StringComparison.OrdinalIgnoreCase) && !monitors.ContainsKey(t.DevicePath)).ToList();
                if (matching.Count == 0) return true;
                uint count;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0) return true;
                Native.PHYSICAL_MONITOR[] list = new Native.PHYSICAL_MONITOR[count];
                if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, list)) return true;
                int targetIndex = 0;
                for (int i = 0; i < list.Length; i++)
                {
                    if (targetIndex < matching.Count)
                    {
                        MonitorTarget target = matching[targetIndex++];
                        ManagedMonitor managed = new ManagedMonitor(target.DevicePath, target.FriendlyName, target.SourceName, list[i].hPhysicalMonitor);
                        monitors[target.DevicePath] = managed;
                        Log("Found DDC/CI monitor: " + managed.Description + " on " + info.szDevice + " (" + target.DevicePath + ")");
                    }
                    else
                    {
                        Native.PHYSICAL_MONITOR[] one = new Native.PHYSICAL_MONITOR[] { list[i] };
                        Native.DestroyPhysicalMonitors(1, one);
                    }
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }

        private void ReconcileOffMonitors(DisplaySnapshot active, HashSet<string> offPaths)
        {
            if (offPaths.Count == 0)
            {
                if (savedDisplay != null)
                {
                    if (RestoreDisplay()) Log("Restored display layout after all DDC-capable monitors reported on.");
                }
                return;
            }
            if (savedDisplay == null)
            {
                savedDisplay = active;
                try { SaveSnapshot(savedDisplay); }
                catch (Exception ex) { savedDisplay = null; Log("Could not save recovery state; leaving monitor layout unchanged: " + ex.Message); return; }
            }

            DisplaySnapshot desired = FilterOffPaths(savedDisplay, offPaths);
            DisplaySnapshot currentFiltered = FilterOffPaths(active, offPaths);
            HashSet<string> present = new HashSet<string>(currentFiltered.Paths.Select(p => Native.GetTargetDetails(p.targetInfo, Native.GetSourceName(p.sourceInfo)).DevicePath), StringComparer.OrdinalIgnoreCase);
            if (desired.Paths.All(p => present.Contains(Native.GetTargetDetails(p.targetInfo, Native.GetSourceName(p.sourceInfo)).DevicePath)))
                desired = currentFiltered;
            if (desired.Paths.Length == 0)
            {
                DisplaySnapshot withVirtual = DisplaySnapshot.QueryActive();
                desired = FilterOffPaths(withVirtual, offPaths);
                if (desired.Paths.Length == 0)
                {
                    // Keep a last physical path until VDD has actually appeared; never strand the desktop in a zero-path topology.
                    Log("All physical monitor paths report off; waiting for the virtual display before removing the final path.");
                    return;
                }
            }

            if (ApplySnapshot(desired, "Hide powered-off monitor paths"))
            {
                try { SaveSnapshot(savedDisplay); } catch { }
            }
        }

        private static int CountUsablePhysical(DisplaySnapshot snapshot, HashSet<string> offPaths)
        {
            int count = 0;
            foreach (Native.DISPLAYCONFIG_PATH_INFO path in snapshot.Paths)
            {
                MonitorTarget target = Native.GetTargetDetails(path.targetInfo, Native.GetSourceName(path.sourceInfo));
                if (offPaths.Contains(target.DevicePath) || Native.IsIndirectDisplay(path.targetInfo.outputTechnology)) continue;
                count++;
            }
            return count;
        }

        private static DisplaySnapshot FilterOffPaths(DisplaySnapshot snapshot, HashSet<string> offPaths)
        {
            Native.DISPLAYCONFIG_PATH_INFO[] paths = snapshot.Paths.Where(p => !offPaths.Contains(Native.GetTargetDetails(p.targetInfo, Native.GetSourceName(p.sourceInfo)).DevicePath)).ToArray();
            return new DisplaySnapshot { Paths = paths, Modes = (Native.DISPLAYCONFIG_MODE_INFO[])snapshot.Modes.Clone() };
        }

        private bool ApplyVirtualDisplayCount(int desired)
        {
            DateTime now = DateTime.UtcNow;
            if (now < nextVddProbeUtc) return vddAvailable;
            nextVddProbeUtc = now.AddSeconds(8);
            if (lastVirtualDisplayCount == desired)
            {
                vddAvailable = VddPipe.Ping();
                if (!vddAvailable) lastVirtualDisplayCount = -1;
                return vddAvailable;
            }
            string response;
            if (!VddPipe.Send("SETDISPLAYCOUNT " + desired, out response))
            {
                vddAvailable = false;
                virtualControlFailures++;
                if (virtualControlFailures == 1 || virtualControlFailures % 20 == 0)
                    Log("Virtual display control unavailable or failed (" + virtualControlFailures + "): " + response);
                return false;
            }
            vddAvailable = true;
            virtualControlFailures = 0;
            lastVirtualDisplayCount = desired;
            Log("Virtual display count set to " + desired + " (automatic=" + autoVirtualEnabled + ", manual mode=" + manualVirtualMode + "); response=" + response);
            return true;
        }

        private void UpdateVddMenuState()
        {
            if (installVddItem == null) return;
            installVddItem.Visible = !vddAvailable;
            autoVirtualItem.Enabled = vddAvailable;
            manualVirtualMenu.Enabled = vddAvailable;
            resolutionMenu.Enabled = vddAvailable;
            refreshMenu.Enabled = vddAvailable;
        }

        private void UpdateConfiguredModeSummary()
        {
            try
            {
                int[] mode = ReadCurrentMode();
                int refresh = ReadCurrentRefreshRate();
                virtualModeSummaryItem.Text = "Virtual mode: " + mode[0] + " × " + mode[1] + " @ " + refresh + " Hz" +
                    (vddAvailable ? "" : " (VDD unavailable)");
            }
            catch (Exception ex)
            {
                virtualModeSummaryItem.Text = vddAvailable ? "Virtual mode: settings unavailable" : "Virtual mode: VDD unavailable";
                Log("Could not read configured virtual display mode: " + ex.Message);
            }
        }

        private void StartVddInstaller()
        {
            try
            {
                string installer = Path.Combine(Application.StartupPath, "install-vdd.cmd");
                if (File.Exists(installer))
                    Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("https://github.com/VirtualDrivers/Virtual-Display-Driver") { UseShellExecute = true });
                SetStatus("Opened VDD installation");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the VDD installer or project page.\r\n\r\n" + ex.Message,
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartUninstaller()
        {
            string scriptPath = Path.Combine(Application.StartupPath, "uninstall.ps1");
            if (!File.Exists(scriptPath))
            {
                MessageBox.Show("The Desktop Screen Manager uninstall script was not found beside the application.",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult answer = MessageBox.Show(
                "Desktop Screen Manager will restore the saved display layout, close, and then open its uninstall script. Continue?",
                ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            try
            {
                ProcessStartInfo start = new ProcessStartInfo("powershell.exe")
                {
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -WaitForProcessId " + Process.GetCurrentProcess().Id,
                    UseShellExecute = true
                };
                Process.Start(start);
                ExitApplication();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start the uninstall script.\r\n\r\n" + ex.Message,
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private sealed class ResolutionPreset
        {
            public readonly string Name;
            public readonly int Width;
            public readonly int Height;
            public readonly int Refresh;
            public readonly bool IsCustom;
            public ResolutionPreset(string name, int width, int height, int refresh = 60, bool isCustom = false) { Name = name; Width = width; Height = height; Refresh = refresh; IsCustom = isCustom; }
            public override string ToString() { return Name + " (" + Width + " x " + Height + ")"; }
        }

        private static readonly ResolutionPreset[] ResolutionPresets = new ResolutionPreset[]
        {
            new ResolutionPreset("720p", 1280, 720),
            new ResolutionPreset("Steam Deck", 1280, 800),
            new ResolutionPreset("1080p", 1920, 1080),
            new ResolutionPreset("1440p", 2560, 1440),
            new ResolutionPreset("4K", 3840, 2160),
            new ResolutionPreset("1440p ultrawide", 3440, 1440),
            new ResolutionPreset("1440p super ultrawide", 5120, 1440),
            new ResolutionPreset("4K ultrawide", 5120, 2160),
            new ResolutionPreset("4K super ultrawide", 7680, 2160)
        };

        private static readonly int[] CommonRefreshRates = new int[] { 30, 60, 75, 90, 100, 120, 144, 165, 240, 244 };
        private static string VddSettingsPath
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("DESKTOP_SCREEN_MANAGER_VDD_SETTINGS");
                if (!String.IsNullOrWhiteSpace(configured)) return configured;
                string systemInstall = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "VirtualDisplayDriver", "vdd_settings.xml");
                string programFilesInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VirtualDisplayDriver", "vdd_settings.xml");
                return File.Exists(systemInstall) || !File.Exists(programFilesInstall) ? systemInstall : programFilesInstall;
            }
        }

        private void AddResolutionItems()
        {
            resolutionMenu.DropDownItems.Clear();
            foreach (ResolutionPreset preset in ResolutionPresets)
            {
                ResolutionPreset itemPreset = preset;
                resolutionMenu.DropDownItems.Add(itemPreset.ToString(), null, delegate { ChangeResolution(itemPreset.Width, itemPreset.Height, itemPreset.Name); });
            }
            resolutionMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (ResolutionPreset preset in customPresets.ToArray())
            {
                ResolutionPreset itemPreset = preset;
                ToolStripMenuItem saved = new ToolStripMenuItem(itemPreset.Name + " (" + itemPreset.Width + " x " + itemPreset.Height + " @ " + itemPreset.Refresh + " Hz)");
                saved.Click += delegate { ApplyNamedResolution(itemPreset); };
                resolutionMenu.DropDownItems.Add(saved);
            }
            if (customPresets.Count > 0)
            {
                ToolStripMenuItem remove = new ToolStripMenuItem("Remove saved resolution");
                foreach (ResolutionPreset preset in customPresets.ToArray())
                {
                    ResolutionPreset itemPreset = preset;
                    remove.DropDownItems.Add(itemPreset.Name, null, delegate { RemoveCustomResolution(itemPreset); });
                }
                resolutionMenu.DropDownItems.Add(remove);
            }
            resolutionMenu.DropDownItems.Add(new ToolStripSeparator());
            resolutionMenu.DropDownItems.Add("Custom…", null, delegate { ChooseCustomMode(); });
            resolutionMenu.DropDownItems.Add("Save custom resolution…", null, delegate { SaveCustomResolution(); });
        }

        private void AddRefreshItems()
        {
            foreach (int rate in CommonRefreshRates)
            {
                int itemRate = rate;
                refreshMenu.DropDownItems.Add(rate + " Hz", null, delegate { ChangeRefreshRate(itemRate); });
            }
            refreshMenu.DropDownItems.Add(new ToolStripSeparator());
            refreshMenu.DropDownItems.Add("Custom…", null, delegate { ChooseCustomRefreshRate(); });
        }

        private void ChooseCustomMode()
        {
            using (CustomModeDialog dialog = new CustomModeDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                    ApplyVirtualMode((int)dialog.WidthValue, (int)dialog.HeightValue, (int)dialog.RefreshValue);
            }
        }

        private void SaveCustomResolution()
        {
            using (CustomModeDialog dialog = new CustomModeDialog(false, true))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                string name = dialog.NameValue.Trim();
                if (name.Length == 0 || name.Length > 40)
                {
                    MessageBox.Show("Enter a name between 1 and 40 characters.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (customPresets.Any(p => String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("A saved resolution already uses that name.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    int width = (int)dialog.WidthValue, height = (int)dialog.HeightValue, refresh = (int)dialog.RefreshValue;
                    ApplyVirtualMode(width, height, refresh);
                    customPresets.Add(new ResolutionPreset(name, width, height, refresh, true));
                    SaveCustomPresets();
                    AddResolutionItems();
                    SetStatus("Saved custom resolution: " + name);
                }
                catch (Exception ex) { ShowModeError(ex); }
            }
        }

        private void ApplyNamedResolution(ResolutionPreset preset)
        {
            try
            {
                int rate = preset.IsCustom ? preset.Refresh : ReadCurrentRefreshRate();
                ApplyVirtualMode(preset.Width, preset.Height, rate);
                SetStatus("Virtual display set to " + preset.Name + " at " + rate + " Hz");
            }
            catch (Exception ex) { ShowModeError(ex); }
        }

        private void RemoveCustomResolution(ResolutionPreset preset)
        {
            ResolutionPreset removed = customPresets.FirstOrDefault(p => String.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (removed == null) return;
            customPresets.Remove(removed);
            try { SaveCustomPresets(); AddResolutionItems(); }
            catch (Exception ex)
            {
                customPresets.Add(removed);
                AddResolutionItems();
                MessageBox.Show("Could not remove the saved resolution.\r\n\r\n" + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ChooseCustomRefreshRate()
        {
            using (CustomModeDialog dialog = new CustomModeDialog(true))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                    ChangeRefreshRate((int)dialog.RefreshValue);
            }
        }

        private void ChangeResolution(int width, int height, string label)
        {
            try
            {
                int refresh = ReadCurrentRefreshRate();
                ApplyVirtualMode(width, height, refresh);
                SetStatus("Virtual display set to " + label + " at " + refresh + " Hz");
            }
            catch (Exception ex) { ShowModeError(ex); }
        }

        private void LoadCustomPresets()
        {
            try
            {
                if (!File.Exists(customResolutionPath)) return;
                XmlDocument doc = new XmlDocument();
                doc.Load(customResolutionPath);
                foreach (XmlNode node in doc.SelectNodes("/custom-resolutions/resolution"))
                {
                    string name = node.Attributes["name"] == null ? "" : node.Attributes["name"].Value.Trim();
                    int width = Int32.Parse(node.Attributes["width"].Value), height = Int32.Parse(node.Attributes["height"].Value);
                    int refresh = Int32.Parse(node.Attributes["refresh"].Value);
                    if (name.Length > 0 && name.Length <= 40 && width >= 640 && width <= 7680 && height >= 480 && height <= 4320 && refresh >= 24 && refresh <= 500 && !customPresets.Any(p => String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        customPresets.Add(new ResolutionPreset(name, width, height, refresh, true));
                }
            }
            catch (Exception ex) { Log("Could not load saved custom resolutions: " + ex.Message); }
        }

        private void SaveCustomPresets()
        {
            XmlDocument doc = new XmlDocument();
            XmlElement root = doc.CreateElement("custom-resolutions");
            doc.AppendChild(root);
            foreach (ResolutionPreset preset in customPresets)
            {
                XmlElement node = doc.CreateElement("resolution");
                node.SetAttribute("name", preset.Name);
                node.SetAttribute("width", preset.Width.ToString());
                node.SetAttribute("height", preset.Height.ToString());
                node.SetAttribute("refresh", preset.Refresh.ToString());
                root.AppendChild(node);
            }
            string temp = customResolutionPath + ".new";
            doc.Save(temp);
            if (File.Exists(customResolutionPath)) File.Replace(temp, customResolutionPath, null);
            else File.Move(temp, customResolutionPath);
        }

        private void LoadVirtualOptions()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Desktop_Screen_Manager"))
                {
                    autoVirtualEnabled = key == null || Convert.ToInt32(key.GetValue("AutoVirtualDisplay", 1)) != 0;
                    object mode = key == null ? null : key.GetValue("ManualVirtualMode");
                    if (mode != null) manualVirtualMode = Math.Max(0, Math.Min(2, Convert.ToInt32(mode)));
                    else manualVirtualMode = key != null && Convert.ToInt32(key.GetValue("ManualVirtualDisplay", 0)) != 0 ? 1 : 0;
                }
            }
            catch { autoVirtualEnabled = true; manualVirtualMode = 0; }
            SyncVirtualModeMenu();
        }

        private void SyncVirtualModeMenu()
        {
            if (automaticVirtualModeItem == null) return;
            automaticVirtualModeItem.Checked = manualVirtualMode == 0;
            forceVirtualOnItem.Checked = manualVirtualMode == 1;
            forceVirtualOffItem.Checked = manualVirtualMode == 2;
        }

        private int DesiredVirtualDisplayCount(int usablePhysical)
        {
            if (manualVirtualMode == 1) return 1;
            if (manualVirtualMode == 2) return 0;
            return autoVirtualEnabled && usablePhysical == 0 ? 1 : 0;
        }

        private void SetManualVirtualMode(int mode)
        {
            manualVirtualMode = mode;
            SyncVirtualModeMenu();
            UpdateVirtualOptions();
        }

        private void UpdateVirtualOptions()
        {
            if (changingVirtualOptions) return;
            autoVirtualEnabled = autoVirtualItem.Checked;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Desktop_Screen_Manager"))
                {
                    key.SetValue("AutoVirtualDisplay", autoVirtualEnabled ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("ManualVirtualMode", manualVirtualMode, RegistryValueKind.DWord);
                }
                lastVirtualDisplayCount = -1;
                nextVddProbeUtc = DateTime.MinValue;
                if (paused)
                {
                    DisplaySnapshot active = DisplaySnapshot.QueryActive();
                    HashSet<string> off = new HashSet<string>(monitors.Values.Where(m => IsPoweredOff(m.PowerState)).Select(m => m.DevicePath), StringComparer.OrdinalIgnoreCase);
                    int usable = CountUsablePhysical(active, off);
                    ApplyVirtualDisplayCount(DesiredVirtualDisplayCount(usable));
                }
                else Poll();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save virtual display options.\r\n\r\n" + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadVirtualOptions();
                changingVirtualOptions = true;
                autoVirtualItem.Checked = autoVirtualEnabled;
                SyncVirtualModeMenu();
                changingVirtualOptions = false;
            }
        }

        private void ChangeRefreshRate(int refresh)
        {
            try
            {
                int[] mode = ReadCurrentMode();
                ApplyVirtualMode(mode[0], mode[1], refresh);
                SetStatus("Virtual display refresh set to " + refresh + " Hz");
            }
            catch (Exception ex) { ShowModeError(ex); }
        }

        internal static int[] ReadCurrentMode()
        {
            XmlDocument doc = LoadVddSettings();
            XmlNode resolution = doc.SelectSingleNode("/vdd_settings/resolutions/resolution");
            if (resolution == null) throw new InvalidDataException("VDD settings do not contain a resolution.");
            return new int[] { ReadChildInt(resolution, "width"), ReadChildInt(resolution, "height") };
        }

        internal static int ReadCurrentRefreshRate()
        {
            XmlDocument doc = LoadVddSettings();
            XmlNode resolution = doc.SelectSingleNode("/vdd_settings/resolutions/resolution");
            if (resolution == null) return 60;
            XmlNode rate = resolution.SelectSingleNode("refresh_rate");
            int value;
            return rate != null && Int32.TryParse(rate.InnerText, out value) && value >= 24 && value <= 500 ? value : 60;
        }

        private void ApplyVirtualMode(int width, int height, int refresh)
        {
            if (width < 640 || width > 7680 || height < 480 || height > 4320)
                throw new ArgumentOutOfRangeException("width", "Resolution must be between 640x480 and 7680x4320.");
            if (refresh < 24 || refresh > 500)
                throw new ArgumentOutOfRangeException("refresh", "Refresh rate must be between 24 and 500 Hz.");

            XmlDocument doc = LoadVddSettings();
            XmlNode resolutions = doc.SelectSingleNode("/vdd_settings/resolutions");
            if (resolutions == null) throw new InvalidDataException("VDD settings are missing the resolutions section.");
            XmlNode selected = null;
            foreach (XmlNode node in resolutions.SelectNodes("resolution"))
            {
                if (ReadChildInt(node, "width") == width && ReadChildInt(node, "height") == height) { selected = node; break; }
            }
            if (selected == null)
            {
                selected = doc.CreateElement("resolution");
                AddChild(doc, selected, "width", width);
                AddChild(doc, selected, "height", height);
                AddChild(doc, selected, "refresh_rate", refresh);
            }
            else SetChildInt(doc, selected, "refresh_rate", refresh);

            resolutions.PrependChild(selected);
            foreach (ResolutionPreset preset in ResolutionPresets)
            {
                XmlNode found = null;
                foreach (XmlNode node in resolutions.SelectNodes("resolution"))
                    if (ReadChildInt(node, "width") == preset.Width && ReadChildInt(node, "height") == preset.Height) { found = node; break; }
                if (found == null)
                {
                    XmlElement node = doc.CreateElement("resolution");
                    AddChild(doc, node, "width", preset.Width);
                    AddChild(doc, node, "height", preset.Height);
                    AddChild(doc, node, "refresh_rate", 60);
                    resolutions.AppendChild(node);
                }
            }

            XmlNode global = doc.SelectSingleNode("/vdd_settings/global");
            if (global == null) throw new InvalidDataException("VDD settings are missing the global section.");
            bool hasRate = false;
            foreach (XmlNode node in global.SelectNodes("g_refresh_rate"))
                if (node.InnerText == refresh.ToString()) hasRate = true;
            if (!hasRate) AddChild(doc, global, "g_refresh_rate", refresh);

            string path = VddSettingsPath;
            string temp = path + ".new";
            doc.Save(temp);
            File.Replace(temp, path, null);

            // Reissuing the current count reloads the mode list in VDD Control.
            lastVirtualDisplayCount = -1;
            string response;
            int usable = CountUsablePhysical(DisplaySnapshot.QueryActive(), new HashSet<string>(monitors.Values.Where(m => IsPoweredOff(m.PowerState)).Select(m => m.DevicePath), StringComparer.OrdinalIgnoreCase));
            int currentCount = DesiredVirtualDisplayCount(usable);
            if (!VddPipe.Send("SETDISPLAYCOUNT " + currentCount, out response))
                throw new IOException("VDD settings were saved, but the driver did not reload them: " + response);
            lastVirtualDisplayCount = currentCount;
            UpdateConfiguredModeSummary();
            Log("Virtual display mode changed to " + width + "x" + height + " @ " + refresh + " Hz; VDD count refreshed to " + currentCount + "; response=" + response);
        }

        private static XmlDocument LoadVddSettings()
        {
            string path = VddSettingsPath;
            if (!File.Exists(path)) throw new FileNotFoundException("Could not find VDD settings at " + path, path);
            XmlDocument doc = new XmlDocument();
            doc.PreserveWhitespace = true;
            doc.Load(path);
            return doc;
        }

        private static int ReadChildInt(XmlNode parent, string name)
        {
            XmlNode child = parent.SelectSingleNode(name);
            int value;
            if (child == null || !Int32.TryParse(child.InnerText, out value)) throw new InvalidDataException("VDD settings contain an invalid " + name + " value.");
            return value;
        }

        private static void SetChildInt(XmlDocument doc, XmlNode parent, string name, int value)
        {
            XmlNode child = parent.SelectSingleNode(name);
            if (child == null) AddChild(doc, parent, name, value);
            else child.InnerText = value.ToString();
        }

        private static void AddChild(XmlDocument doc, XmlNode parent, string name, int value)
        {
            XmlElement child = doc.CreateElement(name);
            child.InnerText = value.ToString();
            parent.AppendChild(child);
        }

        private void ShowModeError(Exception ex)
        {
            Log("Virtual display mode change failed: " + ex);
            MessageBox.Show("Could not change the virtual display mode.\r\n\r\n" + ex.Message,
                ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private bool ApplySnapshot(DisplaySnapshot snapshot, string operation)
        {
            DisplaySnapshot current = DisplaySnapshot.QueryActive();
            HashSet<string> wanted = new HashSet<string>(snapshot.Paths.Select(p => Native.GetTargetDetails(p.targetInfo, Native.GetSourceName(p.sourceInfo)).DevicePath), StringComparer.OrdinalIgnoreCase);
            HashSet<string> present = new HashSet<string>(current.Paths.Select(p => Native.GetTargetDetails(p.targetInfo, Native.GetSourceName(p.sourceInfo)).DevicePath), StringComparer.OrdinalIgnoreCase);
            if (wanted.SetEquals(present)) return true;
            if (snapshot.Paths.Length == 0) return false;

            Native.DISPLAYCONFIG_PATH_INFO[] next = snapshot.Paths;
            Native.DISPLAYCONFIG_MODE_INFO[] nextModes = (Native.DISPLAYCONFIG_MODE_INFO[])snapshot.Modes.Clone();
            bool removedPrimary = current.Paths.Any(path => !wanted.Contains(Native.GetTargetDetails(path.targetInfo, Native.GetSourceName(path.sourceInfo)).DevicePath) && IsPrimary(current, path));
            if (removedPrimary) MoveOtherDisplayToPrimary(nextModes, next);

            int result = Native.SetDisplayConfig((uint)next.Length, next, (uint)nextModes.Length, nextModes,
                Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES | Native.SDC_VIRTUAL_MODE_AWARE);
            if (result != 0)
            {
                Log(operation + " failed: " + result);
                return false;
            }
            lastVirtualDisplayCount = -1;
            Log(operation + " applied to " + next.Length + " display path(s).");
            return true;
        }

        private static bool IsPrimary(DisplaySnapshot snapshot, Native.DISPLAYCONFIG_PATH_INFO path)
        {
            uint idx = path.sourceInfo.modeInfoIdx;
            if ((path.flags & 0x8) != 0) idx &= 0xFFFF;
            if (idx >= snapshot.Modes.Length || snapshot.Modes[idx].infoType != 1) return false;
            return snapshot.Modes[idx].sourceMode.position.x == 0 && snapshot.Modes[idx].sourceMode.position.y == 0;
        }

        private static void MoveOtherDisplayToPrimary(Native.DISPLAYCONFIG_MODE_INFO[] modes, Native.DISPLAYCONFIG_PATH_INFO[] remaining)
        {
            if (remaining.Length == 0) return;
            Native.DISPLAYCONFIG_PATH_INFO newPrimary = remaining[0];
            uint idx = newPrimary.sourceInfo.modeInfoIdx;
            if ((newPrimary.flags & 0x8) != 0) idx &= 0xFFFF;
            if (idx >= modes.Length || modes[idx].infoType != 1) return;
            int dx = modes[idx].sourceMode.position.x;
            int dy = modes[idx].sourceMode.position.y;
            for (int i = 0; i < modes.Length; i++)
            {
                if (modes[i].infoType == 1)
                {
                    Native.DISPLAYCONFIG_SOURCE_MODE mode = modes[i].sourceMode;
                    mode.position.x -= dx;
                    mode.position.y -= dy;
                    modes[i].sourceMode = mode;
                }
            }
        }

        private bool RestoreDisplay()
        {
            if (savedDisplay == null) return true;
            try
            {
                int result = Native.SetDisplayConfig((uint)savedDisplay.Paths.Length, savedDisplay.Paths,
                    (uint)savedDisplay.Modes.Length, savedDisplay.Modes,
                    Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES | Native.SDC_VIRTUAL_MODE_AWARE);
                if (result == 0)
                {
                    Log("Restored saved display configuration.");
                    savedDisplay = null;
                    try { if (File.Exists(statePath)) File.Delete(statePath); } catch { }
                    lastVirtualDisplayCount = -1;
                    return true;
                }
                else Log("SetDisplayConfig restore failed: " + result);
            }
            catch (Exception ex) { Log("Restore error: " + ex); }
            return false;
        }

        private void SetStatus(string value)
        {
            status = value;
            if (statusItem != null) statusItem.Text = value;
            if (tray != null) tray.Text = value.Length > 63 ? value.Substring(0, 63) : value;
        }

        private void ShowStatus()
        {
            MessageBox.Show("Status: " + status + "\r\nDDC/CI monitors tracked: " + monitors.Count +
                "\r\nVirtual Display Driver: " + (vddAvailable ? "available" : "unavailable") +
                "\r\nLog: " + logPath, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExitApplication()
        {
            timer.Stop();
            RestoreDisplay();
            SystemEvents.SessionEnding -= OnSessionEnding;
            foreach (ManagedMonitor monitor in monitors.Values) monitor.Dispose();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }

        private bool StartupLinkExists()
        {
            return File.Exists(StartupLinkPath());
        }

        private static string StartupLinkPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Desktop Screen Manager.lnk");
        }

        private void SetStartup(bool enabled)
        {
            string link = StartupLinkPath();
            if (enabled)
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(link);
                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                shortcut.Description = "Manage DDC/CI monitor power states and virtual display fallback.";
                shortcut.IconLocation = Application.ExecutablePath + ",0";
                shortcut.Save();
                Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
                Log("Created Startup shortcut.");
            }
            else
            {
                if (File.Exists(link)) File.Delete(link);
                Log("Removed Startup shortcut.");
            }
        }

        private void Log(string message)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine); }
            catch { }
        }

        private void RestoreInterruptedState()
        {
            if (!File.Exists(statePath)) return;
            try
            {
                savedDisplay = DisplaySnapshot.Load(statePath);
                Log("Found saved display state from an interrupted run; restoring it before monitoring.");
                if (!RestoreDisplay())
                {
                    Log("Saved layout is no longer valid; discarding stale recovery snapshot.");
                    savedDisplay = null;
                    try { File.Delete(statePath); } catch { }
                }
            }
            catch (Exception ex)
            {
                savedDisplay = null;
                Log("Could not restore interrupted display state: " + ex);
            }
        }

        private void SaveSnapshot(DisplaySnapshot snapshot)
        {
            snapshot.Save(statePath);
        }

        private static Icon CreateIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            using (Pen p = new Pen(Color.FromArgb(60, 205, 220), 2.2f))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(22, 35, 54)))
            using (SolidBrush led = new SolidBrush(Color.FromArgb(92, 224, 142)))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath path = RoundedRect(new Rectangle(3, 4, 26, 19), 3))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(p, path);
                }
                g.DrawLine(p, 16, 23, 16, 27);
                g.DrawLine(p, 10, 28, 22, 28);
                g.FillEllipse(led, 23, 24, 6, 6);
            }
            IntPtr hIcon = bmp.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
            Native.DestroyIcon(hIcon);
            bmp.Dispose();
            return icon;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class CustomModeDialog : Form
    {
        private readonly NumericUpDown widthInput;
        private readonly NumericUpDown heightInput;
        private readonly NumericUpDown refreshInput;
        private readonly TextBox nameInput;
        public decimal WidthValue { get { return widthInput.Value; } }
        public decimal HeightValue { get { return heightInput.Value; } }
        public decimal RefreshValue { get { return refreshInput.Value; } }
        public string NameValue { get { return nameInput == null ? "" : nameInput.Text; } }

        public CustomModeDialog(bool refreshOnly = false, bool includeName = false)
        {
            Text = refreshOnly ? "Custom refresh rate" : "Custom virtual display mode";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(340, refreshOnly ? 105 : (includeName ? 205 : 165));

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 2;
            layout.RowCount = refreshOnly ? 2 : (includeName ? 5 : 4);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            Controls.Add(layout);

            int defaultWidth = 3840, defaultHeight = 2160, defaultRefresh = 60;
            try
            {
                int[] current = GuardContext.ReadCurrentMode();
                defaultWidth = current[0]; defaultHeight = current[1]; defaultRefresh = GuardContext.ReadCurrentRefreshRate();
            }
            catch { }
            widthInput = MakeInput(640, 7680, Math.Max(640, Math.Min(7680, defaultWidth)));
            heightInput = MakeInput(480, 4320, Math.Max(480, Math.Min(4320, defaultHeight)));
            refreshInput = MakeInput(24, 500, Math.Max(24, Math.Min(500, defaultRefresh)));
            nameInput = includeName ? new TextBox { Dock = DockStyle.Fill, MaxLength = 40 } : null;
            if (!refreshOnly)
            {
                if (includeName) AddRow(layout, "Name", nameInput);
                AddRow(layout, "Width (pixels)", widthInput);
                AddRow(layout, "Height (pixels)", heightInput);
            }
            else
            {
                widthInput.Enabled = false;
                heightInput.Enabled = false;
            }
            AddRow(layout, "Refresh rate (Hz)", refreshInput);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            Button ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, AutoSize = true };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, layout.RowCount - 1);
            layout.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static NumericUpDown MakeInput(int min, int max, int value)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Dock = DockStyle.Fill, ThousandsSeparator = true };
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control input)
        {
            int row = layout.Controls.Count / 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            Label text = new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, AutoSize = false };
            layout.Controls.Add(text, 0, row);
            layout.Controls.Add(input, 1, row);
        }
    }

    internal sealed class MonitorTarget
    {
        public string DevicePath;
        public string FriendlyName;
        public string SourceName;
        public int OutputTechnology;
    }

    internal sealed class ManagedMonitor : IDisposable
    {
        public readonly string DevicePath;
        public readonly string FriendlyName;
        public readonly string SourceName;
        public readonly IntPtr Handle;
        public int PowerState = -1;
        public int ReadFailures;
        public string Description { get { return String.IsNullOrEmpty(FriendlyName) ? SourceName : FriendlyName; } }
        public ManagedMonitor(string devicePath, string friendlyName, string sourceName, IntPtr handle)
        {
            DevicePath = devicePath; FriendlyName = friendlyName; SourceName = sourceName; Handle = handle;
        }
        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                Native.DestroyPhysicalMonitors(1, new Native.PHYSICAL_MONITOR[] { new Native.PHYSICAL_MONITOR { hPhysicalMonitor = Handle } });
        }
    }

    internal static class VddPipe
    {
        private const string PipeName = "MTTVirtualDisplayPipe";

        public static bool Ping()
        {
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(500);
                    return pipe.IsConnected;
                }
            }
            catch { return false; }
        }

        public static bool Send(string command, out string response)
        {
            response = "";
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(800);
                    byte[] request = System.Text.Encoding.Unicode.GetBytes(command);
                    pipe.Write(request, 0, request.Length);
                    pipe.Flush();

                    byte[] buffer = new byte[512];
                    using (MemoryStream result = new MemoryStream())
                    {
                        while (true)
                        {
                            Task<int> readTask = pipe.ReadAsync(buffer, 0, buffer.Length);
                            if (!readTask.Wait(1500))
                            {
                                response = "Timed out waiting for driver response.";
                                return false;
                            }
                            int read = readTask.Result;
                            if (read == 0) break;
                            result.Write(buffer, 0, read);
                        }
                        response = System.Text.Encoding.Unicode.GetString(result.ToArray()).Trim('\0', ' ', '\r', '\n', '\t');
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                response = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }

    internal sealed class DisplaySnapshot
    {
        public Native.DISPLAYCONFIG_PATH_INFO[] Paths;
        public Native.DISPLAYCONFIG_MODE_INFO[] Modes;

        public void Save(string path)
        {
            using (BinaryWriter writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(1);
                writer.Write(Paths.Length);
                writer.Write(Modes.Length);
                WriteStructs(writer, Paths);
                WriteStructs(writer, Modes);
            }
        }

        public static DisplaySnapshot Load(string path)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported saved-state version.");
                int pathCount = reader.ReadInt32();
                int modeCount = reader.ReadInt32();
                if (pathCount < 1 || pathCount > 32 || modeCount < 1 || modeCount > 256)
                    throw new InvalidDataException("Saved-state dimensions are invalid.");
                return new DisplaySnapshot
                {
                    Paths = ReadStructs<Native.DISPLAYCONFIG_PATH_INFO>(reader, pathCount),
                    Modes = ReadStructs<Native.DISPLAYCONFIG_MODE_INFO>(reader, modeCount)
                };
            }
        }

        private static void WriteStructs<T>(BinaryWriter writer, T[] values) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                foreach (T item in values)
                {
                    Marshal.StructureToPtr(item, memory, false);
                    byte[] bytes = new byte[size];
                    Marshal.Copy(memory, bytes, 0, size);
                    writer.Write(bytes);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static T[] ReadStructs<T>(BinaryReader reader, int count) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            T[] values = new T[count];
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    byte[] bytes = reader.ReadBytes(size);
                    if (bytes.Length != size) throw new EndOfStreamException("Saved display state is incomplete.");
                    Marshal.Copy(bytes, 0, memory, size);
                    values[i] = (T)Marshal.PtrToStructure(memory, typeof(T));
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
            return values;
        }

        public static DisplaySnapshot QueryActive()
        {
            uint pathCount, modeCount;
            int result = Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS | Native.QDC_VIRTUAL_MODE_AWARE, out pathCount, out modeCount);
            if (result != 0) throw new InvalidOperationException("GetDisplayConfigBufferSizes failed: " + result);
            if (pathCount == 0)
                return new DisplaySnapshot { Paths = new Native.DISPLAYCONFIG_PATH_INFO[0], Modes = new Native.DISPLAYCONFIG_MODE_INFO[0] };
            while (true)
            {
                Native.DISPLAYCONFIG_PATH_INFO[] paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
                Native.DISPLAYCONFIG_MODE_INFO[] modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
                uint pc = pathCount, mc = modeCount;
                result = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS | Native.QDC_VIRTUAL_MODE_AWARE,
                    ref pc, paths, ref mc, modes, IntPtr.Zero);
                if (result == 0)
                {
                    Array.Resize(ref paths, (int)pc);
                    Array.Resize(ref modes, (int)mc);
                    return new DisplaySnapshot { Paths = paths, Modes = modes };
                }
                if (result != 122) throw new InvalidOperationException("QueryDisplayConfig failed: " + result);
                result = Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS | Native.QDC_VIRTUAL_MODE_AWARE, out pathCount, out modeCount);
                if (result != 0) throw new InvalidOperationException("GetDisplayConfigBufferSizes failed: " + result);
            }
        }
    }

    internal static class Native
    {
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        public const uint QDC_VIRTUAL_MODE_AWARE = 0x10;
        public const uint SDC_APPLY = 0x80;
        public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        public const uint SDC_ALLOW_CHANGES = 0x400;
        public const uint SDC_VIRTUAL_MODE_AWARE = 0x8000;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] public struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr dwData);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprc, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, out byte type, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int SetDisplayConfig(uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[] pathArray, uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

        public static string GetSourceName(DISPLAYCONFIG_PATH_SOURCE_INFO source)
        {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            name.header.type = 1; name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            name.header.adapterId = source.adapterId; name.header.id = source.id;
            int result = DisplayConfigGetDeviceInfo(ref name);
            return result == 0 ? name.viewGdiDeviceName ?? "" : "";
        }

        public static MonitorTarget GetTargetDetails(DISPLAYCONFIG_PATH_TARGET_INFO target, string sourceName)
        {
            DISPLAYCONFIG_TARGET_DEVICE_NAME name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            name.header.type = 2; name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
            name.header.adapterId = target.adapterId; name.header.id = target.id;
            int result = DisplayConfigGetDeviceInfo(ref name);
            string fallback = "DISPLAYCONFIG:" + target.adapterId.ToString() + ":" + target.id.ToString();
            return new MonitorTarget
            {
                DevicePath = result == 0 && !String.IsNullOrEmpty(name.monitorDevicePath) ? name.monitorDevicePath : fallback,
                FriendlyName = result == 0 ? name.monitorFriendlyDeviceName ?? "" : "",
                SourceName = sourceName ?? "",
                OutputTechnology = target.outputTechnology
            };
        }

        // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED/INDIRECT_VIRTUAL.
        public static bool IsIndirectDisplay(int outputTechnology)
        {
            return outputTechnology == 16 || outputTechnology == 17;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)] public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type; public uint size; public long adapterId; public uint id;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags; public int outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public long adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public long adapterId; public uint id; public uint modeInfoIdx; public int outputTechnology; public int rotation; public int scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate; public int scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }
        [StructLayout(LayoutKind.Explicit, Pack = 4)] public struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public uint infoType; [FieldOffset(4)] public uint id; [FieldOffset(8)] public long adapterId;
            [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
            [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE targetMode;
            [FieldOffset(16)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width; public uint height; public uint pixelFormat; public POINTL position;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct POINTL { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_TARGET_MODE { public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo; }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate; public DISPLAYCONFIG_RATIONAL hSyncFreq; public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize; public DISPLAYCONFIG_2DREGION totalSize; public uint videoStandard; public uint scanLineOrdering;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
        {
            public POINTL PathSourceSize; public RECTL DesktopImageRegion; public RECTL DesktopImageClip;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct RECTL { public int left; public int top; public int right; public int bottom; }
    }
}
