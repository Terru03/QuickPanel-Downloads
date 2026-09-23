using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class SettingsWindow : Window, IComponentConnector
{
	private const int DwmWindowCornerPreference = 33;

	private const int DwmWindowCornerRound = 2;

	private readonly bool _initialStartWithWindows;

	private readonly bool _initialLaunchCodexAutomatically;

	private readonly bool _initialAlwaysOnTop;

	private readonly string _initialUpdateManifestUrl;

	private readonly bool _initialCheckForUpdatesOnStartup;

	public bool StartWithWindows { get; private set; }

	public bool LaunchCodexAutomatically { get; private set; }

	public bool AlwaysOnTop { get; private set; }

	public string UpdateManifestUrl { get; private set; }

	public bool CheckForUpdatesOnStartup { get; private set; }

	public string CurrentUpdateManifestUrl => UpdateManifestUrlTextBox.Text.Trim();

	public bool WasSaved { get; private set; }

	public event EventHandler? ClearBrowsingDataRequested;

	public event EventHandler? CheckForUpdatesRequested;

	public event EventHandler? DownloadUpdateRequested;

	public event EventHandler? CancelDownloadRequested;

	public event EventHandler? OpenReleaseNotesRequested;

	public event EventHandler? CopyDiagnosticsRequested;

	public event EventHandler? DisableStartupRequested;

	public event EventHandler? OpenSettingsFolderRequested;

	public event EventHandler? ExportSettingsBackupRequested;

	public event EventHandler? ImportSettingsBackupRequested;

	public event EventHandler? RestorePreviousBackupRequested;

	public event EventHandler? ResetLayoutRequested;

	public SettingsWindow(
		bool startWithWindows,
		bool launchCodexAutomatically,
		bool alwaysOnTop,
		string updateManifestUrl,
		bool checkForUpdatesOnStartup,
		string diagnosticsText)
	{
		InitializeComponent();
		_initialStartWithWindows = startWithWindows;
		_initialLaunchCodexAutomatically = launchCodexAutomatically;
		_initialAlwaysOnTop = alwaysOnTop;
		_initialUpdateManifestUrl = updateManifestUrl;
		_initialCheckForUpdatesOnStartup = checkForUpdatesOnStartup;
		UpdateManifestUrl = updateManifestUrl;
		CheckForUpdatesOnStartup = checkForUpdatesOnStartup;
		StartWithWindowsCheckBox.IsChecked = startWithWindows;
		LaunchCodexAutomaticallyCheckBox.IsChecked = launchCodexAutomatically;
		AlwaysOnTopCheckBox.IsChecked = alwaysOnTop;
		CheckUpdatesOnStartupCheckBox.IsChecked = checkForUpdatesOnStartup;
		UpdateManifestUrlTextBox.Text = updateManifestUrl;
		DiagnosticsText.Text = diagnosticsText;
		StartWithWindowsCheckBox.Checked += (_, _) => UpdateSaveButtonState();
		StartWithWindowsCheckBox.Unchecked += (_, _) => UpdateSaveButtonState();
		LaunchCodexAutomaticallyCheckBox.Checked += (_, _) => UpdateSaveButtonState();
		LaunchCodexAutomaticallyCheckBox.Unchecked += (_, _) => UpdateSaveButtonState();
		AlwaysOnTopCheckBox.Checked += (_, _) => UpdateSaveButtonState();
		AlwaysOnTopCheckBox.Unchecked += (_, _) => UpdateSaveButtonState();
		CheckUpdatesOnStartupCheckBox.Checked += (_, _) => UpdateSaveButtonState();
		CheckUpdatesOnStartupCheckBox.Unchecked += (_, _) => UpdateSaveButtonState();
		UpdateManifestUrlTextBox.TextChanged += (_, _) => UpdateSaveButtonState();
		string displayVersion = UpdateService.GetCurrentVersionText();
		VersionText.Text = "v" + displayVersion;
		CurrentVersionText.Text = "Current version v" + displayVersion;
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		nint windowHandle = new WindowInteropHelper(this).Handle;
		if (windowHandle != IntPtr.Zero)
		{
			int round = DwmWindowCornerRound;
			DwmSetWindowAttribute(windowHandle, DwmWindowCornerPreference, ref round, sizeof(int));
		}
		Activate();
	}

	private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
	{
	}

	private void UpdateSaveButtonState()
	{
		bool hasChanges = (AlwaysOnTopCheckBox.IsChecked == true) != _initialAlwaysOnTop ||
			(StartWithWindowsCheckBox.IsChecked == true) != _initialStartWithWindows ||
			(LaunchCodexAutomaticallyCheckBox.IsChecked == true) != _initialLaunchCodexAutomatically ||
			(CheckUpdatesOnStartupCheckBox.IsChecked == true) != _initialCheckForUpdatesOnStartup ||
			!string.Equals(CurrentUpdateManifestUrl, _initialUpdateManifestUrl, StringComparison.Ordinal);
		SaveButton.Content = hasChanges ? "Save" : "Done";
	}

	private void Window_Closed(object? sender, EventArgs e)
	{
	}

	private void SaveButton_Click(object sender, RoutedEventArgs e)
	{
		StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
		LaunchCodexAutomatically = LaunchCodexAutomaticallyCheckBox.IsChecked == true;
		AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
		UpdateManifestUrl = CurrentUpdateManifestUrl;
		CheckForUpdatesOnStartup = CheckUpdatesOnStartupCheckBox.IsChecked == true;
		WasSaved = true;
		Close();
	}

	public void SetUpdateStatus(string message, bool canDownload, bool canOpenReleaseNotes)
	{
		UpdateStatusText.Text = message;
		DownloadUpdateButton.IsEnabled = canDownload;
		ReleaseNotesButton.IsEnabled = canOpenReleaseNotes;
	}

	public void SetUpdateChecking(bool isChecking)
	{
		CheckForUpdatesButton.IsEnabled = !isChecking;
	}

	public void SetReleaseNotes(string? releaseNotes)
	{
		string? notes = releaseNotes?.Trim();
		if (string.IsNullOrWhiteSpace(notes))
		{
			ReleaseNotesBorder.Visibility = Visibility.Collapsed;
			ReleaseNotesText.Text = string.Empty;
			return;
		}
		const int maxLength = 1000;
		if (notes.Length > maxLength)
		{
			notes = notes[..maxLength].TrimEnd() + "...";
		}
		ReleaseNotesText.Text = notes;
		ReleaseNotesBorder.Visibility = Visibility.Visible;
	}

	public void SetUpdateAvailableBadge(bool isVisible)
	{
		UpdateBadge.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
	}

	public void SetDownloadProgress(double? percent, string message)
	{
		UpdateStatusText.Text = message;
		DownloadProgressBar.Visibility = Visibility.Visible;
		DownloadProgressBar.IsIndeterminate = percent == null;
		if (percent != null)
		{
			DownloadProgressBar.Value = Math.Clamp(percent.Value, 0.0, 100.0);
		}
	}

	public void SetDownloadActive(bool isActive)
	{
		CancelDownloadButton.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
		DownloadUpdateButton.IsEnabled = !isActive && DownloadUpdateButton.IsEnabled;
		if (!isActive)
		{
			DownloadProgressBar.Visibility = Visibility.Collapsed;
			DownloadProgressBar.IsIndeterminate = false;
			DownloadProgressBar.Value = 0.0;
		}
	}

	public void SetAdvancedStatus(string message)
	{
		AdvancedStatusText.Text = message;
	}

	public void SetDiagnosticsText(string diagnosticsText)
	{
		DiagnosticsText.Text = diagnosticsText;
	}

	public void SetStartupDisabled(string diagnosticsText)
	{
		StartWithWindowsCheckBox.IsChecked = false;
		SetDiagnosticsText(diagnosticsText);
		UpdateSaveButtonState();
	}

	private void AlwaysOnTopCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!AlwaysOnTopCheckBox.IsMouseOver)
		{
			AlwaysOnTopCheckBox.IsChecked = !(AlwaysOnTopCheckBox.IsChecked ?? false);
		}
	}

	private void StartWithWindowsCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!StartWithWindowsCheckBox.IsMouseOver)
		{
			StartWithWindowsCheckBox.IsChecked = !(StartWithWindowsCheckBox.IsChecked ?? false);
		}
	}

	private void LaunchCodexAutomaticallyCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!LaunchCodexAutomaticallyCheckBox.IsMouseOver)
		{
			LaunchCodexAutomaticallyCheckBox.IsChecked = !(LaunchCodexAutomaticallyCheckBox.IsChecked ?? false);
		}
	}

	private void CheckUpdatesOnStartupCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!CheckUpdatesOnStartupCheckBox.IsMouseOver)
		{
			CheckUpdatesOnStartupCheckBox.IsChecked = !(CheckUpdatesOnStartupCheckBox.IsChecked ?? false);
		}
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Close();
			e.Handled = true;
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void ClearBrowsingDataButton_Click(object sender, RoutedEventArgs e)
	{
		MessageBoxResult result = MessageBox.Show(
			this,
			"This clears cookies, cache, history, and site data for the browsing profile used by the selected web tab. Other profiles are not changed.",
			"Clear browsing data?",
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning);
		if (result == MessageBoxResult.Yes)
		{
			ClearBrowsingDataRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
	{
		CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
	}

	private void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
	{
		DownloadUpdateRequested?.Invoke(this, EventArgs.Empty);
	}

	private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
	{
		CancelDownloadRequested?.Invoke(this, EventArgs.Empty);
	}

	private void OpenReleaseNotesButton_Click(object sender, RoutedEventArgs e)
	{
		OpenReleaseNotesRequested?.Invoke(this, EventArgs.Empty);
	}

	private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
	{
		CopyDiagnosticsRequested?.Invoke(this, EventArgs.Empty);
	}

	private void DisableStartupButton_Click(object sender, RoutedEventArgs e)
	{
		DisableStartupRequested?.Invoke(this, EventArgs.Empty);
	}

	private void OpenSettingsFolderButton_Click(object sender, RoutedEventArgs e)
	{
		OpenSettingsFolderRequested?.Invoke(this, EventArgs.Empty);
	}

	private void ExportSettingsBackupButton_Click(object sender, RoutedEventArgs e)
	{
		ExportSettingsBackupRequested?.Invoke(this, EventArgs.Empty);
	}

	private void ImportSettingsBackupButton_Click(object sender, RoutedEventArgs e)
	{
		ImportSettingsBackupRequested?.Invoke(this, EventArgs.Empty);
	}

	private void RestorePreviousBackupButton_Click(object sender, RoutedEventArgs e)
	{
		RestorePreviousBackupRequested?.Invoke(this, EventArgs.Empty);
	}

	private void ResetLayoutButton_Click(object sender, RoutedEventArgs e)
	{
		ResetLayoutRequested?.Invoke(this, EventArgs.Empty);
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState == MouseButtonState.Pressed)
		{
			DragMove();
		}
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint windowHandle, int attribute, ref int attributeValue, int attributeSize);
}
