[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$InstallFolder
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$sdkId = 'Microsoft.DotNet.SDK.8'
$recommendedInstallFolder = Join-Path $env:LOCALAPPDATA 'Programs\QuickPanel'
$publishStage = Join-Path $env:TEMP ('QuickPanelPublish\update-' + [Guid]::NewGuid().ToString('N'))
$updateFailed = $false
. (Join-Path $PSScriptRoot 'profile-data-safety.ps1')

function Write-Usage {
    Write-Host ''
    Write-Host 'Usage:'
    Write-Host '  update-local.cmd'
    Write-Host '  update-local.cmd "C:\Path\To\QuickPanel"'
    Write-Host ''
    Write-Host "Default install folder: $recommendedInstallFolder"
}

function Stop-WithMessage {
    param([string]$Message)

    Write-Host ''
    Write-Host "ERROR: $Message"
    Write-Usage
    exit 1
}

function Resolve-QuickPanelExecutableFromText {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $expandedText = [Environment]::ExpandEnvironmentVariables($Text).Trim()
    $candidateExe = $null

    if ($expandedText -match '"([^"]+(?:AIQuickPanel|QuickPanel)\.exe)"') {
        $candidateExe = $matches[1]
    }
    elseif ($expandedText -match '(^|\s)([^\s"]+(?:AIQuickPanel|QuickPanel)\.exe)') {
        $candidateExe = $matches[2]
    }

    if ([string]::IsNullOrWhiteSpace($candidateExe)) {
        return $null
    }

    if ([IO.Path]::GetFileName($candidateExe) -inotmatch '^(AIQuickPanel|QuickPanel)\.exe$') {
        return $null
    }

    try {
        $candidateExe = [IO.Path]::GetFullPath($candidateExe)
    }
    catch {
        return $null
    }

    if (-not (Test-Path -LiteralPath $candidateExe -PathType Leaf)) {
        return $null
    }

    return (Resolve-Path -LiteralPath $candidateExe).Path
}

function Resolve-QuickPanelExecutableFromShortcut {
    param([string]$ShortcutPath)

    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
        return $null
    }

    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $targetPath = [string]$shortcut.TargetPath
        if ([IO.Path]::GetFileName($targetPath) -inotmatch '^(AIQuickPanel|QuickPanel)\.exe$') {
            return $null
        }
        if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
            return $null
        }
        return (Resolve-Path -LiteralPath $targetPath).Path
    }
    catch {
        return $null
    }
}

function Resolve-ExistingInstallFromStartupShortcut {
    $startupFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    if ([string]::IsNullOrWhiteSpace($startupFolder)) {
        return $null
    }

    $currentShortcut = Resolve-QuickPanelExecutableFromShortcut (Join-Path $startupFolder 'Quick Panel.lnk')
    if ($currentShortcut) {
        return $currentShortcut
    }
    return Resolve-QuickPanelExecutableFromShortcut (Join-Path $startupFolder 'AI Quick Panel.lnk')
}

function Resolve-ExistingInstallFromLegacyRunKey {
    try {
        $runValue = Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AIQuickPanel' -ErrorAction SilentlyContinue
        return Resolve-QuickPanelExecutableFromText ([string]$runValue)
    }
    catch {
        return $null
    }
}

function Resolve-ExistingInstallFromRecommendedFolder {
    $exe = Join-Path $recommendedInstallFolder 'QuickPanel.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) {
        return (Resolve-Path -LiteralPath $exe).Path
    }
    return $null
}

function Resolve-ExistingInstallFromRunningProcess {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'QuickPanel.exe' OR Name = 'AIQuickPanel.exe'" -ErrorAction SilentlyContinue |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) } |
        ForEach-Object {
            try {
                [IO.Path]::GetFullPath($_.ExecutablePath)
            }
            catch {
                $null
            }
        } |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            [IO.Path]::GetFileName($_) -imatch '^(AIQuickPanel|QuickPanel)\.exe$' -and
            (Test-Path -LiteralPath $_ -PathType Leaf)
        } |
        Sort-Object -Unique)

    if ($processes.Count -eq 1) {
        return (Resolve-Path -LiteralPath $processes[0]).Path
    }

    return $null
}

function Resolve-TargetInstall {
    if (-not [string]::IsNullOrWhiteSpace($InstallFolder)) {
        $expandedFolder = [Environment]::ExpandEnvironmentVariables($InstallFolder.Trim().Trim('"'))

        try {
            $folder = [IO.Path]::GetFullPath($expandedFolder)
        }
        catch {
            Stop-WithMessage "Install folder is not a valid path: $InstallFolder"
        }

        $exe = @('QuickPanel.exe', 'AIQuickPanel.exe') | ForEach-Object { Join-Path $folder $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
            Stop-WithMessage "Refusing to update '$folder' because it does not contain a supported Quick Panel executable."
        }

        return [pscustomobject]@{
            Source = 'command-line argument'
            Folder = (Resolve-Path -LiteralPath $folder).Path
            Exe    = (Resolve-Path -LiteralPath $exe).Path
        }
    }

    $legacyRunKeyExe = Resolve-ExistingInstallFromLegacyRunKey
    if ($legacyRunKeyExe) {
        return [pscustomobject]@{
            Source = 'legacy HKCU Run value (read-only)'
            Folder = (Split-Path -Path $legacyRunKeyExe -Parent)
            Exe    = $legacyRunKeyExe
        }
    }

    $recommendedExe = Resolve-ExistingInstallFromRecommendedFolder
    if ($recommendedExe) {
        return [pscustomobject]@{
            Source = 'recommended per-user install folder'
            Folder = (Split-Path -Path $recommendedExe -Parent)
            Exe    = $recommendedExe
        }
    }

    $startupShortcutExe = Resolve-ExistingInstallFromStartupShortcut
    if ($startupShortcutExe) {
        return [pscustomobject]@{
            Source = 'Startup folder shortcut'
            Folder = (Split-Path -Path $startupShortcutExe -Parent)
            Exe    = $startupShortcutExe
        }
    }

    $runningExe = Resolve-ExistingInstallFromRunningProcess
    if ($runningExe) {
        return [pscustomobject]@{
            Source = 'running Quick Panel process'
            Folder = (Split-Path -Path $runningExe -Parent)
            Exe    = $runningExe
        }
    }

    Stop-WithMessage 'Could not find a reliable existing Quick Panel install. Run scripts\install-clean.ps1 or pass the install folder manually.'
}

function New-Shortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$Arguments
    )

    $shortcutDirectory = Split-Path -Path $ShortcutPath -Parent
    New-Item -ItemType Directory -Force -Path $shortcutDirectory | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = Split-Path -Path $TargetPath -Parent
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Description = 'Quick Panel'
    $shortcut.Save()
}

function Update-StartMenuShortcut {
    param([string]$TargetExe)

    $startMenuFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
    if ([string]::IsNullOrWhiteSpace($startMenuFolder)) {
        return
    }

    Remove-Item -LiteralPath (Join-Path (Join-Path $startMenuFolder 'Programs') 'AI Quick Panel.lnk') -Force -ErrorAction SilentlyContinue
    $shortcutPath = Join-Path (Join-Path $startMenuFolder 'Programs') 'Quick Panel.lnk'
    New-Shortcut -ShortcutPath $shortcutPath -TargetPath $TargetExe -Arguments ''
}

function Invoke-Native {
    param(
        [string]$Description,
        [string]$FilePath,
        [string[]]$Arguments
    )

    Write-Host ''
    Write-Host $Description
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Stop-InstalledApp {
    param([string]$TargetExe)

    Write-Host ''
    Write-Host 'Closing Quick Panel from target folder...'

    $targetFullPath = [IO.Path]::GetFullPath($TargetExe)
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'QuickPanel.exe' OR Name = 'AIQuickPanel.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $targetFullPath)
        })

    if ($processes.Count -eq 0) {
        Write-Host 'No running Quick Panel process found for this install.'
        return
    }

    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
    }

    Start-Sleep -Milliseconds 700

    $stillRunning = @(Get-CimInstance Win32_Process -Filter "Name = 'QuickPanel.exe' OR Name = 'AIQuickPanel.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $targetFullPath)
        })

    if ($stillRunning.Count -gt 0) {
        throw "Quick Panel is still running from '$targetFullPath'."
    }
}

try {
    $target = Resolve-TargetInstall
    Assert-InstallTargetSafe -TargetDirectory $target.Folder

    Write-Host 'Quick Panel local updater'
    Write-Host "Repo:          $repo"
    Write-Host "Target source: $($target.Source)"
    Write-Host "Target folder: $($target.Folder)"
    Write-Host "Target exe:    $($target.Exe)"
    Write-Host ''
    Write-Host 'This updater only publishes over the existing install above.'

    Set-Location -LiteralPath $repo

    Invoke-Native 'Pulling latest changes with git pull --ff-only...' 'git' @('pull', '--ff-only')

    Write-Host ''
    Write-Host 'Checking .NET 8 SDK...'
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $dotnetSdks = @()
    if ($dotnetCommand) {
        $dotnetSdks = & dotnet --list-sdks 2>$null
    }

    if (-not $dotnetCommand -or $LASTEXITCODE -ne 0 -or -not ($dotnetSdks -match '^8\.')) {
        Invoke-Native 'Installing .NET 8 SDK with winget...' 'winget' @('install', '--id', $sdkId, '--source', 'winget', '--accept-package-agreements', '--accept-source-agreements')
        $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    }

    Invoke-Native 'Restoring packages...' 'dotnet' @('restore', 'QuickPanel.sln')
    Invoke-Native 'Building Release...' 'dotnet' @('build', 'QuickPanel.sln', '--configuration', 'Release', '--no-restore')
    Invoke-Native 'Running tests...' 'dotnet' @('run', '--project', 'tests\QuickPanel.Tests.csproj', '--configuration', 'Release')

    New-Item -ItemType Directory -Path $publishStage -Force | Out-Null
    Invoke-Native 'Publishing staged Release build...' 'dotnet' @('publish', 'QuickPanel.csproj', '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false', '--output', $publishStage)
    New-ApplicationFilesManifest -PayloadDirectory $publishStage
    Assert-PayloadSafe -PayloadDirectory $publishStage

    Stop-InstalledApp -TargetExe $target.Exe
    Install-QuickPanelPayload -PayloadDirectory $publishStage -TargetDirectory $target.Folder

    Write-Host ''
    Write-Host 'Updating Start Menu shortcut...'
    $installedExe = Join-Path $target.Folder 'QuickPanel.exe'
    Update-StartMenuShortcut -TargetExe $installedExe

    Write-Host ''
    Write-Host "Starting $installedExe..."
    Start-Process -FilePath $installedExe -WorkingDirectory $target.Folder

    Write-Host ''
    Write-Host 'Update complete.'
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)"
    Write-Host 'UPDATE FAILED.'
    Write-Host 'Leave this window open and send the error above.'
    $updateFailed = $true
}
finally {
    if (Test-Path -LiteralPath $publishStage -PathType Container) {
        $resolvedStage = [IO.Path]::GetFullPath($publishStage)
        $expectedStageRoot = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'QuickPanelPublish')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if ($resolvedStage.StartsWith($expectedStageRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($updateFailed) { exit 1 }
exit 0
