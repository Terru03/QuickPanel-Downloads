using System;
using System.Collections.Generic;
using System.Linq;
using QuickPanel.Models;

namespace QuickPanel.Services;

public static class ExternalAppIconCandidateResolver
{
	public static IReadOnlyList<string> BuildShellTargets(
		ExternalAppDefinition definition,
		string? runningProcessPath = null,
		string? resolvedAppUserModelId = null)
	{
		List<string> targets = [];
		Add(runningProcessPath);
		Add(Expand(definition.ExecutablePath));
		AddAppsFolder(resolvedAppUserModelId);
		AddAppsFolder(definition.AppUserModelId);
		foreach (string appUserModelId in definition.AlternativeAppUserModelIds)
		{
			AddAppsFolder(appUserModelId);
		}
		Add(Expand(definition.ShellLaunchTarget));
		Add(definition.UriLaunchCommand);
		return targets;

		void AddAppsFolder(string? appUserModelId)
		{
			if (!string.IsNullOrWhiteSpace(appUserModelId)) Add("shell:AppsFolder\\" + appUserModelId.Trim());
		}

		void Add(string? value)
		{
			if (string.IsNullOrWhiteSpace(value)) return;
			string target = value.Trim();
			if (!targets.Contains(target, StringComparer.OrdinalIgnoreCase)) targets.Add(target);
		}
	}

	private static string? Expand(string? value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value.Trim());
	}
}
