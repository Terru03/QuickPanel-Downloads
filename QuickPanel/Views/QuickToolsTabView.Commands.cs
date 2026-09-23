using System.Threading.Tasks;
using System.Windows;

namespace QuickPanel.Views;

public partial class QuickToolsTabView
{
    public Task RefreshCodexUsageFromCommandAsync()
    {
        return RefreshCodexUsageAsync(force: true);
    }

    public void RestartExplorerFromCommand()
    {
        RestartExplorerButton_Click(this, new RoutedEventArgs());
    }

    public void ClearTempFromCommand()
    {
        ClearTempButton_Click(this, new RoutedEventArgs());
    }

    public void OpenStartupAppsFromCommand()
    {
        OpenStartupAppsButton_Click(this, new RoutedEventArgs());
    }

    public void TestConnectionFromCommand()
    {
        TestConnectionButton_Click(this, new RoutedEventArgs());
    }

    public void OpenPortableDataFromCommand()
    {
        OpenPortableDataButton_Click(this, new RoutedEventArgs());
    }
}
