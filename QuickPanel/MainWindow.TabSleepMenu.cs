using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using QuickPanel.Views;

namespace QuickPanel;

public partial class MainWindow
{
    private const string SleepMenuTag = "quickpanel:sleep-menu";

    [ModuleInitializer]
    internal static void RegisterTabSleepContextMenu()
    {
        EventManager.RegisterClassHandler(
            typeof(TabItem),
            ContextMenuService.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(TabItem_ContextMenuOpening),
            true);
    }

    private static void TabItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TabItem tabItem ||
            tabItem.Content is not BrowserTabView ||
            Window.GetWindow(tabItem) is not MainWindow window ||
            tabItem.ContextMenu == null ||
            tabItem.Tag is not string tabId ||
            string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        window.EnsureTabSleepMenu(tabItem, tabId);
    }

    private void EnsureTabSleepMenu(TabItem tabItem, string tabId)
    {
        ContextMenu menu = tabItem.ContextMenu!;
        foreach (object existing in menu.Items.OfType<object>().ToList())
        {
            if (existing is MenuItem { Tag: string tag } item && tag == SleepMenuTag)
            {
                menu.Items.Remove(item);
                break;
            }
        }

        if (menu.Items.Count > 0 && menu.Items[menu.Items.Count - 1] is not Separator)
        {
            menu.Items.Add(new Separator());
        }

        int? currentOverride = _performanceSettingsService.GetTabSleepOverrideMinutes(tabId);
        string tabName = tabItem.ToolTip?.ToString() ?? "this tab";
        MenuItem sleep = new()
        {
            Header = "Sleep inactive tab",
            Tag = SleepMenuTag
        };

        AddSleepOption(sleep, "Use global default", currentOverride == null, () =>
        {
            _performanceSettingsService.SaveTabSleepOverrideMinutes(tabId, null);
        });
        AddSleepOption(sleep, "Never sleep", currentOverride == 0, () =>
        {
            _performanceSettingsService.SaveTabSleepOverrideMinutes(tabId, 0);
            if (tabItem.Content is BrowserTabView browser)
            {
                browser.ResumeFromInactivitySleep();
            }
        });
        sleep.Items.Add(new Separator());

        foreach (int minutes in new[] { 5, 10, 15, 30, 60 })
        {
            int captured = minutes;
            AddSleepOption(sleep, "Sleep after " + captured + " min", currentOverride == captured, () =>
            {
                _performanceSettingsService.SaveTabSleepOverrideMinutes(tabId, captured);
            });
        }

        MenuItem custom = new() { Header = "Custom…" };
        custom.Click += (_, _) =>
        {
            int initial = currentOverride is > 0
                ? currentOverride.Value
                : Math.Max(1, _performanceSettingsService.Load().InactiveTabSleepMinutes);
            TabSleepOverrideWindow dialog = new(tabName, initial)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true && dialog.Minutes != null)
            {
                _performanceSettingsService.SaveTabSleepOverrideMinutes(tabId, dialog.Minutes.Value);
            }
        };
        sleep.Items.Add(custom);
        menu.Items.Add(sleep);
    }

    private static void AddSleepOption(MenuItem parent, string header, bool isChecked, Action action)
    {
        MenuItem item = new()
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isChecked
        };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
    }
}
