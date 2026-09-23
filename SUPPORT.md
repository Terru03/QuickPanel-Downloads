# Quick Panel support

**Source preview:** no signed public binary has been released yet. Installation
instructions below describe the planned verified package. See
[release status](docs/release-status.md) before using a development build.

Report non-sensitive bugs at https://github.com/Terru03/QuickPanel-Downloads/issues.
For confidential vulnerability reports, use `SECURITY.md`.

## Install and start

Download `QuickPanel-2.5.0-win-x64.zip`, extract the entire ZIP to its own
folder, and run `QuickPanel.exe`. Do not run it inside the ZIP and do not copy
only the EXE or omit `UpdaterRuntime`. The package is self-contained, so users do not need to
install the .NET 8 Desktop Runtime or SDK.

Microsoft Edge WebView2 Runtime is still required for website tabs. Modern
Windows normally includes it. If Quick Panel reports that it is missing,
install the Evergreen WebView2 Runtime from Microsoft's official WebView2 page
and restart Quick Panel.

## SmartScreen and antivirus warnings

Unsigned development builds can still trigger Microsoft Defender SmartScreen or endpoint controls. A physical-machine release is endpoint-ready only when both `QuickPanel.exe` and `UpdaterRuntime\QuickPanel.Updater.exe` have valid trusted Authenticode signatures. Windows can show
**Windows protected your PC** because the binary has not built reputation. Verify
the ZIP SHA-256 against `SHA256SUMS.txt`, obtain the package from the expected
location, and scan it normally. Do not disable SmartScreen globally or use a
bypass script.

Bitdefender or another antivirus product may flag an uncommon unsigned build,
startup shortcut, process inspection, hardware monitoring, or native window
docking. Submit the exact ZIP to the vendor as a false positive and allowlist
only the verified install and data folders if your security policy permits it.
Do not exclude Downloads or an entire user profile.

## Startup shortcut problems

Startup is disabled by default. Enabling it creates a current-user shortcut in:

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
```

If startup is blocked, check Windows startup-app settings and antivirus history,
then disable and re-enable **Start Quick Panel with Windows**. Quick Panel starts
hidden at sign-in; use `Ctrl + Alt + G` or the tray icon to show it.

## Update-check or download failures

The default update source is the public Quick Panel downloads repository. Until
the first binary release is published, it has no version available to retrieve.
Updates should work without GitHub CLI or a GitHub sign-in. A custom HTTPS
`version.json` URL can be configured in Settings.

For an existing private installation that cannot reach the 2.4.13 bridge,
ask the maintainer for the verified private repair path. The 2.4.13 bridge is
required before applying the renamed package through the in-app updater.
Keep `%LOCALAPPDATA%\AIQuickPanel\data` and its sibling `data-maintenance`
directory while troubleshooting an upgrade.

For manifest updates, confirm the URL is HTTPS, `latest` is a valid version,
`sha256` is 64 hexadecimal characters, and the ZIP URL is reachable. A failed
hash check deletes the rejected download. Retry only after confirming the
publisher has replaced the manifest and ZIP consistently.

If Quick Panel reports that the updater worker did not acknowledge the request,
leave the app and profile topology unchanged and preserve these diagnostics:

```text
<resolved physical data directory>-maintenance\update-staging\<attempt>\update-startup.log
<resolved physical data directory>-maintenance\update-staging\<attempt>\update.log
<resolved physical data directory>-maintenance\update-logs\update-<attempt>.log
```

Released 2.4.7 and older workers used
`%TEMP%\QuickPanelUpdate\<attempt>` instead; preserve that legacy directory when
diagnosing an old attempt. New builds keep worker staging beside the durable
maintenance logs to avoid relying on temporary-directory execution.

The durable log records process IDs, application paths and versions, and update
stages, but not profile contents. On junction-based profiles the resolved
physical data directory can differ from `%LOCALAPPDATA%`; do not remove or
recreate the junction to make the paths look conventional. Preserve the request,
logs, downloaded ZIP, and rollback backup until the failure is diagnosed.

For a reproducible support capture from a source checkout, run
`scripts\capture-updater-evidence.ps1` with an output path outside the install,
profile, updater temp, and maintenance directories. Use a private per-user
directory, never `%PUBLIC%`. It records executable versions, worker
requests/logs, rollback metadata, relevant Application events, junction targets,
and profile-content-safe inventory hashes. The complete JSON is sensitive diagnostic evidence.
Raw logs/events and absolute paths remain, so review and redact it before sharing.
Use `-BaselinePath` plus `-FailOnMismatch` only
after Quick Panel is closed; locked or unreadable profile files make the
inventory incomplete rather than falsely reporting preservation.

## Browser profiles and recovery

Do not rename or move folders inside `data\WebView2`. If a profile appears
missing, close Quick Panel, preserve both `%LOCALAPPDATA%\QuickPanel` and
`%LOCALAPPDATA%\AIQuickPanel`, and record what changed before trying a reset. Quick Panel can detect
managed orphan profiles and retries deletion of locked profile data after
restart. The original shared **Default** profile is retained for compatibility.

When deleting a named profile used by tabs, choose whether to move affected tabs
to the current default profile or close them. This removes local browser data for
that named profile; it does not delete an account at the website.

## Logs, diagnostics, and panel position

Settings can open the settings and logs folders and copy diagnostics. The paths
are:

```text
%LOCALAPPDATA%\QuickPanel\data
%LOCALAPPDATA%\QuickPanel\data\logs
```

Diagnostics omit credentials and full page URLs, but they contain local paths,
the current site origin, and machine display information. Review them before
sharing. Never upload WebView2/profile data publicly.

If the panel is off-screen or badly positioned, open **Settings > Advanced** and
choose **Reset panel position**. It moves the panel near the cursor without
deleting settings or browser profiles.

## Reporting a reproducible bug

Include:

1. Quick Panel version and whether the ZIP was signed or unsigned.
2. Windows version, display scale, and monitor arrangement.
3. Exact steps from a restart or fresh profile.
4. Expected result and actual result.
5. Whether the problem reproduces with antivirus temporarily observing but not
   blocking the action.
6. Reviewed diagnostics and the smallest relevant redacted log excerpt.

For security issues, follow `SECURITY.md` and report privately.
