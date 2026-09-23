# Quick Panel startup behavior

Quick Panel is a per-user Windows desktop app. New installs use a dedicated folder such as:

```text
%LOCALAPPDATA%\Programs\QuickPanel
```

The executable is `QuickPanel.exe`. Persistent settings, logs, icons, and WebView2 profiles are stored separately in:

```text
%LOCALAPPDATA%\QuickPanel\data
```

An upgraded installation keeps `%LOCALAPPDATA%\AIQuickPanel\data` as a recovery copy after verified migration.

When **Start Quick Panel with Windows** is enabled, the app creates this current-user shortcut:

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Quick Panel.lnk
```

The shortcut targets `QuickPanel.exe --startup`. Quick Panel does not require administrator rights for startup. Disabling startup removes the current shortcut and cleans supported legacy `AIQuickPanel` Run, CMD, and shortcut entries.

At sign-in, Quick Panel starts hidden in the tray. Codex starts only when its separate option is enabled, after the configured startup delay.

Do not place user data beside the executable. Do not delete either the new profile or retained legacy profile to repair startup.
