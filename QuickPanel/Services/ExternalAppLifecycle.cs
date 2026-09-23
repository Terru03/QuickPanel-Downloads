namespace QuickPanel.Services;

public enum ExternalAppDockState
{
	Idle,
	Launching,
	Searching,
	Connected,
	ApplicationClosed,
	UnableToEmbed,
	Disconnected,
	ShuttingDown
}

public static class ExternalAppLifecycle
{
	public static bool CanTransition(ExternalAppDockState current, ExternalAppDockState next)
	{
		if (current == next)
		{
			return true;
		}
		if (current == ExternalAppDockState.ShuttingDown)
		{
			return false;
		}
		if (next == ExternalAppDockState.ShuttingDown)
		{
			return true;
		}
		return current switch
		{
			ExternalAppDockState.Idle => next is ExternalAppDockState.Launching or ExternalAppDockState.Searching or ExternalAppDockState.UnableToEmbed or ExternalAppDockState.Disconnected,
			ExternalAppDockState.Launching => next is ExternalAppDockState.Searching or ExternalAppDockState.UnableToEmbed or ExternalAppDockState.Disconnected,
			ExternalAppDockState.Searching => next is ExternalAppDockState.Launching or ExternalAppDockState.Connected or ExternalAppDockState.UnableToEmbed or ExternalAppDockState.Disconnected,
			ExternalAppDockState.Connected => next is ExternalAppDockState.ApplicationClosed or ExternalAppDockState.UnableToEmbed or ExternalAppDockState.Disconnected or ExternalAppDockState.Searching,
			ExternalAppDockState.ApplicationClosed => next is ExternalAppDockState.Launching or ExternalAppDockState.Searching or ExternalAppDockState.UnableToEmbed or ExternalAppDockState.Disconnected,
			ExternalAppDockState.UnableToEmbed => next is ExternalAppDockState.Launching or ExternalAppDockState.Searching or ExternalAppDockState.Disconnected,
			ExternalAppDockState.Disconnected => next is ExternalAppDockState.Launching or ExternalAppDockState.Searching or ExternalAppDockState.Connected or ExternalAppDockState.UnableToEmbed,
			_ => false
		};
	}
}

public sealed class ExternalAppDockStateChangedEventArgs : System.EventArgs
{
	public required ExternalAppDockState State { get; init; }

	public required string Message { get; init; }
}
