# External application framework

Quick Panel has two native-window modes:

- The title-bar **Dock a window** picker creates a session-only tab for an
  exact live HWND. It uses genuine child HWND hosting by default, removes the
  standard desktop frame, and resizes the application to the panel content slot.
- JSON-configured apps such as Codex and WhatsApp use genuine HWND reparenting
  with `SetParent` by default and can launch or reconnect by application identity.
  Explicit JSON compatibility mode remains available for apps that reject child
  hosting.

File Explorer is a separate case: the picker opens its physical folder in an
in-panel Shell view and leaves the original Explorer window unchanged.

## Architecture

- `ExternalAppDefinition` is JSON-backed application configuration.
- `ExternalAppRegistry` merges bundled definitions with optional user entries.
- `ExternalAppLauncher` handles EXE, AppUserModelID, URI, shell, shortcut, and
  dynamic Windows Start-app launch paths.
- `ExternalAppWindowFinder` enumerates top-level HWNDs and gathers process,
  package, title, class, visibility, ownership, bounds, and render facts.
- `ExternalAppWindowMatcher` is pure, unit-tested candidate scoring.
- `ExternalAppWindowCatalog` and `ExternalAppWindowPickerPolicy` build the safe
  visual list, mark unusual app windows **Try**, and block only unusable, shell,
  protected-security, hidden, cloaked, child, stale, or self-owned surfaces.
- `DockWindowPickerWindow` provides separate open-window and installed-app
  views, search, refresh, compatibility labels, real icons, double-click
  actions, and the **Open app or file** flow.
- `ExternalAppDefinitionFactory` turns the exact selected PID and HWND into a
  non-persistent session definition that never auto-launches or terminates apps.
- `ExternalAppNativeMethods` is the only P/Invoke boundary.
- `ExternalAppDockService` owns cancellation, launch/discovery retry, state,
  parent/style/placement capture, docking, resize, focus, health, restore, and
  optional owned-process shutdown.
- `ExternalAppHost` and `ExternalAppTabView` provide reusable WPF hosting and
  Launching, Searching, Connected, Application closed, Unable to embed, and
  Retry states.
- `ExternalAppIconService` resolves the real installed Windows icon from the
  running process, executable, shortcut, shell target, or packaged AppsFolder
  identity. External apps never use a letter header.

Codex and WhatsApp are ordinary entries in `external-apps.json`. Neither has a
dedicated docking service. Codex's 36-pixel-at-96-DPI crop is a generic
`contentTopOffsetAt96Dpi` definition value.

## Visual picker sessions

Select **Dock a window**, choose a **Ready** window, and select **Dock
window**. **Try** means the item is untitled, small, compact, owned, or
tool-style; it stays selectable and Quick Panel will attempt it.

Use **Installed apps** to search everything Windows exposes in its Start app
catalog. Quick Panel reads this catalog only when that view opens. It does not
auto-launch, auto-dock, pin, or save any catalog entry. Choose one app to open
it, then dock the exact safe top-level window that appears.

Use **Open app or file** when the wanted window is not open yet. Windows opens
the exact chosen executable, shortcut, PDF, photo, video, or other file with its
registered handler. Refresh runs after launch so the resulting app window can be
picked. Quick Panel does not build a command string or bypass Windows consent.

Session tabs keep their real Windows icon. They are not saved across Quick Panel
restarts because HWND values only identify live windows. The exact selected
window remains bound to that tab; a retry cannot silently attach a different app.

Reparent mode snapshots parent, style, placement, and topmost state before
changing the frame and calling `SetParent`. It keeps the exact selected HWND in
the native host, resizes from host layout events, and restores the snapshot on
undock, tab close, or normal shutdown. A per-tab top crop can remove redundant
client-drawn title chrome for supported apps.

File Explorer is handled separately: Quick Panel opens the selected physical
folder in an in-panel Shell view. It does not reparent or alter the original
Explorer window. Virtual folders that cannot be resolved to a physical folder
are rejected with an explanation.

Compatibility overlay mode changes no parent, owner, caption, or rendering
style. It is opt-in rather than the picker default.

## Add an application

Create or edit:

```text
%LOCALAPPDATA%\QuickPanel\data\external-apps.user.json
```

The file contains a JSON array. User entries override bundled entries with the
same case-insensitive `id`. Restart Quick Panel after editing. Example:

```json
[
  {
    "id": "app:notepad",
    "displayName": "Notepad",
    "icon": "auto",
    "launchKind": "Executable",
    "executablePath": "%WINDIR%\\System32\\notepad.exe",
    "launchArguments": null,
    "processName": "notepad",
    "alternativeProcessNames": [],
    "windowTitleMatch": "Notepad",
    "alternativeWindowTitleMatches": [],
    "windowTitleRegex": null,
    "windowClassName": "Notepad",
    "alternativeWindowClassNames": [],
    "appUserModelId": null,
    "alternativeAppUserModelIds": [],
    "packageIdentity": null,
    "alternativePackageIdentities": [],
    "uriLaunchCommand": null,
    "shellLaunchTarget": null,
    "startAppNameMatch": null,
    "launchTimeoutMs": 10000,
    "launchAutomatically": true,
    "terminateOnPanelClose": false,
    "preferredDockingBehavior": "Reparent",
    "startupDelayMs": 0,
    "restoreOriginalWindowOnUndock": true,
    "minimumCandidateScore": 55,
    "requireVisibleWindow": true,
    "allowOwnedWindows": false,
    "allowToolWindows": false,
    "minimumWindowWidth": 240,
    "minimumWindowHeight": 160,
    "requireRenderedSurface": false,
    "contentTopOffsetAt96Dpi": 0,
    "isEnabled": true
  }
]
```

`launchKind` accepts `Auto`, `Executable`, `AppUserModelId`, `Uri`, `Shell`, or
`StartApp`. `StartApp` searches `shell:AppsFolder` by `startAppNameMatch`, so a
Store/package install does not need a username, versioned package path, or fixed
AppUserModelID. `Shell` supports shortcuts and other shell targets. `Auto`
selects the first configured AUMID, executable, URI, shell target, or Start-app
name.

Use `"icon": "auto"` to keep the application's real installed Windows icon.
Quick Panel checks the running process, configured executable, shortcut, shell
target, and packaged AppsFolder identity in that order. A definition may instead
use a bundled icon name, a local SVG/PNG/ICO file, or a safe HTTPS SVG URL.

The `alternative*` arrays support applications whose process, title, class,
AppUserModelID, or package identity differs between installers and releases.
Compact apps can lower `minimumWindowWidth` and `minimumWindowHeight`. Set
`allowToolWindows` only for an application whose actual main UI uses the Windows
tool-window style; the default remains false to avoid docking popups.

Definitions may also supply `customWindowMatchingPriority` with scoring weights
named after the properties in `ExternalAppMatchPriority`. A definition is
rejected if its ID, timeout, score, minimum size, content offset, or title regex is invalid.
Regex evaluation has a short timeout.

## Discovery and lifecycle

Quick Panel checks existing top-level windows before launching. It does not rely
on `Process.MainWindowHandle` or assume the first launched process owns the UI.
After launch it uses bounded retry/backoff and can match a helper-created UI by
new HWND, activated PID, process/alternative names, path, package identity,
AppUserModelID, title, regex, and class. It chooses the highest valid score.

When connected, Quick Panel runs one low-frequency 2.5-second HWND health check;
it does not continuously enumerate all desktop windows. Resize is event-driven
from `HwndHost.OnWindowPositionChanged`. Undock, tab removal, and normal Quick
Panel shutdown restore captured state when configured. An app is terminated
only when `terminateOnPanelClose` is true and Quick Panel recorded the same
process ID and start time from its own launch.

## Limits

- Quick Panel cannot manipulate a higher-integrity/elevated window from a
  normal process. It reports access denied and does not bypass Windows security.
- Child hosting depends on the selected desktop app and still requires the
  release's manual compatibility checks. File Explorer uses the separate Shell
  view described above; it is not evidence of child-window hosting.
- An elevated Windows Task Manager cannot be controlled from a non-elevated
  Quick Panel. The built-in Task Manager panel provides process control and
  hardware performance telemetry without bypassing that boundary.
- Secure desktop, Windows shell, protected-security, fullscreen, hidden,
  cloaked, child, and Quick Panel-owned windows are intentionally excluded.
  Tiny and unusual app windows stay available with **Try**.
- Standard desktop title bars are removed in child-host mode. App-drawn client
  chrome can be cropped per tab, but navigation/command rows should remain.
- `SetParent` can still be rejected by some WinUI, GPU,
  DirectX, hardware-overlay, protected-content, fullscreen, or raw-input apps.
- A process crash prevents guaranteed restoration. Normal close/undock/shutdown
  restores state; abnormal Quick Panel termination can only rely on Windows and
  the external app rebuilding its next top-level window.

## Manual validation

For visual picker sessions, confirm the installed-app catalog is searchable,
uses real icons, and launches nothing until selected. Then test File Explorer
navigation, Parsec,
Chrome Remote Desktop, Settings, PDF, photo, and video render/input; tab
switching; panel hide/show; resize; DPI/display moves; app close; exact-window
retry; undock; tab close; and Quick Panel exit restore. Confirm every picker card
and tab keeps the real app icon.

For Codex and WhatsApp, test launch and existing-process attach, mouse, keyboard,
copy/paste, shortcuts, Tab navigation, right click, scrolling, text entry,
tab switching, panel hide/show, resize, DPI/display changes, manual app close,
Retry/relaunch, unpin/undock, and Quick Panel exit restore. Record applications
that render blank, lose raw input, or reject reparenting rather than adding
security-bypassing workarounds.
