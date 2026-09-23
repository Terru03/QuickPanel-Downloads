[CmdletBinding(DefaultParameterSetName = 'ExplicitPaths')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'ExplicitPaths')][string]$PackagePath,
    [Parameter(Mandatory = $true, ParameterSetName = 'ExplicitPaths')][string]$ManifestPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'ExplicitPaths')][string]$ChecksumsPath,
    [Parameter(Mandatory = $true, ParameterSetName = 'ArtifactsDirectory')][string]$ArtifactsDirectory,
    [string]$PreviousTag = 'v2.4.6',
    [ValidateSet('AIQuickPanel.exe', 'QuickPanel.exe')][string]$TargetExecutableName = 'QuickPanel.exe',
    [string]$ReportPath = '',
    [string]$DotNetPath = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = [string]($props.Project.PropertyGroup.Version | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Directory.Build.props must contain a three-part Version value.'
}
if ($PSCmdlet.ParameterSetName -eq 'ArtifactsDirectory') {
    $artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
    if (-not (Test-Path -LiteralPath $artifacts -PathType Container)) {
        throw "Release artifacts directory was not found: '$artifacts'."
    }

    $packageName = [IO.Path]::GetFileNameWithoutExtension($TargetExecutableName)
    $PackagePath = Join-Path $artifacts "$packageName-$version-win-x64.zip"
    $ManifestPath = Join-Path $artifacts 'version.json'
    $ChecksumsPath = Join-Path $artifacts 'SHA256SUMS.txt'
}

$package = [IO.Path]::GetFullPath($PackagePath)
$manifest = [IO.Path]::GetFullPath($ManifestPath)
$checksums = [IO.Path]::GetFullPath($ChecksumsPath)
foreach ($required in @($package, $manifest, $checksums)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Release verification input was not found: '$required'."
    }
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $packageDirectory = Split-Path -Parent $package
    $verificationDirectory = Join-Path (Split-Path -Parent $packageDirectory) 'verification'
    $ReportPath = Join-Path $verificationDirectory (([IO.Path]::GetFileNameWithoutExtension($package)) + '-verification-report.json')
}
$report = [IO.Path]::GetFullPath($ReportPath)
$reportDirectory = Split-Path -Parent $report
if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
}
$previousVersionText = $PreviousTag.TrimStart('v')
if ($previousVersionText -notmatch '^\d+\.\d+\.\d+$') {
    throw "PreviousTag must identify a three-part semantic version tag. Received '$PreviousTag'."
}

$dotnet = if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $command = Get-Command 'dotnet.exe' -ErrorAction Stop
    $command.Source
} else {
    [IO.Path]::GetFullPath($DotNetPath)
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "The .NET SDK host was not found: '$dotnet'."
}

$tempParent = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'QuickPanel.ReleaseVerification')
$workRoot = Join-Path $tempParent ([Guid]::NewGuid().ToString('N'))
$sourceArchive = Join-Path $workRoot 'previous-source.zip'
$sourceRoot = Join-Path $workRoot 'previous-source'
$previousPublish = Join-Path $workRoot 'previous-publish'
New-Item -ItemType Directory -Path $workRoot, $sourceRoot, $previousPublish -Force | Out-Null

try {
    git rev-parse --verify "$PreviousTag^{commit}" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Previous release tag '$PreviousTag' was not found."
    }
    git archive --format=zip --output=$sourceArchive $PreviousTag
    if ($LASTEXITCODE -ne 0) {
        throw "Could not export previous release tag '$PreviousTag'."
    }
    Expand-Archive -LiteralPath $sourceArchive -DestinationPath $sourceRoot

    $previousProject = Join-Path $sourceRoot 'QuickPanel.csproj'
    if (-not (Test-Path -LiteralPath $previousProject -PathType Leaf)) {
        $previousProject = Join-Path $sourceRoot 'AIQuickPanel.csproj'
    }
    & $dotnet publish $previousProject `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $previousPublish
    if ($LASTEXITCODE -ne 0) {
        throw "Could not build the actual $PreviousTag publish fixture."
    }

    & $dotnet run `
        --project (Join-Path $repoRoot 'tools\QuickPanel.ReleaseVerifier\QuickPanel.ReleaseVerifier.csproj') `
        --configuration Release `
        -- `
        --package $package `
        --manifest $manifest `
        --checksums $checksums `
        --previous-publish $previousPublish `
        --previous-version $previousVersionText `
        --target-version $version `
        --target-executable $TargetExecutableName `
        --work-root $workRoot `
        --report $report
    if ($LASTEXITCODE -ne 0) {
        throw 'Release package verification failed.'
    }
}
finally {
    $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot)
    $expectedPrefix = [IO.Path]::GetFullPath($tempParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedWorkRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected release-verification directory: '$resolvedWorkRoot'."
    }
    if (Test-Path -LiteralPath $resolvedWorkRoot -PathType Container) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}
