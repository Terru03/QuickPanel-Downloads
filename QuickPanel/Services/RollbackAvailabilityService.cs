using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickPanel.Services;

public sealed class RollbackAvailabilityService
{
    private readonly Func<CancellationToken, string?> _getLatestVersion;

    public RollbackAvailabilityService()
        : this(PortableUpdateRollback.GetLatestBackupVersion)
    {
    }

    internal RollbackAvailabilityService(Func<CancellationToken, string?> getLatestVersion)
    {
        _getLatestVersion = getLatestVersion ?? throw new ArgumentNullException(nameof(getLatestVersion));
    }

    public Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _getLatestVersion(cancellationToken), cancellationToken);
    }
}
