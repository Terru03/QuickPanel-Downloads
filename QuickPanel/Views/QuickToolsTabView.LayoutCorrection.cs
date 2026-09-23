using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuickPanel.Views;

public partial class QuickToolsTabView
{
    private bool _compactCodexHeaderApplied;
    private bool _layoutCorrectionHooksAttached;

    [ModuleInitializer]
    internal static void RegisterCodexLayoutCorrection()
    {
        EventManager.RegisterClassHandler(
            typeof(QuickToolsTabView),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(CodexLayoutCorrection_Loaded));
    }

    private static void CodexLayoutCorrection_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not QuickToolsTabView view || view._mode != QuickToolsPanelMode.CodexUsage)
        {
            return;
        }

        view.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(view.ApplyCodexLayoutCorrection));
    }

    private void ApplyCodexLayoutCorrection()
    {
        if (_mode != QuickToolsPanelMode.CodexUsage || !IsLoaded)
        {
            return;
        }

        RefreshCodexUsageButton.Content = "Refresh";

        if (!_compactCodexHeaderApplied &&
            PanelTitleText.Parent is Panel rootPanel &&
            ReferenceEquals(PanelSubtitleText.Parent, rootPanel) &&
            RefreshCodexUsageButton.Parent is Panel refreshParent)
        {
            int titleIndex = rootPanel.Children.IndexOf(PanelTitleText);
            if (titleIndex >= 0)
            {
                rootPanel.Children.Remove(PanelTitleText);
                rootPanel.Children.Remove(PanelSubtitleText);
                refreshParent.Children.Remove(RefreshCodexUsageButton);

                Grid header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                StackPanel text = new StackPanel();
                text.Children.Add(PanelTitleText);
                text.Children.Add(PanelSubtitleText);

                RefreshCodexUsageButton.Margin = new Thickness(12, 0, 0, 0);
                RefreshCodexUsageButton.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(RefreshCodexUsageButton, 1);

                header.Children.Add(text);
                header.Children.Add(RefreshCodexUsageButton);
                rootPanel.Children.Insert(titleIndex, header);

                // Remove only the wasted space at the top of this panel.
                // The overall Quick Panel height remains unchanged, matching the other tabs.
                CodexUsageSection.Margin = new Thickness(0, 8, 0, 0);
                UsageWindowsGrid.Margin = new Thickness(0);
                _compactCodexHeaderApplied = true;
            }
        }

        if (!_layoutCorrectionHooksAttached)
        {
            RefreshCodexUsageButton.IsEnabledChanged += RefreshCodexUsageButton_IsEnabledChangedLayoutCorrection;
            LayoutUpdated += CodexLayoutCorrection_LayoutUpdated;
            _layoutCorrectionHooksAttached = true;
        }

        RestoreOriginalWindowHeightIfAutoFitChangedIt();
    }

    private void RefreshCodexUsageButton_IsEnabledChangedLayoutCorrection(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!RefreshCodexUsageButton.IsEnabled)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            RefreshCodexUsageButton.Content = "Refresh";
            RestoreOriginalWindowHeightIfAutoFitChangedIt();
        }));
    }

    private void CodexLayoutCorrection_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_mode == QuickToolsPanelMode.CodexUsage && IsLoaded && _autoFitApplied)
        {
            RestoreOriginalWindowHeightIfAutoFitChangedIt();
        }
    }

    private void RestoreOriginalWindowHeightIfAutoFitChangedIt()
    {
        if (_autoFitApplied)
        {
            RestoreWindowHeightAfterCodexUsage();
        }
    }
}
