using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class SettingsWindow
{
    private readonly PerformanceSettingsService _performanceSettingsService = new();
    private bool _loadingTabSleepSetting;
    private bool _performanceUiInitialised;
    private DispatcherTimer? _resourceMonitorTimer;

    private void EnsurePerformanceSettingsUi()
    {
        if (_performanceUiInitialised)
        {
            return;
        }

        _performanceUiInitialised = true;
        foreach (string item in new[] { "Never", "5 minutes", "10 minutes", "15 minutes", "30 minutes", "60 minutes" })
        {
            TabSleepComboBox.Items.Add(item);
        }
        TabSleepComboBox.IsTextSearchEnabled = false;
        TabSleepComboBox.SelectionChanged += TabSleepComboBox_SelectionChanged;
        TabSleepComboBox.LostKeyboardFocus += TabSleepComboBox_LostKeyboardFocus;
        TabSleepComboBox.PreviewKeyDown += TabSleepComboBox_PreviewKeyDown;
        LoadTabSleepSettingIntoUi();

        _resourceMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _resourceMonitorTimer.Tick += ResourceMonitorTimer_Tick;
        _resourceMonitorTimer.Start();
        _ = RefreshResourceMonitorAsync();
    }

    private void StopPerformanceMonitoring()
    {
        if (_resourceMonitorTimer == null)
        {
            return;
        }

        _resourceMonitorTimer.Stop();
        _resourceMonitorTimer.Tick -= ResourceMonitorTimer_Tick;
        _resourceMonitorTimer = null;
    }

    private async void ResourceMonitorTimer_Tick(object? sender, EventArgs e)
    {
        if (PerformancePage.Visibility == Visibility.Visible)
        {
            await RefreshResourceMonitorAsync();
        }
    }

    private async void ResourceMonitorRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshResourceMonitorAsync();
    }

    private async Task RefreshResourceMonitorAsync()
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            long quickPanelBytes = current.WorkingSet64;
            long webViewBytes = 0;
            int webViewProcessCount = 0;

            try
            {
                var environment = await WebViewEnvironmentService.GetAsync();
                foreach (var processInfo in environment.GetProcessInfos())
                {
                    try
                    {
                        using Process process = Process.GetProcessById(processInfo.ProcessId);
                        webViewBytes += process.WorkingSet64;
                        webViewProcessCount++;
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
            }

            int totalTabs = 0;
            int activeTabs = 0;
            int sleepingTabs = 0;
            if (Owner is QuickPanel.MainWindow mainWindow)
            {
                (totalTabs, activeTabs, sleepingTabs) = mainWindow.GetBrowserTabResourceCounts();
            }

            ResourceMonitorText.Text =
                "Quick Panel RAM: " + FormatBytes(quickPanelBytes) + Environment.NewLine +
                "WebView2 RAM: " + FormatBytes(webViewBytes) + " (" + webViewProcessCount.ToString(CultureInfo.CurrentCulture) + " processes)" + Environment.NewLine +
                "Web tabs: " + totalTabs.ToString(CultureInfo.CurrentCulture) +
                " | active: " + activeTabs.ToString(CultureInfo.CurrentCulture) +
                " | sleeping: " + sleepingTabs.ToString(CultureInfo.CurrentCulture) + Environment.NewLine +
                "GPU: not shown because Windows does not expose reliable per-app WebView2 GPU usage without expensive performance-counter sampling.";
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            ResourceMonitorText.Text = "Resource data unavailable: " + ex.Message;
        }
    }

    private static string FormatBytes(long bytes)
    {
        double mb = Math.Max(0, bytes) / 1024d / 1024d;
        return mb.ToString("0.0", CultureInfo.CurrentCulture) + " MB";
    }

    private void LoadTabSleepSettingIntoUi()
    {
        _loadingTabSleepSetting = true;
        try
        {
            int minutes = _performanceSettingsService.Load().InactiveTabSleepMinutes;
            TabSleepComboBox.Text = FormatSleepMinutes(minutes);
        }
        finally
        {
            _loadingTabSleepSetting = false;
        }
    }

    private void TabSleepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingTabSleepSetting)
        {
            SaveTabSleepSettingFromUi();
        }
    }

    private void TabSleepComboBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SaveTabSleepSettingFromUi();
    }

    private void TabSleepComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveTabSleepSettingFromUi();
            e.Handled = true;
        }
    }

    private void SaveTabSleepSettingFromUi()
    {
        if (_loadingTabSleepSetting)
        {
            return;
        }

        if (!TryParseSleepMinutes(TabSleepComboBox.Text, out int minutes))
        {
            LoadTabSleepSettingIntoUi();
            return;
        }

        try
        {
            _performanceSettingsService.SaveInactiveTabSleepMinutes(minutes);
            _loadingTabSleepSetting = true;
            TabSleepComboBox.Text = FormatSleepMinutes(minutes);
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            LoadTabSleepSettingIntoUi();
        }
        finally
        {
            _loadingTabSleepSetting = false;
        }
    }

    private static bool TryParseSleepMinutes(string? text, out int minutes)
    {
        string value = text?.Trim() ?? string.Empty;
        if (value.Equals("never", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            minutes = 0;
            return true;
        }

        string digits = new(value.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            minutes = Math.Clamp(parsed, 1, 1440);
            return true;
        }

        minutes = 0;
        return false;
    }

    private static string FormatSleepMinutes(int minutes)
    {
        return minutes <= 0 ? "Never" : minutes.ToString(CultureInfo.CurrentCulture) + " minutes";
    }
}
