using System.Runtime.CompilerServices;
using System.Text.Json;
using QuickPanel.Models;
using QuickPanel.Services;

internal static class WebsiteIconCacheTests
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [ModuleInitializer]
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "QuickPanel.Tests", "website-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyAutomaticAndManualCompatibility();
            VerifyPersistentCollisionSafeCache(root);
            VerifyInvalidFaviconIsRejected(root);
            VerifyFallbackDescriptor();
            VerifyBrowserIntegrationInvariants();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyAutomaticAndManualCompatibility()
    {
        AiTab automatic = new()
        {
            Name = "Example",
            Url = "https://example.com",
            IsCustom = true
        };
        Need(!TabIconResolver.HasManualOverride(automatic),
            "New website tabs must default to automatic website-icon mode.");

        AiTab manual = new()
        {
            Name = "Example",
            Url = "https://example.com",
            Icon = "reddit",
            IsCustom = true
        };
        Need(TabIconResolver.HasManualOverride(manual),
            "An explicit saved icon must remain a manual override.");

        string legacyAutoJson = "{\"Name\":\"Legacy\",\"Url\":\"https://legacy.example\",\"IsCustom\":true}";
        AiTab? legacyAuto = JsonSerializer.Deserialize<AiTab>(legacyAutoJson);
        Need(legacyAuto != null && !TabIconResolver.HasManualOverride(legacyAuto),
            "Old saved tabs without Icon must migrate to automatic mode.");

        string legacyManualJson = "{\"Name\":\"Legacy\",\"Url\":\"https://legacy.example\",\"Icon\":\"C:\\\\icons\\\\legacy.svg\",\"IsCustom\":true}";
        AiTab? legacyManual = JsonSerializer.Deserialize<AiTab>(legacyManualJson);
        Need(legacyManual != null && TabIconResolver.HasManualOverride(legacyManual),
            "Old saved tabs with Icon must retain their manual override.");
    }

    private static void VerifyPersistentCollisionSafeCache(string root)
    {
        WebsiteIconCache cache = new(root);
        Uri app = new("https://app.example.com/inbox?view=all#ignored");
        Uri mail = new("https://mail.example.com/inbox?view=all");

        WebsiteIconCacheEntry? appEntry = cache.StoreAsync(app, new MemoryStream(ValidPng)).GetAwaiter().GetResult();
        WebsiteIconCacheEntry? mailEntry = cache.StoreAsync(mail, new MemoryStream(ValidPng)).GetAwaiter().GetResult();
        Need(appEntry != null && mailEntry != null, "Valid WebView2 PNG favicons were not cached.");
        Need(WebsiteIconCache.CreateIdentityKey(app) != WebsiteIconCache.CreateIdentityKey(mail),
            "Different hosts generated the same website-icon cache key.");

        WebsiteIconCache restarted = new(root);
        Need(restarted.TryGetCachedIcon(app, out string? restartedPath) && File.Exists(restartedPath),
            "Cached favicon did not survive a service restart.");
        Need(restarted.TryGetCachedIcon(new Uri("https://app.example.com/other"), out string? originPath) &&
             string.Equals(restartedPath, originPath, StringComparison.OrdinalIgnoreCase),
            "Same-origin navigation could not reuse the cached favicon.");

        Uri navigated = new("https://app.example.com/settings");
        WebsiteIconCacheEntry? navigatedEntry = restarted.StoreAsync(navigated, new MemoryStream(ValidPng)).GetAwaiter().GetResult();
        Need(navigatedEntry != null && restarted.TryGetCachedIcon(navigated, out _),
            "Navigation could not update the current page's favicon cache mapping.");

        WebsiteIconCache duplicate = new(root);
        WebsiteIconCacheEntry? duplicateEntry = duplicate.StoreAsync(app, new MemoryStream(ValidPng)).GetAwaiter().GetResult();
        Need(duplicateEntry != null &&
             string.Equals(appEntry!.FilePath, duplicateEntry.FilePath, StringComparison.OrdinalIgnoreCase),
            "Duplicate tabs did not share the same content-addressed cached favicon.");
    }

    private static void VerifyInvalidFaviconIsRejected(string root)
    {
        WebsiteIconCache cache = new(root);
        Uri invalidSite = new("https://invalid.example/");
        WebsiteIconCacheEntry? result = cache.StoreAsync(invalidSite, new MemoryStream([1, 2, 3, 4])).GetAwaiter().GetResult();
        Need(result == null && !cache.TryGetCachedIcon(invalidSite, out _),
            "Malformed favicon data entered the website-icon cache.");

        WebsiteIconCacheEntry? oversized = cache.StoreAsync(
            new Uri("https://oversized.example/"),
            new MemoryStream(new byte[WebsiteIconCache.MaxPngBytes + 1])).GetAwaiter().GetResult();
        Need(oversized == null, "An oversized favicon entered the website-icon cache.");

        byte[] excessiveDimensions = ValidPng.ToArray();
        excessiveDimensions[16] = 0;
        excessiveDimensions[17] = 0;
        excessiveDimensions[18] = 8;
        excessiveDimensions[19] = 0;
        WebsiteIconCacheEntry? huge = cache.StoreAsync(
            new Uri("https://huge.example/"),
            new MemoryStream(excessiveDimensions)).GetAwaiter().GetResult();
        Need(huge == null, "A favicon with excessive decoded dimensions entered the website-icon cache.");
    }

    private static void VerifyFallbackDescriptor()
    {
        AiTab noFavicon = new()
        {
            Name = "Quiet corner",
            Url = "https://quiet.example/",
            IsCustom = true
        };
        TabIconFallbackDescriptor fallback = TabIconResolver.CreateFallbackDescriptor(noFavicon);
        Need(fallback.Monogram == "Q" && fallback.BackgroundHex.StartsWith('#') && fallback.BackgroundHex.Length == 7,
            "A website with no favicon did not receive a deterministic styled monogram.");
    }

    private static void VerifyBrowserIntegrationInvariants()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string browserCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Views", "BrowserTabView.cs"));
        string iconServiceCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "TabIconService.cs"));
        string dataPathsCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "PortableDataPaths.cs"));
        string webViewEnvironmentCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "Services", "WebViewEnvironmentService.cs"));
        string mainWindowCode = File.ReadAllText(Path.Combine(repoRoot, "QuickPanel", "MainWindow.cs"));

        Need(browserCode.Contains("FaviconChanged +=", StringComparison.Ordinal) &&
             browserCode.Contains("GetFaviconAsync(CoreWebView2FaviconImageFormat.Png)", StringComparison.Ordinal) &&
             browserCode.Contains("FaviconChanged -=", StringComparison.Ordinal),
            "Browser tabs are not wired to current-page WebView2 favicon updates with clean disposal.");
        Need(browserCode.Contains("TabIconResolver.HasManualOverride(_tab)", StringComparison.Ordinal),
            "Automatic WebView2 favicon updates can overwrite a manual icon override.");
        Need(!iconServiceCode.Contains("google.com/s2/favicons", StringComparison.OrdinalIgnoreCase) &&
             !iconServiceCode.Contains("new Uri(siteUri, \"/favicon", StringComparison.OrdinalIgnoreCase),
            "Automatic icon lookup still leaks website names or assumes /favicon paths.");
        Need(iconServiceCode.Contains("ReadBoundedContentAsync", StringComparison.Ordinal) &&
			 iconServiceCode.Contains("TryReadCachedSvgAsync", StringComparison.Ordinal) &&
			 iconServiceCode.Contains("ContainsUnsafeCssReference", StringComparison.Ordinal) &&
			 iconServiceCode.Contains("target.StartsWith('#')", StringComparison.Ordinal) &&
             !iconServiceCode.Contains("ReadAsByteArrayAsync", StringComparison.Ordinal) &&
             iconServiceCode.Contains("DecodePixelWidth = WebsiteIconCache.MaxDimension", StringComparison.Ordinal),
            "Manual remote/local icon handling is not bounded before buffering or bitmap decoding.");
        Need(dataPathsCode.Contains("IconCache", StringComparison.Ordinal) &&
             dataPathsCode.Contains("Websites", StringComparison.Ordinal) &&
             dataPathsCode.Contains("WebView2Directory", StringComparison.Ordinal),
            "Website icon cache is not separate from WebView2 browsing data.");
        int clearStart = mainWindowCode.IndexOf("private async Task ClearSelectedBrowsingDataAsync()", StringComparison.Ordinal);
        int clearEnd = clearStart < 0
            ? -1
            : mainWindowCode.IndexOf("\n\tprivate ", clearStart + 1, StringComparison.Ordinal);
        string clearDataCode = clearStart >= 0 && clearEnd > clearStart
            ? mainWindowCode[clearStart..clearEnd]
            : string.Empty;
        Need(clearDataCode.Contains("ClearAllBrowsingDataAsync", StringComparison.Ordinal) &&
             !clearDataCode.Contains("IconCache", StringComparison.Ordinal) &&
             !clearDataCode.Contains("_settingsService", StringComparison.Ordinal),
            "Clearing WebView2 browsing data can remove saved-tab icon configuration.");
        Need(webViewEnvironmentCode.Contains("WebView2RuntimeNotFoundException", StringComparison.Ordinal) &&
             webViewEnvironmentCode.Contains("Microsoft Edge WebView2 Runtime is required", StringComparison.Ordinal) &&
             webViewEnvironmentCode.Contains("developer.microsoft.com/microsoft-edge/webview2", StringComparison.Ordinal),
            "A missing WebView2 Runtime does not produce an actionable user-facing message.");
    }

    private static void Need(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
