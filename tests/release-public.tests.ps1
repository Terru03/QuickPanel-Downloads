#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$helper = Join-Path $repo 'scripts/release-public.ps1'
if (-not (Test-Path -LiteralPath $helper)) { throw 'Missing signed-release handoff; safety behaviors are not implemented.' }
. $helper
$script:passed = 0
function Check([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Output "PASS $Name"
}
function Reject([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw "Wrong rejection: $($_.Exception.Message); expected $Pattern" }
        return
    }
    throw "Expected rejection: $Pattern"
}
function Equal($Actual, $Expected) {
    if ($Actual -cne $Expected) { throw "Expected '$Expected', got '$Actual'." }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('QuickPanel.ReleaseHandoff.Tests.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $fixture = Join-Path $root 'repo'
    New-Item -ItemType Directory -Path $fixture | Out-Null
    & git -C $fixture init --quiet
    & git -C $fixture config user.name 'Release Test'
    & git -C $fixture config user.email 'release-test@example.invalid'
    & git -C $fixture remote add origin 'https://github.com/Terru03/QuickPanel-Downloads.git'
    '<Project><PropertyGroup><Version>2.5.0</Version><AssemblyVersion>2.5.0.0</AssemblyVersion><FileVersion>2.5.0.0</FileVersion><InformationalVersion>2.5.0</InformationalVersion></PropertyGroup></Project>' | Set-Content (Join-Path $fixture 'Directory.Build.props')
    'Release notes.' | Set-Content (Join-Path $fixture 'notes.md')
    & git -C $fixture add -- Directory.Build.props notes.md
    & git -C $fixture commit --quiet -m fixture
    $commit = (& git -C $fixture rev-parse HEAD).Trim()
    Check 'Accept clean exact public checkout' { Assert-ReleaseCheckout $fixture $commit '2.5.0' }
    Check 'Reject source mismatch' { Reject { Assert-ReleaseCheckout $fixture ('a' * 40) '2.5.0' } 'reviewed commit' }
    Check 'Reject version mismatch' { Reject { Assert-ReleaseCheckout $fixture $commit '2.5.1' } 'version' }
    Check 'Reject dirty tracked files' {
        Add-Content (Join-Path $fixture 'notes.md') 'change'
        Reject { Assert-ReleaseCheckout $fixture $commit '2.5.0' } 'clean'
        & git -C $fixture restore -- notes.md
    }
    Check 'Reject untracked build inputs' {
        'class Surprise {}' | Set-Content (Join-Path $fixture 'Surprise.cs')
        Reject { Assert-ReleaseCheckout $fixture $commit '2.5.0' } 'clean'
        Remove-Item -LiteralPath (Join-Path $fixture 'Surprise.cs')
    }
    Check 'Reject lookalike origin' {
        & git -C $fixture remote set-url origin 'https://github.com/Terru03/QuickPanel-Downloads.git.evil'
        Reject { Assert-ReleaseCheckout $fixture $commit '2.5.0' } 'origin'
        & git -C $fixture remote set-url origin 'https://github.com/Terru03/QuickPanel-Downloads.git'
    }
    Check 'Reject output inside checkout' { Reject { Assert-ReleaseWorkDirectory $fixture (Join-Path $fixture 'artifacts') } 'outside' }
    Check 'Resolve publisher output outside checkout' {
        $relative = Get-ReleasePublisherOutputPath $fixture (Join-Path $root 'assets')
        Equal ([IO.Path]::GetFullPath((Join-Path $fixture $relative))) (Join-Path $root 'assets')
        Equal ([IO.Path]::IsPathRooted($relative)) $false
    }
    Check 'Reject missing thumbprint' { Reject { ConvertTo-ReleaseThumbprint '' } 'thumbprint' }
    Check 'Normalize explicit thumbprint' { Equal (ConvertTo-ReleaseThumbprint (('ab ' * 20).Trim())) ('AB' * 20) }
    Check 'Render release notes with pinned media and no source-preview notice' {
        $template = "<!-- SOURCE_PREVIEW_NOTICE_START -->`nNot released yet.`n<!-- SOURCE_PREVIEW_NOTICE_END -->`n# Release`nhttps://example.invalid/{{SOURCE_COMMIT}}/image.jpg"
        $rendered = ConvertTo-PublicReleaseNotes $template $commit
        Equal ($rendered -match 'Not released yet|SOURCE_PREVIEW_NOTICE|\{\{') $false
        Equal ($rendered.Contains("https://example.invalid/$commit/image.jpg")) $true
    }
    Check 'Propagate native failures' { Reject { Invoke-ReleaseCommand 'git' @('-C', $fixture, 'this-command-does-not-exist') } 'failed' }

    $assets = Join-Path $root 'assets'
    $payload = Join-Path $root 'payload'
    New-Item -ItemType Directory -Path $assets,$payload,(Join-Path $payload 'UpdaterRuntime') | Out-Null
    foreach ($name in @('QuickPanel.exe','QuickPanel.dll','UpdaterRuntime/QuickPanel.Updater.exe','UpdaterRuntime/QuickPanel.Updater.dll','UpdaterRuntime/QuickPanel.Updater.Core.dll')) {
        'unsigned fixture' | Set-Content (Join-Path $payload $name)
    }
    function Refresh-Bundle {
        New-ApplicationFilesManifest -PayloadDirectory $payload
        $zip = Join-Path $assets 'QuickPanel-2.5.0-win-x64.zip'
        if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
        [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
        $zipHash = (Get-FileHash $zip).Hash.ToLowerInvariant()
        @{latest='2.5.0';downloadUrl='https://github.com/Terru03/QuickPanel-Downloads/releases/download/v2.5.0/QuickPanel-2.5.0-win-x64.zip';sha256=$zipHash;releaseNotesUrl='https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v2.5.0'} | ConvertTo-Json | Set-Content (Join-Path $assets 'version.json')
        $manifestHash = (Get-FileHash (Join-Path $assets 'version.json')).Hash.ToLowerInvariant()
        "$zipHash  QuickPanel-2.5.0-win-x64.zip`n$manifestHash  version.json" | Set-Content (Join-Path $assets 'SHA256SUMS.txt')
    }
    Refresh-Bundle
    Check 'Reject unsigned exact archive' { Reject { Assert-ReleaseBundle $assets '2.5.0' ('A' * 40) 'unused' $root $fixture } 'signature' }
    # Authenticode needs a trusted certificate, unavailable in CI. Replace only that OS boundary.
    function Assert-ReleaseSignature { param($Path,$Thumbprint,$SignToolPath) }
    Check 'Accept matching archive manifest and checksums' {
        $hashes = Assert-ReleaseBundle $assets '2.5.0' ('A' * 40) 'unused' $root $fixture
        Equal $hashes.Count 3
    }
    Check 'Reject a tampered manifest' {
        Add-Content (Join-Path $assets 'version.json') 'tampered'
        Reject { Assert-ReleaseBundle $assets '2.5.0' ('A' * 40) 'unused' $root $fixture } 'checksum'
        Refresh-Bundle
    }
    Check 'Reject extra public files' {
        'secret' | Set-Content (Join-Path $assets 'private-key.pfx')
        Reject { Assert-ReleaseBundle $assets '2.5.0' ('A' * 40) 'unused' $root $fixture } 'allowlist'
        Remove-Item -LiteralPath (Join-Path $assets 'private-key.pfx')
    }
    Check 'Reject sensitive archive contents' {
        'secret' | Set-Content (Join-Path $payload 'private-key.pfx')
        Refresh-Bundle
        Reject { Assert-ReleaseBundle $assets '2.5.0' ('A' * 40) 'unused' $root $fixture } 'persistent profile'
        Remove-Item -LiteralPath (Join-Path $payload 'private-key.pfx')
        Refresh-Bundle
    }
    Check 'Reject traversal before extraction' {
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $assets 'QuickPanel-2.5.0-win-x64.zip'), 'Update')
        $null = $zip.CreateEntry('../escape.txt')
        $zip.Dispose()
        $z = (Get-FileHash (Join-Path $assets 'QuickPanel-2.5.0-win-x64.zip')).Hash.ToLowerInvariant()
        $manifest = Get-Content (Join-Path $assets 'version.json') -Raw | ConvertFrom-Json
        $manifest.sha256 = $z
        $manifest | ConvertTo-Json | Set-Content (Join-Path $assets 'version.json')
        $m = (Get-FileHash (Join-Path $assets 'version.json')).Hash.ToLowerInvariant()
        "$z  QuickPanel-2.5.0-win-x64.zip`n$m  version.json" | Set-Content (Join-Path $assets 'SHA256SUMS.txt')
        Reject { Assert-ReleaseBundle $assets '2.5.0' ('A' * 40) 'unused' $root $fixture } 'archive path'
        Equal (Test-Path (Join-Path $root 'escape.txt')) $false
        Refresh-Bundle
    }
    $context = @{Version='2.5.0';Commit=$commit;Notes='Release notes.';AssetDirectory=$assets;WorkDirectory=$root;RepositoryRoot=$fixture;Thumbprint=('A'*40);SignToolPath='unused';Hashes=(Assert-ReleaseBundle $assets '2.5.0' ('A'*40) 'unused' $root $fixture)}
    $release = [pscustomobject]@{id=101;draft=$true;prerelease=$false;tag_name='v2.5.0';target_commitish=$commit;name='Quick Panel 2.5.0';body='Release notes.';assets=@()}
    Check 'Accept matching draft' { Assert-RemoteRelease $release $context }
    Check 'Reject changed target' { $release.target_commitish='main'; Reject { Assert-RemoteRelease $release $context } 'commit'; $release.target_commitish=$commit }
    Check 'Reject changed notes' { $release.body='Different'; Reject { Assert-RemoteRelease $release $context } 'notes'; $release.body='Release notes.' }
    Check 'Reject extra remote assets' { $release.assets=@([pscustomobject]@{name='secrets.json';state='uploaded'}); Reject { Assert-RemoteRelease $release $context } 'assets'; $release.assets=@() }
    Check 'Reject already public release' { $release.draft=$false; Reject { Assert-RemoteRelease $release $context } 'already public'; $release.draft=$true }
    Check 'Require explicit GUI acceptance for publishing' { Reject { Assert-PublishAcceptance $context $false $context.Hashes['QuickPanel-2.5.0-win-x64.zip'] } 'GUI' }
    Check 'Bind GUI acceptance to exact signed ZIP' { Reject { Assert-PublishAcceptance $context $true ('0'*64) } 'accepted ZIP' }
    Check 'Accept exact signed ZIP attestation' { Assert-PublishAcceptance $context $true $context.Hashes['QuickPanel-2.5.0-win-x64.zip'] }
    $context.NotesPath = Join-Path $fixture 'notes.md'
    $script:remoteRelease = $null
    $script:remoteFiles = @{}
    $script:createCount = 0
    $script:uploadCount = 0
    $script:publishCount = 0
    $script:anonymousCount = 0
    $script:failCommand = ''
    $script:changeAfterDownload = $false
    $script:downloadCompleted = $false
    $script:tagCommit = ''
    $script:tamperAnonymous = $false
    $remoteStore = Join-Path $root 'remote'
    New-Item -ItemType Directory -Path $remoteStore | Out-Null
    # These doubles replace network I/O only. Git/ZIP/hash validation remains real.
    function Invoke-ReleaseCommand {
        param($FilePath,$ArgumentList)
        if ($FilePath -ne 'gh') { throw "Unexpected native dependency: $FilePath" }
        if ($script:failCommand -eq $ArgumentList[1]) { throw 'Injected GitHub failure.' }
        if ($ArgumentList[0] -eq 'api') {
            $endpoint = $ArgumentList[1]
            if ($endpoint -eq 'repos/Terru03/QuickPanel-Downloads') {
                if ($script:changeAfterDownload -and $script:downloadCompleted) { $script:remoteRelease.assets[0].id = 999 }
                return '{"full_name":"Terru03/QuickPanel-Downloads","private":false}'
            }
            if ($endpoint -eq "repos/Terru03/QuickPanel-Downloads/commits/$commit") { return "{`"sha`":`"$commit`"}" }
            if ($endpoint -eq 'repos/Terru03/QuickPanel-Downloads/commits/v2.5.0') { return "{`"sha`":`"$script:tagCommit`"}" }
            if ($endpoint -like '*/git/matching-refs/*') {
                if ($script:tagCommit) { return '[{"ref":"refs/tags/v2.5.0","object":{"type":"commit"}}]' }
                return '[]'
            }
            if ($endpoint -eq 'repos/Terru03/QuickPanel-Downloads/releases?per_page=100') {
                if ($null -eq $script:remoteRelease) { return '[[]]' }
                return '[[' + ($script:remoteRelease | ConvertTo-Json -Depth 5 -Compress) + ']]'
            }
            throw "Unexpected endpoint: $endpoint"
        }
        if ($ArgumentList -contains '--clobber') { throw 'Forbidden overwrite requested.' }
        if ($ArgumentList[1] -eq 'create') {
            $script:createCount++
            $target = $ArgumentList[[Array]::IndexOf($ArgumentList, '--target') + 1]
            $body = Get-Content -LiteralPath $ArgumentList[[Array]::IndexOf($ArgumentList, '--notes-file') + 1] -Raw
            $script:remoteRelease = [pscustomobject]@{id=101;draft=$true;prerelease=$false;tag_name=$ArgumentList[2];target_commitish=$target;name='Quick Panel 2.5.0';body=$body;assets=@()}
        }
        if ($ArgumentList[1] -in @('create','upload')) {
            if ($ArgumentList[1] -eq 'upload') { $script:uploadCount++ }
            foreach ($path in $ArgumentList | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }) {
                if ($path -eq $context.NotesPath) { continue }
                $name = [IO.Path]::GetFileName($path)
                if ($script:remoteFiles.ContainsKey($name)) { throw 'Remote assets cannot be replaced.' }
                $destination = Join-Path $remoteStore $name
                Copy-Item -LiteralPath $path -Destination $destination -Force
                $script:remoteFiles[$name] = $destination
                $script:remoteRelease.assets += [pscustomobject]@{id=201;name=$name;state='uploaded'}
            }
            return ''
        }
        if ($ArgumentList[1] -eq 'download') {
            $script:downloadCompleted = $true
            $name = $ArgumentList[[Array]::IndexOf($ArgumentList, '--pattern') + 1]
            $destination = $ArgumentList[[Array]::IndexOf($ArgumentList, '--dir') + 1]
            Copy-Item -LiteralPath $script:remoteFiles[$name] -Destination (Join-Path $destination $name)
            return ''
        }
        if ($ArgumentList[1] -eq 'edit') {
            $script:publishCount++
            $script:remoteRelease.draft = $false
            return ''
        }
        throw "Unexpected GitHub command: $ArgumentList"
    }
    function Invoke-WebRequest {
        param($Uri,$OutFile)
        $script:anonymousCount++
        $name = [IO.Path]::GetFileName(([uri]$Uri).AbsolutePath)
        Copy-Item -LiteralPath $script:remoteFiles[$name] -Destination $OutFile
        if ($script:tamperAnonymous) { Add-Content -LiteralPath $OutFile 'tampered' }
    }
    Check 'Create commit-pinned draft with exactly three assets' {
        Invoke-ReleaseHandoff $context 'Draft' $false '' | Out-Null
        Equal $script:remoteRelease.target_commitish $commit
        Equal $script:remoteRelease.draft $true
        Equal (@($script:remoteFiles.Keys | Sort-Object) -join ',') 'QuickPanel-2.5.0-win-x64.zip,SHA256SUMS.txt,version.json'
    }
    Check 'Rerun matching draft without replacing assets' {
        Invoke-ReleaseHandoff $context 'Draft' $false '' | Out-Null
        Equal $script:createCount 1
        Equal $script:uploadCount 0
    }
    Check 'Resume incomplete matching draft' {
        $script:remoteFiles.Remove('version.json')
        $script:remoteRelease.assets = @($script:remoteRelease.assets | Where-Object { $_.name -ne 'version.json' })
        Invoke-ReleaseHandoff $context 'Draft' $false '' | Out-Null
        Equal $script:remoteFiles.Count 3
        Equal $script:uploadCount 1
    }
    Check 'Refuse remote-byte mismatch before upload or publish' {
        Add-Content -LiteralPath $script:remoteFiles['version.json'] 'tampered'
        Reject { Invoke-ReleaseHandoff $context 'Draft' $false '' } 'Uploaded asset differs'
        Equal $script:uploadCount 1
        Equal $script:publishCount 0
        Copy-Item -LiteralPath (Join-Path $assets 'version.json') -Destination $script:remoteFiles['version.json'] -Force
    }
    Check 'Reject existing tag mismatch' {
        $script:tagCommit = 'a'*40
        Reject { Invoke-ReleaseHandoff $context 'Draft' $false '' } 'tag points'
        Equal $script:publishCount 0
        $script:tagCommit = ''
    }
    Check 'GitHub errors never become absent-release success' {
        $script:failCommand = 'repos/Terru03/QuickPanel-Downloads/releases?per_page=100'
        Reject { Invoke-ReleaseHandoff $context 'Draft' $false '' } 'Injected GitHub failure'
        Equal $script:createCount 1
        $script:failCommand = ''
    }
    Check 'Publish does not fill missing assets' {
        $savedAssets = $script:remoteRelease.assets
        $script:remoteRelease.assets = @($savedAssets | Where-Object { $_.name -ne 'version.json' })
        Reject { Invoke-ReleaseHandoff $context 'Publish' $true $context.Hashes['QuickPanel-2.5.0-win-x64.zip'] } 'missing required assets'
        Equal $script:uploadCount 1
        Equal $script:publishCount 0
        $script:remoteRelease.assets = $savedAssets
    }
    Check 'Reject replaced assets between verification and publication' {
        $script:changeAfterDownload = $true
        $script:downloadCompleted = $false
        Reject { Invoke-ReleaseHandoff $context 'Publish' $true $context.Hashes['QuickPanel-2.5.0-win-x64.zip'] } 'changed after verification'
        Equal $script:publishCount 0
        $script:remoteRelease.assets[0].id = 201
        $script:changeAfterDownload = $false
    }
    Check 'Publish verified draft and check anonymous hashes' {
        Invoke-ReleaseHandoff $context 'Publish' $true $context.Hashes['QuickPanel-2.5.0-win-x64.zip'] | Out-Null
        Equal $script:remoteRelease.draft $false
        Equal $script:publishCount 1
        Equal $script:anonymousCount 3
    }
    Check 'Refuse modifying an already public release' {
        Reject { Invoke-ReleaseHandoff $context 'Draft' $false '' } 'already public'
        Equal $script:uploadCount 1
        Equal $script:createCount 1
    }
    Check 'Report post-publish anonymous mismatch as failure' {
        $script:remoteRelease.draft = $true
        $script:tamperAnonymous = $true
        Reject { Invoke-ReleaseHandoff $context 'Publish' $true $context.Hashes['QuickPanel-2.5.0-win-x64.zip'] } 'Public release exists but anonymous'
        Equal $script:remoteRelease.draft $false
    }
    Write-Output "$script:passed release handoff tests passed. Trusted signing and live GitHub publishing were not exercised."
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# Expected native failures are asserted above. Report success only after every
# assertion and cleanup completed; GitHub's pwsh wrapper checks LASTEXITCODE.
$global:LASTEXITCODE = 0
