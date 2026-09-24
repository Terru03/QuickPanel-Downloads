# Quick Panel

**Your everyday tools, one shortcut away.**

> **Quick Panel 2.5.0 — unsigned release for Windows x64.**
> [Download the ZIP](https://github.com/Terru03/QuickPanel-Downloads/releases/download/v2.5.0/QuickPanel-2.5.0-win-x64.zip) ·
> [Release notes and checksums](https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v2.5.0)
> Windows may show an unknown-publisher warning or block this unsigned app.
> Start with a fresh Windows account; legacy upgrades are not yet approved.

## What is Quick Panel?

Quick Panel is a compact Windows workspace for frequently used websites,
separate browser profiles, system information, and supported desktop apps.
Press **Ctrl + Alt + G** to show or hide the panel.

Keep favorite websites together, separate work and personal browser sessions,
and bring useful tools into view from one place. Add your own websites; the
initial default tabs include several AI services.

## Screenshot or demo

![Quick Panel 2.5.0 with a signed-out website in its Windows panel](docs/media/2.5.0/quick-panel-2.5.0-workspace.jpg)

Actual 2.5.0 application captured in a disposable Windows VM. See the
[screenshot gallery](docs/media/2.5.0/README.md) for the Add tab dialog and browser
profile choices, and [release status](docs/release-status.md) for verification limits.

## Major features

- Website tabs with persistent WebView2 sessions, custom icons, zoom, pins,
  ordering, and closed-tab history.
- Named browser profiles for separate work and personal account sessions.
- Built-in Task Manager, hardware telemetry, Windows Helper, Trip Planner,
  and optional Codex Usage panels.
- Session docking for supported desktop windows, with a compatibility path
  for apps that cannot safely be hosted as child windows.
- Opt-in startup and updates with SHA-256 verification and guarded recovery.

Hardware metrics depend on available sensors and permissions. Docking support
varies by application; see [compatibility notes](EXTERNAL-APPS.md).

## Download and installation

Download `QuickPanel-2.5.0-win-x64.zip` and `SHA256SUMS.txt` from the
[2.5.0 release release](https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v2.5.0).
The package includes .NET and requires Microsoft Edge WebView2 Runtime.

Compare the ZIP's hash with the ZIP entry in `SHA256SUMS.txt`:

```powershell
Get-FileHash .\QuickPanel-2.5.0-win-x64.zip -Algorithm SHA256
```

Extract the complete ZIP into a dedicated folder, keep its files together, and
open `QuickPanel.exe`. This free release is unsigned; a checksum checks download
integrity and does not establish a trusted publisher identity.

Use a fresh Windows account or disposable VM for this release. Do not replace
an existing 2.4 installation or assume that extracting to another folder isolates
its profile. See the [known limitations](docs/release-status.md).

Developers can build using the commands below. Run development builds on a
**disposable Windows user or VM**: startup can migrate a legacy profile. Do not
use a daily-use profile for release testing.

## Keyboard shortcut

Use `Ctrl + Alt + G` to show or hide Quick Panel. Escape hides it.

- `Ctrl + T`: add a website tab.
- `Ctrl + Tab` / `Ctrl + Shift + Tab`: switch tabs.
- `Ctrl + 1` through `Ctrl + 9`: select a tab by position.
- `Ctrl + W` / `Ctrl + Shift + T`: close or restore a custom tab.
- `Ctrl + Plus`, `Ctrl + Minus`, `Ctrl + 0`: adjust or reset web zoom.
- `Ctrl + R` or `F5`: reload the selected web tab.

## Adding websites

Select **+**, enter a name and URL, and choose **Add tab**. Automatic icons come
from the loaded page and are cached locally. Manual icon choices override them.
Tabs with the same name or URL keep their own saved state.

## Browser profiles

Use Add/Edit dialogs or a tab context menu to select or create a named browser
profile. Tabs in the same profile share sessions; different profiles keep
browser sessions separate. The Default profile remains available.

Profiles organize accounts; they are not an operating-system security boundary.
See [privacy information](PRIVACY.md).

## Window docking

**Dock a window** selects a supported open window, app, or file. Session tabs
are temporary. Normal undocking or exit restores the original window state.

File Explorer opens the selected physical folder in a separate in-panel Shell
view without reparenting the original window. Elevated, protected, secure-desktop,
and other unsupported windows are excluded. See [EXTERNAL-APPS.md](EXTERNAL-APPS.md).

## Updates

The public updater checks this repository's latest GitHub release without
GitHub CLI or private credentials. Version 2.5.0 is also available as a direct
ZIP download. Publishing release metadata does not prove the complete old-version
GUI upgrade path.

The planned 2.4.13 compatibility bridge is still pending GUI acceptance.
Do not replace a legacy installation with this release. Retain existing
settings, browser profiles, and recovery material.

## Privacy and local data

Settings and browser state live under `%LOCALAPPDATA%\QuickPanel\data`.
Legacy `%LOCALAPPDATA%\AIQuickPanel\data` is retained during migration.

The app has no developer analytics endpoint. Loaded websites communicate with
their own providers. The optional Codex Usage feature reads local Codex
authentication state when refreshed. Read [PRIVACY.md](PRIVACY.md) for details.

Never upload browser profiles, cookies, authentication files, or unreviewed logs
to a public issue. Review screenshots and diagnostics before sharing.

## Troubleshooting

See [SUPPORT.md](SUPPORT.md) for setup and recovery. Report non-sensitive bugs
using this repository's [issue tracker](https://github.com/Terru03/QuickPanel-Downloads/issues).
Use [SECURITY.md](SECURITY.md) for confidential vulnerability reports.

## Development information

Quick Panel is a WPF application targeting .NET 8 for Windows. Install the .NET 8
SDK on Windows and run:

```powershell
dotnet restore .\QuickPanel.sln
dotnet build .\QuickPanel.sln --configuration Release --no-restore
dotnet run --project .\tests\QuickPanel.Tests.csproj --configuration Release --no-build
.\tests\profile-data-safety.tests.ps1
.\tests\public-release-safety.tests.ps1
```

Build workflows are manual-only and do not publish releases. See
[RELEASE.md](RELEASE.md) for packaging and [QA.md](QA.md) for acceptance checks.
This repository starts with a reviewed source snapshot; historical private
releases, diagnostic reports, and development history are not included.

The source is publicly viewable under the reserved-rights terms in [LICENSE](LICENSE).
**This is not an open-source license.** Third-party components retain their own
[licenses and notices](THIRD-PARTY-NOTICES.md).
