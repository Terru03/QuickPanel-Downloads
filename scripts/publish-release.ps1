param(
    [string]$Version = "",

    [Parameter(Mandatory = $true)]
    [string]$DownloadBaseUrl,

    [string]$ReleaseNotesUrl = "",

    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",

    [string]$OutputDirectory = "artifacts\release",

    [string]$SigningCertificateThumbprint = "",

    [string]$SignToolPath = "",

    [string]$TimestampUrl = "https://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'profile-data-safety.ps1')
. (Join-Path $PSScriptRoot 'public-release-safety.ps1')

function Test-AllowedUpdateUrl {
    param([string]$Url)

    $uri = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri)) {
        return $false
    }
    if ($uri.Scheme -eq "https") {
        return $true
    }
    if ($uri.Scheme -ne "http") {
        return $false
    }

    $hostName = $uri.Host
    return $hostName -ieq "localhost" `
        -or $hostName -ieq "127.0.0.1" `
        -or $hostName -eq "::1" `
        -or $hostName.EndsWith(".localhost", [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-CentralVersion {
    param([string]$RepoRoot)

    $propsPath = Join-Path $RepoRoot "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Directory.Build.props was not found. Pass -Version explicitly or restore the central version file."
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $versionNode = $props.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "Directory.Build.props does not contain a Version value."
    }
    $centralVersion = [string]$versionNode
    $expectedFileVersion = "$centralVersion.0"
    $assemblyVersion = [string]($props.Project.PropertyGroup.AssemblyVersion | Select-Object -First 1)
    $fileVersion = [string]($props.Project.PropertyGroup.FileVersion | Select-Object -First 1)
    $informationalVersion = [string]($props.Project.PropertyGroup.InformationalVersion | Select-Object -First 1)
    if ($assemblyVersion -ne $expectedFileVersion -or
        $fileVersion -ne $expectedFileVersion -or
        $informationalVersion -ne $centralVersion) {
        throw "Directory.Build.props version fields are inconsistent with Version $centralVersion."
    }
    return $centralVersion
}

function Resolve-SignTool {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "SignToolPath was not found: '$resolved'."
        }
        return $resolved
    }

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Filter 'signtool.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }
    throw 'Signing was requested, but signtool.exe could not be found. Install the Windows SDK or pass -SignToolPath.'
}

function Invoke-OptionalSigning {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDirectory,
        [string]$CertificateThumbprint,
        [string]$RequestedSignTool,
        [string]$TimestampServer
    )

    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        return 'Unsigned (no certificate thumbprint supplied)'
    }
    $thumbprint = $CertificateThumbprint.Replace(' ', '')
    if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'SigningCertificateThumbprint must be a 40-character certificate-store thumbprint.'
    }
    if (-not (Test-AllowedUpdateUrl $TimestampServer)) {
        throw 'TimestampUrl must use HTTPS. Localhost HTTP is allowed for testing.'
    }
    $signTool = Resolve-SignTool -RequestedPath $RequestedSignTool
    $binaries = @(
        (Join-Path $PublishDirectory 'QuickPanel.exe'),
        (Join-Path $PublishDirectory 'QuickPanel.dll'),
        (Join-Path $PublishDirectory 'UpdaterRuntime\QuickPanel.Updater.exe'),
        (Join-Path $PublishDirectory 'UpdaterRuntime\QuickPanel.Updater.dll'),
        (Join-Path $PublishDirectory 'UpdaterRuntime\QuickPanel.Updater.Core.dll')
    )
    foreach ($binary in $binaries) {
        if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw "Expected release binary was not found for signing: '$binary'."
        }
        & $signTool sign /sha1 $thumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $binary
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed for '$binary'."
        }
        & $signTool verify /pa /all $binary
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode verification failed for '$binary'."
        }
    }
    return "Signed and verified with certificate $($thumbprint.Substring($thumbprint.Length - 8))"
}

function Assert-ReleaseArchiveMatchesPayload {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$PayloadDirectory
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expected = @(Get-ChildItem -LiteralPath $PayloadDirectory -File -Recurse |
        ForEach-Object { [IO.Path]::GetRelativePath($PayloadDirectory, $_.FullName).Replace('\', '/') } |
        Sort-Object)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $actual = @($archive.Entries |
            Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
            ForEach-Object {
                if ($_.FullName.StartsWith('/') -or $_.FullName.Contains('../')) {
                    throw "Release ZIP contains an unsafe path: '$($_.FullName)'."
                }
                $_.FullName.Replace('\', '/')
            } |
            Sort-Object)
        if (($expected -join "`n") -cne ($actual -join "`n")) {
            throw 'Release ZIP contents do not exactly match the validated publish payload.'
        }
    }
    finally {
        $archive.Dispose()
    }
}

if (-not (Test-AllowedUpdateUrl $DownloadBaseUrl)) {
    throw "DownloadBaseUrl must use HTTPS. Localhost HTTP is allowed for testing."
}

function Assert-NoDeveloperOnlyPayloadFiles {
    param([Parameter(Mandatory = $true)][string]$PayloadDirectory)

    $forbiddenNames = @('QA.md', 'RELEASE.md', 'version.example.json')
    $forbidden = @(Get-ChildItem -LiteralPath $PayloadDirectory -File -Recurse |
        Where-Object { $forbiddenNames -icontains $_.Name })
    if ($forbidden.Count -gt 0) {
        $relativeNames = @($forbidden |
            ForEach-Object { [IO.Path]::GetRelativePath($PayloadDirectory, $_.FullName) }) -join ', '
        throw "Public release payload contains developer-only files: $relativeNames"
    }
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesUrl) -and -not (Test-AllowedUpdateUrl $ReleaseNotesUrl)) {
    throw "ReleaseNotesUrl must use HTTPS. Localhost HTTP is allowed for testing."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-CentralVersion -RepoRoot $repoRoot
}
$centralVersion = Get-CentralVersion -RepoRoot $repoRoot
if ($Version -ne $centralVersion) {
    throw "Requested release version '$Version' does not match central version '$centralVersion'."
}

$expectedPublicBase = "https://github.com/Terru03/QuickPanel-Downloads/releases/download/v$Version"
if ($DownloadBaseUrl.TrimEnd('/') -cne $expectedPublicBase) {
    throw "Public download URL must be '$expectedPublicBase'."
}
$expectedNotesUrl = "https://github.com/Terru03/QuickPanel-Downloads/releases/tag/v$Version"
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesUrl) -and $ReleaseNotesUrl -cne $expectedNotesUrl) {
    throw "Public release notes URL must be '$expectedNotesUrl'."
}

$publishRoot = Join-Path $repoRoot "artifacts\publish"
$publishDir = Join-Path $publishRoot ("$Version-$Runtime-" + [Guid]::NewGuid().ToString('N'))
$outputDirFull = Join-Path $repoRoot $OutputDirectory
$zipName = "QuickPanel-$Version-$Runtime.zip"
$zipPath = Join-Path $outputDirFull $zipName
$manifestPath = Join-Path $outputDirFull "version.json"
$checksumsPath = Join-Path $outputDirFull "SHA256SUMS.txt"
$publicFileNames = @($zipName, 'version.json', 'SHA256SUMS.txt')

Assert-PublicHandoffDirectoryClean -OutputDirectory $outputDirFull -ExpectedFileNames $publicFileNames
New-Item -ItemType Directory -Path $outputDirFull -Force | Out-Null
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$resolvedPublishDir = [IO.Path]::GetFullPath($publishDir)
$resolvedPublishRoot = [IO.Path]::GetFullPath($publishRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
try {
    dotnet build (Join-Path $repoRoot "QuickPanel.sln") --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet publish (Join-Path $repoRoot "QuickPanel.csproj") `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained release publish failed.' }

    $updaterPublishDir = Join-Path $publishDir 'UpdaterRuntime'
    dotnet publish (Join-Path $repoRoot "QuickPanel.Updater\QuickPanel.Updater.csproj") `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        --output $updaterPublishDir
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained updater publish failed.' }

    $signingStatus = Invoke-OptionalSigning `
        -PublishDirectory $publishDir `
        -CertificateThumbprint $SigningCertificateThumbprint `
        -RequestedSignTool $SignToolPath `
        -TimestampServer $TimestampUrl

    New-ApplicationFilesManifest -PayloadDirectory $publishDir
    Assert-PayloadSafe -PayloadDirectory $publishDir
    Assert-NoDeveloperOnlyPayloadFiles -PayloadDirectory $publishDir
    Assert-NoMachineSpecificPayloadText -PayloadDirectory $publishDir -RepositoryRoot $repoRoot

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
    Assert-ReleaseArchiveMatchesPayload -ArchivePath $zipPath -PayloadDirectory $publishDir

    $sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $downloadUrl = $DownloadBaseUrl.TrimEnd("/") + "/" + $zipName
    $manifest = [ordered]@{
        latest = $Version
        downloadUrl = $downloadUrl
        sha256 = $sha256
        releaseNotesUrl = $ReleaseNotesUrl
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 4
    Write-Utf8NoBomFile -Path $manifestPath -Content $manifestJson

    $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumText = "$sha256  $zipName`n$manifestSha256  version.json`n"
    Write-Utf8NoBomFile -Path $checksumsPath -Content $checksumText
    Assert-PublicHandoffDirectoryClean `
        -OutputDirectory $outputDirFull `
        -ExpectedFileNames $publicFileNames `
        -RequireAll

    [pscustomobject]@{
        Version = $Version
        Runtime = $Runtime
        SelfContained = $true
        SigningStatus = $signingStatus
        ZipPath = $zipPath
        Sha256 = $sha256
        VersionJson = $manifestPath
        Sha256Sums = $checksumsPath
        DownloadUrl = $downloadUrl
    }
}
finally {
    if (-not $resolvedPublishDir.StartsWith($resolvedPublishRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected publish staging directory: '$resolvedPublishDir'."
    }
    if (Test-Path -LiteralPath $resolvedPublishDir -PathType Container) {
        Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
    }
}
