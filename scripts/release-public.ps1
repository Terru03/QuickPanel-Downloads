#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Draft', 'Publish')][string]$Mode = 'Draft',
    [string]$ReviewedCommit = '',
    [string]$Version = '2.5.0',
    [string]$SigningCertificateThumbprint = '',
    [string]$SignToolPath = '',
    [string]$WorkDirectory = '',
    [switch]$FinalGuiAccepted,
    [string]$AcceptedZipSha256 = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'profile-data-safety.ps1')
. (Join-Path $PSScriptRoot 'public-release-safety.ps1')

function Invoke-ReleaseCommand {
    param([string]$FilePath, [string[]]$ArgumentList)
    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed (exit $LASTEXITCODE): $($output -join "`n")" }
    return ($output -join "`n")
}

function ConvertTo-ReleaseThumbprint([string]$Value) {
    $value = $Value.Replace(' ', '').ToUpperInvariant()
    if ($value -notmatch '^[A-F0-9]{40}$') { throw 'An explicit 40-character signing certificate thumbprint is required.' }
    return $value
}

function Get-ReleaseAssetNames([string]$Version) {
    return @("QuickPanel-$Version-win-x64.zip", 'version.json', 'SHA256SUMS.txt')
}

function ConvertTo-PublicReleaseNotes([string]$Template, [string]$Commit) {
    if ($Commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Release notes need the reviewed commit.' }
    $rendered = [regex]::Replace($Template, '(?s)<!-- SOURCE_PREVIEW_NOTICE_START -->.*?<!-- SOURCE_PREVIEW_NOTICE_END -->', '').Trim()
    $rendered = $rendered.Replace('{{SOURCE_COMMIT}}', $Commit)
    if ($rendered.Contains('{{') -or $rendered.Contains('SOURCE_PREVIEW_NOTICE')) { throw 'Release notes contain an unresolved template marker.' }
    return $rendered
}

function Assert-ReleaseCheckout([string]$RepositoryRoot, [string]$ReviewedCommit, [string]$Version) {
    if ($ReviewedCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'ReviewedCommit must be the full reviewed commit SHA.' }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'A three-part version is required.' }
    $origin = Invoke-ReleaseCommand git @('-C', $RepositoryRoot, 'remote', 'get-url', 'origin')
    $allowed = @('https://github.com/Terru03/QuickPanel-Downloads.git', 'https://github.com/Terru03/QuickPanel-Downloads', 'git@github.com:Terru03/QuickPanel-Downloads.git')
    if ($allowed -cnotcontains $origin.Trim()) { throw 'The checkout origin must be exactly Terru03/QuickPanel-Downloads.' }
    $head = Invoke-ReleaseCommand git @('-C', $RepositoryRoot, 'rev-parse', 'HEAD')
    if ($head.Trim() -ine $ReviewedCommit) { throw 'HEAD does not match the reviewed commit.' }
    $status = Invoke-ReleaseCommand git @('-C', $RepositoryRoot, 'status', '--porcelain', '--untracked-files=all')
    if (-not [string]::IsNullOrWhiteSpace($status)) { throw 'Release checkout must be clean, including untracked files.' }
    [xml]$props = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
    $group = $props.Project.PropertyGroup | Where-Object { $null -ne $_.Version } | Select-Object -First 1
    if ($group.Version -cne $Version -or $group.AssemblyVersion -cne "$Version.0" -or $group.FileVersion -cne "$Version.0" -or $group.InformationalVersion -cne $Version) {
        throw 'Requested version and central assembly/file/informational versions must match.'
    }
}

function Assert-ReleaseWorkDirectory([string]$RepositoryRoot, [string]$Path) {
    $repo = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\','/')
    $work = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ($work -ieq $repo -or $work.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WorkDirectory must be outside the source checkout to keep local evidence out of public source/assets.'
    }
    Assert-NoReparsePointPath $work
    Assert-NoReparsePointTree $work
}

function Get-ReleasePublisherOutputPath([string]$RepositoryRoot, [string]$AssetDirectory) {
    return [IO.Path]::GetRelativePath(
        [IO.Path]::GetFullPath($RepositoryRoot),
        [IO.Path]::GetFullPath($AssetDirectory))
}

function Assert-ReleaseSignature([string]$Path, [string]$Thumbprint, [string]$SignToolPath) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ine $Thumbprint -or $null -eq $signature.TimeStamperCertificate) {
        throw "Release signature must be trusted, timestamped, and from the requested certificate: '$([IO.Path]::GetFileName($Path))'."
    }
    Invoke-ReleaseCommand $SignToolPath @('verify', '/pa', '/all', $Path) | Out-Null
}

function Assert-ReleaseBundle([string]$AssetDirectory, [string]$Version, [string]$Thumbprint, [string]$SignToolPath, [string]$WorkDirectory, [string]$RepositoryRoot) {
    $names = @(Get-ReleaseAssetNames $Version)
    Assert-NoReparsePointTree $AssetDirectory
    Assert-PublicHandoffDirectoryClean $AssetDirectory $names -RequireAll
    $hashes = [ordered]@{}
    foreach ($name in $names) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $AssetDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $expectedChecksums = "$($hashes[$names[0]])  $($names[0])`n$($hashes['version.json'])  version.json"
    $checksums = (Get-Content -LiteralPath (Join-Path $AssetDirectory 'SHA256SUMS.txt') -Raw).Replace("`r`n", "`n").TrimEnd("`n")
    if ($checksums -cne $expectedChecksums) { throw 'Release checksum file does not exactly match the ZIP and version.json.' }
    $manifest = Get-Content -LiteralPath (Join-Path $AssetDirectory 'version.json') -Raw | ConvertFrom-Json
    $base = "https://github.com/Terru03/QuickPanel-Downloads/releases"
    if ($manifest.latest -cne $Version -or $manifest.sha256 -cne $hashes[$names[0]] -or $manifest.downloadUrl -cne "$base/download/v$Version/$($names[0])" -or $manifest.releaseNotesUrl -cne "$base/tag/v$Version") {
        throw 'Release manifest version, hash, or public URLs do not match.'
    }
    if ((@($manifest.PSObject.Properties.Name | Sort-Object) -join ',') -cne 'downloadUrl,latest,releaseNotesUrl,sha256') { throw 'Unexpected release manifest fields.' }
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $AssetDirectory $names[0]))
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $path = $entry.FullName.Replace('\', '/')
            $segments = $path.TrimEnd('/').Split('/')
            if ($path.StartsWith('/') -or $path.Contains(':') -or $segments.Where({ $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_ -match '[. ]$|[<>"|?*\x00-\x1f]' }).Count -gt 0 -or -not $seen.Add($path.TrimEnd('/')) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "Unsafe or duplicate archive path: '$path'."
            }
        }
    } finally { $archive.Dispose() }
    $inspection = Join-Path $WorkDirectory ('inspection-' + [Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $AssetDirectory $names[0]), $inspection)
    Assert-NoReparsePointTree $inspection
    Assert-PayloadSafe -PayloadDirectory $inspection
    Assert-NoMachineSpecificPayloadText -PayloadDirectory $inspection -RepositoryRoot $RepositoryRoot -AdditionalForbiddenText $WorkDirectory
    foreach ($relative in @('QuickPanel.exe','QuickPanel.dll','UpdaterRuntime/QuickPanel.Updater.exe','UpdaterRuntime/QuickPanel.Updater.dll','UpdaterRuntime/QuickPanel.Updater.Core.dll')) {
        $binary = Join-Path $inspection $relative
        if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing first-party binary: '$relative'." }
        Assert-ReleaseSignature $binary $Thumbprint $SignToolPath
    }
    return $hashes
}

function Assert-RemoteRelease($Release, $Context, [switch]$AllowPublished) {
    if (-not $Release.draft -and -not $AllowPublished) { throw 'Release is already public; refusing to overwrite it.' }
    if ($Release.tag_name -cne "v$($Context.Version)" -or $Release.target_commitish -cne $Context.Commit) { throw 'Remote release tag or target commit differs from the reviewed commit.' }
    if ($Release.prerelease -or $Release.name -cne "Quick Panel $($Context.Version)") { throw 'Remote release title/type differs.' }
    if ($Release.body.Replace("`r`n", "`n").TrimEnd() -cne $Context.Notes.Replace("`r`n", "`n").TrimEnd()) { throw 'Remote release notes differ from the reviewed notes.' }
    $names = @(Get-ReleaseAssetNames $Context.Version)
    $actual = @($Release.assets | ForEach-Object { $_.name })
    if (@($actual | Select-Object -Unique).Count -ne $actual.Count -or @($actual | Where-Object { $names -cnotcontains $_ }).Count -gt 0 -or @($Release.assets | Where-Object { $_.state -cne 'uploaded' }).Count -gt 0) {
        throw 'Remote release contains unexpected, duplicate, or incomplete assets; no assets will be overwritten.'
    }
}

function Assert-PublishAcceptance($Context, [bool]$FinalGuiAccepted, [string]$AcceptedZipSha256) {
    if (-not $FinalGuiAccepted) { throw 'Publish requires -FinalGuiAccepted after manual GUI acceptance of the signed ZIP.' }
    if ($AcceptedZipSha256 -inotmatch '^[0-9a-f]{64}$' -or $AcceptedZipSha256 -ine $Context.Hashes["QuickPanel-$($Context.Version)-win-x64.zip"]) {
        throw 'The accepted ZIP SHA-256 must match the prepared signed ZIP.'
    }
}

function Invoke-ReleaseApi([string]$Endpoint) {
    return (Invoke-ReleaseCommand gh @('api', $Endpoint) | ConvertFrom-Json)
}

function Get-PublicRelease([string]$Version) {
    # A failed request is never interpreted as an absent release.
    $pages = Invoke-ReleaseCommand gh @('api', 'repos/Terru03/QuickPanel-Downloads/releases?per_page=100', '--paginate', '--slurp') | ConvertFrom-Json
    $matches = @($pages | ForEach-Object { $_ } | Where-Object { $_.tag_name -ceq "v$Version" })
    if ($matches.Count -gt 1) { throw 'Multiple releases match this version.' }
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}

function Assert-PublicRemote($Context) {
    $metadata = Invoke-ReleaseApi 'repos/Terru03/QuickPanel-Downloads'
    if ($metadata.full_name -cne 'Terru03/QuickPanel-Downloads' -or $metadata.private) { throw 'Expected public Terru03/QuickPanel-Downloads repository.' }
    $commit = Invoke-ReleaseApi "repos/Terru03/QuickPanel-Downloads/commits/$($Context.Commit)"
    if ($commit.sha -cne $Context.Commit) { throw 'Reviewed commit is not present in the public repository.' }
    $refs = @(Invoke-ReleaseApi "repos/Terru03/QuickPanel-Downloads/git/matching-refs/tags/v$($Context.Version)")
    if (@($refs | Where-Object { $_.ref -ceq "refs/tags/v$($Context.Version)" }).Count -gt 0) {
        $target = Invoke-ReleaseApi "repos/Terru03/QuickPanel-Downloads/commits/v$($Context.Version)"
        if ($target.sha -cne $Context.Commit) { throw 'Existing public tag points to a different commit.' }
    }
}

function Save-RemoteReleaseAssets($Release, $Context) {
    $directory = Join-Path $Context.WorkDirectory ('download-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    foreach ($asset in $Release.assets) {
        Invoke-ReleaseCommand gh @('release', 'download', "v$($Context.Version)", '--repo', 'Terru03/QuickPanel-Downloads', '--pattern', $asset.name, '--dir', $directory) | Out-Null
        $hash = (Get-FileHash -LiteralPath (Join-Path $directory $asset.name) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $Context.Hashes[$asset.name]) { throw "Uploaded asset differs from prepared artifact: '$($asset.name)'." }
    }
    return $directory
}

function Invoke-ReleaseHandoff($Context, [string]$Mode, [bool]$FinalGuiAccepted, [string]$AcceptedZipSha256) {
    if ($Mode -eq 'Publish') { Assert-PublishAcceptance $Context $FinalGuiAccepted $AcceptedZipSha256 }
    Assert-PublicRemote $Context
    $release = Get-PublicRelease $Context.Version
    if ($null -eq $release) {
        if ($Mode -eq 'Publish') { throw 'Publish requires an existing verified draft.' }
        $paths = @(Get-ReleaseAssetNames $Context.Version | ForEach-Object { Join-Path $Context.AssetDirectory $_ })
        Invoke-ReleaseCommand gh (@('release','create',"v$($Context.Version)",'--repo','Terru03/QuickPanel-Downloads','--target',$Context.Commit,'--draft','--title',"Quick Panel $($Context.Version)",'--notes-file',$Context.NotesPath) + $paths) | Out-Null
        $release = Get-PublicRelease $Context.Version
        if ($null -eq $release) { throw 'Created draft could not be read back.' }
    }
    Assert-RemoteRelease $release $Context
    $download = Save-RemoteReleaseAssets $release $Context
    $present = @($release.assets | ForEach-Object { $_.name })
    $missing = @(Get-ReleaseAssetNames $Context.Version | Where-Object { $present -cnotcontains $_ })
    if ($missing.Count -gt 0) {
        if ($Mode -eq 'Publish') { throw 'Draft is missing required assets; run Draft to resume it first.' }
        $paths = @($missing | ForEach-Object { Join-Path $Context.AssetDirectory $_ })
        Invoke-ReleaseCommand gh (@('release','upload',"v$($Context.Version)",'--repo','Terru03/QuickPanel-Downloads') + $paths) | Out-Null
        $release = Get-PublicRelease $Context.Version
        Assert-RemoteRelease $release $Context
        $download = Save-RemoteReleaseAssets $release $Context
    }
    Assert-ReleaseBundle $download $Context.Version $Context.Thumbprint $Context.SignToolPath $Context.WorkDirectory $Context.RepositoryRoot | Out-Null
    if ($Mode -eq 'Draft') { return "Verified draft v$($Context.Version). Signed ZIP SHA-256: $($Context.Hashes["QuickPanel-$($Context.Version)-win-x64.zip"])" }
    # Recheck the tag immediately before publishing. Never rebuild/re-sign in Publish mode.
    Assert-PublicRemote $Context
    $latestDraft = Get-PublicRelease $Context.Version
    Assert-RemoteRelease $latestDraft $Context
    $verifiedIds = @($release.assets | Sort-Object name | ForEach-Object { "$($_.name):$($_.id)" }) -join "`n"
    $latestIds = @($latestDraft.assets | Sort-Object name | ForEach-Object { "$($_.name):$($_.id)" }) -join "`n"
    if ($latestIds -cne $verifiedIds) { throw 'Draft assets changed after verification; publication was stopped.' }
    Invoke-ReleaseCommand gh @('release','edit',"v$($Context.Version)",'--repo','Terru03/QuickPanel-Downloads','--draft=false') | Out-Null
    $publicRelease = Get-PublicRelease $Context.Version
    Assert-RemoteRelease $publicRelease $Context -AllowPublished
    if ($publicRelease.draft) { throw 'GitHub still reports a draft after publish.' }
    $anonymous = Join-Path $Context.WorkDirectory ('anonymous-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $anonymous | Out-Null
    foreach ($name in Get-ReleaseAssetNames $Context.Version) {
        # No gh token, Authorization header, or authenticated web session is supplied.
        Invoke-WebRequest -Uri "https://github.com/Terru03/QuickPanel-Downloads/releases/download/v$($Context.Version)/$name" -OutFile (Join-Path $anonymous $name)
        if ((Get-FileHash -LiteralPath (Join-Path $anonymous $name)).Hash -ine $Context.Hashes[$name]) { throw "Public release exists but anonymous download verification failed for '$name'." }
    }
    return "Published v$($Context.Version); all three anonymous download hashes match the verified signed draft."
}

# Dot sourcing exposes the same validation functions for behavioral tests without running a release.
if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $IsWindows) { throw 'Release signing requires Windows and PowerShell 7.' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$thumbprint = ConvertTo-ReleaseThumbprint $SigningCertificateThumbprint
$ReviewedCommit = $ReviewedCommit.ToLowerInvariant()
Assert-ReleaseCheckout $repositoryRoot $ReviewedCommit $Version
if ([string]::IsNullOrWhiteSpace($WorkDirectory)) { $WorkDirectory = Join-Path $env:LOCALAPPDATA "QuickPanelRelease\$Version-$ReviewedCommit" }
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
Assert-ReleaseWorkDirectory $repositoryRoot $WorkDirectory
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
    $tool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $tool) { $SignToolPath = $tool.Source }
    else {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $tool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -File | Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($null -ne $tool) { $SignToolPath = $tool.FullName }
    }
}
if ([string]::IsNullOrWhiteSpace($SignToolPath) -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'Install Windows SDK SignTool or pass -SignToolPath.' }
$notesSourcePath = Join-Path $repositoryRoot "docs/public-release-notes-$Version.md"
Invoke-ReleaseCommand git @('-C',$repositoryRoot,'ls-files','--error-unmatch',"docs/public-release-notes-$Version.md") | Out-Null
$notes = ConvertTo-PublicReleaseNotes (Get-Content -LiteralPath $notesSourcePath -Raw) $ReviewedCommit
$notesPath = Join-Path $WorkDirectory 'release-notes.md'
Set-Content -LiteralPath $notesPath -Value $notes -Encoding utf8NoBOM
$notesHash = (Get-FileHash -LiteralPath $notesSourcePath).Hash.ToLowerInvariant()
$assetDirectory = Join-Path $WorkDirectory 'assets'
$receiptPath = Join-Path $WorkDirectory 'receipt.json'
if (-not (Test-Path -LiteralPath $receiptPath)) {
    if ($Mode -eq 'Publish') { throw 'Publish requires the original signed preparation receipt; it never rebuilds.' }
    if (Test-Path -LiteralPath $assetDirectory) { throw 'Unrecorded assets exist. Keep them for diagnosis and use a fresh WorkDirectory.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction Stop
    $codeSigningUsages = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } | ForEach-Object { $_.EnhancedKeyUsages } | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })
    if (-not $certificate.HasPrivateKey -or $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date) -or $codeSigningUsages.Count -eq 0) { throw 'Certificate must be valid for code signing and have an accessible private key in CurrentUser/My.' }
    Invoke-ReleaseCommand dotnet @('restore', (Join-Path $repositoryRoot 'QuickPanel.sln')) | Write-Host
    Invoke-ReleaseCommand dotnet @('build', (Join-Path $repositoryRoot 'QuickPanel.sln'), '--configuration', 'Release', '--no-restore') | Write-Host
    Invoke-ReleaseCommand dotnet @('run', '--project', (Join-Path $repositoryRoot 'tests/QuickPanel.Tests.csproj'), '--configuration', 'Release', '--no-build') | Write-Host
    foreach ($test in @('profile-data-safety.tests.ps1','public-release-safety.tests.ps1','capture-updater-evidence.tests.ps1','release-public.tests.ps1')) {
        Invoke-ReleaseCommand (Join-Path $PSHOME 'pwsh.exe') @('-NoProfile','-File',(Join-Path $repositoryRoot "tests/$test")) | Write-Host
    }
    $publisherOutput = Get-ReleasePublisherOutputPath $repositoryRoot $assetDirectory
    & (Join-Path $PSScriptRoot 'publish-release.ps1') -Version $Version -DownloadBaseUrl "https://github.com/Terru03/QuickPanel-Downloads/releases/download/v$Version" -ReleaseNotesUrl "https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v$Version" -SigningCertificateThumbprint $thumbprint -SignToolPath $SignToolPath -OutputDirectory $publisherOutput | Out-Host
    Assert-ReleaseCheckout $repositoryRoot $ReviewedCommit $Version
    $hashes = Assert-ReleaseBundle $assetDirectory $Version $thumbprint $SignToolPath $WorkDirectory $repositoryRoot
    [ordered]@{schema=1;commit=$ReviewedCommit;version=$Version;thumbprint=$thumbprint;notesSha256=$notesHash;hashes=$hashes} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
if ($receipt.schema -ne 1 -or $receipt.commit -cne $ReviewedCommit -or $receipt.version -cne $Version -or $receipt.thumbprint -cne $thumbprint -or $receipt.notesSha256 -cne $notesHash) { throw 'Preparation receipt differs from the reviewed commit, version, signer, or notes.' }
$hashes = Assert-ReleaseBundle $assetDirectory $Version $thumbprint $SignToolPath $WorkDirectory $repositoryRoot
foreach ($name in Get-ReleaseAssetNames $Version) { if ($receipt.hashes[$name] -cne $hashes[$name]) { throw "Prepared asset changed since signing: '$name'." } }
$context = @{Version=$Version;Commit=$ReviewedCommit;Notes=$notes;NotesPath=$notesPath;AssetDirectory=$assetDirectory;WorkDirectory=$WorkDirectory;RepositoryRoot=$repositoryRoot;Thumbprint=$thumbprint;SignToolPath=$SignToolPath;Hashes=$hashes}
if ($Mode -eq 'Prepare') { Write-Output "Prepared signed assets: $assetDirectory`nSigned ZIP SHA-256: $($hashes["QuickPanel-$Version-win-x64.zip"])"; return }
Assert-ReleaseCheckout $repositoryRoot $ReviewedCommit $Version
Invoke-ReleaseHandoff $context $Mode $FinalGuiAccepted.IsPresent $AcceptedZipSha256
