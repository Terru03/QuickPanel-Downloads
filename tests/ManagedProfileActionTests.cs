using System.Runtime.CompilerServices;

internal static class ManagedProfileActionTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string profileManagementPath = Path.Combine(repoRoot, "QuickPanel", "MainWindow.ProfileManagement.cs");
        string browserTabViewPath = Path.Combine(repoRoot, "QuickPanel", "Views", "BrowserTabView.cs");

        string profileManagementCode = File.ReadAllText(profileManagementPath);
        string browserTabViewCode = File.ReadAllText(browserTabViewPath);

        int switchStart = profileManagementCode.IndexOf("private void SwitchCurrentTabToManagedProfile", StringComparison.Ordinal);
        int switchEnd = profileManagementCode.IndexOf("private BrowserProfileDefinition? PromptAndCreateManagedProfile", switchStart, StringComparison.Ordinal);
        Need(switchStart >= 0 && switchEnd > switchStart,
            "Open-with-profile switch implementation is missing.");
        string switchCode = profileManagementCode[switchStart..switchEnd];
        Need(switchCode.Contains("sourceTabItem.Content = replacement", StringComparison.Ordinal) &&
            switchCode.Contains("new(updatedTab, normalizedUrl)", StringComparison.Ordinal) &&
            !switchCode.Contains("AddCustomTab(", StringComparison.Ordinal),
            "Open with profile must switch the existing tab instead of creating another tab.");

        int duplicateStart = profileManagementCode.IndexOf("private void DuplicateTabWithManagedProfile", StringComparison.Ordinal);
        int duplicateEnd = profileManagementCode.IndexOf("private void SwitchCurrentTabToManagedProfile", duplicateStart, StringComparison.Ordinal);
        Need(duplicateStart >= 0 && duplicateEnd > duplicateStart,
            "Duplicate-with-profile implementation is missing.");
        string duplicateCode = profileManagementCode[duplicateStart..duplicateEnd];
        Need(duplicateCode.Contains("AddCustomTab(", StringComparison.Ordinal),
            "Duplicate with profile must continue to create a new tab.");

        Need(profileManagementCode.Contains("switchCurrentTab: false", StringComparison.Ordinal) &&
            profileManagementCode.Contains("switchCurrentTab: true", StringComparison.Ordinal),
            "Managed profile menu actions are not routed to distinct behaviors.");

        Need(browserTabViewCode.Contains("BrowserTabView(AiTab tab, string? initialUrl = null)", StringComparison.Ordinal) &&
            browserTabViewCode.Contains("NavigateTo(_initialUrl)", StringComparison.Ordinal) &&
            browserTabViewCode.Contains("GoHome()", StringComparison.Ordinal) &&
            browserTabViewCode.Contains("NavigateTo(_tab.Url)", StringComparison.Ordinal),
            "Browser profile switching must preserve the current page while keeping the tab's saved home URL.");
    }

    private static void Need(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
