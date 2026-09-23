using System.Diagnostics;
using System.IO;
using System.Windows;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class ProfileMigrationRecoveryWindow : Window
{
    private readonly string _diagnosticDirectory;

    public ProfileMigrationRecoveryWindow(
        ProfileMigrationResult result,
        string canonicalDirectory,
        string diagnosticDirectory)
    {
        InitializeComponent();
        _diagnosticDirectory = diagnosticDirectory;
        MessageText.Text = result.Message;
        CanonicalPathText.Text = canonicalDirectory;
        RecoveryPathText.Text = result.BackupDirectory ?? result.SourceDirectory ?? "No alternate profile was changed.";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        WindowAppearanceService.Apply(this);
        ExitButton.Focus();
    }

    private void OpenDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Directory.CreateDirectory(_diagnosticDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _diagnosticDirectory,
            UseShellExecute = true
        });
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }
}
