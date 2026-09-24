# Quick Panel 2.5.0 release status

## Unsigned public release

[Download the Windows x64 release](https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v2.5.0).
This is a free, unsigned release for fresh Windows accounts. Windows may show
an unknown-publisher warning or block execution. Production signing is optional
for this distribution; no certificate or trusted publisher identity is claimed.

The portable ZIP contains the application, its self-contained .NET runtime, and
the dedicated updater. Microsoft Edge WebView2 Runtime is also required.

## Verification and limits

Release checks cover the Release build, C# tests, PowerShell safety suites,
payload privacy, checksums, and packaged update/profile-preservation behavior.
The application was launched in a disposable Windows 11 VM and genuine captures
show its workspace, Add tab dialog, and browser profile choices. See the
[gallery and provenance](media/2.5.0/README.md).

These checks do not establish complete end-to-end GUI coverage. The following
remain unverified and are not claimed as supported upgrade paths or tested configurations:

- Exact Settings-button 2.4.12 → 2.4.13 → 2.5.0 upgrade with session retention.
- GUI interruption, rollback/reapply, sign-in startup, and restart acceptance.
- Full docking/restore compatibility and Windows/DPI combinations.
- Complete website/profile interaction and session-isolation smoke tests.

Do not replace an existing legacy installation with this release. Startup can
migrate an existing account's legacy profile, even when the ZIP is extracted to
a different folder. Use a separate Windows account or disposable VM.

Version 2.5.0 is published as a normal GitHub release, using the same unsigned
ZIP distribution format as earlier releases. The public latest-release endpoint
provides its metadata. Publishing it does not establish that the legacy rename
upgrade works through the old GUI; no historical compatibility bridge is being
announced with it.

Only the ZIP, `version.json`, and `SHA256SUMS.txt` are release assets. Public
screenshots are reviewed separately. Profiles, credentials, raw diagnostics,
private history, and local evidence are not part of the release.
