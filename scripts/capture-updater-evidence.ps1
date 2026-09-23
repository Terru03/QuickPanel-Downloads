[CmdletBinding()]
param(
    [ValidateSet('Before', 'AfterRunning', 'AfterClosed', 'Failure')]
    [string]$Phase = 'Failure',

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\QuickPanel'),

    [string]$CanonicalDataDirectory = (Join-Path $env:LOCALAPPDATA 'QuickPanel\data'),

    [string]$TempUpdateRoot = (Join-Path $env:TEMP 'QuickPanelUpdate'),

    [string]$BaselinePath,

    [string]$ExpectedVersion,

    [ValidateRange(1, 720)]
    [int]$EventLookbackHours = 168,

    [switch]$SkipWindowsEvents,

    [switch]$FailOnMismatch
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($FailOnMismatch -and [string]::IsNullOrWhiteSpace($BaselinePath)) {
    throw '-FailOnMismatch requires -BaselinePath.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'scripts\profile-data-safety.ps1')

$script:EvidenceSchemaVersion = 1
$script:MaximumTextEvidenceBytes = 1MB
$script:MaximumEventMessageCharacters = 4000
$script:KnownProfileCategories = @(
    'settings.json',
    'profiles.json',
    'external-apps.user.json',
    'performance.json',
    'IconCache',
    'WebView2',
    'logs',
    'sessions'
)

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Test-SameOrDescendantEvidencePath {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $candidatePath = Get-NormalizedDirectoryPath -Path $Candidate
    $rootPath = Get-NormalizedDirectoryPath -Path $Root
    return $candidatePath -ieq $rootPath -or
        $candidatePath.StartsWith(
            $rootPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
}

function Assert-LocalEvidencePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root) -or
        $root.StartsWith('\\', [StringComparison]::Ordinal)) {
        throw "Evidence paths must remain on a local Windows volume: '$fullPath'."
    }

    $drive = [IO.DriveInfo]::new($root)
    if ($drive.DriveType -eq [IO.DriveType]::Network) {
        throw "Evidence paths cannot use a mapped network volume: '$fullPath'."
    }
}

function Assert-SafeEvidenceFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    Assert-LocalEvidencePath -Path $fullPath
    Assert-NoReparsePointPath -Path $fullPath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Evidence input must be an ordinary file: '$fullPath'."
    }

    Initialize-QuickPanelPhysicalDirectoryResolver
    $linkCount = [QuickPanelPowerShellPhysicalDirectoryResolver]::GetLinkCount($fullPath)
    if ($linkCount -ne 1) {
        throw "Evidence input must have exactly one filesystem link: '$fullPath'."
    }
}

function Write-NewEvidenceFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $stream = [IO.FileStream]::new(
        $fullPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($Content)
            $writer.Flush()
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-ProfileCategory {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $firstSegment = $RelativePath.Replace('\', '/').Split('/')[0]
    foreach ($category in $script:KnownProfileCategories) {
        if ($firstSegment -ieq $category) {
            return $category
        }
    }
    return 'Other'
}

function Get-ReparseTargetText {
    param([Parameter(Mandatory = $true)][IO.FileSystemInfo]$Item)

    try {
        $target = $Item.LinkTarget
        if ($null -ne $target) {
            return [string]$target
        }
    }
    catch {
        return $null
    }
    return $null
}

function Get-HashedTreeInventory {
    param(
        [Parameter(Mandatory = $true)][string]$RootDirectory,
        [string[]]$IncludeDirectChildNames,
        [switch]$UseProfileCategories
    )

    $root = Get-NormalizedDirectoryPath -Path $RootDirectory
    Assert-LocalEvidencePath -Path $root
    Assert-NoReparsePointPath -Path $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return [ordered]@{
            Exists = $false
            Complete = $false
            FileCount = 0
            DirectoryCount = 0
            ReparsePointCount = 0
            InventorySha256 = $null
            Files = @()
            DirectoryPathIds = @()
            ReparsePoints = @()
            Errors = @([ordered]@{ PathId = $null; ErrorType = 'DirectoryNotFound' })
        }
    }

    $include = $null
    if ($null -ne $IncludeDirectChildNames) {
        $include = @{}
        foreach ($name in $IncludeDirectChildNames) {
            if (-not [string]::IsNullOrWhiteSpace($name)) {
                $include[[string]$name] = $true
            }
        }
    }

    $files = New-Object 'System.Collections.Generic.List[object]'
    $directoryPathIds = New-Object 'System.Collections.Generic.List[string]'
    $reparsePoints = New-Object 'System.Collections.Generic.List[object]'
    $errors = New-Object 'System.Collections.Generic.List[object]'
    $directories = New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push($root)
    $directoryCount = 0

    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        $directoryCount++
        if ($directory -ine $root) {
            $relativeDirectoryPath = [IO.Path]::GetRelativePath($root, $directory).Replace('\', '/')
            $directoryPathIds.Add((Get-StringSha256 -Value $relativeDirectoryPath.ToLowerInvariant()))
        }
        try {
            $items = @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)
        }
        catch {
            $relativeDirectory = if ($directory -ieq $root) { '.' } else { [IO.Path]::GetRelativePath($root, $directory) }
            $errors.Add([ordered]@{
                PathId = Get-StringSha256 -Value $relativeDirectory.ToLowerInvariant()
                ErrorType = $_.Exception.GetType().Name
            })
            continue
        }

        foreach ($item in $items) {
            $relativePath = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
            $segments = $relativePath.Split('/')
            if ($null -ne $include -and -not $include.ContainsKey($segments[0])) {
                continue
            }

            $pathId = Get-StringSha256 -Value $relativePath.ToLowerInvariant()
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $reparsePoints.Add([ordered]@{
                    PathId = $pathId
                    Attributes = [string]$item.Attributes
                    LinkType = [string]$item.LinkType
                    Target = Get-ReparseTargetText -Item $item
                })
                continue
            }

            if ($item.PSIsContainer) {
                $directories.Push($item.FullName)
                continue
            }

            try {
                $sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
                $record = [ordered]@{
                    PathId = $pathId
                    Length = [long]$item.Length
                    Sha256 = $sha256
                }
                if ($UseProfileCategories) {
                    $record.Category = Get-ProfileCategory -RelativePath $relativePath
                }
                $files.Add($record)
            }
            catch {
                $errors.Add([ordered]@{
                    PathId = $pathId
                    ErrorType = $_.Exception.GetType().Name
                })
            }
        }
    }

    $sortedFiles = @($files | Sort-Object PathId)
    $sortedDirectoryPathIds = @($directoryPathIds | Sort-Object)
    $sortedReparsePoints = @($reparsePoints | Sort-Object PathId)
    $inventoryText = @(
        @($sortedFiles | ForEach-Object {
            'file|{0}|{1}|{2}' -f $_.PathId, $_.Length, $_.Sha256
        })
        @($sortedDirectoryPathIds | ForEach-Object { 'directory|' + $_ })
        @($sortedReparsePoints | ForEach-Object {
            'reparse|{0}|{1}|{2}|{3}' -f $_.PathId, $_.Attributes, $_.LinkType, $_.Target
        })
    ) -join "`n"
    return [ordered]@{
        Exists = $true
        Complete = $errors.Count -eq 0
        FileCount = $files.Count
        DirectoryCount = $directoryCount
        ReparsePointCount = $reparsePoints.Count
        InventorySha256 = Get-StringSha256 -Value $inventoryText
        Files = $sortedFiles
        DirectoryPathIds = $sortedDirectoryPathIds
        ReparsePoints = $sortedReparsePoints
        Errors = @($errors | Sort-Object PathId)
    }
}

function Get-PathReparseTopology {
    param(
        [Parameter(Mandatory = $true)][string]$CanonicalPath,
        [Parameter(Mandatory = $true)][string]$PhysicalPath
    )

    $fullPath = [IO.Path]::GetFullPath($CanonicalPath)
    $current = [IO.Path]::GetPathRoot($fullPath)
    $records = New-Object 'System.Collections.Generic.List[object]'
    $remainder = $fullPath.Substring($current.Length)
    $separators = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    foreach ($segment in $remainder.Split($separators, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) {
            break
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $records.Add([ordered]@{
                Path = [IO.Path]::GetFullPath($current)
                Attributes = [string]$item.Attributes
                LinkType = [string]$item.LinkType
                Target = Get-ReparseTargetText -Item $item
            })
        }
    }

    $canonical = Get-NormalizedDirectoryPath -Path $CanonicalPath
    $physical = Get-NormalizedDirectoryPath -Path $PhysicalPath
    $topologyText = @(
        'canonical=' + $canonical.ToLowerInvariant()
        'physical=' + $physical.ToLowerInvariant()
        @($records | ForEach-Object {
            '{0}|{1}|{2}|{3}' -f $_.Path.ToLowerInvariant(), $_.Attributes, $_.LinkType, $_.Target
        })
    ) -join "`n"
    return [ordered]@{
        CanonicalPath = $canonical
        PhysicalPath = $physical
        ReparsePoints = @($records | ForEach-Object { $_ })
        TopologySha256 = Get-StringSha256 -Value $topologyText
    }
}

function Get-FileEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$IncludeText
    )

    Assert-SafeEvidenceFile -Path $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $evidence = [ordered]@{
        Path = [IO.Path]::GetFullPath($item.FullName)
        Length = [long]$item.Length
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('O')
        Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($IncludeText) {
        if ($item.Length -le $script:MaximumTextEvidenceBytes) {
            try {
                $evidence.Text = Get-Content -LiteralPath $item.FullName -Raw -ErrorAction Stop
                $evidence.TextTruncated = $false
            }
            catch {
                $evidence.Text = $null
                $evidence.TextTruncated = $false
                $evidence.ReadError = $_.Exception.GetType().Name
            }
        }
        else {
            $evidence.Text = $null
            $evidence.TextTruncated = $true
        }
    }
    return $evidence
}

function Get-ExecutableEvidence {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    Assert-LocalEvidencePath -Path $fullPath
    Assert-NoReparsePointPath -Path $fullPath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        return [ordered]@{ Path = $fullPath; Exists = $false }
    }
    Assert-SafeEvidenceFile -Path $fullPath
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath)
    return [ordered]@{
        Path = $fullPath
        Exists = $true
        ProductVersion = $info.ProductVersion
        FileVersion = $info.FileVersion
        Length = (Get-Item -LiteralPath $fullPath).Length
        Sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-RunningApplicationEvidence {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $expected = [IO.Path]::GetFullPath($ExecutablePath)
    $processes = New-Object 'System.Collections.Generic.List[object]'
    foreach ($name in @('QuickPanel', 'AIQuickPanel')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                $path = $process.Path
            }
            catch {
                $path = $null
            }
            $processes.Add([ordered]@{
                ProcessId = $process.Id
                Path = $path
                MatchesInstalledExecutable = $null -ne $path -and
                    [IO.Path]::GetFullPath($path).Equals($expected, [StringComparison]::OrdinalIgnoreCase)
                StartTimeUtc = try { $process.StartTime.ToUniversalTime().ToString('O') } catch { $null }
            })
        }
    }
    return @($processes | Sort-Object ProcessId)
}

function Resolve-QuickPanelExecutablePath {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $root = Get-NormalizedDirectoryPath -Path $Directory
    $existing = @(@('QuickPanel.exe', 'AIQuickPanel.exe') |
        ForEach-Object { Join-Path $root $_ } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($existing.Count -gt 1) {
        throw "Quick Panel executable evidence is ambiguous in '$root'."
    }
    if ($existing.Count -eq 1) {
        return [IO.Path]::GetFullPath($existing[0])
    }
    return Join-Path $root 'QuickPanel.exe'
}

function Get-ApplicationOwnedNames {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $manifestPath = Join-Path $InstallRoot $script:QuickPanelApplicationManifestName
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            Assert-SafeEvidenceFile -Path $manifestPath
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.schemaVersion -eq 1 -and $null -ne $manifest.entries) {
                return @($manifest.entries | ForEach-Object { [string]$_ })
            }
        }
        catch {
            throw "Application ownership manifest could not be inspected safely: $($_.Exception.Message)"
        }
    }
    return @($script:QuickPanelKnownApplicationNames)
}

function Get-UnownedInstallInventory {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $install = Get-NormalizedDirectoryPath -Path $InstallRoot
    if (-not (Test-Path -LiteralPath $install -PathType Container)) {
        return Get-HashedTreeInventory -RootDirectory $install -IncludeDirectChildNames @()
    }
    $owned = @(Get-ApplicationOwnedNames -InstallRoot $install)
    $unowned = @(Get-ChildItem -LiteralPath $install -Force |
        Where-Object { $owned -inotcontains $_.Name } |
        Select-Object -ExpandProperty Name)
    return Get-HashedTreeInventory -RootDirectory $install -IncludeDirectChildNames $unowned
}

function Get-TempUpdaterEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$RootDirectory,
        [Parameter(Mandatory = $true)][string[]]$ProtectedRoots
    )

    $root = Get-NormalizedDirectoryPath -Path $RootDirectory
    Assert-LocalEvidencePath -Path $root
    Assert-NoReparsePointPath -Path $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return [ordered]@{ Root = $root; Exists = $false; Attempts = @() }
    }

    $attemptDirectories = @(Get-ChildItem -LiteralPath $root -Directory -Force |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 } |
        Sort-Object LastWriteTimeUtc -Descending)
    $attempts = New-Object 'System.Collections.Generic.List[object]'
    foreach ($attempt in $attemptDirectories) {
        $files = New-Object 'System.Collections.Generic.List[object]'
        foreach ($name in @(
            'update-request.json',
            'rollback-request.json',
            'update.log',
            'rollback.log',
            'update-startup.log',
            'worker-handoff.json',
            'worker-handoff.required'
        )) {
            $path = Join-Path $attempt.FullName $name
            Assert-SafeEvidenceFile -Path $path
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $files.Add((Get-FileEvidence -Path $path -IncludeText))
            }
        }

        $payloadExecutable = Resolve-QuickPanelExecutablePath -Directory (Join-Path $attempt.FullName 'payload')
        $requestEvidence = @($files | Where-Object { $_.Path.EndsWith('request.json', [StringComparison]::OrdinalIgnoreCase) })
        $downloadedArchives = New-Object 'System.Collections.Generic.List[object]'
        $downloadedArchiveErrors = New-Object 'System.Collections.Generic.List[object]'
        foreach ($requestFile in $requestEvidence) {
            if ([string]::IsNullOrWhiteSpace($requestFile.Text)) {
                continue
            }
            try {
                $request = $requestFile.Text | ConvertFrom-Json
                $downloadedZip = [string]$request.DownloadedZip
                if (-not [string]::IsNullOrWhiteSpace($downloadedZip)) {
                    $archivePath = [IO.Path]::GetFullPath($downloadedZip)
                    Assert-LocalEvidencePath -Path $archivePath
                    if ([IO.Path]::GetExtension($archivePath) -ine '.zip') {
                        throw 'Downloaded archive reference is not a ZIP file.'
                    }
                    foreach ($protectedRoot in @($ProtectedRoots | Select-Object -Unique)) {
                        if (Test-SameOrDescendantEvidencePath -Candidate $archivePath -Root $protectedRoot) {
                            throw 'Downloaded archive reference overlaps a protected updater/profile root.'
                        }
                    }
                    Assert-SafeEvidenceFile -Path $archivePath
                    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
                        $downloadedArchives.Add((Get-FileEvidence -Path $archivePath))
                    }
                }
            }
            catch {
                $downloadedArchiveErrors.Add([ordered]@{
                    RequestPathId = Get-StringSha256 -Value $requestFile.Path.ToLowerInvariant()
                    ErrorType = $_.Exception.GetType().Name
                })
            }
        }

        $attempts.Add([ordered]@{
            Directory = $attempt.FullName
            CreatedTimeUtc = $attempt.CreationTimeUtc.ToString('O')
            LastWriteTimeUtc = $attempt.LastWriteTimeUtc.ToString('O')
            Files = @($files | ForEach-Object { $_ })
            PayloadExecutable = Get-ExecutableEvidence -Path $payloadExecutable
            DownloadedArchives = @($downloadedArchives | ForEach-Object { $_ })
            DownloadedArchiveErrors = @($downloadedArchiveErrors | ForEach-Object { $_ })
        })
    }

    return [ordered]@{
        Root = $root
        Exists = $true
        Attempts = @($attempts | ForEach-Object { $_ })
    }
}

function Get-UpdaterMaintenanceLayout {
    param(
        [Parameter(Mandatory = $true)][string]$CanonicalPath,
        [Parameter(Mandatory = $true)][string]$PhysicalPath
    )

    $maintenanceRoot = $PhysicalPath.TrimEnd('\', '/') + '-maintenance'
    $durableLogRoot = Join-Path $maintenanceRoot 'update-logs'
    $newBackupRoot = Join-Path $maintenanceRoot 'update-backups'
    $backupRoots = New-Object 'System.Collections.Generic.List[string]'
    $backupRoots.Add($newBackupRoot)
    try {
        $canonicalProduct = Split-Path -Parent (Get-NormalizedDirectoryPath -Path $CanonicalPath)
        $physicalProduct = Resolve-QuickPanelExistingPhysicalDirectory -Path $canonicalProduct
        $legacyBackupRoot = Join-Path $physicalProduct 'update-backups'
        if ($backupRoots -inotcontains $legacyBackupRoot) {
            $backupRoots.Add($legacyBackupRoot)
        }
    }
    catch {
        # The primary maintenance root remains authoritative when no legacy root can be resolved.
    }

    return [ordered]@{
        MaintenanceRoot = $maintenanceRoot
        DurableLogRoot = $durableLogRoot
        BackupRoots = @($backupRoots | Select-Object -Unique)
    }
}

function Get-MaintenanceEvidence {
    param([Parameter(Mandatory = $true)]$Layout)

    $maintenanceRoot = [string]$Layout.MaintenanceRoot
    $durableLogRoot = [string]$Layout.DurableLogRoot
    $backupRoots = @($Layout.BackupRoots)
    foreach ($path in @($maintenanceRoot, $durableLogRoot) + $backupRoots | Select-Object -Unique) {
        Assert-LocalEvidencePath -Path $path
        Assert-NoReparsePointPath -Path $path
    }

    $logs = New-Object 'System.Collections.Generic.List[object]'
    if (Test-Path -LiteralPath $durableLogRoot -PathType Container) {
        foreach ($log in @(Get-ChildItem -LiteralPath $durableLogRoot -File -Force |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 20)) {
            Assert-SafeEvidenceFile -Path $log.FullName
            $logs.Add((Get-FileEvidence -Path $log.FullName -IncludeText))
        }
    }

    $backups = New-Object 'System.Collections.Generic.List[object]'
    foreach ($backupRoot in @($backupRoots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $backupRoot -PathType Container)) {
            continue
        }
        foreach ($backup in @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 20)) {
            $backups.Add([ordered]@{
                Directory = $backup.FullName
                LastWriteTimeUtc = $backup.LastWriteTimeUtc.ToString('O')
                InvalidMarkerPresent = Test-Path -LiteralPath (Join-Path $backup.FullName '.invalid-update-backup') -PathType Leaf
                Executable = Get-ExecutableEvidence -Path (Resolve-QuickPanelExecutablePath -Directory $backup.FullName)
                Inventory = Get-HashedTreeInventory -RootDirectory $backup.FullName
            })
        }
    }

    return [ordered]@{
        MaintenanceRoot = $maintenanceRoot
        DurableLogRoot = $durableLogRoot
        DurableLogs = @($logs | ForEach-Object { $_ })
        BackupRoots = $backupRoots
        Backups = @($backups | ForEach-Object { $_ })
    }
}

function Get-WindowsApplicationEvidence {
    param([Parameter(Mandatory = $true)][int]$LookbackHours)

    $events = New-Object 'System.Collections.Generic.List[object]'
    try {
        $startTime = (Get-Date).AddHours(-$LookbackHours)
        $candidates = @(Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            ProviderName = @('.NET Runtime', 'Application Error', 'Windows Error Reporting')
            StartTime = $startTime
        } -ErrorAction Stop)
        foreach ($event in @($candidates | Where-Object {
            $_.Message -match '(?i)AIQuickPanel|Quick Panel'
        } | Sort-Object TimeCreated -Descending | Select-Object -First 100)) {
            $message = ([string]$event.Message).Replace("`r", ' ').Replace("`n", ' ').Trim()
            if ($message.Length -gt $script:MaximumEventMessageCharacters) {
                $message = $message.Substring(0, $script:MaximumEventMessageCharacters)
            }
            $events.Add([ordered]@{
                TimeCreatedUtc = $event.TimeCreated.ToUniversalTime().ToString('O')
                ProviderName = $event.ProviderName
                EventId = $event.Id
                LevelDisplayName = $event.LevelDisplayName
                Message = $message
            })
        }
        return [ordered]@{
            Available = $true
            Events = @($events | ForEach-Object { $_ })
            ErrorType = $null
        }
    }
    catch {
        return [ordered]@{
            Available = $false
            Events = @()
            ErrorType = $_.Exception.GetType().Name
        }
    }
}

function Compare-HashedInventories {
    param(
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)]$Current
    )

    if (-not $Baseline.Complete -or -not $Current.Complete) {
        return [ordered]@{
            Verified = $false
            Unchanged = $false
            Reason = 'One or both inventories are incomplete.'
            AddedPathIds = @()
            RemovedPathIds = @()
            ChangedPathIds = @()
            AddedDirectoryPathIds = @()
            RemovedDirectoryPathIds = @()
            AddedReparsePathIds = @()
            RemovedReparsePathIds = @()
            ChangedReparsePathIds = @()
        }
    }

    $before = @{}
    foreach ($file in @($Baseline.Files)) { $before[[string]$file.PathId] = $file }
    $after = @{}
    foreach ($file in @($Current.Files)) { $after[[string]$file.PathId] = $file }
    $added = @($after.Keys | Where-Object { -not $before.ContainsKey($_) } | Sort-Object)
    $removed = @($before.Keys | Where-Object { -not $after.ContainsKey($_) } | Sort-Object)
    $changed = @($before.Keys | Where-Object {
        $after.ContainsKey($_) -and
        ($before[$_].Length -ne $after[$_].Length -or $before[$_].Sha256 -ne $after[$_].Sha256)
    } | Sort-Object)
    $beforeDirectories = @{}
    foreach ($pathId in @($Baseline.DirectoryPathIds)) { $beforeDirectories[[string]$pathId] = $true }
    $afterDirectories = @{}
    foreach ($pathId in @($Current.DirectoryPathIds)) { $afterDirectories[[string]$pathId] = $true }
    $addedDirectories = @($afterDirectories.Keys | Where-Object { -not $beforeDirectories.ContainsKey($_) } | Sort-Object)
    $removedDirectories = @($beforeDirectories.Keys | Where-Object { -not $afterDirectories.ContainsKey($_) } | Sort-Object)
    $beforeReparse = @{}
    foreach ($item in @($Baseline.ReparsePoints)) { $beforeReparse[[string]$item.PathId] = $item }
    $afterReparse = @{}
    foreach ($item in @($Current.ReparsePoints)) { $afterReparse[[string]$item.PathId] = $item }
    $addedReparse = @($afterReparse.Keys | Where-Object { -not $beforeReparse.ContainsKey($_) } | Sort-Object)
    $removedReparse = @($beforeReparse.Keys | Where-Object { -not $afterReparse.ContainsKey($_) } | Sort-Object)
    $changedReparse = @($beforeReparse.Keys | Where-Object {
        $afterReparse.ContainsKey($_) -and
        ($beforeReparse[$_].Attributes -ne $afterReparse[$_].Attributes -or
            $beforeReparse[$_].LinkType -ne $afterReparse[$_].LinkType -or
            $beforeReparse[$_].Target -ne $afterReparse[$_].Target)
    } | Sort-Object)
    return [ordered]@{
        Verified = $true
        Unchanged = $added.Count -eq 0 -and $removed.Count -eq 0 -and $changed.Count -eq 0 -and
            $addedDirectories.Count -eq 0 -and $removedDirectories.Count -eq 0 -and
            $addedReparse.Count -eq 0 -and $removedReparse.Count -eq 0 -and $changedReparse.Count -eq 0 -and
            $Baseline.InventorySha256 -eq $Current.InventorySha256
        Reason = $null
        AddedPathIds = $added
        RemovedPathIds = $removed
        ChangedPathIds = $changed
        AddedDirectoryPathIds = $addedDirectories
        RemovedDirectoryPathIds = $removedDirectories
        AddedReparsePathIds = $addedReparse
        RemovedReparsePathIds = $removedReparse
        ChangedReparsePathIds = $changedReparse
    }
}

$install = Get-NormalizedDirectoryPath -Path $InstallDirectory
$canonicalData = Get-NormalizedDirectoryPath -Path $CanonicalDataDirectory
$tempRoot = Get-NormalizedDirectoryPath -Path $TempUpdateRoot
$output = [IO.Path]::GetFullPath($OutputPath)
Assert-LocalEvidencePath -Path $install
Assert-LocalEvidencePath -Path $canonicalData
Assert-LocalEvidencePath -Path $tempRoot
Assert-LocalEvidencePath -Path $output
Assert-NoReparsePointPath -Path $install
Assert-NoReparsePointPath -Path $tempRoot
Assert-NoReparsePointPath -Path $output
$physicalData = Resolve-QuickPanelExistingPhysicalDirectory -Path $canonicalData
$maintenanceLayout = Get-UpdaterMaintenanceLayout -CanonicalPath $canonicalData -PhysicalPath $physicalData
$protectedRoots = @(
    @(
        $install,
        $canonicalData,
        $physicalData,
        $tempRoot,
        $maintenanceLayout.MaintenanceRoot,
        $maintenanceLayout.DurableLogRoot
    ) + @($maintenanceLayout.BackupRoots) |
        Select-Object -Unique
)

foreach ($protectedRoot in $protectedRoots) {
    if (Test-SameOrDescendantEvidencePath -Candidate $output -Root $protectedRoot) {
        throw "Evidence output must remain outside install, profile, updater temp, and maintenance roots: '$output'."
    }
}
if (Test-Path -LiteralPath $output) {
    throw "Evidence output must be a new file and cannot replace existing evidence or a hard link: '$output'."
}

$baselineFullPath = $null
if (-not [string]::IsNullOrWhiteSpace($BaselinePath)) {
    $baselineFullPath = [IO.Path]::GetFullPath($BaselinePath)
    Assert-SafeEvidenceFile -Path $baselineFullPath
    if (-not (Test-Path -LiteralPath $baselineFullPath -PathType Leaf)) {
        throw "Updater evidence baseline was not found: '$baselineFullPath'."
    }
    if ($baselineFullPath.Equals($output, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Evidence output cannot replace the trusted baseline.'
    }
    foreach ($protectedRoot in $protectedRoots) {
        if (Test-SameOrDescendantEvidencePath -Candidate $baselineFullPath -Root $protectedRoot) {
            throw "Evidence baseline must remain outside protected updater/profile roots: '$baselineFullPath'."
        }
    }
}

$installedExecutable = Resolve-QuickPanelExecutablePath -Directory $install
$profileInventory = Get-HashedTreeInventory -RootDirectory $physicalData -UseProfileCategories
$topology = Get-PathReparseTopology -CanonicalPath $canonicalData -PhysicalPath $physicalData
$installed = Get-ExecutableEvidence -Path $installedExecutable
$running = Get-RunningApplicationEvidence -ExecutablePath $installedExecutable
$unownedInstall = Get-UnownedInstallInventory -InstallRoot $install
$tempEvidence = Get-TempUpdaterEvidence -RootDirectory $tempRoot -ProtectedRoots $protectedRoots
$maintenance = Get-MaintenanceEvidence -Layout $maintenanceLayout
$windowsEvents = if ($SkipWindowsEvents) {
    [ordered]@{ Available = $false; Events = @(); ErrorType = 'Skipped' }
}
else {
    Get-WindowsApplicationEvidence -LookbackHours $EventLookbackHours
}

$comparison = $null
if (-not [string]::IsNullOrWhiteSpace($BaselinePath)) {
    $baseline = Get-Content -LiteralPath $baselineFullPath -Raw | ConvertFrom-Json
    if ($baseline.SchemaVersion -ne $script:EvidenceSchemaVersion) {
        throw "Unsupported updater evidence baseline schema: '$($baseline.SchemaVersion)'."
    }
    $profileComparison = Compare-HashedInventories -Baseline $baseline.Profile.Inventory -Current $profileInventory
    $unownedComparison = Compare-HashedInventories -Baseline $baseline.Install.UnownedInventory -Current $unownedInstall
    $topologyUnchanged = $baseline.Profile.Topology.TopologySha256 -eq $topology.TopologySha256
    $comparison = [ordered]@{
        BaselinePath = $baselineFullPath
        Profile = $profileComparison
        JunctionTopologyVerified = $true
        JunctionTopologyUnchanged = $topologyUnchanged
        UnownedInstall = $unownedComparison
        Pass = $profileComparison.Verified -and $profileComparison.Unchanged -and
            $topologyUnchanged -and $unownedComparison.Verified -and $unownedComparison.Unchanged
    }
}

$versionMatches = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $versionPattern = '^' + [Text.RegularExpressions.Regex]::Escape($ExpectedVersion) + '(?:$|[.+-])'
    $versionMatches = $installed.Exists -and
        -not [string]::IsNullOrWhiteSpace([string]$installed.ProductVersion) -and
        ([string]$installed.ProductVersion) -match $versionPattern
}

$runningInstalledProcessCount = @($running | Where-Object { $_.MatchesInstalledExecutable }).Count
$afterRunningExpectationPass = if ($Phase -eq 'AfterRunning') {
    $runningInstalledProcessCount -gt 0 -and ($null -eq $versionMatches -or $versionMatches)
}
else {
    $null
}

$report = [ordered]@{
    SchemaVersion = $script:EvidenceSchemaVersion
    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Phase = $Phase
    ContainsSensitiveDiagnosticEvidence = $true
    SharingWarning = 'Private diagnostic evidence: review and redact raw updater text, event messages, local paths, and junction targets before sharing.'
    HostIdSha256 = Get-StringSha256 -Value ([string]$env:COMPUTERNAME).ToLowerInvariant()
    WindowsVersion = [Environment]::OSVersion.VersionString
    ExpectedVersion = $ExpectedVersion
    ExpectedVersionMatches = $versionMatches
    AfterRunningExpectationPass = $afterRunningExpectationPass
    Install = [ordered]@{
        Directory = $install
        Executable = $installed
        RunningProcesses = $running
        RunningInstalledProcessCount = $runningInstalledProcessCount
        UnownedInventory = $unownedInstall
    }
    Profile = [ordered]@{
        Topology = $topology
        Inventory = $profileInventory
    }
    Updater = [ordered]@{
        Temp = $tempEvidence
        Maintenance = $maintenance
    }
    WindowsApplicationEvents = $windowsEvents
    Comparison = $comparison
}

$outputDirectory = Split-Path -Parent $output
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "Evidence output path has no parent directory: '$output'."
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Assert-NoReparsePointPath -Path $outputDirectory
Assert-NoReparsePointPath -Path $output
if (Test-Path -LiteralPath $output) {
    throw "Evidence output appeared before the atomic write and will not be replaced: '$output'."
}
Write-NewEvidenceFile -Path $output -Content ($report | ConvertTo-Json -Depth 16)

Write-Output "Updater evidence written: $output"
Write-Output "Profile inventory complete: $($profileInventory.Complete); files: $($profileInventory.FileCount); SHA256: $($profileInventory.InventorySha256)"
if ($null -ne $comparison) {
    Write-Output "Before/after comparison pass: $($comparison.Pass)"
}
if ($null -ne $versionMatches) {
    Write-Output "Installed version matches ${ExpectedVersion}: $versionMatches"
}

if ($FailOnMismatch) {
    if (-not $comparison.Pass) {
        throw 'Updater evidence comparison found a profile, junction, or unrelated-install change.'
    }
    if ($null -ne $versionMatches -and -not $versionMatches) {
        throw "Installed Quick Panel version does not match '$ExpectedVersion'."
    }
}
