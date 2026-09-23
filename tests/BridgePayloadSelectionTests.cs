using QuickPanel.Services;

internal static class BridgePayloadSelectionTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "QuickPanelBridgePayloadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ExpectInvalid(root, "A payload without an application executable was accepted.");

            string legacy = Path.Combine(root, "AIQuickPanel.exe");
            File.WriteAllText(legacy, "legacy");
            Need(PortableUpdateInstaller.ResolvePayloadWorkerExecutable(root) == legacy,
                "The bridge cannot launch an existing-format payload.");

            File.Delete(legacy);
            string renamed = Path.Combine(root, "QuickPanel.exe");
            File.WriteAllText(renamed, "renamed");
            Need(PortableUpdateInstaller.ResolvePayloadWorkerExecutable(root) == renamed,
                "The bridge cannot launch the renamed public payload.");

            File.WriteAllText(legacy, "legacy");
            ExpectInvalid(root, "An ambiguous payload with both executable names was accepted.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectInvalid(string root, string message)
    {
        try
        {
            _ = PortableUpdateInstaller.ResolvePayloadWorkerExecutable(root);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Need(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
