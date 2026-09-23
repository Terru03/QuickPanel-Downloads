using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuickPanel.Services;

public sealed record CodexResetExpiry(
	string Label,
	DateTimeOffset? GrantedAt,
	string? GrantedRawValue,
	DateTimeOffset? ExpiresAt,
	string? ExpiresRawValue)
{
	public string? Title { get; init; }

	public string? Status { get; init; }

	public DateTimeOffset? RedeemedAt { get; init; }

	public string? RedeemedRawValue { get; init; }

	public DateTimeOffset? EstimatedExpiresAt =>
		ExpiresAt == null &&
		string.IsNullOrWhiteSpace(ExpiresRawValue) &&
		GrantedAt != null
			? GrantedAt.Value.AddDays(30)
			: null;
}

public sealed record CodexStateSummary(string Text, bool HasExpiryFields, IReadOnlyList<CodexResetExpiry>? ResetExpiries = null);

public sealed record CodexResetCreditsSummary(
	string Text,
	bool HasExpiryFields,
	int? AvailableCount,
	IReadOnlyList<CodexResetExpiry> ResetExpiries)
{
	public bool ApiSucceeded { get; init; }

	public string ApiStatus { get; init; } = "not checked";
}

public sealed record CodexUsageWindow(double? UsedPercent, TimeSpan? WindowLength, DateTimeOffset? ResetAt, TimeSpan? ResetAfter);

public sealed record CodexUsageCredits(bool? HasCredits, bool? Unlimited, bool? OverageLimitReached, string? Balance);

public sealed record CodexSpendControl(bool? Reached, string? IndividualLimit);

public sealed record CodexUsageMetrics(
	string? PlanType,
	bool? Allowed,
	bool? LimitReached,
	string? LimitReachedType,
	CodexUsageWindow? PrimaryWindow,
	CodexUsageWindow? SecondaryWindow,
	int? BankedResets,
	IReadOnlyList<CodexResetExpiry> ResetExpiries,
	CodexUsageCredits? Credits,
	CodexSpendControl? SpendControl);

public sealed record CodexUsageSnapshot(string UsageSummary, string StateSummary, bool HasExpiryFields, CodexUsageMetrics? Metrics)
{
	public bool ResetCreditsApiSucceeded { get; init; }

	public string ResetCreditsApiStatus { get; init; } = "not checked";
}

public sealed class CodexUsageException : Exception
{
	public CodexUsageException(string message, Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed record UsageField(string Path, string Name, string Text);

internal sealed record UsageFieldGroup(string Label, IReadOnlyList<string> Terms, string MissingText);

public sealed class CodexUsageService
{
	private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

	private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

	private const int MaxDisplayCharacters = 20000;

	private static readonly JsonSerializerOptions PrettyJsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private static readonly string[] ResetSearchTerms =
	{
		"reset",
		"expire",
		"expiry",
		"expires",
		"grant",
		"credit",
		"usage",
		"window",
		"bank",
		"remain",
		"weekly",
		"5h"
	};

	private static readonly UsageFieldGroup[] DisplayGroups =
	{
		new UsageFieldGroup(
			"Current 5h window usage/status",
			new[] { "5h", "five", "hour", "window", "current", "rolling" },
			"not exposed"),
		new UsageFieldGroup(
			"Weekly usage/status",
			new[] { "weekly", "week" },
			"not exposed"),
		new UsageFieldGroup(
			"Reset time",
			new[] { "reset" },
			"not exposed"),
		new UsageFieldGroup(
			"Reset credits",
			new[] { "bank", "banked" },
			"not exposed"),
		new UsageFieldGroup(
			"Grant / expiry fields",
			new[] { "grant", "expire", "expiry", "expires" },
			"API expiry: not exposed")
	};

	private readonly HttpClient _httpClient;

	private readonly string _authPath;

	private readonly string _statePath;

	public CodexUsageService()
		: this(new HttpClient { Timeout = TimeSpan.FromSeconds(12) }, GetDefaultAuthPath(), GetDefaultStatePath())
	{
	}

	public CodexUsageService(HttpClient httpClient, string authPath, string statePath)
	{
		_httpClient = httpClient;
		_authPath = authPath;
		_statePath = statePath;
	}

	public async Task<CodexUsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
	{
		string token = await ReadAccessTokenAsync(cancellationToken).ConfigureAwait(false);
		string usageJson = await FetchUsageJsonAsync(token, cancellationToken).ConfigureAwait(false);
		CodexResetCreditsSummary resetCreditsSummary = await FetchResetCreditsSummaryAsync(token, usageJson, cancellationToken).ConfigureAwait(false);
		string usageSummary = BuildUsageSummary(usageJson, resetCreditsSummary);
		CodexUsageMetrics? metrics = BuildUsageMetrics(usageJson);
		metrics = MergeResetCredits(metrics, resetCreditsSummary);
		CodexStateSummary stateSummary = await ReadStateSummaryAsync(cancellationToken).ConfigureAwait(false);
		metrics = MergeStateResetExpiries(metrics, stateSummary.ResetExpiries);
		bool hasExpiryFields = resetCreditsSummary.HasExpiryFields || JsonHasExpiryField(usageJson) || stateSummary.HasExpiryFields;
		return new CodexUsageSnapshot(usageSummary, stateSummary.Text, hasExpiryFields, metrics)
		{
			ResetCreditsApiSucceeded = resetCreditsSummary.ApiSucceeded,
			ResetCreditsApiStatus = resetCreditsSummary.ApiStatus
		};
	}

	public static string? ExtractAccessToken(string authJson)
	{
		using JsonDocument document = JsonDocument.Parse(authJson);
		JsonElement root = document.RootElement;
		return ReadString(root, "tokens", "access_token") ??
			ReadString(root, "tokens", "accessToken") ??
			ReadString(root, "access_token") ??
			ReadString(root, "accessToken") ??
			ReadString(root, "oauth", "access_token") ??
			ReadString(root, "oauth", "accessToken") ??
			FindStringProperty(root, "access_token") ??
			FindStringProperty(root, "accessToken");
	}

	public static string? ExtractAccountId(string usageJson)
	{
		using JsonDocument document = JsonDocument.Parse(usageJson);
		JsonElement root = document.RootElement;
		return ReadStringOrNumber(root, "account_id") ??
			ReadStringOrNumber(root, "accountId") ??
			ReadStringOrNumber(root, "user_id") ??
			ReadStringOrNumber(root, "userId") ??
			FindStringOrNumberProperty(root, "account_id") ??
			FindStringOrNumberProperty(root, "accountId") ??
			FindStringOrNumberProperty(root, "user_id") ??
			FindStringOrNumberProperty(root, "userId");
	}

	public static string BuildUsageSummary(string usageJson, string? resetCreditsSummary)
	{
		string usageSummary = BuildUsageSummaryCore(usageJson, suppressMissingApiExpiry: false);
		if (string.IsNullOrWhiteSpace(resetCreditsSummary))
		{
			return usageSummary;
		}
		return usageSummary.TrimEnd() + Environment.NewLine + Environment.NewLine + resetCreditsSummary.Trim();
	}

	public static string BuildUsageSummary(string usageJson, CodexResetCreditsSummary resetCreditsSummary)
	{
		string usageSummary = BuildUsageSummaryCore(usageJson, resetCreditsSummary.HasExpiryFields);
		if (string.IsNullOrWhiteSpace(resetCreditsSummary.Text))
		{
			return usageSummary;
		}
		return usageSummary.TrimEnd() + Environment.NewLine + Environment.NewLine + resetCreditsSummary.Text.Trim();
	}

	public static string BuildUsageSummary(string usageJson)
	{
		return BuildUsageSummaryCore(usageJson, suppressMissingApiExpiry: false);
	}

	private static string BuildUsageSummaryCore(string usageJson, bool suppressMissingApiExpiry)
	{
		if (string.IsNullOrWhiteSpace(usageJson))
		{
			return "Codex usage API returned no data.";
		}
		try
		{
			using JsonDocument document = JsonDocument.Parse(usageJson);
			string? whamDisplay = BuildWhamUsageDisplay(document.RootElement, suppressMissingApiExpiry);
			if (!string.IsNullOrWhiteSpace(whamDisplay))
			{
				return whamDisplay;
			}
			List<UsageField> fields = new List<UsageField>();
			bool hasExpiryFields = false;
			CollectUsageFields(document.RootElement, "$", fields, ref hasExpiryFields);
			string groupedDisplay = BuildGroupedDisplay(
				fields,
				"No reset or usage fields were exposed by the Codex usage API.",
				suppressMissingApiExpiry);
			return suppressMissingApiExpiry
				? groupedDisplay
				: AppendEstimatedResetExpiryDisplay(groupedDisplay, CollectResetExpiries(document.RootElement));
		}
		catch (JsonException)
		{
			return "Codex usage API returned data, but Quick Panel could not parse it.";
		}
	}

	public static CodexUsageMetrics? BuildUsageMetrics(string usageJson)
	{
		if (string.IsNullOrWhiteSpace(usageJson))
		{
			return null;
		}
		try
		{
			using JsonDocument document = JsonDocument.Parse(usageJson);
			return BuildWhamUsageMetrics(document.RootElement);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static CodexResetCreditsSummary BuildResetCreditsSummary(string resetCreditsJson)
	{
		if (string.IsNullOrWhiteSpace(resetCreditsJson))
		{
			return new CodexResetCreditsSummary("Reset credits API returned no data.", false, null, Array.Empty<CodexResetExpiry>())
			{
				ApiStatus = "empty response"
			};
		}
		try
		{
			using JsonDocument document = JsonDocument.Parse(resetCreditsJson);
			int? availableCount = FindFirstNonNegativeIntProperty(document.RootElement, "available_count");
			bool hasExpiryFields = HasExpiryField(document.RootElement);
			List<CodexResetExpiry> resetCredits = CollectResetCredits(document.RootElement);
			return new CodexResetCreditsSummary(
				BuildResetCreditsDisplay(availableCount, resetCredits, hasExpiryFields),
				hasExpiryFields,
				availableCount,
				resetCredits)
			{
				ApiSucceeded = true,
				ApiStatus = "OK"
			};
		}
		catch (JsonException)
		{
			return new CodexResetCreditsSummary("Reset credits API returned data, but Quick Panel could not parse it.", false, null, Array.Empty<CodexResetExpiry>())
			{
				ApiStatus = "invalid JSON response"
			};
		}
	}

	public static CodexStateSummary BuildStateSummary(string stateJson)
	{
		using JsonDocument document = JsonDocument.Parse(stateJson);
		List<UsageField> fields = new List<UsageField>();
		bool hasExpiryFields = false;
		CollectUsageFields(document.RootElement, "$", fields, ref hasExpiryFields);
		List<CodexResetExpiry> resetExpiries = CollectResetExpiries(document.RootElement);
		if (fields.Count == 0)
		{
			return new CodexStateSummary(
				AppendEstimatedResetExpiryDisplay("No reset or usage fields found in the local Codex state file.", resetExpiries),
				false,
				resetExpiries);
		}
		return new CodexStateSummary(
			AppendEstimatedResetExpiryDisplay(
				BuildGroupedDisplay(fields, "No reset or usage fields found in the local Codex state file."),
				resetExpiries),
			hasExpiryFields,
			resetExpiries);
	}

	private static string GetDefaultAuthPath()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
	}

	private static string GetDefaultStatePath()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", ".codex-global-state.json");
	}

	private async Task<string> ReadAccessTokenAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_authPath))
		{
			throw new CodexUsageException("Codex login not found");
		}
		try
		{
			string authJson = await File.ReadAllTextAsync(_authPath, cancellationToken).ConfigureAwait(false);
			string? token = ExtractAccessToken(authJson);
			if (string.IsNullOrWhiteSpace(token))
			{
				throw new CodexUsageException("Codex login found, but access token was not exposed.");
			}
			return token;
		}
		catch (JsonException ex)
		{
			throw new CodexUsageException("Codex auth.json is not valid JSON.", ex);
		}
		catch (IOException ex)
		{
			throw new CodexUsageException("Could not read Codex auth.json.", ex);
		}
		catch (UnauthorizedAccessException ex)
		{
			throw new CodexUsageException("Quick Panel was not allowed to read Codex auth.json.", ex);
		}
	}

	private async Task<string> FetchUsageJsonAsync(string token, CancellationToken cancellationToken)
	{
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			if (!request.Headers.TryAddWithoutValidation("Content-Type", "application/json"))
			{
				request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
			}
			using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
				{
					throw new CodexUsageException("Codex usage API rejected the login. Sign in to Codex again, then refresh.");
				}
				throw new CodexUsageException("Codex usage API failed with HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ". Try again soon.");
			}
			return body;
		}
		catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			throw new CodexUsageException("Codex usage API timed out. Try again soon.", ex);
		}
		catch (HttpRequestException ex)
		{
			throw new CodexUsageException("Could not reach the Codex usage API. Check internet, then try again.", ex);
		}
	}

	private async Task<CodexResetCreditsSummary> FetchResetCreditsSummaryAsync(string token, string usageJson, CancellationToken cancellationToken)
	{
		string? accountId;
		try
		{
			accountId = ExtractAccountId(usageJson);
		}
		catch (JsonException)
		{
			return new CodexResetCreditsSummary("Reset credits API was not called because /wham/usage account data could not be parsed.", false, null, Array.Empty<CodexResetExpiry>())
			{
				ApiStatus = "account data could not be parsed"
			};
		}

		if (string.IsNullOrWhiteSpace(accountId))
		{
			return new CodexResetCreditsSummary("Reset credits API was not called because /wham/usage did not expose account_id or user_id.", false, null, Array.Empty<CodexResetExpiry>())
			{
				ApiStatus = "account id not exposed"
			};
		}

		try
		{
			string resetCreditsJson = await FetchResetCreditsJsonAsync(token, accountId, cancellationToken).ConfigureAwait(false);
			return BuildResetCreditsSummary(resetCreditsJson);
		}
		catch (CodexUsageException ex)
		{
			return new CodexResetCreditsSummary("Reset credits API: " + ex.Message, false, null, Array.Empty<CodexResetExpiry>())
			{
				ApiStatus = ex.Message
			};
		}
	}

	private async Task<string> FetchResetCreditsJsonAsync(string token, string accountId, CancellationToken cancellationToken)
	{
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ResetCreditsUrl);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
				{
					throw new CodexUsageException("rejected the login. Sign in to Codex again, then refresh.");
				}
				throw new CodexUsageException("failed with HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ". Try again soon.");
			}
			return body;
		}
		catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			throw new CodexUsageException("timed out. Try again soon.", ex);
		}
		catch (HttpRequestException ex)
		{
			throw new CodexUsageException("could not be reached. Check internet, then try again.", ex);
		}
	}

	private async Task<CodexStateSummary> ReadStateSummaryAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_statePath))
		{
			return new CodexStateSummary("State file not found at " + _statePath + ".", false);
		}
		try
		{
			string stateJson = await File.ReadAllTextAsync(_statePath, cancellationToken).ConfigureAwait(false);
			return BuildStateSummary(stateJson);
		}
		catch (JsonException ex)
		{
			return new CodexStateSummary("State file exists but could not be parsed: " + ex.Message, false);
		}
		catch (IOException ex)
		{
			return new CodexStateSummary("Could not read state file: " + ex.Message, false);
		}
		catch (UnauthorizedAccessException ex)
		{
			return new CodexStateSummary("Quick Panel was not allowed to read the state file: " + ex.Message, false);
		}
	}

	private static string? ReadString(JsonElement root, params string[] path)
	{
		JsonElement current = root;
		foreach (string segment in path)
		{
			if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
			{
				return null;
			}
		}
		return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
	}

	private static string? ReadStringOrNumber(JsonElement root, string propertyName)
	{
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out JsonElement value))
		{
			return null;
		}
		return ReadStringOrNumberValue(value);
	}

	private static string? ReadStringOrNumberValue(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.String)
		{
			return value.GetString();
		}
		if (value.ValueKind == JsonValueKind.Number)
		{
			return value.GetRawText();
		}
		return null;
	}

	private static string? FindStringProperty(JsonElement element, string propertyName)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.String)
					{
						return property.Value.GetString();
					}
					string? child = FindStringProperty(property.Value, propertyName);
					if (!string.IsNullOrWhiteSpace(child))
					{
						return child;
					}
				}
				break;
			case JsonValueKind.Array:
				foreach (JsonElement item in element.EnumerateArray())
				{
					string? child = FindStringProperty(item, propertyName);
					if (!string.IsNullOrWhiteSpace(child))
					{
						return child;
					}
				}
				break;
		}
		return null;
	}

	private static string? FindStringOrNumberProperty(JsonElement element, string propertyName)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (property.NameEquals(propertyName))
					{
						string? value = ReadStringOrNumberValue(property.Value);
						if (!string.IsNullOrWhiteSpace(value))
						{
							return value;
						}
					}
					string? child = FindStringOrNumberProperty(property.Value, propertyName);
					if (!string.IsNullOrWhiteSpace(child))
					{
						return child;
					}
				}
				break;
			case JsonValueKind.Array:
				foreach (JsonElement item in element.EnumerateArray())
				{
					string? child = FindStringOrNumberProperty(item, propertyName);
					if (!string.IsNullOrWhiteSpace(child))
					{
						return child;
					}
				}
				break;
		}
		return null;
	}

	private static string? BuildWhamUsageDisplay(JsonElement root, bool suppressMissingApiExpiry)
	{
		CodexUsageMetrics? metrics = BuildWhamUsageMetrics(root);
		if (metrics == null ||
			root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("rate_limit", out JsonElement rateLimit))
		{
			return null;
		}

		StringBuilder builder = new StringBuilder();
		builder.AppendLine("Codex usage API");
		AppendStringIfPresent(builder, "Plan", root, "plan_type");
		AppendBoolIfPresent(builder, "Allowed", rateLimit, "allowed");
		AppendBoolIfPresent(builder, "Limit reached", rateLimit, "limit_reached");
		AppendStringIfPresent(builder, "Limit reached type", root, "rate_limit_reached_type");

		builder.AppendLine();
		builder.AppendLine("Current 5h window usage/status:");
		if (TryGetObject(rateLimit, "primary_window", out JsonElement primaryWindow))
		{
			AppendWindow(builder, primaryWindow);
		}
		else
		{
			builder.AppendLine("  not exposed");
		}

		builder.AppendLine();
		builder.AppendLine("Weekly usage/status:");
		if (TryGetObject(rateLimit, "secondary_window", out JsonElement secondaryWindow))
		{
			AppendWindow(builder, secondaryWindow);
		}
		else
		{
			builder.AppendLine("  not exposed");
		}

		builder.AppendLine();
		builder.AppendLine("Reset credits:");
		if (TryGetObject(root, "rate_limit_reset_credits", out JsonElement resetCredits) &&
			TryGetNumber(resetCredits, "available_count", out double availableCount))
		{
			builder.AppendLine("  Available: " + availableCount.ToString("0", CultureInfo.InvariantCulture));
		}
		else
		{
			builder.AppendLine("  not exposed");
		}

		builder.AppendLine();
		builder.AppendLine("Grant / expiry fields:");
		List<UsageField> grantFields = new List<UsageField>();
		bool hasExpiryFields = false;
		CollectUsageFields(root, "$", grantFields, ref hasExpiryFields);
		List<UsageField> filteredGrantFields = grantFields
			.Where(field =>
				field.Path.Contains("grant", StringComparison.OrdinalIgnoreCase) ||
				field.Path.Contains("expire", StringComparison.OrdinalIgnoreCase) ||
				field.Path.Contains("expiry", StringComparison.OrdinalIgnoreCase) ||
				field.Path.Contains("expires", StringComparison.OrdinalIgnoreCase))
			.Take(12)
			.ToList();
		if (filteredGrantFields.Count == 0)
		{
			builder.AppendLine(suppressMissingApiExpiry ? "  see reset credits API" : "  API expiry: not exposed");
		}
		else
		{
			foreach (UsageField field in filteredGrantFields)
			{
				builder.AppendLine("  " + field.Text);
			}
			if (!hasExpiryFields && !suppressMissingApiExpiry)
			{
				builder.AppendLine("  API expiry: not exposed");
			}
		}
		if (!suppressMissingApiExpiry)
		{
			AppendEstimatedResetExpiryLines(builder, CollectResetExpiries(root), "  ");
		}

		builder.AppendLine();
		builder.AppendLine("Credits:");
		if (TryGetObject(root, "credits", out JsonElement credits))
		{
			AppendBoolIfPresent(builder, "  Has credits", credits, "has_credits");
			AppendBoolIfPresent(builder, "  Unlimited", credits, "unlimited");
			AppendBoolIfPresent(builder, "  Overage limit reached", credits, "overage_limit_reached");
			AppendStringIfPresent(builder, "  Balance", credits, "balance");
		}
		else
		{
			builder.AppendLine("  not exposed");
		}

		if (TryGetObject(root, "spend_control", out JsonElement spendControl))
		{
			builder.AppendLine();
			builder.AppendLine("Spend control:");
			AppendBoolIfPresent(builder, "  Reached", spendControl, "reached");
			AppendNumberOrNullIfPresent(builder, "  Individual limit", spendControl, "individual_limit");
		}

		return builder.ToString().TrimEnd();
	}

	private static CodexUsageMetrics? BuildWhamUsageMetrics(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("rate_limit", out JsonElement rateLimit) ||
			rateLimit.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		CodexUsageWindow? primaryWindow = TryGetObject(rateLimit, "primary_window", out JsonElement primary)
			? BuildWindowMetrics(primary)
			: null;
		CodexUsageWindow? secondaryWindow = TryGetObject(rateLimit, "secondary_window", out JsonElement secondary)
			? BuildWindowMetrics(secondary)
			: null;
		int? bankedResets = null;
		if (TryGetObject(root, "rate_limit_reset_credits", out JsonElement resetCredits) &&
			TryGetNumber(resetCredits, "available_count", out double availableCount))
		{
			bankedResets = Convert.ToInt32(Math.Max(0, Math.Floor(availableCount)));
		}

		CodexUsageCredits? credits = null;
		if (TryGetObject(root, "credits", out JsonElement creditsElement))
		{
			credits = new CodexUsageCredits(
				TryGetBoolean(creditsElement, "has_credits", out bool hasCredits) ? hasCredits : null,
				TryGetBoolean(creditsElement, "unlimited", out bool unlimited) ? unlimited : null,
				TryGetBoolean(creditsElement, "overage_limit_reached", out bool overageLimitReached) ? overageLimitReached : null,
				ReadSimpleValue(creditsElement, "balance"));
		}

		CodexSpendControl? spendControl = null;
		if (TryGetObject(root, "spend_control", out JsonElement spendElement))
		{
			spendControl = new CodexSpendControl(
				TryGetBoolean(spendElement, "reached", out bool reached) ? reached : null,
				ReadSimpleValue(spendElement, "individual_limit"));
		}

		return new CodexUsageMetrics(
			ReadSimpleValue(root, "plan_type"),
			TryGetBoolean(rateLimit, "allowed", out bool allowed) ? allowed : null,
			TryGetBoolean(rateLimit, "limit_reached", out bool limitReached) ? limitReached : null,
			ReadSimpleValue(root, "rate_limit_reached_type"),
			primaryWindow,
			secondaryWindow,
			bankedResets,
			CollectResetExpiries(root),
			credits,
			spendControl);
	}

	private static CodexUsageMetrics? MergeResetCredits(CodexUsageMetrics? metrics, CodexResetCreditsSummary resetCreditsSummary)
	{
		if (resetCreditsSummary.AvailableCount == null && resetCreditsSummary.ResetExpiries.Count == 0)
		{
			return metrics;
		}
		if (metrics == null)
		{
			return new CodexUsageMetrics(
				null,
				null,
				null,
				null,
				null,
				null,
				resetCreditsSummary.AvailableCount,
				resetCreditsSummary.ResetExpiries,
				null,
				null);
		}

		return metrics with
		{
			BankedResets = resetCreditsSummary.AvailableCount ?? metrics.BankedResets,
			ResetExpiries = MergeResetExpiryLists(metrics.ResetExpiries, resetCreditsSummary.ResetExpiries)
		};
	}

	private static CodexUsageMetrics? MergeStateResetExpiries(CodexUsageMetrics? metrics, IReadOnlyList<CodexResetExpiry>? stateExpiries)
	{
		if (stateExpiries == null || stateExpiries.Count == 0)
		{
			return metrics;
		}
		if (metrics == null)
		{
			return new CodexUsageMetrics(null, null, null, null, null, null, null, stateExpiries, null, null);
		}

		return metrics with { ResetExpiries = MergeResetExpiryLists(metrics.ResetExpiries, stateExpiries) };
	}

	private static IReadOnlyList<CodexResetExpiry> MergeResetExpiryLists(IReadOnlyList<CodexResetExpiry> existingExpiries, IReadOnlyList<CodexResetExpiry> additionalExpiries)
	{
		List<CodexResetExpiry> merged = existingExpiries.ToList();
		foreach (CodexResetExpiry expiry in additionalExpiries)
		{
			bool duplicate = merged.Any(existing =>
				existing.GrantedAt == expiry.GrantedAt &&
				string.Equals(existing.GrantedRawValue, expiry.GrantedRawValue, StringComparison.Ordinal) &&
				existing.ExpiresAt == expiry.ExpiresAt &&
				string.Equals(existing.ExpiresRawValue, expiry.ExpiresRawValue, StringComparison.Ordinal) &&
				string.Equals(existing.Title, expiry.Title, StringComparison.Ordinal) &&
				string.Equals(existing.Status, expiry.Status, StringComparison.Ordinal) &&
				existing.RedeemedAt == expiry.RedeemedAt &&
				string.Equals(existing.RedeemedRawValue, expiry.RedeemedRawValue, StringComparison.Ordinal));
			if (!duplicate)
			{
				merged.Add(expiry);
			}
		}
		return merged;
	}

	private static CodexUsageWindow BuildWindowMetrics(JsonElement window)
	{
		double? usedPercent = TryGetNumber(window, "used_percent", out double used) ? used : null;
		TimeSpan? windowLength = TryGetNumber(window, "limit_window_seconds", out double windowSeconds)
			? TimeSpan.FromSeconds(Math.Max(0, windowSeconds))
			: null;
		DateTimeOffset? resetAt = TryGetNumber(window, "reset_at", out double resetSeconds)
			? TryBuildUnixTime(resetSeconds)
			: null;
		TimeSpan? resetAfter = TryGetNumber(window, "reset_after_seconds", out double resetAfterSeconds)
			? TimeSpan.FromSeconds(Math.Max(0, resetAfterSeconds))
			: null;
		return new CodexUsageWindow(usedPercent, windowLength, resetAt, resetAfter);
	}

	private static List<CodexResetExpiry> CollectResetExpiries(JsonElement root)
	{
		List<CodexResetExpiry> expiries = new List<CodexResetExpiry>();
		CollectResetExpiries(root, "$", expiries);
		return expiries.Take(20).ToList();
	}

	private static string AppendEstimatedResetExpiryDisplay(string display, IReadOnlyList<CodexResetExpiry> expiries)
	{
		if (!expiries.Any(expiry => expiry.EstimatedExpiresAt != null))
		{
			return display;
		}

		StringBuilder builder = new StringBuilder(display.TrimEnd());
		if (builder.Length > 0)
		{
			builder.AppendLine();
			builder.AppendLine();
		}
		builder.AppendLine("Estimated reset expiry:");
		AppendEstimatedResetExpiryLines(builder, expiries, "  ");
		return Truncate(builder.ToString().TrimEnd(), MaxDisplayCharacters);
	}

	private static void AppendEstimatedResetExpiryLines(StringBuilder builder, IReadOnlyList<CodexResetExpiry> expiries, string indent)
	{
		foreach (CodexResetExpiry expiry in expiries.Where(expiry => expiry.EstimatedExpiresAt != null).Take(12))
		{
			builder.AppendLine(indent + expiry.Label + " - Estimated expiry: " +
				FormatShortDate(expiry.EstimatedExpiresAt) +
				" (grant + 30 days; estimated, not confirmed)");
		}
	}

	private static string BuildResetCreditsDisplay(int? availableCount, IReadOnlyList<CodexResetExpiry> resetCredits, bool hasExpiryFields)
	{
		StringBuilder builder = new StringBuilder();
		builder.AppendLine("Reset credits API");
		builder.AppendLine("Available: " + (availableCount == null ? "not exposed" : availableCount.Value.ToString(CultureInfo.InvariantCulture)));
		builder.AppendLine();
		builder.AppendLine("Reset credit details:");
		if (resetCredits.Count == 0)
		{
			builder.AppendLine("  no reset credits exposed");
			if (!hasExpiryFields)
			{
				builder.AppendLine("  API expiry: not exposed");
			}
			return builder.ToString().TrimEnd();
		}

		foreach (CodexResetExpiry resetCredit in resetCredits.Take(20))
		{
			AppendResetCreditDisplay(builder, resetCredit, "  ");
		}
		if (!hasExpiryFields)
		{
			builder.AppendLine("  API expiry: not exposed");
		}
		return builder.ToString().TrimEnd();
	}

	private static void AppendResetCreditDisplay(StringBuilder builder, CodexResetExpiry resetCredit, string indent)
	{
		builder.AppendLine(indent + resetCredit.Label + ":");
		if (!string.IsNullOrWhiteSpace(resetCredit.Title))
		{
			builder.AppendLine(indent + "  Title: " + resetCredit.Title);
		}
		if (!string.IsNullOrWhiteSpace(resetCredit.Status))
		{
			builder.AppendLine(indent + "  Status: " + resetCredit.Status);
		}
		if (resetCredit.GrantedAt != null)
		{
			builder.AppendLine(indent + "  Granted: " + FormatShortDate(resetCredit.GrantedAt));
		}
		else if (!string.IsNullOrWhiteSpace(resetCredit.GrantedRawValue))
		{
			builder.AppendLine(indent + "  Granted: " + resetCredit.GrantedRawValue);
		}
		if (resetCredit.ExpiresAt != null)
		{
			builder.AppendLine(indent + "  Expires: " + FormatShortDate(resetCredit.ExpiresAt));
		}
		else if (!string.IsNullOrWhiteSpace(resetCredit.ExpiresRawValue))
		{
			builder.AppendLine(indent + "  Expires: " + resetCredit.ExpiresRawValue);
		}
		else
		{
			builder.AppendLine(indent + "  API expiry: not exposed");
			if (resetCredit.EstimatedExpiresAt != null)
			{
				builder.AppendLine(indent + "  Estimated expiry: " + FormatShortDate(resetCredit.EstimatedExpiresAt) + " (grant + 30 days; estimated, not confirmed)");
			}
		}
		if (resetCredit.RedeemedAt != null)
		{
			builder.AppendLine(indent + "  Redeemed: " + FormatShortDate(resetCredit.RedeemedAt));
		}
		else if (!string.IsNullOrWhiteSpace(resetCredit.RedeemedRawValue))
		{
			builder.AppendLine(indent + "  Redeemed: " + resetCredit.RedeemedRawValue);
		}
	}

	private static List<CodexResetExpiry> CollectResetCredits(JsonElement root)
	{
		List<CodexResetExpiry> resetCredits = new List<CodexResetExpiry>();
		CollectResetCredits(root, "$", resetCredits);
		return resetCredits.Take(20).ToList();
	}

	private static void CollectResetCredits(JsonElement element, string path, List<CodexResetExpiry> resetCredits)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				bool addedResetCredit = TryBuildResetCreditFromObject(element, path, resetCredits.Count + 1, out CodexResetExpiry? resetCredit);
				if (addedResetCredit && resetCredit != null)
				{
					resetCredits.Add(resetCredit);
				}
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (IsSensitiveName(property.Name))
					{
						continue;
					}
					if (addedResetCredit && IsResetCreditDetailName(property.Name))
					{
						continue;
					}
					CollectResetCredits(property.Value, path + "." + property.Name, resetCredits);
				}
				break;
			case JsonValueKind.Array:
				int index = 0;
				foreach (JsonElement item in element.EnumerateArray())
				{
					CollectResetCredits(item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", resetCredits);
					index++;
				}
				break;
		}
	}

	private static bool TryBuildResetCreditFromObject(JsonElement element, string path, int fallbackIndex, out CodexResetExpiry? resetCredit)
	{
		resetCredit = null;
		JsonElement grantedValue = default;
		JsonElement expiresValue = default;
		JsonElement redeemedValue = default;
		bool hasGranted = false;
		bool hasExpires = false;
		bool hasRedeemed = false;
		string? title = null;
		string? status = null;

		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (IsSensitiveName(property.Name))
			{
				continue;
			}
			if (!hasGranted && IsGrantDateName(property.Name))
			{
				grantedValue = property.Value;
				hasGranted = true;
			}
			if (!hasExpires && IsExpiryName(property.Name))
			{
				expiresValue = property.Value;
				hasExpires = true;
			}
			if (!hasRedeemed && IsRedeemedDateName(property.Name))
			{
				redeemedValue = property.Value;
				hasRedeemed = true;
			}
			if (title == null && IsTitleName(property.Name))
			{
				title = ReadDisplayValue(property.Value);
			}
			if (status == null && IsStatusName(property.Name))
			{
				status = ReadDisplayValue(property.Value);
			}
		}

		if (!hasGranted && !hasExpires && !hasRedeemed)
		{
			return false;
		}

		resetCredit = BuildResetExpiry(path, hasGranted ? grantedValue : default, hasExpires ? expiresValue : default, fallbackIndex, assumeUtc: true) with
		{
			Title = title,
			Status = status,
			RedeemedAt = hasRedeemed ? TryReadDateTimeOffset(redeemedValue, assumeUtc: true) : null,
			RedeemedRawValue = hasRedeemed ? ReadDisplayValue(redeemedValue) : null
		};
		return true;
	}

	private static void CollectResetExpiries(JsonElement element, string path, List<CodexResetExpiry> expiries)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				bool addedObjectReset = TryBuildResetExpiryFromObject(element, path, expiries.Count + 1, out CodexResetExpiry? resetExpiry);
				if (addedObjectReset && resetExpiry != null)
				{
					expiries.Add(resetExpiry);
				}
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (IsSensitiveName(property.Name))
					{
						continue;
					}
					string propertyPath = path + "." + property.Name;
					if (addedObjectReset && (IsExpiryName(property.Name) || IsGrantDateName(property.Name)))
					{
						continue;
					}
					if (IsExpiryName(property.Name) && IsResetExpiryContext(propertyPath))
					{
						expiries.Add(BuildResetExpiry(propertyPath, default, property.Value, expiries.Count + 1));
					}
					CollectResetExpiries(property.Value, propertyPath, expiries);
				}
				break;
			case JsonValueKind.Array:
				int index = 0;
				foreach (JsonElement item in element.EnumerateArray())
				{
					CollectResetExpiries(item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", expiries);
					index++;
				}
				break;
		}
	}

	private static bool TryBuildResetExpiryFromObject(JsonElement element, string path, int fallbackIndex, out CodexResetExpiry? resetExpiry)
	{
		resetExpiry = null;
		if (!IsResetExpiryContext(path))
		{
			return false;
		}

		JsonElement grantedValue = default;
		JsonElement expiresValue = default;
		bool hasGranted = false;
		bool hasExpires = false;
		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (IsSensitiveName(property.Name))
			{
				continue;
			}
			if (!hasGranted && IsGrantDateName(property.Name))
			{
				grantedValue = property.Value;
				hasGranted = true;
			}
			if (!hasExpires && IsExpiryName(property.Name))
			{
				expiresValue = property.Value;
				hasExpires = true;
			}
		}
		if (!hasGranted && !hasExpires)
		{
			return false;
		}

		resetExpiry = BuildResetExpiry(path, hasGranted ? grantedValue : default, hasExpires ? expiresValue : default, fallbackIndex);
		return true;
	}

	private static CodexResetExpiry BuildResetExpiry(string path, JsonElement grantedValue, JsonElement expiresValue, int fallbackIndex, bool assumeUtc = false)
	{
		string label = BuildResetExpiryLabel(path, fallbackIndex);
		return new CodexResetExpiry(
			label,
			grantedValue.ValueKind == JsonValueKind.Undefined ? null : TryReadDateTimeOffset(grantedValue, assumeUtc),
			grantedValue.ValueKind == JsonValueKind.Undefined ? null : ReadDisplayValue(grantedValue),
			expiresValue.ValueKind == JsonValueKind.Undefined ? null : TryReadDateTimeOffset(expiresValue, assumeUtc),
			expiresValue.ValueKind == JsonValueKind.Undefined ? null : ReadDisplayValue(expiresValue));
	}

	private static string BuildResetExpiryLabel(string path, int fallbackIndex)
	{
		int close = path.LastIndexOf(']');
		int open = close >= 0 ? path.LastIndexOf('[', close) : -1;
		if (open >= 0 &&
			close > open &&
			int.TryParse(path.Substring(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
		{
			return "Reset credit " + (index + 1).ToString(CultureInfo.InvariantCulture);
		}
		return "Reset credit " + fallbackIndex.ToString(CultureInfo.InvariantCulture);
	}

	private static bool IsResetExpiryContext(string path)
	{
		return path.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
			path.Contains("grant", StringComparison.OrdinalIgnoreCase) ||
			path.Contains("bank", StringComparison.OrdinalIgnoreCase) ||
			path.Contains("credit", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsGrantDateName(string name)
	{
		return name.Contains("granted", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("grant_at", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("grant_time", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("granttime", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("grant_date", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("grantdate", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsRedeemedDateName(string name)
	{
		return name.Contains("redeemed", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("redeem_at", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("redeem_time", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("redeemtime", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("redeem_date", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("redeemdate", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsTitleName(string name)
	{
		return string.Equals(name, "title", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("reset_title", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsStatusName(string name)
	{
		return string.Equals(name, "status", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(name, "state", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsResetCreditDetailName(string name)
	{
		return IsGrantDateName(name) ||
			IsExpiryName(name) ||
			IsRedeemedDateName(name) ||
			IsTitleName(name) ||
			IsStatusName(name);
	}

	private static DateTimeOffset? TryReadDateTimeOffset(JsonElement value, bool assumeUtc = false)
	{
		if (value.ValueKind == JsonValueKind.String)
		{
			string? text = value.GetString();
			string[] formats =
			{
				"dd.MM.yyyy HH:mm:ss",
				"d.M.yyyy HH:mm:ss",
				"yyyy-MM-dd HH:mm:ss",
				"yyyy-MM-ddTHH:mm:ss",
				"yyyy-MM-ddTHH:mm:ss.fff",
				"yyyy-MM-ddTHH:mm:ssK",
				"yyyy-MM-ddTHH:mm:ss.fffK"
			};
			DateTimeStyles dateTimeStyle = assumeUtc ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
			if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, dateTimeStyle, out DateTimeOffset exact))
			{
				return exact;
			}
			if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, dateTimeStyle, out DateTime localExact))
			{
				return assumeUtc
					? new DateTimeOffset(DateTime.SpecifyKind(localExact, DateTimeKind.Utc))
					: new DateTimeOffset(localExact);
			}
			DateTimeStyles fallbackStyle = assumeUtc ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
			if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, fallbackStyle, out DateTimeOffset parsed) ||
				DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, fallbackStyle, out parsed))
			{
				return parsed;
			}
			return null;
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
		{
			double seconds = number > 100000000000 ? number / 1000.0 : number;
			return TryBuildUnixTime(seconds);
		}
		return null;
	}

	private static string? ReadDisplayValue(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.String)
		{
			return value.GetString();
		}
		if (value.ValueKind == JsonValueKind.Number ||
			value.ValueKind == JsonValueKind.True ||
			value.ValueKind == JsonValueKind.False)
		{
			return value.GetRawText();
		}
		return null;
	}

	private static void AppendWindow(StringBuilder builder, JsonElement window)
	{
		if (TryGetNumber(window, "used_percent", out double usedPercent))
		{
			builder.AppendLine("  Used: " + usedPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%");
		}
		else
		{
			builder.AppendLine("  Used: not exposed");
		}
		if (TryGetNumber(window, "limit_window_seconds", out double windowSeconds))
		{
			builder.AppendLine("  Window: " + FormatSeconds(windowSeconds));
		}
		if (TryGetNumber(window, "reset_after_seconds", out double resetAfterSeconds))
		{
			builder.AppendLine("  Reset after: " + FormatSeconds(resetAfterSeconds));
		}
		if (TryGetNumber(window, "reset_at", out double resetAt))
		{
			builder.AppendLine("  Reset at: " + FormatUnixSeconds(resetAt));
		}
	}

	private static void AppendStringIfPresent(StringBuilder builder, string label, JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement value))
		{
			return;
		}
		if (value.ValueKind == JsonValueKind.String)
		{
			builder.AppendLine(label + ": " + value.GetString());
		}
		else if (value.ValueKind != JsonValueKind.Null)
		{
			builder.AppendLine(label + ": " + FormatElementValue(value));
		}
	}

	private static void AppendBoolIfPresent(StringBuilder builder, string label, JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement value))
		{
			return;
		}
		if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
		{
			builder.AppendLine(label + ": " + (value.GetBoolean() ? "yes" : "no"));
		}
	}

	private static void AppendNumberOrNullIfPresent(StringBuilder builder, string label, JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement value))
		{
			return;
		}
		builder.AppendLine(label + ": " + (value.ValueKind == JsonValueKind.Null ? "not exposed" : FormatElementValue(value)));
	}

	private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object &&
			element.TryGetProperty(propertyName, out value) &&
			value.ValueKind == JsonValueKind.Object)
		{
			return true;
		}
		value = default;
		return false;
	}

	private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
	{
		value = 0;
		if (element.ValueKind != JsonValueKind.Object ||
			!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return false;
		}
		if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
		{
			return true;
		}
		if (property.ValueKind == JsonValueKind.String &&
			double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
		{
			return true;
		}
		return false;
	}

	private static int? FindFirstNonNegativeIntProperty(JsonElement element, string propertyName)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (IsSensitiveName(property.Name))
					{
						continue;
					}
					if (property.NameEquals(propertyName) && TryReadNonNegativeInt(property.Value, out int value))
					{
						return value;
					}
					int? childValue = FindFirstNonNegativeIntProperty(property.Value, propertyName);
					if (childValue != null)
					{
						return childValue;
					}
				}
				break;
			case JsonValueKind.Array:
				foreach (JsonElement item in element.EnumerateArray())
				{
					int? childValue = FindFirstNonNegativeIntProperty(item, propertyName);
					if (childValue != null)
					{
						return childValue;
					}
				}
				break;
		}
		return null;
	}

	private static bool TryReadNonNegativeInt(JsonElement element, out int value)
	{
		value = 0;
		double number;
		if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out number) ||
			element.ValueKind == JsonValueKind.String &&
			double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
		{
			value = Convert.ToInt32(Math.Max(0, Math.Floor(number)));
			return true;
		}
		return false;
	}

	private static bool TryGetBoolean(JsonElement element, string propertyName, out bool value)
	{
		value = false;
		if (element.ValueKind != JsonValueKind.Object ||
			!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return false;
		}
		if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
		{
			value = property.GetBoolean();
			return true;
		}
		if (property.ValueKind == JsonValueKind.String &&
			bool.TryParse(property.GetString(), out value))
		{
			return true;
		}
		return false;
	}

	private static string? ReadSimpleValue(JsonElement element, string propertyName)
	{
		if (element.ValueKind != JsonValueKind.Object ||
			!element.TryGetProperty(propertyName, out JsonElement property) ||
			property.ValueKind == JsonValueKind.Null)
		{
			return null;
		}
		if (property.ValueKind == JsonValueKind.String)
		{
			return property.GetString();
		}
		if (property.ValueKind == JsonValueKind.Number ||
			property.ValueKind == JsonValueKind.True ||
			property.ValueKind == JsonValueKind.False)
		{
			return FormatElementValue(property).Trim('"');
		}
		return null;
	}

	private static DateTimeOffset? TryBuildUnixTime(double seconds)
	{
		try
		{
			return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(seconds));
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
		catch (OverflowException)
		{
			return null;
		}
	}

	private static string FormatSeconds(double seconds)
	{
		TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
		if (span.TotalDays >= 1)
		{
			return Math.Floor(span.TotalDays).ToString("0", CultureInfo.InvariantCulture) + "d " + span.Hours.ToString(CultureInfo.InvariantCulture) + "h";
		}
		if (span.TotalHours >= 1)
		{
			return Math.Floor(span.TotalHours).ToString("0", CultureInfo.InvariantCulture) + "h " + span.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
		}
		return Math.Floor(span.TotalMinutes).ToString("0", CultureInfo.InvariantCulture) + "m";
	}

	private static string FormatUnixSeconds(double seconds)
	{
		try
		{
			DateTimeOffset? resetAt = TryBuildUnixTime(seconds);
			if (resetAt == null)
			{
				return seconds.ToString("0", CultureInfo.InvariantCulture);
			}
			return resetAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
		}
		catch (ArgumentOutOfRangeException)
		{
			return seconds.ToString("0", CultureInfo.InvariantCulture);
		}
		catch (OverflowException)
		{
			return seconds.ToString("0", CultureInfo.InvariantCulture);
		}
	}

	private static string FormatShortDate(DateTimeOffset? value)
	{
		return value?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "not set";
	}

	private static string BuildGroupedDisplay(IReadOnlyList<UsageField> fields, string emptyMessage, bool suppressMissingApiExpiry = false)
	{
		if (fields.Count == 0)
		{
			return emptyMessage;
		}
		StringBuilder builder = new StringBuilder();
		HashSet<int> usedIndexes = new HashSet<int>();
		foreach (UsageFieldGroup group in DisplayGroups)
		{
			builder.AppendLine(group.Label + ":");
			List<int> groupMatches = fields
				.Select((field, index) => new { field, index })
				.Where(item => group.Terms.Any(term => item.field.Path.Contains(term, StringComparison.OrdinalIgnoreCase)))
				.Select(item => item.index)
				.ToList();
			List<int> matches = groupMatches.Take(8).ToList();
			if (matches.Count == 0)
			{
				builder.AppendLine("  " + (suppressMissingApiExpiry && string.Equals(group.Label, "Grant / expiry fields", StringComparison.Ordinal)
					? "see reset credits API"
					: group.MissingText));
			}
			else
			{
				foreach (int index in matches)
				{
					usedIndexes.Add(index);
					builder.AppendLine("  " + fields[index].Text);
				}
				if (string.Equals(group.Label, "Grant / expiry fields", StringComparison.Ordinal) &&
					!suppressMissingApiExpiry &&
					!groupMatches.Any(index => IsExpiryName(fields[index].Name)))
				{
					builder.AppendLine("  API expiry: not exposed");
				}
			}
			builder.AppendLine();
		}
		List<UsageField> otherFields = fields
			.Select((field, index) => new { field, index })
			.Where(item => !usedIndexes.Contains(item.index))
			.Select(item => item.field)
			.Take(20)
			.ToList();
		if (otherFields.Count > 0)
		{
			builder.AppendLine("Other usage fields:");
			foreach (UsageField field in otherFields)
			{
				builder.AppendLine("  " + field.Text);
			}
		}
		return Truncate(builder.ToString().TrimEnd(), MaxDisplayCharacters);
	}

	private static string FormatJsonForDisplay(string json, int maxCharacters)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			string pretty = JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
			return Truncate(pretty, maxCharacters);
		}
		catch (JsonException)
		{
			return Truncate(json.Trim(), maxCharacters);
		}
	}

	private static bool JsonHasExpiryField(string json)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			return HasExpiryField(document.RootElement);
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static bool HasExpiryField(JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (IsExpiryName(property.Name) || HasExpiryField(property.Value))
					{
						return true;
					}
				}
				return false;
			case JsonValueKind.Array:
				return element.EnumerateArray().Any(HasExpiryField);
			default:
				return false;
		}
	}

	private static void CollectUsageFields(JsonElement element, string path, List<UsageField> matches, ref bool hasExpiryFields)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (JsonProperty property in element.EnumerateObject())
				{
					if (IsSensitiveName(property.Name))
					{
						continue;
					}
					string propertyPath = path + "." + property.Name;
					if (IsRelevantName(property.Name))
					{
						if (IsExpiryName(property.Name))
						{
							hasExpiryFields = true;
						}
						matches.Add(new UsageField(propertyPath, property.Name, propertyPath + " = " + FormatElementValue(property.Value)));
					}
					CollectUsageFields(property.Value, propertyPath, matches, ref hasExpiryFields);
				}
				break;
			case JsonValueKind.Array:
				int index = 0;
				foreach (JsonElement item in element.EnumerateArray())
				{
					CollectUsageFields(item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", matches, ref hasExpiryFields);
					index++;
				}
				break;
		}
	}

	private static bool IsRelevantName(string name)
	{
		return ResetSearchTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsExpiryName(string name)
	{
		return name.Contains("expire", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("expiry", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("expires", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsSensitiveName(string name)
	{
		return name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("session", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("jwt", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("bearer", StringComparison.OrdinalIgnoreCase);
	}

	private static string FormatElementValue(JsonElement element)
	{
		return element.ValueKind switch
		{
			JsonValueKind.String => Quote(Truncate(element.GetString() ?? string.Empty, 300)),
			JsonValueKind.Number => element.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			JsonValueKind.Null => "null",
			JsonValueKind.Object or JsonValueKind.Array => Truncate(CompactJson(element), 500),
			_ => Truncate(element.GetRawText(), 500)
		};
	}

	private static string CompactJson(JsonElement element)
	{
		using MemoryStream stream = new MemoryStream();
		using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
		{
			element.WriteTo(writer);
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static string Quote(string value)
	{
		return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	private static string Truncate(string value, int maxCharacters)
	{
		if (value.Length <= maxCharacters)
		{
			return value;
		}
		return value[..maxCharacters] + Environment.NewLine + "... truncated";
	}
}
