using System.IO;

namespace QuickPanel.Services;

internal static class PortableUpdateAutoApply
{
    public static string ApplyVerifiedDownload(string zipPath, string expectedSha256, bool sha256Verified)
    {
        if (!sha256Verified)
        {
            throw new InvalidDataException("Quick Panel will not install an update that was not SHA256 verified.");
        }
        return PortableUpdateInstaller.PrepareAndLaunch(zipPath, expectedSha256);
    }
}
