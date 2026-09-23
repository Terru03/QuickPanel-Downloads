using System;
using System.Windows;
using System.Windows.Threading;

namespace QuickPanel.Views;

public partial class SettingsWindow
{
    private const double OwnerInset = 12.0;
    private bool _ownerLayoutHooksAttached;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        AttachOwnerLayoutHooks();
        EnsurePerformanceSettingsUi();
        _ = RefreshRollbackButtonAsync();
        InitializeProfilesPage();
        FitInsideOwner();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsVisible && Owner?.IsActive == true)
            {
                Close();
            }
        }));
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelRollbackAvailabilityRefresh();
        StopPerformanceMonitoring();
        DetachOwnerLayoutHooks();
        base.OnClosed(e);
    }

    private void AttachOwnerLayoutHooks()
    {
        if (_ownerLayoutHooksAttached || Owner == null)
        {
            return;
        }

        Owner.LocationChanged += Owner_LayoutChanged;
        Owner.SizeChanged += Owner_SizeChanged;
        Owner.StateChanged += Owner_LayoutChanged;
        _ownerLayoutHooksAttached = true;
    }

    private void DetachOwnerLayoutHooks()
    {
        if (!_ownerLayoutHooksAttached || Owner == null)
        {
            return;
        }

        Owner.LocationChanged -= Owner_LayoutChanged;
        Owner.SizeChanged -= Owner_SizeChanged;
        Owner.StateChanged -= Owner_LayoutChanged;
        _ownerLayoutHooksAttached = false;
    }

    private void Owner_LayoutChanged(object? sender, EventArgs e) => FitInsideOwner();

    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e) => FitInsideOwner();

    private void FitInsideOwner()
    {
        if (Owner is not Window owner ||
            owner.WindowState == WindowState.Minimized ||
            owner.ActualWidth <= 0 ||
            owner.ActualHeight <= 0)
        {
            return;
        }

        double availableWidth = Math.Max(360.0, owner.ActualWidth - OwnerInset * 2.0);
        double availableHeight = Math.Max(420.0, owner.ActualHeight - OwnerInset * 2.0);

        Width = Math.Min(560.0, availableWidth);
        Height = Math.Min(720.0, availableHeight);

        Left = owner.Left + Math.Max(0.0, (owner.ActualWidth - Width) / 2.0);
        Top = owner.Top + Math.Max(0.0, (owner.ActualHeight - Height) / 2.0);
    }
}
