using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace QuickPanel.Services;

public sealed record TaskManagerProcessSnapshot(
	int ProcessId,
	string Name,
	DateTime? StartTimeUtc,
	double CpuPercent,
	long WorkingSetBytes,
	string Status,
	string WindowTitle,
	bool CanTerminate);

public sealed class TaskManagerProcessService
{
	private sealed record CpuBaseline(string Name, DateTime? StartTimeUtc, TimeSpan ProcessorTime, DateTime CapturedAtUtc);

	private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"System",
		"Registry",
		"Idle",
		"smss",
		"csrss",
		"wininit",
		"services",
		"lsass",
		"winlogon",
		"fontdrvhost",
		"dwm"
	};

	private readonly Dictionary<int, CpuBaseline> _cpuBaselines = [];

	public IReadOnlyList<TaskManagerProcessSnapshot> Capture()
	{
		DateTime capturedAtUtc = DateTime.UtcNow;
		HashSet<int> activeProcessIds = [];
		List<TaskManagerProcessSnapshot> snapshots = [];
		foreach (Process process in Process.GetProcesses())
		{
			using (process)
			{
				try
				{
					int processId = process.Id;
					string name = process.ProcessName;
					DateTime? startTimeUtc = TryGetStartTimeUtc(process);
					TimeSpan processorTime = process.TotalProcessorTime;
					activeProcessIds.Add(processId);
					double cpuPercent = _cpuBaselines.TryGetValue(processId, out CpuBaseline? baseline) &&
						baseline.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
						baseline.StartTimeUtc == startTimeUtc
						? CalculateCpuPercent(baseline.ProcessorTime, processorTime, capturedAtUtc - baseline.CapturedAtUtc, Environment.ProcessorCount)
						: 0.0;
					_cpuBaselines[processId] = new CpuBaseline(name, startTimeUtc, processorTime, capturedAtUtc);
					long workingSetBytes = Math.Max(0, process.WorkingSet64);
					string windowTitle = TryGetWindowTitle(process);
					int sessionId = TryGetSessionId(process);
					string status = string.IsNullOrWhiteSpace(windowTitle)
						? sessionId == 0 ? "System" : "Background"
						: process.Responding ? "Running" : "Not responding";
					bool canTerminate = processId != Environment.ProcessId &&
						processId > 4 &&
						sessionId != 0 &&
						startTimeUtc.HasValue &&
						!ProtectedProcessNames.Contains(name);
					snapshots.Add(new TaskManagerProcessSnapshot(
						processId,
						name,
						startTimeUtc,
						cpuPercent,
						workingSetBytes,
						status,
						windowTitle,
						canTerminate));
				}
				catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
				{
					// Processes can exit or become inaccessible during enumeration.
				}
			}
		}
		foreach (int staleProcessId in _cpuBaselines.Keys.Where(processId => !activeProcessIds.Contains(processId)).ToList())
		{
			_cpuBaselines.Remove(staleProcessId);
		}
		return snapshots;
	}

	public bool TryTerminate(TaskManagerProcessSnapshot snapshot, out string error)
	{
		error = string.Empty;
		if (!snapshot.CanTerminate)
		{
			error = "Quick Panel protects this system process from termination.";
			return false;
		}
		try
		{
			using Process process = Process.GetProcessById(snapshot.ProcessId);
			DateTime currentStartTimeUtc = process.StartTime.ToUniversalTime();
			if (!MatchesConfirmedInstance(snapshot, process.ProcessName, currentStartTimeUtc))
			{
				error = "The selected process ended or changed before confirmation. Refresh and choose it again.";
				return false;
			}
			process.Kill(entireProcessTree: false);
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
		{
			error = exception is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
				? "Windows denied access. This process may require an elevated task manager."
				: exception.Message;
			return false;
		}
	}

	public bool TryGetExecutablePath(int processId, out string path, out string error)
	{
		path = string.Empty;
		error = string.Empty;
		try
		{
			using Process process = Process.GetProcessById(processId);
			path = process.MainModule?.FileName ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(path)) return true;
			error = "Windows did not expose an executable path for this process.";
			return false;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
		{
			error = exception is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }
				? "Windows denied access to this process path."
				: exception.Message;
			return false;
		}
	}

	internal static double CalculateCpuPercent(TimeSpan previousProcessorTime, TimeSpan currentProcessorTime, TimeSpan elapsed, int processorCount)
	{
		if (elapsed <= TimeSpan.Zero || processorCount <= 0 || currentProcessorTime < previousProcessorTime) return 0.0;
		double value = (currentProcessorTime - previousProcessorTime).TotalMilliseconds /
			elapsed.TotalMilliseconds /
			processorCount * 100.0;
		return Math.Clamp(Math.Round(value, 1), 0.0, 100.0);
	}

	internal static bool MatchesConfirmedInstance(
		TaskManagerProcessSnapshot snapshot,
		string currentName,
		DateTime currentStartTimeUtc)
	{
		return snapshot.StartTimeUtc.HasValue &&
			snapshot.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase) &&
			snapshot.StartTimeUtc.Value == currentStartTimeUtc;
	}

	private static string TryGetWindowTitle(Process process)
	{
		try
		{
			return process.MainWindowTitle ?? string.Empty;
		}
		catch (InvalidOperationException)
		{
			return string.Empty;
		}
	}

	private static int TryGetSessionId(Process process)
	{
		try
		{
			return process.SessionId;
		}
		catch (InvalidOperationException)
		{
			return -1;
		}
	}

	private static DateTime? TryGetStartTimeUtc(Process process)
	{
		try
		{
			return process.StartTime.ToUniversalTime();
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}
}
