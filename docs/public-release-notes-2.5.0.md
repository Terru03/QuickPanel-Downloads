# Quick Panel 2.5.0 — unsigned release

Keep everyday websites and utilities in one compact Windows panel. Press
**Ctrl + Alt + G** to bring the workspace into view or hide it again.

**This free Windows x64 release is unsigned.** Windows may show an
unknown-publisher warning or block execution. No trusted signing identity is
claimed. Start with a fresh Windows account or disposable VM; this is not an
approved upgrade for existing 2.4 installations.

## Download and run

1. Download `QuickPanel-2.5.0-win-x64.zip` and `SHA256SUMS.txt` below.
2. Compare the ZIP hash using `Get-FileHash .\QuickPanel-2.5.0-win-x64.zip -Algorithm SHA256`.
3. Extract the complete ZIP into a dedicated folder and run `QuickPanel.exe`.

The .NET runtime is included. Microsoft Edge WebView2 Runtime is required.
Checksums verify download integrity; they do not replace code signing.

## See the app

![Quick Panel workspace with a signed-out website](https://raw.githubusercontent.com/Terru03/QuickPanel-Downloads/{{SOURCE_COMMIT}}/docs/media/2.5.0/quick-panel-2.5.0-workspace.jpg)

![Add a website tab with a name, URL, and browsing profile](https://raw.githubusercontent.com/Terru03/QuickPanel-Downloads/{{SOURCE_COMMIT}}/docs/media/2.5.0/quick-panel-2.5.0-add-website.jpg)

![Choose the default browsing profile or create a new profile](https://raw.githubusercontent.com/Terru03/QuickPanel-Downloads/{{SOURCE_COMMIT}}/docs/media/2.5.0/quick-panel-2.5.0-browser-profiles.jpg)

Genuine 2.5.0 VM captures using synthetic demonstration inputs. The gallery
shows real controls; it is not a claim of complete workflow or session-isolation
testing. No generated product UI or edited demonstration video is used.

## Release scope

Build, automated safety, package privacy, checksum, and packaged update/profile
preservation checks support this release. Full GUI upgrade/rollback, sign-in
startup, docking compatibility, website/profile workflows, and the Windows/DPI
matrix remain incomplete. A separate extraction folder does not isolate legacy
profile migration; use a fresh Windows account. Publishing this normal GitHub
release does not establish legacy GUI upgrade support. A compatibility bridge
for existing 2.4 installations is not being announced with it.

The source is publicly viewable under reserved-rights terms. Official binaries
may be used on your own devices under the included license. Settings and browser
state are stored locally; see `PRIVACY.md`, `LICENSE`, and `SUPPORT.md`.

[Report a reproducible issue](https://github.com/Terru03/QuickPanel-Downloads/issues)
with steps and the app version. Keep profiles, cookies, tokens, and personal
diagnostics out of public reports.
