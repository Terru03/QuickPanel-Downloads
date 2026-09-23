using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickPanel.Services;
using QuickPanel.Views;

namespace QuickPanel;

public partial class MainWindow
{
    private readonly PerformanceSettingsService _performanceSettingsService = new();
    private readonly Dictionary<BrowserTabView, DateTimeOffset> _browserTabLastActive = new();
    private DispatcherTimer? _tabSleepTimer;
    private bool _tabSleepAttached;

    [ModuleInitializer]
    internal static void RegisterTabSleeping()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_TabSleepingLoaded));
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(MainWindow_TabSleepingUnloaded));
    }

    private static void MainWindow_TabSleepingLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.AttachTabSleeping();
        }
    }

    private static void MainWindow_TabSleepingUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.DetachTabSleeping();
        }
    }

    private void AttachTabSleeping()
    {
        if (_tabSleepAttached)
        {
            return;
        }

        _tabSleepAttached = true;
        AiTabs.SelectionChanged += AiTabs_TabSleepingSelectionChanged;
        IsVisibleChanged += MainWindow_TabSleepingVisibilityChanged;
        _tabSleepTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _tabSleepTimer.Tick += TabSleepTimer_Tick;
        _tabSleepTimer.Start();
        MarkSelectedBrowserTabActive();
    }

    private void DetachTabSleeping()
    {
        if (!_tabSleepAttached)
        {
            return;
        }

        _tabSleepAttached = false;
        AiTabs.SelectionChanged -= AiTabs_TabSleepingSelectionChanged;
        IsVisibleChanged -= MainWindow_TabSleepingVisibilityChanged;
        if (_tabSleepTimer != null)
        {
            _tabSleepTimer.Stop();
            _tabSleepTimer.Tick -= TabSleepTimer_Tick;
            _tabSleepTimer = null;
        }
    }

    private void AiTabs_TabSleepingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkSelectedBrowserTabActive();
    }

    private void MainWindow_TabSleepingVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            MarkSelectedBrowserTabActive();
        }
    }

    private async void TabSleepTimer_Tick(object? sender, EventArgs e)
    {
        await ApplyTabSleepPolicyAsync();
    }

    private void MarkSelectedBrowserTabActive()
    {
        if (AiTabs.SelectedItem is TabItem { Content: BrowserTabView browser })
        {
            _browserTabLastActive[browser] = DateTimeOffset.Now;
            browser.ResumeFromInactivitySleep();
        }
    }

    private async Task ApplyTabSleepPolicyAsync()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        HashSet<BrowserTabView> currentViews = AiTabs.Items
            .OfType<TabItem>()
            .Select(item => item.Content)
            .OfType<BrowserTabView>()
            .ToHashSet();

        foreach (BrowserTabView stale in _browserTabLastActive.Keys.Where(view => !currentViews.Contains(view)).ToList())
        {
            _browserTabLastActive.Remove(stale);
        }

        foreach (TabItem tabItem in AiTabs.Items.OfType<TabItem>())
        {
            if (tabItem.Content is not BrowserTabView browser)
            {
                continue;
            }

            string? tabId = tabItem.Tag?.ToString();
            int sleepMinutes = _performanceSettingsService.GetEffectiveSleepMinutes(tabId);
            bool isActive = IsVisible && ReferenceEquals(AiTabs.SelectedItem, tabItem);
            if (sleepMinutes <= 0 || isActive)
            {
                browser.ResumeFromInactivitySleep();
                if (isActive)
                {
                    _browserTabLastActive[browser] = now;
                }
                continue;
            }

            if (!_browserTabLastActive.TryGetValue(browser, out DateTimeOffset lastActive))
            {
                _browserTabLastActive[browser] = now;
                continue;
            }

            if (now - lastActive >= TimeSpan.FromMinutes(sleepMinutes))
            {
                await browser.TrySuspendForInactivityAsync();
            }
        }
    }

    internal (int Total, int Active, int Sleeping) GetBrowserTabResourceCounts()
    {
        List<BrowserTabView> views = AiTabs.Items
            .OfType<TabItem>()
            .Select(item => item.Content)
            .OfType<BrowserTabView>()
            .ToList();

        int sleeping = views.Count(view => view.IsSuspended);
        int active = views.Count - sleeping;
        return (views.Count, active, sleeping);
    }
}
