using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class SettingsWindow
{
    private readonly RollbackAvailabilityService _rollbackAvailabilityService = new();
    private readonly LogService _rollbackLogService = new();
    private CancellationTokenSource? _rollbackAvailabilityCancellation;

    private async Task RefreshRollbackButtonAsync()
    {
        _rollbackAvailabilityCancellation?.Cancel();
        _rollbackAvailabilityCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _rollbackAvailabilityCancellation = cancellation;
        RollbackUpdateButton.IsEnabled = false;
        RollbackUpdateButton.Content = "Checking rollback...";
        RollbackUpdateButton.ToolTip = "Checking the available rollback copy.";

        Stopwatch stopwatch = Stopwatch.StartNew();
        string? version = null;
        Exception? failure = null;
        bool canceled = false;
        try
        {
            version = await _rollbackAvailabilityService.GetLatestVersionAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            stopwatch.Stop();
            string status = canceled || cancellation.IsCancellationRequested
                ? "canceled"
                : failure is null ? "completed" : "failed";
            _rollbackLogService.Info(
                "Settings rollback availability finished. elapsedMs=" +
                stopwatch.ElapsedMilliseconds +
                "; status=" + status +
                "; available=" + !string.IsNullOrWhiteSpace(version) +
                (failure is null ? string.Empty : "; error=" + failure.GetType().Name));
        }

        if (canceled || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (failure is not null)
        {
            RollbackUpdateButton.IsEnabled = false;
            RollbackUpdateButton.Content = "Rollback unavailable";
            RollbackUpdateButton.ToolTip = "Rollback availability could not be checked. See the application log.";
            return;
        }

        RollbackUpdateButton.IsEnabled = !string.IsNullOrWhiteSpace(version);
        RollbackUpdateButton.Content = version == null ? "Rollback unavailable" : "Rollback to " + version;
        RollbackUpdateButton.ToolTip = version == null
            ? "A rollback copy is created automatically before future updates."
            : "Restore application files from " + version + ". Portable data is preserved.";
    }

    private void CancelRollbackAvailabilityRefresh()
    {
        _rollbackAvailabilityCancellation?.Cancel();
        _rollbackAvailabilityCancellation?.Dispose();
        _rollbackAvailabilityCancellation = null;
    }

    private void RollbackUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        string? version = PortableUpdateRollback.GetLatestBackupVersion();
        if (version == null)
        {
            MessageBox.Show(this, "No rollback copy is available yet. Quick Panel now creates one before each update.", "Rollback unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            "Rollback Quick Panel to " + version + "?\n\nYour portable data folder and settings will not be replaced.",
            "Rollback Quick Panel",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            PortableUpdateRollback.PrepareAndLaunchLatest();
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException ||
            ex is System.IO.InvalidDataException || ex is UnauthorizedAccessException ||
            ex is System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "Could not start rollback: " + ex.Message, "Rollback failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
