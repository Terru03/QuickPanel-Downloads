using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickPanel.Views;

public sealed class TabSleepOverrideWindow : Window
{
    private readonly TextBox _minutesBox;

    public int? Minutes { get; private set; }

    public TabSleepOverrideWindow(string tabName, int initialMinutes)
    {
        Title = "Tab sleep";
        Width = 320;
        Height = 180;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(13, 16, 23));
        Foreground = Brushes.White;

        Border outer = new()
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromRgb(47, 55, 72)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(13, 16, 23))
        };

        StackPanel stack = new();
        stack.Children.Add(new TextBlock
        {
            Text = "Sleep " + tabName + " after",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Enter any value from 1 to 1440 minutes.",
            Margin = new Thickness(0, 4, 0, 10),
            Foreground = new SolidColorBrush(Color.FromRgb(160, 171, 192)),
            FontSize = 11
        });

        _minutesBox = new TextBox
        {
            Text = Math.Clamp(initialMinutes, 1, 1440).ToString(CultureInfo.CurrentCulture),
            Height = 32,
            Padding = new Thickness(8, 5, 8, 5),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(18, 22, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 68, 89)),
            BorderThickness = new Thickness(1)
        };
        stack.Children.Add(_minutesBox);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Button cancel = new() { Content = "Cancel", Width = 72, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        Button save = new() { Content = "Save", Width = 72, Height = 30, IsDefault = true };
        save.Click += (_, _) => SaveAndClose();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        outer.Child = stack;
        Content = outer;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        ContentRendered += (_, _) =>
        {
            _minutesBox.Focus();
            _minutesBox.SelectAll();
        };
    }

    private void SaveAndClose()
    {
        if (!int.TryParse(_minutesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int minutes) ||
            minutes < 1 || minutes > 1440)
        {
            MessageBox.Show(this, "Enter a number from 1 to 1440 minutes.", "Invalid sleep time", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Minutes = minutes;
        DialogResult = true;
    }
}
