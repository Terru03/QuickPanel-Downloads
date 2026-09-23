namespace QuickPanel.Updater;

public static class UpdateRetryPolicy
{
    private static readonly int[] DelaysMilliseconds = [100, 250, 500, 1000, 2000];

    public static void Execute(Action action, Action<int, Exception>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                attempt < DelaysMilliseconds.Length)
            {
                onRetry?.Invoke(attempt + 1, exception);
                Thread.Sleep(DelaysMilliseconds[attempt]);
            }
        }
    }
}
