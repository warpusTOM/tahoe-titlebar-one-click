using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TahoeTitlebarOneClick;

public sealed class TahoeInstaller
{
    private const string KnownOriginalApplicationFrameSha256 = "FF079E7A4B4DC31E458D179923F50F3FF15F8EB9E2E19A89D7BFB2EE0E4AE47A";
    private const string KnownPatchedApplicationFrameSha256 = "E714259B03ECBE49E2A6F04AC471519F7D2E002CA50FA519A01B7B198D86DBA2";

    private readonly Action<string> log;
    private readonly Action<int> progress;
    private readonly string programDataRoot;
    private readonly string backupRoot;
    private readonly List<BackupEntry> backupEntries = [];
    private string currentBackupDir = "";

    public TahoeInstaller(Action<string> log, Action<int> progress)
    {
        this.log = log;
        this.progress = progress;
        programDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "JhonLloydMolino",
            "TahoeTitlebar");
        backupRoot = Path.Combine(programDataRoot, "Backups");
    }

    public Task Install()
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(programDataRoot);
            Directory.CreateDirectory(backupRoot);
            currentBackupDir = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(Path.Combine(currentBackupDir, "files"));
            Directory.CreateDirectory(Path.Combine(currentBackupDir, "registry"));

            Step(3, "Backup folder: " + currentBackupDir);
            BackupRegistry();

            Step(10, "Installing TahoeTraffic theme package...");
            InstallThemePackage();

            Step(23, "Applying dark glass/titlebar registry settings...");
            InstallRegistrySettings();

            Step(32, "Applying Tahoe StartAllBack taskbar/start menu profile...");
            var restartExplorer = InstallStartAllBackProfile();

            Step(42, "Forcing browser native Windows titlebars...");
            InstallBrowsers();

            Step(62, "Forcing Windows Terminal to use OS titlebar buttons...");
            InstallTerminal();

            Step(74, "Applying Settings/UWP ApplicationFrame patch with safety checks...");
            InstallApplicationFramePatch();

            Step(88, "Saving backup manifest...");
            SaveBackupManifest();

            Step(94, "Refreshing Windows shell settings...");
            BroadcastSettingChange();
            RunQuiet("rundll32.exe", "user32.dll,UpdatePerUserSystemParameters", ignoreExitCode: true);
            if (restartExplorer)
            {
                RestartExplorer();
            }

            Step(100, "Tahoe titlebar install finished.");
        });
    }

    public Task RestoreLatestBackup()
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(backupRoot))
            {
                throw new InvalidOperationException("No backup folder exists yet.");
            }

            var latest = Directory.GetDirectories(backupRoot)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (latest == null)
            {
                throw new InvalidOperationException("No backups found.");
            }

            Step(5, "Latest backup: " + latest);
            CloseProcesses(["brave", "chrome", "msedge", "WindowsTerminal", "SystemSettings", "ApplicationFrameHost"]);

            var manifest = Path.Combine(latest, "backup-manifest.json");
            if (File.Exists(manifest))
            {
                var entries = JsonSerializer.Deserialize<List<BackupEntry>>(File.ReadAllText(manifest)) ?? [];
                var total = Math.Max(entries.Count, 1);
                var index = 0;
                foreach (var entry in entries)
                {
                    index++;
                    if (!File.Exists(entry.BackupPath))
                    {
                        continue;
                    }

                    if (entry.OriginalPath.EndsWith(@"\System32\ApplicationFrame.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        PrepareSystemFileForWrite(entry.OriginalPath);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
                    File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                    progress(5 + (int)(70.0 * index / total));
                    log("Restored file: " + entry.OriginalPath);
                }
            }

            var regDir = Path.Combine(latest, "registry");
            if (Directory.Exists(regDir))
            {
                foreach (var regFile in Directory.GetFiles(regDir, "*.reg").OrderBy(p => p))
                {
                    RunQuiet("reg.exe", $"import \"{regFile}\"", ignoreExitCode: true);
                    log("Imported registry backup: " + regFile);
                }
            }

            BroadcastSettingChange();
            RestartExplorer();
            Step(100, "Restore finished. Restart Windows for the safest full rollback.");
        });
    }

    private void InstallThemePackage()
    {
        if (!HasResourceOrSidecar("TahoeTraffic.theme") || !HasResourceOrSidecar("TahoeTraffic.msstyles"))
        {
            log("Skipped Tahoe theme package: TahoeTraffic.theme/msstyles assets are not embedded or beside the EXE.");
            log(@"Put redistributable assets in .\Assets\ beside the EXE, or build a private package with embedded assets.");
            return;
        }

        var themeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Themes");
        var tahoeDir = Path.Combine(themeDir, "TahoeTraffic");
        var themePath = Path.Combine(themeDir, "TahoeTraffic.theme");
        var msstylesPath = Path.Combine(tahoeDir, "TahoeTraffic.msstyles");

        Directory.CreateDirectory(tahoeDir);
        BackupFile(themePath);
        BackupFile(msstylesPath);
        WriteResourceToFile("TahoeTraffic.theme", themePath);
        WriteResourceToFile("TahoeTraffic.msstyles", msstylesPath);

        using var themes = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes");
        themes?.SetValue("InstallVisualStyleColor", "NormalColor", RegistryValueKind.String);
        themes?.SetValue("InstallVisualStyleSize", "NormalSize", RegistryValueKind.String);
        themes?.SetValue("CurrentTheme", themePath, RegistryValueKind.String);

        using var personalize = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        personalize?.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);
        personalize?.SetValue("SystemUsesLightTheme", 0, RegistryValueKind.DWord);
        personalize?.SetValue("EnableTransparency", 1, RegistryValueKind.DWord);

        TryShellOpen(themePath);
        log("Theme package installed: " + themePath);
    }

    private void InstallRegistrySettings()
    {
        using (var dwm = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM"))
        {
            dwm?.SetValue("ColorizationGlassAttribute", 1, RegistryValueKind.DWord);
            dwm?.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord);
            dwm?.SetValue("AccentColor", unchecked((int)0xFF3A3A3A), RegistryValueKind.DWord);
            dwm?.SetValue("ColorizationColor", unchecked((int)0xC43A3A3A), RegistryValueKind.DWord);
            dwm?.SetValue("ColorizationColorBalance", 89, RegistryValueKind.DWord);
            dwm?.SetValue("ColorizationAfterglow", unchecked((int)0xC43A3A3A), RegistryValueKind.DWord);
            dwm?.SetValue("ColorizationAfterglowBalance", 10, RegistryValueKind.DWord);
            dwm?.SetValue("ColorizationBlurBalance", 1, RegistryValueKind.DWord);
            dwm?.SetValue("EnableWindowColorization", 0, RegistryValueKind.DWord);
        }

        using var metrics = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop\WindowMetrics");
        SetString(metrics, "BorderWidth", "-15");
        SetString(metrics, "CaptionHeight", "-330");
        SetString(metrics, "CaptionWidth", "-330");
        SetString(metrics, "SmCaptionHeight", "-330");
        SetString(metrics, "SmCaptionWidth", "-330");
        SetString(metrics, "PaddedBorderWidth", "-60");
        SetString(metrics, "ScrollHeight", "-255");
        SetString(metrics, "ScrollWidth", "-255");
        SetString(metrics, "MenuHeight", "-285");
        SetString(metrics, "MenuWidth", "-285");
        SetString(metrics, "MinAnimate", "1");
    }

    private bool InstallStartAllBackProfile()
    {
        using var existingStartIsBack = Registry.CurrentUser.OpenSubKey(@"Software\StartIsBack");
        var installed = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StartAllBack")) ||
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StartAllBack", "StartAllBackCfg.exe")) ||
            existingStartIsBack != null;

        if (!installed)
        {
            log("StartAllBack not detected, skipped taskbar/start menu profile.");
            log("Install StartAllBack separately, then run this tool again to apply the Tahoe taskbar profile.");
            return false;
        }

        var orbPath = InstallTahoeOrb();

        using (var startIsBack = Registry.CurrentUser.CreateSubKey(@"Software\StartIsBack"))
        {
            SetDword(startIsBack, "NavBarGlass", 1);
            SetDword(startIsBack, "TaskbarTranslucentEffect", 3);
            SetDword(startIsBack, "TaskbarColoring", 1);
            SetDword(startIsBack, "TaskbarColor", 659480);
            SetDword(startIsBack, "TaskbarAlpha", 26);
            SetDword(startIsBack, "TaskbarBlur", 0);
            SetDword(startIsBack, "StartMenuColoring", 1);
            SetDword(startIsBack, "StartMenuColor", 659480);
            SetDword(startIsBack, "StartMenuAlpha", 36);
            SetDword(startIsBack, "StartMenuBlur", 0);
            SetDword(startIsBack, "FrameStyle", 1);
            SetDword(startIsBack, "TaskbarOneSegment", 0);
            SetDword(startIsBack, "TaskbarCenterIcons", 2);
            SetDword(startIsBack, "FatTaskbar", 2);
            SetDword(startIsBack, "TaskbarLargerIcons", 0);
            SetDword(startIsBack, "TaskbarSpacierIcons", unchecked((int)0xFFFFFFFE));
            SetDword(startIsBack, "Start_MinMFU", 10);
            SetDword(startIsBack, "SettingsVersion", 6);
            SetDword(startIsBack, "WelcomeShown", 3);
            startIsBack?.SetValue("OrbBitmap", orbPath, RegistryValueKind.String);
        }

        using (var advanced = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
        {
            SetDword(advanced, "TaskbarMn", 0);
            SetDword(advanced, "TaskbarAl", 0);
            SetDword(advanced, "ShowTaskViewButton", 0);
            SetDword(advanced, "ShowCopilotButton", 0);
            SetDword(advanced, "Start_AccountNotifications", 0);
            SetDword(advanced, "TaskbarSmallIcons", 0);
            SetDword(advanced, "TaskbarGlomLevel", 0);
            SetDword(advanced, "MMTaskbarGlomLevel", 0);
            SetDword(advanced, "UseOLEDTaskbarTransparency", 1);
            SetDword(advanced, "DisablePreviewDesktop", 1);
        }

        log("Applied StartAllBack Tahoe taskbar profile.");
        log("Taskbar alpha=26, blur=0; Start menu alpha=36, blur=0; centered/spaced taskbar icons enabled.");
        log("Tahoe orb installed: " + orbPath);
        return true;
    }

    private string InstallTahoeOrb()
    {
        var orbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StartAllBack", "Orbs");
        Directory.CreateDirectory(orbDir);
        var orbPath = Path.Combine(orbDir, "Tahoe Traffic Orb.bmp");

        using var bitmap = new Bitmap(54, 162);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        DrawOrbState(graphics, new Rectangle(0, 0, 54, 54), Color.FromArgb(18, 22, 28), 0);
        DrawOrbState(graphics, new Rectangle(0, 54, 54, 54), Color.FromArgb(28, 36, 44), 3);
        DrawOrbState(graphics, new Rectangle(0, 108, 54, 54), Color.FromArgb(10, 12, 16), -2);
        bitmap.Save(orbPath, System.Drawing.Imaging.ImageFormat.Bmp);
        return orbPath;
    }

    private static void DrawOrbState(Graphics graphics, Rectangle bounds, Color background, int lift)
    {
        var outer = new Rectangle(bounds.X + 5, bounds.Y + 5 + lift, 44, 44);
        using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
        using var bg = new SolidBrush(background);
        using var edge = new Pen(Color.FromArgb(130, 210, 225, 235), 1f);
        using var hi = new Pen(Color.FromArgb(65, 255, 255, 255), 1f);

        graphics.FillEllipse(shadow, outer.X + 1, outer.Y + 2, outer.Width, outer.Height);
        graphics.FillEllipse(bg, outer);
        graphics.DrawEllipse(edge, outer);
        graphics.DrawArc(hi, outer.X + 7, outer.Y + 6, outer.Width - 14, outer.Height - 14, 205, 130);

        var y = outer.Y + 18;
        var x = outer.X + 12;
        using var red = new SolidBrush(Color.FromArgb(232, 89, 83));
        using var yellow = new SolidBrush(Color.FromArgb(218, 195, 82));
        using var green = new SolidBrush(Color.FromArgb(39, 184, 96));
        graphics.FillEllipse(red, x, y, 6, 6);
        graphics.FillEllipse(yellow, x + 13, y, 6, 6);
        graphics.FillEllipse(green, x + 26, y, 6, 6);
    }

    private void InstallBrowsers()
    {
        CloseProcesses(["brave", "chrome", "msedge"]);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var brave = new BrowserInfo(
            "Brave",
            Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            "--enable-features=Windows11MicaTitlebar --disable-windows10-custom-titlebar",
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Preferences"),
            [
                RegistryPath.LocalMachine64(@"SOFTWARE\Clients\StartMenuInternet\Brave\shell\open\command"),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\BraveHTML\shell\open\command", true),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\BraveFile\shell\open\command", true),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\BravePDF\shell\open\command", true),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\brave-browser\shell\open\command", true)
            ],
            GetShortcutPaths("Brave.lnk", includeTaskbar: true, includeUserDesktop: false));

        var chrome = new BrowserInfo(
            "Google Chrome",
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            "--enable-features=Windows11MicaTitlebar --disable-windows10-custom-titlebar",
            Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Preferences"),
            [
                RegistryPath.LocalMachine64(@"SOFTWARE\Clients\StartMenuInternet\Google Chrome\shell\open\command"),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\ChromeHTML\shell\open\command", true),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\ChromePDF\shell\open\command", true)
            ],
            GetShortcutPaths("Google Chrome.lnk", includeTaskbar: false, includeUserDesktop: false));

        var edge = new BrowserInfo(
            "Microsoft Edge",
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            "--enable-features=Windows11MicaTitlebar,msVisualRejuvMica,MicaBackdropEnabled,MicaEnabled --disable-windows10-custom-titlebar",
            Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Preferences"),
            [
                RegistryPath.LocalMachine64(@"SOFTWARE\Clients\StartMenuInternet\Microsoft Edge\shell\open\command"),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\MSEdgeHTM\shell\open\command", true),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\MSEdgeMHT\shell\open\command", true),
                RegistryPath.LocalMachine64(@"SOFTWARE\Classes\MSEdgePDF\shell\open\command", true)
            ],
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Microsoft Edge.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Microsoft Edge.lnk")
            ]);

        foreach (var browser in new[] { brave, chrome, edge })
        {
            InstallBrowser(browser);
        }
    }

    private void InstallBrowser(BrowserInfo browser)
    {
        log("Browser: " + browser.Name);
        if (File.Exists(browser.PreferencesPath))
        {
            BackupFile(browser.PreferencesPath);
            var root = JsonNode.Parse(File.ReadAllText(browser.PreferencesPath))?.AsObject() ?? [];
            var browserNode = root["browser"] as JsonObject;
            if (browserNode == null)
            {
                browserNode = [];
                root["browser"] = browserNode;
            }
            browserNode["custom_chrome_frame"] = false;
            browserNode["chrome_custom_frame"] = false;
            File.WriteAllText(browser.PreferencesPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log("Updated preferences: " + browser.PreferencesPath);
        }
        else
        {
            log("Preferences missing, skipped: " + browser.PreferencesPath);
        }

        if (!File.Exists(browser.ExePath))
        {
            log("Browser EXE missing, skipped shortcut/registry command updates: " + browser.ExePath);
            return;
        }

        foreach (var shortcut in browser.ShortcutPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(shortcut))
            {
                continue;
            }
            BackupFile(shortcut);
            UpdateShortcut(shortcut, browser.ExePath, browser.Arguments);
            log("Updated shortcut: " + shortcut);
        }

        foreach (var regPath in browser.RegistryCommands)
        {
            var command = $"\"{browser.ExePath}\" {browser.Arguments}";
            if (regPath.IsFileClass)
            {
                command += " --single-argument %1";
            }
            SetDefaultRegistryValue(regPath, command);
            log("Updated registry command: " + regPath.SubKey);
        }
    }

    private void InstallTerminal()
    {
        CloseProcesses(["WindowsTerminal"]);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settings = Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
        if (!File.Exists(settings))
        {
            log("Windows Terminal settings missing, skipped: " + settings);
            return;
        }

        BackupFile(settings);
        var root = JsonNode.Parse(File.ReadAllText(settings))?.AsObject() ?? [];
        root["showTabsInTitlebar"] = false;
        root["alwaysShowTabs"] = false;
        root["showTerminalTitleInTitlebar"] = true;
        root["theme"] = "system";
        File.WriteAllText(settings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        log("Updated Windows Terminal settings: " + settings);
    }

    private void InstallApplicationFramePatch()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ApplicationFrame.dll");
        if (!File.Exists(target))
        {
            log("ApplicationFrame.dll missing, skipped.");
            return;
        }

        var currentHash = Sha256(target);
        if (currentHash.Equals(KnownPatchedApplicationFrameSha256, StringComparison.OrdinalIgnoreCase))
        {
            log("ApplicationFrame.dll is already patched.");
            return;
        }

        if (!currentHash.Equals(KnownOriginalApplicationFrameSha256, StringComparison.OrdinalIgnoreCase))
        {
            log("Skipped ApplicationFrame.dll: unsupported Windows build/hash.");
            log("Current SHA256: " + currentHash);
            log("This protects the laptop from a wrong system DLL. Browser/theme parts still apply.");
            return;
        }

        if (!HasResourceOrSidecar("ApplicationFrame.dll.patched"))
        {
            log("Skipped ApplicationFrame.dll: no patched DLL asset is embedded or beside the EXE.");
            log("For public builds, do not redistribute Microsoft system DLLs. Use a private local asset if you own the test machine.");
            return;
        }

        BackupFile(target);
        var temp = Path.Combine(programDataRoot, "ApplicationFrame.dll.patched");
        WriteResourceToFile("ApplicationFrame.dll.patched", temp);
        var patchedHash = Sha256(temp);
        if (!patchedHash.Equals(KnownPatchedApplicationFrameSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Embedded ApplicationFrame patch hash does not match expected patch.");
        }

        CloseProcesses(["SystemSettings", "ApplicationFrameHost"]);
        PrepareSystemFileForWrite(target);
        try
        {
            File.Copy(temp, target, overwrite: true);
        }
        catch
        {
            RunQuiet("cmd.exe", $"/c copy /Y \"{temp}\" \"{target}\"", ignoreExitCode: false);
        }
        log("Patched Settings/UWP titlebar file: " + target);
    }

    private void PrepareSystemFileForWrite(string path)
    {
        var aclPath = Path.Combine(string.IsNullOrWhiteSpace(currentBackupDir) ? programDataRoot : currentBackupDir, "ApplicationFrame.dll.icacls.txt");
        RunQuiet("cmd.exe", $"/c icacls \"{path}\" > \"{aclPath}\"", ignoreExitCode: true);
        RunQuiet("takeown.exe", $"/f \"{path}\" /a", ignoreExitCode: true);
        RunQuiet("icacls.exe", $"\"{path}\" /grant Administrators:F", ignoreExitCode: true);
    }

    private void BackupRegistry()
    {
        var regDir = Path.Combine(currentBackupDir, "registry");
        var keys = new Dictionary<string, string>
        {
            ["HKCU-Themes.reg"] = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes",
            ["HKCU-DWM.reg"] = @"HKCU\Software\Microsoft\Windows\DWM",
            ["HKCU-WindowMetrics.reg"] = @"HKCU\Control Panel\Desktop\WindowMetrics",
            ["HKCU-StartIsBack.reg"] = @"HKCU\Software\StartIsBack",
            ["HKCU-Explorer-Advanced.reg"] = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            ["HKCU-Explorer-StuckRects3.reg"] = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3",
            ["HKLM-Client-Brave.reg"] = @"HKLM\SOFTWARE\Clients\StartMenuInternet\Brave",
            ["HKLM-Client-Chrome.reg"] = @"HKLM\SOFTWARE\Clients\StartMenuInternet\Google Chrome",
            ["HKLM-Client-Edge.reg"] = @"HKLM\SOFTWARE\Clients\StartMenuInternet\Microsoft Edge",
            ["HKLM-Class-BraveHTML.reg"] = @"HKLM\SOFTWARE\Classes\BraveHTML",
            ["HKLM-Class-BraveFile.reg"] = @"HKLM\SOFTWARE\Classes\BraveFile",
            ["HKLM-Class-BravePDF.reg"] = @"HKLM\SOFTWARE\Classes\BravePDF",
            ["HKLM-Class-brave-browser.reg"] = @"HKLM\SOFTWARE\Classes\brave-browser",
            ["HKLM-Class-ChromeHTML.reg"] = @"HKLM\SOFTWARE\Classes\ChromeHTML",
            ["HKLM-Class-ChromePDF.reg"] = @"HKLM\SOFTWARE\Classes\ChromePDF",
            ["HKLM-Class-MSEdgeHTM.reg"] = @"HKLM\SOFTWARE\Classes\MSEdgeHTM",
            ["HKLM-Class-MSEdgeMHT.reg"] = @"HKLM\SOFTWARE\Classes\MSEdgeMHT",
            ["HKLM-Class-MSEdgePDF.reg"] = @"HKLM\SOFTWARE\Classes\MSEdgePDF"
        };

        foreach (var (file, key) in keys)
        {
            var outFile = Path.Combine(regDir, file);
            RunQuiet("reg.exe", $"export \"{key}\" \"{outFile}\" /y", ignoreExitCode: true);
        }
    }

    private void RestartExplorer()
    {
        log("Restarting Explorer to refresh taskbar/start menu style...");
        RunQuiet("taskkill.exe", "/F /IM explorer.exe", ignoreExitCode: true);
        Thread.Sleep(1000);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log("Explorer restart fallback needed: " + ex.Message);
        }
    }

    private void BackupFile(string path)
    {
        if (string.IsNullOrWhiteSpace(currentBackupDir) || !File.Exists(path))
        {
            return;
        }

        var backupFile = Path.Combine(currentBackupDir, "files", SafeFileName(path));
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.Copy(path, backupFile, overwrite: true);
        backupEntries.Add(new BackupEntry(path, backupFile));
    }

    private void SaveBackupManifest()
    {
        if (string.IsNullOrWhiteSpace(currentBackupDir))
        {
            return;
        }
        var manifest = Path.Combine(currentBackupDir, "backup-manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(backupEntries, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteResourceToFile(string shortName, string path)
    {
        var resourceName = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + shortName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            var sidecar = FindSidecarAsset(shortName);
            if (sidecar == null)
            {
                throw new FileNotFoundException("Asset missing: " + shortName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(sidecar, path, overwrite: true);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Could not open resource: " + resourceName);
        using var output = File.Create(path);
        input.CopyTo(output);
    }

    private static bool HasResourceOrSidecar(string shortName)
    {
        return Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Any(n => n.EndsWith("." + shortName, StringComparison.OrdinalIgnoreCase)) ||
            FindSidecarAsset(shortName) != null;
    }

    private static string? FindSidecarAsset(string shortName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", shortName),
            Path.Combine(Environment.CurrentDirectory, "Assets", shortName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void UpdateShortcut(string shortcutPath, string targetPath, string arguments)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell COM is unavailable.");
        var shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
            ?? throw new InvalidOperationException("Could not open shortcut: " + shortcutPath);
        var shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
        shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [arguments]);
        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{targetPath},0"]);
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, []);
    }

    private static void SetDefaultRegistryValue(RegistryPath path, string value)
    {
        using var root = RegistryKey.OpenBaseKey(path.Hive, path.View);
        using var key = root.CreateSubKey(path.SubKey, writable: true);
        key?.SetValue("", value, RegistryValueKind.String);
    }

    private static string[] GetShortcutPaths(string fileName, bool includeTaskbar, bool includeUserDesktop)
    {
        var paths = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), fileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", fileName)
        };

        if (includeUserDesktop)
        {
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName));
        }

        if (includeTaskbar)
        {
            paths.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar", fileName));
        }

        return [.. paths];
    }

    private void CloseProcesses(string[] names)
    {
        foreach (var name in names)
        {
            RunQuiet("taskkill.exe", $"/F /IM {name}.exe /T", ignoreExitCode: true);
        }
    }

    private void RunQuiet(string fileName, string arguments, bool ignoreExitCode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start: " + fileName);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!ignoreExitCode && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {stderr}{stdout}");
        }
    }

    private static void TryShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // File and registry installation still completed.
        }
    }

    private static void BroadcastSettingChange()
    {
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    private static void SetString(RegistryKey? key, string name, string value)
    {
        key?.SetValue(name, value, RegistryValueKind.String);
    }

    private static void SetDword(RegistryKey? key, string name, int value)
    {
        key?.SetValue(name, value, RegistryValueKind.DWord);
    }

    private void Step(int percent, string message)
    {
        progress(percent);
        log(message);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SafeFileName(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = path.Select(c => invalid.Contains(c) || c == ':' || c == '\\' || c == '/' ? '_' : c).ToArray();
        return new string(chars);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    private sealed record BrowserInfo(
        string Name,
        string ExePath,
        string Arguments,
        string PreferencesPath,
        RegistryPath[] RegistryCommands,
        string[] ShortcutPaths);

    private sealed record RegistryPath(RegistryHive Hive, RegistryView View, string SubKey, bool IsFileClass = false)
    {
        public static RegistryPath LocalMachine64(string subKey, bool isFileClass = false)
        {
            return new RegistryPath(RegistryHive.LocalMachine, RegistryView.Registry64, subKey, isFileClass);
        }
    }

    private sealed record BackupEntry(string OriginalPath, string BackupPath);
}
