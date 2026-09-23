using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using QuickPanel.Models;

namespace QuickPanel.Services;

internal sealed record ExternalAppStartTarget(string Name, string AppUserModelId, string PackageFamilyName, int Score);

internal sealed record ExternalAppLaunchResult(
	bool Success,
	ExternalAppLaunchIdentity? Identity,
	uint OwnedProcessId,
	string Description,
	string? Error);

public sealed class ExternalAppLauncher
{
	private readonly Action<string>? _log;

	public ExternalAppLauncher(Action<string>? log = null)
	{
		_log = log;
	}

	internal async Task<ExternalAppLaunchResult> LaunchAsync(ExternalAppDefinition definition, CancellationToken cancellationToken)
	{
		if (definition.StartupDelayMs > 0)
		{
			await Task.Delay(definition.StartupDelayMs, cancellationToken);
		}
		ExternalAppLaunchKind kind = ResolveLaunchKind(definition);
		return kind switch
		{
			ExternalAppLaunchKind.StartApp => LaunchStartApp(definition),
			ExternalAppLaunchKind.AppUserModelId => LaunchAumid(definition.AppUserModelId, definition.LaunchArguments),
			ExternalAppLaunchKind.Executable => LaunchProcess(definition.ExecutablePath, definition.LaunchArguments, useShell: false, ownProcess: true),
			ExternalAppLaunchKind.Uri => LaunchProcess(definition.UriLaunchCommand, definition.LaunchArguments, useShell: true, ownProcess: false),
			ExternalAppLaunchKind.Shell => LaunchProcess(definition.ShellLaunchTarget, definition.LaunchArguments, useShell: true, ownProcess: false),
			_ => Failed("No launch target is configured.")
		};
	}

	internal IReadOnlyList<ExternalAppStartTarget> FindStartApps(ExternalAppDefinition definition)
	{
		return EnumerateInstalledApps()
			.Select(app => new ExternalAppStartTarget(
				app.Name,
				app.AppUserModelId,
				app.PackageFamilyName,
				ExternalAppStartTargetMatcher.Score(definition, app.Name, app.AppUserModelId, app.PackageFamilyName)))
			.Where(target => target.Score > 0 &&
				ExternalAppStartTargetMatcher.HasStableIdentity(definition, target.Name, target.AppUserModelId, target.PackageFamilyName))
			.OrderByDescending(target => target.Score)
			.ThenBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	internal IReadOnlyList<ExternalAppInstalledApp> EnumerateInstalledApps()
	{
		List<ExternalAppInstalledApp> apps = [];
		object? shell = null;
		object? folder = null;
		object? items = null;
		try
		{
			Type? shellType = Type.GetTypeFromProgID("Shell.Application");
			if (shellType == null)
			{
				return apps;
			}
			shell = Activator.CreateInstance(shellType);
			folder = InvokeCom(shell, "NameSpace", BindingFlags.InvokeMethod, "shell:AppsFolder");
			items = InvokeCom(folder, "Items", BindingFlags.InvokeMethod);
			int count = Convert.ToInt32(InvokeCom(items, "Count", BindingFlags.GetProperty) ?? 0);
			for (int index = 0; index < count; index++)
			{
				object? item = null;
				try
				{
					item = InvokeCom(items, "Item", BindingFlags.InvokeMethod, index);
					string name = Convert.ToString(InvokeCom(item, "Name", BindingFlags.GetProperty)) ?? string.Empty;
					string path = Convert.ToString(InvokeCom(item, "Path", BindingFlags.GetProperty)) ?? string.Empty;
					string appUserModelId = Convert.ToString(InvokeCom(item, "ExtendedProperty", BindingFlags.InvokeMethod, "System.AppUserModel.ID")) ?? string.Empty;
					string packageFamily = Convert.ToString(InvokeCom(item, "ExtendedProperty", BindingFlags.InvokeMethod, "System.AppUserModel.PackageFamilyName")) ?? string.Empty;
					if (!string.IsNullOrWhiteSpace(name) && (!string.IsNullOrWhiteSpace(appUserModelId) || !string.IsNullOrWhiteSpace(path)))
					{
						apps.Add(new ExternalAppInstalledApp(name.Trim(), appUserModelId.Trim(), packageFamily.Trim(), path.Trim()));
					}
				}
				finally
				{
					ReleaseCom(item);
				}
			}
		}
		catch (Exception exception) when (exception is COMException or InvalidOperationException or TargetInvocationException)
		{
			_log?.Invoke("Start app enumeration failed: " + exception.Message);
		}
		finally
		{
			ReleaseCom(items);
			ReleaseCom(folder);
			ReleaseCom(shell);
		}
		return apps
			.GroupBy(app => string.IsNullOrWhiteSpace(app.AppUserModelId) ? app.LaunchTarget : app.AppUserModelId, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}

	internal ExternalAppLaunchResult LaunchInstalledApp(ExternalAppInstalledApp app)
	{
		if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
		{
			ExternalAppLaunchResult activation = LaunchAumid(app.AppUserModelId, null, app.PackageFamilyName);
			if (activation.Success) return activation;
		}
		if (!string.IsNullOrWhiteSpace(app.LaunchTarget) && File.Exists(app.LaunchTarget))
		{
			ExternalAppLaunchResult directLaunch = LaunchProcess(app.LaunchTarget, null, useShell: true, ownProcess: false);
			if (directLaunch.Success) return directLaunch;
		}
		return LaunchProcess(
			app.ShellTarget,
			null,
			useShell: true,
			ownProcess: false,
			new ExternalAppLaunchIdentity(app.AppUserModelId, app.PackageFamilyName, 0));
	}

	private ExternalAppLaunchResult LaunchStartApp(ExternalAppDefinition definition)
	{
		ExternalAppStartTarget? target = FindStartApps(definition).FirstOrDefault();
		if (target != null)
		{
			ExternalAppLaunchResult activation = LaunchAumid(target.AppUserModelId, definition.LaunchArguments, target.PackageFamilyName);
			if (activation.Success)
			{
				return activation;
			}
			ExternalAppLaunchResult shellLaunch = LaunchProcess(
				"shell:AppsFolder\\" + target.AppUserModelId,
				null,
				useShell: true,
				ownProcess: false,
				new ExternalAppLaunchIdentity(target.AppUserModelId, target.PackageFamilyName, 0));
			if (shellLaunch.Success)
			{
				return shellLaunch;
			}
		}
		foreach (string shortcut in FindStartMenuShortcuts(definition.StartAppNameMatch ?? definition.DisplayName))
		{
			ExternalAppLaunchResult result = LaunchProcess(shortcut, null, useShell: true, ownProcess: false);
			if (result.Success)
			{
				return result;
			}
		}
		return Failed($"Windows Start apps has no launchable '{definition.StartAppNameMatch ?? definition.DisplayName}' entry.");
	}

	private static ExternalAppLaunchKind ResolveLaunchKind(ExternalAppDefinition definition)
	{
		if (definition.LaunchKind != ExternalAppLaunchKind.Auto)
		{
			return definition.LaunchKind;
		}
		if (!string.IsNullOrWhiteSpace(definition.AppUserModelId)) return ExternalAppLaunchKind.AppUserModelId;
		if (!string.IsNullOrWhiteSpace(definition.ExecutablePath)) return ExternalAppLaunchKind.Executable;
		if (!string.IsNullOrWhiteSpace(definition.UriLaunchCommand)) return ExternalAppLaunchKind.Uri;
		if (!string.IsNullOrWhiteSpace(definition.ShellLaunchTarget)) return ExternalAppLaunchKind.Shell;
		if (!string.IsNullOrWhiteSpace(definition.StartAppNameMatch)) return ExternalAppLaunchKind.StartApp;
		return ExternalAppLaunchKind.Auto;
	}

	private static ExternalAppLaunchResult LaunchAumid(string? appUserModelId, string? arguments, string packageFamily = "")
	{
		if (string.IsNullOrWhiteSpace(appUserModelId))
		{
			return Failed("AppUserModelID is empty.");
		}
		if (!ExternalAppNativeMethods.TryActivateApplication(appUserModelId, arguments, out uint processId, out string error))
		{
			return Failed(error);
		}
		return new ExternalAppLaunchResult(
			true,
			new ExternalAppLaunchIdentity(appUserModelId, packageFamily, processId),
			0,
			"ApplicationActivationManager:" + appUserModelId,
			null);
	}

	private static ExternalAppLaunchResult LaunchProcess(
		string? target,
		string? arguments,
		bool useShell,
		bool ownProcess,
		ExternalAppLaunchIdentity? identity = null)
	{
		if (string.IsNullOrWhiteSpace(target))
		{
			return Failed("Launch target is empty.");
		}
		try
		{
			Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = Environment.ExpandEnvironmentVariables(target),
				Arguments = arguments ?? string.Empty,
				UseShellExecute = useShell
			});
			uint processId = ownProcess && process != null ? (uint)process.Id : 0;
			process?.Dispose();
			return new ExternalAppLaunchResult(true, identity ?? new ExternalAppLaunchIdentity(string.Empty, string.Empty, processId), processId, (useShell ? "Shell:" : "Executable:") + target, null);
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
		{
			return Failed(exception.Message);
		}
	}

	private static IEnumerable<string> FindStartMenuShortcuts(string searchName)
	{
		foreach (string directory in new[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
		}.Where(Directory.Exists))
		{
			string[] files;
			try
			{
				files = Directory.GetFiles(directory, "*.lnk", SearchOption.AllDirectories);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}
			foreach (string file in files.Where(path => Path.GetFileNameWithoutExtension(path).Equals(searchName, StringComparison.OrdinalIgnoreCase)))
			{
				yield return file;
			}
		}
	}

	private static ExternalAppLaunchResult Failed(string error)
	{
		return new ExternalAppLaunchResult(false, null, 0, string.Empty, error);
	}

	private static object? InvokeCom(object? target, string memberName, BindingFlags flags, params object?[] arguments)
	{
		return target?.GetType().InvokeMember(memberName, flags, null, target, arguments);
	}

	private static void ReleaseCom(object? value)
	{
		if (value != null && Marshal.IsComObject(value))
		{
			Marshal.FinalReleaseComObject(value);
		}
	}
}
