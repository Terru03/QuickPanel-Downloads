using System;
using System.IO;
using System.Runtime.InteropServices;
using QuickPanel.Models;

namespace QuickPanel.Services;

public static class ShellExplorerLocationService
{
    public static bool IsFileExplorerWindow(ExternalAppWindowCandidate candidate)
    {
        return candidate.ClassName.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileNameWithoutExtension(candidate.ProcessName).Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetFolderPath(nint windowHandle, out string folderPath)
    {
        folderPath = string.Empty;
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
        {
            return false;
        }

        object? shell = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            shell = shellType == null ? null : Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return false;
            }
            dynamic windows = ((dynamic)shell).Windows();
            int count = Convert.ToInt32(windows.Count);
            for (int index = 0; index < count; index++)
            {
                dynamic window = windows.Item(index);
                if (Convert.ToInt64(window.HWND) != windowHandle.ToInt64())
                {
                    continue;
                }
                string? candidate = Convert.ToString(window.Document.Folder.Self.Path)?.Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate) && Directory.Exists(candidate))
                {
                    folderPath = Path.GetFullPath(candidate);
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        return false;
    }
}
