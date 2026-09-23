using System;

namespace QuickPanel.Views;

public partial class SettingsWindow
{
    public void RequestUpdateCheck()
    {
        CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
    }
}
