using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace QuickPanel.Views;

public partial class ShellExplorerTabView : UserControl
{
    public ShellExplorerTabView(string folderPath)
    {
        InitializeComponent();
        Navigate(folderPath);
    }

    private void Navigate(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException("The selected Explorer folder is no longer available.");
        }
        AddressBox.Text = full;
        Browser.Navigate(new Uri(full, UriKind.Absolute));
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack) Browser.GoBack();
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        string? parent = Directory.GetParent(AddressBox.Text)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent)) Navigate(parent);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Browser.Refresh();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        try
        {
            Navigate(AddressBox.Text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    private void Browser_Navigated(object sender, NavigationEventArgs e)
    {
        if (e.Uri?.IsFile == true)
        {
            AddressBox.Text = e.Uri.LocalPath;
        }
        BackButton.IsEnabled = Browser.CanGoBack;
    }
}
