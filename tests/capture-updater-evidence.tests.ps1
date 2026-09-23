Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$collector = Join-Path $repoRoot 'scripts\capture-updater-evidence.ps1'
$sandboxRoot = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'QuickPanel.Tests'))
$sandbox = Join-Path $sandboxRoot ('updater-evidence-' + [Guid]::NewGuid().ToString('N'))
$junctions = New-Object 'System.Collections.Generic.List[string]'

function Write-Manifest {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Entries
    )

    $json = [ordered]@{ schemaVersion = 1; entries = $Entries } | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText(
        (Join-Path $Root 'application-files.json'),
        $json,
        [Text.UTF8Encoding]::new($false))
}

function Invoke-Collector {
    param(
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$Output,
        [string]$Baseline,
        [switch]$FailOnMismatch
    )

    $arguments = @{
        Phase = $Phase
        OutputPath = $Output
        InstallDirectory = $script:install
        CanonicalDataDirectory = $script:canonicalData
        TempUpdateRoot = $script:tempUpdateRoot
        SkipWindowsEvents = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($Baseline)) {
        $arguments.BaselinePath = $Baseline
    }
    if ($FailOnMismatch) {
        $arguments.FailOnMismatch = $true
    }
    & $collector @arguments | Out-Null
}

try {
    $script:install = Join-Path $sandbox 'LocalAppData\Programs\AIQuickPanel'
    $localAppData = Join-Path $sandbox 'LocalAppData'
    $physicalProfile = Join-Path $sandbox 'Downloads\AIQuickPanel'
    $script:canonicalData = Join-Path $localAppData 'AIQuickPanel\data'
    $script:tempUpdateRoot = Join-Path $sandbox 'Temp\QuickPanelUpdate'
    $evidenceRoot = Join-Path $sandbox 'Evidence'
    New-Item -ItemType Directory -Path $script:install, $localAppData, $physicalProfile, $script:tempUpdateRoot, $evidenceRoot -Force | Out-Null

    $terminalAlias = Join-Path $physicalProfile 'data'
    & $env:ComSpec /d /c ('mklink /J "' + $terminalAlias + '" "' + $physicalProfile + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the terminal evidence-collector junction fixture.'
    }
    $junctions.Add($terminalAlias)

    $parentAlias = Join-Path $localAppData 'AIQuickPanel'
    & $env:ComSpec /d /c ('mklink /J "' + $parentAlias + '" "' + $physicalProfile + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the parent evidence-collector junction fixture.'
    }
    $junctions.Add($parentAlias)

    New-Item -ItemType Directory -Path (Join-Path $physicalProfile 'WebView2\Default\Network'), (Join-Path $physicalProfile 'IconCache\Websites') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $physicalProfile 'settings.json') -Value '{"selectedTab":"https://private.example.invalid","startup":true}'
    Set-Content -LiteralPath (Join-Path $physicalProfile 'profiles.json') -Value '{"profiles":["Work"]}'
    Set-Content -LiteralPath (Join-Path $physicalProfile 'WebView2\Default\Network\Cookies') -Value 'cookie-session-secret-marker'
    Set-Content -LiteralPath (Join-Path $physicalProfile 'IconCache\Websites\icon.png') -Value 'icon-bytes'

    Set-Content -LiteralPath (Join-Path $script:install 'AIQuickPanel.exe') -Value 'old-executable'
    Set-Content -LiteralPath (Join-Path $script:install 'AIQuickPanel.dll') -Value 'old-library'
    Set-Content -LiteralPath (Join-Path $script:install 'operator-owned.txt') -Value 'preserve-operator-entry'
    Write-Manifest -Root $script:install -Entries @('AIQuickPanel.exe', 'AIQuickPanel.dll', 'application-files.json')

    $attempt = Join-Path $script:tempUpdateRoot '0123456789abcdef0123456789abcdef'
    $payload = Join-Path $attempt 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $payload 'AIQuickPanel.exe') -Value 'new-executable'
    $downloadedZip = Join-Path $sandbox 'downloaded-update.zip'
    Set-Content -LiteralPath $downloadedZip -Value 'zip-evidence'
    $request = [ordered]@{
        Operation = 'update'
        ProcessId = 4321
        PayloadDirectory = $payload
        InstallDirectory = $script:install
        CanonicalDataDirectory = $physicalProfile
        ApplicationExecutable = (Join-Path $script:install 'AIQuickPanel.exe')
        RollbackDirectory = ($physicalProfile + '-maintenance\update-backups\before-update')
        DownloadedZip = $downloadedZip
        LogPath = (Join-Path $attempt 'update.log')
        RestartApplication = $true
    }
    [IO.File]::WriteAllText(
        (Join-Path $attempt 'update-request.json'),
        ($request | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))
    Set-Content -LiteralPath (Join-Path $attempt 'update.log') -Value '[test] stage=worker-process-started; worker-pid=9876'
    Set-Content -LiteralPath (Join-Path $attempt 'update-startup.log') -Value '[test] worker entry'

    $maintenanceRoot = $physicalProfile + '-maintenance'
    $durableLogRoot = Join-Path $maintenanceRoot 'update-logs'
    $backup = Join-Path $maintenanceRoot 'update-backups\before-update'
    New-Item -ItemType Directory -Path $durableLogRoot, $backup -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $durableLogRoot 'update-test.log') -Value '[test] stage=failure; synthetic failure'
    Set-Content -LiteralPath (Join-Path $backup 'AIQuickPanel.exe') -Value 'backup-executable'
    Write-Manifest -Root $backup -Entries @('AIQuickPanel.exe', 'application-files.json')

    $beforePath = Join-Path $evidenceRoot 'before.json'
    Invoke-Collector -Phase Before -Output $beforePath
    $before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
    if (-not $before.Profile.Inventory.Complete -or
        $before.Profile.Inventory.FileCount -ne 4 -or
        $before.Profile.Topology.ReparsePoints.Count -ne 2 -or
        $before.Updater.Temp.Attempts.Count -ne 1 -or
        $before.Updater.Temp.Attempts[0].DownloadedArchiveErrors.Count -ne 0 -or
        $before.Updater.Maintenance.DurableLogs.Count -ne 1 -or
        $before.Updater.Maintenance.Backups.Count -ne 1 -or
        -not $before.ContainsSensitiveDiagnosticEvidence -or
        -not ([string]$before.SharingWarning).Contains('review and redact', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Evidence collector did not capture the complete synthetic updater/profile topology.'
    }

    $rawBefore = Get-Content -LiteralPath $beforePath -Raw
    foreach ($privateValue in @(
        'private.example.invalid',
        'cookie-session-secret-marker',
        'preserve-operator-entry',
        'Cookies',
        'icon.png'
    )) {
        if ($rawBefore.Contains($privateValue, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Evidence collector exposed profile or unrelated-install content/name: '$privateValue'."
        }
    }

    Set-Content -LiteralPath (Join-Path $script:install 'AIQuickPanel.exe') -Value 'new-executable'
    Set-Content -LiteralPath (Join-Path $script:install 'AIQuickPanel.dll') -Value 'new-library'
    $afterPath = Join-Path $evidenceRoot 'after.json'
    Invoke-Collector -Phase AfterClosed -Output $afterPath -Baseline $beforePath -FailOnMismatch
    $after = Get-Content -LiteralPath $afterPath -Raw | ConvertFrom-Json
    if (-not $after.Comparison.Pass -or
        -not $after.Comparison.Profile.Unchanged -or
        -not $after.Comparison.JunctionTopologyUnchanged -or
        -not $after.Comparison.UnownedInstall.Unchanged) {
        throw 'Evidence collector did not prove an unchanged profile, junction topology, and unrelated install inventory.'
    }

    Set-Content -LiteralPath (Join-Path $physicalProfile 'settings.json') -Value '{"changed":true}'
    $changedPath = Join-Path $evidenceRoot 'changed.json'
    $mismatchRejected = $false
    try {
        Invoke-Collector -Phase AfterClosed -Output $changedPath -Baseline $beforePath -FailOnMismatch
    }
    catch {
        $mismatchRejected = $true
    }
    $changed = Get-Content -LiteralPath $changedPath -Raw | ConvertFrom-Json
    if (-not $mismatchRejected -or
        $changed.Comparison.Profile.Unchanged -or
        $changed.Comparison.Profile.ChangedPathIds.Count -ne 1) {
        throw 'Evidence collector did not identify and reject a byte-level profile change.'
    }

    $unsafeOutputRejected = $false
    try {
        Invoke-Collector -Phase Failure -Output (Join-Path $physicalProfile 'unsafe-evidence.json')
    }
    catch {
        $unsafeOutputRejected = $true
    }
    if (-not $unsafeOutputRejected) {
        throw 'Evidence collector allowed its report to be written inside the persistent profile.'
    }

    $maintenanceOutput = Join-Path $maintenanceRoot 'update-logs\unsafe-evidence.json'
    $maintenanceOutputRejected = $false
    try {
        Invoke-Collector -Phase Failure -Output $maintenanceOutput
    }
    catch {
        $maintenanceOutputRejected = $true
    }
    if (-not $maintenanceOutputRejected -or (Test-Path -LiteralPath $maintenanceOutput)) {
        throw 'Evidence collector allowed its report to overwrite updater maintenance evidence.'
    }

    $outputAlias = Join-Path $sandbox 'output-alias-to-profile'
    & $env:ComSpec /d /c ('mklink /J "' + $outputAlias + '" "' + $physicalProfile + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not create the aliased evidence-output fixture.'
    }
    $junctions.Add($outputAlias)
    $aliasedOutputRejected = $false
    try {
        Invoke-Collector -Phase Failure -Output (Join-Path $outputAlias 'aliased-evidence.json')
    }
    catch {
        $aliasedOutputRejected = $true
    }
    if (-not $aliasedOutputRejected -or
        (Test-Path -LiteralPath (Join-Path $physicalProfile 'aliased-evidence.json'))) {
        throw 'Evidence collector followed an output junction into the persistent profile.'
    }

    $existingOutput = Join-Path $evidenceRoot 'existing.json'
    Set-Content -LiteralPath $existingOutput -Value 'existing-evidence-must-survive'
    $existingOutputRejected = $false
    try {
        Invoke-Collector -Phase Failure -Output $existingOutput
    }
    catch {
        $existingOutputRejected = $true
    }
    if (-not $existingOutputRejected -or
        (Get-Content -LiteralPath $existingOutput -Raw).Trim() -cne 'existing-evidence-must-survive') {
        throw 'Evidence collector replaced an existing output file.'
    }

    $missingBaselineOutput = Join-Path $evidenceRoot 'missing-baseline.json'
    $missingBaselineRejected = $false
    try {
        Invoke-Collector -Phase AfterClosed -Output $missingBaselineOutput -FailOnMismatch
    }
    catch {
        $missingBaselineRejected = $true
    }
    if (-not $missingBaselineRejected -or (Test-Path -LiteralPath $missingBaselineOutput)) {
        throw 'Evidence collector wrote a report before rejecting -FailOnMismatch without a baseline.'
    }

    $baselineHash = (Get-FileHash -LiteralPath $beforePath -Algorithm SHA256).Hash
    $baselineReuseRejected = $false
    try {
        Invoke-Collector -Phase AfterClosed -Output $beforePath -Baseline $beforePath
    }
    catch {
        $baselineReuseRejected = $true
    }
    if (-not $baselineReuseRejected -or
        (Get-FileHash -LiteralPath $beforePath -Algorithm SHA256).Hash -ne $baselineHash) {
        throw 'Evidence collector overwrote the trusted comparison baseline.'
    }

    $linkedAttempt = Join-Path $script:tempUpdateRoot 'fedcba9876543210fedcba9876543210'
    New-Item -ItemType Directory -Path $linkedAttempt -Force | Out-Null
    $unrelatedSecret = Join-Path $sandbox 'unrelated-sensitive.txt'
    Set-Content -LiteralPath $unrelatedSecret -Value 'must-not-enter-evidence'
    New-Item -ItemType HardLink -Path (Join-Path $linkedAttempt 'update.log') -Target $unrelatedSecret | Out-Null
    $linkedInputOutput = Join-Path $evidenceRoot 'linked-input.json'
    $linkedInputRejected = $false
    try {
        Invoke-Collector -Phase Failure -Output $linkedInputOutput
    }
    catch {
        $linkedInputRejected = $true
    }
    if (-not $linkedInputRejected -or (Test-Path -LiteralPath $linkedInputOutput)) {
        throw 'Evidence collector followed a multi-link updater artifact.'
    }

    Remove-Item -LiteralPath $linkedAttempt -Recurse -Force
    $privateArchive = Join-Path $physicalProfile 'private-reference.zip'
    Set-Content -LiteralPath $privateArchive -Value 'private-reference-content'
    $request.DownloadedZip = $privateArchive
    [IO.File]::WriteAllText(
        (Join-Path $attempt 'update-request.json'),
        ($request | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))
    $protectedReferenceOutput = Join-Path $evidenceRoot 'protected-reference.json'
    Invoke-Collector -Phase Failure -Output $protectedReferenceOutput
    $protectedReference = Get-Content -LiteralPath $protectedReferenceOutput -Raw | ConvertFrom-Json
    $protectedAttempt = @($protectedReference.Updater.Temp.Attempts |
        Where-Object { $_.Directory -ieq $attempt })[0]
    if ($protectedAttempt.DownloadedArchives.Count -ne 0 -or
        $protectedAttempt.DownloadedArchiveErrors.Count -ne 1 -or
        (Get-Content -LiteralPath $protectedReferenceOutput -Raw).Contains(
            'private-reference-content',
            [StringComparison]::Ordinal)) {
        throw 'Evidence collector followed a protected request-controlled archive reference.'
    }

    $durableSecret = Join-Path $sandbox 'durable-sensitive.txt'
    Set-Content -LiteralPath $durableSecret -Value 'must-not-enter-durable-evidence'
    New-Item -ItemType HardLink -Path (Join-Path $durableLogRoot 'linked.log') -Target $durableSecret | Out-Null
    $linkedDurableOutput = Join-Path $evidenceRoot 'linked-durable.json'
    $linkedDurableRejected = $false
    try {
        Invoke-Collector -Phase Failure -Output $linkedDurableOutput
    }
    catch {
        $linkedDurableRejected = $true
    }
    if (-not $linkedDurableRejected -or (Test-Path -LiteralPath $linkedDurableOutput)) {
        throw 'Evidence collector followed a multi-link durable updater log.'
    }

    Write-Output 'Updater evidence capture tests pass.'
}
finally {
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
