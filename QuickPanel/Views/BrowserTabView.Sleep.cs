using System;
using System.Threading.Tasks;

namespace QuickPanel.Views;

public partial class BrowserTabView
{
    private bool _isSuspended;

    public bool IsSuspended => _isSuspended;

    public async Task<bool> TrySuspendForInactivityAsync()
    {
        if (_isSuspended || _isLoading || Browser.CoreWebView2 == null)
        {
            return false;
        }

        try
        {
            bool suspended = await Browser.CoreWebView2.TrySuspendAsync();
            if (suspended)
            {
                _isSuspended = true;
            }
            return suspended;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            return false;
        }
    }

    public void ResumeFromInactivitySleep()
    {
        if (!_isSuspended || Browser.CoreWebView2 == null)
        {
            return;
        }

        try
        {
            Browser.CoreWebView2.Resume();
            _isSuspended = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            _isSuspended = false;
        }
    }
}
