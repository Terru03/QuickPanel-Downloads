Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts\profile-data-safety.ps1')

$sandboxRoot = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'QuickPanel.Tests'))
$sandbox = Join-Path $sandboxRoot ('powershell-update-' + [Guid]::NewGuid().ToString('N'))
$originalLocalAppData = $env:LOCALAPPDATA
$junctions = New-Object 'System.Collections.Generic.List[string]'

try {
    $payload = Join-Path $sandbox 'payload'
    $sharedTarget = Join-Path $sandbox 'Downloads-like'
    $localAppData = Join-Path $sandbox 'LocalAppData'
    $physicalProfile = Join-Path $sandbox 'physical-profile'
    New-Item -ItemType Directory -Path $payload, $sharedTarget, $localAppData, $physicalProfile -Force | Out-Null

    $terminalAlias = Join-Path $physicalProfile 'data'
    & $env:ComSpec /d /c ('mklink /J "' + $terminalAlias + '" "' + $physicalProfile + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the terminal profile junction for the PowerShell safety test.'
    }
    $junctions.Add($terminalAlias)

    $parentAlias = Join-Path $localAppData 'AIQuickPanel'
    & $env:ComSpec /d /c ('mklink /J "' + $parentAlias + '" "' + $physicalProfile + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the parent profile junction for the PowerShell safety test.'
    }
    $junctions.Add($parentAlias)
    $env:LOCALAPPDATA = $localAppData

    $strictTarget = Join-Path $sandbox 'strict-reparse-target'
    $strictChild = Join-Path $strictTarget 'ordinary-child'
    New-Item -ItemType Directory -Path $strictChild -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $strictChild 'payload.bin') -Value 'not safe through a junction'
    $strictAlias = Join-Path $sandbox 'strict-reparse-alias'
    & $env:ComSpec /d /c ('mklink /J "' + $strictAlias + '" "' + $strictTarget + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the intermediate-junction path safety fixture.'
    }
    $junctions.Add($strictAlias)
    $intermediateJunctionRejected = $false
    try {
        Assert-NoReparsePointPath -Path (Join-Path $strictAlias 'ordinary-child\payload.bin')
    }
    catch {
        $intermediateJunctionRejected = $true
    }
    if (-not $intermediateJunctionRejected) {
        throw 'PowerShell path safety missed an intermediate junction when the final file was ordinary.'
    }

    $resolvedProfile = Get-LegacyQuickPanelPhysicalDataDirectory
    if ($resolvedProfile -ine [IO.Path]::GetFullPath($physicalProfile).TrimEnd('\', '/')) {
        throw 'The PowerShell updater did not resolve the parent-plus-terminal junction profile physically.'
    }

    $currentPhysicalProfile = Join-Path $sandbox 'current-physical-profile'
    $currentCanonicalProduct = Join-Path $localAppData 'QuickPanel'
    $currentCanonicalData = Join-Path $currentCanonicalProduct 'data'
    New-Item -ItemType Directory -Path $currentPhysicalProfile, $currentCanonicalProduct -Force | Out-Null
    & $env:ComSpec /d /c ('mklink /J "' + $currentCanonicalData + '" "' + $currentPhysicalProfile + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the current Quick Panel profile junction for the PowerShell safety test.'
    }
    $junctions.Add($currentCanonicalData)
    $currentPhysicalTargetRejected = $false
    try {
        Assert-InstallTargetSafe -TargetDirectory $currentPhysicalProfile
    }
    catch {
        $currentPhysicalTargetRejected = $true
    }
    if (-not $currentPhysicalTargetRejected) {
        throw 'Install target safety missed the physical target of the current Quick Panel profile junction.'
    }

    Set-Content -LiteralPath (Join-Path $payload 'AIQuickPanel.exe') -Value 'next'
    New-ApplicationFilesManifest -PayloadDirectory $payload
    $manifestBytes = [IO.File]::ReadAllBytes((Join-Path $payload 'application-files.json'))
    if ($manifestBytes.Length -ge 3 -and
        $manifestBytes[0] -eq 0xEF -and
        $manifestBytes[1] -eq 0xBB -and
        $manifestBytes[2] -eq 0xBF) {
        throw 'The application-files manifest must be UTF-8 without a byte-order mark.'
    }
    Assert-PayloadSafe -PayloadDirectory $payload

    $forbiddenPayload = Join-Path $sandbox 'forbidden-payload'
    New-Item -ItemType Directory -Path $forbiddenPayload -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $forbiddenPayload 'AIQuickPanel.exe') -Value 'next'
    Set-Content -LiteralPath (Join-Path $forbiddenPayload 'AIQuickPanel.pdb') -Value 'debug paths'
    New-ApplicationFilesManifest -PayloadDirectory $forbiddenPayload
    $forbiddenRejected = $false
    try {
        Assert-PayloadSafe -PayloadDirectory $forbiddenPayload
    }
    catch {
        $forbiddenRejected = $true
    }
    if (-not $forbiddenRejected) {
        throw 'The PowerShell release policy accepted a debug-symbol payload.'
    }

    foreach ($name in @('AIQuickPanel.exe', 'AIQuickPanel.dll', 'AIQuickPanel.deps.json', 'AIQuickPanel.runtimeconfig.json')) {
        Set-Content -LiteralPath (Join-Path $sharedTarget $name) -Value 'old'
    }
    Set-Content -LiteralPath (Join-Path $sharedTarget 'irreplaceable-photo.jpg') -Value 'keep'
    $rejected = $false
    try {
        Install-QuickPanelPayload -PayloadDirectory $payload -TargetDirectory $sharedTarget
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected -or (Get-Content -LiteralPath (Join-Path $sharedTarget 'irreplaceable-photo.jpg') -Raw).Trim() -ne 'keep') {
        throw 'The PowerShell updater accepted a shared folder or changed an unrelated file.'
    }

    $legacyTarget = Join-Path $sandbox 'legacy-install'
    New-Item -ItemType Directory -Path (Join-Path $legacyTarget 'data') -Force | Out-Null
    foreach ($name in @('AIQuickPanel.exe', 'AIQuickPanel.dll', 'AIQuickPanel.deps.json', 'AIQuickPanel.runtimeconfig.json', 'System.Management.dll')) {
        Set-Content -LiteralPath (Join-Path $legacyTarget $name) -Value 'old'
    }
    Set-Content -LiteralPath (Join-Path $legacyTarget 'data\settings.json') -Value '{"preserve":true}'
    $profileHash = (Get-FileHash -LiteralPath (Join-Path $legacyTarget 'data\settings.json') -Algorithm SHA256).Hash
    Install-QuickPanelPayload -PayloadDirectory $payload -TargetDirectory $legacyTarget
    if ((Get-FileHash -LiteralPath (Join-Path $legacyTarget 'data\settings.json') -Algorithm SHA256).Hash -ne $profileHash -or
        (Test-Path -LiteralPath (Join-Path $legacyTarget 'System.Management.dll')) -or
        -not (Test-Path -LiteralPath (Join-Path $legacyTarget 'application-files.json') -PathType Leaf)) {
        throw 'The PowerShell updater did not preserve profile data or replace only owned application files.'
    }

    $profileOnlyTarget = Join-Path $sandbox 'profile-only-install'
    New-Item -ItemType Directory -Path (Join-Path $profileOnlyTarget 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $profileOnlyTarget 'data\settings.json') -Value '{"legacy":true}'
    $profileOnlyHash = (Get-FileHash -LiteralPath (Join-Path $profileOnlyTarget 'data\settings.json') -Algorithm SHA256).Hash
    Install-QuickPanelPayload -PayloadDirectory $payload -TargetDirectory $profileOnlyTarget -AllowProfileOnlyTarget
    if ((Get-FileHash -LiteralPath (Join-Path $profileOnlyTarget 'data\settings.json') -Algorithm SHA256).Hash -ne $profileOnlyHash -or
        -not (Test-Path -LiteralPath (Join-Path $profileOnlyTarget 'AIQuickPanel.exe') -PathType Leaf)) {
        throw 'Clean installation over a profile-only legacy target did not preserve its data.'
    }

    Write-Output 'PowerShell profile/update safety tests pass.'
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    for ($index = $junctions.Count - 1; $index -ge 0; $index--) {
        $junction = $junctions[$index]
        if (Test-Path -LiteralPath $junction) {
            $junctionItem = Get-Item -LiteralPath $junction -Force
            if (($junctionItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                throw "Refusing to remove a cleanup path that is no longer a junction: '$junction'."
            }
            [IO.Directory]::Delete($junction, $false)
        }
    }
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    $expectedPrefix = $sandboxRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedSandbox.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedSandbox -PathType Container)) {
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force
    }
}
