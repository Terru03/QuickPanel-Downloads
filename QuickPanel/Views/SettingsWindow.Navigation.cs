using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickPanel.Views;

public sealed record SettingsSearchResult(string Title, string Category, string Keywords)
{
    public string DisplayText => Title + "  ·  " + Category;
}

public partial class SettingsWindow
{
    private static readonly IReadOnlyList<SettingsSearchResult> SearchIndex = new[]
    {
        new SettingsSearchResult("Always on top", "General", "window topmost panel"),
        new SettingsSearchResult("Start Quick Panel with Windows", "General", "startup boot login windows"),
        new SettingsSearchResult("Launch Codex automatically", "General", "codex startup dock"),
        new SettingsSearchResult("Sleep inactive web tabs", "Performance", "sleep suspend webview tabs cpu gpu ram minutes"),
        new SettingsSearchResult("Resource monitor", "Performance", "ram memory webview active sleeping tabs gpu"),
        new SettingsSearchResult("Show or hide panel", "Keyboard", "shortcut hotkey ctrl alt g"),
        new SettingsSearchResult("Quick Command", "Keyboard", "command palette ctrl k shortcut"),
        new SettingsSearchResult("WebView2 browsing data", "Privacy", "clear cookies cache history site data"),
        new SettingsSearchResult("Check for updates", "Updates", "update github release version"),
        new SettingsSearchResult("Check for updates on startup", "Updates", "automatic update startup"),
        new SettingsSearchResult("Rollback update", "Updates", "restore previous version rollback recovery"),
        new SettingsSearchResult("Diagnostics", "Advanced", "copy diagnostics logs status"),
        new SettingsSearchResult("Settings backup", "Advanced", "export import restore backup settings"),
        new SettingsSearchResult("Reset layout", "Advanced", "reset layout recovery")
    };

    private bool SettingsPagesReady => GeneralPage != null && PerformancePage != null && KeyboardPage != null && PrivacyPage != null && UpdatesPage != null && AdvancedPage != null && SearchResultsBorder != null;

    private void SettingsCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!SettingsPagesReady || SettingsCategoryList.SelectedItem is not ListBoxItem item || item.Tag is not string category)
        {
            return;
        }

        ShowSettingsCategory(category);
    }

    private void ShowSettingsCategory(string category)
    {
        if (!SettingsPagesReady)
        {
            return;
        }

        GeneralPage.Visibility = category == "General" ? Visibility.Visible : Visibility.Collapsed;
        PerformancePage.Visibility = category == "Performance" ? Visibility.Visible : Visibility.Collapsed;
        KeyboardPage.Visibility = category == "Keyboard" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = category == "Privacy" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPage.Visibility = category == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = category == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        SearchResultsBorder.Visibility = Visibility.Collapsed;

        if (category == "Performance")
        {
            _ = RefreshResourceMonitorAsync();
        }
    }

    private void SettingsSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!SettingsPagesReady || SearchResultsList == null)
        {
            return;
        }

        string query = SettingsSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResultsBorder.Visibility = Visibility.Collapsed;
            if (SettingsCategoryList.SelectedItem is ListBoxItem selected && selected.Tag is string category)
            {
                ShowSettingsCategory(category);
            }
            return;
        }

        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<SettingsSearchResult> results = SearchIndex
            .Where(result => terms.All(term =>
                result.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                result.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                result.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(30)
            .ToList();

        GeneralPage.Visibility = Visibility.Collapsed;
        PerformancePage.Visibility = Visibility.Collapsed;
        KeyboardPage.Visibility = Visibility.Collapsed;
        PrivacyPage.Visibility = Visibility.Collapsed;
        UpdatesPage.Visibility = Visibility.Collapsed;
        AdvancedPage.Visibility = Visibility.Collapsed;
        SearchResultsList.ItemsSource = results;
        SearchResultsBorder.Visibility = Visibility.Visible;
        if (results.Count > 0)
        {
            SearchResultsList.SelectedIndex = 0;
        }
    }

    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedSearchResult();

    private void SearchResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedSearchResult();
            e.Handled = true;
        }
    }

    private void OpenSelectedSearchResult()
    {
        if (SearchResultsList.SelectedItem is not SettingsSearchResult result)
        {
            return;
        }

        SettingsSearchTextBox.Text = string.Empty;
        foreach (ListBoxItem item in SettingsCategoryList.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), result.Category, StringComparison.Ordinal))
            {
                SettingsCategoryList.SelectedItem = item;
                item.BringIntoView();
                ShowSettingsCategory(result.Category);
                break;
            }
        }
    }
}
