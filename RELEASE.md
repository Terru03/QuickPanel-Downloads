# Quick Panel release process

The version is stored in `Directory.Build.props`. Version 2.5.0 is an explicitly
unsigned public release with [documented verification limits](docs/release-status.md).

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

Without signing inputs this produces an **unsigned candidate**. For the approved
2.5.0 release, verify the exact package and publish it as a normal GitHub release
with an unsigned notice and fresh-account guidance. Do not claim full GUI acceptance or a trusted
signature. Keep candidates private until privacy and package checks pass.
The default `artifacts/release` output contains exactly:

- `QuickPanel-2.5.0-win-x64.zip`
- `version.json`
- `SHA256SUMS.txt`

Never upload verification reports, diagnostics, profiles, logs, development
builds, certificates, or files selected with a broad wildcard.

## Signing

Signing is optional for the unsigned release. For a future signed version,
use a production code-signing identity and verify the application and dedicated
updater. The existing certificate-store path accepts
`-SigningCertificateThumbprint` and optionally `-SignToolPath`. Keep keys and
certificates outside the repository. Signing precedes packaging and hashing.
A signature does not guarantee absence of reputation or antivirus warnings.

For personal-PC signing and a verified draft/publication handoff, follow
[Sign and publish](docs/sign-and-release.md). Keys stay on the signing PC.

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

Include genuine screenshots of the reviewed release version in the release body,
with an optional short demonstration. Follow the
[2.5.0 media checklist](docs/release-media-2.5.0.md) and keep raw captures and
private capture notes out of public uploads. Media files are reviewed separately
and linked from their committed source location; the binary asset set stays the
same.

After the applicable acceptance and privacy checks, publish only the reviewed
three-file set to this repository's Releases. For 2.5.0 use the title
`Quick Panel 2.5.0` and prominently state that the package is unsigned in the notes.
Publish as a normal latest release in both repositories. Fetch every public file
without credentials and compare hashes.
The complete updater GUI route remains unverified; preserve recovery material
and do not advertise a legacy upgrade until it is proven separately.

Both Actions workflows are manual-only. They build artifacts; they do not create
GitHub Releases or change repository visibility.
