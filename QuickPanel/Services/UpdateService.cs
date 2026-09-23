using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace QuickPanel.Services;

public sealed class UpdateManifest
{
	public string? Latest { get; init; }

	public string? DownloadUrl { get; init; }

	public string? Sha256 { get; init; }

	public string? ReleaseNotesUrl { get; init; }

	public string? ReleaseNotes { get; init; }
}

public sealed record UpdateCheckResult(
	Version CurrentVersion,
	Version LatestVersion,
	bool IsUpdateAvailable,
	UpdateManifest Manifest);

public sealed record DownloadUpdateResult(string FilePath, bool Sha256Verified);

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
	public double? Percent => TotalBytes is > 0
		? Math.Min(100.0, BytesReceived * 100.0 / TotalBytes.Value)
		: null;
}

public enum UpdateErrorCode
{
	InvalidManifestUrl,
	ManifestUnreachable,
	GitHubReleaseUnreachable,
	RateLimited,
	CheckTimedOut,
	InvalidGitHubResponse,
	InvalidVersion,
	InvalidDownloadUrl,
	InvalidSha256,
	Sha256Mismatch,
	DownloadFailed
}

public sealed class UpdateException : Exception
{
	public UpdateException(UpdateErrorCode code, string message, Exception? innerException = null)
		: base(message, innerException)
	{
		Code = code;
	}

	public UpdateErrorCode Code { get; }
}

public sealed class UpdateService
{
	private const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/Terru03/QuickPanel-Downloads/releases/latest";

	private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(12);

	private static readonly Regex Sha256Regex = new Regex("^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	};

	private readonly HttpClient _httpClient;

	public UpdateService()
		: this(new HttpClient())
	{
	}

	public UpdateService(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public async Task<UpdateCheckResult> CheckForUpdatesAsync(string manifestUrl, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(manifestUrl))
		{
			return await CheckGitHubReleaseForUpdatesAsync(cancellationToken).ConfigureAwait(false);
		}
		return await CheckManifestForUpdatesAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
	}

	private async Task<UpdateCheckResult> CheckManifestForUpdatesAsync(string manifestUrl, CancellationToken cancellationToken)
	{
		Uri uri = ParseUpdateUri(manifestUrl, UpdateErrorCode.InvalidManifestUrl, "Use an HTTPS version.json URL. Localhost http is allowed for testing.");

		UpdateManifest manifest;
		using CancellationTokenSource timeout = CreateCheckTimeout(cancellationToken);
		try
		{
			using HttpResponseMessage response = await _httpClient.GetAsync(uri, timeout.Token).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, timeout.Token).ConfigureAwait(false) ??
				throw new UpdateException(UpdateErrorCode.ManifestUnreachable, "Update manifest is empty.");
		}
		catch (UpdateException)
		{
			throw;
		}
		catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			throw new UpdateException(UpdateErrorCode.CheckTimedOut, "Update check timed out.", ex);
		}
		catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is IOException)
		{
			throw new UpdateException(UpdateErrorCode.ManifestUnreachable, "Could not reach or read the update manifest.", ex);
		}
		if (!TryParseSemanticVersion(manifest.Latest, out Version? latestVersion))
		{
			throw new UpdateException(UpdateErrorCode.InvalidVersion, "Update manifest latest version is missing or invalid.");
		}
		ValidateManifestUrls(manifest);
		ValidateSha256OrThrow(manifest.Sha256);
		Version currentVersion = GetCurrentVersion();
		return new UpdateCheckResult(currentVersion, latestVersion!, latestVersion!.CompareTo(currentVersion) > 0, manifest);
	}

	private async Task<UpdateCheckResult> CheckGitHubReleaseForUpdatesAsync(CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CreateCheckTimeout(cancellationToken);
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, GitHubLatestReleaseApiUrl);
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
			request.Headers.UserAgent.ParseAdd("QuickPanel/" + AppVersion);
			using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			if (IsGitHubRateLimited(response))
			{
				throw new UpdateException(UpdateErrorCode.RateLimited, "GitHub update checks are temporarily rate-limited.");
			}
			if (!response.IsSuccessStatusCode)
			{
				throw new UpdateException(UpdateErrorCode.GitHubReleaseUnreachable, "Could not reach the latest GitHub release.");
			}
			await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			GitHubRelease release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, timeout.Token).ConfigureAwait(false) ??
				throw new UpdateException(UpdateErrorCode.InvalidGitHubResponse, "GitHub release response was empty.");
			return BuildGitHubReleaseResult(release);
		}
		catch (UpdateException)
		{
			throw;
		}
		catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			throw new UpdateException(UpdateErrorCode.CheckTimedOut, "Update check timed out.", ex);
		}
		catch (JsonException ex)
		{
			throw new UpdateException(UpdateErrorCode.InvalidGitHubResponse, "GitHub release response was not valid JSON.", ex);
		}
		catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
		{
			throw new UpdateException(UpdateErrorCode.GitHubReleaseUnreachable, "Could not reach the latest GitHub release.", ex);
		}
	}

	private static UpdateCheckResult BuildGitHubReleaseResult(GitHubRelease release)
	{
		if (!TryParseSemanticVersion(release.TagName, out Version? latestVersion))
		{
			throw new UpdateException(UpdateErrorCode.InvalidVersion, "GitHub release tag is missing or invalid.");
		}
		if (string.IsNullOrWhiteSpace(release.HtmlUrl) || !IsSecureUpdateUri(release.HtmlUrl))
		{
			throw new UpdateException(UpdateErrorCode.InvalidGitHubResponse, "GitHub release page URL is missing or invalid.");
		}
		GitHubAsset? downloadAsset = FindReleaseDownloadAsset(release);
		UpdateManifest manifest = new UpdateManifest
		{
			Latest = release.TagName,
			DownloadUrl = downloadAsset?.BrowserDownloadUrl,
			Sha256 = NormalizeAssetSha256(downloadAsset?.Digest),
			ReleaseNotesUrl = release.HtmlUrl,
			ReleaseNotes = NormalizeReleaseNotes(release.Body)
		};
		ValidateManifestUrls(manifest);
		ValidateSha256OrThrow(manifest.Sha256);
		Version currentVersion = GetCurrentVersion();
		return new UpdateCheckResult(currentVersion, latestVersion!, latestVersion!.CompareTo(currentVersion) > 0, manifest);
	}

	private static GitHubAsset? FindReleaseDownloadAsset(GitHubRelease release)
	{
		if (release.Assets == null || release.Assets.Count == 0)
		{
			return null;
		}
		return release.Assets
			.Where(IsDownloadableReleaseAsset)
			.OrderByDescending(item => item.Name?.Contains("QuickPanel", StringComparison.OrdinalIgnoreCase) == true)
			.ThenByDescending(item => item.Name?.Contains("win-x64", StringComparison.OrdinalIgnoreCase) == true)
			.ThenByDescending(item => item.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
			.FirstOrDefault();
	}

	private static bool IsDownloadableReleaseAsset(GitHubAsset asset)
	{
		if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) ||
			!IsSecureUpdateUri(asset.BrowserDownloadUrl))
		{
			return false;
		}
		return asset.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true ||
			string.Equals(asset.ContentType, "application/zip", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(asset.ContentType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);
	}

	public async Task<DownloadUpdateResult> DownloadUpdateAsync(
		UpdateManifest manifest,
		string? targetDirectory = null,
		IProgress<DownloadProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		Uri uri = ParseUpdateUri(manifest.DownloadUrl, UpdateErrorCode.InvalidDownloadUrl, "Update download URL must be HTTPS. Localhost http is allowed for testing.");
		ValidateSha256OrThrow(manifest.Sha256);
		string directory = string.IsNullOrWhiteSpace(targetDirectory) ? GetDefaultDownloadDirectory() : targetDirectory;
		Directory.CreateDirectory(directory);
		string fileName = Path.GetFileName(uri.LocalPath);
		if (string.IsNullOrWhiteSpace(fileName))
		{
			fileName = "QuickPanel-win-x64.zip";
		}
		string targetPath = GetUniqueFilePath(Path.Combine(directory, fileName));

		try
		{
			using HttpResponseMessage response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			long? totalBytes = response.Content.Headers.ContentLength;
			await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			await using FileStream destination = File.Create(targetPath);
			await CopyWithProgressAsync(source, destination, totalBytes, progress, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			DeleteQuietly(targetPath);
			throw;
		}
		catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is UnauthorizedAccessException)
		{
			DeleteQuietly(targetPath);
			throw new UpdateException(UpdateErrorCode.DownloadFailed, "Update download failed.", ex);
		}

		bool verified = false;
		if (!string.IsNullOrWhiteSpace(manifest.Sha256))
		{
			string expected = manifest.Sha256.Trim();
			string actual = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
			if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
			{
				DeleteQuietly(targetPath);
				throw new UpdateException(UpdateErrorCode.Sha256Mismatch, "Downloaded update SHA256 did not match manifest.");
			}
			verified = true;
		}

		return new DownloadUpdateResult(targetPath, verified);
	}

	public static bool IsSecureUpdateUri(string? value)
	{
		return TryParseUpdateUri(value, out _);
	}

	public static bool IsValidSha256(string? value)
	{
		return string.IsNullOrWhiteSpace(value) || Sha256Regex.IsMatch(value.Trim());
	}

	public static string GetCurrentVersionText()
	{
		string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
			Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
			"0.0.0";
		int metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
		return metadataIndex >= 0 ? version[..metadataIndex] : version;
	}

	public static string AppVersion => GetCurrentVersionText();

	public static Version GetCurrentVersion()
	{
		if (TryParseSemanticVersion(GetCurrentVersionText(), out Version? version))
		{
			return version!;
		}
		return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
	}

	public static string FormatVersion(Version version)
	{
		string text = version.Major.ToString(CultureInfo.InvariantCulture) + "." +
			version.Minor.ToString(CultureInfo.InvariantCulture) + "." +
			Math.Max(0, version.Build).ToString(CultureInfo.InvariantCulture);
		return version.Revision > 0
			? text + "." + version.Revision.ToString(CultureInfo.InvariantCulture)
			: text;
	}

	public static bool TryParseSemanticVersion(string? value, out Version? version)
	{
		version = null;
		string? text = value?.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
		{
			text = text[1..];
		}
		text = text.Split('+')[0].Split('-')[0];
		string[] parts = text.Split('.');
		if (parts.Length == 0 || parts.Length > 4)
		{
			return false;
		}
		int[] numbers = new[] { 0, 0, 0, 0 };
		for (int index = 0; index < parts.Length; index++)
		{
			if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] < 0)
			{
				return false;
			}
		}
		version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
		return true;
	}

	private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
	{
		await using FileStream stream = File.OpenRead(filePath);
		byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static Uri ParseUpdateUri(string? value, UpdateErrorCode code, string message)
	{
		if (TryParseUpdateUri(value, out Uri? uri))
		{
			return uri!;
		}
		throw new UpdateException(code, message);
	}

	private static CancellationTokenSource CreateCheckTimeout(CancellationToken cancellationToken)
	{
		CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(CheckTimeout);
		return timeout;
	}

	private static bool IsGitHubRateLimited(HttpResponseMessage response)
	{
		if (response.StatusCode == (HttpStatusCode)429)
		{
			return true;
		}
		return response.StatusCode == HttpStatusCode.Forbidden &&
			response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values) &&
			values.Any(value => value == "0");
	}

	internal static string? NormalizeReleaseNotes(string? releaseNotes)
	{
		string text = releaseNotes?.Trim() ?? string.Empty;
		if (text.StartsWith('\uFEFF'))
		{
			text = text[1..].TrimStart();
		}
		else if (text.StartsWith("ï»¿", StringComparison.Ordinal))
		{
			text = text[3..].TrimStart();
		}
		return string.IsNullOrWhiteSpace(text) ? null : text;
	}

	private static string? NormalizeAssetSha256(string? digest)
	{
		string? text = digest?.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		const string prefix = "sha256:";
		if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			text = text[prefix.Length..];
		}
		return IsValidSha256(text) ? text : null;
	}

	private static bool TryParseUpdateUri(string? value, out Uri? uri)
	{
		uri = null;
		if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? parsed))
		{
			return false;
		}
		if (parsed.Scheme == Uri.UriSchemeHttps ||
			(parsed.Scheme == Uri.UriSchemeHttp && IsLocalDevHost(parsed.Host)))
		{
			uri = parsed;
			return true;
		}
		return false;
	}

	private static bool IsLocalDevHost(string host)
	{
		return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
			host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
	}

	private static void ValidateManifestUrls(UpdateManifest manifest)
	{
		if (!string.IsNullOrWhiteSpace(manifest.DownloadUrl))
		{
			ParseUpdateUri(manifest.DownloadUrl, UpdateErrorCode.InvalidDownloadUrl, "Manifest downloadUrl must be HTTPS. Localhost http is allowed for testing.");
		}
		if (!string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl))
		{
			ParseUpdateUri(manifest.ReleaseNotesUrl, UpdateErrorCode.InvalidDownloadUrl, "Manifest releaseNotesUrl must be HTTPS. Localhost http is allowed for testing.");
		}
	}

	private static void ValidateSha256OrThrow(string? sha256)
	{
		if (!IsValidSha256(sha256))
		{
			throw new UpdateException(UpdateErrorCode.InvalidSha256, "Manifest sha256 must be exactly 64 hex characters.");
		}
	}

	private static async Task CopyWithProgressAsync(
		Stream source,
		Stream destination,
		long? totalBytes,
		IProgress<DownloadProgress>? progress,
		CancellationToken cancellationToken)
	{
		byte[] buffer = new byte[81920];
		long bytesReceived = 0;
		progress?.Report(new DownloadProgress(bytesReceived, totalBytes));
		int bytesRead;
		while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
		{
			await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
			bytesReceived += bytesRead;
			progress?.Report(new DownloadProgress(bytesReceived, totalBytes));
		}
	}

	private static void DeleteQuietly(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static string GetDefaultDownloadDirectory()
	{
		string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string downloads = Path.Combine(userProfile, "Downloads");
		return Directory.Exists(downloads) ? downloads : Path.GetTempPath();
	}


	private static string GetUniqueFilePath(string path)
	{
		if (!File.Exists(path))
		{
			return path;
		}
		string directory = Path.GetDirectoryName(path) ?? string.Empty;
		string name = Path.GetFileNameWithoutExtension(path);
		string extension = Path.GetExtension(path);
		string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
		string candidate = Path.Combine(directory, name + "-" + timestamp + extension);
		int suffix = 2;
		while (File.Exists(candidate))
		{
			candidate = Path.Combine(directory, name + "-" + timestamp + "-" + suffix.ToString(CultureInfo.InvariantCulture) + extension);
			suffix++;
		}
		return candidate;
	}

	private sealed class GitHubRelease
	{
		[JsonPropertyName("tag_name")]
		public string? TagName { get; init; }

		[JsonPropertyName("html_url")]
		public string? HtmlUrl { get; init; }

		[JsonPropertyName("body")]
		public string? Body { get; init; }

		[JsonPropertyName("assets")]
		public List<GitHubAsset>? Assets { get; init; }
	}

	private sealed class GitHubAsset
	{
		[JsonPropertyName("name")]
		public string? Name { get; init; }

		[JsonPropertyName("content_type")]
		public string? ContentType { get; init; }

		[JsonPropertyName("browser_download_url")]
		public string? BrowserDownloadUrl { get; init; }

		[JsonPropertyName("digest")]
		public string? Digest { get; init; }
	}
}
