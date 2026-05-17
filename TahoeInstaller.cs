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
    private static readonly SupportedApplicationFramePatch[] SupportedApplicationFramePatches =
    [
        new(
            Id: "legacy-private-test",
            WindowsBuild: "Add exact build after verification",
            OriginalSha256: KnownOriginalApplicationFrameSha256,
            PatchedSha256: KnownPatchedApplicationFrameSha256,
            PatchedAssetName: "ApplicationFrame.dll.patched")
    ];

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

    public Task<DiagnosticsReport> Diagnose()
    {
        return Task.Run(() =>
        {
            var report = BuildDiagnostics(createAssetsFolder: false);
            LogDiagnostics(report);
            return report;
        });
    }

    public Task<InstallReport> FixEverythingAutomatically()
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(programDataRoot);
            Directory.CreateDirectory(backupRoot);
            currentBackupDir = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(Path.Combine(currentBackupDir, "files"));
            Directory.CreateDirectory(Path.Combine(currentBackupDir, "registry"));

            var diagnostics = BuildDiagnostics(createAssetsFolder: true);
            var report = new InstallReport(diagnostics)
            {
                BackupPath = currentBackupDir,
                RestoreAvailable = true
            };

            Step(3, "Auto diagnosis before install...");
            LogDiagnostics(diagnostics);

            Step(8, "Backup folder: " + currentBackupDir);
            TryApply("Registry backup", BackupRegistry, report);

            Step(15, "Installing TahoeTraffic theme package when available...");
            TryApply("Theme package", () => InstallThemePackage(report), report);

            Step(25, "Applying dark glass/titlebar registry settings...");
            TryApply("Windows titlebar registry settings", () =>
            {
                InstallRegistrySettings();
                report.RegistryConfigured = true;
            }, report);

            Step(35, "Applying Tahoe StartAllBack taskbar/start menu profile if installed...");
            var restartExplorer = false;
            TryApply("StartAllBack profile", () =>
            {
                restartExplorer = InstallStartAllBackProfile(report);
            }, report);

            Step(48, "Forcing browser native Windows titlebars...");
            TryApply("Browser titlebars", () => InstallBrowsers(report), report);

            Step(62, "Forcing Windows Terminal to use OS titlebar buttons...");
            TryApply("Windows Terminal", () => InstallTerminal(report), report);

            Step(74, "Applying Settings/UWP ApplicationFrame patch only when supported...");
            TryApply("Settings/UWP patch", () => InstallApplicationFramePatch(report), report);

            Step(86, "Saving backup manifest...");
            TryApply("Backup manifest", SaveBackupManifest, report);

            Step(92, "Refreshing Windows shell settings...");
            TryApply("Shell refresh", () =>
            {
                BroadcastSettingChange();
                RunQuiet("rundll32.exe", "user32.dll,UpdatePerUserSystemParameters", ignoreExitCode: true);
                if (restartExplorer)
                {
                    RestartExplorer();
                    report.RestartRequired = true;
                }
            }, report);

            report.FinalStatus = DetermineFinalStatus(report);
            Step(98, FinalStatusMessage(report));
            WriteFinalReport(report);
            Step(100, "Final report opened: " + report.ReportPath);
            return report;
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

    private DiagnosticsReport BuildDiagnostics(bool createAssetsFolder)
    {
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (createAssetsFolder)
        {
            Directory.CreateDirectory(assetsDir);
        }

        var windows = GetWindowsBuildInfo();
        var applicationFramePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ApplicationFrame.dll");
        var applicationFrameHash = File.Exists(applicationFramePath) ? Sha256(applicationFramePath) : "";
        var supportedPatch = SupportedApplicationFramePatches.FirstOrDefault(p =>
            applicationFrameHash.Equals(p.OriginalSha256, StringComparison.OrdinalIgnoreCase));
        var alreadyPatched = SupportedApplicationFramePatches.Any(p =>
            applicationFrameHash.Equals(p.PatchedSha256, StringComparison.OrdinalIgnoreCase));

        var report = new DiagnosticsReport
        {
            WindowsVersion = windows,
            AssetsDirectory = assetsDir,
            ApplicationFramePath = applicationFramePath,
            ApplicationFrameSha256 = string.IsNullOrWhiteSpace(applicationFrameHash) ? "missing" : applicationFrameHash,
            ApplicationFrameSupported = supportedPatch != null || alreadyPatched,
            ApplicationFrameAlreadyPatched = alreadyPatched,
            ApplicationFrameSupportNote = supportedPatch != null
                ? $"Supported patch table match: {supportedPatch.Id} ({supportedPatch.WindowsBuild})"
                : alreadyPatched
                    ? "Current ApplicationFrame.dll already matches a supported patched hash."
                    : "Unsupported build/hash. Settings/UWP patch will be skipped.",
            TahoeThemeAsset = FindAsset("TahoeTraffic.theme"),
            TahoeMsstylesAsset = FindAsset("TahoeTraffic.msstyles"),
            ApplicationFramePatchedAsset = FindAsset("ApplicationFrame.dll.patched"),
            StartAllBackInstalled = IsStartAllBackInstalled(),
            WindowsTerminalSettingsPath = GetWindowsTerminalSettingsPath(),
            BrowserProfiles = GetBrowserDiagnostics()
        };

        report.WindowsTerminalSettingsExists = File.Exists(report.WindowsTerminalSettingsPath);
        report.CanInstallTheme = report.TahoeThemeAsset.Exists && report.TahoeMsstylesAsset.Exists;
        report.CanPatchApplicationFrame =
            supportedPatch != null &&
            report.ApplicationFramePatchedAsset.Exists;

        return report;
    }

    private void LogDiagnostics(DiagnosticsReport report)
    {
        log("=== Tahoe Auto Diagnose ===");
        log("Windows: " + report.WindowsVersion);
        log("Assets folder: " + report.AssetsDirectory);
        log("TahoeTraffic.theme: " + report.TahoeThemeAsset.Describe());
        log("TahoeTraffic.msstyles: " + report.TahoeMsstylesAsset.Describe());
        log("ApplicationFrame.dll.patched: " + report.ApplicationFramePatchedAsset.Describe());
        log("ApplicationFrame.dll SHA256: " + report.ApplicationFrameSha256);
        log("ApplicationFrame support: " + report.ApplicationFrameSupportNote);
        log("StartAllBack installed: " + YesNo(report.StartAllBackInstalled));
        log("Windows Terminal settings: " + (report.WindowsTerminalSettingsExists ? report.WindowsTerminalSettingsPath : "missing"));
        foreach (var browser in report.BrowserProfiles)
        {
            log($"{browser.Name}: exe={YesNo(browser.ExeExists)}, profiles={browser.ProfileCount}, shortcuts={browser.ShortcutCount}");
        }

        log("Can install core theme/msstyles: " + YesNo(report.CanInstallTheme));
        log("Can patch Settings/UWP safely: " + YesNo(report.CanPatchApplicationFrame));
        if (!report.CanInstallTheme)
        {
            log("Public build note: full min/max/close replacement needs user-provided or private embedded TahoeTraffic.theme/msstyles assets.");
        }
        if (!report.ApplicationFrameSupported)
        {
            log("Settings/UWP patch skipped: current ApplicationFrame.dll hash is not in the supported-build table.");
        }
    }

    private static string DetermineFinalStatus(InstallReport report)
    {
        if (report.ThemeInstalled && report.MsstylesInstalled)
        {
            return "Full";
        }

        if (report.AnySafeChangeApplied)
        {
            return "Partial";
        }

        return "Failed";
    }

    private static string FinalStatusMessage(InstallReport report)
    {
        return report.FinalStatus switch
        {
            "Full" => "Full Tahoe titlebar installed.",
            "Partial" => "Partial install completed. Core theme assets were skipped or unavailable.",
            _ => "Install failed. No supported Tahoe changes were applied."
        };
    }

    private void WriteFinalReport(InstallReport report)
    {
        var reportPath = Path.Combine(currentBackupDir, "final-report.txt");
        report.ReportPath = reportPath;
        File.WriteAllText(reportPath, report.ToText(), System.Text.Encoding.UTF8);
        log("=== Final Report ===");
        foreach (var line in report.ToText().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            log(line);
        }

        TryShellOpen(reportPath);
    }

    private bool TryApply(string name, Action action, InstallReport report)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            var message = $"{name} failed: {ex.Message}";
            report.Errors.Add(message);
            log("FAILED: " + message);
            return false;
        }
    }

    private void InstallThemePackage(InstallReport report)
    {
        if (!HasResourceOrSidecar("TahoeTraffic.theme") || !HasResourceOrSidecar("TahoeTraffic.msstyles"))
        {
            log("Skipped Tahoe theme package: TahoeTraffic.theme/msstyles assets are not embedded or beside the EXE.");
            log(@"Put redistributable assets in .\Assets\ beside the EXE, or build a private package with embedded assets.");
            report.MissingRequirements.Add("Core theme assets missing: TahoeTraffic.theme and/or TahoeTraffic.msstyles.");
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
        report.ThemeInstalled = File.Exists(themePath);
        report.MsstylesInstalled = File.Exists(msstylesPath);

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

    private bool InstallStartAllBackProfile(InstallReport report)
    {
        var installed = IsStartAllBackInstalled();

        if (!installed)
        {
            log("StartAllBack not detected, skipped taskbar/start menu profile.");
            log("Install StartAllBack separately, then run this tool again to apply the Tahoe taskbar profile.");
            report.MissingRequirements.Add("StartAllBack is not installed, so the Tahoe taskbar/Start menu profile was skipped.");
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
        report.StartAllBackConfigured = true;
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

    private void InstallBrowsers(InstallReport report)
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
            report.BrowserTitlebarsConfigured |= InstallBrowser(browser);
        }

        if (!report.BrowserTitlebarsConfigured)
        {
            report.MissingRequirements.Add("No Brave, Chrome, or Edge profiles/shortcuts were found to configure.");
        }
    }

    private bool InstallBrowser(BrowserInfo browser)
    {
        var changed = false;
        log("Browser: " + browser.Name);
        var preferencePaths = GetBrowserPreferencePaths(browser.PreferencesPath);
        foreach (var preferencesPath in preferencePaths)
        {
            BackupFile(preferencesPath);
            var root = JsonNode.Parse(File.ReadAllText(preferencesPath))?.AsObject() ?? [];
            var browserNode = root["browser"] as JsonObject;
            if (browserNode == null)
            {
                browserNode = [];
                root["browser"] = browserNode;
            }
            browserNode["custom_chrome_frame"] = false;
            browserNode["chrome_custom_frame"] = false;
            File.WriteAllText(preferencesPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log("Updated preferences: " + preferencesPath);
            changed = true;
        }

        if (preferencePaths.Length == 0)
        {
            log("Preferences missing, skipped: " + browser.PreferencesPath);
        }

        if (!File.Exists(browser.ExePath))
        {
            log("Browser EXE missing, skipped shortcut/registry command updates: " + browser.ExePath);
            return changed;
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
            changed = true;
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
            changed = true;
        }

        return changed;
    }

    private void InstallTerminal(InstallReport report)
    {
        CloseProcesses(["WindowsTerminal"]);
        var settings = GetWindowsTerminalSettingsPath();
        if (!File.Exists(settings))
        {
            log("Windows Terminal settings missing, skipped: " + settings);
            report.MissingRequirements.Add("Windows Terminal settings file was not found.");
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
        report.WindowsTerminalConfigured = true;
    }

    private void InstallApplicationFramePatch(InstallReport report)
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ApplicationFrame.dll");
        if (!File.Exists(target))
        {
            log("ApplicationFrame.dll missing, skipped.");
            report.SettingsPatchSkippedReason = "ApplicationFrame.dll was not found.";
            return;
        }

        var currentHash = Sha256(target);
        var alreadyPatched = SupportedApplicationFramePatches.FirstOrDefault(p =>
            currentHash.Equals(p.PatchedSha256, StringComparison.OrdinalIgnoreCase));
        if (alreadyPatched != null)
        {
            log("ApplicationFrame.dll is already patched.");
            report.SettingsPatchApplied = true;
            return;
        }

        var supportedPatch = SupportedApplicationFramePatches.FirstOrDefault(p =>
            currentHash.Equals(p.OriginalSha256, StringComparison.OrdinalIgnoreCase));
        if (supportedPatch == null)
        {
            log("Skipped ApplicationFrame.dll: unsupported Windows build/hash.");
            log("Current SHA256: " + currentHash);
            log("This protects the laptop from a wrong system DLL. Browser/theme parts still apply.");
            report.SettingsPatchSkippedReason = "Unsupported ApplicationFrame.dll hash: " + currentHash;
            report.MissingRequirements.Add("Settings/UWP patch skipped because this Windows build/hash is not in the supported table.");
            return;
        }

        if (!HasResourceOrSidecar(supportedPatch.PatchedAssetName))
        {
            log("Skipped ApplicationFrame.dll: no patched DLL asset is embedded or beside the EXE.");
            log("For public builds, do not redistribute Microsoft system DLLs. Use a private local asset if you own the test machine.");
            report.SettingsPatchSkippedReason = "No verified patched ApplicationFrame.dll asset was found.";
            report.MissingRequirements.Add("ApplicationFrame.dll.patched is missing. Public builds intentionally do not redistribute Microsoft DLLs.");
            return;
        }

        BackupFile(target);
        var temp = Path.Combine(programDataRoot, supportedPatch.PatchedAssetName);
        WriteResourceToFile(supportedPatch.PatchedAssetName, temp);
        var patchedHash = Sha256(temp);
        if (!patchedHash.Equals(supportedPatch.PatchedSha256, StringComparison.OrdinalIgnoreCase))
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
        report.SettingsPatchApplied = true;
        report.RebootRequired = true;
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

    private static AssetStatus FindAsset(string shortName)
    {
        var resourceName = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + shortName, StringComparison.OrdinalIgnoreCase));
        if (resourceName != null)
        {
            return new AssetStatus(shortName, true, "embedded", resourceName);
        }

        var sidecar = FindSidecarAsset(shortName);
        return sidecar == null
            ? new AssetStatus(shortName, false, "missing", "")
            : new AssetStatus(shortName, true, "sidecar", sidecar);
    }

    private static string GetWindowsBuildInfo()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var productName = key?.GetValue("ProductName")?.ToString() ?? "Windows";
        var displayVersion = key?.GetValue("DisplayVersion")?.ToString() ?? key?.GetValue("ReleaseId")?.ToString() ?? "unknown";
        var build = key?.GetValue("CurrentBuildNumber")?.ToString() ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = key?.GetValue("UBR")?.ToString() ?? "0";
        return $"{productName} {displayVersion}, build {build}.{ubr}";
    }

    private static bool IsStartAllBackInstalled()
    {
        using var existingStartIsBack = Registry.CurrentUser.OpenSubKey(@"Software\StartIsBack");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return existingStartIsBack != null ||
            Directory.Exists(Path.Combine(programFiles, "StartAllBack")) ||
            File.Exists(Path.Combine(programFiles, "StartAllBack", "StartAllBackCfg.exe")) ||
            Directory.Exists(Path.Combine(programFilesX86, "StartAllBack")) ||
            File.Exists(Path.Combine(programFilesX86, "StartAllBack", "StartAllBackCfg.exe"));
    }

    private static string GetWindowsTerminalSettingsPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
    }

    private static BrowserDiagnostic[] GetBrowserDiagnostics()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var browserChecks = new[]
        {
            new BrowserDiagnosticInput(
                "Brave",
                Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Preferences"),
                GetShortcutPaths("Brave.lnk", includeTaskbar: true, includeUserDesktop: false)),
            new BrowserDiagnosticInput(
                "Google Chrome",
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Preferences"),
                GetShortcutPaths("Google Chrome.lnk", includeTaskbar: false, includeUserDesktop: false)),
            new BrowserDiagnosticInput(
                "Microsoft Edge",
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Preferences"),
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Microsoft Edge.lnk"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Microsoft Edge.lnk")
                ])
        };

        return browserChecks
            .Select(b => new BrowserDiagnostic(
                b.Name,
                File.Exists(b.ExePath),
                GetBrowserPreferencePaths(b.DefaultPreferencesPath).Length,
                b.ShortcutPaths.Count(File.Exists)))
            .ToArray();
    }

    private static string[] GetBrowserPreferencePaths(string defaultPreferencesPath)
    {
        var defaultProfileDir = Path.GetDirectoryName(defaultPreferencesPath);
        var userDataDir = defaultProfileDir == null ? null : Path.GetDirectoryName(defaultProfileDir);
        if (userDataDir == null || !Directory.Exists(userDataDir))
        {
            return File.Exists(defaultPreferencesPath) ? [defaultPreferencesPath] : [];
        }

        return Directory.GetDirectories(userDataDir)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase);
            })
            .Select(d => Path.Combine(d, "Preferences"))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string YesNo(bool value)
    {
        return value ? "yes" : "no";
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

    public sealed class DiagnosticsReport
    {
        public string WindowsVersion { get; init; } = "";
        public string AssetsDirectory { get; init; } = "";
        public AssetStatus TahoeThemeAsset { get; init; } = AssetStatus.Missing("TahoeTraffic.theme");
        public AssetStatus TahoeMsstylesAsset { get; init; } = AssetStatus.Missing("TahoeTraffic.msstyles");
        public AssetStatus ApplicationFramePatchedAsset { get; init; } = AssetStatus.Missing("ApplicationFrame.dll.patched");
        public string ApplicationFramePath { get; init; } = "";
        public string ApplicationFrameSha256 { get; init; } = "";
        public bool ApplicationFrameSupported { get; init; }
        public bool ApplicationFrameAlreadyPatched { get; init; }
        public string ApplicationFrameSupportNote { get; init; } = "";
        public bool StartAllBackInstalled { get; init; }
        public string WindowsTerminalSettingsPath { get; init; } = "";
        public bool WindowsTerminalSettingsExists { get; set; }
        public bool CanInstallTheme { get; set; }
        public bool CanPatchApplicationFrame { get; set; }
        public BrowserDiagnostic[] BrowserProfiles { get; init; } = [];
    }

    public sealed class InstallReport(DiagnosticsReport diagnostics)
    {
        public DiagnosticsReport Diagnostics { get; } = diagnostics;
        public string FinalStatus { get; set; } = "Failed";
        public bool ThemeInstalled { get; set; }
        public bool MsstylesInstalled { get; set; }
        public bool BrowserTitlebarsConfigured { get; set; }
        public bool WindowsTerminalConfigured { get; set; }
        public bool StartAllBackConfigured { get; set; }
        public bool SettingsPatchApplied { get; set; }
        public bool RegistryConfigured { get; set; }
        public string SettingsPatchSkippedReason { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public bool RestoreAvailable { get; set; }
        public bool RestartRequired { get; set; }
        public bool RebootRequired { get; set; }
        public List<string> MissingRequirements { get; } = [];
        public List<string> Errors { get; } = [];

        public bool AnySafeChangeApplied =>
            ThemeInstalled ||
            MsstylesInstalled ||
            BrowserTitlebarsConfigured ||
            WindowsTerminalConfigured ||
            StartAllBackConfigured ||
            SettingsPatchApplied ||
            RegistryConfigured;

        public string ToText()
        {
            var lines = new List<string>
            {
                "Tahoe Titlebar Final Report",
                "Status: " + FinalStatus,
                "Windows: " + Diagnostics.WindowsVersion,
                "ApplicationFrame.dll SHA256: " + Diagnostics.ApplicationFrameSha256,
                "Theme installed: " + TahoeInstaller.YesNo(ThemeInstalled),
                "msstyles installed: " + TahoeInstaller.YesNo(MsstylesInstalled),
                "Browser titlebars configured: " + TahoeInstaller.YesNo(BrowserTitlebarsConfigured),
                "Windows Terminal configured: " + TahoeInstaller.YesNo(WindowsTerminalConfigured),
                "StartAllBack configured: " + TahoeInstaller.YesNo(StartAllBackConfigured),
                "Settings/UWP patch applied: " + TahoeInstaller.YesNo(SettingsPatchApplied),
                "Backup path: " + BackupPath,
                "Restore available: " + TahoeInstaller.YesNo(RestoreAvailable),
                "Restart required: " + TahoeInstaller.YesNo(RestartRequired),
                "Reboot required: " + TahoeInstaller.YesNo(RebootRequired)
            };

            if (!string.IsNullOrWhiteSpace(SettingsPatchSkippedReason))
            {
                lines.Add("Settings/UWP patch skipped: " + SettingsPatchSkippedReason);
            }

            if (MissingRequirements.Count > 0)
            {
                lines.Add("");
                lines.Add("Missing requirements / skipped safe parts:");
                lines.AddRange(MissingRequirements.Select(item => "- " + item));
            }

            if (Errors.Count > 0)
            {
                lines.Add("");
                lines.Add("Errors:");
                lines.AddRange(Errors.Select(item => "- " + item));
            }

            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
    }

    public sealed record AssetStatus(string Name, bool Exists, string Source, string Path)
    {
        public static AssetStatus Missing(string name)
        {
            return new AssetStatus(name, false, "missing", "");
        }

        public string Describe()
        {
            return Exists ? $"{Source}: {Path}" : "missing";
        }
    }

    public sealed record BrowserDiagnostic(string Name, bool ExeExists, int ProfileCount, int ShortcutCount);

    private sealed record BrowserDiagnosticInput(string Name, string ExePath, string DefaultPreferencesPath, string[] ShortcutPaths);

    private sealed record SupportedApplicationFramePatch(
        string Id,
        string WindowsBuild,
        string OriginalSha256,
        string PatchedSha256,
        string PatchedAssetName);

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
