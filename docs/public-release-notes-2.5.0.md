<!-- SOURCE_PREVIEW_NOTICE_START -->
**Release body template — no public binary has been released.** The signing
handoff removes this notice and pins media URLs to the reviewed commit. Signing
and final GUI acceptance are required before publication.
<!-- SOURCE_PREVIEW_NOTICE_END -->

# Quick Panel 2.5.0

Quick Panel is a compact Windows workspace for websites, utilities, hardware
telemetry, and supported docked desktop applications.

## Screenshots and demonstration

![Quick Panel 2.5.0 showing a signed-out website in its Windows panel](https://raw.githubusercontent.com/Terru03/QuickPanel-Downloads/{{SOURCE_COMMIT}}/docs/media/2.5.0/quick-panel-2.5.0-workspace.jpg)

Genuine capture from the 2.5.0 candidate in a disposable Windows VM with a fresh
profile, before signing. No generated product UI is used.

## Download

Download `QuickPanel-2.5.0-win-x64.zip`, compare its SHA-256 value with
`SHA256SUMS.txt`, extract the complete ZIP, and run `QuickPanel.exe`.

The package is self-contained for Windows x64. Microsoft Edge WebView2 Runtime
is required and is normally included with current Windows installations.

## Existing private installations

Install the private 2.4.13 bridge before using the in-app updater to move from
the old executable name to Quick Panel 2.5.0. The upgrade copies and verifies
the established profile in `%LOCALAPPDATA%\QuickPanel\data` and retains the old
profile as recovery material.

## Terms and privacy

The reviewed source is publicly viewable under reserved-rights terms, not an
open-source license. The binary terms permit use on your own devices and
reserve redistribution rights. Quick Panel stores settings and
browser state locally and does not include application telemetry or analytics.
See the included `LICENSE`, `PRIVACY.md`, and `SUPPORT.md` files.

The release handoff signs and verifies the first-party application and updater
binaries before packaging. Check the downloaded ZIP against `SHA256SUMS.txt`.
A valid signature does not guarantee that Windows or antivirus software will
never show a reputation warning.
