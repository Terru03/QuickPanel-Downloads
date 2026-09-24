# Sign and publish Quick Panel from your Windows PC

The 2.5.0 release uses the owner-approved **unsigned release** route. This guide
and `release-public.ps1` remain available for a future signed version; they are
not needed to download or run 2.5.0. The helper deliberately refuses to replace
an existing public release. Use a new reviewed version for a later signed release.

The signing key stays on your PC. `scripts/release-public.ps1` builds from an
explicit reviewed commit, verifies trusted timestamped signatures, and uploads
only the ZIP, version manifest, and checksum file to `Terru03/QuickPanel-Downloads`.
Its default mode creates a **draft**. Publication is a separate command against
the same signed bytes after you test them.

## Prerequisites

- Windows, PowerShell 7 (`pwsh`), Git, GitHub CLI (`gh`), and the .NET 8 SDK.
- Windows SDK SignTool, discoverable under Windows Kits or passed as `-SignToolPath`.
- A trusted code-signing certificate in `Cert:\CurrentUser\My` with access to its
  private key, including any required token/provider software. A certificate
  thumbprint alone does not create a signing identity. Self-signed certificates
  are rejected. A cloud provider that is not exposed through SignTool's
  certificate-store path needs its own integration.
- A GitHub account with release-write access to the public repository.

Run `gh auth login` on your PC if necessary. Do not paste tokens, certificate
passwords, or private keys into the repository or chat. The certificate's
validated publisher identity is visible in signed files; inspect it before use.

List available signing certificates locally:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
  Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey
```

## Pull the source, sign, and upload a draft

Run in **PowerShell 7**. For a new checkout:

```powershell
$ErrorActionPreference = 'Stop'
git clone https://github.com/Terru03/QuickPanel-Downloads.git QuickPanel-Public
if ($LASTEXITCODE -ne 0) { throw 'Clone failed; do not continue.' }
Set-Location QuickPanel-Public
$reviewedCommit = (git rev-parse HEAD).Trim()
$thumbprint = Read-Host 'Signing certificate thumbprint'
& .\scripts\release-public.ps1 -ReviewedCommit $reviewedCommit `
  -SigningCertificateThumbprint $thumbprint
```

Compare `$reviewedCommit` with the reviewed commit in the release handoff. When
reusing a clean checkout of this public repository on `main`, first run
`git pull --ff-only origin main`; stop if it fails. Do not merge private history
or reset a checkout containing your own changes to make the command succeed.

The script performs the build and C# / PowerShell checks, invokes the existing
publisher, then inspects the exact ZIP again. Five first-party executables/DLLs
must have valid timestamped signatures from the specified certificate. It
verifies payload privacy, updater URLs, ZIP and manifest hashes, and the exact
three-file upload list. No application is launched on your PC by this command.

The default private work directory is
`%LOCALAPPDATA%\QuickPanelRelease\<version>-<commit>`. It contains the signed
assets, a preparation receipt, and inspection/download copies. Keep it for the
Publish step. It is outside the Git checkout and is never uploaded wholesale.
Use `-WorkDirectory` for a different private location. Use `-Mode Prepare` if you
want to build and verify without contacting GitHub to create a draft.

Draft creation pins the target commit. A repeat invocation verifies and reuses
the signed files; it does not re-sign them. It may fill missing draft assets
after verifying existing ones, but it refuses conflicting assets, notes, tags,
commits, and already-public releases. Nothing is uploaded with `--clobber`.

## Test the signed ZIP, then publish those exact bytes

Test the signed package in a disposable Windows environment. Complete the
relevant [release QA](../QA.md), including the exact legacy bridge chain when
claiming upgrade support. Review the publisher identity and the screenshots in
the draft. A successful signature check does not establish GUI correctness.

When the signed ZIP has passed acceptance, use the SHA-256 printed by the draft
command. In the same checkout and PowerShell session:

```powershell
$acceptedHash = Read-Host 'SHA-256 of the signed ZIP you tested'
& .\scripts\release-public.ps1 -Mode Publish `
  -ReviewedCommit $reviewedCommit -SigningCertificateThumbprint $thumbprint `
  -FinalGuiAccepted -AcceptedZipSha256 $acceptedHash
```

`-FinalGuiAccepted` is your attestation that the signed artifact passed the
manual checks. Publish does not build, sign, replace assets, or infer acceptance.
It downloads the draft files, checks them against the local receipt, verifies
signatures and hashes again, then publishes. Finally, it downloads all three
files without credentials and verifies their hashes. If that final check fails,
the command reports failure and the release may already be public; inspect it
before taking further action.

Source and media changes are committed separately. Neither command pushes
arbitrary local files, your certificate, browser profiles, or private reports.

## Verification boundary

The helper's behavior tests cover invalid/dirty source, missing signing inputs,
unsigned or tampered packages, unexpected assets, conflicting tags/notes,
partial draft recovery, publication gating, and anonymous-download failures.
Trusted signing and a live GitHub release must be exercised on the signing PC;
the preparation VM has no trusted signing certificate. GitHub and the successful
Authenticode boundary are simulated in these tests; the unsigned-file rejection
and Git/ZIP/checksum checks use real fixtures.
