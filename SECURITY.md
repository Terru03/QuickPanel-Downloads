# Quick Panel security policy

## Supported releases

Security fixes are provided for the latest published Quick Panel release. Older private releases may receive bridge assistance for migration to the current build.

## Reporting a vulnerability

Use this repository's confidential GitHub vulnerability report form:
https://github.com/Terru03/QuickPanel-Downloads/security/advisories/new.
If the form is unavailable, do not put sensitive details in a public issue;
wait for a confidential reporting route. Do not publish an exploit, token,
cookie, private URL, browser database, local path, or personal data.

Include the Quick Panel version, Windows version, concise reproduction steps, expected and actual behavior, and whether the issue reproduces with a fresh profile. Review and redact diagnostics and screenshots before sharing.

Never attach these items to a public report:

- `%LOCALAPPDATA%\QuickPanel\data\WebView2` or the retained legacy WebView2 directory;
- `settings.json`, profile metadata, cookies, sessions, Local Storage, or IndexedDB;
- `%USERPROFILE%\.codex\auth.json` or any access or refresh token;
- signing certificates, private keys, `.env` files, or unreviewed logs.

## Security boundaries

Release packages reject persistent profile data, browser databases, debug symbols, build trees, secrets, key material, reparse points, and unowned install files. HTTPS update manifests are validated, downloaded ZIPs are SHA-256 checked, and application replacement is kept separate from the profile.

Websites loaded in WebView2 remain third-party content with their own security and privacy policies.
