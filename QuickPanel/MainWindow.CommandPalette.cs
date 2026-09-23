using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using QuickPanel.Views;

namespace QuickPanel;

public partial class MainWindow
{
    private CommandPaletteWindow? _commandPaletteWindow;

    [ModuleInitializer]
    internal static void RegisterCommandPaletteShortcut()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(MainWindow_CommandPalettePreviewKeyDown),
            true);
    }

    private static void MainWindow_CommandPalettePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not MainWindow window ||
            e.Key != Key.K ||
            Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        window.OpenCommandPalette();
        e.Handled = true;
    }

    private void OpenCommandPalette()
    {
        if (_commandPaletteWindow is { IsVisible: true })
        {
            _commandPaletteWindow.Activate();
            return;
        }

        CommandPaletteWindow palette = new(BuildCommandPaletteItems())
        {
            Owner = this
        };
        _commandPaletteWindow = palette;
        palette.Closed += (_, _) =>
        {
            if (ReferenceEquals(_commandPaletteWindow, palette))
            {
                _commandPaletteWindow = null;
            }
        };
        palette.Show();
        palette.Activate();
    }

    private List<CommandPaletteItem> BuildCommandPaletteItems()
    {
        List<CommandPaletteItem> items = new()
        {
            new CommandPaletteItem("Settings", "preferences options configuration", OpenSettings),
            new CommandPaletteItem("Check for updates", "settings update github release version", () =>
            {
                OpenSettings();
                _settingsWindow?.RequestUpdateCheck();
            }),
            new CommandPaletteItem("Add tab", "new website web tab", OpenAddTabDialog),
            new CommandPaletteItem("Reload current tab", "refresh reload browser", () => ReloadSelectedBrowserTab()),
            new CommandPaletteItem("Codex Usage: refresh", "codex usage rate limit refresh", () =>
            {
                QuickToolsTabView? view = SelectQuickToolsPanel(NativeCodexUsageTabId);
                if (view != null)
                {
                    _ = view.RefreshCodexUsageFromCommandAsync();
                }
            }),
            new CommandPaletteItem("Open Trip Planner", "trip travel planner packing", () => SelectQuickToolsPanel(NativeTripPlannerTabId)),
            new CommandPaletteItem("Open Windows Helper", "windows helper fixes tools", () => SelectQuickToolsPanel(NativeWindowsHelperTabId)),
            new CommandPaletteItem("Windows Helper: restart Explorer", "windows explorer taskbar restart", () => SelectQuickToolsPanel(NativeWindowsHelperTabId)?.RestartExplorerFromCommand()),
            new CommandPaletteItem("Windows Helper: clear temp", "windows cleanup temp files", () => SelectQuickToolsPanel(NativeWindowsHelperTabId)?.ClearTempFromCommand()),
            new CommandPaletteItem("Windows Helper: Startup Apps", "windows startup apps settings", () => SelectQuickToolsPanel(NativeWindowsHelperTabId)?.OpenStartupAppsFromCommand()),
            new CommandPaletteItem("Windows Helper: test connection", "internet network lan ping speed", () => SelectQuickToolsPanel(NativeWindowsHelperTabId)?.TestConnectionFromCommand()),
            new CommandPaletteItem("Open Downloads", "folder downloads explorer", OpenDownloadsFolder),
            new CommandPaletteItem("Toggle Always on Top", "window topmost pin always top", ToggleAlwaysOnTopFromCommand),
            new CommandPaletteItem("Hide Quick Panel", "hide minimise close panel", HidePanel)
        };

        foreach (TabItem tabItem in AiTabs.Items.OfType<TabItem>())
        {
            string title = tabItem.ToolTip?.ToString() ?? AutomationProperties.GetName(tabItem);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            string keywords = "tab open switch " + (tabItem.Tag?.ToString() ?? string.Empty);
            items.Add(new CommandPaletteItem(title, keywords, () =>
            {
                AiTabs.SelectedItem = tabItem;
                tabItem.BringIntoView();
            }));
        }

        return items;
    }

    private QuickToolsTabView? SelectQuickToolsPanel(string tabId)
    {
        TabItem? tabItem = AiTabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tabId, StringComparison.Ordinal));
        if (tabItem == null)
        {
            tabItem = CreateNativePanelTabItem(tabId);
            AiTabs.Items.Add(tabItem);
            SaveTabOrder();
        }

        AiTabs.SelectedItem = tabItem;
        tabItem.BringIntoView();
        return tabItem.Content as QuickToolsTabView;
    }

    private static void OpenDownloadsFolder()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Process.Start(new ProcessStartInfo
        {
            FileName = downloads,
            UseShellExecute = true
        });
    }

    private void ToggleAlwaysOnTopFromCommand()
    {
        Topmost = !Topmost;
        _settingsService.SaveAlwaysOnTop(Topmost);
    }
}
