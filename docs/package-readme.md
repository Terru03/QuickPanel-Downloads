# Quick Panel for Windows

Your everyday tools, one shortcut away.

## Start

Extract the complete ZIP into a normal folder and open `QuickPanel.exe`. Keep
the adjacent files and `UpdaterRuntime` folder together. The package includes
the .NET runtime; Microsoft Edge WebView2 Runtime is also required.

Press **Ctrl + Alt + G** to show or hide the panel. Escape hides it.

## Websites and profiles

Use **+** or **Ctrl + T** to add a website. Select or create a browser profile
in the Add/Edit dialog to organize separate account sessions. Tabs in the same
profile share sessions. Browser profiles are not an operating-system security
boundary.

Right-click a tab for available pinning, editing, profile, zoom, and history
actions. Use **Dock a window** for supported desktop apps; compatibility varies.
File Explorer uses a separate in-panel folder view.

## Settings and updates

Settings includes startup, window behavior, update checks, and recovery options.
Updates require access to the official public GitHub release. Before upgrading
a legacy 2.4 installation, follow the compatibility-bridge instructions in the
release notes. Retain existing settings, browser profiles, and recovery data.

## Privacy and support

Settings and browser state are stored under `%LOCALAPPDATA%\QuickPanel\data`.
Legacy `%LOCALAPPDATA%\AIQuickPanel\data` is retained during migration.
Never attach either profile directory, cookies, tokens, or unreviewed diagnostics
to a public issue.

Read the included `PRIVACY.md`, `LICENSE`, `SUPPORT.md`, and
`THIRD-PARTY-NOTICES.md`. Public source retains reserved-rights terms.

Official source, release notes, screenshots, and support:
https://github.com/Terru03/QuickPanel-Downloads
