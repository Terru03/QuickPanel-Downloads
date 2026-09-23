using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using QuickPanel.Services;
using QuickPanel.Views;

namespace QuickPanel;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		if (e.Args.Length == 2 &&
			e.Args[0].Equals("--portable-update-worker", StringComparison.OrdinalIgnoreCase))
		{
			int exitCode;
			try
			{
				exitCode = PortableUpdateWorker.ApplyRequestFile(e.Args[1]);
			}
			catch (Exception exception)
			{
				PortableUpdateDiagnostics.WriteStartup(
					e.Args[1],
					"unhandled worker startup failure: " +
					exception.GetType().Name + ": " + exception.Message);
				exitCode = 1;
			}
			Shutdown(exitCode);
			return;
		}

		UpdateRecoveryLaunchResult recovery = UpdateRecoveryService.TryStartRecovery();
		if (recovery == UpdateRecoveryLaunchResult.RecoveryStarted)
		{
			Shutdown(0);
			return;
		}
		if (recovery == UpdateRecoveryLaunchResult.NeedsAttention)
		{
			base.OnStartup(e);
			MessageBox.Show(
				UpdateRecoveryService.StartupAttentionMessage ??
				"An interrupted update needs attention. Recovery files were preserved.",
				"Quick Panel update recovery",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
			Shutdown(-1);
			return;
		}

		base.OnStartup(e);
		Stopwatch migrationStopwatch = Stopwatch.StartNew();
		ProfileMigrationResult migrationResult = RunProfileMigration();
		migrationStopwatch.Stop();
		WriteMigrationDiagnostic(migrationResult, migrationStopwatch.Elapsed);
		if (migrationResult.Status is ProfileMigrationStatus.Ambiguous or
			ProfileMigrationStatus.Unsafe or
			ProfileMigrationStatus.Failed)
		{
			var recoveryWindow = new ProfileMigrationRecoveryWindow(
				migrationResult,
				PortableDataPaths.DataDirectory,
				PortableDataPaths.MigrationLogDirectory);
			recoveryWindow.ShowDialog();
			Shutdown(-1);
			return;
		}

		bool num = e.Args.Any((string argument) => argument.Equals("--startup", StringComparison.OrdinalIgnoreCase));
		MainWindow mainWindow = (MainWindow)(base.MainWindow = new MainWindow(num));
		if (num)
		{
			mainWindow.Opacity = 0.0;
			mainWindow.ShowActivated = false;
			mainWindow.ShowInTaskbar = false;
		}
		mainWindow.Show();
	}

	private static ProfileMigrationResult RunProfileMigration()
	{
		using var migrationMutex = new Mutex(initiallyOwned: false, "Local\\QuickPanel.ProfileMigration.v1");
		bool lockTaken;
		try
		{
			lockTaken = migrationMutex.WaitOne(TimeSpan.FromSeconds(2));
		}
		catch (AbandonedMutexException)
		{
			lockTaken = true;
		}

		if (!lockTaken)
		{
			return new ProfileMigrationResult(
				ProfileMigrationStatus.Unsafe,
				null,
				null,
				"Another Quick Panel process is checking persistent profile data.");
		}

		try
		{
			var request = new ProfileMigrationRequest(
				PortableDataPaths.DataDirectory,
				PortableDataPaths.MaintenanceDirectory,
				PortableDataPaths.GetLegacyProfileCandidates(AppContext.BaseDirectory),
				ProfileMigrationProcessGuard.Check);
			return new ProfileMigrationService().Migrate(request);
		}
		finally
		{
			migrationMutex.ReleaseMutex();
		}
	}

	private static void WriteMigrationDiagnostic(ProfileMigrationResult result, TimeSpan elapsed)
	{
		try
		{
			Directory.CreateDirectory(PortableDataPaths.MigrationLogDirectory);
			string path = Path.Combine(
				PortableDataPaths.MigrationLogDirectory,
				$"profile-migration-{DateTime.UtcNow:yyyyMMdd}.log");
			string[] lines =
			[
				$"[{DateTimeOffset.UtcNow:O}] Status: {result.Status}",
				$"Canonical: {PortableDataPaths.DataDirectory}",
				$"Source: {result.SourceDirectory ?? "(none)"}",
				$"Recovery: {result.BackupDirectory ?? "(none)"}",
				$"Inspection: {result.InspectionMode}",
				$"Elapsed milliseconds: {elapsed.TotalMilliseconds:0}",
				$"Message: {result.Message}"
			];
			File.AppendAllLines(path, lines);
			if (result.CandidateAssessments is not null)
			{
				foreach (ProfileAssessment assessment in result.CandidateAssessments)
				{
					File.AppendAllText(
						path,
						$"Candidate metadata: tier={assessment.DurableTier}; settings-items={assessment.SettingsItemCount}; " +
						$"settings-bytes={assessment.SettingsBytes}; auxiliary-items={assessment.AuxiliaryStateCount}; " +
						$"cookie-databases={assessment.CookieDatabaseCount}; cookie-bytes={assessment.CookieBytes}; " +
						$"local-storage-files={assessment.LocalStorageFileCount}; indexed-db-files={assessment.IndexedDbFileCount}; " +
						$"webview-files={assessment.WebViewFileCount}; webview-bytes={assessment.WebViewBytes}{Environment.NewLine}");
				}
			}
		}
		catch
		{
			// Diagnostics must never turn a safe migration decision into a profile mutation.
		}
	}
}
