using System.IO;

namespace QuickPanel.Services;

public sealed record ExternalAppInstalledApp(
	string Name,
	string AppUserModelId,
	string PackageFamilyName,
	string LaunchTarget = "")
{
	public string ShellTarget => !string.IsNullOrWhiteSpace(AppUserModelId) && !Path.IsPathFullyQualified(AppUserModelId)
		? "shell:AppsFolder\\" + AppUserModelId
		: !string.IsNullOrWhiteSpace(LaunchTarget) ? LaunchTarget : AppUserModelId;
}
