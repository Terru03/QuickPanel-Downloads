# Changelog

The 2.5.0 entry describes the source candidate, not a completed public binary
release. Older entries are historical development notes; their compatibility
claims are not current release acceptance. Use `QA.md` and
`docs/release-status.md` for the current verification boundary.

## 2.5.0 - unreleased

- Rename the Windows application and release ZIP to Quick Panel.
- Copy and verify established profile data in the new Quick Panel directory before browser startup, retaining the old directory for recovery.
- Guard updates across old and new executable names and retain verified rollback of the old install.

## 2.4.13 - 2026-09-20

- Private compatibility bridge from the existing executable to the planned Quick Panel public package. The updater accepts exactly one supported root executable, preserving the legacy package format for upgrades from 2.4.12.
- Default update checks target the public binary repository for releases after the bridge. Explicit HTTPS manifest settings still work.

## 2.4.12 - 2026-09-15

- Fixed in-app rollback from 2.4.11 when the selected older backup predates the dedicated updater runtime: rollback now stages the updater from the verified current recovery copy instead of incorrectly requiring it in the old payload.
- Verify the complete current recovery copy against the active installation before starting its updater runtime or acknowledging parent shutdown.
- Added guarded rollback regressions for an older payload without `UpdaterRuntime`, committed replacement, and fail-closed rejection of a tampered recovery copy.

## 2.4.11 - 2026-09-14

- Added a dedicated transaction runner and independent guardian so an interrupted update automatically verifies the new install or restores the previous version.
- Delayed application shutdown until a rollback backup is verified and the guardian has durably acknowledged readiness.
- Added startup recovery for transactions where both updater processes were terminated, with a one-time Settings outcome and durable privacy-safe logs.
- Preserved the exact stable installation target and canonical profile boundary for requests emitted by versions 2.4.5 through 2.4.10.
- Moved ZIP extraction and guarded handoff preparation off the WPF dispatcher to keep Settings responsive.
- Packaged the updater as its own self-contained runtime and extended optional Authenticode signing and release verification to both executables.

## 2.4.10 - 2026-09-14

- Removed repeated recursive WebView2 profile inspection from normal startup: verified migrated profiles use a lightweight marker fast path, while an already-established canonical profile is deeply assessed once and receives a path- and Windows-directory-identity-bound cache in sibling maintenance storage without changing settings, cookies, logins, or other profile bytes. Invalid, stale, recreated, or junction-retargeted cache state falls back to deep assessment.
- Made Settings render before rollback availability validation. The existing strict manifest, profile-exclusion, and reparse-point checks now run cooperatively in the background for display, stop when Settings closes, and still run again before any rollback changes application files.
- Added persistent startup inspection-mode and elapsed-time diagnostics plus completed, canceled, and failed rollback-availability timing so managed-PC delays can be identified without deleting app data or weakening endpoint protection.

## 2.4.9 - 2026-09-13

- Fixed the legacy 2.4.5/2.4.6 in-app handoff when Quick Panel was launched from an updater backup: the newly downloaded worker now resolves the real install before durable-log validation, rejects the maintenance copy, resolves exactly one manifest-backed stable installation from the standard per-user install location and Windows launch registrations, normalizes the obsolete 2.4.5 rollback destination into current maintenance storage, updates only that installation, and restarts its exact executable.
- Kept the canonical `%LOCALAPPDATA%\AIQuickPanel\data` profile independent from the repaired install target, with regression coverage proving settings, tabs, logins, cookies, WebView2 state, icons, the launched backup, and unrelated install files remain unchanged.
- Added durable legacy-target diagnostics and safe ambiguity handling: no installation is changed when no stable candidate or more than one distinct validated candidate is found.

## 2.4.8 - 2026-08-30

- Bound update, rollback, restart, and shortcut targets to the actually launched, application-identified install folder; updater backup, staging, maintenance, and temporary copies are now rejected before handoff so the running app stays open and no wrong-folder files are changed.
- Added an authenticated private-release repair path for installed 2.4.5/2.4.6 builds that verifies the exact GitHub asset digest, uses the released worker, preserves the physical profile and a rollback, and requires no SDK or antivirus exclusion. A recoverable per-user launch guard prevents the old executable from reopening during replacement and is removed before restart.
- Made future private-release checks find GitHub CLI in standard WinGet and Program Files locations when the GUI inherited a stale `PATH`, and moved executable updater staging out of `%TEMP%` into validated profile-maintenance storage.
- Added an explicit parent/worker acknowledgement so Quick Panel remains open when the updater process cannot start or take ownership of the request, instead of closing after `Process.Start` alone.
- Added persistent, privacy-safe stage diagnostics under the resolved profile's sibling maintenance directory, including worker PID, request and application paths, old/new versions, backup, replacement, restore, and restart results.
- Added SHA-256 verification of rollback backups and installed application-owned files while preserving unrelated install entries and the entire persistent profile.
- Hardened update and rollback recovery so a failed replacement restores verified previous program files and safely requests a restart; rollback now stages the current updater separately instead of relying on an older backup executable to understand the acknowledgement protocol.
- Retained compatibility with legacy framework-dependent parents and self-contained payloads, payload-root dependency resolution, strict junction/path boundaries, and profile-free release packages.

## 2.4.7 - 2026-08-27

- Added automatic current-page website icons using WebView2 favicon events, a persistent collision-resistant local cache, safe PNG validation, navigation updates, and styled domain/name fallbacks instead of blank tabs.
- Kept explicit SVG, local, brand, and remote icon choices as manual overrides that automatic favicon updates never replace; existing saved tabs remain compatible and blank icons default to automatic mode.
- Hardened managed reusable browser profiles with regression coverage for stable identities, explicit names, rename/default behavior, shared tabs, orphan recovery, protected legacy Default data, locked profile deletion, and deferred cleanup.
- Changed the normal Windows x64 release to a reliable self-contained multi-file deployment; .NET 8 Desktop Runtime is no longer a user prerequisite, while a missing Microsoft Edge WebView2 Runtime now produces an actionable message.
- Strengthened public-release packaging to reject profile data, browser databases, logs, secrets, keys/certificates, debug symbols, build/test output, reparse points, and machine-specific paths.
- Added `SHA256SUMS.txt`, exact ZIP-content verification, configurable HTTPS hosting, and optional certificate-store Authenticode signing that remains non-blocking for unsigned builds.
- Added an isolated package verifier that builds the actual 2.4.6 tag and proves a realistic update preserves settings, tabs, selection, zoom, icons, browser-profile metadata, cookie/session sentinels, startup preference, and updater state byte-for-byte.
- Improved copied diagnostics so they report only the active page origin rather than a full URL that could contain sensitive query or fragment data.
- Prepared public-user README, privacy, security, support, license-pending, QA, and release documentation while keeping the source repository private and publishing no public release.

## 2.4.6 - 2026-08-26

- Fixed the built-in updater and rollback flow for canonical profiles reached through parent junctions, terminal junctions, or junction chains, including the established parent-plus-self-junction layout. Broken links, loops, files, and install/profile overlaps are rejected before any replacement begins.
- Kept update backups, migration staging, and migration logs in a validated sibling maintenance directory outside the active physical profile. Existing legacy rollback backups remain discoverable read-only and are not moved or deleted.
- Preserved the resolved physical profile path from installer request creation through the update worker and rollback request, with regression tests proving byte-identical profile contents across synthetic update and rollback operations.
- Corrected rollback labels to use the backed-up executable's version metadata, distinguished available/current/newer-local update states, and separated check, download, and install-preparation failures in Settings.
- Normalized leading Unicode and mojibake BOM markers in historical release notes and now writes release JSON as UTF-8 without a BOM under Windows PowerShell 5.1.
- Added Windows junction, containment, payload, integrity, rollback, version-display, update-presentation, failure-path, and release-encoding coverage for the updater safety contract.

## 2.4.2 - 2026-08-25

- Added persistent per-tab WebView2 profiles so the same website can stay signed into different accounts inside one Quick Panel instance. Existing tabs continue using the established default profile and retain their current cookies and logins.
- Added **Open current page with new profile** to every web-tab menu. It opens the current URL beside the source tab with a new isolated cookie jar, keeps the site's real icon, and adds a small profile badge.
- Added a browsing-profile selector to Add/Edit tab so tabs can use the default login, reuse an existing separate login, or create another profile without Windows or Chrome profiles.
- Kept all profile data beneath the canonical `%LOCALAPPDATA%\AIQuickPanel\data\WebView2` directory so the 2.4.1 updater protections continue to preserve every account session.

## 2.4.1 - 2026-08-25

- Fixed the 2.4.0 profile-location regression with a startup migration gate that runs before settings and WebView2. The permanent profile is `%LOCALAPPDATA%\AIQuickPanel\data`, independent of the executable, build, publish, and installation directories.
- Added conservative recovery for legacy root, nested, installed-app `data`, and current executable/build `data` layouts. A clearly richer legacy profile is copied through SHA-256-verified staging, a fresh canonical profile is retained in a unique recovery backup, and legacy sources are never moved or deleted.
- Preserved established canonical profiles, made migration idempotent with a versioned marker, blocked ambiguous or in-use migrations before WebView2 starts, and added a themed recovery window with non-sensitive diagnostics.
- Hardened the built-in auto-updater, rollback, local updater, clean installer, and release packager so they replace application binaries without touching canonical or legacy settings, WebView2 cookies/sessions, tab state, icons, logs, or other profile entries. Profile-bearing release archives are rejected.
- Moved binary rollback backups outside the active profile and removed automatic push-triggered GitHub release builds so 2.4.1 can be built, tested, packaged, and uploaded locally without consuming GitHub Actions minutes.
- Added synthetic regression coverage for canonical paths, legacy migrations, established/fresh/ambiguous profiles, in-use failure, recovery backups, idempotence, payload rejection, and byte-identical settings/WebView2 state across simulated updates.

## 2.4.0 - 2026-08-24

- Redesigned the built-in Task Manager for the panel instead of squeezing desktop-width tables into it. Narrow process views keep Name, CPU, and Memory readable, selection exposes PID/status/window details with guarded actions, and wider saved panel sizes progressively reveal more columns.
- Replaced the clipped Performance table with responsive overview cards, live bounded telemetry graphs, and scrollable hardware cards. CPU, RAM, GPU, SSD/HDD/NVMe, temperature, fan, capacity, network, power, clock, board, controller, and other exposed device data now wrap cleanly; unavailable sensors remain explicit and are never graphed as fake data.
- Added dedicated Temperature and Fans overview cards derived only from valid live readings, while keeping the detailed reason when firmware, permissions, or vendor support prevents fan access.
- Reduced the title-bar resize grip to 18% of pointer movement for much finer control while retaining the saved-width and saved-height restore behavior.
- Replaced the built-in Codex Usage, Windows Helper, Trip Planner, and Task Manager letter badges with distinct Segoe Fluent icons; website tabs and docked apps keep their real favicons or installed Windows icons.
- Replaced the legacy light Windows End task message box with a compact Quick Panel confirmation that names the process and PID, keeps Cancel as the focused safe default, uses an explicit destructive action, and preserves the dark design language. Selected process rows now retain their dark accent instead of inheriting a white cell highlight.

## 2.3.0 - 2026-08-24

- Added a visual **Dock a window** picker with separate open-window and installed-app views, search, refresh, real app icons, compatibility labels, and double-click support. It indexes the PC's Windows Start app catalog but opens and adds nothing until the user chooses it.
- Added an **Open app or file** path for executables, shortcuts, PDFs, photos, and videos that are not exposed by the Windows Start app catalog.
- Added safe session-only window tabs that bind to the exact selected HWND, keep the application's native frame and rendering model intact, and restore its original size, position, state, and topmost status on undock, tab close, or normal Quick Panel exit.
- Added broad top-level-window support through managed overlay docking, including verified File Explorer navigation, Task Manager, Parsec, Chrome Remote Desktop, Windows Settings, Photos, Media Player, and Adobe Acrobat PDF windows.
- Added tab-aware app visibility so docked windows hide when another tab or the panel is hidden, then return to the panel bounds when selected again.
- Kept unusual utility, untitled, compact, and tiny app windows available behind a clear **Try** label. Only Quick Panel itself, unusable handles, child/hidden/cloaked surfaces, and Windows shell or protected-security surfaces are excluded.
- Preserved real Windows icons in both picker cards and session tabs; no generic letter icons are created.
- Replaced picker overlay-follow behavior with genuine in-panel HWND child hosting, standard-frame removal, exact-window restore, and per-app client-chrome crop controls. File Explorer removes only its duplicate inner title/tab row while keeping navigation and commands.
- Made the whole Quick Panel resizable, saved its last width and height, restored that size on the next launch, and damped the title-bar resize grip for fine hand control.
- Added a native Task Manager with searchable live processes, guarded file-location and End task actions, plus a Performance view for CPU, RAM, GPU, SSD/HDD/NVMe, network, temperatures, clocks, power, and fan sensors. Unsupported or privilege-gated sensors are reported as unavailable.

## 2.2.0 - 2026-08-24

- Replaced the Codex-only host with a data-driven external Windows application framework shared by Codex, WhatsApp, and future configured apps.
- Added EXE, AppUserModelID, URI, shell, shortcut, and dynamic Windows Start-app launch support with scored top-level HWND discovery.
- Added genuine HWND docking, original parent/style/placement restore, event-driven resize, focus handoff, health checks, disconnect states, and Retry/relaunch.
- Added installed Windows icon discovery for running executables, shortcuts, shell targets, and packaged `shell:AppsFolder` apps; external app tabs no longer fall back to letter icons.
- Added compatibility rules for alternative titles, window classes, AppUserModelIDs, package identities, compact windows, and opt-in tool windows while keeping strong identity checks.
- Renamed all user-visible product text to Quick Panel while retaining internal `AIQuickPanel` executable and data names for upgrade compatibility.

## 2.0.0-dev - 2026-06-25

- Added private GitHub release update checks through the local authenticated GitHub CLI after the no-token GitHub API path returns private-repo `404` responses.
- Added SHA256 detection for GitHub release ZIP assets when GitHub provides an asset digest.
- Removed the manual Codex reset override fields from Quick Tools and rewrote reset credit labels to Granted, Expires, and Redeemed wording.
- Documented that `1.7.7` installs need a one-time `update-local.cmd` or public `version.json` bootstrap because they cannot see private releases in-app.

## 1.7.7 - 2026-06-23

- Added Settings update checks against the latest GitHub Release, an optional quiet startup update check, release-note display, and friendly offline/rate-limit errors without auto-installing updates.
- Renamed the user-facing app title to Quick Panel and switched the app icon while keeping existing executable, settings, and data paths compatible.
- Added a protected Quick Tools tab with a manual Codex Reset Manager, safe Windows fix actions, and Trip Mode travel planning data stored through the existing settings system.
- Added a manual **Refresh Codex Usage** action that reads local Codex auth/state files on demand, calls both `/wham/usage` and `/wham/rate-limit-reset-credits`, displays usage/reset fields with direct reset credit grant/expiry details when exposed, reports `API expiry: not exposed` when direct expiry fields are unavailable, and shows a clearly labeled grant-time + 30-day estimated expiry when grant time is available.
- Restored portable data behavior so settings, logs, WebView2 data, icon cache, updater preference, reset manager data, and trip data stay under the app folder's `data` directory.
- Added saved pin/unpin support for built-in, native, and custom tabs.
- Replaced startup registration with a normal current-user Startup folder `.lnk` shortcut and Start Menu shortcut; legacy Run-key and `.cmd` entries are only cleaned up.
- Added an explicit, disabled-by-default **Launch Codex automatically** setting and a 45 second delay before optional Codex startup actions.
- Prevented hidden Windows startup from implicitly launching/docking Codex or rewriting shortcuts.
- Added Settings diagnostics for install path, startup method, Codex autostart, last startup result, and Bitdefender-friendly allowlist paths.
- Removed the Settings low-level mouse hook and added clean install documentation/scripts.

## 1.7.6 - 2026-06-23

- Stopped fresh or migrated installs from automatically writing Windows startup entries when the startup preference has never been explicitly saved.
- Kept the Settings and tray startup toggle available, with clearer messaging when endpoint security blocks startup registration.

## 1.7.5 - 2026-06-23

- Added a direct settings-file overwrite fallback when endpoint security blocks atomic replace saves.
- Clarified startup-registration failures so Bitdefender or Windows security blocks are reported as startup issues instead of generic settings-save errors.

## 1.7.4 - 2026-06-23

- Added a verified Startup-folder launcher fallback when Windows blocks or ignores updates to the `HKCU` Run entry, avoiding repeated startup warning banners.
- Made startup detection ignore stale Run-key values that point to missing `AIQuickPanel.exe` files.
- Updated the local updater to detect an existing install from a valid Run key, Startup launcher, or currently running AIQuickPanel process before asking for a manual path.

## 1.7.3 - 2026-06-22

- Added a Windows GitHub Actions CI workflow for restore, Release build, tests, and win-x64 publish.
- Moved app version metadata into `Directory.Build.props` so assembly version, informational version, and release packaging share one source of truth.
- Added a safe local `LogService` with redaction and startup/unhandled-exception logging under `%LocalAppData%\AIQuickPanel\logs`.
- Updated `scripts/publish-release.ps1` so `-Version` is optional and defaults to the central version.
- Added a manual `QA.md` checklist for release smoke testing across tabs, updates, settings, docking, and DPI.

## 1.7.2 - 2026-06-22

- Hardened update checks to require HTTPS except localhost/dev URLs, validate 64-character SHA256 values before download, and show clearer update error messages.
- Added update download progress, cancel support, Settings/About update badge, and tray update indicator.
- Added `scripts/publish-release.ps1` plus README release manifest publishing steps.
- Made page-focus handoff ignore app text entry controls so Add Tab, edit dialogs, Settings, text boxes, password boxes, and combo boxes keep typing.
- Added `Ctrl+R` and `F5` reload support, a title-bar Reload button, and Escape-to-stop-loading before hide.
- Added import preview details for settings backups and a Restore previous backup action.
- Expanded tests for secure update URLs, SHA256 validation, focus policy, reload shortcuts, backup import summaries, and the release publishing script.

## 1.7.1 - 2026-06-22

- Changed web-tab switching to focus app chrome first, preventing website skip-link artifacts such as "Skip to content" unless the page is intentionally keyboard-focused.
- Added public manifest update checks with semantic version comparison, manual update status in Settings, update zip download, optional SHA256 verification, and release notes opening.
- Added Settings advanced actions for diagnostics, settings folder open, settings backup export/import with validation, and layout reset.
- Saved `settings.json` through a temp file and atomic replace, and backed up corrupt settings as `settings.corrupt-yyyyMMdd-HHmmss.json` before recreating defaults.
- Hardened duplicate custom tab tests for same-name and same-URL tabs across selection, order, pinning, edits, zoom, and closed-tab restore.
- Added `version.example.json` and documentation for public update manifests and why auto-install needs a separate updater process.

## 1.7.0 - 2026-06-22

- Added stable unique tab IDs for custom tabs so duplicate names and duplicate URLs are supported and persist across restart.
- Migrated older `settings.json` files that only used URL keys by assigning tab IDs without dropping existing custom tabs.
- Added duplicate, rename, edit URL, change icon, pin or unpin, reset zoom, open externally, copy URL, reopen closed tab, and close actions to each web tab context menu.
- Persisted the last 10 closed custom tabs so `Ctrl+Shift+T` restores closed tabs after app restart.
- Added pinned custom tabs, with built-in tabs treated as pinned and protected.
- Moved Back, Forward, and Home controls into the top title bar and removed the extra browser controls row.
- Added a Settings privacy action to clear all WebView2 browsing data, with confirmation.
- Fixed Add Tab text alignment inside input boxes.

## 1.6.1 - 2026-06-22

- Persisted per-tab WebView zoom for built-in and custom tabs in `settings.json`.
- Replaced viewport chrome calculations with measured tab-strip height plus a safe fallback.
- Added web-tab context actions for reopen closed tab, open externally, copy URL, and reset zoom.
- Added compact Back, Forward, and Refresh/Stop controls above the browser viewport.
- Added load-error recovery actions for Retry, Open externally, and Copy URL, plus richer tab tooltips.

## 1.6.0 - 2026-06-22

- Added `Ctrl+T` support from both WPF and focused WebView2 pages to open the Add Tab dialog.
- Added an in-memory closed custom-tab stack with `Ctrl+Shift+T` restore near the previous tab position.
- Added `Ctrl+W` custom-tab close and per-tab WebView zoom shortcuts: `Ctrl+Plus`, `Ctrl+Minus`, and `Ctrl+0`.
- Tightened the WebView content viewport clipping so website headers stay below the app chrome.
- Polished tab selection, overflow menu spacing, tab scrolling, and tooltips for tab, docking, settings, and panel controls.

## 1.5.6 - 2026-06-14

- Added a direct `Settings…` action to the tray menu.
- Opened the panel before showing Settings from the tray so owner positioning stays correct.
- Added a debounced live icon preview to the Add Tab dialog.

## 1.5.5 - 2026-06-14

- Replaced the layered transparent Settings HWND with an opaque borderless window.
- Let DWM round the native Settings window and its shadow as one surface.
- Removed the square native surface that showed beneath all four WPF-rounded corners.

## 1.5.3 - 2026-06-14

- Changed the Settings window height from 390 to 392 logical pixels.
- Made both Settings dimensions resolve to whole physical pixels at 125%, 150%, 175%, and 200% display scaling.

## 1.5.2 - 2026-06-14

- Explicitly set `DWMWA_WINDOW_CORNER_PREFERENCE` to `DWMWCP_DONOTROUND` for Settings.
- Kept WPF's transparent rounded border as the only source of the popup corner shape.

## 1.5.1 - 2026-06-14

- Removed the native GDI window region that regressed the Settings popup corners in 1.5.0.
- Restored the transparent WPF window path used by 1.4.4 so the rounded border owns the corner shape at all DPI scales.

## 1.5.0 - 2026-06-14

- Replaced tab labels with compact SVG brand icons while keeping names in tooltips, accessibility data, and the overflow menu.
- Added automatic SVG/favicon discovery and optional brand name, SVG URL, raw SVG, or local SVG selection for custom tabs.
- Added bundled OpenAI, Gemini, Anthropic, Perplexity, X, Reddit, and globe icons with a guarded SVG parser and local icon cache.
- Restored the exact DPI-aware rounded settings HWND region so no square surface remains below the rounded WPF chrome.
- Added dock busy feedback, inline warning banners, keyboard tab navigation, and middle-click custom-tab close.
- Matched the requested deeper chrome, content background, violet selected-tab tint, and tab-strip fade colors.

## 1.4.4 - 2026-06-14

- Removed the native settings window region override and let the transparent WPF window render its rounded chrome directly.

## 1.4.3 - 2026-06-14

- Kept the native settings region symmetric and refreshed it after DPI changes.
- Added explicit Escape handling for the borderless settings window.
- Added hover and full-card click behavior to interactive settings rows.
- Changed the settings action label from Done to Save only when values differ from their initial state.

## 1.4.2 - 2026-06-14

- Cut the settings popup HWND to a DPI-aware rounded region so no square window surface remains below the rounded chrome.

## 1.4.1 - 2026-06-13

- Removed the fixed-size settings window clip so rounded chrome follows runtime layout.

## 1.4.0 - 2026-06-13

- Animated settings toggles with short eased thumb and color transitions.
- Kept settings title and content inside clean rounded corners.
- Moved duplicate-tab validation into the add-tab dialog.
- Replaced remove-tab system confirmation with an inline two-click menu action.
- Added a separator around the selected tab in the overflow menu.
