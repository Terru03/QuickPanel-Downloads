# Quick Panel 2.5.0

**Draft — not released.** Signing and real GUI acceptance remain pending.
The instructions below apply after the verified binary release is published.

Quick Panel is a compact Windows workspace for websites, utilities, hardware
telemetry, and supported docked desktop applications.

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

This candidate is unsigned unless the release notes explicitly state that both
application executables were Authenticode signed and verified before packaging.
