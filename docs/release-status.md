# Release status

## Current stage: source preview

The 2.5.0 candidate source is available for inspection. A signed, generally
available Windows binary has not been released. Source visibility is not a
claim that the app has completed release acceptance.

## Required before binary release

- Trusted signing and verification of the application and dedicated updater.
- Clean install on a disposable Windows user or VM.
- Actual Settings-button upgrade through the 2.4.13 compatibility bridge and
  the 2.5.0 rename/migration, including browser-session retention.
- Interruption, rollback, reapply, shortcut, and restart acceptance.
- Docking restoration and supported Windows/DPI checks.
- Anonymous downloads with matching checksums and an unauthenticated update.

Automated build, safety, and packaged-worker checks support these gates; they
do not replace real GUI testing. Detailed diagnostics are kept privately.

No download button or launch announcement is presented before these gates pass.
