[CmdletBinding()]
param(
    [switch]$EnableStartup,
    [switch]$NoStart
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$installFolder = Join-Path $env:LOCALAPPDATA 'Programs\QuickPanel'
$targetExe = Join-Path $installFolder 'QuickPanel.exe'
$publishStage = Join-Path $env:TEMP ('QuickPanelPublish\install-' + [Guid]::NewGuid().ToString('N'))
. (Join-Path $PSScriptRoot 'profile-data-safety.ps1')

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

function Remove-LegacyStartupEntries {
    $startupFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    if (-not [string]::IsNullOrWhiteSpace($startupFolder)) {
        foreach ($name in @('AIQuickPanel.cmd', 'AIQuickPanel.lnk', 'AI Quick Panel.lnk')) {
            $path = Join-Path $startupFolder $name
            if (Test-Path -LiteralPath $path) {
                try {
                    Remove-Item -LiteralPath $path -Force
                    Write-Host "Removed legacy startup entry: $path"
                }
                catch {
                    Write-Host "Could not remove legacy startup entry: $path"
                    Write-Host "  $($_.Exception.Message)"
                }
            }
        }
    }

    try {
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AIQuickPanel' -ErrorAction SilentlyContinue
    }
    catch {
        Write-Host "Could not remove legacy HKCU Run entry: $($_.Exception.Message)"
    }
}

try {
    Assert-InstallTargetSafe -TargetDirectory $installFolder
    Write-Host 'Quick Panel clean per-user install'
    Write-Host "Repo:          $repo"
    Write-Host "Install path:  $installFolder"
    Write-Host "Startup:       $EnableStartup"

    Set-Location -LiteralPath $repo

Get-CimInstance Win32_Process -Filter "Name = 'QuickPanel.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq [IO.Path]::GetFullPath($targetExe)) } |
    ForEach-Object {
        Write-Host "Stopping existing Quick Panel PID $($_.ProcessId)"
        Stop-Process -Id $_.ProcessId -Force
    }

    Invoke-Native 'Restoring packages...' 'dotnet' @('restore', 'QuickPanel.sln')
    Invoke-Native 'Building Release...' 'dotnet' @('build', 'QuickPanel.sln', '--configuration', 'Release', '--no-restore')
    Invoke-Native 'Running tests...' 'dotnet' @('run', '--project', 'tests\QuickPanel.Tests.csproj', '--configuration', 'Release')
    New-Item -ItemType Directory -Path $publishStage -Force | Out-Null
    Invoke-Native 'Publishing staged Release build...' 'dotnet' @('publish', 'QuickPanel.csproj', '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'false', '--output', $publishStage)
    New-ApplicationFilesManifest -PayloadDirectory $publishStage
    Assert-PayloadSafe -PayloadDirectory $publishStage
    Install-QuickPanelPayload -PayloadDirectory $publishStage -TargetDirectory $installFolder -AllowProfileOnlyTarget

Remove-LegacyStartupEntries

$startMenuFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
if (-not [string]::IsNullOrWhiteSpace($startMenuFolder)) {
    Remove-Item -LiteralPath (Join-Path (Join-Path $startMenuFolder 'Programs') 'AI Quick Panel.lnk') -Force -ErrorAction SilentlyContinue
    New-Shortcut -ShortcutPath (Join-Path (Join-Path $startMenuFolder 'Programs') 'Quick Panel.lnk') -TargetPath $targetExe -Arguments ''
}

if ($EnableStartup) {
    $startupFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    if ([string]::IsNullOrWhiteSpace($startupFolder)) {
        throw 'Could not resolve the current user Startup folder.'
    }
    New-Shortcut -ShortcutPath (Join-Path $startupFolder 'Quick Panel.lnk') -TargetPath $targetExe -Arguments '--startup'
}

if (-not $NoStart) {
    Start-Process -FilePath $targetExe -WorkingDirectory $installFolder
}

    Write-Host ''
    Write-Host 'Clean install complete.'
    Write-Host "Installed EXE: $targetExe"
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
