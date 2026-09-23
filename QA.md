# Quick Panel 2.5.0 release QA

Record the machine, Windows build, DPI, antivirus state, package SHA-256, and exact source revision for each manual run.

## Automated gate

- [ ] Restore `QuickPanel.sln`.
- [ ] Build Release sequentially with no errors.
- [ ] Run `tests\QuickPanel.Tests.csproj` and confirm `Tests pass.`
- [ ] Run `tests\profile-data-safety.tests.ps1`.
- [ ] Build the self-contained public candidate with `scripts\publish-release.ps1`.
- [ ] Run `scripts\verify-release.ps1` against the final ZIP with `v2.4.12` and `QuickPanel.exe` in the private verification checkout, which retains that historical fixture. Do not import private history into this public snapshot.
- [ ] Confirm `git diff --check` passes.
- [ ] Review every remaining `AIQuickPanel` reference and confirm it is historical or required for upgrade compatibility.

## Package privacy

- [ ] ZIP contains `QuickPanel.exe`, `QuickPanel.dll`, and `UpdaterRuntime\QuickPanel.Updater.exe`.
- [ ] ZIP contains no PDB, source, `bin`, `obj`, test output, verification report, settings, profile, WebView2, cookie database, key, token, or development configuration.
- [ ] Search text files in the extracted ZIP for the Windows user name, user profile path, repository checkout path, email addresses, and private repository URLs.
- [ ] Keep diagnostic evidence in a private per-user directory; review and redact it before sharing.
- [ ] Capture any public screenshot from a fresh profile with no personal tabs, accounts, paths, notifications, or browser history.
- [ ] Complete the [2.5.0 media checklist](docs/release-media-2.5.0.md): capture the final signed build, review every image or video frame, and include working media links in the release notes.
- [ ] Public upload selection names exactly the ZIP, `version.json`, and `SHA256SUMS.txt`.

## Existing install bridge

- [ ] On a disposable Windows user or machine, install the published 2.4.12 ZIP.
- [ ] Create a recognizable settings and browser-session fingerprint without using real credentials.
- [ ] Update through the privately published 2.4.13 bridge and confirm the old executable restarts.
- [ ] Confirm settings and WebView2 fingerprint are unchanged.
- [ ] Update the bridge to the verified 2.5.0 candidate.
- [ ] Confirm `QuickPanel.exe` starts and reports 2.5.0.
- [ ] Confirm `%LOCALAPPDATA%\QuickPanel\data` contains the verified copied profile.
- [ ] Confirm `%LOCALAPPDATA%\AIQuickPanel\data` still exists as a recovery copy and has the original fingerprint.
- [ ] Confirm the Start Menu and optional Startup shortcuts target `QuickPanel.exe`.

## Failure and recovery

- [ ] Interrupt the update after replacement starts; confirm the guardian commits a complete 2.5.0 install or restores 2.4.13.
- [ ] Inject a corrupt or incomplete payload; confirm the old app restarts from the verified backup.
- [ ] Confirm failed migration reports the preserved recovery directory and does not create a fresh browser profile.
- [ ] Confirm rollback and reapply leave profile fingerprints unchanged.
- [ ] Confirm no test deletes either canonical profile directory.

## App smoke test

- [ ] Launch the extracted ZIP outside the checkout.
- [ ] Open, hide, restore, resize, and move the panel across tested monitors and DPI scales.
- [ ] Open several web tabs and confirm profile separation, navigation, icons, zoom, pinning, and restart persistence.
- [ ] Exercise docking and restore for each supported app type listed in `EXTERNAL-APPS.md`.
- [ ] Confirm File Explorer opens its physical folder in the in-panel Shell view, leaves the original window unchanged, and does not crash Explorer.
- [ ] Enable and disable startup; restart Windows and confirm the shortcut behavior.
- [ ] Check update, rollback, logs, diagnostics, and Settings paths for the Quick Panel name.

## Public download

- [ ] Keep historical development and diagnostic material private; publish only the reviewed source snapshot.
- [ ] Confirm public source contains no private history, attachments, or personal metadata.
- [ ] Publish the three allowlisted files.
- [ ] Fetch release metadata and all files anonymously.
- [ ] Confirm anonymous responses are successful and local/downloaded SHA-256 values match.
- [ ] Confirm a normal user update works without GitHub CLI or credentials.
