using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QuickPanel.Services;

internal readonly record struct PhysicalDirectoryIdentity(
    string ResolvedPath,
    uint VolumeSerialNumber,
    ulong FileId);

internal static class PhysicalDirectoryResolver
{
    private const uint FileFlagBackupSemantics = 0x02000000;

    internal static string ResolveExistingDirectory(string path)
    {
        using SafeFileHandle handle = OpenExistingDirectory(path);
        return ReadAndValidatePhysicalPath(handle);
    }

    internal static PhysicalDirectoryIdentity GetExistingDirectoryIdentity(string path)
    {
        using SafeFileHandle handle = OpenExistingDirectory(path);
        string physical = ReadAndValidatePhysicalPath(handle);
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw ResolutionFailure(new Win32Exception(Marshal.GetLastWin32Error()));
        }

        ulong fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new PhysicalDirectoryIdentity(physical, information.VolumeSerialNumber, fileId);
    }

    private static SafeFileHandle OpenExistingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Physical updater path resolution requires Windows.");
        }

        string full = Normalize(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ResolutionFailure(exception);
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException("The persistent user-data path must resolve to a directory.");
        }

        SafeFileHandle handle = CreateFile(
            full,
            desiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw ResolutionFailure(new Win32Exception(error));
        }

        return handle;
    }

    private static string ReadAndValidatePhysicalPath(SafeFileHandle handle)
    {
        string physical = Normalize(ReadFinalPath(handle));
        if (!Directory.Exists(physical))
        {
            throw new DirectoryNotFoundException("The physical persistent user-data directory does not exist.");
        }

        return physical;
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        int capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, flags: 0);
            if (length == 0)
            {
                throw ResolutionFailure(new Win32Exception(Marshal.GetLastWin32Error()));
            }
            if (length < buffer.Capacity)
            {
                return RemoveExtendedPathPrefix(buffer.ToString());
            }
            if (length >= int.MaxValue - 1)
            {
                throw new InvalidOperationException("The resolved persistent user-data path is too long.");
            }

            capacity = checked((int)length + 1);
        }
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }
        return path.StartsWith(extendedPrefix, StringComparison.Ordinal)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static InvalidOperationException ResolutionFailure(Exception innerException)
    {
        return new InvalidOperationException(
            "Could not resolve the persistent user-data directory alias. The link may be broken, looping, or inaccessible.",
            innerException);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
