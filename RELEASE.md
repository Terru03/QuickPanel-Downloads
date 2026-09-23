# Quick Panel release process

The version is stored in `Directory.Build.props`. This is the 2.5.0 source preview.
Public binary release is pending [these gates](docs/release-status.md).

## Build and test

Use Windows and the .NET 8 SDK. Run sequentially:

```powershell
dotnet restore .\QuickPanel.sln
dotnet build .\QuickPanel.sln --configuration Release --no-restore
dotnet run --project .\tests\QuickPanel.Tests.csproj --configuration Release --no-build
.\tests\profile-data-safety.tests.ps1
.\tests\public-release-safety.tests.ps1
.\tests\capture-updater-evidence.tests.ps1
```

Legacy authenticated repair tools and their tests remain in the private
verification checkout because they require historical private releases. Their
checks remain a separate release gate; public CI does not claim that coverage.
The app's public updater needs no private credentials.

## Package candidate

```powershell
.\scripts\publish-release.ps1 `
  -Version 2.5.0 `
  -DownloadBaseUrl https://github.com/Terru03/QuickPanel-Downloads/releases/download/v2.5.0 `
  -ReleaseNotesUrl https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v2.5.0
```

Without signing inputs this produces an **unsigned local candidate**, not an
approved public release. Keep it local or in private verification storage.
The default `artifacts/release` output contains exactly:

- `QuickPanel-2.5.0-win-x64.zip`
- `version.json`
- `SHA256SUMS.txt`

Never upload verification reports, diagnostics, profiles, logs, development
builds, certificates, or files selected with a broad wildcard.

## Signing

Use a production code-signing identity and verify the application and dedicated
updater. The existing certificate-store path accepts
`-SigningCertificateThumbprint` and optionally `-SignToolPath`. Keep keys and
certificates outside the repository. Signing precedes packaging and hashing.
A signature does not guarantee absence of reputation or antivirus warnings.

## Acceptance

Complete [QA.md](QA.md) on a disposable Windows user or VM. Confirm clean launch,
real GUI bridge updates, profile/session retention, startup shortcuts, interrupted
recovery, rollback, docking restoration, and the Windows/DPI environments tested.
Do not test migration against a daily-use browser profile.

`scripts/verify-release.ps1` supplements these checks using a prior source tag.
The initial public snapshot intentionally has no legacy tags. Maintainers run
cross-version verification in the retained private checkout with the exact
historical fixture and final ZIP. Do not import private history or publish its
reports to make that command run here.

## Publish and read back

After signing and acceptance, publish only the reviewed three-file set to this
repository's Releases. Fetch every file without credentials, compare hashes, and
confirm the updater checks/downloads without GitHub CLI. Preserve recovery
material until the upgrade is proven on a separate Windows user or machine.

Both Actions workflows are manual-only. They build artifacts; they do not create
GitHub Releases or change repository visibility.
