Set-StrictMode -Version 2.0

$script:QuickPanelProtectedProfileNames = @(
    'data',
    'WebView2',
    'IconCache',
    'logs',
    'sessions',
    'user_data',
    'UserData',
    'User Data',
    'appdata',
    'AppData',
    'settings.json',
    'profiles.json',
    'external-apps.user.json',
    'performance.json',
    'Cookies',
    'Local Storage',
    'IndexedDB'
)

$script:QuickPanelForbiddenReleaseNames = @(
    $script:QuickPanelProtectedProfileNames + @(
        'bin', 'obj', 'TestResults', 'test-output', 'migration-backups', 'migration-logs',
        'update-backups', 'tmp', 'temp', '.env', 'secrets.json', 'launchSettings.json',
        'appsettings.Development.json'
    )
)

$script:QuickPanelForbiddenReleaseExtensions = @(
    '.db', '.sqlite', '.sqlite3', '.pdb', '.pfx', '.p12', '.pem', '.key', '.snk'
)

$script:QuickPanelApplicationManifestName = 'application-files.json'
$script:QuickPanelKnownApplicationNames = @(
    'AIQuickPanel.exe', 'AIQuickPanel.dll', 'AIQuickPanel.deps.json', 'AIQuickPanel.runtimeconfig.json',
    'QuickPanel.exe', 'QuickPanel.dll', 'QuickPanel.deps.json', 'QuickPanel.runtimeconfig.json',
    'appsettings.json', 'external-apps.json', 'application-files.json', 'runtimes', 'win-x64', 'Assets',
    'BlackSharp.Core.dll', 'DiskInfoToolkit.dll', 'HidSharp.dll', 'libMonoPosixHelper.dll',
    'LibreHardwareMonitorLib.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Core.xml',
    'Microsoft.Web.WebView2.WinForms.dll', 'Microsoft.Web.WebView2.WinForms.xml',
    'Microsoft.Web.WebView2.Wpf.dll', 'Microsoft.Web.WebView2.Wpf.xml', 'Mono.Posix.NETStandard.dll',
    'MonoPosixHelper.dll', 'RAMSPDToolkit-NDD.dll', 'SharpVectors.Converters.Wpf.dll',
    'SharpVectors.Core.dll', 'SharpVectors.Css.dll', 'SharpVectors.Dom.dll', 'SharpVectors.Model.dll',
    'SharpVectors.Rendering.Wpf.dll', 'SharpVectors.Runtime.Wpf.dll', 'System.CodeDom.dll',
    'System.IO.Ports.dll', 'System.Management.dll', 'System.Threading.AccessControl.dll', 'WebView2Loader.dll',
    'CHANGELOG.md', 'EXTERNAL-APPS.md', 'QA.md', 'README.md', 'STARTUP.md',
    'THIRD-PARTY-NOTICES.md', 'version.example.json'
)
$script:LegacyQuickPanelRequiredIdentityNames = @(
    'AIQuickPanel.exe', 'AIQuickPanel.dll', 'AIQuickPanel.deps.json', 'AIQuickPanel.runtimeconfig.json'
)

function Get-LegacyQuickPanelCanonicalDataDirectory {
    return [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AIQuickPanel\data')).TrimEnd('\', '/')
}

function Get-QuickPanelCanonicalDataDirectory {
    return [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'QuickPanel\data')).TrimEnd('\', '/')
}

function Get-NormalizedDirectoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Write-Utf8NoBomFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($Path),
        $Content,
        [Text.UTF8Encoding]::new($false))
}

function Assert-NoReparsePointPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($fullPath)
    $remainder = $fullPath.Substring($current.Length)
    $separators = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    foreach ($segment in $remainder.Split($separators, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) {
            $parent = Split-Path -Parent $current
            $leaf = Split-Path -Leaf $current
            $item = Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop |
                Where-Object { $_.Name -ieq $leaf } |
                Select-Object -First 1
        }
        if ($null -eq $item) {
            break
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Updater path traverses a filesystem reparse point: '$current'."
        }
    }
}

function Assert-NoReparsePointTree {
    param([Parameter(Mandatory = $true)][string]$RootDirectory)

    $root = Get-NormalizedDirectoryPath -Path $RootDirectory
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return
    }
    $rootItem = Get-Item -LiteralPath $root -Force
    $reparse = @($rootItem) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    $reparse = @($reparse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparse.Count -gt 0) {
        throw "Updater trees cannot contain filesystem reparse points: '$($reparse[0].FullName)'."
    }
}

function Initialize-QuickPanelPhysicalDirectoryResolver {
    if ($null -ne ('QuickPanelPowerShellPhysicalDirectoryResolver' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class QuickPanelPowerShellPhysicalDirectoryResolver
{
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public static string Resolve(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A directory path is required.", "path");
        }

        string fullPath = Path.GetFullPath(path);
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("The path does not resolve to a directory: '" + fullPath + "'.");
        }

        using (SafeFileHandle handle = CreateFile(
            fullPath,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not open the persistent profile directory: '" + fullPath + "'.");
            }

            int capacity = 512;
            while (true)
            {
                StringBuilder buffer = new StringBuilder(capacity);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not resolve the persistent profile directory: '" + fullPath + "'.");
                }
                if (length < buffer.Capacity)
                {
                    return NormalizeFinalPath(buffer.ToString());
                }
                if (length >= Int32.MaxValue - 1)
                {
                    throw new PathTooLongException("The resolved persistent profile path is too long.");
                }
                capacity = checked((int)length + 1);
            }
        }
    }

    public static uint GetLinkCount(string path)
    {
        if (String.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file path is required.", "path");
        }

        string fullPath = Path.GetFullPath(path);
        FileAttributes attributes = File.GetAttributes(fullPath);
        uint flags = (attributes & FileAttributes.Directory) != 0
            ? FileFlagBackupSemantics
            : 0;
        using (SafeFileHandle handle = CreateFile(
            fullPath,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not inspect file identity: '" + fullPath + "'.");
            }

            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not inspect file link count: '" + fullPath + "'.");
            }
            return information.NumberOfLinks;
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        string normalized;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"\\" + path.Substring(8);
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = path.Substring(4);
        }
        else
        {
            normalized = path;
        }

        return Path.GetFullPath(normalized).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}
'@
}

function Resolve-QuickPanelExistingPhysicalDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    Initialize-QuickPanelPhysicalDirectoryResolver
    try {
        $physical = [QuickPanelPowerShellPhysicalDirectoryResolver]::Resolve($Path)
    }
    catch {
        throw "Could not safely resolve persistent profile directory '$Path': $($_.Exception.Message)"
    }

    Assert-NoReparsePointPath -Path $physical
    if (-not (Test-Path -LiteralPath $physical -PathType Container)) {
        throw "The resolved persistent profile is not an existing directory: '$physical'."
    }
    return Get-NormalizedDirectoryPath -Path $physical
}

function Get-QuickPanelPhysicalDataDirectory {
    param([Parameter(Mandatory = $true)][string]$CanonicalDataDirectory)

    $canonical = Get-NormalizedDirectoryPath -Path $CanonicalDataDirectory
    try {
        return Resolve-QuickPanelExistingPhysicalDirectory -Path $canonical
    }
    catch {
        $resolutionError = $_
    }

    try {
        Assert-NoReparsePointPath -Path $canonical
    }
    catch {
        throw "The persistent profile alias could not be resolved safely: $($resolutionError.Exception.Message)"
    }

    if (Test-Path -LiteralPath $canonical -PathType Leaf) {
        throw "The persistent profile path is a file instead of a directory: '$canonical'."
    }
    if (Test-Path -LiteralPath $canonical) {
        throw "The persistent profile path is not a usable directory: '$canonical'."
    }

    return $canonical
}

function Get-LegacyQuickPanelPhysicalDataDirectory {
    return Get-QuickPanelPhysicalDataDirectory -CanonicalDataDirectory (Get-LegacyQuickPanelCanonicalDataDirectory)
}

function Get-CurrentQuickPanelPhysicalDataDirectory {
    return Get-QuickPanelPhysicalDataDirectory -CanonicalDataDirectory (Get-QuickPanelCanonicalDataDirectory)
}

function Test-SameOrDescendantPath {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $candidatePath = Get-NormalizedDirectoryPath -Path $Candidate
    $rootPath = Get-NormalizedDirectoryPath -Path $Root
    return $candidatePath -ieq $rootPath -or
        $candidatePath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-InstallTargetSafe {
    param([Parameter(Mandatory = $true)][string]$TargetDirectory)

    $target = Get-NormalizedDirectoryPath -Path $TargetDirectory
    Assert-NoReparsePointPath -Path $target
    $protectedProfiles = @(
        (Get-LegacyQuickPanelCanonicalDataDirectory),
        (Get-LegacyQuickPanelPhysicalDataDirectory),
        (Get-QuickPanelCanonicalDataDirectory),
        (Get-CurrentQuickPanelPhysicalDataDirectory)
    ) | Select-Object -Unique
    foreach ($profilePath in $protectedProfiles) {
        if ((Test-SameOrDescendantPath -Candidate $target -Root $profilePath) -or
            (Test-SameOrDescendantPath -Candidate $profilePath -Root $target)) {
            throw "Install target '$target' overlaps the persistent profile '$profilePath'."
        }
    }

    $volumeRoot = [IO.Path]::GetPathRoot($target).TrimEnd('\', '/')
    if ($target -ieq $volumeRoot) {
        throw "Refusing to use a volume root as the Quick Panel install target: '$target'."
    }
}

function New-ApplicationFilesManifest {
    param([Parameter(Mandatory = $true)][string]$PayloadDirectory)

    $payload = Get-NormalizedDirectoryPath -Path $PayloadDirectory
    if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
        throw "Application payload directory was not found: '$payload'."
    }
    $entries = @(Get-ChildItem -LiteralPath $payload -Force |
        Where-Object { $_.Name -ine $script:QuickPanelApplicationManifestName } |
        Select-Object -ExpandProperty Name)
    $entries += $script:QuickPanelApplicationManifestName
    $entries = @($entries | Sort-Object -Unique)
    $manifestJson = [ordered]@{ schemaVersion = 1; entries = $entries } |
        ConvertTo-Json -Depth 3
    Write-Utf8NoBomFile `
        -Path (Join-Path $payload $script:QuickPanelApplicationManifestName) `
        -Content $manifestJson
}

function Get-ApplicationManifestEntries {
    param([Parameter(Mandatory = $true)][string]$RootDirectory)

    $root = Get-NormalizedDirectoryPath -Path $RootDirectory
    $manifestPath = Join-Path $root $script:QuickPanelApplicationManifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Application payload is missing $($script:QuickPanelApplicationManifestName)."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($null -eq $manifest -or $manifest.schemaVersion -ne 1 -or $null -eq $manifest.entries) {
        throw 'Application file manifest is invalid.'
    }
    $entries = @($manifest.entries)
    $uniqueEntries = @($entries | Sort-Object -Unique)
    $supportedExecutables = @($entries | Where-Object { $_ -ieq 'AIQuickPanel.exe' -or $_ -ieq 'QuickPanel.exe' })
    if ($entries.Count -ne $uniqueEntries.Count -or
        $supportedExecutables.Count -ne 1 -or
        $entries -inotcontains $script:QuickPanelApplicationManifestName) {
        throw 'Application file manifest contains duplicate or incomplete entries.'
    }
    foreach ($nameValue in $entries) {
        $name = [string]$nameValue
        if ([string]::IsNullOrWhiteSpace($name) -or $name -in @('.', '..') -or
            [IO.Path]::GetFileName($name) -cne $name -or
            $script:QuickPanelProtectedProfileNames -icontains $name) {
            throw 'Application file manifest contains an unsafe entry.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $root $name))) {
            throw "Application file manifest references a missing entry: '$name'."
        }
    }
    return $entries
}

function Get-OwnedInstallEntries {
    param(
        [Parameter(Mandatory = $true)][string]$TargetDirectory,
        [switch]$AllowProfileOnlyTarget
    )

    $target = Get-NormalizedDirectoryPath -Path $TargetDirectory
    if (Test-Path -LiteralPath (Join-Path $target $script:QuickPanelApplicationManifestName) -PathType Leaf) {
        return @(Get-ApplicationManifestEntries -RootDirectory $target)
    }

    $existingNames = @(Get-ChildItem -LiteralPath $target -Force | Select-Object -ExpandProperty Name)
    $hasLegacyIdentity = $true
    foreach ($requiredName in $script:LegacyQuickPanelRequiredIdentityNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $target $requiredName) -PathType Leaf)) {
            $hasLegacyIdentity = $false
        }
    }
    $unknownNames = @($existingNames | Where-Object {
        $script:QuickPanelProtectedProfileNames -inotcontains $_ -and
        $script:QuickPanelKnownApplicationNames -inotcontains $_
    })
    $profileOnlyTarget = $AllowProfileOnlyTarget -and -not $hasLegacyIdentity -and
        $unknownNames.Count -eq 0 -and
        @($existingNames | Where-Object { $script:QuickPanelProtectedProfileNames -icontains $_ }).Count -eq $existingNames.Count
    if ($profileOnlyTarget) {
        return @()
    }
    if (-not $hasLegacyIdentity -or $unknownNames.Count -gt 0) {
        throw "Refusing to update a shared or unrecognized folder. Move Quick Panel to its own install directory first: '$target'."
    }
    return @($existingNames | Where-Object { $script:QuickPanelKnownApplicationNames -icontains $_ })
}

function Assert-PayloadSafe {
    param([Parameter(Mandatory = $true)][string]$PayloadDirectory)

    $payload = Get-NormalizedDirectoryPath -Path $PayloadDirectory
    Assert-NoReparsePointPath -Path $payload
    if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
        throw "Application payload directory was not found: '$payload'."
    }
    Assert-NoReparsePointTree -RootDirectory $payload

    $forbidden = @(Get-ChildItem -LiteralPath $payload -Recurse -Force |
        Where-Object {
            $script:QuickPanelForbiddenReleaseNames -icontains $_.Name -or
            (-not $_.PSIsContainer -and $script:QuickPanelForbiddenReleaseExtensions -icontains $_.Extension)
        })
    if ($forbidden.Count -gt 0) {
        $names = ($forbidden | Select-Object -ExpandProperty Name) -join ', '
        throw "Application payload contains persistent profile entries: $names"
    }

    $manifestEntries = @(Get-ApplicationManifestEntries -RootDirectory $payload)
    $actualEntries = @(Get-ChildItem -LiteralPath $payload -Force | Select-Object -ExpandProperty Name | Sort-Object)
    $declaredEntries = @($manifestEntries | Sort-Object)
    if (($actualEntries -join "`n") -cne ($declaredEntries -join "`n")) {
        throw "Application payload entries do not match $($script:QuickPanelApplicationManifestName)."
    }
}

function Install-QuickPanelPayload {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory,
        [switch]$AllowProfileOnlyTarget
    )

    Assert-InstallTargetSafe -TargetDirectory $TargetDirectory
    Assert-PayloadSafe -PayloadDirectory $PayloadDirectory

    $payload = Get-NormalizedDirectoryPath -Path $PayloadDirectory
    $target = Get-NormalizedDirectoryPath -Path $TargetDirectory
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Assert-NoReparsePointPath -Path $target
    Assert-NoReparsePointTree -RootDirectory $target

    $existingEntries = @(Get-ChildItem -LiteralPath $target -Force)
    $replaceableNames = if ($existingEntries.Count -gt 0) {
        @(Get-OwnedInstallEntries -TargetDirectory $target -AllowProfileOnlyTarget:$AllowProfileOnlyTarget)
    }
    else {
        @()
    }

    foreach ($name in $replaceableNames) {
        $entryPath = [IO.Path]::GetFullPath((Join-Path $target $name))
        $entryParent = [IO.Path]::GetDirectoryName($entryPath).TrimEnd('\', '/')
        if ($entryParent -ine $target) {
            throw "Application replacement resolved outside the validated install directory: '$entryPath'."
        }

        Remove-Item -LiteralPath $entryPath -Recurse -Force
    }

    foreach ($name in @(Get-ApplicationManifestEntries -RootDirectory $payload)) {
        Copy-Item -LiteralPath (Join-Path $payload $name) -Destination (Join-Path $target $name) -Recurse -Force
    }
}
