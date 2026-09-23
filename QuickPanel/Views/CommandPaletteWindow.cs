using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickPanel.Views;

public sealed record CommandPaletteItem(string Title, string Keywords, Action Execute);

public sealed class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<CommandPaletteItem> _allItems;
    private readonly TextBox _searchBox;
    private readonly ListBox _results;
    private bool _isClosing;

    public CommandPaletteWindow(IReadOnlyList<CommandPaletteItem> items)
    {
        _allItems = items;
        Title = "Quick Command";
        Width = 400;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(13, 16, 23));
        Foreground = Brushes.White;

        Border outer = new()
        {
            Padding = new Thickness(14),
            Background = new SolidColorBrush(Color.FromRgb(13, 16, 23)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(47, 55, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12)
        };

        Grid layout = new();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.Child = layout;

        TextBlock title = new()
        {
            Text = "Quick Command",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 10)
        };
        layout.Children.Add(title);

        _searchBox = new TextBox
        {
            Height = 36,
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 13,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(18, 22, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 68, 89)),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Type a command or tab name"
        };
        _searchBox.TextChanged += SearchBox_TextChanged;
        _searchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        Grid.SetRow(_searchBox, 1);
        layout.Children.Add(_searchBox);

        _results = new ListBox
        {
            Margin = new Thickness(0, 10, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            DisplayMemberPath = nameof(CommandPaletteItem.Title)
        };
        _results.MouseDoubleClick += Results_MouseDoubleClick;
        _results.PreviewKeyDown += Results_PreviewKeyDown;
        Grid.SetRow(_results, 2);
        layout.Children.Add(_results);

        Content = outer;
        PreviewKeyDown += CommandPaletteWindow_PreviewKeyDown;
        ContentRendered += CommandPaletteWindow_ContentRendered;
        Deactivated += CommandPaletteWindow_Deactivated;
        RefreshResults();
    }

    private void CommandPaletteWindow_ContentRendered(object? sender, EventArgs e)
    {
        FitInsideOwner();
        _searchBox.Focus();
        Keyboard.Focus(_searchBox);
    }

    private void FitInsideOwner()
    {
        if (Owner is not Window owner || owner.ActualWidth <= 0 || owner.ActualHeight <= 0)
        {
            return;
        }

        Width = Math.Min(400, Math.Max(320, owner.ActualWidth - 32));
        Height = Math.Min(360, Math.Max(260, owner.ActualHeight - 32));
        Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
        Top = owner.Top + Math.Max(0, (owner.ActualHeight - Height) / 2);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshResults();
    }

    private void RefreshResults()
    {
        string query = _searchBox?.Text?.Trim() ?? string.Empty;
        IEnumerable<CommandPaletteItem> filtered = _allItems;
        if (!string.IsNullOrWhiteSpace(query))
        {
            string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(item => terms.All(term =>
                item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        _results.ItemsSource = filtered.Take(30).ToList();
        if (_results.Items.Count > 0)
        {
            _results.SelectedIndex = 0;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _results.Items.Count > 0)
        {
            _results.Focus();
            _results.SelectedIndex = Math.Max(0, _results.SelectedIndex);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            ExecuteSelected();
            e.Handled = true;
        }
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClosePalette();
            e.Handled = true;
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteSelected();
    }

    private void CommandPaletteWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClosePalette();
            e.Handled = true;
        }
    }

    private void CommandPaletteWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_isClosing)
        {
            ClosePalette();
        }
    }

    private void ClosePalette()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        Close();
    }

    private void ExecuteSelected()
    {
        if (_results.SelectedItem is not CommandPaletteItem item || _isClosing)
        {
            return;
        }

        Action execute = item.Execute;
        Dispatcher dispatcher = Owner?.Dispatcher ?? Application.Current.Dispatcher;
        Window? owner = Owner;

        ClosePalette();

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                execute();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    owner,
                    "The command could not be completed.\n\n" + ex.Message,
                    "Quick Command",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }));
    }
}
