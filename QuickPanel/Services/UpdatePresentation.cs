using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace QuickPanel.Services;

public enum UpdateVersionState
{
    UpdateAvailable,
    UpToDate,
    RunningNewerBuild
}

public enum UpdateFailureStage
{
    Check,
    Download,
    InstallPreparation
}

public static class UpdatePresentation
{
    public static UpdateVersionState GetVersionState(Version currentVersion, Version latestVersion)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(latestVersion);
        int comparison = currentVersion.CompareTo(latestVersion);
        return comparison < 0
            ? UpdateVersionState.UpdateAvailable
            : comparison == 0
                ? UpdateVersionState.UpToDate
                : UpdateVersionState.RunningNewerBuild;
    }

    public static string GetStatusMessage(Version currentVersion, Version latestVersion)
    {
        string current = UpdateService.FormatVersion(currentVersion);
        string latest = UpdateService.FormatVersion(latestVersion);
        return GetVersionState(currentVersion, latestVersion) switch
        {
            UpdateVersionState.UpdateAvailable =>
                "Update available. Current v" + current + "; latest v" + latest + ".",
            UpdateVersionState.UpToDate =>
                "Up to date. Current v" + current + "; latest v" + latest + ".",
            _ =>
                "Running a newer/local build. Current v" + current + "; latest release v" + latest + "."
        };
    }

    public static bool CanDownload(UpdateVersionState state, bool hasDownloadAsset)
    {
        return state == UpdateVersionState.UpdateAvailable && hasDownloadAsset;
    }

    public static string GetErrorMessage(Exception exception, UpdateFailureStage stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is UpdateException updateException)
        {
            return updateException.Code switch
            {
                UpdateErrorCode.InvalidManifestUrl => "Invalid manifest URL. Use HTTPS, or localhost HTTP for testing.",
                UpdateErrorCode.ManifestUnreachable => "Manifest unreachable. Check the URL and your internet connection.",
				UpdateErrorCode.GitHubReleaseUnreachable => "Could not read the public Quick Panel release. Check your internet connection and try again.",
                UpdateErrorCode.RateLimited => "GitHub update checks are temporarily rate-limited. Try again later.",
                UpdateErrorCode.CheckTimedOut => "Update check timed out. Check your connection and try again.",
                UpdateErrorCode.InvalidGitHubResponse => "GitHub sent an update response Quick Panel could not read. Try again later.",
                UpdateErrorCode.InvalidVersion => "Invalid latest version. Use a GitHub tag or version.json value like 2.4.10.",
                UpdateErrorCode.InvalidDownloadUrl => "Invalid download or release notes URL. Use HTTPS, or localhost HTTP for testing.",
                UpdateErrorCode.InvalidSha256 => "Invalid SHA256 in version.json. It must be exactly 64 hex characters.",
                UpdateErrorCode.Sha256Mismatch => "SHA256 mismatch. Download was deleted because it did not match version.json.",
                UpdateErrorCode.DownloadFailed => "Download failed. Check the download URL and try again.",
                _ => updateException.Message
            };
        }

        if (exception is TaskCanceledException)
        {
            return stage switch
            {
                UpdateFailureStage.Check => "Update check timed out.",
                UpdateFailureStage.Download => "Download timed out or was canceled.",
                _ => "Update preparation failed: operation timed out or was canceled."
            };
        }

        if (exception is HttpRequestException)
        {
            return stage switch
            {
                UpdateFailureStage.Check => "Manifest unreachable. Check the URL and your internet connection.",
                UpdateFailureStage.Download => "Download failed. Check the download URL and try again.",
                _ => "Update preparation failed: network request failed."
            };
        }

        string prefix = stage switch
        {
            UpdateFailureStage.Check => "Update check failed: ",
            UpdateFailureStage.Download => "Download failed: ",
            _ => "Update preparation failed: "
        };
        return prefix + exception.Message;
    }
}
