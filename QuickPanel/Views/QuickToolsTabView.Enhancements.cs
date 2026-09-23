using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class QuickToolsTabView
{
    private const string EnhancedResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private static readonly HttpClient EnhancedResetCreditsHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private DispatcherTimer? _localUsageUiTimer;
    private bool _usageEnhancementsAttached;
    private bool _autoFitApplied;
    private double _heightBeforeCodexAutoFit;
    private DateTimeOffset? _nextResetCreditExpiry;

    [ModuleInitializer]
    internal static void RegisterQuickToolsEnhancements()
    {
        EventManager.RegisterClassHandler(
            typeof(QuickToolsTabView),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(QuickToolsEnhancements_Loaded));
        EventManager.RegisterClassHandler(
            typeof(QuickToolsTabView),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(QuickToolsEnhancements_Unloaded));
    }

    private static void QuickToolsEnhancements_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is QuickToolsTabView view)
        {
            view.AttachCodexUsageEnhancements();
        }
    }

    private static void QuickToolsEnhancements_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is QuickToolsTabView view)
        {
            view.DetachCodexUsageEnhancements();
        }
    }

    private void AttachCodexUsageEnhancements()
    {
        if (_mode != QuickToolsPanelMode.CodexUsage || _usageEnhancementsAttached)
        {
            return;
        }

        _usageEnhancementsAttached = true;
        RefreshCodexUsageButton.IsEnabledChanged += RefreshCodexUsageButton_IsEnabledChangedEnhanced;
        RefreshCodexUsageButton.Content = "Refresh";

        _localUsageUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _localUsageUiTimer.Tick += LocalUsageUiTimer_Tick;
        _localUsageUiTimer.Start();

        SimplifyAccountStatusDisplay();
        UpdateLocalResetCountdowns();
        ApplyResetCreditUrgencyVisual();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AutoFitCodexUsageWindow));
    }

    private void DetachCodexUsageEnhancements()
    {
        if (!_usageEnhancementsAttached)
        {
            return;
        }

        _usageEnhancementsAttached = false;
        RefreshCodexUsageButton.IsEnabledChanged -= RefreshCodexUsageButton_IsEnabledChangedEnhanced;
        if (_localUsageUiTimer != null)
        {
            _localUsageUiTimer.Stop();
            _localUsageUiTimer.Tick -= LocalUsageUiTimer_Tick;
            _localUsageUiTimer = null;
        }

        RestoreWindowHeightAfterCodexUsage();
    }

    private void RefreshCodexUsageButton_IsEnabledChangedEnhanced(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (RefreshCodexUsageButton.IsEnabled)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                RefreshCodexUsageButton.Content = "Refresh";
                SimplifyAccountStatusDisplay();
                UpdateLocalResetCountdowns();
                await RefreshResetCreditsFromTokenAsync();
                AutoFitCodexUsageWindow();
            }));
        }
    }

    private void LocalUsageUiTimer_Tick(object? sender, EventArgs e)
    {
        UpdateLocalResetCountdowns();
        ApplyResetCreditUrgencyVisual();
    }

    private async Task RefreshResetCreditsFromTokenAsync()
    {
        try
        {
            string authPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "auth.json");
            if (!File.Exists(authPath))
            {
                SetEnhancedResetApiStatus("Codex login not found");
                return;
            }

            string authJson = await File.ReadAllTextAsync(authPath);
            string? token = CodexUsageService.ExtractAccessToken(authJson);
            if (string.IsNullOrWhiteSpace(token))
            {
                SetEnhancedResetApiStatus("access token not exposed");
                return;
            }

            string? accountId = ExtractAccountIdFromAccessToken(token);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                SetEnhancedResetApiStatus("account id not exposed");
                return;
            }

            using HttpRequestMessage request = new(HttpMethod.Get, EnhancedResetCreditsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await EnhancedResetCreditsHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                SetEnhancedResetApiStatus("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                return;
            }

            CodexResetCreditsSummary summary = CodexUsageService.BuildResetCreditsSummary(body);
            RenderEnhancedResetCredits(summary);
        }
        catch (TaskCanceledException)
        {
            SetEnhancedResetApiStatus("timed out");
        }
        catch (HttpRequestException)
        {
            SetEnhancedResetApiStatus("network error");
        }
        catch (JsonException)
        {
            SetEnhancedResetApiStatus("invalid Codex token data");
        }
        catch (IOException)
        {
            SetEnhancedResetApiStatus("could not read Codex login");
        }
        catch (UnauthorizedAccessException)
        {
            SetEnhancedResetApiStatus("Codex login access denied");
        }
    }

    private void RenderEnhancedResetCredits(CodexResetCreditsSummary summary)
    {
        int? available = summary.AvailableCount;
        List<string> lines = new();
        if (available != null)
        {
            lines.Add("Available: " + available.Value.ToString(CultureInfo.CurrentCulture));
        }

        List<CodexResetExpiry> credits = summary.ResetExpiries
            .Where(expiry =>
                expiry.GrantedAt != null ||
                expiry.ExpiresAt != null ||
                !string.IsNullOrWhiteSpace(expiry.Status) ||
                !string.IsNullOrWhiteSpace(expiry.Title))
            .OrderByDescending(expiry => string.Equals(expiry.Status, "available", StringComparison.OrdinalIgnoreCase))
            .ThenBy(expiry => expiry.ExpiresAt ?? DateTimeOffset.MaxValue)
            .Take(Math.Max(available ?? 1, 1))
            .ToList();

        if (available == 0)
        {
            lines.Add("No reset credits available.");
        }
        else if (credits.Count == 0)
        {
            lines.Add("Expiry not exposed by reset API.");
        }
        else
        {
            foreach (CodexResetExpiry credit in credits)
            {
                if (!string.IsNullOrWhiteSpace(credit.Title))
                {
                    lines.Add(credit.Title);
                }
                if (credit.GrantedAt != null)
                {
                    lines.Add("Granted: " + FormatShortDate(credit.GrantedAt));
                }
                if (credit.ExpiresAt != null)
                {
                    lines.Add("Expires: " + FormatShortDate(credit.ExpiresAt));
                }
                else if (!string.IsNullOrWhiteSpace(credit.ExpiresRawValue))
                {
                    lines.Add("Expires: " + credit.ExpiresRawValue);
                }
                else
                {
                    lines.Add("Expiry not exposed by reset API.");
                }
            }
        }

        BankedStatusText.Text = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : summary.Text;
        _nextResetCreditExpiry = credits
            .Where(expiry => string.Equals(expiry.Status, "available", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(expiry.Status))
            .Where(expiry => expiry.ExpiresAt != null)
            .Select(expiry => expiry.ExpiresAt)
            .OrderBy(expiry => expiry)
            .FirstOrDefault();

        ApplyResetCreditUrgencyVisual();
        SetEnhancedResetApiStatus(summary.ApiSucceeded ? "OK" : summary.ApiStatus);
    }

    private void SetEnhancedResetApiStatus(string status)
    {
        string lastRefresh = _lastCodexUsageRefresh?.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "--:--:--";
        ResetActionStatusText.Text = "Usage API: OK | Reset credits API: " + status + " | Last refresh: " + lastRefresh;
    }

    private static string? ExtractAccountIdFromAccessToken(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        string payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        byte[] bytes = Convert.FromBase64String(payload);
        using JsonDocument document = JsonDocument.Parse(bytes);

        return FindJwtClaim(document.RootElement, "chatgpt_account_id") ??
               FindJwtClaim(document.RootElement, "account_id") ??
               FindJwtClaim(document.RootElement, "user_id");
    }

    private static string? FindJwtClaim(JsonElement element, string claimName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(claimName))
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        return property.Value.GetRawText();
                    }
                }

                string? nested = FindJwtClaim(property.Value, claimName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindJwtClaim(item, claimName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private void SimplifyAccountStatusDisplay()
    {
        string text = TokenStatusText.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Equals("waiting", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not exposed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dictionary<string, string> values = text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(new[] { ':' }, 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

        List<string> lines = new();
        if (values.TryGetValue("Plan", out string? plan))
        {
            lines.Add("Plan: " + plan);
        }

        bool? allowed = TryParseYesNo(values, "Usage allowed");
        bool? limitReached = TryParseYesNo(values, "Rate limit reached");
        if (limitReached == true)
        {
            lines.Add("Status: rate limited");
            if (values.TryGetValue("Limit type", out string? limitType) && !string.IsNullOrWhiteSpace(limitType))
            {
                lines.Add("Limit: " + limitType);
            }
        }
        else if (allowed == true)
        {
            lines.Add("Status: available");
        }
        else if (allowed == false)
        {
            lines.Add("Status: unavailable");
        }

        bool? unlimited = TryParseYesNo(values, "Paid usage unlimited");
        bool? hasPaidCredits = TryParseYesNo(values, "Paid credits");
        if (unlimited == true)
        {
            lines.Add("Paid usage: unlimited");
        }
        else if (hasPaidCredits == true)
        {
            string paidBalance = values.TryGetValue("Paid balance", out string? balance) && !string.IsNullOrWhiteSpace(balance)
                ? balance
                : "available";
            lines.Add("Paid credits: " + paidBalance);
        }

        if (TryParseYesNo(values, "Spend control reached") == true)
        {
            lines.Add("Spend control: reached");
        }

        if (lines.Count > 0)
        {
            TokenStatusText.Text = string.Join(Environment.NewLine, lines);
        }
    }

    private static bool? TryParseYesNo(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? value))
        {
            return null;
        }
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return null;
    }

    private void UpdateLocalResetCountdowns()
    {
        WindowDetailText.Text = RefreshRelativeResetText(WindowDetailText.Text);
        if (SecondaryWindowBorder.Visibility == Visibility.Visible)
        {
            WeeklyDetailText.Text = RefreshRelativeResetText(WeeklyDetailText.Text);
        }
    }

    private static string RefreshRelativeResetText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.StartsWith("Reset: ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int relativeStart = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (relativeStart <= 7 || !line.EndsWith(")", StringComparison.Ordinal))
            {
                continue;
            }

            string dateText = line.Substring(7, relativeStart - 7).Trim();
            if (!TryParseDisplayedResetDate(dateText, out DateTimeOffset resetAt))
            {
                continue;
            }

            lines[i] = "Reset: " + dateText + " (" + FormatRelative(resetAt, DateTimeOffset.Now) + ")";
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool TryParseDisplayedResetDate(string text, out DateTimeOffset value)
    {
        DateTime now = DateTime.Now;
        string withYear = text + " " + now.Year.ToString(CultureInfo.InvariantCulture);
        if (!DateTime.TryParseExact(
                withYear,
                "MMM d, HH:mm yyyy",
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime local))
        {
            value = default;
            return false;
        }

        if (local < now.AddMonths(-6))
        {
            local = local.AddYears(1);
        }
        else if (local > now.AddMonths(6))
        {
            local = local.AddYears(-1);
        }

        value = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return true;
    }

    private void ApplyResetCreditUrgencyVisual()
    {
        int available = 0;
        string firstLine = (BankedStatusText.Text ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        if (firstLine.StartsWith("Available:", StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(firstLine.Substring("Available:".Length).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out available);
        }

        if (available <= 0)
        {
            BankedStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(92, 76, 37));
            BankedStatusBorder.Background = new SolidColorBrush(Color.FromRgb(36, 31, 20));
            return;
        }

        if (_nextResetCreditExpiry == null)
        {
            BankedStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(184, 137, 52));
            BankedStatusBorder.Background = new SolidColorBrush(Color.FromRgb(45, 36, 18));
            return;
        }

        TimeSpan remaining = _nextResetCreditExpiry.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.FromDays(2))
        {
            BankedStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(225, 87, 87));
            BankedStatusBorder.Background = new SolidColorBrush(Color.FromRgb(48, 25, 25));
        }
        else if (remaining <= TimeSpan.FromDays(7))
        {
            BankedStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(238, 174, 82));
            BankedStatusBorder.Background = new SolidColorBrush(Color.FromRgb(48, 37, 20));
        }
        else
        {
            BankedStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(93, 154, 96));
            BankedStatusBorder.Background = new SolidColorBrush(Color.FromRgb(24, 39, 27));
        }
    }

    private void AutoFitCodexUsageWindow()
    {
        if (_mode != QuickToolsPanelMode.CodexUsage || !IsLoaded)
        {
            return;
        }

        Window? window = Window.GetWindow(this);
        if (window == null || window.WindowState != WindowState.Normal || ActualHeight <= 0)
        {
            return;
        }

        Point bottom = ResetActionStatusText.TranslatePoint(
            new Point(0, Math.Max(ResetActionStatusText.ActualHeight, 18)),
            this);
        double chromeHeight = Math.Max(0, window.ActualHeight - ActualHeight);
        double targetHeight = Math.Ceiling(chromeHeight + bottom.Y + 26);
        targetHeight = Math.Max(window.MinHeight, targetHeight);

        if (!_autoFitApplied)
        {
            _heightBeforeCodexAutoFit = window.Height;
            _autoFitApplied = true;
        }

        if (Math.Abs(window.Height - targetHeight) >= 24)
        {
            window.Height = targetHeight;
        }
    }

    private void RestoreWindowHeightAfterCodexUsage()
    {
        if (!_autoFitApplied)
        {
            return;
        }

        Window? window = Window.GetWindow(this);
        if (window != null && window.WindowState == WindowState.Normal && _heightBeforeCodexAutoFit > 0)
        {
            window.Height = _heightBeforeCodexAutoFit;
        }

        _autoFitApplied = false;
        _heightBeforeCodexAutoFit = 0;
    }
}
