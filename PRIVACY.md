# Quick Panel privacy

This document describes Quick Panel 2.5.0 behavior as implemented. Quick Panel
does not provide its own user account and does not include telemetry, analytics,
advertising, or behavioral tracking.

## Data stored on this PC

Quick Panel stores persistent per-user data beneath:

```text
%LOCALAPPDATA%\QuickPanel\data
```

This includes settings, custom tabs, panel layout, local logs, website-icon
cache, browser-profile metadata, and Microsoft Edge WebView2 profile data.
WebView2 profile data can include cookies, sessions, local storage, IndexedDB,
cache, and browsing state created by the websites you use. Named browsing
profiles keep separate local sign-in sessions. Quick Panel does not sync this
data to a Quick Panel service.

Automatic website icons come from the favicon selected by the page already
loaded in WebView2. They are cached under `IconCache\Websites`. Automatic mode
does not send website names to Google S2, Simple Icons, or another unrelated
favicon service. If you explicitly enter a remote manual icon URL, Quick Panel
requests that URL as directed by you.

## Network requests

Quick Panel makes network requests only for features that need them:

- WebView2 connects to the websites opened in your tabs. Those sites have their
  own privacy practices.
- Update checks request either the configured HTTPS `version.json` URL or the
  GitHub Releases API. Update downloads request the HTTPS URL in the manifest.
  These are normal HTTPS requests and include no Quick Panel account identifier.
- The optional Codex Usage panel reads `%USERPROFILE%\.codex\auth.json` only
  when that feature refreshes. It uses the local access token to request the
  Codex usage and reset-credit endpoints. The token is not written to Quick
  Panel settings or logs.
- A manual remote icon URL is fetched only when you configure that override.

Quick Panel has no analytics endpoint and does not sell or transmit usage data
to the developer.

## Logs and diagnostics

Logs stay under `%LOCALAPPDATA%\QuickPanel\data\logs`. The logger redacts
common token, authorization, and cookie key/value forms and does not
deliberately record credentials or browser databases.

Copied diagnostics contain app/Windows/WebView2 versions, startup state, local
install and data paths, selected tab ID/name, the current page origin (not its
path, query, fragment, user name, or password), DPI, and monitor size. They do
not include cookies, auth tokens, browser databases, or full page URLs. Local
paths and site origins may still be personal; review diagnostics before sharing
them.

Do not upload `settings.json`, `profiles.json`, WebView2 data, the icon cache, or
unreviewed logs publicly. They can reveal sites, account names, browsing state,
or other private information even when no password is visible.

## Removing local data

Settings can clear browsing data for the selected loaded profile. An upgraded
installation retains `%LOCALAPPDATA%\AIQuickPanel\data` as a recovery copy after
verified migration. Do not delete either data directory as an update repair.
Back up both directories before manually removing local data, and close Quick
Panel first.
