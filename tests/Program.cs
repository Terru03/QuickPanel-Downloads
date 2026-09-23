using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using QuickPanel.Models;
using QuickPanel.Services;
using QuickPanel.Updater.Core;

if (args.Length == 4 &&
    args[0].Equals("--integration-start-worker", StringComparison.OrdinalIgnoreCase))
{
    PortableUpdateRequest integrationRequest = PortableUpdateWorker.ReadRequest(args[2]) with
    {
        ProcessId = Environment.ProcessId
    };
    PortableUpdateWorker.WriteRequest(args[2], integrationRequest);
    int integrationWorkerProcessId = PortableUpdateInstaller.StartWorker(args[1], args[2], args[3]);
    Console.WriteLine(integrationWorkerProcessId.ToString(CultureInfo.InvariantCulture));
    return;
}

if (args.Length == 2 &&
    args[0].Equals("--portable-update-worker", StringComparison.OrdinalIgnoreCase) &&
    Environment.GetEnvironmentVariable("QUICKPANEL_TEST_HANDOFF_WORKER") == "1")
{
    PortableUpdateRequest handoffRequest = PortableUpdateWorker.ReadRequest(args[1]);
    PortableUpdateDiagnostics.Create(args[1], handoffRequest).SignalHandoff();
    Thread.Sleep(750);
    return;
}
if (args.Length == 2 &&
    args[0].Equals("--portable-update-worker", StringComparison.OrdinalIgnoreCase) &&
    Environment.GetEnvironmentVariable("QUICKPANEL_TEST_APPLY_WORKER") == "1")
{
    Environment.ExitCode = PortableUpdateWorker.ApplyRequestFile(
        args[1],
        Array.Empty<string>(),
        Process.Start);
    return;
}

static void Need(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static AiTab CustomTab(
    string name,
    string url,
    string? icon = null,
    string? id = null,
    bool pinned = false,
    string? browserProfileId = null,
    string? browserProfileLabel = null)
{
    return new AiTab
    {
        Id = id,
        Name = name,
        Url = url,
        Icon = icon,
        IsPinned = pinned,
        BrowserProfileId = browserProfileId,
        BrowserProfileLabel = browserProfileLabel,
        IsCustom = true
    };
}

static string TempSettingsPath()
{
    return Path.Combine(Path.GetTempPath(), "QuickPanel.Tests", Guid.NewGuid().ToString("N"), "settings.json");
}

static string TempDirectory()
{
    string path = Path.Combine(Path.GetTempPath(), "QuickPanel.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void WriteSyntheticProfile(string root, bool meaningfulSettings, bool browserState, string marker = "profile")
{
    Directory.CreateDirectory(root);
    File.WriteAllText(
        Path.Combine(root, "settings.json"),
        meaningfulSettings
            ? $$"""
              {
                "CustomTabs": [
                  {
                    "Id": "custom:{{marker}}",
                    "Name": "Synthetic",
                    "Url": "https://example.invalid/{{marker}}",
                    "IsPinned": true,
                    "IsCustom": true
                  }
                ],
                "TabOrder": [ "custom:{{marker}}" ],
                "TabPinStates": { "custom:{{marker}}": true },
                "PanelWidth": 640.0,
                "PanelHeight": 720.0
              }
              """
            : "{}");

    if (!browserState)
    {
        return;
    }

    string browserRoot = Path.Combine(root, "WebView2", "EBWebView", "Default");
    string cookiesDirectory = Path.Combine(browserRoot, "Network");
    string localStorageDirectory = Path.Combine(browserRoot, "Local Storage", "leveldb");
    Directory.CreateDirectory(cookiesDirectory);
    Directory.CreateDirectory(localStorageDirectory);
    byte[] fakeCookieDatabase = new byte[32 * 1024];
    Encoding.UTF8.GetBytes($"fake-cookie-db-{marker}").CopyTo(fakeCookieDatabase, 0);
    File.WriteAllBytes(Path.Combine(cookiesDirectory, "Cookies"), fakeCookieDatabase);
    File.WriteAllBytes(Path.Combine(localStorageDirectory, "000003.log"), Encoding.UTF8.GetBytes($"fake-storage-{marker}"));
}

static void WriteSyntheticFreshBrowserSkeleton(string root)
{
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
    string browserRoot = Path.Combine(root, "WebView2", "EBWebView", "Default");
    string cookiesDirectory = Path.Combine(browserRoot, "Network");
    string localStorageDirectory = Path.Combine(browserRoot, "Local Storage", "leveldb");
    Directory.CreateDirectory(cookiesDirectory);
    Directory.CreateDirectory(localStorageDirectory);
    File.WriteAllBytes(Path.Combine(cookiesDirectory, "Cookies"), new byte[4096]);
    File.WriteAllBytes(Path.Combine(localStorageDirectory, "CURRENT"), [1, 2, 3]);
}

static void WriteFreshDefaultSettings(string root)
{
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "settings.json"), """
        {
          "CustomTabs": [],
          "ClosedTabs": [],
          "LastSelectedTabId": null,
          "TabOrder": [],
          "TabZoomFactors": {},
          "TabPinStates": {},
          "StartWithWindows": false,
          "AlwaysOnTop": false,
          "PanelWidth": 500.0,
          "PanelHeight": 650.0,
          "LaunchCodexAutomatically": false,
          "UpdateManifestUrl": null,
          "CheckForUpdatesOnStartup": true,
          "CodexReset": null,
          "TripMode": null
        }
        """);
}

static void WriteApplicationManifest(string root, params string[] ownedEntries)
{
    Directory.CreateDirectory(root);
    string[] entries = ownedEntries
        .Append(ReleasePayloadPolicy.ApplicationManifestFileName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    File.WriteAllText(
        Path.Combine(root, ReleasePayloadPolicy.ApplicationManifestFileName),
        JsonSerializer.Serialize(new { schemaVersion = 1, entries }, new JsonSerializerOptions { WriteIndented = true }));
}

static string HashDirectory(string root)
{
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                 .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
    {
        string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
        hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
        hash.AppendData([0]);
        hash.AppendData(File.ReadAllBytes(file));
        hash.AppendData([0]);
    }

    return Convert.ToHexString(hash.GetHashAndReset());
}

static string FileSha256(string path)
{
    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return Convert.ToHexString(SHA256.HashData(stream));
}

IReadOnlyList<ExternalAppDefinition> externalDefinitions = ExternalAppRegistry.Parse("""
[
  {
    "id": "app:test",
    "displayName": "Test App",
    "launchKind": "StartApp",
    "startAppNameMatch": "Test App",
    "processName": "TestApp",
    "alternativeProcessNames": [ "TestAppHelper" ],
    "windowTitleRegex": "Test App",
    "launchTimeoutMs": 12000,
    "launchAutomatically": true,
    "preferredDockingBehavior": "Reparent"
  }
]
""");
Need(externalDefinitions.Count == 1 && externalDefinitions[0].Id == "app:test",
    "External application definition did not parse.");
Need(externalDefinitions[0].AlternativeProcessNames.SequenceEqual(["TestAppHelper"]),
    "Alternative process names did not parse.");
IReadOnlyList<ExternalAppDefinition> builtInExternalApps = new ExternalAppRegistry().GetAll();
Need(builtInExternalApps.Any(definition => definition.Id == "native:codex") &&
    builtInExternalApps.Any(definition => definition.Id == "native:whatsapp"),
    "Built-in external application registry did not expose Codex and WhatsApp.");
Need(builtInExternalApps.Where(definition => definition.Id is "native:codex" or "native:whatsapp")
        .All(definition => definition.Icon?.Equals("auto", StringComparison.OrdinalIgnoreCase) == true),
    "Built-in desktop apps should resolve their installed Windows icons instead of letter fallbacks.");
Need(Path.GetFileName(StartupService.StartupShortcutPath) == "Quick Panel.lnk",
    "User-facing startup shortcut did not use the Quick Panel name.");

string registryTestDirectory = Path.Combine(Path.GetTempPath(), "QuickPanel.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(registryTestDirectory);
string registryBuiltInsPath = Path.Combine(registryTestDirectory, "built-in.json");
string registryUserPath = Path.Combine(registryTestDirectory, "user.json");
File.WriteAllText(registryBuiltInsPath, """[{ "id": "app:one", "displayName": "Built in", "launchTimeoutMs": 5000 }]""");
File.WriteAllText(registryUserPath, """[{ "id": "app:one", "displayName": "User override", "launchTimeoutMs": 5000 }]""");
var mergedRegistry = new ExternalAppRegistry(registryBuiltInsPath, registryUserPath);
Need(mergedRegistry.GetAll().Count == 1 && mergedRegistry.Find("APP:ONE")?.DisplayName == "User override",
    "User external application definition did not override built-in definition by stable ID.");

string externalSettingsPath = TempSettingsPath();
Directory.CreateDirectory(Path.GetDirectoryName(externalSettingsPath)!);
File.WriteAllText(externalSettingsPath, """
{
  "LastSelectedTabId": "native:whatsapp",
  "TabOrder": [ "native:codex", "native:whatsapp" ],
  "TabPinStates": { "native:whatsapp": false }
}
""");
var externalSettings = new SettingsService(externalSettingsPath);
Need(externalSettings.LoadLastSelectedTabId() == "native:whatsapp" &&
    externalSettings.LoadTabOrder().Contains("native:whatsapp") &&
    !externalSettings.LoadTabPinned("native:whatsapp", defaultValue: true),
    "Data-driven external application IDs did not survive settings normalization.");

var matchingCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)123,
    ProcessId = 42,
    ProcessName = "TestAppHelper",
    Title = "Test App - Home",
    ClassName = "Chrome_WidgetWin_1",
    IsVisible = true,
    Width = 1200,
    Height = 800,
    HasRenderedSurface = true
};
ExternalAppWindowMatch matchingResult = ExternalAppWindowMatcher.Evaluate(externalDefinitions[0], matchingCandidate, ownProcessId: 7);
Need(matchingResult.IsValid && matchingResult.HasIdentityMatch && matchingResult.Score >= externalDefinitions[0].MinimumCandidateScore,
    "External application candidate scoring rejected matching helper process window.");

var weakBrowserCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)125,
    ProcessId = 43,
    ProcessName = "chrome",
    Title = "Test App - Google Chrome",
    ClassName = "Chrome_WidgetWin_1",
    IsVisible = true,
    IsForeground = true,
    Width = 1200,
    Height = 800,
    HasRenderedSurface = true
};
ExternalAppWindowMatch weakBrowserResult = ExternalAppWindowMatcher.Evaluate(externalDefinitions[0], weakBrowserCandidate, ownProcessId: 7);
Need(!weakBrowserResult.IsValid && weakBrowserResult.RejectionReason?.Contains("strong", StringComparison.Ordinal) == true,
    "Title and generic window class selected a browser without process or package identity.");

var explorerPickerCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)0xABC,
    ProcessId = 321,
    ProcessName = "explorer",
    ProcessPath = "C:\\Windows\\explorer.exe",
    Title = "Downloads",
    ClassName = "CabinetWClass",
    IsVisible = true,
    Width = 1100,
    Height = 760,
    HasRenderedSurface = true
};
WindowDockAssessment explorerAssessment = ExternalAppWindowPickerPolicy.Evaluate(explorerPickerCandidate, ownProcessId: 7);
Need(explorerAssessment.IsDockable && explorerAssessment.Level == WindowDockCompatibilityLevel.Ready,
    "Visual window picker rejected a normal File Explorer window.");
Need(explorerAssessment.Label == "Shell view" &&
    ShellExplorerLocationService.IsFileExplorerWindow(explorerPickerCandidate),
    "File Explorer was not routed to the safe in-panel Shell fallback.");
ExternalAppDefinition explorerDefinition = ExternalAppDefinitionFactory.CreateForWindow(explorerPickerCandidate);
Need(explorerDefinition.ContentTopOffsetAt96Dpi == 40,
    "Direct-host compatibility metadata should retain the Explorer top-row crop if explicitly inspected.");

var nativeNoRendererCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)0x105,
    ProcessId = 8,
    ProcessName = "Notepad",
    ProcessPath = "C:\\Windows\\System32\\notepad.exe",
    Title = "Notes - Notepad",
    ClassName = "Notepad",
    IsVisible = true,
    Width = 1000,
    Height = 700,
    HasRenderedSurface = false
};
Need(ExternalAppWindowPickerPolicy.Evaluate(nativeNoRendererCandidate, ownProcessId: 7).Level == WindowDockCompatibilityLevel.Ready,
    "Normal native windows should not be marked risky only because they have no Chrome renderer child.");
Need(ExternalAppDefinitionFactory.CreateForWindow(nativeNoRendererCandidate).ContentTopOffsetAt96Dpi == 32,
	"Notepad should retain its client-drawn title-strip crop metadata.");
var modernNotepadCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)0x106,
    ProcessId = 9,
    ProcessName = "Notepad",
    ProcessPath = "C:\\Program Files\\WindowsApps\\Microsoft.WindowsNotepad\\Notepad.exe",
    Title = "Untitled - Notepad",
    ClassName = "Notepad",
    AppUserModelId = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
    PackageFullName = "Microsoft.WindowsNotepad_11.2606.15.0_x64__8wekyb3d8bbwe",
    IsVisible = true,
    Width = 1000,
    Height = 700
};
Need(ExternalAppDefinitionFactory.CreateForWindow(modernNotepadCandidate).PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay,
    "Packaged modern Notepad must use the compatibility overlay because repeated child reparenting replaces its HWND.");
Need(ExternalAppDefinitionFactory.CreateForInstalledApp(
        new ExternalAppInstalledApp(
            "Notepad",
            "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            "Microsoft.WindowsNotepad_8wekyb3d8bbwe"))
    .PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay,
    "Installed packaged Notepad must use the same compatibility overlay as a picked Notepad window.");

var settingsPickerCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)0xABD,
    ProcessId = 322,
    ProcessName = "SystemSettings",
    Title = "Settings",
    ClassName = "ApplicationFrameWindow",
    AppUserModelId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
    PackageFullName = "windows.immersivecontrolpanel_10.0.26100.1_neutral_neutral_cw5n1h2txyewy",
    IsVisible = true,
    Width = 1000,
    Height = 720,
    HasRenderedSurface = true
};
WindowDockAssessment settingsAssessment = ExternalAppWindowPickerPolicy.Evaluate(settingsPickerCandidate, ownProcessId: 7);
Need(settingsAssessment.IsDockable && settingsAssessment.Level == WindowDockCompatibilityLevel.Try &&
	settingsAssessment.Reason.Contains("render blank", StringComparison.OrdinalIgnoreCase),
	"Packaged ApplicationFrameWindow surfaces should remain available with a compositor warning.");

var hardwarePickerCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)0xABE,
    ProcessId = 323,
    ProcessName = "parsecd",
    ProcessPath = "C:\\Program Files\\Parsec\\parsecd.exe",
    Title = "Parsec",
    ClassName = "Qt5152QWindowIcon",
    IsVisible = true,
    Width = 1280,
    Height = 720,
    HasRenderedSurface = false
};
WindowDockAssessment hardwareAssessment = ExternalAppWindowPickerPolicy.Evaluate(hardwarePickerCandidate, ownProcessId: 7);
Need(hardwareAssessment.IsDockable && hardwareAssessment.Level == WindowDockCompatibilityLevel.Ready,
	"Normal hardware-surface app should remain ready for true child hosting.");

var utilityPickerCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)0xABF,
    ProcessId = 324,
    ProcessName = "UtilityApp",
    Title = "Utility workspace",
    ClassName = "UtilityWindow",
    IsVisible = true,
    IsOwned = true,
    IsToolWindow = true,
    Width = 640,
    Height = 480,
    HasRenderedSurface = true
};
WindowDockAssessment utilityAssessment = ExternalAppWindowPickerPolicy.Evaluate(utilityPickerCandidate, ownProcessId: 7);
Need(utilityAssessment.IsDockable && utilityAssessment.Level == WindowDockCompatibilityLevel.Try,
    "Useful titled utility window was hidden instead of exposed as an opt-in Try candidate.");

ExternalAppDefinition pickedDefinition = ExternalAppDefinitionFactory.CreateForWindow(settingsPickerCandidate);
Need(pickedDefinition.Id.StartsWith("window:322:", StringComparison.Ordinal) &&
    pickedDefinition.DisplayName == "Settings" &&
    pickedDefinition.Icon == "auto" &&
    pickedDefinition.ProcessName == "SystemSettings" &&
    pickedDefinition.AppUserModelId == settingsPickerCandidate.AppUserModelId &&
    pickedDefinition.PackageIdentity == "windows.immersivecontrolpanel" &&
    !pickedDefinition.LaunchAutomatically &&
    !pickedDefinition.TerminateOnPanelClose,
    "Picked window did not become a safe session external-app definition.");
Need(pickedDefinition.PreferredDockingBehavior == ExternalAppDockingBehavior.Reparent,
	"Picked windows should use real in-panel child-window hosting.");
Need(pickedDefinition.ContentTopOffsetAt96Dpi == 32,
    "Known client-drawn title bars should start with a small content crop.");
Need(ExternalAppDefinitionFactory.TryReadWindowId(pickedDefinition.Id, out uint pickedProcessId, out nint pickedHandle) &&
    pickedProcessId == settingsPickerCandidate.ProcessId &&
    pickedHandle == settingsPickerCandidate.Handle,
    "Picked-window ID did not preserve its exact process and HWND identity.");
Need(!ExternalAppDefinitionFactory.TryReadWindowId("window:bad:not-a-handle", out _, out _),
    "Invalid picked-window ID was accepted.");
Need(TaskManagerProcessService.CalculateCpuPercent(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(4),
        processorCount: 2) == 25.0,
    "Task Manager CPU sampling did not normalize processor time across cores.");
Need(TaskManagerProcessService.CalculateCpuPercent(
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(4),
        processorCount: 2) == 0.0,
    "Task Manager CPU sampling accepted a stale or reused PID baseline.");
DateTime originalProcessStart = new(2026, 8, 24, 20, 0, 0, DateTimeKind.Utc);
var originalProcessSnapshot = new TaskManagerProcessSnapshot(
    4242,
    "chrome",
    originalProcessStart,
    12.0,
    256 * 1024 * 1024,
    "Running",
    "Original tab",
    true);
var reusedPidSnapshot = originalProcessSnapshot with
{
    StartTimeUtc = originalProcessStart.AddSeconds(15),
    WindowTitle = "Different tab"
};
Need(TaskManagerProcessListPolicy.GetMergeAction(originalProcessSnapshot, originalProcessSnapshot) == TaskManagerProcessMergeAction.Update,
    "The same process instance should update its existing Task Manager row.");
Need(TaskManagerProcessListPolicy.GetMergeAction(originalProcessSnapshot, reusedPidSnapshot) == TaskManagerProcessMergeAction.Replace,
    "A reused PID with the same executable name must replace the stale Task Manager row.");
Need(!TaskManagerProcessService.MatchesConfirmedInstance(originalProcessSnapshot, "chrome", reusedPidSnapshot.StartTimeUtc!.Value) &&
    TaskManagerProcessService.MatchesConfirmedInstance(originalProcessSnapshot, "chrome", originalProcessStart),
    "End task must verify process creation time as well as PID and executable name.");
TaskManagerResponsiveLayout compactTaskManagerLayout = TaskManagerPresentationPolicy.ForWidth(475);
Need(!compactTaskManagerLayout.ShowProcessId &&
    !compactTaskManagerLayout.ShowStatus &&
    !compactTaskManagerLayout.ShowWindowTitle &&
    compactTaskManagerLayout.SummaryColumns == 2,
    "Compact Task Manager layout should preserve only essential process columns and two summary columns.");
TaskManagerResponsiveLayout mediumTaskManagerLayout = TaskManagerPresentationPolicy.ForWidth(650);
Need(mediumTaskManagerLayout.ShowProcessId &&
    !mediumTaskManagerLayout.ShowStatus &&
    !mediumTaskManagerLayout.ShowWindowTitle &&
    mediumTaskManagerLayout.SummaryColumns == 2,
    "Medium Task Manager layout should reveal PID without compressing secondary text.");
TaskManagerResponsiveLayout wideTaskManagerLayout = TaskManagerPresentationPolicy.ForWidth(820);
Need(wideTaskManagerLayout.ShowProcessId &&
    wideTaskManagerLayout.ShowStatus &&
    wideTaskManagerLayout.ShowWindowTitle &&
    wideTaskManagerLayout.SummaryColumns == 3,
    "Wide Task Manager layout should reveal all process details and three summary columns.");
Need(TaskManagerPresentationPolicy.FormatTemperatureSummary([double.NaN, -2.0, 0.0]) == "Unavailable",
    "Invalid temperature readings should remain explicitly unavailable.");
Need(TaskManagerPresentationPolicy.FormatTemperatureSummary([54.4, 71.2]).Contains("71", StringComparison.Ordinal),
    "Temperature summary did not report the hottest valid sensor.");
Need(TaskManagerPresentationPolicy.FormatFanSummary([0.0, double.PositiveInfinity]) == "Unavailable",
    "Invalid fan readings should remain explicitly unavailable.");
string fanSummary = TaskManagerPresentationPolicy.FormatFanSummary([840.0, 1260.0]);
Need(fanSummary.Contains("2", StringComparison.Ordinal) && fanSummary.Contains(1260.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal),
    "Fan summary did not report sensor count and maximum RPM.");
Need(PanelResizePolicy.ApplyDelta(500.0, 50.0, 420.0) == 509.0,
    "Panel resize grip should turn a large hand movement into a fine size adjustment.");
Need(PanelResizePolicy.ApplyDelta(421.0, -50.0, 420.0) == 420.0,
    "Panel resize grip should clamp fine adjustments at the minimum size.");
var telemetryHistory = new TelemetryHistorySeries(maxSamples: 3, ceiling: 100.0);
telemetryHistory.Add(25.0);
telemetryHistory.Add(double.NaN);
telemetryHistory.Add(50.0);
telemetryHistory.Add(200.0);
telemetryHistory.Add(75.0);
Need(telemetryHistory.NormalizedValues.SequenceEqual([0.5, 1.0, 0.75]),
    "Telemetry history should ignore invalid samples, clamp values, normalize them, and retain only its bounded history.");
string[] nativePanelIds =
[
    "native:codex-usage",
    "native:windows-helper",
    "native:trip-planner",
    "native:task-manager"
];
string[] nativePanelGlyphs = nativePanelIds.Select(NativePanelIconCatalog.GetGlyph).ToArray();
Need(nativePanelGlyphs.Distinct(StringComparer.Ordinal).Count() == nativePanelGlyphs.Length &&
    nativePanelGlyphs.All(glyph => glyph.Length > 0 && !glyph.All(char.IsLetterOrDigit)),
    "Every built-in panel should use its own real Fluent icon instead of a letter badge.");
var windowsTaskManagerCandidate = new ExternalAppWindowCandidate
{
    Handle = (nint)0xAC3,
    ProcessId = 500,
    ProcessName = "Taskmgr",
    Title = "Task Manager",
    ClassName = "TaskManagerWindow",
    IsVisible = true,
    Width = 1000,
    Height = 720
};
WindowDockAssessment windowsTaskManagerAssessment = ExternalAppWindowPickerPolicy.Evaluate(windowsTaskManagerCandidate, ownProcessId: 7);
Need(windowsTaskManagerAssessment.IsDockable && windowsTaskManagerAssessment.Level == WindowDockCompatibilityLevel.Try &&
    windowsTaskManagerAssessment.Reason.Contains("built-in Task Manager", StringComparison.OrdinalIgnoreCase),
    "Elevated-prone Windows Task Manager should recommend the safe built-in panel.");
ExternalAppDefinition installedDefinition = ExternalAppDefinitionFactory.CreateForInstalledApp(
    new ExternalAppInstalledApp("Task Manager", "Microsoft.AutoGenerated.TaskManager", ""));
Need(installedDefinition.DisplayName == "Task Manager" &&
    installedDefinition.AppUserModelId == "Microsoft.AutoGenerated.TaskManager" &&
    installedDefinition.StartAppNameMatch == "Task Manager" &&
    installedDefinition.LaunchKind == ExternalAppLaunchKind.StartApp &&
    !installedDefinition.LaunchAutomatically &&
    installedDefinition.PreferredDockingBehavior == ExternalAppDockingBehavior.Reparent,
    "Installed app did not become a safe launch-and-pick definition.");
ExternalAppDefinition utilityDefinition = ExternalAppDefinitionFactory.CreateForWindow(utilityPickerCandidate);
Need(utilityDefinition.AllowOwnedWindows && utilityDefinition.AllowToolWindows,
    "Picked utility window definition did not retain the explicit compatibility opt-ins.");

foreach (ExternalAppWindowCandidate blockedCandidate in new[]
{
    new ExternalAppWindowCandidate { Handle = (nint)0xAC0, ProcessId = 7, ProcessName = "AIQuickPanel", Title = "Quick Panel", ClassName = "HwndWrapper", IsVisible = true, Width = 500, Height = 650 },
    new ExternalAppWindowCandidate { Handle = (nint)0xAC1, ProcessId = 325, ProcessName = "explorer", Title = "Taskbar", ClassName = "Shell_TrayWnd", IsVisible = true, Width = 1920, Height = 48 },
    new ExternalAppWindowCandidate { Handle = (nint)0xAC2, ProcessId = 326, ProcessName = "SecHealthUI", Title = "Windows Security", ClassName = "ApplicationFrameWindow", IsVisible = true, Width = 1000, Height = 720 }
})
{
    Need(!ExternalAppWindowPickerPolicy.Evaluate(blockedCandidate, ownProcessId: 7).IsDockable,
        $"Unsafe or meaningless picker candidate was accepted: {blockedCandidate.ProcessName}/{blockedCandidate.ClassName}.");
}

ExternalAppWindowCandidate untitledCandidate = new() { Handle = (nint)0xAC3, ProcessId = 327, ProcessName = "UntitledApp", Title = "", ClassName = "AppWindow", IsVisible = true, Width = 800, Height = 600 };
ExternalAppWindowCandidate tinyCandidate = new() { Handle = (nint)0xAC4, ProcessId = 328, ProcessName = "TinyApp", Title = "Tiny", ClassName = "AppWindow", IsVisible = true, Width = 79, Height = 59 };
Need(ExternalAppWindowPickerPolicy.Evaluate(untitledCandidate, ownProcessId: 7).Level == WindowDockCompatibilityLevel.Try,
    "Untitled app windows should remain available behind a Try label.");
Need(ExternalAppWindowPickerPolicy.Evaluate(tinyCandidate, ownProcessId: 7).Level == WindowDockCompatibilityLevel.Try,
    "Small app windows should remain available behind a Try label.");
ExternalAppDefinition tinyDefinition = ExternalAppDefinitionFactory.CreateForWindow(tinyCandidate);
Need(tinyDefinition.MinimumWindowWidth == 79 && tinyDefinition.MinimumWindowHeight == 59,
    "Explicitly picked small windows should keep dockable minimum bounds.");

var flexibleDefinition = new ExternalAppDefinition
{
    Id = "app:flexible",
    DisplayName = "Flexible App",
    ProcessName = "FlexibleApp",
    AlternativeWindowClassNames = ["WinUIDesktopWin32WindowClass"],
    MinimumWindowWidth = 120,
    MinimumWindowHeight = 80,
    AllowToolWindows = true,
    MinimumCandidateScore = 50
};
ExternalAppWindowMatch flexibleResult = ExternalAppWindowMatcher.Evaluate(
    flexibleDefinition,
    new ExternalAppWindowCandidate
    {
        Handle = (nint)126,
        ProcessId = 44,
        ProcessName = "FlexibleApp",
        ClassName = "WinUIDesktopWin32WindowClass",
        IsVisible = true,
        IsToolWindow = true,
        Width = 180,
        Height = 100
    },
    ownProcessId: 7);
Need(flexibleResult.IsValid && flexibleResult.ScoreReasons.Any(reason => reason.StartsWith("alternative-window-class", StringComparison.Ordinal)),
    "Opt-in compact tool window with an alternative class was not accepted.");

var safeDefaultsDefinition = new ExternalAppDefinition
{
    Id = "app:safe-defaults",
    DisplayName = "Safe Defaults",
    ProcessName = "SafeDefaults",
    MinimumCandidateScore = 40
};
ExternalAppWindowMatch defaultToolResult = ExternalAppWindowMatcher.Evaluate(
    safeDefaultsDefinition,
    new ExternalAppWindowCandidate
    {
        Handle = (nint)128,
        ProcessId = 46,
        ProcessName = "SafeDefaults",
        IsVisible = true,
        IsToolWindow = true,
        Width = 800,
        Height = 600
    },
    ownProcessId: 7);
Need(!defaultToolResult.IsValid && defaultToolResult.RejectionReason?.Contains("tool window", StringComparison.Ordinal) == true,
    "Default matching accepted a tool-window popup without explicit opt-in.");

ExternalAppWindowMatch defaultTinyResult = ExternalAppWindowMatcher.Evaluate(
    safeDefaultsDefinition,
    new ExternalAppWindowCandidate
    {
        Handle = (nint)129,
        ProcessId = 47,
        ProcessName = "SafeDefaults",
        IsVisible = true,
        Width = 239,
        Height = 600
    },
    ownProcessId: 7);
Need(!defaultTinyResult.IsValid && defaultTinyResult.RejectionReason?.Contains("too small", StringComparison.Ordinal) == true,
    "Default matching accepted a candidate below its minimum window size.");

var alternateIdentityDefinition = new ExternalAppDefinition
{
    Id = "app:alternate-identity",
    DisplayName = "Alternate Identity",
    ProcessName = "PrimaryProcess",
    AlternativeAppUserModelIds = ["Vendor.Alternate_123!App"],
    AlternativePackageIdentities = ["Vendor.Alternate_123"],
    MinimumCandidateScore = 50
};
ExternalAppWindowMatch alternateIdentityResult = ExternalAppWindowMatcher.Evaluate(
    alternateIdentityDefinition,
    new ExternalAppWindowCandidate
    {
        Handle = (nint)127,
        ProcessId = 45,
        ProcessName = "PackageHost",
        AppUserModelId = "Vendor.Alternate_123!App",
        PackageFullName = "Vendor.Alternate_123_2.2.0_x64__publisher",
        IsVisible = true,
        Width = 900,
        Height = 700
    },
    ownProcessId: 7);
Need(alternateIdentityResult.IsValid &&
    alternateIdentityResult.ScoreReasons.Any(reason => reason.StartsWith("alternative-aumid", StringComparison.Ordinal)) &&
    alternateIdentityResult.ScoreReasons.Any(reason => reason.StartsWith("alternative-package", StringComparison.Ordinal)),
    "Alternative packaged-app identities were not accepted.");

bool invalidMinimumSizeRejected = false;
try
{
    ExternalAppRegistry.Parse("""[{ "id": "bad-size", "displayName": "Bad Size", "minimumWindowWidth": 0 }]""");
}
catch (InvalidDataException)
{
    invalidMinimumSizeRejected = true;
}
Need(invalidMinimumSizeRejected, "Invalid external application minimum window size was accepted.");

var iconDefinition = new ExternalAppDefinition
{
    Id = "app:icon",
    DisplayName = "Icon App",
    ExecutablePath = "%WINDIR%\\System32\\notepad.exe",
    AppUserModelId = "Example.IconApp_123!App",
    AlternativeAppUserModelIds = ["Example.IconApp.Beta_456!App"],
    ShellLaunchTarget = "%ProgramData%\\Microsoft\\Windows\\Start Menu\\Programs\\Icon App.lnk"
};
IReadOnlyList<string> iconTargets = ExternalAppIconCandidateResolver.BuildShellTargets(
    iconDefinition,
    "C:\\Apps\\IconApp.exe",
    "Resolved.IconApp_456!App");
Need(iconTargets[0] == "C:\\Apps\\IconApp.exe" &&
    iconTargets.Contains(Environment.ExpandEnvironmentVariables(iconDefinition.ExecutablePath)) &&
    iconTargets.Contains("shell:AppsFolder\\Resolved.IconApp_456!App") &&
    iconTargets.Contains("shell:AppsFolder\\Example.IconApp_123!App") &&
    iconTargets.Contains("shell:AppsFolder\\Example.IconApp.Beta_456!App") &&
    iconTargets.Contains(Environment.ExpandEnvironmentVariables(iconDefinition.ShellLaunchTarget)),
    "External app icon candidates did not retain running, executable, packaged, and shell identities.");

Need(ExternalAppStartTargetMatcher.HasStableIdentity(
        alternateIdentityDefinition,
        "Package Host",
        "Vendor.Alternate_123!App",
        "Vendor.Alternate_123"),
    "Alternative packaged-app identity was not accepted during Start-app discovery.");
Need(ExternalAppStartTargetMatcher.Score(
        alternateIdentityDefinition,
        "Package Host",
        "Vendor.Alternate_123!App",
        "Vendor.Alternate_123") >= 150,
    "Alternative packaged-app identity did not receive a stable Start-app score.");

Need(ExternalAppStartTargetMatcher.HasStableIdentity(
        builtInExternalApps.Single(definition => definition.Id == "native:codex"),
        "ChatGPT",
        "OpenAI.Codex_2p2nqsd0c76g0!App",
        "OpenAI.Codex_2p2nqsd0c76g0"),
    "Stable Codex AUMID token was not accepted as a Start-app identity.");
Need(!ExternalAppStartTargetMatcher.HasStableIdentity(
        builtInExternalApps.Single(definition => definition.Id == "native:codex"),
        "Codex Helper",
        "Example.CodexHelper_123!App",
        "Example.CodexHelper_123"),
    "Fuzzy lookalike Start-app identity was accepted.");

ExternalAppWindowMatch ownWindowResult = ExternalAppWindowMatcher.Evaluate(
    externalDefinitions[0],
    new ExternalAppWindowCandidate
    {
        Handle = (nint)124,
        ProcessId = 7,
        ProcessName = "TestApp",
        Title = "Test App",
        IsVisible = true,
        Width = 1200,
        Height = 800
    },
    ownProcessId: 7);
Need(!ownWindowResult.IsValid && ownWindowResult.RejectionReason?.Contains("Quick Panel", StringComparison.Ordinal) == true,
    "Quick Panel window was not rejected as external application candidate.");

bool invalidRegexRejected = false;
try
{
    ExternalAppRegistry.Parse("""[{ "id": "bad", "displayName": "Bad", "windowTitleRegex": "[" }]""");
}
catch (InvalidDataException)
{
    invalidRegexRejected = true;
}
Need(invalidRegexRejected, "Invalid external application regex was accepted.");
Need(ExternalAppLifecycle.CanTransition(ExternalAppDockState.Idle, ExternalAppDockState.Launching),
    "Lifecycle rejected initial launch.");
Need(ExternalAppLifecycle.CanTransition(ExternalAppDockState.Launching, ExternalAppDockState.Searching) &&
    ExternalAppLifecycle.CanTransition(ExternalAppDockState.Searching, ExternalAppDockState.Connected),
    "Lifecycle rejected launch/search/connect path.");
Need(ExternalAppLifecycle.CanTransition(ExternalAppDockState.Connected, ExternalAppDockState.ApplicationClosed) &&
    ExternalAppLifecycle.CanTransition(ExternalAppDockState.ApplicationClosed, ExternalAppDockState.Searching),
    "Lifecycle rejected close/retry path.");
Need(!ExternalAppLifecycle.CanTransition(ExternalAppDockState.ShuttingDown, ExternalAppDockState.Connected),
    "Lifecycle allowed reconnect after shutdown.");

var gate = new RendererReadinessGate(10);
for (var sample = 1; sample < 10; sample++)
{
    Need(!gate.Observe((nint)1, (nint)10, 1200, 800, isResponsive: true), "Gate opened too soon.");
}
Need(gate.Observe((nint)1, (nint)10, 1200, 800, isResponsive: true), "Gate did not open after stable samples.");

gate = new RendererReadinessGate(3);
Need(!gate.Observe((nint)1, (nint)10, 1200, 800, isResponsive: true), "Gate opened on first sample.");
Need(!gate.Observe((nint)1, (nint)11, 1200, 800, isResponsive: true), "Renderer swap did not reset gate.");
Need(!gate.Observe((nint)1, (nint)11, 1200, 800, isResponsive: true), "Gate opened after only two stable samples.");
Need(gate.Observe((nint)1, (nint)11, 1200, 800, isResponsive: true), "Gate did not recover after renderer swap.");

gate = new RendererReadinessGate(2);
Need(!gate.Observe((nint)1, (nint)10, 1200, 800, isResponsive: true), "Gate opened on first size.");
Need(!gate.Observe((nint)1, (nint)10, 1199, 800, isResponsive: true), "Size change did not reset gate.");
Need(gate.Observe((nint)1, (nint)10, 1199, 800, isResponsive: true), "Gate did not open after size became stable.");
Need(!gate.Observe((nint)1, (nint)10, 1199, 800, isResponsive: false), "Hung window did not close gate.");

var reddit = CustomTab("Reddit", "https://www.reddit.com");
Need(TabIconResolver.ResolveBrandKey(reddit) == "reddit", "Reddit host did not resolve to bundled icon.");

var selectedIcon = CustomTab("News", "https://example.com", icon: "reddit");
Need(TabIconResolver.ResolveBrandKey(selectedIcon) == "reddit", "Explicit brand icon did not win.");
Need(TabIconResolver.NormalizeIconSlug("Git Hub!") == "github", "Brand slug normalization failed.");

string json = JsonSerializer.Serialize(selectedIcon);
AiTab? restored = JsonSerializer.Deserialize<AiTab>(json);
Need(restored?.Icon == "reddit", "Custom icon did not survive settings serialization.");

var profiledIdentity = CustomTab(
    "Reddit",
    "https://reddit.com/",
    id: "custom:reddit-profile-two",
    browserProfileId: "qp-0123456789abcdef",
    browserProfileLabel: "Profile 2");
string profiledJson = JsonSerializer.Serialize(profiledIdentity);
AiTab? restoredProfiledIdentity = JsonSerializer.Deserialize<AiTab>(profiledJson);
Need(restoredProfiledIdentity?.BrowserProfileId == "qp-0123456789abcdef" &&
    restoredProfiledIdentity.BrowserProfileLabel == "Profile 2",
    "Per-tab browser profile identity was not serialized.");
Need(BrowserProfilePolicy.NormalizeProfileId(" qp-valid_2 ") == "qp-valid_2" &&
    BrowserProfilePolicy.NormalizeProfileId("bad/profile") == null &&
    BrowserProfilePolicy.NormalizeProfileId("Default") == null &&
    BrowserProfilePolicy.NormalizeProfileId("other-profile") == null &&
    BrowserProfilePolicy.NormalizeProfileId("qp-") == null &&
    BrowserProfilePolicy.NormalizeProfileId(new string('a', 65)) == null &&
    BrowserProfilePolicy.NormalizeProfileId("trailing.") == null,
    "WebView2 profile-name constraints were not enforced.");
BrowserProfileIdentity nextProfile = BrowserProfilePolicy.CreateNew(
    new[]
    {
        CustomTab("Reddit 2", "https://reddit.com", browserProfileId: "qp-one", browserProfileLabel: "Profile 2"),
        CustomTab("Reddit 3", "https://reddit.com", browserProfileId: "qp-two", browserProfileLabel: "Profile 3")
    },
    () => "fixedprofileid");
Need(nextProfile.Id == "qp-fixedprofileid" && nextProfile.Label == "Profile 4",
    "New isolated browser profiles were not assigned a safe unique identity and readable label.");
IReadOnlyList<BrowserProfileIdentity> availableProfiles = BrowserProfilePolicy.GetAvailableProfiles(
    new[]
    {
        CustomTab("Reddit 2", "https://reddit.com", browserProfileId: "qp-account-two", browserProfileLabel: "Profile 2"),
        CustomTab("Reddit duplicate", "https://reddit.com", browserProfileId: "QP-ACCOUNT-TWO", browserProfileLabel: "Wrong duplicate label"),
        CustomTab("Reddit 3", "https://reddit.com", browserProfileId: "qp-account-three", browserProfileLabel: "Profile 3")
    });
Need(availableProfiles.Count == 3 &&
    availableProfiles[0].IsDefault &&
    availableProfiles[1].Label == "Profile 2" &&
    availableProfiles[2].Label == "Profile 3",
    "Available profile discovery did not preserve default, deduplicate IDs, and retain readable labels.");
string profiledSettingsPath = TempSettingsPath();
var profiledSettings = new SettingsService(profiledSettingsPath);
profiledSettings.SaveCustomTabs(new[] { profiledIdentity });
AiTab persistedProfiledTab = new SettingsService(profiledSettingsPath).LoadTabs()
    .Single(tab => tab.IsCustom && SettingsService.GetTabId(tab) == "custom:reddit-profile-two");
Need(persistedProfiledTab.BrowserProfileId == "qp-0123456789abcdef" &&
    persistedProfiledTab.BrowserProfileLabel == "Profile 2",
    "Settings normalization dropped a custom tab's isolated browser profile.");

var closedTabs = new ClosedTabHistory();
var builtIn = new AiTab
{
    Id = "builtin:chatgpt",
    Name = "ChatGPT",
    Url = "https://chatgpt.com",
    Icon = "openai",
    IsPinned = true
};
closedTabs.Push(builtIn, 0);
Need(closedTabs.Count == 0, "Built-in tabs must not enter the closed-tab stack.");

var customOne = CustomTab("Docs", "https://docs.example.com", icon: "globe", id: "custom:docs");
var customTwo = CustomTab(
    "News",
    "https://news.example.com",
    icon: "reddit",
    id: "custom:news",
    browserProfileId: "qp-news-account",
    browserProfileLabel: "Profile 2");
closedTabs.Push(customOne, 4);
closedTabs.Push(customTwo, 2);
Need(closedTabs.TryPop(out ClosedTabEntry? closedTwo), "Most recent closed tab was not restorable.");
Need(closedTwo?.Tab.Name == "News", "Closed-tab stack did not restore the latest tab first.");
Need(closedTwo?.PreviousIndex == 2, "Closed-tab previous index was not preserved.");
Need(closedTwo?.Tab.Icon == "reddit", "Closed-tab icon was not preserved.");
Need(closedTwo?.Tab.BrowserProfileId == "qp-news-account" &&
    closedTwo?.Tab.BrowserProfileLabel == "Profile 2",
    "Closed-tab history dropped the isolated browser profile identity.");
Need(closedTwo?.Tab.IsCustom == true, "Restored closed tab must remain custom for persistence.");
Need(closedTabs.TryPop(out ClosedTabEntry? closedOne), "Older closed tab was not restorable.");
Need(closedOne?.Tab.Name == "Docs", "Closed-tab stack order was not LIFO.");
Need(!closedTabs.TryPop(out _), "Closed-tab stack should be empty after restores.");

var serializedClosedTab = JsonSerializer.Serialize(closedTwo?.Tab);
AiTab? restoredClosedTab = JsonSerializer.Deserialize<AiTab>(serializedClosedTab);
Need(restoredClosedTab?.Name == "News", "Restored custom tab name did not survive serialization.");
Need(restoredClosedTab?.Url == "https://news.example.com", "Restored custom tab URL did not survive serialization.");
Need(restoredClosedTab?.Icon == "reddit", "Restored custom tab icon did not survive serialization.");
Need(restoredClosedTab?.Id == "custom:news", "Restored custom tab ID did not survive serialization.");

Need(PanelShortcutResolver.TryResolve(0x54, PanelShortcutModifiers.Control, out PanelShortcut shortcut) &&
    shortcut.Command == PanelShortcutCommand.AddTab, "Ctrl+T did not resolve to AddTab.");
Need(PanelShortcutResolver.TryResolve(0x54, PanelShortcutModifiers.Control | PanelShortcutModifiers.Shift, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.RestoreClosedTab, "Ctrl+Shift+T did not resolve to RestoreClosedTab.");
Need(PanelShortcutResolver.TryResolve(0x57, PanelShortcutModifiers.Control, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.CloseCurrentCustomTab, "Ctrl+W did not resolve to CloseCurrentCustomTab.");
Need(PanelShortcutResolver.TryResolve(0x09, PanelShortcutModifiers.Control | PanelShortcutModifiers.Shift, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.SelectPreviousTab, "Ctrl+Shift+Tab did not resolve to previous tab.");
Need(PanelShortcutResolver.TryResolve(0x35, PanelShortcutModifiers.Control, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.JumpToTab &&
    shortcut.Argument == 4, "Ctrl+5 did not resolve to the fifth tab.");
Need(PanelShortcutResolver.TryResolve(0xBB, PanelShortcutModifiers.Control | PanelShortcutModifiers.Shift, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.ZoomIn, "Ctrl+Plus did not resolve to ZoomIn.");
Need(PanelShortcutResolver.TryResolve(0xBD, PanelShortcutModifiers.Control, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.ZoomOut, "Ctrl+Minus did not resolve to ZoomOut.");
Need(PanelShortcutResolver.TryResolve(0x30, PanelShortcutModifiers.Control, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.ResetZoom, "Ctrl+0 did not resolve to ResetZoom.");
Need(!PanelShortcutResolver.TryResolve(0x54, PanelShortcutModifiers.Control | PanelShortcutModifiers.Alt, out _),
    "Ctrl+Alt shortcuts should be left alone.");
Need(PanelShortcutResolver.TryResolve(0x52, PanelShortcutModifiers.Control, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.ReloadCurrentTab, "Ctrl+R did not resolve to ReloadCurrentTab.");
Need(PanelShortcutResolver.TryResolve(0x74, PanelShortcutModifiers.None, out shortcut) &&
    shortcut.Command == PanelShortcutCommand.ReloadCurrentTab, "F5 did not resolve to ReloadCurrentTab.");

Need(FocusPolicy.ShouldFocusChromeAfterTabSwitch(isNativeTab: false),
    "Browser tab switch should move focus to app chrome.");
Need(!FocusPolicy.ShouldFocusChromeAfterTabSwitch(isNativeTab: true),
    "Native Codex tab should keep native focus policy.");
Need(FocusPolicy.ShouldFocusPageForKey(0x41, PanelShortcutModifiers.None),
    "Typing a letter should move focus to the active page.");
Need(FocusPolicy.ShouldFocusPageForKey(0x31, PanelShortcutModifiers.Shift),
    "Shift typing should move focus to the active page.");
Need(!FocusPolicy.ShouldFocusPageForKey(0x09, PanelShortcutModifiers.None),
    "Tab key should not focus the page and expose skip links.");
Need(!FocusPolicy.ShouldFocusPageForKey(0x54, PanelShortcutModifiers.Control),
    "Ctrl shortcuts should not be stolen by page focus.");
Need(!FocusPolicy.ShouldFocusPageForTyping(
        isAppEditingControlFocused: true,
        isSelectedBrowserTab: true,
        isBrowserAlreadyFocused: false,
        virtualKey: 0x41,
        modifiers: PanelShortcutModifiers.None),
    "Typing in app text boxes should not move focus to the page.");
Need(!FocusPolicy.ShouldFocusPageForTyping(
        isAppEditingControlFocused: false,
        isSelectedBrowserTab: false,
        isBrowserAlreadyFocused: false,
        virtualKey: 0x41,
        modifiers: PanelShortcutModifiers.None),
    "Typing without a selected browser tab should not move focus to the page.");
Need(!FocusPolicy.ShouldFocusPageForTyping(
        isAppEditingControlFocused: false,
        isSelectedBrowserTab: true,
        isBrowserAlreadyFocused: true,
        virtualKey: 0x41,
        modifiers: PanelShortcutModifiers.None),
    "Typing should not refocus a page that already has focus.");

Need(UpdateService.TryParseSemanticVersion("v1.7.1+build", out Version? parsedVersion) &&
    parsedVersion == new Version(1, 7, 1, 0),
    "Semantic version parser did not trim v prefix and metadata.");
Need(UpdateService.TryParseSemanticVersion("v2.0.0-dev", out Version? prereleaseVersion) &&
    prereleaseVersion == new Version(2, 0, 0, 0),
    "Semantic version parser did not trim prerelease labels.");
Need(UpdateService.TryParseSemanticVersion("1.7.2", out Version? newerVersion) &&
    newerVersion!.CompareTo(parsedVersion) > 0,
    "Semantic version comparison did not order newer patch versions.");
Need(UpdateService.FormatVersion(new Version(1, 7, 1, 0)) == "1.7.1",
    "Version display should not show trailing revision zero.");
Need(!UpdateService.TryParseSemanticVersion("nope", out _),
    "Invalid semantic version should fail.");
Need(UpdateService.TryParseSemanticVersion(UpdateService.GetCurrentVersionText(), out _),
	"Quick Panel development version is not a valid semantic version.");

Need(UpdatePresentation.GetVersionState(new Version(2, 4, 4), new Version(2, 4, 5)) ==
        UpdateVersionState.UpdateAvailable,
    "Older current version was not marked update available.");
Need(UpdatePresentation.GetVersionState(new Version(2, 4, 5), new Version(2, 4, 5)) ==
        UpdateVersionState.UpToDate,
    "Equal versions were not marked up to date.");
Need(UpdatePresentation.GetVersionState(new Version(2, 4, 6), new Version(2, 4, 5)) ==
        UpdateVersionState.RunningNewerBuild,
    "Newer local version was not distinguished from up to date.");
Need(UpdatePresentation.GetStatusMessage(new Version(2, 4, 4), new Version(2, 4, 5)) ==
        "Update available. Current v2.4.4; latest v2.4.5.",
    "Update-available status text is inaccurate.");
Need(UpdatePresentation.GetStatusMessage(new Version(2, 4, 5), new Version(2, 4, 5)) ==
        "Up to date. Current v2.4.5; latest v2.4.5.",
    "Up-to-date status text is inaccurate.");
Need(UpdatePresentation.GetStatusMessage(new Version(2, 4, 6), new Version(2, 4, 5)) ==
        "Running a newer/local build. Current v2.4.6; latest release v2.4.5.",
    "Newer/local build status text is inaccurate.");
Need(UpdatePresentation.CanDownload(UpdateVersionState.UpdateAvailable, hasDownloadAsset: true) &&
    !UpdatePresentation.CanDownload(UpdateVersionState.UpdateAvailable, hasDownloadAsset: false) &&
    !UpdatePresentation.CanDownload(UpdateVersionState.UpToDate, hasDownloadAsset: true) &&
    !UpdatePresentation.CanDownload(UpdateVersionState.RunningNewerBuild, hasDownloadAsset: true),
    "Update download button eligibility does not match the version state.");
Need(UpdatePresentation.GetErrorMessage(
        new InvalidOperationException("synthetic check"),
        UpdateFailureStage.Check) == "Update check failed: synthetic check",
    "Update check failure wording is inaccurate.");
Need(UpdatePresentation.GetErrorMessage(
        new IOException("synthetic download"),
        UpdateFailureStage.Download) == "Download failed: synthetic download",
    "Download failure wording is inaccurate.");
Need(UpdatePresentation.GetErrorMessage(
        new InvalidOperationException("synthetic preparation"),
        UpdateFailureStage.InstallPreparation) == "Update preparation failed: synthetic preparation",
    "Preparation failure was mislabeled as a download failure.");
Need(UpdateService.NormalizeReleaseNotes("\uFEFFQuick Panel 2.4.5") == "Quick Panel 2.4.5",
    "Unicode BOM was not removed from release notes.");
Need(UpdateService.NormalizeReleaseNotes("ï»¿Quick Panel 2.4.5") == "Quick Panel 2.4.5",
    "Mojibake BOM was not removed from release notes.");
Need(UpdateService.NormalizeReleaseNotes("Legitimate ï»¿ text") == "Legitimate ï»¿ text",
    "Interior release-note text was altered.");

var startupExe = Path.Combine(Path.GetTempPath(), "QuickPanel.Tests", "Install", "AIQuickPanel.exe");
var startupExeFullPath = Path.GetFullPath(startupExe);
Need(StartupService.BuildStartupCommand(startupExe) == $"\"{startupExe}\" --startup",
    "Startup command should quote the executable and pass --startup.");
Need(StartupService.TryExtractExecutablePath(StartupService.BuildStartupCommand(startupExe)) == startupExeFullPath,
    "Run-key startup command did not resolve the AIQuickPanel executable.");
Need(StartupService.TryExtractExecutablePath("@echo off\r\nstart \"\" \"" + startupExe + "\" --startup\r\n") == startupExeFullPath,
    "Startup-folder launcher did not resolve the AIQuickPanel executable.");
Need(StartupService.TryExtractExecutablePath("\"C:\\Tools\\OtherApp.exe\" --startup") == null,
    "Startup parser should ignore non-AIQuickPanel executables.");

Need(UpdateService.IsSecureUpdateUri("https://updates.example.com/version.json"),
    "HTTPS update URL should be allowed.");
Need(UpdateService.IsSecureUpdateUri("http://localhost:8080/version.json"),
    "Localhost HTTP update URL should be allowed for dev.");
Need(!UpdateService.IsSecureUpdateUri("http://updates.example.com/version.json"),
    "Plain HTTP update URL should be rejected.");
Need(UpdateService.IsValidSha256(new string('a', 64)),
    "64 hex SHA256 should validate.");
Need(!UpdateService.IsValidSha256(new string('a', 63)),
    "Short SHA256 should not validate.");
Need(!UpdateService.IsValidSha256(new string('g', 64)),
    "Non-hex SHA256 should not validate.");

byte[] updateBytes = Encoding.UTF8.GetBytes("AIQuickPanel update zip bytes");
string updateSha = Convert.ToHexString(SHA256.HashData(updateBytes)).ToLowerInvariant();
using var updateHttpClient = new HttpClient(new FakeUpdateHttpMessageHandler($$"""
{
  "latest": "99.0.0",
  "downloadUrl": "https://updates.example.com/AIQuickPanel-win-x64.zip",
  "sha256": "{{updateSha}}",
  "releaseNotesUrl": "https://updates.example.com/releases/99.0.0"
}
""", updateBytes));
var updateService = new UpdateService(updateHttpClient);
UpdateCheckResult updateCheck = await updateService.CheckForUpdatesAsync("https://updates.example.com/version.json");
Need(updateCheck.IsUpdateAvailable,
    "Update service did not detect newer manifest version.");
string updateDownloadDir = Path.Combine(Path.GetTempPath(), "QuickPanel.Tests", Guid.NewGuid().ToString("N"), "downloads");
DownloadUpdateResult updateDownload = await updateService.DownloadUpdateAsync(updateCheck.Manifest, updateDownloadDir);
Need(File.Exists(updateDownload.FilePath) && updateDownload.Sha256Verified,
    "Update download did not save and verify SHA256.");
Need(File.ReadAllBytes(updateDownload.FilePath).SequenceEqual(updateBytes),
    "Downloaded update bytes were not preserved.");
using var invalidShaHttpClient = new HttpClient(new FakeUpdateHttpMessageHandler($$"""
{
  "latest": "99.0.0",
  "downloadUrl": "https://updates.example.com/AIQuickPanel-win-x64.zip",
  "sha256": "abc",
  "releaseNotesUrl": "https://updates.example.com/releases/99.0.0"
}
""", updateBytes));
try
{
    _ = await new UpdateService(invalidShaHttpClient).CheckForUpdatesAsync("https://updates.example.com/version.json");
    throw new InvalidOperationException("Invalid SHA256 manifest should fail.");
}
catch (UpdateException ex) when (ex.Code == UpdateErrorCode.InvalidSha256)
{
}
try
{
    _ = await updateService.CheckForUpdatesAsync("http://updates.example.com/version.json");
    throw new InvalidOperationException("HTTP manifest URL should fail.");
}
catch (UpdateException ex) when (ex.Code == UpdateErrorCode.InvalidManifestUrl)
{
}

static string LocalTestDirectory()
{
    string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "QuickPanel.Tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void WriteLegacyUpdateRequest(string path, PortableUpdateRequest request)
{
    File.WriteAllText(
        path,
        JsonSerializer.Serialize(new
        {
            request.Operation,
            request.ProcessId,
            request.PayloadDirectory,
            request.InstallDirectory,
            request.CanonicalDataDirectory,
            request.ApplicationExecutable,
            request.RollbackDirectory,
            request.DownloadedZip,
            request.LogPath,
            request.RestartApplication
        }));
}
var publicReleaseHandler = new FakeUpdateHttpMessageHandler($$"""
{
  "tag_name": "v99.1.0-dev",
  "html_url": "https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v99.1.0",
  "body": "Release notes from GitHub.",
  "assets": [
    {
      "name": "version.json",
      "content_type": "application/json",
      "browser_download_url": "https://github.com/Terru03/QuickPanel-Downloads/releases/download/v99.1.0/version.json"
    },
    {
      "name": "QuickPanel-99.1.0-win-x64.zip",
      "content_type": "application/zip",
      "browser_download_url": "https://github.com/Terru03/QuickPanel-Downloads/releases/download/v99.1.0/QuickPanel-99.1.0-win-x64.zip",
      "digest": "sha256:{{updateSha}}"
    }
  ]
}
""", updateBytes);
using var githubReleaseClient = new HttpClient(publicReleaseHandler);
UpdateCheckResult githubReleaseCheck = await new UpdateService(githubReleaseClient).CheckForUpdatesAsync("");
Need(publicReleaseHandler.LastRequestUri?.AbsoluteUri ==
        "https://api.github.com/repos/Terru03/QuickPanel-Downloads/releases/latest",
    "Default update checks did not use the public binary repository.");
Need(githubReleaseCheck.IsUpdateAvailable &&
    githubReleaseCheck.Manifest.ReleaseNotesUrl == "https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v99.1.0" &&
    githubReleaseCheck.Manifest.DownloadUrl == "https://github.com/Terru03/QuickPanel-Downloads/releases/download/v99.1.0/QuickPanel-99.1.0-win-x64.zip" &&
    githubReleaseCheck.Manifest.Sha256 == updateSha &&
    githubReleaseCheck.Manifest.ReleaseNotes == "Release notes from GitHub.",
    "GitHub release update check did not parse latest version, release page, notes, and asset metadata.");
DownloadUpdateResult githubReleaseDownload = await new UpdateService(githubReleaseClient).DownloadUpdateAsync(githubReleaseCheck.Manifest, updateDownloadDir);
Need(File.Exists(githubReleaseDownload.FilePath) && githubReleaseDownload.Sha256Verified,
    "GitHub release asset download did not save and verify SHA256.");
try
{
    _ = await new UpdateService(new HttpClient(new NotFoundUpdateHttpMessageHandler())).CheckForUpdatesAsync("");
    throw new InvalidOperationException("Missing public GitHub release should fail without an authenticated CLI fallback.");
}
catch (UpdateException ex) when (ex.Code == UpdateErrorCode.GitHubReleaseUnreachable)
{
}
try
{
    _ = await new UpdateService(new HttpClient(new ThrowingUpdateHttpMessageHandler())).CheckForUpdatesAsync("");
    throw new InvalidOperationException("Offline GitHub update check should fail safely.");
}
catch (UpdateException ex) when (ex.Code == UpdateErrorCode.GitHubReleaseUnreachable)
{
}
try
{
    _ = await new UpdateService(new HttpClient(new RateLimitedUpdateHttpMessageHandler())).CheckForUpdatesAsync("");
    throw new InvalidOperationException("GitHub rate-limited update check should fail safely.");
}
catch (UpdateException ex) when (ex.Code == UpdateErrorCode.RateLimited)
{
}

Need(CodexUsageService.ExtractAccessToken("""{"tokens":{"access_token":"abc123"}}""") == "abc123",
    "Codex usage service did not extract tokens.access_token.");
Need(CodexUsageService.ExtractAccessToken("""{"tokens":{"accessToken":"camel123"}}""") == "camel123",
    "Codex usage service did not extract tokens.accessToken.");
Need(CodexUsageService.ExtractAccessToken("""{"oauth":{"access_token":"oauth-token"}}""") == "oauth-token",
    "Codex usage service did not extract oauth.access_token.");
Need(CodexUsageService.ExtractAccountId("""{"account_id":"acct_123","user_id":"user_123"}""") == "acct_123",
    "Codex usage service did not prefer account_id for reset credits.");
Need(CodexUsageService.ExtractAccountId("""{"user_id":"user_123"}""") == "user_123",
    "Codex usage service did not fall back to user_id for reset credits.");
string whamUsageJson = """
{
  "email": "person@example.com",
  "account_id": "acct_test",
  "plan_type": "plus",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": {
      "used_percent": 7,
      "limit_window_seconds": 18000,
      "reset_after_seconds": 17489,
      "reset_at": 1782331601
    },
    "secondary_window": {
      "used_percent": 1,
      "limit_window_seconds": 604800,
      "reset_after_seconds": 604289,
      "reset_at": 1782918401
    }
  },
  "credits": {
    "has_credits": false,
    "unlimited": false,
    "overage_limit_reached": false,
    "balance": "0"
  },
  "rate_limit_reset_credits": {
    "available_count": 2,
    "grants": [
      { "granted_at": "12.06.2026 06:58:00", "expires_at": "12.07.2026 06:58:00" },
      { "granted_at": "18.06.2026 03:26:04", "expires_at": "18.07.2026 03:26:04" }
    ]
  },
  "access_token": "do-not-show"
}
""";
string usageSummary = CodexUsageService.BuildUsageSummary(whamUsageJson);
Need(usageSummary.Contains("Current 5h window usage/status", StringComparison.Ordinal) &&
    usageSummary.Contains("Used: 7%", StringComparison.Ordinal) &&
    usageSummary.Contains("Weekly usage/status", StringComparison.Ordinal) &&
    usageSummary.Contains("Used: 1%", StringComparison.Ordinal) &&
    usageSummary.Contains("Reset credits", StringComparison.Ordinal) &&
    usageSummary.Contains("Available: 2", StringComparison.Ordinal) &&
    usageSummary.Contains("Grant / expiry fields", StringComparison.Ordinal) &&
    !usageSummary.Contains("person@example.com", StringComparison.Ordinal) &&
    !usageSummary.Contains("do-not-show", StringComparison.Ordinal),
    "Codex usage summary did not surface useful fields safely.");
CodexUsageMetrics? usageMetrics = CodexUsageService.BuildUsageMetrics(whamUsageJson);
Need(usageMetrics?.PrimaryWindow?.UsedPercent == 7 &&
    usageMetrics.PrimaryWindow.WindowLength == TimeSpan.FromHours(5) &&
    usageMetrics.PrimaryWindow.ResetAt == DateTimeOffset.FromUnixTimeSeconds(1782331601) &&
    usageMetrics.SecondaryWindow?.UsedPercent == 1 &&
    usageMetrics.SecondaryWindow.WindowLength == TimeSpan.FromDays(7) &&
    usageMetrics.BankedResets == 2 &&
    usageMetrics.ResetExpiries.Count == 2 &&
    usageMetrics.ResetExpiries[0].GrantedAt != null &&
    usageMetrics.ResetExpiries[0].ExpiresAt != null &&
    usageMetrics.ResetExpiries[1].GrantedAt != null &&
    usageMetrics.ResetExpiries[1].ExpiresAt != null,
    "Codex usage metrics did not parse API windows for UI bars.");
string noExpiryUsageSummary = CodexUsageService.BuildUsageSummary("""
{
  "rate_limit": {
    "primary_window": { "used_percent": 7 },
    "secondary_window": { "used_percent": 1 }
  },
  "rate_limit_reset_credits": { "available_count": 1 }
}
""");
Need(noExpiryUsageSummary.Contains("API expiry: not exposed", StringComparison.Ordinal) &&
    !noExpiryUsageSummary.Contains("Estimated expiry:", StringComparison.Ordinal),
    "Codex usage summary should expose missing API expiry without estimating when no grant time exists.");
string grantOnlyUsageJson = """
{
  "rate_limit": {
    "primary_window": { "used_percent": 7 },
    "secondary_window": { "used_percent": 1 }
  },
  "rate_limit_reset_credits": {
    "available_count": 1,
    "grants": [
      { "resetGrantTime": "2026-06-24T10:00:00Z" }
    ]
  }
}
""";
string grantOnlyUsageSummary = CodexUsageService.BuildUsageSummary(grantOnlyUsageJson);
Need(grantOnlyUsageSummary.Contains("API expiry: not exposed", StringComparison.Ordinal) &&
    grantOnlyUsageSummary.Contains("Estimated expiry:", StringComparison.Ordinal) &&
    grantOnlyUsageSummary.Contains("grant + 30 days", StringComparison.Ordinal) &&
    grantOnlyUsageSummary.Contains("estimated, not confirmed", StringComparison.Ordinal),
    "Codex usage summary should show a clearly labeled estimated expiry when grant time is available.");
CodexUsageMetrics? grantOnlyMetrics = CodexUsageService.BuildUsageMetrics(grantOnlyUsageJson);
Need(grantOnlyMetrics?.ResetExpiries.Count == 1 &&
    grantOnlyMetrics.ResetExpiries[0].ExpiresAt == null &&
    grantOnlyMetrics.ResetExpiries[0].EstimatedExpiresAt == DateTimeOffset.Parse("2026-07-24T10:00:00Z", CultureInfo.InvariantCulture),
    "Codex usage metrics should calculate grant time + 30 days only as an estimate.");
CodexResetCreditsSummary resetCreditsSummary = CodexUsageService.BuildResetCreditsSummary("""
{
  "available_count": 1,
  "credits": [
    {
      "title": "Reset GPT-5 thinking",
      "status": "available",
      "granted_at": "2026-06-24T10:00:00Z",
      "expires_at": "2026-07-24T10:00:00Z",
      "redeemed_at": "2026-06-25T12:30:00Z"
    }
  ],
  "access_token": "do-not-show"
}
""");
Need(resetCreditsSummary.AvailableCount == 1 &&
    resetCreditsSummary.ApiSucceeded &&
    resetCreditsSummary.ApiStatus == "OK" &&
    resetCreditsSummary.HasExpiryFields &&
    resetCreditsSummary.ResetExpiries.Count == 1 &&
    resetCreditsSummary.ResetExpiries[0].Title == "Reset GPT-5 thinking" &&
    resetCreditsSummary.ResetExpiries[0].Status == "available" &&
    resetCreditsSummary.ResetExpiries[0].GrantedAt != null &&
    resetCreditsSummary.ResetExpiries[0].ExpiresAt != null &&
    resetCreditsSummary.ResetExpiries[0].RedeemedAt != null &&
    resetCreditsSummary.Text.Contains("Reset credit details:", StringComparison.Ordinal) &&
    resetCreditsSummary.Text.Contains("Available: 1", StringComparison.Ordinal) &&
    resetCreditsSummary.Text.Contains("Title: Reset GPT-5 thinking", StringComparison.Ordinal) &&
    resetCreditsSummary.Text.Contains("Status: available", StringComparison.Ordinal) &&
    resetCreditsSummary.Text.Contains("Granted:", StringComparison.Ordinal) &&
    resetCreditsSummary.Text.Contains("Expires:", StringComparison.Ordinal) &&
    resetCreditsSummary.Text.Contains("Redeemed:", StringComparison.Ordinal) &&
    !resetCreditsSummary.Text.Contains("do-not-show", StringComparison.Ordinal),
    "Reset credits summary did not parse and display reset credit details safely.");
string codexAuthPath = Path.Combine(Path.GetDirectoryName(TempSettingsPath())!, "auth.json");
string codexStatePath = Path.Combine(Path.GetDirectoryName(codexAuthPath)!, ".codex-global-state.json");
Directory.CreateDirectory(Path.GetDirectoryName(codexAuthPath)!);
File.WriteAllText(codexAuthPath, """{"tokens":{"access_token":"safe-test-token"}}""");
File.WriteAllText(codexStatePath, """{"weekly_reset_at":"2026-06-29T00:00:00Z"}""");
var fakeCodexUsageHandler = new FakeCodexUsageHttpMessageHandler();
var codexUsageService = new CodexUsageService(new HttpClient(fakeCodexUsageHandler), codexAuthPath, codexStatePath);
CodexUsageSnapshot codexUsageSnapshot = await codexUsageService.RefreshAsync();
Need(codexUsageSnapshot.UsageSummary.Contains("Used: 7%", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Weekly usage/status", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Reset credits API", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Reset credit details:", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Available: 1", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Reset GPT-5 thinking", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Status: available", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Granted:", StringComparison.Ordinal) &&
    codexUsageSnapshot.UsageSummary.Contains("Expires:", StringComparison.Ordinal) &&
    !codexUsageSnapshot.UsageSummary.Contains("API expiry: not exposed", StringComparison.Ordinal) &&
    !codexUsageSnapshot.UsageSummary.Contains("Estimated expiry:", StringComparison.Ordinal) &&
    codexUsageSnapshot.ResetCreditsApiSucceeded &&
    codexUsageSnapshot.ResetCreditsApiStatus == "OK" &&
    codexUsageSnapshot.Metrics?.PrimaryWindow?.UsedPercent == 7 &&
    codexUsageSnapshot.Metrics.BankedResets == 1 &&
    codexUsageSnapshot.Metrics.ResetExpiries.Any(expiry => expiry.Title == "Reset GPT-5 thinking" && expiry.ExpiresAt != null) &&
    !codexUsageSnapshot.UsageSummary.Contains("safe-test-token", StringComparison.Ordinal),
    "Codex usage refresh did not use bearer token safely.");
Need(fakeCodexUsageHandler.UsageWasCalled &&
    fakeCodexUsageHandler.ResetCreditsWasCalled &&
    fakeCodexUsageHandler.ResetCreditsAccountHeader == "acct-safe-test",
    "Codex usage refresh did not call reset credits with the account header from /wham/usage.");
CodexStateSummary codexStateSummary = CodexUsageService.BuildStateSummary("""
{
  "usage": { "remaining": 12 },
  "weekly_reset_at": "2026-06-29T00:00:00Z",
  "grants": [{ "resetGrantTime": "2026-06-24T10:00:00Z" }],
  "bankedResets": 1
}
""");
Need(codexStateSummary.Text.Contains("weekly_reset_at", StringComparison.Ordinal) &&
    codexStateSummary.Text.Contains("bankedResets", StringComparison.Ordinal) &&
    codexStateSummary.Text.Contains("Estimated expiry:", StringComparison.Ordinal) &&
    codexStateSummary.Text.Contains("grant + 30 days", StringComparison.Ordinal),
    "Codex state summary did not surface reset, bank, and estimated expiry fields.");
Need(!codexStateSummary.HasExpiryFields,
    "Codex state summary should not invent expiry fields.");
CodexStateSummary codexExpiryStateSummary = CodexUsageService.BuildStateSummary("""
{
  "bankedResets": 2,
  "grants": [
    { "granted_at": "12.06.2026 06:58:00", "expires_at": "12.07.2026 06:58:00" },
    { "granted_at": "18.06.2026 03:26:04", "expires_at": "18.07.2026 03:26:04" }
  ]
}
""");
Need(codexExpiryStateSummary.HasExpiryFields &&
    codexExpiryStateSummary.ResetExpiries?.Count == 2 &&
    codexExpiryStateSummary.ResetExpiries[0].GrantedAt != null &&
    codexExpiryStateSummary.ResetExpiries[0].ExpiresAt != null,
    "Codex state summary did not parse per-reset expiry dates.");

BrowserViewportBounds viewport = BrowserViewportLayout.Calculate(500, 650, measuredChromeHeight: 42);
Need(viewport.Y == 42, "Viewport must start below the measured tab chrome.");
Need(viewport.Height == 608, "Viewport height should subtract measured tab chrome height.");
viewport = BrowserViewportLayout.Calculate(500, 650, measuredChromeHeight: 0);
Need(viewport.Y == BrowserViewportLayout.FallbackChromeHeight, "Viewport should use the fallback only when measurement is unavailable.");
viewport = BrowserViewportLayout.Calculate(500, 20);
Need(viewport.Height == 0, "Viewport height must not go negative.");

TabContextMenuState builtInMenu = TabContextMenuStateFactory.Create(
    isCustomWebTab: false,
    hasClosedTabs: false,
    hasUrl: true,
    zoomFactor: 1.0,
    isPinned: true);
Need(builtInMenu.CanDuplicateTab, "Built-in web tabs should be duplicable into custom tabs.");
Need(!builtInMenu.CanEditCustomTab, "Built-in tab context menu must not expose custom edit actions.");
Need(builtInMenu.CanTogglePin, "Built-in tab context menu should expose pin toggle.");
Need(!builtInMenu.CanCloseTab, "Built-in tab context menu must not expose close.");
Need(!builtInMenu.CanReopenClosedTab, "Reopen should be disabled without a closed tab.");
Need(builtInMenu.CanOpenExternally, "Open externally should be available for web tabs.");
Need(builtInMenu.CanCopyUrl, "Copy URL should be available for web tabs.");
Need(!builtInMenu.CanResetZoom, "Reset zoom should be disabled at 100%.");
Need(!builtInMenu.CanClearThisTabData, "Per-tab data clearing should stay disabled for shared WebView2 profile.");
Need(builtInMenu.IsPinned, "Built-in tabs should behave pinned.");

TabContextMenuState customMenu = TabContextMenuStateFactory.Create(
    isCustomWebTab: true,
    hasClosedTabs: true,
    hasUrl: true,
    zoomFactor: 1.25,
    isPinned: false);
Need(customMenu.CanDuplicateTab, "Custom web tabs should be duplicable.");
Need(customMenu.CanEditCustomTab, "Custom tab context menu should expose edit actions.");
Need(customMenu.CanTogglePin, "Custom tab context menu should expose pin toggle.");
Need(customMenu.CanCloseTab, "Custom tab context menu should expose close.");
Need(customMenu.CanReopenClosedTab, "Reopen should be enabled when a closed custom tab exists.");
Need(customMenu.CanResetZoom, "Reset zoom should be enabled when zoom is not 100%.");
Need(!customMenu.IsPinned, "Unpinned custom tab state did not survive menu state.");

string settingsPath = TempSettingsPath();
var settings = new SettingsService(settingsPath);
string expectedCanonicalDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "QuickPanel",
    "data");
Need(Path.GetFullPath(PortableDataPaths.DataDirectory)
        .Equals(Path.GetFullPath(expectedCanonicalDataDirectory), StringComparison.OrdinalIgnoreCase),
    "Persistent data directory did not resolve to %LOCALAPPDATA%\\QuickPanel\\data.");
Need(!Path.GetFullPath(PortableDataPaths.DataDirectory)
        .StartsWith(Path.GetFullPath(AppContext.BaseDirectory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase),
    "Persistent data directory must not derive from the executable/build directory.");
Need(PortableDataPaths.SettingsPath == Path.Combine(expectedCanonicalDataDirectory, "settings.json") &&
    PortableDataPaths.WebView2Directory == Path.Combine(expectedCanonicalDataDirectory, "WebView2") &&
    PortableDataPaths.TabIconCacheDirectory == Path.Combine(expectedCanonicalDataDirectory, "IconCache"),
    "Persistent settings, WebView2, and icon paths must derive from the canonical data directory.");
IReadOnlyList<ProfileMigrationCandidate> legacyCandidates = PortableDataPaths.GetLegacyProfileCandidates(
    Path.Combine(Path.GetTempPath(), "SyntheticAIQuickPanelInstall"));
Need(legacyCandidates.Any(candidate => candidate.Kind == "legacy-root" && candidate.IsProductRoot) &&
    legacyCandidates.Any(candidate => candidate.Kind == "legacy-canonical-data" && !candidate.IsProductRoot) &&
    legacyCandidates.Any(candidate => candidate.Kind == "legacy-nested") &&
    legacyCandidates.Any(candidate => candidate.Kind == "legacy-install-data") &&
    legacyCandidates.Any(candidate => candidate.Kind == "legacy-executable-data") &&
    legacyCandidates.All(candidate => !Path.GetFullPath(candidate.Directory)
        .Equals(Path.GetFullPath(PortableDataPaths.DataDirectory), StringComparison.OrdinalIgnoreCase)),
    "Legacy profile discovery did not cover every supported non-canonical layout.");
Need(!PortableDataPaths.IsUnderPortableDataDirectory(PortableDataPaths.MigrationBackupDirectory) &&
    !PortableDataPaths.IsUnderPortableDataDirectory(PortableDataPaths.MigrationLogDirectory) &&
    !PortableDataPaths.IsUnderPortableDataDirectory(PortableDataPaths.UpdateBackupDirectory),
    "Migration recovery data must remain outside the active canonical profile.");
Need(ProfileMigrationProcessGuard.ReferencesAnyProfile(
        $"--user-data-dir=\"{expectedCanonicalDataDirectory}\\WebView2\" --type=renderer",
        [expectedCanonicalDataDirectory]),
    "WebView2 process guard did not recognize a referenced canonical profile.");
Need(!ProfileMigrationProcessGuard.ReferencesAnyProfile(
        "--user-data-dir=\"C:\\Unrelated\\WebView2\" --type=renderer",
        [expectedCanonicalDataDirectory]),
    "WebView2 process guard matched an unrelated profile.");
Need(ProfileMigrationProcessGuard.IsQuickPanelProcessName("QuickPanel") &&
    ProfileMigrationProcessGuard.IsQuickPanelProcessName("AIQuickPanel") &&
    !ProfileMigrationProcessGuard.IsQuickPanelProcessName("UnrelatedApp"),
    "Profile migration process guard does not recognize both current and legacy application names.");
Need(!ReleasePayloadPolicy.IsSafeInstallTarget(expectedCanonicalDataDirectory, expectedCanonicalDataDirectory),
    "Updater accepted the active profile as its install target.");
Need(ReleasePayloadPolicy.IsSafeInstallTarget(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AIQuickPanel"),
        expectedCanonicalDataDirectory),
    "Updater rejected the independent per-user application install directory.");
IReadOnlyList<string> forbiddenReleaseEntries = ReleasePayloadPolicy.FindForbiddenEntries(
    [
        "AIQuickPanel.exe",
        "data/settings.json",
        "wrapper/WebView2/EBWebView/Default/Network/Cookies",
        "wrapper/state.sqlite",
        "AIQuickPanel.pdb",
        "certificates/release.pfx",
        "config/.env",
        "obj/Release/generated.dll",
        "profiles.json"
    ]);
Need(forbiddenReleaseEntries.Count == 8,
    "Release payload policy did not reject profile, debug, secret, certificate, or build artifacts.");

BridgePayloadSelectionTests.Run();
string updateSafetySandbox = LocalTestDirectory();
string updateInstallDirectory = Path.Combine(updateSafetySandbox, "Programs", "AIQuickPanel");
string updatePayloadDirectory = Path.Combine(updateSafetySandbox, "payload");
string updateProfileDirectory = Path.Combine(updateSafetySandbox, "AIQuickPanel", "data");
Directory.CreateDirectory(updateInstallDirectory);
Directory.CreateDirectory(updatePayloadDirectory);
string updateStagingRoot = PortableUpdateInstaller.GetUpdateStagingRoot(
    updateProfileDirectory,
    updateInstallDirectory);
Need(updateStagingRoot == Path.Combine(
        PortableDataPaths.GetMaintenanceDirectory(updateProfileDirectory),
        "update-staging"),
    "Portable updater staging did not move to persistent profile-maintenance storage.");
string legacyInstallProfileDirectory = Path.Combine(updateInstallDirectory, "data");
WriteSyntheticProfile(legacyInstallProfileDirectory, meaningfulSettings: true, browserState: true, marker: "legacy-install-update");
string legacyInstallProfileHashBeforeUpdate = HashDirectory(legacyInstallProfileDirectory);
WriteSyntheticProfile(updateProfileDirectory, meaningfulSettings: true, browserState: true, marker: "update");
Directory.CreateDirectory(Path.Combine(updateProfileDirectory, "IconCache"));
File.WriteAllBytes(Path.Combine(updateProfileDirectory, "IconCache", "custom.ico"), [9, 8, 7, 6]);
string profileHashBeforeUpdate = HashDirectory(updateProfileDirectory);
File.WriteAllText(Path.Combine(updateInstallDirectory, "AIQuickPanel.exe"), "version-n");
File.WriteAllText(Path.Combine(updateInstallDirectory, "AIQuickPanel.dll"), "version-n-library");
File.WriteAllText(Path.Combine(updateInstallDirectory, "AIQuickPanel.deps.json"), "{}");
File.WriteAllText(Path.Combine(updateInstallDirectory, "AIQuickPanel.runtimeconfig.json"), "{}");
File.WriteAllText(Path.Combine(updateInstallDirectory, "System.Management.dll"), "stale");
File.WriteAllText(Path.Combine(updatePayloadDirectory, "AIQuickPanel.exe"), "version-n-plus-one");
File.WriteAllText(Path.Combine(updatePayloadDirectory, "current-binary.dll"), "current");
WriteApplicationManifest(updatePayloadDirectory, "AIQuickPanel.exe", "current-binary.dll");
ReleasePayloadPolicy.ReplaceApplicationFiles(
    updatePayloadDirectory,
    updateInstallDirectory,
    updateProfileDirectory);
Need(HashDirectory(updateProfileDirectory) == profileHashBeforeUpdate &&
    HashDirectory(legacyInstallProfileDirectory) == legacyInstallProfileHashBeforeUpdate &&
    File.ReadAllText(Path.Combine(updateInstallDirectory, "AIQuickPanel.exe")) == "version-n-plus-one" &&
    File.Exists(Path.Combine(updateInstallDirectory, "current-binary.dll")) &&
    !File.Exists(Path.Combine(updateInstallDirectory, "System.Management.dll")),
    "Simulated N-to-N+1 binary update changed user data or retained stale application files.");

string accidentallyLaunchedBackupDirectory = Path.Combine(
    updateSafetySandbox,
    "WrapperProfile",
    "data-maintenance",
    "update-backups",
    "before-2.4.7-20260910-110229");
ReleasePayloadPolicy.CopyApplicationFiles(
    updateInstallDirectory,
    accidentallyLaunchedBackupDirectory,
    updateProfileDirectory);
bool rejectedAccidentallyLaunchedBackup = false;
try
{
    _ = PortableUpdateInstaller.CreateUpdateRequest(
        0,
        updatePayloadDirectory,
        accidentallyLaunchedBackupDirectory,
        updateProfileDirectory,
        Path.Combine(accidentallyLaunchedBackupDirectory, "AIQuickPanel.exe"),
        Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
            "wrong-folder-regression-backup"),
        null,
        Path.Combine(updateSafetySandbox, "wrong-folder-regression.log"),
        restartApplication: false);
}
catch (InvalidOperationException)
{
    rejectedAccidentallyLaunchedBackup = true;
}
Need(rejectedAccidentallyLaunchedBackup,
    "Updater accepted an accidentally launched update-backup copy as the active installation.");
Need(!StartupService.IsEligibleShortcutTarget(
        Path.Combine(accidentallyLaunchedBackupDirectory, "AIQuickPanel.exe")),
    "Startup service accepted an update-backup executable as a shortcut target.");

string normalPortableInstallDirectory = Path.Combine(updateSafetySandbox, "CustomPortableLocation");
ReleasePayloadPolicy.CopyApplicationFiles(
    updateInstallDirectory,
    normalPortableInstallDirectory,
    updateProfileDirectory);
string normalPortableExecutable = Path.Combine(normalPortableInstallDirectory, "AIQuickPanel.exe");
Need(PortableUpdateInstaller.ResolveActiveInstallExecutable(normalPortableExecutable) ==
        Path.GetFullPath(normalPortableExecutable) &&
    StartupService.IsEligibleShortcutTarget(normalPortableExecutable),
    "Updater or shortcut policy rejected a valid manifest-owned installation in a custom folder.");

string temporaryInstallDirectory = Path.Combine(
    Path.GetTempPath(),
    "QuickPanelUpdate",
    Guid.NewGuid().ToString("N"),
    "payload");
ReleasePayloadPolicy.CopyApplicationFiles(
    updateInstallDirectory,
    temporaryInstallDirectory,
    updateProfileDirectory);
bool rejectedTemporaryInstall = false;
try
{
    _ = PortableUpdateInstaller.ResolveActiveInstallExecutable(
        Path.Combine(temporaryInstallDirectory, "AIQuickPanel.exe"));
}
catch (InvalidOperationException)
{
    rejectedTemporaryInstall = true;
}
Need(rejectedTemporaryInstall &&
    !StartupService.IsEligibleShortcutTarget(Path.Combine(temporaryInstallDirectory, "AIQuickPanel.exe")),
    "Updater or shortcut policy accepted an application copy from the temporary update workspace.");
Directory.Delete(Path.Combine(Path.GetTempPath(), "QuickPanelUpdate", Path.GetFileName(Path.GetDirectoryName(temporaryInstallDirectory)!)), recursive: true);

string aliasedTemporaryInstallDirectory = Path.Combine(
    Path.GetTempPath(),
    "QuickPanel.TestsAlias",
    Guid.NewGuid().ToString("N"));
ReleasePayloadPolicy.CopyApplicationFiles(
    updateInstallDirectory,
    aliasedTemporaryInstallDirectory,
    updateProfileDirectory);
string extendedTemporaryInstallDirectory = @"\\?\" + Path.GetFullPath(aliasedTemporaryInstallDirectory);
bool rejectedAliasedTemporaryInstall = false;
try
{
    PortableUpdatePathSafety.EnsureActiveInstallDirectorySafe(extendedTemporaryInstallDirectory);
}
catch (InvalidOperationException)
{
    rejectedAliasedTemporaryInstall = true;
}
Need(rejectedAliasedTemporaryInstall,
    "Updater accepted the system temporary directory through an extended Windows path alias.");
Need(!ReleasePayloadPolicy.IsSafeInstallTarget(
        extendedTemporaryInstallDirectory,
        aliasedTemporaryInstallDirectory),
    "Install/profile separation treated two Windows aliases of the same directory as independent.");
Directory.Delete(aliasedTemporaryInstallDirectory, recursive: true);

string malformedManifestInstallDirectory = Path.Combine(updateSafetySandbox, "MalformedManifestInstall");
ReleasePayloadPolicy.CopyApplicationFiles(
    updateInstallDirectory,
    malformedManifestInstallDirectory,
    updateProfileDirectory);
File.WriteAllText(
    Path.Combine(malformedManifestInstallDirectory, ReleasePayloadPolicy.ApplicationManifestFileName),
    "{not-json");
string malformedManifestExecutable = Path.Combine(malformedManifestInstallDirectory, "AIQuickPanel.exe");
bool rejectedMalformedManifest = false;
try
{
    _ = PortableUpdateInstaller.ResolveActiveInstallExecutable(malformedManifestExecutable);
}
catch (InvalidDataException)
{
    rejectedMalformedManifest = true;
}
Need(rejectedMalformedManifest && !StartupService.IsEligibleShortcutTarget(malformedManifestExecutable),
    "A malformed application manifest escaped the updater or shortcut safe-failure path.");
string malformedManifestInstallHashBeforeWorker = HashDirectory(malformedManifestInstallDirectory);
string? malformedManifestRestart = null;
string malformedManifestRollbackDirectory = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
    "malformed-manifest-regression-backup");
int malformedManifestWorkerExitCode = PortableUpdateWorker.Apply(
    new PortableUpdateRequest(
        "update",
        0,
        updatePayloadDirectory,
        malformedManifestInstallDirectory,
        updateProfileDirectory,
        malformedManifestExecutable,
        malformedManifestRollbackDirectory,
        null,
        Path.Combine(updateSafetySandbox, "malformed-manifest-regression.log"),
        RestartApplication: true),
    faultInjection: null,
    info =>
    {
        malformedManifestRestart = info.FileName;
        return null;
    });
Need(malformedManifestWorkerExitCode == 1 &&
    malformedManifestRestart is null &&
    !Directory.Exists(malformedManifestRollbackDirectory) &&
    HashDirectory(malformedManifestInstallDirectory) == malformedManifestInstallHashBeforeWorker,
    "Worker changed files or restarted after rejecting a malformed installed manifest.");
bool rollbackRejectedMalformedManifest = false;
try
{
    _ = PortableUpdateRollback.CreateRequest(
        updatePayloadDirectory,
        malformedManifestInstallDirectory,
        updateProfileDirectory,
        Path.Combine(updateSafetySandbox, "malformed-manifest-rollback.log"),
        processId: 0,
        restartApplication: false);
}
catch (InvalidDataException)
{
    rollbackRejectedMalformedManifest = true;
}
Need(rollbackRejectedMalformedManifest,
    "Rollback preparation accepted an install with a malformed application manifest.");

string backupInstallHashBeforeWorkerRejection = HashDirectory(accidentallyLaunchedBackupDirectory);
string profileHashBeforeWorkerRejection = HashDirectory(updateProfileDirectory);
string? rejectedBackupRestart = null;
string rejectedBackupRollbackDirectory = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
    "worker-wrong-folder-regression-backup");
int rejectedBackupWorkerExitCode = PortableUpdateWorker.Apply(
    new PortableUpdateRequest(
        "update",
        0,
        updatePayloadDirectory,
        accidentallyLaunchedBackupDirectory,
        updateProfileDirectory,
        Path.Combine(accidentallyLaunchedBackupDirectory, "AIQuickPanel.exe"),
        rejectedBackupRollbackDirectory,
        null,
        Path.Combine(updateSafetySandbox, "worker-wrong-folder-regression.log"),
        RestartApplication: true),
    faultInjection: null,
    info =>
    {
        rejectedBackupRestart = info.FileName;
        return null;
    });
Need(rejectedBackupWorkerExitCode == 1 &&
    rejectedBackupRestart is null &&
    !Directory.Exists(rejectedBackupRollbackDirectory) &&
    HashDirectory(accidentallyLaunchedBackupDirectory) == backupInstallHashBeforeWorkerRejection &&
    HashDirectory(updateProfileDirectory) == profileHashBeforeWorkerRejection,
    "Worker changed files or restarted an executable after rejecting an update-backup install target.");

string legacy246ProfileDirectory = Path.Combine(updateSafetySandbox, "legacy-2.4.6-profile", "data");
WriteSyntheticProfile(legacy246ProfileDirectory, meaningfulSettings: true, browserState: true, marker: "legacy-2.4.6");
string legacy246StableInstall = Path.Combine(updateSafetySandbox, "legacy-2.4.6-stable-install");
string legacy246SecondInstall = Path.Combine(updateSafetySandbox, "legacy-2.4.6-second-install");
string legacy246LaunchedBackup = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(legacy246ProfileDirectory),
    "before-2.4.6-20260911-041537");
string legacy246Payload = Path.Combine(updateSafetySandbox, "legacy-2.4.6-payload");
ReleasePayloadPolicy.CopyApplicationFiles(updateInstallDirectory, legacy246StableInstall, legacy246ProfileDirectory);
ReleasePayloadPolicy.CopyApplicationFiles(updateInstallDirectory, legacy246SecondInstall, legacy246ProfileDirectory);
ReleasePayloadPolicy.CopyApplicationFiles(updateInstallDirectory, legacy246LaunchedBackup, legacy246ProfileDirectory);
Directory.CreateDirectory(legacy246Payload);
File.WriteAllText(Path.Combine(legacy246Payload, "AIQuickPanel.exe"), "legacy-2.4.6-new");
File.WriteAllText(Path.Combine(legacy246Payload, "legacy-2.4.6-new.dll"), "new");
WriteApplicationManifest(legacy246Payload, "AIQuickPanel.exe", "legacy-2.4.6-new.dll");
string legacy246ProfileHash = HashDirectory(legacy246ProfileDirectory);
string legacy246LaunchedBackupHash = HashDirectory(legacy246LaunchedBackup);
string legacy246RequestRoot = Path.Combine(updateSafetySandbox, "legacy-2.4.6-request");
Directory.CreateDirectory(legacy246RequestRoot);
string legacy246RequestPath = Path.Combine(legacy246RequestRoot, "update-request.json");
string legacy246Rollback = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(legacy246ProfileDirectory),
    "2.4.6-20260911-041537");
var legacy246Request = new PortableUpdateRequest(
    "update",
    0,
    legacy246Payload,
    legacy246LaunchedBackup,
    legacy246ProfileDirectory,
    Path.Combine(legacy246LaunchedBackup, "AIQuickPanel.exe"),
    legacy246Rollback,
    null,
    Path.Combine(legacy246RequestRoot, "update.log"),
    RestartApplication: true);
WriteLegacyUpdateRequest(legacy246RequestPath, legacy246Request);
string? legacy246Restart = null;
int legacy246ExitCode = PortableUpdateWorker.ApplyRequestFile(
    legacy246RequestPath,
    [Path.Combine(legacy246StableInstall, "AIQuickPanel.exe")],
    info =>
    {
        legacy246Restart = info.FileName;
        return null;
    });
string legacy246DurableLog = Directory.GetFiles(
    PortableDataPaths.GetUpdateLogDirectory(legacy246ProfileDirectory),
    "update-*.log").Single();
string legacy246DurableLogText = File.ReadAllText(legacy246DurableLog);
string legacy246ResolutionLine = File.ReadLines(legacy246DurableLog)
    .Single(line => line.Contains("stage=legacy-target-resolved", StringComparison.Ordinal));
Need(legacy246ExitCode == 0 &&
     File.ReadAllText(Path.Combine(legacy246StableInstall, "AIQuickPanel.exe")) == "legacy-2.4.6-new" &&
     File.Exists(Path.Combine(legacy246StableInstall, "legacy-2.4.6-new.dll")) &&
     HashDirectory(legacy246LaunchedBackup) == legacy246LaunchedBackupHash &&
     HashDirectory(legacy246ProfileDirectory) == legacy246ProfileHash &&
     File.Exists(Path.Combine(legacy246Rollback, "AIQuickPanel.exe")) &&
     Path.GetFullPath(legacy246Restart!).Equals(
         Path.GetFullPath(Path.Combine(legacy246StableInstall, "AIQuickPanel.exe")),
         StringComparison.OrdinalIgnoreCase) &&
     legacy246DurableLogText.Contains("stage=legacy-target-resolved", StringComparison.Ordinal) &&
     legacy246DurableLogText.Contains("stage=success", StringComparison.Ordinal) &&
     !legacy246ResolutionLine.Contains(legacy246LaunchedBackup, StringComparison.OrdinalIgnoreCase) &&
     !legacy246ResolutionLine.Contains(legacy246StableInstall, StringComparison.OrdinalIgnoreCase),
    "Exact v2.4.6 request topology did not update and restart the one stable install while preserving its profile and launched backup.");

string legacy245ProductDirectory = Path.Combine(updateSafetySandbox, "legacy-2.4.5-product");
string legacy245ProfileDirectory = Path.Combine(legacy245ProductDirectory, "data");
WriteSyntheticProfile(legacy245ProfileDirectory, meaningfulSettings: true, browserState: true, marker: "legacy-2.4.5");
string legacy245StableInstall = Path.Combine(updateSafetySandbox, "legacy-2.4.5-stable-install");
string legacy245LaunchedBackup = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(legacy245ProfileDirectory),
    "before-2.4.5-20260911-041537");
string legacy245Payload = Path.Combine(updateSafetySandbox, "legacy-2.4.5-payload");
ReleasePayloadPolicy.CopyApplicationFiles(updateInstallDirectory, legacy245StableInstall, legacy245ProfileDirectory);
ReleasePayloadPolicy.CopyApplicationFiles(updateInstallDirectory, legacy245LaunchedBackup, legacy245ProfileDirectory);
Directory.CreateDirectory(legacy245Payload);
File.WriteAllText(Path.Combine(legacy245Payload, "AIQuickPanel.exe"), "legacy-2.4.5-new");
WriteApplicationManifest(legacy245Payload, "AIQuickPanel.exe");
string legacy245ProfileHash = HashDirectory(legacy245ProfileDirectory);
string legacy245LaunchedBackupHash = HashDirectory(legacy245LaunchedBackup);
string legacy245RequestRoot = Path.Combine(updateSafetySandbox, "legacy-2.4.5-request");
Directory.CreateDirectory(legacy245RequestRoot);
string legacy245RequestPath = Path.Combine(legacy245RequestRoot, "update-request.json");
string legacy245Rollback = Path.Combine(legacy245ProductDirectory, "update-backups", "2.4.5-20260911-041537");
var legacy245Request = new PortableUpdateRequest(
    "update",
    0,
    legacy245Payload,
    legacy245LaunchedBackup,
    legacy245ProfileDirectory,
    Path.Combine(legacy245LaunchedBackup, "AIQuickPanel.exe"),
    legacy245Rollback,
    null,
    Path.Combine(legacy245RequestRoot, "update.log"),
    RestartApplication: false);
WriteLegacyUpdateRequest(legacy245RequestPath, legacy245Request);
int legacy245ExitCode = PortableUpdateWorker.ApplyRequestFile(
    legacy245RequestPath,
    [Path.Combine(legacy245StableInstall, "AIQuickPanel.exe")],
    _ => null);
string[] legacy245CurrentBackups = Directory.GetDirectories(
    PortableDataPaths.GetUpdateBackupDirectory(legacy245ProfileDirectory));
Need(legacy245ExitCode == 0 &&
     File.ReadAllText(Path.Combine(legacy245StableInstall, "AIQuickPanel.exe")) == "legacy-2.4.5-new" &&
     HashDirectory(legacy245LaunchedBackup) == legacy245LaunchedBackupHash &&
     HashDirectory(legacy245ProfileDirectory) == legacy245ProfileHash &&
     !Directory.Exists(legacy245Rollback) &&
     legacy245CurrentBackups.Length == 2 &&
     legacy245CurrentBackups.Any(path =>
         !Path.GetFullPath(path).Equals(Path.GetFullPath(legacy245LaunchedBackup), StringComparison.OrdinalIgnoreCase) &&
         File.Exists(Path.Combine(path, "AIQuickPanel.exe")) &&
         File.ReadAllText(Path.Combine(path, "AIQuickPanel.exe")) == "version-n-plus-one"),
    "Exact v2.4.5 request did not normalize its legacy rollback destination before updating the stable install.");

string legacyRejectedStableHash = HashDirectory(legacy245StableInstall);
string legacyRejectedSecondInstallHash = HashDirectory(legacy246SecondInstall);
string legacyRejectedBackupHash = HashDirectory(legacy245LaunchedBackup);
string legacyRejectedProfileHash = HashDirectory(legacy245ProfileDirectory);
int legacyRejectedBackupDirectoryCount = Directory.GetDirectories(
    PortableDataPaths.GetUpdateBackupDirectory(legacy245ProfileDirectory)).Length;
string zeroCandidateRequestPath = Path.Combine(legacy245RequestRoot, "zero-candidate-request.json");
WriteLegacyUpdateRequest(
    zeroCandidateRequestPath,
    legacy245Request with { LogPath = Path.Combine(legacy245RequestRoot, "zero-candidate.log") });
int zeroCandidateExitCode = PortableUpdateWorker.ApplyRequestFile(zeroCandidateRequestPath, []);
string ambiguousRequestPath = Path.Combine(legacy245RequestRoot, "ambiguous-request.json");
WriteLegacyUpdateRequest(
    ambiguousRequestPath,
    legacy245Request with { LogPath = Path.Combine(legacy245RequestRoot, "ambiguous.log") });
int ambiguousExitCode = PortableUpdateWorker.ApplyRequestFile(
    ambiguousRequestPath,
    [
        Path.Combine(legacy245StableInstall, "AIQuickPanel.exe"),
        Path.Combine(legacy246SecondInstall, "AIQuickPanel.exe")
    ]);
string[] rejectedLegacyLogs = Directory.GetFiles(
    PortableDataPaths.GetUpdateLogDirectory(legacy245ProfileDirectory),
    "update-*.log");
Need(zeroCandidateExitCode == 1 &&
     ambiguousExitCode == 1 &&
     HashDirectory(legacy245StableInstall) == legacyRejectedStableHash &&
     HashDirectory(legacy246SecondInstall) == legacyRejectedSecondInstallHash &&
     HashDirectory(legacy245LaunchedBackup) == legacyRejectedBackupHash &&
     HashDirectory(legacy245ProfileDirectory) == legacyRejectedProfileHash &&
     Directory.GetDirectories(
         PortableDataPaths.GetUpdateBackupDirectory(legacy245ProfileDirectory)).Length ==
         legacyRejectedBackupDirectoryCount &&
     rejectedLegacyLogs.Count(path =>
         File.ReadAllText(path).Contains("stage=legacy-target-resolution-failed", StringComparison.Ordinal)) == 2,
    "Legacy zero/ambiguous target rejection did not fail before mutation with durable diagnostics.");

string rejectedPayloadDirectory = Path.Combine(updateSafetySandbox, "rejected-payload");
Directory.CreateDirectory(Path.Combine(rejectedPayloadDirectory, "data"));
File.WriteAllText(Path.Combine(rejectedPayloadDirectory, "AIQuickPanel.exe"), "rejected");
File.WriteAllText(Path.Combine(rejectedPayloadDirectory, "data", "settings.json"), "{}");
WriteApplicationManifest(rejectedPayloadDirectory, "AIQuickPanel.exe", "data");
string installHashBeforeRejectedUpdate = HashDirectory(updateInstallDirectory);
bool rejectedProfilePayload = false;
try
{
    ReleasePayloadPolicy.ReplaceApplicationFiles(
        rejectedPayloadDirectory,
        updateInstallDirectory,
        updateProfileDirectory);
}
catch (InvalidDataException)
{
    rejectedProfilePayload = true;
}
Need(rejectedProfilePayload &&
    HashDirectory(updateInstallDirectory) == installHashBeforeRejectedUpdate &&
    HashDirectory(updateProfileDirectory) == profileHashBeforeUpdate,
    "A profile-bearing release payload was not rejected before changing application or user data.");

string typedPayloadDirectory = Path.Combine(updateSafetySandbox, "typed-payload");
string typedRollbackDirectory = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
    "typed-rollback");
string typedUpdateLog = Path.Combine(updateSafetySandbox, "typed-update.log");
Directory.CreateDirectory(typedPayloadDirectory);
File.WriteAllText(Path.Combine(typedPayloadDirectory, "AIQuickPanel.exe"), "version-n-plus-two");
File.WriteAllText(Path.Combine(typedPayloadDirectory, "next-binary.dll"), "next");
WriteApplicationManifest(typedPayloadDirectory, "AIQuickPanel.exe", "next-binary.dll");
int typedUpdateExitCode = PortableUpdateWorker.Apply(new PortableUpdateRequest(
    "update",
    0,
    typedPayloadDirectory,
    updateInstallDirectory,
    updateProfileDirectory,
    Path.Combine(updateInstallDirectory, "AIQuickPanel.exe"),
    typedRollbackDirectory,
    null,
    typedUpdateLog,
    RestartApplication: false));
Need(typedUpdateExitCode == 0 &&
    File.ReadAllText(Path.Combine(updateInstallDirectory, "AIQuickPanel.exe")) == "version-n-plus-two" &&
    File.Exists(Path.Combine(typedRollbackDirectory, "AIQuickPanel.exe")) &&
    HashDirectory(updateProfileDirectory) == profileHashBeforeUpdate &&
    HashDirectory(legacyInstallProfileDirectory) == legacyInstallProfileHashBeforeUpdate,
    "Typed updater did not preserve canonical and legacy in-install profile state.");
int typedRollbackExitCode = PortableUpdateWorker.Apply(new PortableUpdateRequest(
    "rollback",
    0,
    typedRollbackDirectory,
    updateInstallDirectory,
    updateProfileDirectory,
    Path.Combine(updateInstallDirectory, "AIQuickPanel.exe"),
    null,
    null,
    Path.Combine(updateSafetySandbox, "typed-rollback.log"),
    RestartApplication: false));
Need(typedRollbackExitCode == 0 &&
    File.ReadAllText(Path.Combine(updateInstallDirectory, "AIQuickPanel.exe")) == "version-n-plus-one" &&
    HashDirectory(updateProfileDirectory) == profileHashBeforeUpdate &&
    HashDirectory(legacyInstallProfileDirectory) == legacyInstallProfileHashBeforeUpdate,
    "Typed rollback changed profile state or did not restore the prior application.");

string rollbackVersionRoot = Path.Combine(updateSafetySandbox, "rollback-version-root");
string rollbackVersionBackup = Path.Combine(rollbackVersionRoot, "backup-20260826-120000");
Directory.CreateDirectory(rollbackVersionBackup);
string testExecutable = Environment.ProcessPath
    ?? throw new InvalidOperationException("Test executable path is unavailable.");
File.Copy(testExecutable, Path.Combine(rollbackVersionBackup, "AIQuickPanel.exe"));
WriteApplicationManifest(rollbackVersionBackup, "AIQuickPanel.exe");
FileVersionInfo testExecutableVersion = FileVersionInfo.GetVersionInfo(testExecutable);
string? expectedRollbackVersionText = new[] { testExecutableVersion.ProductVersion, testExecutableVersion.FileVersion }
    .FirstOrDefault(candidate => UpdateService.TryParseSemanticVersion(candidate, out _));
Need(UpdateService.TryParseSemanticVersion(expectedRollbackVersionText, out Version? expectedRollbackVersion),
    "Test executable does not expose parseable ProductVersion or FileVersion metadata.");
Need(PortableUpdateRollback.GetBackupVersion(rollbackVersionBackup) ==
        UpdateService.FormatVersion(expectedRollbackVersion!),
    "Rollback label did not use executable version metadata.");
Need(PortableUpdateRollback.GetLatestBackupVersion([rollbackVersionRoot]) ==
        UpdateService.FormatVersion(expectedRollbackVersion!),
    "Latest rollback label did not inspect the selected backup executable.");
Need(PortableUpdateRollback.GetBackupVersion(rollbackVersionBackup) != Path.GetFileName(rollbackVersionBackup),
    "Rollback label exposed the timestamped backup directory name.");

var slowRollbackAvailability = new RollbackAvailabilityService(_ =>
{
    Thread.Sleep(750);
    return "2.4.9";
});
Stopwatch rollbackAvailabilityStopwatch = Stopwatch.StartNew();
Task<string?> rollbackAvailabilityTask = slowRollbackAvailability.GetLatestVersionAsync();
rollbackAvailabilityStopwatch.Stop();
bool rollbackAvailabilityWasDeferred = !rollbackAvailabilityTask.IsCompleted;
string? deferredRollbackVersion = rollbackAvailabilityTask.GetAwaiter().GetResult();
Need(rollbackAvailabilityStopwatch.Elapsed < TimeSpan.FromMilliseconds(300) &&
    rollbackAvailabilityWasDeferred &&
    deferredRollbackVersion == "2.4.9",
    "Rollback availability validation blocked the caller instead of completing in the background.");

using var rollbackAvailabilityCancellation = new CancellationTokenSource();
using var rollbackAvailabilityStarted = new ManualResetEventSlim(initialState: false);
using var rollbackAvailabilityStopped = new ManualResetEventSlim(initialState: false);
var cancellableRollbackAvailability = new RollbackAvailabilityService(token =>
{
    rollbackAvailabilityStarted.Set();
    try
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(10);
        }
    }
    finally
    {
        rollbackAvailabilityStopped.Set();
    }
});
Task<string?> cancellableRollbackTask = cancellableRollbackAvailability.GetLatestVersionAsync(
    rollbackAvailabilityCancellation.Token);
Need(rollbackAvailabilityStarted.Wait(TimeSpan.FromSeconds(2)),
    "Cancelable rollback availability work did not start.");
Stopwatch rollbackCancellationStopwatch = Stopwatch.StartNew();
rollbackAvailabilityCancellation.Cancel();
bool rollbackAvailabilityCanceled = false;
try
{
    cancellableRollbackTask.GetAwaiter().GetResult();
}
catch (OperationCanceledException)
{
    rollbackAvailabilityCanceled = true;
}
rollbackCancellationStopwatch.Stop();
Need(rollbackAvailabilityCanceled &&
    rollbackAvailabilityStopped.Wait(TimeSpan.FromSeconds(1)) &&
    rollbackCancellationStopwatch.Elapsed < TimeSpan.FromSeconds(1),
    "Closing Settings could not cooperatively stop active rollback availability work.");

using var preCanceledRollbackScan = new CancellationTokenSource();
preCanceledRollbackScan.Cancel();
bool rollbackTreeScanCanceled = false;
try
{
    PortableUpdateRollback.GetLatestBackupVersion([rollbackVersionRoot], preCanceledRollbackScan.Token);
}
catch (OperationCanceledException)
{
    rollbackTreeScanCanceled = true;
}
Need(rollbackTreeScanCanceled,
    "Rollback backup enumeration ignored a cancellation request.");

string failingPayloadDirectory = Path.Combine(updateSafetySandbox, "failing-payload");
string failingRollbackDirectory = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
    "failing-rollback");
Directory.CreateDirectory(failingPayloadDirectory);
File.WriteAllText(Path.Combine(failingPayloadDirectory, "AIQuickPanel.exe"), "broken-next");
WriteApplicationManifest(failingPayloadDirectory, "AIQuickPanel.exe");
string preFailureInstallHash = HashDirectory(updateInstallDirectory);
int failingUpdateExitCode = PortableUpdateWorker.Apply(new PortableUpdateRequest(
    "update",
    0,
    failingPayloadDirectory,
    updateInstallDirectory,
    updateProfileDirectory,
    Path.Combine(updateInstallDirectory, "AIQuickPanel.exe"),
    failingRollbackDirectory,
    null,
    Path.Combine(updateSafetySandbox, "failing-update.log"),
    RestartApplication: false),
    phase => { if (phase == "after-replace") throw new InvalidDataException("Synthetic update failure."); });
Need(failingUpdateExitCode == 1 &&
    HashDirectory(updateInstallDirectory) == preFailureInstallHash &&
    HashDirectory(updateProfileDirectory) == profileHashBeforeUpdate &&
    HashDirectory(legacyInstallProfileDirectory) == legacyInstallProfileHashBeforeUpdate,
    "Failed typed update did not restore the prior application without changing user data.");

string tamperedBackupPayload = Path.Combine(updateSafetySandbox, "tampered-backup-payload");
string tamperedBackupDirectory = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
    "tampered-backup");
Directory.CreateDirectory(tamperedBackupPayload);
File.WriteAllText(Path.Combine(tamperedBackupPayload, "AIQuickPanel.exe"), "tampered-backup-next");
WriteApplicationManifest(tamperedBackupPayload, "AIQuickPanel.exe");
string preTamperedBackupInstallHash = HashDirectory(updateInstallDirectory);
int tamperedBackupExitCode = PortableUpdateWorker.Apply(
    new PortableUpdateRequest(
        "update",
        0,
        tamperedBackupPayload,
        updateInstallDirectory,
        updateProfileDirectory,
        Path.Combine(updateInstallDirectory, "AIQuickPanel.exe"),
        tamperedBackupDirectory,
        null,
        Path.Combine(updateSafetySandbox, "tampered-backup.log"),
        RestartApplication: false),
    phase =>
    {
        if (phase == "after-backup-copy")
        {
            File.AppendAllText(Path.Combine(tamperedBackupDirectory, "AIQuickPanel.exe"), "tampered");
        }
    });
Need(tamperedBackupExitCode == 1 &&
     HashDirectory(updateInstallDirectory) == preTamperedBackupInstallHash &&
     File.Exists(Path.Combine(
         tamperedBackupDirectory,
         PortableUpdateWorker.InvalidBackupMarkerFileName)),
    "Updater mutated the install after backup verification detected tampering.");
Directory.SetLastWriteTimeUtc(tamperedBackupDirectory, DateTime.UtcNow.AddMinutes(1));
Need(!string.Equals(
        PortableUpdateRollback.GetLatestBackupDirectory(
            [Path.GetDirectoryName(tamperedBackupDirectory)!]),
        Path.GetFullPath(tamperedBackupDirectory),
        StringComparison.OrdinalIgnoreCase),
    "Rollback discovery offered a backup that failed hash verification.");

string unownedInstallDirectory = Path.Combine(updateSafetySandbox, "unowned-entry-install");
string unownedPayloadDirectory = Path.Combine(updateSafetySandbox, "unowned-entry-payload");
Directory.CreateDirectory(unownedInstallDirectory);
Directory.CreateDirectory(unownedPayloadDirectory);
File.WriteAllText(Path.Combine(unownedInstallDirectory, "AIQuickPanel.exe"), "unowned-old");
File.WriteAllText(Path.Combine(unownedInstallDirectory, "managed-old.dll"), "managed-old");
WriteApplicationManifest(unownedInstallDirectory, "AIQuickPanel.exe", "managed-old.dll");
File.WriteAllText(Path.Combine(unownedInstallDirectory, "operator-note.txt"), "leave this file alone");
File.WriteAllText(Path.Combine(unownedPayloadDirectory, "AIQuickPanel.exe"), "unowned-new");
File.WriteAllText(Path.Combine(unownedPayloadDirectory, "managed-new.dll"), "managed-new");
WriteApplicationManifest(unownedPayloadDirectory, "AIQuickPanel.exe", "managed-new.dll");
ReleasePayloadPolicy.ReplaceApplicationFiles(
    unownedPayloadDirectory,
    unownedInstallDirectory,
    updateProfileDirectory);
Need(File.ReadAllText(Path.Combine(unownedInstallDirectory, "operator-note.txt")) == "leave this file alone" &&
     !File.Exists(Path.Combine(unownedInstallDirectory, "managed-old.dll")) &&
     File.Exists(Path.Combine(unownedInstallDirectory, "managed-new.dll")),
    "Manifest-based replacement removed an unrelated install entry or retained a stale managed entry.");

string forcedRollbackInstall = Path.Combine(updateSafetySandbox, "forced-rollback-install");
string forcedRollbackPayload = Path.Combine(updateSafetySandbox, "forced-rollback-payload");
string forcedRollbackRecovery = Path.Combine(updateSafetySandbox, "forced-rollback-recovery");
Directory.CreateDirectory(forcedRollbackInstall);
Directory.CreateDirectory(forcedRollbackPayload);
File.WriteAllText(Path.Combine(forcedRollbackInstall, "AIQuickPanel.exe"), "current-before-rollback");
File.WriteAllText(Path.Combine(forcedRollbackInstall, "current.dll"), "current");
WriteApplicationManifest(forcedRollbackInstall, "AIQuickPanel.exe", "current.dll");
File.WriteAllText(Path.Combine(forcedRollbackPayload, "AIQuickPanel.exe"), "older-rollback-target");
File.WriteAllText(Path.Combine(forcedRollbackPayload, "older.dll"), "older");
WriteApplicationManifest(forcedRollbackPayload, "AIQuickPanel.exe", "older.dll");
ReleasePayloadPolicy.CopyApplicationFiles(
    forcedRollbackInstall,
    forcedRollbackRecovery,
    updateProfileDirectory);
string forcedRollbackInstallHash = HashDirectory(forcedRollbackInstall);
string? recoveryRestartExecutable = null;
int forcedRollbackFailureExitCode = PortableUpdateWorker.Apply(
    new PortableUpdateRequest(
        "rollback",
        0,
        forcedRollbackPayload,
        forcedRollbackInstall,
        updateProfileDirectory,
        Path.Combine(forcedRollbackInstall, "AIQuickPanel.exe"),
        null,
        null,
        Path.Combine(updateSafetySandbox, "forced-rollback-failure.log"),
        RestartApplication: true,
        RecoveryDirectory: forcedRollbackRecovery),
    phase =>
    {
        if (phase == "after-replace")
        {
            throw new InvalidDataException("Forced rollback failure.");
        }
    },
    info =>
    {
        recoveryRestartExecutable = info.FileName;
        return null;
    });
Need(forcedRollbackFailureExitCode == 1 &&
     HashDirectory(forcedRollbackInstall) == forcedRollbackInstallHash &&
     Path.GetFullPath(recoveryRestartExecutable!).Equals(
         Path.GetFullPath(Path.Combine(forcedRollbackInstall, "AIQuickPanel.exe")),
         StringComparison.OrdinalIgnoreCase),
    "Failed rollback did not verify recovery, restore the current version, and request its restart.");

string partialInstall = Path.Combine(updateSafetySandbox, "partial-replacement-install");
string partialRecovery = Path.Combine(updateSafetySandbox, "partial-replacement-recovery");
string partialAttemptedPayload = Path.Combine(updateSafetySandbox, "partial-replacement-payload");
Directory.CreateDirectory(partialInstall);
Directory.CreateDirectory(partialRecovery);
Directory.CreateDirectory(partialAttemptedPayload);
File.WriteAllText(Path.Combine(partialRecovery, "AIQuickPanel.exe"), "partial-old");
File.WriteAllText(Path.Combine(partialRecovery, "old-managed.dll"), "partial-old-managed");
WriteApplicationManifest(partialRecovery, "AIQuickPanel.exe", "old-managed.dll");
File.WriteAllText(Path.Combine(partialAttemptedPayload, "AIQuickPanel.exe"), "partial-new");
File.WriteAllText(Path.Combine(partialAttemptedPayload, "new-managed.dll"), "partial-new-managed");
WriteApplicationManifest(partialAttemptedPayload, "AIQuickPanel.exe", "new-managed.dll");
File.WriteAllText(Path.Combine(partialInstall, "operator-owned.txt"), "preserve-partial");
File.WriteAllText(Path.Combine(partialInstall, "AIQuickPanel.exe"), "partially-copied-new");
File.WriteAllText(Path.Combine(partialInstall, "new-managed.dll"), "partially-copied-new-managed");
ReleasePayloadPolicy.RestoreApplicationFiles(
    partialRecovery,
    partialAttemptedPayload,
    partialInstall,
    updateProfileDirectory);
ReleasePayloadPolicy.VerifyApplicationFilesCopy(
    partialRecovery,
    partialInstall,
    updateProfileDirectory);
Need(File.ReadAllText(Path.Combine(partialInstall, "AIQuickPanel.exe")) == "partial-old" &&
     File.Exists(Path.Combine(partialInstall, "old-managed.dll")) &&
     !File.Exists(Path.Combine(partialInstall, "new-managed.dll")) &&
     File.ReadAllText(Path.Combine(partialInstall, "operator-owned.txt")) == "preserve-partial",
    "Recovery could not restore a manifest-less partial replacement without deleting an unrelated entry.");

string legacyRequestPath = Path.Combine(updateSafetySandbox, "legacy-request.json");
File.WriteAllText(
    legacyRequestPath,
    JsonSerializer.Serialize(new
    {
        Operation = "rollback",
        ProcessId = 0,
        PayloadDirectory = forcedRollbackPayload,
        InstallDirectory = forcedRollbackInstall,
        CanonicalDataDirectory = updateProfileDirectory,
        ApplicationExecutable = Path.Combine(forcedRollbackInstall, "AIQuickPanel.exe"),
        RollbackDirectory = (string?)null,
        DownloadedZip = (string?)null,
        LogPath = Path.Combine(updateSafetySandbox, "legacy-request.log"),
        RestartApplication = false
    }));
Need(PortableUpdateWorker.ReadRequest(legacyRequestPath).RecoveryDirectory is null,
    "Worker no longer accepts legacy update requests that predate rollback recovery metadata.");

string invalidLegacyPayload = Path.Combine(updateSafetySandbox, "invalid-legacy-payload");
Directory.CreateDirectory(invalidLegacyPayload);
File.WriteAllText(Path.Combine(invalidLegacyPayload, "AIQuickPanel.exe"), "missing-manifest");
string? legacyFailureRestart = null;
int legacyValidationFailure = PortableUpdateWorker.Apply(
    new PortableUpdateRequest(
        "update",
        0,
        invalidLegacyPayload,
        forcedRollbackInstall,
        updateProfileDirectory,
        Path.Combine(forcedRollbackInstall, "AIQuickPanel.exe"),
        Path.Combine(
            PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
            "legacy-validation-failure-backup"),
        null,
        Path.Combine(updateSafetySandbox, "legacy-validation-failure.log"),
        RestartApplication: true),
    faultInjection: null,
    info =>
    {
        legacyFailureRestart = info.FileName;
        return null;
    });
Need(legacyValidationFailure == 1 &&
     Path.GetFullPath(legacyFailureRestart!).Equals(
         Path.GetFullPath(Path.Combine(forcedRollbackInstall, "AIQuickPanel.exe")),
         StringComparison.OrdinalIgnoreCase),
    "Legacy parent validation failure did not request a safe restart of the unchanged installed version.");

string deadWorkerRequestPath = Path.Combine(updateSafetySandbox, "dead-worker-request.json");
PortableUpdateWorker.WriteRequest(
    deadWorkerRequestPath,
    PortableUpdateInstaller.CreateUpdateRequest(
        0,
        typedPayloadDirectory,
        updateInstallDirectory,
        updateProfileDirectory,
        Path.Combine(updateInstallDirectory, "AIQuickPanel.exe"),
        Path.Combine(PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory), "dead-worker-backup"),
        null,
        Path.Combine(updateSafetySandbox, "dead-worker.log"),
        restartApplication: false));
string immediatelyExitingWorker = Path.Combine(updateSafetySandbox, "fake-worker.exe");
File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), immediatelyExitingWorker);
bool rejectedDeadWorker = false;
try
{
    PortableUpdateInstaller.StartWorker(
        immediatelyExitingWorker,
        deadWorkerRequestPath,
        updateSafetySandbox);
    Thread.Sleep(500);
}
catch (InvalidOperationException)
{
    rejectedDeadWorker = true;
}
Need(rejectedDeadWorker,
    "Updater accepted Process.Start as a successful handoff even though the worker exited before acknowledging ownership.");

string acknowledgedRequestDirectory = Path.Combine(updateSafetySandbox, "acknowledged-worker");
Directory.CreateDirectory(acknowledgedRequestDirectory);
string acknowledgedRequestPath = Path.Combine(acknowledgedRequestDirectory, "update-request.json");
PortableUpdateRequest acknowledgedRequest = PortableUpdateInstaller.CreateUpdateRequest(
    0,
    typedPayloadDirectory,
    updateInstallDirectory,
    updateProfileDirectory,
    Path.Combine(updateInstallDirectory, "AIQuickPanel.exe"),
    Path.Combine(PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory), "acknowledged-worker-backup"),
    null,
    Path.Combine(acknowledgedRequestDirectory, "update.log"),
    restartApplication: false);
PortableUpdateWorker.WriteRequest(acknowledgedRequestPath, acknowledgedRequest);
string acknowledgedDurableLog = PortableUpdateDiagnostics
    .Create(acknowledgedRequestPath, acknowledgedRequest)
    .DurableLogPath;
int acknowledgedWorkerProcessId;
Environment.SetEnvironmentVariable("QUICKPANEL_TEST_HANDOFF_WORKER", "1");
try
{
    acknowledgedWorkerProcessId = PortableUpdateInstaller.StartWorker(
        testExecutable,
        acknowledgedRequestPath,
        Path.GetDirectoryName(testExecutable)!);
}
finally
{
    Environment.SetEnvironmentVariable("QUICKPANEL_TEST_HANDOFF_WORKER", null);
}
using (Process acknowledgedWorkerProcess = Process.GetProcessById(acknowledgedWorkerProcessId))
{
    Need(acknowledgedWorkerProcess.WaitForExit(5000),
        "Acknowledgement test worker did not exit after completing its handoff.");
}
Need(File.Exists(acknowledgedDurableLog) &&
     File.ReadAllText(acknowledgedDurableLog).Contains("worker-handoff-complete", StringComparison.Ordinal),
    "Updater did not persist a successful parent/worker acknowledgement in the durable log.");

string processWorkerDirectory = Path.Combine(updateSafetySandbox, "process-worker");
string processWorkerRequestPath = Path.Combine(processWorkerDirectory, "update-request.json");
string processWorkerInstall = Path.Combine(updateSafetySandbox, "process-worker-install");
string processWorkerPayload = Path.Combine(updateSafetySandbox, "process-worker-payload");
string processWorkerRollback = Path.Combine(
    PortableDataPaths.GetUpdateBackupDirectory(updateProfileDirectory),
    "process-worker-backup");
Directory.CreateDirectory(processWorkerDirectory);
Directory.CreateDirectory(processWorkerInstall);
Directory.CreateDirectory(processWorkerPayload);
File.WriteAllText(Path.Combine(processWorkerInstall, "AIQuickPanel.exe"), "process-worker-old");
File.WriteAllText(Path.Combine(processWorkerInstall, "old.dll"), "old");
WriteApplicationManifest(processWorkerInstall, "AIQuickPanel.exe", "old.dll");
File.WriteAllText(Path.Combine(processWorkerInstall, "operator-owned.txt"), "preserve");
File.WriteAllText(Path.Combine(processWorkerPayload, "AIQuickPanel.exe"), "process-worker-new");
File.WriteAllText(Path.Combine(processWorkerPayload, "new.dll"), "new");
WriteApplicationManifest(processWorkerPayload, "AIQuickPanel.exe", "new.dll");
string processWorkerProfileHash = HashDirectory(updateProfileDirectory);
var parentInfo = new ProcessStartInfo
{
    FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
    UseShellExecute = false,
    CreateNoWindow = true
};
parentInfo.ArgumentList.Add("-n");
parentInfo.ArgumentList.Add("4");
parentInfo.ArgumentList.Add("127.0.0.1");
using Process simulatedParent = Process.Start(parentInfo)
    ?? throw new InvalidOperationException("Could not start simulated updater parent process.");
PortableUpdateRequest processWorkerRequest = PortableUpdateInstaller.CreateUpdateRequest(
    simulatedParent.Id,
    processWorkerPayload,
    processWorkerInstall,
    updateProfileDirectory,
    Path.Combine(processWorkerInstall, "AIQuickPanel.exe"),
    processWorkerRollback,
    null,
    Path.Combine(processWorkerDirectory, "update.log"),
    restartApplication: false);
PortableUpdateWorker.WriteRequest(processWorkerRequestPath, processWorkerRequest);
string processWorkerDurableLog = PortableUpdateDiagnostics
    .Create(processWorkerRequestPath, processWorkerRequest)
    .DurableLogPath;
int processWorkerId;
Environment.SetEnvironmentVariable("QUICKPANEL_TEST_APPLY_WORKER", "1");
try
{
    processWorkerId = PortableUpdateInstaller.StartWorker(
        testExecutable,
        processWorkerRequestPath,
        Path.GetDirectoryName(testExecutable)!);
}
finally
{
    Environment.SetEnvironmentVariable("QUICKPANEL_TEST_APPLY_WORKER", null);
}
using Process processWorker = Process.GetProcessById(processWorkerId);
simulatedParent.Kill(entireProcessTree: true);
Need(simulatedParent.WaitForExit(5000), "Simulated updater parent did not exit.");
Need(processWorker.WaitForExit(10000),
    "Acknowledged updater worker did not survive the parent exit and complete independently.");
Need(File.ReadAllText(Path.Combine(processWorkerInstall, "AIQuickPanel.exe")) == "process-worker-new" &&
     File.Exists(Path.Combine(processWorkerInstall, "new.dll")) &&
     !File.Exists(Path.Combine(processWorkerInstall, "old.dll")) &&
     File.ReadAllText(Path.Combine(processWorkerInstall, "operator-owned.txt")) == "preserve" &&
     File.Exists(Path.Combine(processWorkerRollback, "AIQuickPanel.exe")) &&
     HashDirectory(updateProfileDirectory) == processWorkerProfileHash,
    "Independent updater process did not replace only application-owned files while preserving profile and unrelated entries.");
string processWorkerLogText = File.ReadAllText(processWorkerDurableLog);
Need(processWorkerLogText.Contains("stage=worker-handoff-complete", StringComparison.Ordinal) &&
     processWorkerLogText.Contains("stage=backup-verified", StringComparison.Ordinal) &&
     processWorkerLogText.Contains("stage=replacement-verified", StringComparison.Ordinal) &&
     processWorkerLogText.Contains("stage=success", StringComparison.Ordinal),
    "Independent updater process did not persist its handoff, verification, and completion stages.");

string malformedRequestDirectory = Path.Combine(updateSafetySandbox, "malformed-request");
string malformedRequestPath = Path.Combine(malformedRequestDirectory, "update-request.json");
Directory.CreateDirectory(malformedRequestDirectory);
File.WriteAllText(malformedRequestPath, "{");
Need(PortableUpdateWorker.ApplyRequestFile(malformedRequestPath) == 1,
    "Malformed update request did not return a controlled worker failure.");
string startupFailureLog = Path.Combine(malformedRequestDirectory, "update-startup.log");
Need(File.Exists(startupFailureLog) &&
     File.ReadAllText(startupFailureLog).Contains("request deserialization failed", StringComparison.OrdinalIgnoreCase),
    "Worker startup failure disappeared before any durable diagnostic was written.");

string sharedFolder = Path.Combine(updateSafetySandbox, "Downloads-like");
string sharedPayload = Path.Combine(updateSafetySandbox, "shared-payload");
Directory.CreateDirectory(sharedFolder);
Directory.CreateDirectory(sharedPayload);
File.WriteAllText(Path.Combine(sharedFolder, "AIQuickPanel.exe"), "app");
File.WriteAllText(Path.Combine(sharedFolder, "AIQuickPanel.dll"), "app");
File.WriteAllText(Path.Combine(sharedFolder, "AIQuickPanel.deps.json"), "{}");
File.WriteAllText(Path.Combine(sharedFolder, "AIQuickPanel.runtimeconfig.json"), "{}");
File.WriteAllText(Path.Combine(sharedFolder, "irreplaceable-photo.jpg"), "user-file");
File.WriteAllText(Path.Combine(sharedPayload, "AIQuickPanel.exe"), "next");
WriteApplicationManifest(sharedPayload, "AIQuickPanel.exe");
bool rejectedSharedFolder = false;
try
{
    ReleasePayloadPolicy.ReplaceApplicationFiles(sharedPayload, sharedFolder, updateProfileDirectory);
}
catch (InvalidDataException)
{
    rejectedSharedFolder = true;
}
Need(rejectedSharedFolder &&
    File.ReadAllText(Path.Combine(sharedFolder, "irreplaceable-photo.jpg")) == "user-file",
    "Updater accepted a shared folder and deleted an unrelated user file.");

if (OperatingSystem.IsWindows())
{
    string reparseTarget = Path.Combine(updateSafetySandbox, "reparse-target");
    string reparseAlias = Path.Combine(updateSafetySandbox, "reparse-alias");
    Directory.CreateDirectory(reparseTarget);
    bool reparseCreated = false;
    try
    {
        Directory.CreateSymbolicLink(reparseAlias, reparseTarget);
        reparseCreated = true;
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
    {
    }
    if (reparseCreated)
    {
        bool rejectedReparse = false;
        try
        {
            ReleasePayloadPolicy.EnsurePathContainsNoReparsePoints(Path.Combine(reparseAlias, "install"));
        }
        catch (InvalidOperationException)
        {
            rejectedReparse = true;
        }
        Need(rejectedReparse, "Updater accepted a path traversing a filesystem reparse point.");
    }
}
Directory.Delete(updateSafetySandbox, recursive: true);
Need(PortableDataPaths.IsUnderPortableDataDirectory(new SettingsService().UserSettingsPath),
    "Default settings path should live under the portable data directory.");
Need(new LogService().LogDirectory == PortableDataPaths.LogDirectory,
    "Default log path should live under the portable data directory.");

string rootLayoutSandbox = TempDirectory();
string rootLayoutProductDirectory = Path.Combine(rootLayoutSandbox, "LocalAppData", "AIQuickPanel");
string rootLayoutCanonical = Path.Combine(rootLayoutProductDirectory, "data");
WriteSyntheticProfile(rootLayoutProductDirectory, meaningfulSettings: true, browserState: true, marker: "root");
string rootLayoutSourceHash = HashDirectory(rootLayoutProductDirectory);
var migrationService = new ProfileMigrationService();
string renameSandbox = TempDirectory();
string oldCanonical = Path.Combine(renameSandbox, "LocalAppData", "AIQuickPanel", "data");
string renamedCanonical = Path.Combine(renameSandbox, "LocalAppData", "QuickPanel", "data");
WriteSyntheticProfile(oldCanonical, meaningfulSettings: true, browserState: true, marker: "renamed");
string oldProfileHash = HashDirectory(oldCanonical);
ProfileMigrationResult renameResult = migrationService.Migrate(new ProfileMigrationRequest(
    renamedCanonical,
    PortableDataPaths.GetMaintenanceDirectory(renamedCanonical),
    [new ProfileMigrationCandidate("legacy-canonical-data", oldCanonical, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(renameResult.Status == ProfileMigrationStatus.Migrated &&
    File.Exists(Path.Combine(renamedCanonical, "WebView2", "EBWebView", "Default", "Network", "Cookies")) &&
    HashDirectory(oldCanonical) == oldProfileHash,
    "Renamed canonical profile did not copy browser state while preserving the old profile.");
ProfileMigrationResult rootLayoutMigration = migrationService.Migrate(new ProfileMigrationRequest(
    rootLayoutCanonical,
    rootLayoutProductDirectory,
    [new ProfileMigrationCandidate("legacy-root", rootLayoutProductDirectory, IsProductRoot: true)],
    _ => ProfileAccessCheck.Safe));
Need(rootLayoutMigration.Status == ProfileMigrationStatus.Migrated &&
    File.Exists(Path.Combine(rootLayoutCanonical, "settings.json")) &&
    File.Exists(Path.Combine(rootLayoutCanonical, "WebView2", "EBWebView", "Default", "Network", "Cookies")),
    "Legacy root profile did not migrate to canonical data.");
Need(HashDirectory(rootLayoutProductDirectory) != rootLayoutSourceHash,
    "Root profile hash should change only because canonical data and the migration marker were added beside it.");
Need(File.Exists(Path.Combine(rootLayoutProductDirectory, "settings.json")) &&
    File.Exists(Path.Combine(rootLayoutProductDirectory, "WebView2", "EBWebView", "Default", "Network", "Cookies")),
    "Migration removed or moved the legacy root profile.");

string installLayoutSandbox = TempDirectory();
string installLayoutCanonical = Path.Combine(installLayoutSandbox, "LocalAppData", "AIQuickPanel", "data");
string installLayoutSource = Path.Combine(installLayoutSandbox, "LocalAppData", "Programs", "AIQuickPanel", "data");
WriteSyntheticProfile(installLayoutSource, meaningfulSettings: true, browserState: true, marker: "install");
string installLayoutSourceHash = HashDirectory(installLayoutSource);
ProfileMigrationRequest installLayoutRequest = new(
    installLayoutCanonical,
    Path.GetDirectoryName(installLayoutCanonical)!,
    [new ProfileMigrationCandidate("install-data", installLayoutSource, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe);
ProfileMigrationResult installLayoutMigration = migrationService.Migrate(installLayoutRequest);
Need(installLayoutMigration.Status == ProfileMigrationStatus.Migrated &&
    HashDirectory(installLayoutSource) == installLayoutSourceHash,
    "Install-folder migration did not copy the profile without modifying its source.");
string installedCanonicalCookie = Path.Combine(
    installLayoutCanonical,
    "WebView2",
    "EBWebView",
    "Default",
    "Network",
    "Cookies");
byte[] lockedCanonicalCookie = new byte[8192];
Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(lockedCanonicalCookie, 0);
File.WriteAllBytes(installedCanonicalCookie, lockedCanonicalCookie);
string installCanonicalHash = HashDirectory(installLayoutCanonical);
ProfileMigrationResult repeatedInstallMigration;
using (new FileStream(installedCanonicalCookie, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    repeatedInstallMigration = migrationService.Migrate(installLayoutRequest);
}
Need(repeatedInstallMigration.Status is ProfileMigrationStatus.AlreadyComplete or ProfileMigrationStatus.CanonicalPreserved &&
    repeatedInstallMigration.InspectionMode == ProfileMigrationInspectionMode.MarkerFastPath &&
    HashDirectory(installLayoutCanonical) == installCanonicalHash,
    "Running migration twice did not use the marker fast path or altered the successfully migrated canonical profile.");

string emptyMarkedSandbox = TempDirectory();
string emptyMarkedCanonical = Path.Combine(emptyMarkedSandbox, "AIQuickPanel", "data");
Directory.CreateDirectory(emptyMarkedCanonical);
File.WriteAllText(
    Path.Combine(emptyMarkedCanonical, ProfileMigrationService.MarkerFileName),
    """
    {
      "migrationVersion": 1,
      "completedAtUtc": "2026-09-14T00:00:00+00:00",
      "sourceKind": "synthetic",
      "verifiedFileCount": 1
    }
    """);
ProfileMigrationResult emptyMarkedResult = migrationService.Migrate(new ProfileMigrationRequest(
    emptyMarkedCanonical,
    Path.GetDirectoryName(emptyMarkedCanonical)!,
    [],
    _ => ProfileAccessCheck.Safe));
Need(emptyMarkedResult.Status == ProfileMigrationStatus.Failed &&
    emptyMarkedResult.InspectionMode == ProfileMigrationInspectionMode.MarkerFastPath,
    "A migration marker without any canonical profile state was trusted or triggered a recursive assessment.");

string incompleteMarkedSandbox = TempDirectory();
string incompleteMarkedCanonical = Path.Combine(incompleteMarkedSandbox, "AIQuickPanel", "data");
string incompleteMarkedWorkRoot = Path.Combine(incompleteMarkedSandbox, "AIQuickPanel", "data-maintenance");
string incompleteMarkedLegacy = Path.Combine(incompleteMarkedSandbox, "legacy", "data");
Directory.CreateDirectory(Path.Combine(incompleteMarkedCanonical, "WebView2"));
File.WriteAllText(
    Path.Combine(incompleteMarkedCanonical, ProfileMigrationService.MarkerFileName),
    """
    {
      "migrationVersion": 1,
      "completedAtUtc": "2026-09-14T00:00:00+00:00"
    }
    """);
WriteSyntheticProfile(incompleteMarkedLegacy, meaningfulSettings: true, browserState: true, marker: "incomplete-marker-legacy");
ProfileMigrationResult incompleteMarkedResult = migrationService.Migrate(new ProfileMigrationRequest(
    incompleteMarkedCanonical,
    incompleteMarkedWorkRoot,
    [new ProfileMigrationCandidate("incomplete-marker-legacy", incompleteMarkedLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(incompleteMarkedResult.Status == ProfileMigrationStatus.Migrated &&
    incompleteMarkedResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment,
    "A migrated-profile marker without source and verified-file semantics bypassed deep assessment.");

string establishedSandbox = TempDirectory();
string establishedCanonical = Path.Combine(establishedSandbox, "AIQuickPanel", "data");
string establishedLegacy = Path.Combine(establishedSandbox, "Programs", "AIQuickPanel", "data");
string establishedWorkRoot = Path.GetDirectoryName(establishedCanonical)!;
WriteSyntheticProfile(establishedCanonical, meaningfulSettings: true, browserState: true, marker: "canonical");
WriteSyntheticProfile(establishedLegacy, meaningfulSettings: true, browserState: true, marker: "legacy");
string establishedSettingsHash = FileSha256(Path.Combine(establishedCanonical, "settings.json"));
string establishedWebViewHash = HashDirectory(Path.Combine(establishedCanonical, "WebView2"));
ProfileMigrationResult establishedResult = migrationService.Migrate(new ProfileMigrationRequest(
    establishedCanonical,
    establishedWorkRoot,
    [new ProfileMigrationCandidate("install-data", establishedLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(establishedResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    File.Exists(Path.Combine(establishedWorkRoot, ProfileMigrationService.EstablishedMarkerFileName)) &&
    FileSha256(Path.Combine(establishedCanonical, "settings.json")) == establishedSettingsHash &&
    HashDirectory(Path.Combine(establishedCanonical, "WebView2")) == establishedWebViewHash,
    "An established canonical profile was not marked for fast startup or its user data changed.");
ProfileMigrationResult repeatedEstablishedResult = migrationService.Migrate(new ProfileMigrationRequest(
    establishedCanonical,
    establishedWorkRoot,
    [new ProfileMigrationCandidate("install-data", establishedLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(repeatedEstablishedResult.Status == ProfileMigrationStatus.AlreadyComplete &&
    repeatedEstablishedResult.InspectionMode == ProfileMigrationInspectionMode.MarkerFastPath,
    "A safely established canonical profile did not use the marker fast path on its next launch.");

string recreatedCanonicalSandbox = TempDirectory();
string recreatedCanonical = Path.Combine(recreatedCanonicalSandbox, "AIQuickPanel", "data");
string recreatedWorkRoot = Path.Combine(recreatedCanonicalSandbox, "AIQuickPanel", "data-maintenance");
string recreatedLegacy = Path.Combine(recreatedCanonicalSandbox, "legacy", "data");
WriteSyntheticProfile(recreatedCanonical, meaningfulSettings: true, browserState: true, marker: "original-canonical");
WriteSyntheticProfile(recreatedLegacy, meaningfulSettings: true, browserState: true, marker: "legacy-after-recreation");
ProfileMigrationResult originalCanonicalResult = migrationService.Migrate(new ProfileMigrationRequest(
    recreatedCanonical,
    recreatedWorkRoot,
    [],
    _ => ProfileAccessCheck.Safe));
Need(originalCanonicalResult.Status == ProfileMigrationStatus.CanonicalPreserved,
    "The original canonical profile was not assessed before the recreation test.");
Directory.Delete(recreatedCanonical, recursive: true);
WriteFreshDefaultSettings(recreatedCanonical);
ProfileMigrationResult recreatedCanonicalResult = migrationService.Migrate(new ProfileMigrationRequest(
    recreatedCanonical,
    recreatedWorkRoot,
    [new ProfileMigrationCandidate("legacy-after-recreation", recreatedLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(recreatedCanonicalResult.Status == ProfileMigrationStatus.Migrated &&
    recreatedCanonicalResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment,
    "A cache from a deleted and recreated canonical directory bypassed deep assessment.");

string mismatchedCacheSandbox = TempDirectory();
string mismatchedCacheCanonical = Path.Combine(mismatchedCacheSandbox, "AIQuickPanel", "data");
string mismatchedCacheWorkRoot = Path.Combine(mismatchedCacheSandbox, "AIQuickPanel", "data-maintenance");
WriteSyntheticProfile(mismatchedCacheCanonical, meaningfulSettings: true, browserState: true, marker: "cache-path-mismatch");
Directory.CreateDirectory(mismatchedCacheWorkRoot);
string establishedMarkerJson = File.ReadAllText(
    Path.Combine(establishedWorkRoot, ProfileMigrationService.EstablishedMarkerFileName));
File.WriteAllText(
    Path.Combine(mismatchedCacheWorkRoot, ProfileMigrationService.EstablishedMarkerFileName),
    establishedMarkerJson);
string mismatchedCacheHash = HashDirectory(mismatchedCacheCanonical);
ProfileMigrationResult mismatchedCacheResult = migrationService.Migrate(new ProfileMigrationRequest(
    mismatchedCacheCanonical,
    mismatchedCacheWorkRoot,
    [],
    _ => ProfileAccessCheck.Safe));
Need(mismatchedCacheResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    mismatchedCacheResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment &&
    HashDirectory(mismatchedCacheCanonical) == mismatchedCacheHash,
    "A cache bound to another canonical path was trusted or changed profile data.");

string semanticCacheSandbox = TempDirectory();
string semanticCacheCanonical = Path.Combine(semanticCacheSandbox, "AIQuickPanel", "data");
string semanticCacheWorkRoot = Path.Combine(semanticCacheSandbox, "AIQuickPanel", "data-maintenance");
WriteSyntheticProfile(semanticCacheCanonical, meaningfulSettings: true, browserState: true, marker: "semantic-cache");
Directory.CreateDirectory(semanticCacheWorkRoot);
File.WriteAllText(
    Path.Combine(semanticCacheWorkRoot, ProfileMigrationService.EstablishedMarkerFileName),
    establishedMarkerJson.Replace(
        "\"sourceKind\": \"canonical-established\"",
        "\"sourceKind\": \"untrusted-value\"",
        StringComparison.Ordinal));
string semanticCacheHash = HashDirectory(semanticCacheCanonical);
ProfileMigrationResult semanticCacheResult = migrationService.Migrate(new ProfileMigrationRequest(
    semanticCacheCanonical,
    semanticCacheWorkRoot,
    [],
    _ => ProfileAccessCheck.Safe));
Need(semanticCacheResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    semanticCacheResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment &&
    HashDirectory(semanticCacheCanonical) == semanticCacheHash,
    "A semantically invalid established-profile cache was trusted or changed profile data.");

string corruptEstablishedCacheSandbox = TempDirectory();
string corruptEstablishedCacheCanonical = Path.Combine(corruptEstablishedCacheSandbox, "AIQuickPanel", "data");
string corruptEstablishedCacheWorkRoot = Path.GetDirectoryName(corruptEstablishedCacheCanonical)!;
WriteSyntheticProfile(corruptEstablishedCacheCanonical, meaningfulSettings: true, browserState: true, marker: "cache-recovery");
File.WriteAllText(
    Path.Combine(corruptEstablishedCacheWorkRoot, ProfileMigrationService.EstablishedMarkerFileName),
    "not-json");
string corruptCacheSettingsHash = FileSha256(Path.Combine(corruptEstablishedCacheCanonical, "settings.json"));
string corruptCacheWebViewHash = HashDirectory(Path.Combine(corruptEstablishedCacheCanonical, "WebView2"));
ProfileMigrationResult corruptEstablishedCacheResult = migrationService.Migrate(new ProfileMigrationRequest(
    corruptEstablishedCacheCanonical,
    corruptEstablishedCacheWorkRoot,
    [],
    _ => ProfileAccessCheck.Safe));
Need(corruptEstablishedCacheResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    corruptEstablishedCacheResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment &&
    FileSha256(Path.Combine(corruptEstablishedCacheCanonical, "settings.json")) == corruptCacheSettingsHash &&
    HashDirectory(Path.Combine(corruptEstablishedCacheCanonical, "WebView2")) == corruptCacheWebViewHash,
    "A corrupt maintenance-only startup cache blocked or changed an established canonical profile.");

string freshSandbox = TempDirectory();
string freshCanonical = Path.Combine(freshSandbox, "AIQuickPanel", "data");
string richLegacy = Path.Combine(freshSandbox, "Programs", "AIQuickPanel", "data");
WriteSyntheticProfile(freshCanonical, meaningfulSettings: false, browserState: false, marker: "fresh");
WriteSyntheticProfile(richLegacy, meaningfulSettings: true, browserState: true, marker: "rich");
ProfileMigrationResult freshResult = migrationService.Migrate(new ProfileMigrationRequest(
    freshCanonical,
    Path.GetDirectoryName(freshCanonical)!,
    [new ProfileMigrationCandidate("install-data", richLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(freshResult.Status == ProfileMigrationStatus.Migrated &&
    !string.IsNullOrWhiteSpace(freshResult.BackupDirectory) &&
    File.Exists(Path.Combine(freshResult.BackupDirectory, "settings.json")) &&
    File.ReadAllText(Path.Combine(freshResult.BackupDirectory, "settings.json")) == "{}",
    "Fresh canonical profile was not backed up before richer legacy recovery.");

string freshDefaultsSandbox = TempDirectory();
string freshDefaultsCanonical = Path.Combine(freshDefaultsSandbox, "AIQuickPanel", "data");
string freshDefaultsLegacy = Path.Combine(freshDefaultsSandbox, "legacy", "data");
WriteFreshDefaultSettings(freshDefaultsCanonical);
WriteSyntheticProfile(freshDefaultsLegacy, meaningfulSettings: true, browserState: true, marker: "fresh-defaults-rich");
ProfileMigrationResult freshDefaultsResult = migrationService.Migrate(new ProfileMigrationRequest(
    freshDefaultsCanonical,
    Path.GetDirectoryName(freshDefaultsCanonical)!,
    [new ProfileMigrationCandidate("fresh-defaults-rich", freshDefaultsLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(freshDefaultsResult.Status == ProfileMigrationStatus.Migrated,
    "An exact fresh 2.4.0 settings skeleton blocked recovery of a richer legacy profile.");

string customizedScalarSandbox = TempDirectory();
string customizedScalarCanonical = Path.Combine(customizedScalarSandbox, "AIQuickPanel", "data");
string customizedScalarLegacy = Path.Combine(customizedScalarSandbox, "legacy", "data");
WriteFreshDefaultSettings(customizedScalarCanonical);
string customizedScalarSettingsPath = Path.Combine(customizedScalarCanonical, "settings.json");
File.WriteAllText(
    customizedScalarSettingsPath,
    File.ReadAllText(customizedScalarSettingsPath).Replace("\"PanelWidth\": 500.0", "\"PanelWidth\": 642.0", StringComparison.Ordinal));
WriteSyntheticProfile(customizedScalarLegacy, meaningfulSettings: true, browserState: true, marker: "customized-scalar");
string customizedScalarHash = HashDirectory(customizedScalarCanonical);
ProfileMigrationResult customizedScalarResult = migrationService.Migrate(new ProfileMigrationRequest(
    customizedScalarCanonical,
    Path.GetDirectoryName(customizedScalarCanonical)!,
    [new ProfileMigrationCandidate("customized-scalar", customizedScalarLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(customizedScalarResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    HashDirectory(customizedScalarCanonical) == customizedScalarHash,
    "A user-customized scalar setting was treated as disposable fresh settings.");

string auxiliarySandbox = TempDirectory();
string auxiliaryCanonical = Path.Combine(auxiliarySandbox, "AIQuickPanel", "data");
string auxiliaryLegacy = Path.Combine(auxiliarySandbox, "legacy", "data");
Directory.CreateDirectory(auxiliaryCanonical);
File.WriteAllText(Path.Combine(auxiliaryCanonical, "external-apps.user.json"), "[{\"Id\":\"user-tool\"}]");
WriteSyntheticProfile(auxiliaryLegacy, meaningfulSettings: true, browserState: true, marker: "legacy");
string auxiliaryHash = HashDirectory(auxiliaryCanonical);
ProfileMigrationResult auxiliaryResult = migrationService.Migrate(new ProfileMigrationRequest(
    auxiliaryCanonical,
    Path.GetDirectoryName(auxiliaryCanonical)!,
    [new ProfileMigrationCandidate("legacy", auxiliaryLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(auxiliaryResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    HashDirectory(auxiliaryCanonical) == auxiliaryHash,
    "Auxiliary-only user state was displaced by migration.");

string freshBrowserSandbox = TempDirectory();
string freshBrowserCanonical = Path.Combine(freshBrowserSandbox, "AIQuickPanel", "data");
string freshBrowserLegacy = Path.Combine(freshBrowserSandbox, "legacy", "data");
WriteSyntheticFreshBrowserSkeleton(freshBrowserCanonical);
WriteSyntheticProfile(freshBrowserLegacy, meaningfulSettings: true, browserState: true, marker: "rich-browser");
ProfileMigrationResult freshBrowserResult = migrationService.Migrate(new ProfileMigrationRequest(
    freshBrowserCanonical,
    Path.GetDirectoryName(freshBrowserCanonical)!,
    [new ProfileMigrationCandidate("rich-browser", freshBrowserLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(freshBrowserResult.Status == ProfileMigrationStatus.Migrated &&
    !string.IsNullOrWhiteSpace(freshBrowserResult.BackupDirectory) &&
    File.Exists(Path.Combine(freshBrowserResult.BackupDirectory, "WebView2", "EBWebView", "Default", "Network", "Cookies")),
    "A tiny fresh WebView2 skeleton blocked recovery from a clearly richer legacy profile.");

string establishedBrowserSandbox = TempDirectory();
string establishedBrowserCanonical = Path.Combine(establishedBrowserSandbox, "AIQuickPanel", "data");
string establishedBrowserLegacy = Path.Combine(establishedBrowserSandbox, "legacy", "data");
WriteSyntheticProfile(establishedBrowserCanonical, meaningfulSettings: false, browserState: true, marker: "signed-in-canonical");
WriteSyntheticProfile(establishedBrowserLegacy, meaningfulSettings: true, browserState: true, marker: "older-rich");
string establishedBrowserHash = HashDirectory(establishedBrowserCanonical);
ProfileMigrationResult establishedBrowserResult = migrationService.Migrate(new ProfileMigrationRequest(
    establishedBrowserCanonical,
    Path.GetDirectoryName(establishedBrowserCanonical)!,
    [new ProfileMigrationCandidate("older-rich", establishedBrowserLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(establishedBrowserResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    HashDirectory(establishedBrowserCanonical) == establishedBrowserHash,
    "An established canonical WebView2 login profile was replaced by legacy data.");

string smallCookieSandbox = TempDirectory();
string smallCookieCanonical = Path.Combine(smallCookieSandbox, "AIQuickPanel", "data");
string smallCookieLegacy = Path.Combine(smallCookieSandbox, "legacy", "data");
WriteSyntheticProfile(smallCookieCanonical, meaningfulSettings: false, browserState: false, marker: "small-cookie");
string smallCookiePath = Path.Combine(smallCookieCanonical, "WebView2", "EBWebView", "Default", "Network", "Cookies");
Directory.CreateDirectory(Path.GetDirectoryName(smallCookiePath)!);
byte[] sqliteCookie = new byte[8192];
Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(sqliteCookie, 0);
File.WriteAllBytes(smallCookiePath, sqliteCookie);
WriteSyntheticProfile(smallCookieLegacy, meaningfulSettings: true, browserState: true, marker: "legacy");
string smallCookieHash = HashDirectory(smallCookieCanonical);
ProfileMigrationResult smallCookieResult = migrationService.Migrate(new ProfileMigrationRequest(
    smallCookieCanonical,
    Path.GetDirectoryName(smallCookieCanonical)!,
    [new ProfileMigrationCandidate("legacy", smallCookieLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(smallCookieResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    HashDirectory(smallCookieCanonical) == smallCookieHash,
    "A small valid Cookies database was treated as disposable browser scaffolding.");

string smallLocalStorageSandbox = TempDirectory();
string smallLocalStorageCanonical = Path.Combine(smallLocalStorageSandbox, "AIQuickPanel", "data");
string smallLocalStorageLegacy = Path.Combine(smallLocalStorageSandbox, "legacy", "data");
WriteSyntheticProfile(smallLocalStorageCanonical, meaningfulSettings: false, browserState: false, marker: "small-storage");
string smallLocalStoragePath = Path.Combine(smallLocalStorageCanonical, "WebView2", "EBWebView", "Default", "Local Storage", "leveldb", "000003.log");
Directory.CreateDirectory(Path.GetDirectoryName(smallLocalStoragePath)!);
File.WriteAllBytes(smallLocalStoragePath, Encoding.UTF8.GetBytes("small but legitimate local storage state"));
WriteSyntheticProfile(smallLocalStorageLegacy, meaningfulSettings: true, browserState: true, marker: "legacy-storage");
string smallLocalStorageHash = HashDirectory(smallLocalStorageCanonical);
ProfileMigrationResult smallLocalStorageResult = migrationService.Migrate(new ProfileMigrationRequest(
    smallLocalStorageCanonical,
    Path.GetDirectoryName(smallLocalStorageCanonical)!,
    [new ProfileMigrationCandidate("legacy-storage", smallLocalStorageLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(smallLocalStorageResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    HashDirectory(smallLocalStorageCanonical) == smallLocalStorageHash,
    "A small Local Storage write-ahead log was treated as disposable browser scaffolding.");

string ambiguousSandbox = TempDirectory();
string ambiguousCanonical = Path.Combine(ambiguousSandbox, "AIQuickPanel", "data");
string ambiguousOne = Path.Combine(ambiguousSandbox, "one", "data");
string ambiguousTwo = Path.Combine(ambiguousSandbox, "two", "data");
WriteSyntheticProfile(ambiguousOne, meaningfulSettings: true, browserState: true, marker: "one");
WriteSyntheticProfile(ambiguousTwo, meaningfulSettings: true, browserState: true, marker: "two");
string ambiguousOneHash = HashDirectory(ambiguousOne);
string ambiguousTwoHash = HashDirectory(ambiguousTwo);
ProfileMigrationResult ambiguousResult = migrationService.Migrate(new ProfileMigrationRequest(
    ambiguousCanonical,
    Path.GetDirectoryName(ambiguousCanonical)!,
    [
        new ProfileMigrationCandidate("one", ambiguousOne, IsProductRoot: false),
        new ProfileMigrationCandidate("two", ambiguousTwo, IsProductRoot: false)
    ],
    _ => ProfileAccessCheck.Safe));
Need(ambiguousResult.Status == ProfileMigrationStatus.Ambiguous &&
    !Directory.Exists(ambiguousCanonical) &&
    HashDirectory(ambiguousOne) == ambiguousOneHash &&
    HashDirectory(ambiguousTwo) == ambiguousTwoHash,
    "Ambiguous migration did not fail without changing either profile.");

string inUseSandbox = TempDirectory();
string inUseCanonical = Path.Combine(inUseSandbox, "AIQuickPanel", "data");
string inUseLegacy = Path.Combine(inUseSandbox, "legacy", "data");
WriteSyntheticProfile(inUseLegacy, meaningfulSettings: true, browserState: true, marker: "in-use");
string inUseHash = HashDirectory(inUseLegacy);
ProfileMigrationResult inUseResult = migrationService.Migrate(new ProfileMigrationRequest(
    inUseCanonical,
    Path.GetDirectoryName(inUseCanonical)!,
    [new ProfileMigrationCandidate("in-use", inUseLegacy, IsProductRoot: false)],
    _ => new ProfileAccessCheck(false, "Synthetic profile is in use.")));
Need(inUseResult.Status == ProfileMigrationStatus.Unsafe &&
    !Directory.Exists(inUseCanonical) &&
    HashDirectory(inUseLegacy) == inUseHash,
    "In-use migration changed a source or destination profile.");

string raceSandbox = TempDirectory();
string raceCanonical = Path.Combine(raceSandbox, "AIQuickPanel", "data");
string raceLegacy = Path.Combine(raceSandbox, "legacy", "data");
WriteSyntheticProfile(raceLegacy, meaningfulSettings: true, browserState: true, marker: "race");
string raceSourceHash = HashDirectory(raceLegacy);
int accessChecks = 0;
ProfileMigrationResult raceResult = migrationService.Migrate(new ProfileMigrationRequest(
    raceCanonical,
    Path.GetDirectoryName(raceCanonical)!,
    [new ProfileMigrationCandidate("race", raceLegacy, IsProductRoot: false)],
    _ =>
    {
        accessChecks++;
        if (accessChecks == 2)
        {
            File.AppendAllText(Path.Combine(raceLegacy, "settings.json"), " ");
        }
        return ProfileAccessCheck.Safe;
    }));
Need(raceResult.Status == ProfileMigrationStatus.Failed && accessChecks == 2 &&
    !Directory.Exists(raceCanonical) && HashDirectory(raceLegacy) != raceSourceHash,
    "A source mutation between copy and activation was not detected.");

string corruptMarkerSandbox = TempDirectory();
string corruptMarkerCanonical = Path.Combine(corruptMarkerSandbox, "AIQuickPanel", "data");
string corruptMarkerLegacy = Path.Combine(corruptMarkerSandbox, "legacy", "data");
WriteSyntheticProfile(corruptMarkerCanonical, meaningfulSettings: true, browserState: true, marker: "canonical-with-corrupt-marker");
File.WriteAllText(Path.Combine(corruptMarkerCanonical, ProfileMigrationService.MarkerFileName), "not-json");
WriteSyntheticProfile(corruptMarkerLegacy, meaningfulSettings: true, browserState: true, marker: "marker");
string corruptMarkerHash = HashDirectory(corruptMarkerCanonical);
ProfileMigrationResult corruptMarkerResult = migrationService.Migrate(new ProfileMigrationRequest(
    corruptMarkerCanonical,
    Path.Combine(corruptMarkerSandbox, "AIQuickPanel", "data-maintenance"),
    [new ProfileMigrationCandidate("marker", corruptMarkerLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(corruptMarkerResult.Status == ProfileMigrationStatus.CanonicalPreserved &&
    corruptMarkerResult.InspectionMode == ProfileMigrationInspectionMode.DeepAssessment &&
    HashDirectory(corruptMarkerCanonical) == corruptMarkerHash,
    "A corrupt migration marker blocked deep assessment or changed an established profile.");

string rollbackSandbox = TempDirectory();
string rollbackCanonical = Path.Combine(rollbackSandbox, "AIQuickPanel", "data");
string rollbackLegacy = Path.Combine(rollbackSandbox, "legacy", "data");
Directory.CreateDirectory(rollbackCanonical);
File.WriteAllText(Path.Combine(rollbackCanonical, "settings.json"), "{}");
WriteSyntheticProfile(rollbackLegacy, meaningfulSettings: true, browserState: true, marker: "rollback");
string rollbackOriginalHash = HashDirectory(rollbackCanonical);
ProfileMigrationResult rollbackResult = migrationService.Migrate(new ProfileMigrationRequest(
    rollbackCanonical,
    Path.GetDirectoryName(rollbackCanonical)!,
    [new ProfileMigrationCandidate("rollback", rollbackLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe,
    phase => { if (phase == "before-marker-write") throw new InvalidDataException("Synthetic activation failure."); }));
Need(rollbackResult.Status == ProfileMigrationStatus.Failed &&
    Directory.Exists(rollbackCanonical) && HashDirectory(rollbackCanonical) == rollbackOriginalHash &&
    !string.IsNullOrWhiteSpace(rollbackResult.BackupDirectory) && Directory.Exists(rollbackResult.BackupDirectory),
    "Failed activation did not quarantine the failed copy and restore the prior canonical profile.");

string filteredSandbox = TempDirectory();
string filteredCanonical = Path.Combine(filteredSandbox, "AIQuickPanel", "data");
string filteredLegacy = Path.Combine(filteredSandbox, "legacy", "data");
WriteSyntheticProfile(filteredLegacy, meaningfulSettings: true, browserState: true, marker: "filtered");
File.WriteAllText(Path.Combine(filteredLegacy, "AIQuickPanel.exe"), "binary");
File.WriteAllText(Path.Combine(filteredLegacy, "stale.dll"), "binary");
File.WriteAllText(Path.Combine(filteredLegacy, "update.zip"), "archive");
ProfileMigrationResult filteredResult = migrationService.Migrate(new ProfileMigrationRequest(
    filteredCanonical,
    Path.GetDirectoryName(filteredCanonical)!,
    [new ProfileMigrationCandidate("filtered", filteredLegacy, IsProductRoot: false)],
    _ => ProfileAccessCheck.Safe));
Need(filteredResult.Status == ProfileMigrationStatus.Migrated &&
    !File.Exists(Path.Combine(filteredCanonical, "AIQuickPanel.exe")) &&
    !File.Exists(Path.Combine(filteredCanonical, "stale.dll")) &&
    !File.Exists(Path.Combine(filteredCanonical, "update.zip")),
    "Dedicated legacy migration copied application or update payload files into canonical user data.");

foreach (string sandbox in new[]
{
    rootLayoutSandbox,
    installLayoutSandbox,
    establishedSandbox,
    recreatedCanonicalSandbox,
    mismatchedCacheSandbox,
    semanticCacheSandbox,
    corruptEstablishedCacheSandbox,
    freshSandbox,
    freshDefaultsSandbox,
    auxiliarySandbox,
    freshBrowserSandbox,
    establishedBrowserSandbox,
    customizedScalarSandbox,
    smallCookieSandbox,
    smallLocalStorageSandbox,
    ambiguousSandbox,
    inUseSandbox,
    raceSandbox,
    corruptMarkerSandbox,
    emptyMarkedSandbox,
    incompleteMarkedSandbox,
    rollbackSandbox,
    filteredSandbox
})
{
    Directory.Delete(sandbox, recursive: true);
}
Need(settings.LoadStartWithWindowsPreference() == null,
    "Unset startup preference should remain distinguishable from an explicit choice.");
Need(!settings.LoadStartWithWindows(),
    "Startup should default to disabled until the user explicitly enables it.");
Need(!settings.LoadLaunchCodexAutomatically(),
    "Codex autostart should default to disabled.");
(double defaultPanelWidth, double defaultPanelHeight) = settings.LoadPanelSize(500, 650);
Need(defaultPanelWidth == 500 && defaultPanelHeight == 650,
    "Panel size should use the supplied defaults before the user resizes it.");
settings.SavePanelSize(875.4, 744.6);
(double savedPanelWidth, double savedPanelHeight) = new SettingsService(settingsPath).LoadPanelSize(500, 650);
Need(savedPanelWidth == 875.4 && savedPanelHeight == 744.6,
    "The last panel resize did not persist.");
settings.SavePanelSize(120, double.PositiveInfinity);
(double clampedPanelWidth, double clampedPanelHeight) = new SettingsService(settingsPath).LoadPanelSize(500, 650);
Need(clampedPanelWidth == SettingsService.MinimumPanelWidth &&
    clampedPanelHeight == SettingsService.MinimumPanelHeight,
    "Unsafe panel dimensions were not clamped to usable minimums.");
settings.SaveStartWithWindows(false);
Need(new SettingsService(settingsPath).LoadStartWithWindowsPreference() == false,
    "Explicit startup preference did not persist.");
settings.SaveLaunchCodexAutomatically(true);
Need(new SettingsService(settingsPath).LoadLaunchCodexAutomatically(),
    "Explicit Codex autostart preference did not persist.");
Need(!settings.LoadCheckForUpdatesOnStartup(),
    "Update checks on startup should default to disabled.");
settings.SaveCheckForUpdatesOnStartup(true);
Need(new SettingsService(settingsPath).LoadCheckForUpdatesOnStartup(),
    "Update checks on startup preference did not persist.");
var resetSettings = new CodexResetSettings
{
    WindowStartedAt = DateTimeOffset.UtcNow.AddHours(-1),
    WindowEndedAt = DateTimeOffset.UtcNow.AddHours(4),
    WeeklyResetAt = DateTimeOffset.UtcNow.AddDays(3),
    ResetExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    BankedResets = 2,
    TokensUsed = 1200,
    TokenLimit = 5000,
    Notes = "Use banked reset before expiry."
};
settings.SaveCodexResetSettings(resetSettings);
CodexResetSettings reloadedResetSettings = new SettingsService(settingsPath).LoadCodexResetSettings();
Need(reloadedResetSettings.BankedResets == 2 &&
    reloadedResetSettings.TokenLimit == 5000 &&
    reloadedResetSettings.Notes == "Use banked reset before expiry.",
    "Codex reset manager settings did not persist.");
var tripSettings = new TripModeSettings
{
    SelectedTripId = "trip-1",
    Trips = new List<TripPlan>
    {
        new TripPlan
        {
            Id = "trip-1",
            Name = "Weekend trip",
            PackingItems = new List<TripChecklistItem>
            {
                new TripChecklistItem { Text = "Tent", IsPacked = true },
                new TripChecklistItem { Text = "Maps", IsPacked = false }
            },
            SavedMapsLinks = "https://maps.google.com/?q=campsite",
            CampingLinks = "https://camp.example.com",
            ParkingSpots = "Lot A",
            FuelEstimate = "300 km",
            DailyBudget = "100 EUR",
            EmergencyDocs = "Insurance PDF",
            OfflineNotes = "Keep copies offline."
        }
    }
};
settings.SaveTripModeSettings(tripSettings);
TripModeSettings reloadedTripSettings = new SettingsService(settingsPath).LoadTripModeSettings();
Need(reloadedTripSettings.SelectedTripId == "trip-1" &&
    reloadedTripSettings.Trips.Count == 1 &&
    reloadedTripSettings.Trips[0].PackingItems.Count == 2 &&
    reloadedTripSettings.Trips[0].EmergencyDocs == "Insurance PDF",
    "Trip Mode settings did not persist.");
settings.SaveTabPinState("builtin:chatgpt", false);
settings.SaveTabPinState("native:codex", false);
var pinReload = new SettingsService(settingsPath);
Need(!pinReload.LoadTabs().Single(tab => SettingsService.GetTabId(tab) == "builtin:chatgpt").IsPinned,
    "Built-in tab pin state did not persist.");
Need(!pinReload.LoadTabPinned("native:codex", defaultValue: true),
    "Native tab pin state did not persist.");
settings.SaveCustomTabs(new[]
{
    CustomTab("Same", "https://dup.example.com", icon: "globe"),
    CustomTab("Same", "https://other.example.com", icon: "reddit"),
    CustomTab("Other", "https://dup.example.com", icon: "openai", pinned: true)
});

var reloadedSettings = new SettingsService(settingsPath);
List<AiTab> customTabs = reloadedSettings.LoadTabs().Where(tab => tab.IsCustom).ToList();
Need(customTabs.Count == 3, "Duplicate custom tabs did not survive settings reload.");
Need(customTabs.Count(tab => tab.Name == "Same") == 2, "Duplicate tab names were not allowed.");
Need(customTabs.Count(tab => tab.Url == "https://dup.example.com") == 2, "Duplicate tab URLs were not allowed.");
Need(customTabs.Select(SettingsService.GetTabId).Distinct(StringComparer.Ordinal).Count() == customTabs.Count,
    "Custom tabs do not have stable unique IDs.");
Need(customTabs.Single(tab => tab.Name == "Other").IsPinned, "Pinned custom tab did not persist.");
reloadedSettings.SaveAlwaysOnTop(false);
reloadedSettings.SaveUpdateManifestUrl("https://updates.example.com/version.json");
string resetBackupPath = reloadedSettings.BackupAndResetPanelPreferences();
var resetReload = new SettingsService(settingsPath);
Need(File.Exists(resetBackupPath),
    "Config reset did not create a settings backup.");
Need(resetReload.LoadTabs().Where(tab => tab.IsCustom).Count() == 3,
    "Config reset should preserve custom tabs.");
Need(resetReload.LoadTripModeSettings().Trips.Count == 1 &&
    resetReload.LoadCodexResetSettings().BankedResets == 2,
    "Config reset should preserve Codex usage and trip data.");
Need(!resetReload.LoadTabs().Single(tab => SettingsService.GetTabId(tab) == "builtin:chatgpt").IsPinned &&
    !resetReload.LoadTabPinned("native:codex", defaultValue: true),
    "Config reset should preserve tab pin states.");
Need(resetReload.LoadAlwaysOnTop() &&
    string.IsNullOrWhiteSpace(resetReload.LoadUpdateManifestUrl()) &&
    !resetReload.LoadCheckForUpdatesOnStartup(),
    "Config reset did not reset panel preferences.");

string legacyQuickToolsPath = TempSettingsPath();
Directory.CreateDirectory(Path.GetDirectoryName(legacyQuickToolsPath)!);
File.WriteAllText(legacyQuickToolsPath, """
{
  "LastSelectedTabId": "native:quick-tools",
  "TabOrder": [ "native:quick-tools" ],
  "TabPinStates": {
    "native:quick-tools": false
  }
}
""");
var legacyQuickToolsSettings = new SettingsService(legacyQuickToolsPath);
Need(legacyQuickToolsSettings.LoadLastSelectedTabId() == "native:codex-usage",
    "Legacy Quick Tools selected tab did not migrate to Codex Usage.");
Need(legacyQuickToolsSettings.LoadTabOrder().Take(3).SequenceEqual(new[] { "native:codex-usage", "native:windows-helper", "native:trip-planner" }),
    "Legacy Quick Tools order did not expand to separate built-in panels.");
Need(!legacyQuickToolsSettings.LoadTabPinned("native:codex-usage", defaultValue: true) &&
    !legacyQuickToolsSettings.LoadTabPinned("native:windows-helper", defaultValue: true) &&
    !legacyQuickToolsSettings.LoadTabPinned("native:trip-planner", defaultValue: true),
    "Legacy Quick Tools pin state did not migrate to separate built-in panels.");

string sameDuplicatePath = TempSettingsPath();
const string sameIdA = "custom:claude-a";
const string sameIdB = "custom:claude-b";
var sameDuplicateSettings = new SettingsService(sameDuplicatePath);
sameDuplicateSettings.SaveCustomTabs(new[]
{
    CustomTab("Claude", "https://claude.ai/", icon: "anthropic", id: sameIdA, pinned: true),
    CustomTab("Claude", "https://claude.ai/", icon: "anthropic", id: sameIdB)
});
sameDuplicateSettings.SaveTabOrder(new[] { sameIdB, sameIdA });
sameDuplicateSettings.SaveLastSelectedTabId(sameIdB);
sameDuplicateSettings.SaveTabZoomFactor(sameIdA, 0.9);
var sameDuplicateReload = new SettingsService(sameDuplicatePath);
List<AiTab> sameDuplicateTabs = sameDuplicateReload.LoadTabs()
    .Where(tab => tab.IsCustom && tab.Url == "https://claude.ai/")
    .ToList();
Need(sameDuplicateTabs.Count == 2, "Same name and same URL duplicate tabs collapsed.");
Need(sameDuplicateTabs.Select(SettingsService.GetTabId).OrderBy(id => id).SequenceEqual(new[] { sameIdA, sameIdB }),
    "Same duplicate IDs did not survive reload.");
Need(sameDuplicateReload.LoadLastSelectedTabId() == sameIdB,
    "Selected same duplicate tab did not survive restart.");
Need(sameDuplicateReload.LoadTabOrder().Take(2).SequenceEqual(new[] { sameIdB, sameIdA }),
    "Same duplicate tab order collapsed after restart.");
Need(sameDuplicateReload.LoadTabZoomFactors().TryGetValue(sameIdA, out double sameZoom) && Math.Abs(sameZoom - 0.9) < 0.001,
    "Zoom did not stay with correct same duplicate ID.");
sameDuplicateReload.SaveCustomTabs(new[]
{
    CustomTab("Claude Work", "https://claude.ai/", icon: "anthropic", id: sameIdA, pinned: true),
    CustomTab("Claude", "https://claude.ai/new", icon: "anthropic", id: sameIdB)
});
List<AiTab> editedSameDuplicates = new SettingsService(sameDuplicatePath).LoadTabs()
    .Where(tab => tab.IsCustom && (SettingsService.GetTabId(tab) == sameIdA || SettingsService.GetTabId(tab) == sameIdB))
    .ToList();
Need(editedSameDuplicates.Single(tab => SettingsService.GetTabId(tab) == sameIdA).Name == "Claude Work",
    "Renaming one duplicate changed the wrong tab.");
Need(editedSameDuplicates.Single(tab => SettingsService.GetTabId(tab) == sameIdB).Url == "https://claude.ai/new",
    "Editing one duplicate URL changed the wrong tab.");
Need(editedSameDuplicates.Single(tab => SettingsService.GetTabId(tab) == sameIdA).IsPinned,
    "Pinned duplicate lost pinned state after edit.");

string firstId = SettingsService.GetTabId(customTabs[0]);
string secondId = SettingsService.GetTabId(customTabs[1]);
settings = new SettingsService(settingsPath);
settings.SaveLastSelectedTabId(secondId);
settings.SaveTabOrder(new[] { secondId, firstId });
settings.SaveTabZoomFactor(firstId, 1.25);
settings.SaveTabZoomFactor(secondId, 0.85);

reloadedSettings = new SettingsService(settingsPath);
Need(reloadedSettings.LoadLastSelectedTabId() == secondId, "Selected tab did not persist by stable ID.");
IReadOnlyList<string> tabOrder = reloadedSettings.LoadTabOrder();
Need(tabOrder.Count >= 2 && tabOrder[0] == secondId && tabOrder[1] == firstId,
    "Tab order did not persist by stable ID.");
IReadOnlyDictionary<string, double> zoomFactors = reloadedSettings.LoadTabZoomFactors();
Need(zoomFactors.TryGetValue(firstId, out double firstZoom) && Math.Abs(firstZoom - 1.25) < 0.001,
    "First duplicate zoom did not persist by stable ID.");
Need(zoomFactors.TryGetValue(secondId, out double secondZoom) && Math.Abs(secondZoom - 0.85) < 0.001,
    "Second duplicate zoom did not persist by stable ID.");
reloadedSettings.SaveTabZoomFactor(firstId, 1.0);
zoomFactors = new SettingsService(settingsPath).LoadTabZoomFactors();
Need(!zoomFactors.ContainsKey(firstId), "Reset zoom should remove the stored zoom factor.");
Need(zoomFactors.ContainsKey(secondId), "Resetting one duplicate zoom should not touch another duplicate.");

List<ClosedTabEntry> closedEntries = Enumerable.Range(0, 12)
    .Select(index => new ClosedTabEntry(
        CustomTab("Closed " + index, "https://closed" + index + ".example.com"),
        index,
        DateTimeOffset.UtcNow.AddMinutes(index)))
    .ToList();
settings = new SettingsService(settingsPath);
settings.SaveClosedTabs(closedEntries);
IReadOnlyList<ClosedTabEntry> persistedClosed = new SettingsService(settingsPath).LoadClosedTabs();
Need(persistedClosed.Count == 10, "Closed-tab history should persist only the last 10 custom tabs.");
Need(persistedClosed[0].Tab.Name == "Closed 2", "Closed-tab history did not keep the newest 10 entries.");
Need(persistedClosed.All(entry => entry.Tab.IsCustom), "Closed-tab history should persist only custom tabs.");
Need(persistedClosed.Select(entry => SettingsService.GetTabId(entry.Tab)).Distinct(StringComparer.Ordinal).Count() == persistedClosed.Count,
    "Closed duplicate tabs need unique IDs.");

var usedIds = new HashSet<string>(StringComparer.Ordinal) { secondId };
AiTab restoredConflict = SettingsService.EnsureUniqueCustomTabId(
    CustomTab("Same", "https://dup.example.com", id: secondId),
    usedIds);
Need(SettingsService.GetTabId(restoredConflict) != secondId,
    "Restored closed duplicate should receive a new ID when its old ID is already open.");

string closedDuplicatePath = TempSettingsPath();
var closedDuplicateSettings = new SettingsService(closedDuplicatePath);
closedDuplicateSettings.SaveClosedTabs(new[]
{
    new ClosedTabEntry(CustomTab("Claude", "https://claude.ai/", icon: "anthropic", id: "custom:closed-claude-a"), 1, DateTimeOffset.UtcNow),
    new ClosedTabEntry(CustomTab("Claude", "https://claude.ai/", icon: "anthropic", id: "custom:closed-claude-b"), 2, DateTimeOffset.UtcNow.AddSeconds(1))
});
IReadOnlyList<ClosedTabEntry> closedDuplicateEntries = new SettingsService(closedDuplicatePath).LoadClosedTabs();
Need(closedDuplicateEntries.Count == 2,
    "Closed same-name same-URL duplicates did not persist.");
Need(closedDuplicateEntries.Select(entry => SettingsService.GetTabId(entry.Tab)).SequenceEqual(new[] { "custom:closed-claude-a", "custom:closed-claude-b" }),
    "Closed duplicate IDs changed on reload.");
usedIds = new HashSet<string>(StringComparer.Ordinal) { "custom:closed-claude-b" };
AiTab reopenedClosedDuplicate = SettingsService.EnsureUniqueCustomTabId(closedDuplicateEntries[1].Tab, usedIds);
Need(SettingsService.GetTabId(reopenedClosedDuplicate) != "custom:closed-claude-b",
    "Reopening closed duplicate conflicted with existing tab ID.");

string oldSettingsPath = TempSettingsPath();
Directory.CreateDirectory(Path.GetDirectoryName(oldSettingsPath)!);
File.WriteAllText(oldSettingsPath, """
{
  "CustomTabs": [
    { "Name": "Legacy", "Url": "https://legacy.example.com", "Icon": "globe" },
    { "Name": "Legacy", "Url": "https://legacy.example.com", "Icon": "reddit" }
  ],
  "LastSelectedTabId": "web:https://legacy.example.com",
  "TabOrder": [ "web:https://legacy.example.com" ],
  "TabZoomFactors": {
    "web:https://legacy.example.com": 1.35
  }
}
""");
var migratedSettings = new SettingsService(oldSettingsPath);
List<AiTab> migratedCustomTabs = migratedSettings.LoadTabs().Where(tab => tab.IsCustom).ToList();
Need(migratedCustomTabs.Count == 2, "Legacy duplicate custom tabs without IDs were lost.");
Need(migratedCustomTabs.Select(SettingsService.GetTabId).Distinct(StringComparer.Ordinal).Count() == 2,
    "Legacy custom tabs did not get unique IDs.");
Need(migratedSettings.LoadLastSelectedTabId() == SettingsService.GetTabId(migratedCustomTabs[0]),
    "Legacy selected tab did not migrate to a stable ID.");
Need(migratedSettings.LoadTabOrder().Take(2).SequenceEqual(migratedCustomTabs.Select(SettingsService.GetTabId)),
    "Legacy tab order did not expand duplicate URL keys to stable IDs.");
zoomFactors = migratedSettings.LoadTabZoomFactors();
Need(migratedCustomTabs.All(tab =>
        zoomFactors.TryGetValue(SettingsService.GetTabId(tab), out double zoom) && Math.Abs(zoom - 1.35) < 0.001),
    "Legacy URL-keyed zoom did not migrate to duplicate stable IDs.");
Need(File.ReadAllText(oldSettingsPath).Contains("\"Id\"", StringComparison.Ordinal),
    "Legacy settings file was not rewritten with tab IDs.");

string manifestSettingsPath = TempSettingsPath();
var manifestSettings = new SettingsService(manifestSettingsPath);
manifestSettings.SaveUpdateManifestUrl(" https://example.com/AIQuickPanel/version.json ");
Need(new SettingsService(manifestSettingsPath).LoadUpdateManifestUrl() == "https://example.com/AIQuickPanel/version.json",
    "Update manifest URL did not persist.");
manifestSettings = new SettingsService(manifestSettingsPath);
manifestSettings.SaveUpdateManifestUrl("ftp://example.com/version.json");
Need(string.IsNullOrEmpty(new SettingsService(manifestSettingsPath).LoadUpdateManifestUrl()),
    "Invalid update manifest URL should be dropped.");
Need(Directory.GetFiles(Path.GetDirectoryName(manifestSettingsPath)!, "*.tmp").Length == 0,
    "Atomic settings save left a temp file behind.");

string corruptSettingsPath = TempSettingsPath();
Directory.CreateDirectory(Path.GetDirectoryName(corruptSettingsPath)!);
File.WriteAllText(corruptSettingsPath, "{ nope");
var corruptSettings = new SettingsService(corruptSettingsPath);
Need(corruptSettings.LoadTabs().Count > 0,
    "Corrupt settings should fall back to defaults.");
string[] corruptBackups = Directory.GetFiles(Path.GetDirectoryName(corruptSettingsPath)!, "settings.corrupt-*.json");
Need(corruptBackups.Length == 1,
    "Corrupt settings backup was not created.");
Need(File.ReadAllText(corruptBackups[0]).Contains("nope", StringComparison.Ordinal),
    "Corrupt backup did not preserve original bad file.");
Need(File.ReadAllText(corruptSettingsPath).Contains("CustomTabs", StringComparison.Ordinal),
    "Corrupt settings file was not recreated.");

string importSourcePath = TempSettingsPath();
var importSource = new SettingsService(importSourcePath);
importSource.SaveCustomTabs(new[] { CustomTab("Import", "https://import.example.com", id: "custom:import") });
importSource.SaveClosedTabs(new[] { new ClosedTabEntry(CustomTab("Closed Import", "https://closed-import.example.com", id: "custom:closed-import"), 0, DateTimeOffset.UtcNow) });
importSource.SaveUpdateManifestUrl("https://updates.example.com/version.json");
string importTargetPath = TempSettingsPath();
var importTarget = new SettingsService(importTargetPath);
importTarget.SaveCustomTabs(new[] { CustomTab("Old", "https://old.example.com", id: "custom:old") });
Need(importTarget.TryValidateSettingsFile(importSourcePath, out string? importError) && importError == null,
    "Valid settings backup did not validate.");
SettingsBackupSummary importSummary = importTarget.GetSettingsBackupSummary(importSourcePath);
Need(importSummary.CustomTabCount == 1 &&
    importSummary.ClosedTabCount == 1 &&
    importSummary.HasUpdateManifestUrl,
    "Import summary did not describe custom tabs, closed tabs, and update URL.");
importTarget.ImportSettingsBackup(importSourcePath);
Need(new SettingsService(importTargetPath).LoadTabs().Any(tab => tab.IsCustom && tab.Name == "Import"),
    "Imported settings backup did not replace custom tabs.");
Need(Directory.GetFiles(Path.GetDirectoryName(importTargetPath)!, "settings.pre-import-*.json").Length == 1,
    "Import did not back up existing settings.");
Need(!string.IsNullOrWhiteSpace(new SettingsService(importTargetPath).FindLatestPreImportBackup()),
    "Latest pre-import backup was not found.");
string badImportPath = TempSettingsPath();
Directory.CreateDirectory(Path.GetDirectoryName(badImportPath)!);
File.WriteAllText(badImportPath, "{ bad");
Need(!importTarget.TryValidateSettingsFile(badImportPath, out importError) && !string.IsNullOrWhiteSpace(importError),
    "Invalid settings backup should not validate.");

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string taskManagerViewPath = Path.Combine(repoRoot, "QuickPanel", "Views", "TaskManagerTabView.xaml");
string taskManagerViewCodePath = Path.Combine(repoRoot, "QuickPanel", "Views", "TaskManagerTabView.cs");
string dockWindowPickerCodePath = Path.Combine(repoRoot, "QuickPanel", "Views", "DockWindowPickerWindow.cs");
string endTaskConfirmationPath = Path.Combine(repoRoot, "QuickPanel.Views.EndTaskConfirmationWindow.xaml");
string appStartupPath = Path.Combine(repoRoot, "QuickPanel", "App.cs");
string webViewEnvironmentPath = Path.Combine(repoRoot, "QuickPanel", "Services", "WebViewEnvironmentService.cs");
string migrationRecoveryMarkupPath = Path.Combine(repoRoot, "QuickPanel.Views.ProfileMigrationRecoveryWindow.xaml");
string taskManagerViewMarkup = File.ReadAllText(taskManagerViewPath);
string taskManagerViewCode = File.ReadAllText(taskManagerViewCodePath);
Need(File.Exists(endTaskConfirmationPath) &&
	taskManagerViewCode.Contains("EndTaskConfirmationWindow", StringComparison.Ordinal) &&
	!taskManagerViewCode.Contains("MessageBox.Show", StringComparison.Ordinal) &&
	File.ReadAllText(endTaskConfirmationPath).Contains("IsDefault=\"True\"", StringComparison.Ordinal) &&
	File.ReadAllText(endTaskConfirmationPath).Contains("IsCancel=\"True\"", StringComparison.Ordinal),
	"End task should use the modern Quick Panel confirmation with a safe default and cancel path.");
Need(taskManagerViewMarkup.Contains("x:Key=\"TaskManagerDataGridCellStyle\"", StringComparison.Ordinal) &&
	taskManagerViewMarkup.Contains("CellStyle=\"{StaticResource TaskManagerDataGridCellStyle}\"", StringComparison.Ordinal) &&
	taskManagerViewMarkup.Contains("<Trigger Property=\"IsSelected\" Value=\"True\">", StringComparison.Ordinal),
	"Selected process cells should keep the Quick Panel dark accent treatment.");
string dockWindowPickerCode = File.ReadAllText(dockWindowPickerCodePath);
int pickerClosedHandler = dockWindowPickerCode.IndexOf("private void Window_Closed", StringComparison.Ordinal);
string pickerClosedCode = pickerClosedHandler >= 0 ? dockWindowPickerCode[pickerClosedHandler..] : string.Empty;
Need(pickerClosedCode.Contains("_lifetimeCancellation.Cancel()", StringComparison.Ordinal) &&
    !pickerClosedCode.Contains("_lifetimeCancellation.Dispose()", StringComparison.Ordinal),
    "Closing the app picker must cancel work without disposing the token source while async handlers still use it.");
string appStartupCode = File.ReadAllText(appStartupPath);
string mainWindowCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "MainWindow.cs"));
string mainWindowMarkupCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel.MainWindow.xaml"));
string externalAppNativeMethodsCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "ExternalAppNativeMethods.cs"));
string externalAppDockServiceCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "ExternalAppDockService.cs"));
string externalAppDefinitionFactoryCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "ExternalAppDefinitionFactory.cs"));
int migrationCallIndex = appStartupCode.IndexOf("RunProfileMigration();", StringComparison.Ordinal);
int mainWindowCreationIndex = appStartupCode.IndexOf("new MainWindow", StringComparison.Ordinal);
Need(migrationCallIndex >= 0 && mainWindowCreationIndex > migrationCallIndex &&
    appStartupCode.Contains("new ProfileMigrationService().Migrate", StringComparison.Ordinal) &&
    appStartupCode.Contains("ProfileMigrationRecoveryWindow", StringComparison.Ordinal),
    "Profile migration must gate MainWindow and WebView2 startup with a recovery path.");
Need(mainWindowCode.Contains("ShellExplorerLocationService.IsFileExplorerWindow", StringComparison.Ordinal) &&
    mainWindowCode.Contains("OpenShellExplorerTab", StringComparison.Ordinal) &&
    File.Exists(Path.Combine(repoRoot, "QuickPanel", "Views", "ShellExplorerTabView.xaml")),
    "File Explorer must use its safe in-panel Shell view instead of child-hosting CabinetWClass.");
Need(mainWindowCode.Contains("RestorePanelResizeSelection", StringComparison.Ordinal) &&
    mainWindowMarkupCode.Contains("DragStarted=\"PanelResizeThumb_DragStarted\"", StringComparison.Ordinal) &&
    mainWindowMarkupCode.Contains("DragCompleted=\"PanelResizeThumb_DragCompleted\"", StringComparison.Ordinal),
    "Panel resizing must retain the selected external-app tab while the overlay follows the host bounds.");
int windowClosingStart = mainWindowCode.IndexOf("private void Window_Closing", StringComparison.Ordinal);
int windowClosedStart = mainWindowCode.IndexOf("private void Window_Closed", windowClosingStart, StringComparison.Ordinal);
string windowClosingCode = mainWindowCode[windowClosingStart..windowClosedStart];
Need(mainWindowMarkupCode.Contains("Closing=\"Window_Closing\"", StringComparison.Ordinal) &&
    windowClosingCode.Contains("DisposeExternalAppViews();", StringComparison.Ordinal) &&
    mainWindowCode.Contains("private void DisposeExternalAppViews()", StringComparison.Ordinal),
    "External application windows must be restored during Closing, before the Quick Panel owner HWND is destroyed.");
int createHostWindowStart = externalAppNativeMethodsCode.IndexOf("internal static nint CreateHostWindow", StringComparison.Ordinal);
int createHostWindowEnd = externalAppNativeMethodsCode.IndexOf("internal static void DestroyHostWindow", createHostWindowStart, StringComparison.Ordinal);
string createHostWindowCode = externalAppNativeMethodsCode[createHostWindowStart..createHostWindowEnd];
Need(createHostWindowCode.Contains("SsBlackRect", StringComparison.Ordinal) &&
    createHostWindowCode.Contains("WsClipChildren", StringComparison.Ordinal),
    "The external HWND host must paint an opaque backing surface without repainting over its docked child.");
Need(externalAppDockServiceCode.Contains("Refresh(nudgeCompositor: _definition.PreferredDockingBehavior == ExternalAppDockingBehavior.Reparent)", StringComparison.Ordinal) &&
    externalAppDockServiceCode.Contains("ResizeDockedWindow(nudgeCompositor);", StringComparison.Ordinal),
    "Showing a reparented app host must nudge the child compositor so the app paints without a manual panel resize.");
Need(externalAppDockServiceCode.Contains("SuspendReparentedWindow();", StringComparison.Ordinal) &&
    externalAppDockServiceCode.Contains("ResumeReparentedWindow();", StringComparison.Ordinal) &&
    externalAppDockServiceCode.Contains("host-visibility minimized-top-level", StringComparison.Ordinal) &&
    externalAppDockServiceCode.Contains("ExternalAppNativeMethods.SwMinimize", StringComparison.Ordinal) &&
    !externalAppNativeMethodsCode.Contains("CreateParkingWindow", StringComparison.Ordinal),
    "Inactive reparented apps must temporarily regain their top-level style and minimize instead of staying under a hidden host.");
Need(externalAppDockServiceCode.Contains("UsesMinimizedOverlayVisibility", StringComparison.Ordinal) &&
    externalAppDockServiceCode.Contains("host-visibility minimized-overlay", StringComparison.Ordinal) &&
    externalAppDockServiceCode.Contains("ExternalAppNativeMethods.SwShowNoActivate", StringComparison.Ordinal) &&
    externalAppDockServiceCode.Contains("ExternalAppNativeMethods.SetWindowOwner", StringComparison.Ordinal) &&
    externalAppDefinitionFactoryCode.Contains("WindowsNotepad", StringComparison.Ordinal) &&
    externalAppDefinitionFactoryCode.Contains("ExternalAppDockingBehavior.Overlay", StringComparison.Ordinal),
    "Modern Notepad must use an in-panel overlay that minimizes, rather than hides or reparents, its top-level HWND while inactive.");
int resumeWindowStart = externalAppDockServiceCode.IndexOf("private void ResumeReparentedWindow", StringComparison.Ordinal);
int resumeWindowEnd = externalAppDockServiceCode.IndexOf("public bool FocusDockedWindow", resumeWindowStart, StringComparison.Ordinal);
string resumeWindowCode = externalAppDockServiceCode[resumeWindowStart..resumeWindowEnd];
int resumeRestoreCall = resumeWindowCode.IndexOf("ExternalAppNativeMethods.SwRestore", StringComparison.Ordinal);
int resumeChildStyle = resumeWindowCode.IndexOf("ApplyReparentedWindowStyle();", StringComparison.Ordinal);
int resumeSetParent = resumeWindowCode.IndexOf("SetWindowParent", StringComparison.Ordinal);
Need(resumeRestoreCall >= 0 && resumeChildStyle > resumeRestoreCall && resumeSetParent > resumeChildStyle,
    "A minimized reparented app must restore as a top-level window before child styling and reparenting are applied.");
int restoreWindowStart = externalAppDockServiceCode.IndexOf("private void TryRestoreWindow", StringComparison.Ordinal);
int restoreWindowEnd = externalAppDockServiceCode.IndexOf("private void AttachInputThreads", restoreWindowStart, StringComparison.Ordinal);
string restoreWindowCode = externalAppDockServiceCode[restoreWindowStart..restoreWindowEnd];
int overlayRestoreCheck = restoreWindowCode.IndexOf("PreferredDockingBehavior == ExternalAppDockingBehavior.Overlay", StringComparison.Ordinal);
int restoreHideCall = restoreWindowCode.IndexOf("ExternalAppNativeMethods.Show(handle, ExternalAppNativeMethods.SwHide);", StringComparison.Ordinal);
int reparentRestoreBranch = restoreWindowCode.IndexOf("else", overlayRestoreCheck, StringComparison.Ordinal);
Need(overlayRestoreCheck >= 0 && restoreHideCall > overlayRestoreCheck && reparentRestoreBranch > restoreHideCall,
    "Restoring a reparented app must not send the destructive SW_HIDE used only for overlay windows.");
Need(restoreWindowCode.Contains("RestoreOverlayOwner(handle, snapshot.Parent);", StringComparison.Ordinal) &&
    restoreWindowCode.Contains("ExternalAppNativeMethods.GetOwner(handle) != owner", StringComparison.Ordinal) &&
    restoreWindowCode.Contains("restore-owner-confirmed-after-error", StringComparison.Ordinal),
    "Overlay restoration must continue after a teardown owner error only when the live HWND already has the intended owner.");
string webViewEnvironmentCode = File.ReadAllText(webViewEnvironmentPath);
Need(webViewEnvironmentCode.Contains("DATA-SAFETY INVARIANT", StringComparison.Ordinal) &&
    webViewEnvironmentCode.Contains("PortableDataPaths.WebView2Directory", StringComparison.Ordinal) &&
    webViewEnvironmentCode.Contains("AppContext.BaseDirectory", StringComparison.Ordinal) &&
    !webViewEnvironmentCode.Contains("Path.Combine(AppContext.BaseDirectory", StringComparison.Ordinal),
    "WebView2 environment is missing the canonical path logout-prevention invariant.");
Need(mainWindowCode.Contains("Current tab origin:", StringComparison.Ordinal) &&
    mainWindowCode.Contains("FormatDiagnosticPageOrigin(selectedTabUrl)", StringComparison.Ordinal) &&
    !mainWindowCode.Contains("Current tab URL:", StringComparison.Ordinal),
    "Copied diagnostics can expose a full page URL with sensitive query or fragment data.");
Need(File.Exists(migrationRecoveryMarkupPath) &&
    File.ReadAllText(migrationRecoveryMarkupPath).Contains("Your sign-ins and settings were protected", StringComparison.Ordinal),
    "Unsafe migration should use the themed profile recovery window.");
string publishScriptPath = Path.Combine(repoRoot, "scripts", "publish-release.ps1");
Need(File.Exists(publishScriptPath),
    "Release manifest publish script is missing.");
string publishScript = File.ReadAllText(publishScriptPath);
Need(publishScript.Contains("dotnet publish", StringComparison.OrdinalIgnoreCase) &&
    publishScript.Contains("New-ApplicationFilesManifest", StringComparison.Ordinal) &&
    publishScript.Contains("Compress-Archive", StringComparison.OrdinalIgnoreCase) &&
    publishScript.Contains("Get-FileHash", StringComparison.OrdinalIgnoreCase) &&
    publishScript.Contains("version.json", StringComparison.OrdinalIgnoreCase) &&
    publishScript.Contains("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) &&
    publishScript.Contains("--self-contained true", StringComparison.OrdinalIgnoreCase) &&
    publishScript.Contains("PublishSingleFile=false", StringComparison.OrdinalIgnoreCase) &&
    publishScript.Contains("Invoke-OptionalSigning", StringComparison.Ordinal) &&
    publishScript.Contains("Assert-PayloadSafe", StringComparison.Ordinal) &&
    publishScript.Contains("Assert-NoDeveloperOnlyPayloadFiles", StringComparison.Ordinal) &&
    publishScript.Contains("'QA.md', 'RELEASE.md', 'version.example.json'", StringComparison.Ordinal) &&
    publishScript.Contains("Write-Utf8NoBomFile", StringComparison.Ordinal) &&
    !publishScript.Contains("Set-Content -LiteralPath $manifestPath -Encoding UTF8", StringComparison.OrdinalIgnoreCase),
    "Release manifest publish script does not build, zip, hash, reject profile data, and generate version.json.");
string publicCandidateWorkflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));
Need(publicCandidateWorkflow.Contains("artifacts/release/QuickPanel-${{ steps.meta.outputs.version }}-win-x64.zip", StringComparison.Ordinal) &&
    publicCandidateWorkflow.Contains("artifacts/release/version.json", StringComparison.Ordinal) &&
    publicCandidateWorkflow.Contains("artifacts/release/SHA256SUMS.txt", StringComparison.Ordinal) &&
    !publicCandidateWorkflow.Contains("artifacts/release/*\n", StringComparison.Ordinal) &&
    !publicCandidateWorkflow.Contains("gh release create", StringComparison.Ordinal),
    "Private-source release workflow does not enforce the three-file public artifact allowlist.");
string updateLocalScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "update-local.ps1"));
string installCleanScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "install-clean.ps1"));
string autoApplyCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "PortableUpdateAutoApply.cs"));
string portableInstallerCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "PortableUpdateInstaller.cs"));
string portableWorkerCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "PortableUpdateWorker.cs"));
string releaseWorkflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));
Need(updateLocalScript.Contains("Assert-InstallTargetSafe", StringComparison.Ordinal) &&
    updateLocalScript.Contains("New-ApplicationFilesManifest", StringComparison.Ordinal) &&
    updateLocalScript.Contains("Assert-PayloadSafe", StringComparison.Ordinal) &&
    updateLocalScript.Contains("publishStage", StringComparison.Ordinal) &&
    !updateLocalScript.Contains("--output', $target.Folder", StringComparison.OrdinalIgnoreCase),
    "Local updater does not stage and validate application payload independently from persistent data.");
string profileSafetyScript = File.ReadAllText(Path.Combine(repoRoot, "scripts", "profile-data-safety.ps1"));
Need(profileSafetyScript.Contains("Assert-NoReparsePointPath", StringComparison.Ordinal) &&
    profileSafetyScript.Contains("Get-OwnedInstallEntries", StringComparison.Ordinal) &&
    profileSafetyScript.Contains("application-files.json", StringComparison.Ordinal) &&
    profileSafetyScript.Contains("FileAttributes]::ReparsePoint", StringComparison.Ordinal) &&
    profileSafetyScript.Contains("GetLinkCount", StringComparison.Ordinal) &&
    profileSafetyScript.Contains("[char[]]", StringComparison.Ordinal) &&
    profileSafetyScript.Contains("[Text.UTF8Encoding]::new($false)", StringComparison.Ordinal),
    "PowerShell install/update safety does not reject reparse-point path aliases.");
string updaterEvidenceScriptPath = Path.Combine(repoRoot, "scripts", "capture-updater-evidence.ps1");
string updaterEvidenceTestPath = Path.Combine(repoRoot, "tests", "capture-updater-evidence.tests.ps1");
Need(File.Exists(updaterEvidenceScriptPath) && File.Exists(updaterEvidenceTestPath),
    "Work-laptop updater evidence capture and its regression test are missing.");
string updaterEvidenceScript = File.ReadAllText(updaterEvidenceScriptPath);
Need(updaterEvidenceScript.Contains("Get-HashedTreeInventory", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("Resolve-QuickPanelExistingPhysicalDirectory", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("Get-WindowsApplicationEvidence", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("Events = @($events | ForEach-Object { $_ })", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("Assert-LocalEvidencePath", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("FileMode]::CreateNew", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("ContainsSensitiveDiagnosticEvidence", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("MaintenanceRoot", StringComparison.Ordinal) &&
    updaterEvidenceScript.Contains("FailOnMismatch", StringComparison.Ordinal) &&
    !updaterEvidenceScript.Contains("Remove-Item", StringComparison.OrdinalIgnoreCase),
    "Updater evidence capture can mutate profile state or omit required forensic evidence.");
string qaDocument = File.ReadAllText(Path.Combine(repoRoot, "QA.md"));
string supportDocument = File.ReadAllText(Path.Combine(repoRoot, "SUPPORT.md"));
Need(!qaDocument.Contains("$env:PUBLIC", StringComparison.OrdinalIgnoreCase) &&
    qaDocument.Contains("review and redact", StringComparison.OrdinalIgnoreCase) &&
    supportDocument.Contains("never `%PUBLIC%`", StringComparison.OrdinalIgnoreCase) &&
    supportDocument.Contains("sensitive diagnostic evidence", StringComparison.OrdinalIgnoreCase),
    "Updater evidence guidance can expose raw diagnostics through a shared or unreviewed report.");
Need(installCleanScript.Contains("Assert-InstallTargetSafe", StringComparison.Ordinal) &&
    installCleanScript.Contains("New-ApplicationFilesManifest", StringComparison.Ordinal) &&
    installCleanScript.Contains("Assert-PayloadSafe", StringComparison.Ordinal) &&
    installCleanScript.Contains("publishStage", StringComparison.Ordinal) &&
    !installCleanScript.Contains("--output', $installFolder", StringComparison.OrdinalIgnoreCase),
    "Clean installer can publish directly over persistent or legacy profile data.");
Need(!autoApplyCode.Contains("ModuleInitializer", StringComparison.Ordinal) &&
    !autoApplyCode.Contains("FileSystemWatcher", StringComparison.Ordinal) &&
    autoApplyCode.Contains("sha256Verified", StringComparison.Ordinal) &&
    portableInstallerCode.Contains("ReleasePayloadPolicy.EnsurePayloadContainsNoProfile", StringComparison.Ordinal) &&
    portableWorkerCode.Contains("ReleasePayloadPolicy.ReplaceApplicationFiles", StringComparison.Ordinal) &&
    !portableWorkerCode.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
    "Built-in updater is not explicitly SHA256-bound, profile-safe, and shell-free.");
Need(!releaseWorkflow.Contains("push:", StringComparison.Ordinal) &&
    releaseWorkflow.Contains("workflow_dispatch:", StringComparison.Ordinal),
    "Release workflow would consume GitHub Actions minutes on a normal main-branch push.");
Need(!new[] { portableInstallerCode, portableWorkerCode, updateLocalScript, installCleanScript, publishScript }
        .Any(text => text.Contains("/MIR", StringComparison.OrdinalIgnoreCase)),
    "An update/install path still uses destructive mirror semantics.");
Need(!File.Exists(Path.Combine(repoRoot, "tools", "patch-portable-settings-and-build.ps1")),
    "The obsolete portable build helper could still bundle a real user's settings into a release.");

string browserTabViewCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Views", "BrowserTabView.cs"));
Need(browserTabViewCode.Contains("CreateCoreWebView2ControllerOptions", StringComparison.Ordinal) &&
    browserTabViewCode.Contains("controllerOptions.ProfileName = profileId", StringComparison.Ordinal) &&
    browserTabViewCode.Contains("EnsureCoreWebView2Async(environment, controllerOptions)", StringComparison.Ordinal) &&
    browserTabViewCode.Contains("EnsureCoreWebView2Async(environment)", StringComparison.Ordinal),
    "Web tabs do not preserve the legacy default profile while creating named WebView2 profiles for isolated accounts.");
Need(mainWindowCode.Contains("Open current page with new profile", StringComparison.Ordinal) &&
    mainWindowCode.Contains("TryNormalizeUrl(currentUrl, out string normalizedUrl)", StringComparison.Ordinal) &&
    mainWindowCode.Contains("GetTabsForProfileDiscovery()", StringComparison.Ordinal) &&
    mainWindowCode.Contains("_closedTabs.Entries.Select(entry => entry.Tab)", StringComparison.Ordinal) &&
    mainWindowCode.Contains("CreateWebTabHeader(tab)", StringComparison.Ordinal),
    "The web-tab workflow is missing the same-URL new-profile action or its profile-aware icon badge.");
int clearProfileStart = mainWindowCode.IndexOf("private async Task ClearSelectedBrowsingDataAsync()", StringComparison.Ordinal);
int clearProfileEnd = mainWindowCode.IndexOf("private async void SettingsWindow_CheckForUpdatesRequested", clearProfileStart, StringComparison.Ordinal);
string clearProfileCode = mainWindowCode[clearProfileStart..clearProfileEnd];
Need(clearProfileCode.Contains("Select a web tab before clearing browsing data.", StringComparison.Ordinal) &&
    clearProfileCode.Contains("Wait for the selected web tab to finish loading", StringComparison.Ordinal) &&
    !clearProfileCode.Contains("FirstOrDefault", StringComparison.Ordinal),
    "Browsing-data clearing can still fall back to an unrelated profile.");
string addTabMarkup = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel.Views.AddTabWindow.xaml"));
Need(addTabMarkup.Contains("BROWSING PROFILE", StringComparison.Ordinal) &&
    addTabMarkup.Contains("AutomationProperties.Name=\"Browsing profile\"", StringComparison.Ordinal),
    "Add/Edit tab does not expose an accessible browsing-profile selector.");
string settingsMarkup = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel.Views.SettingsWindow.xaml"));
Need(settingsMarkup.Contains("selected tab's browsing profile", StringComparison.Ordinal) &&
    !settingsMarkup.Contains("data for all web tabs", StringComparison.Ordinal),
    "Browsing-data settings still imply that independent profiles share one data store.");

string transactionSandbox = TempDirectory();
try
{
    string transactionPath = Path.Combine(transactionSandbox, "transaction.json");
    string statePath = Path.Combine(transactionSandbox, "state.json");
    var transaction = new UpdateTransaction(
        SchemaVersion: 1,
        AttemptId: "literal-attempt-id",
        Nonce: "literal-test-nonce",
        Operation: "update",
        ParentProcessId: 101,
        PayloadDirectory: Path.Combine(transactionSandbox, "payload"),
        InstallDirectory: Path.Combine(transactionSandbox, "install"),
        CanonicalDataDirectory: Path.Combine(transactionSandbox, "profile"),
        ApplicationExecutable: Path.Combine(transactionSandbox, "install", "AIQuickPanel.exe"),
        BackupDirectory: Path.Combine(transactionSandbox, "backup"),
        LogPath: Path.Combine(transactionSandbox, "update.log"),
        ExpectedCurrentVersion: "2.4.10",
        ExpectedTargetVersion: "2.4.11",
        PayloadManifestSha256: new string('A', 64),
        RestartApplication: true);

    UpdateTransactionStore.Create(transactionPath, transaction);
    UpdateTransaction readTransaction = UpdateTransactionStore.ReadTransaction(transactionPath);
    Need(readTransaction.Nonce == "literal-test-nonce" &&
        readTransaction.ExpectedCurrentVersion == "2.4.10" &&
        readTransaction.ExpectedTargetVersion == "2.4.11",
        "Transaction round-trip changed immutable update inputs.");

    UpdateTransactionStore.Advance(
        statePath,
        transaction.Nonce,
        UpdateTransactionPhase.RollbackVerified,
        processId: 201);
    UpdateTransactionStore.Advance(
        statePath,
        transaction.Nonce,
        UpdateTransactionPhase.GuardianReady,
        processId: 202,
        detail: "guardian prepared");
    UpdateTransactionSnapshot guardianReady = UpdateTransactionStore.ReadState(statePath);
    Need(guardianReady.Phase == UpdateTransactionPhase.GuardianReady &&
        guardianReady.ProcessId == 202 &&
        guardianReady.Detail == "guardian prepared",
        "Valid transaction transitions were not persisted.");

    string lastValidState = File.ReadAllText(statePath);
    bool nonceMismatchRejected = false;
    try
    {
        UpdateTransactionStore.Advance(
            statePath,
            "wrong-nonce",
            UpdateTransactionPhase.ParentExited,
            processId: 203);
    }
    catch (InvalidDataException)
    {
        nonceMismatchRejected = true;
    }
    Need(nonceMismatchRejected && File.ReadAllText(statePath) == lastValidState,
        "A nonce mismatch changed durable transaction state.");

    bool skippedTransitionRejected = false;
    try
    {
        UpdateTransactionStore.Advance(
            statePath,
            transaction.Nonce,
            UpdateTransactionPhase.ReplacementStarted,
            processId: 203);
    }
    catch (InvalidOperationException)
    {
        skippedTransitionRejected = true;
    }
    Need(skippedTransitionRejected && File.ReadAllText(statePath) == lastValidState,
        "An out-of-order transition changed durable transaction state.");

    string malformedPath = Path.Combine(transactionSandbox, "malformed.json");
    File.WriteAllText(malformedPath, "not-json");
    bool malformedRejected = false;
    try
    {
        _ = UpdateTransactionStore.ReadTransaction(malformedPath);
    }
    catch (InvalidDataException)
    {
        malformedRejected = true;
    }
    Need(malformedRejected, "Malformed transaction JSON was accepted.");

    bool duplicateCreateRejected = false;
    try
    {
        UpdateTransactionStore.Create(transactionPath, transaction);
    }
    catch (IOException)
    {
        duplicateCreateRejected = true;
    }
    Need(duplicateCreateRejected &&
        UpdateTransactionStore.ReadTransaction(transactionPath) == readTransaction,
        "Duplicate transaction creation overwrote immutable inputs.");

    UpdateTransactionStore.Advance(
        statePath,
        transaction.Nonce,
        UpdateTransactionPhase.ParentExited,
        processId: 204);
    UpdateTransactionStore.Advance(
        statePath,
        transaction.Nonce,
        UpdateTransactionPhase.ReplacementStarted,
        processId: 204);
    UpdateTransactionStore.Advance(
        statePath,
        transaction.Nonce,
        UpdateTransactionPhase.ReplacementVerified,
        processId: 204);
    UpdateTransactionStore.Advance(
        statePath,
        transaction.Nonce,
        UpdateTransactionPhase.RestartStarted,
        processId: 204);
    UpdateTransactionStore.Advance(
        statePath,
        transaction.Nonce,
        UpdateTransactionPhase.Committed,
        processId: 204);
    string committedState = File.ReadAllText(statePath);
    bool terminalRewriteRejected = false;
    try
    {
        UpdateTransactionStore.Advance(
            statePath,
            transaction.Nonce,
            UpdateTransactionPhase.Failed,
            processId: 205);
    }
    catch (InvalidOperationException)
    {
        terminalRewriteRejected = true;
    }
    Need(terminalRewriteRejected && File.ReadAllText(statePath) == committedState,
        "A terminal transaction was rewritten.");
}
finally
{
    Directory.Delete(transactionSandbox, recursive: true);
}

Console.WriteLine("Tests pass.");

sealed class FakeUpdateHttpMessageHandler : HttpMessageHandler
{
    private readonly string _manifestJson;
    private readonly byte[] _downloadBytes;

    public Uri? LastRequestUri { get; private set; }

    public FakeUpdateHttpMessageHandler(string manifestJson, byte[] downloadBytes)
    {
        _manifestJson = manifestJson;
        _downloadBytes = downloadBytes;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        if (request.RequestUri?.AbsolutePath.EndsWith("/version.json", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_manifestJson, Encoding.UTF8, "application/json")
            });
        }
        if (request.RequestUri?.AbsolutePath.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_manifestJson, Encoding.UTF8, "application/json")
            });
        }
        if (request.RequestUri?.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_downloadBytes)
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

sealed class NotFoundUpdateHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

sealed class FakeCodexUsageHttpMessageHandler : HttpMessageHandler
{
    public bool UsageWasCalled { get; private set; }

    public bool ResetCreditsWasCalled { get; private set; }

    public string? ResetCreditsAccountHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool hasBearer = request.Headers.Authorization?.Scheme == "Bearer" &&
            request.Headers.Authorization.Parameter == "safe-test-token";
        bool isResetCreditsRequest = request.RequestUri?.AbsolutePath.EndsWith("/wham/rate-limit-reset-credits", StringComparison.OrdinalIgnoreCase) == true;
        if (!hasBearer)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"missing bearer auth"}""", Encoding.UTF8, "application/json")
            });
        }
        if (isResetCreditsRequest)
        {
            bool acceptsJson = request.Headers.Accept.Any(value => string.Equals(value.MediaType, "application/json", StringComparison.OrdinalIgnoreCase));
            if (!acceptsJson || request.Content != null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"reset credit GET must accept JSON and have no request body"}""", Encoding.UTF8, "application/json")
                });
            }
            ResetCreditsWasCalled = true;
            ResetCreditsAccountHeader = request.Headers.TryGetValues("ChatGPT-Account-Id", out IEnumerable<string>? accountHeaders)
                ? accountHeaders.SingleOrDefault()
                : null;
            if (ResetCreditsAccountHeader != "acct-safe-test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"missing account header"}""", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "available_count": 1,
                  "credits": [
                    {
                      "title": "Reset GPT-5 thinking",
                      "status": "available",
                      "granted_at": "2026-06-24T10:00:00Z",
                      "expires_at": "2026-07-24T10:00:00Z"
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json")
            });
        }
        if (request.RequestUri?.AbsolutePath.EndsWith("/wham/usage", StringComparison.OrdinalIgnoreCase) != true)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        bool hasContentType = request.Headers.TryGetValues("Content-Type", out IEnumerable<string>? headerValues) &&
            headerValues.Contains("application/json", StringComparer.OrdinalIgnoreCase);
        hasContentType = hasContentType ||
            string.Equals(request.Content?.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        if (!hasContentType)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
        UsageWasCalled = true;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "account_id": "acct-safe-test",
              "plan_type": "plus",
              "rate_limit": {
                "allowed": true,
                "limit_reached": false,
                "primary_window": {
                  "used_percent": 7,
                  "limit_window_seconds": 18000,
                  "reset_after_seconds": 17489,
                  "reset_at": 1782331601
                },
                "secondary_window": {
                  "used_percent": 1,
                  "limit_window_seconds": 604800,
                  "reset_after_seconds": 604289,
                  "reset_at": 1782918401
                }
              },
              "rate_limit_reset_credits": {
                "available_count": 0
              }
            }
            """, Encoding.UTF8, "application/json")
        });
    }
}

sealed class ThrowingUpdateHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new HttpRequestException("No internet");
    }
}

sealed class RateLimitedUpdateHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        return Task.FromResult(response);
    }
}
