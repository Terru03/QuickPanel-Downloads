namespace QuickPanel.Services;

public enum TaskManagerProcessMergeAction
{
	Add,
	Update,
	Replace
}

public static class TaskManagerProcessListPolicy
{
	public static TaskManagerProcessMergeAction GetMergeAction(
		TaskManagerProcessSnapshot? existing,
		TaskManagerProcessSnapshot incoming)
	{
		if (existing == null)
		{
			return TaskManagerProcessMergeAction.Add;
		}

		return existing.ProcessId == incoming.ProcessId &&
			existing.Name.Equals(incoming.Name, System.StringComparison.OrdinalIgnoreCase) &&
			existing.StartTimeUtc == incoming.StartTimeUtc
			? TaskManagerProcessMergeAction.Update
			: TaskManagerProcessMergeAction.Replace;
	}
}
