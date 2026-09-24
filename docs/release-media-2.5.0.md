# Quick Panel 2.5.0 screenshots and demonstration

Status: the [gallery](media/2.5.0/README.md) includes the workspace, Add tab dialog,
and profile menu from the real 2.5.0 application in a disposable Windows VM.
The current images show controls, not completed website/profile workflows.
Utility/docking captures and a complete recording remain pending.

## Capture the actual release

Use a disposable Windows user or VM with a fresh Quick Panel profile. Extract
the complete ZIP and verify its SHA-256. Record the package hash, executable
version, source revision, and signing status in private capture notes. Captures
may precede packaging, provided the shipped application source and UI match.
The 2.5.0 public release is explicitly unsigned. An earlier version, a
rendering, or a generated mockup is not a screenshot of the released product.

The current app uses the Windows account's canonical profile and can migrate
legacy data at startup. It has no separate demo-profile switch. Do not rename,
redirect, overwrite, or clear a daily-use profile to stage the demonstration.

Keep captures to the Quick Panel window. Use only synthetic profile names and
public websites while signed out. Close personal windows and notifications.
Avoid account menus, diagnostics, file pickers, process lists, hardware serials,
Codex Usage, and other views that can show host or account information.

## Required screenshots

Capture these three screens at a readable, consistent window size. Preserve the
native image format (the current capture tool returns JPEG):

| Filename | Real UI to show | Caption |
| --- | --- | --- |
| `quick-panel-2.5.0-workspace.jpg` | Main panel with public website tabs and one loaded page | Your everyday websites in one Windows panel. |
| `quick-panel-2.5.0-add-website.jpg` | Add website dialog with a harmless public URL and generic tab name | Add a website and make it part of your workspace. |
| `quick-panel-2.5.0-browser-profiles.jpg` | Actual profile selection UI using synthetic Work and Personal names | Organize separate browser sessions with named profiles. |

Capture the version display privately to establish which build is running. Use
only a clean version crop publicly if it contains no install path or personal
details. Preserve the original captures privately; avoid retouching the app UI.

## Optional 30-second demonstration

Record the actual interactions, with no microphone or desktop audio required:

1. 0–5 seconds: show Quick Panel and the main website workspace.
2. 5–15 seconds: add a public website and select its tab.
3. 15–24 seconds: show the Work and Personal profile choices; do not claim a
   screenshot alone proves session isolation.
4. 24–30 seconds: switch tabs and demonstrate hiding/showing the panel with
   Ctrl + Alt + G on the clean desktop.

Save as `quick-panel-2.5.0-demo.mp4`. A short GIF may be used as a secondary
release, but retain a readable MP4. Never speed up loading to imply a performance
claim; label cuts or edits where they would otherwise mislead.

## Review and include with the release

- Inspect every screenshot and the complete recording for personal information,
  account state, credentials, local paths, notifications, and unintended windows.
- Remove nonessential personal metadata from the media files and recheck them.
- Keep raw footage, capture notes, profiles, and diagnostics private.
- Commit only the approved images or clip under `docs/media/2.5.0/`. That folder
  is created only when genuine reviewed media exists.
- In the 2.5.0 GitHub release body, embed the images using links pinned to their
  reviewed media commit. Link the MP4 if available. Include short captions and
  descriptive alt text; check the links without authentication.
- Add the workspace screenshot and demo link to the README once they exist.
  Do not publish broken placeholders or call the candidate generally available.
- Keep the binary release asset allowlist unchanged: ZIP, `version.json`, and
  `SHA256SUMS.txt`. Release-body media references the separately reviewed source
  media, so raw capture folders are never swept into a package upload.

If the shipped UI or application behavior changes after capture, repeat the
affected captures and update the provenance record. Signing alone does not
require recreating otherwise identical UI screenshots.
