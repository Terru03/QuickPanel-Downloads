using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using QuickPanel.Models;
using QuickPanel.Services;
using Microsoft.Web.WebView2.Core;

namespace QuickPanel.Views;

public sealed class WebsiteIconChangedEventArgs : EventArgs
{
	public WebsiteIconChangedEventArgs(Uri pageUri, string cachePath)
	{
		PageUri = pageUri;
		CachePath = cachePath;
	}

	public Uri PageUri { get; }

	public string CachePath { get; }
}

public partial class BrowserTabView : UserControl, IDisposable, IComponentConnector
{
	private readonly AiTab _tab;
	private readonly WebsiteIconCache _websiteIconCache = new();

	private readonly string _initialUrl;

	private bool _initialized;

	private string? _lastCompletedUrl;

	private string? _lastRequestedUrl;

	private double _zoomFactor = 1.0;

	private bool _isLoading;
	private bool _disposed;
	private long _faviconRequestVersion;

	public BrowserTabView(AiTab tab, string? initialUrl = null)
	{
		_tab = tab;
		_initialUrl = string.IsNullOrWhiteSpace(initialUrl) ? tab.Url : initialUrl;
		InitializeComponent();
		LoadingText.Text = "Loading " + _tab.Name + "...";
		base.Loaded += BrowserTabView_Loaded;
		UpdateBrowserControls();
	}

	public double ZoomFactor
	{
		get => _zoomFactor;
		set
		{
			_zoomFactor = Math.Clamp(value, 0.25, 3.0);
			if (_initialized)
			{
				Browser.ZoomFactor = _zoomFactor;
			}
		}
	}

	public string CurrentUrl => _lastRequestedUrl ?? Browser.Source?.AbsoluteUri ?? _initialUrl;

	public bool CanGoBack => Browser.CoreWebView2?.CanGoBack == true;

	public bool CanGoForward => Browser.CoreWebView2?.CanGoForward == true;

	public bool IsLoading => _isLoading;

	public bool HasBrowsingProfile => Browser.CoreWebView2?.Profile != null;

	public string? BrowserProfileId => BrowserProfilePolicy.NormalizeProfileId(_tab.BrowserProfileId);

	public string BrowserProfileLabel => BrowserProfilePolicy.GetDisplayLabel(_tab);

	public string? BrowserProfilePath => Browser.CoreWebView2?.Profile?.ProfilePath;

	public event EventHandler? NavigationStateChanged;

	public event EventHandler<WebsiteIconChangedEventArgs>? WebsiteIconChanged;

	public bool FocusPage()
	{
		return Browser.Focus();
	}

	private async void BrowserTabView_Loaded(object sender, RoutedEventArgs e)
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;
		try
		{
			CoreWebView2Environment environment = await WebViewEnvironmentService.GetAsync();
			string? profileId = BrowserProfileId;
			if (profileId is null)
			{
				// Preserve the existing WebView2 Default profile for all pre-2.4.2 tabs.
				await Browser.EnsureCoreWebView2Async(environment);
			}
			else
			{
				CoreWebView2ControllerOptions controllerOptions = environment.CreateCoreWebView2ControllerOptions();
				controllerOptions.ProfileName = profileId;
				controllerOptions.IsInPrivateModeEnabled = false;
				await Browser.EnsureCoreWebView2Async(environment, controllerOptions);
				if (!Browser.CoreWebView2.Profile.ProfileName.Equals(profileId, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("WebView2 opened a different browsing profile than requested.");
				}
			}
			Browser.CoreWebView2.HistoryChanged += Browser_HistoryChanged;
			if (!TabIconResolver.HasManualOverride(_tab))
			{
				Browser.CoreWebView2.FaviconChanged += Browser_FaviconChanged;
			}
			Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
			Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
			Browser.ZoomFactor = _zoomFactor;
			NavigateTo(_initialUrl);
			UpdateBrowserControls();
		}
		catch (Exception ex)
		{
			ShowLoadError("Could not load " + _tab.Name, ex.Message);
		}
	}

	private void Browser_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
	{
		Interlocked.Increment(ref _faviconRequestVersion);
		_isLoading = true;
		_lastRequestedUrl = e.Uri;
		if (_lastCompletedUrl == null || !UrlsMatch(_lastCompletedUrl, e.Uri))
		{
			ShowLoading();
		}
		UpdateBrowserControls();
	}

	private void Browser_HistoryChanged(object? sender, object e)
	{
		UpdateBrowserControls();
	}

	private async void Browser_FaviconChanged(object? sender, object e)
	{
		if (_disposed || TabIconResolver.HasManualOverride(_tab) || Browser.CoreWebView2 is not CoreWebView2 webView)
		{
			return;
		}
		string source = webView.Source;
		if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? pageUri) ||
			(pageUri.Scheme != Uri.UriSchemeHttps && pageUri.Scheme != Uri.UriSchemeHttp))
		{
			return;
		}

		long requestVersion = Interlocked.Increment(ref _faviconRequestVersion);
		try
		{
			await using Stream favicon = await webView.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
			WebsiteIconCacheEntry? cached = await _websiteIconCache.StoreAsync(pageUri, favicon);
			if (cached == null || _disposed || requestVersion != Interlocked.Read(ref _faviconRequestVersion) ||
				!UrlsMatch(pageUri.AbsoluteUri, webView.Source))
			{
				return;
			}
			WebsiteIconChanged?.Invoke(this, new WebsiteIconChangedEventArgs(pageUri, cached.FilePath));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			InvalidOperationException or COMException or NotSupportedException)
		{
			// A malformed, missing, or transient favicon must never take down a browser tab.
		}
	}

	private void Browser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
	{
		_isLoading = false;
		if (e.IsSuccess)
		{
			_lastCompletedUrl = Browser.Source?.AbsoluteUri;
			LoadingOverlay.Visibility = Visibility.Collapsed;
			UpdateBrowserControls();
			return;
		}
		ShowLoadError("Navigation failed", e.WebErrorStatus.ToString());
		UpdateBrowserControls();
	}

	public void GoBack()
	{
		if (Browser.CoreWebView2?.CanGoBack == true)
		{
			Browser.CoreWebView2.GoBack();
		}
		UpdateBrowserControls();
	}

	public void GoForward()
	{
		if (Browser.CoreWebView2?.CanGoForward == true)
		{
			Browser.CoreWebView2.GoForward();
		}
		UpdateBrowserControls();
	}

	public void GoHome()
	{
		NavigateTo(_tab.Url);
		UpdateBrowserControls();
	}

	public void Reload()
	{
		if (Browser.CoreWebView2 != null)
		{
			Browser.CoreWebView2.Reload();
		}
		else
		{
			NavigateTo(CurrentUrl);
		}
		UpdateBrowserControls();
	}

	public bool StopLoading()
	{
		if (!_isLoading)
		{
			return false;
		}
		Browser.CoreWebView2?.Stop();
		_isLoading = false;
		LoadingOverlay.Visibility = Visibility.Collapsed;
		UpdateBrowserControls();
		return true;
	}

	public async Task ClearAllBrowsingDataAsync()
	{
		if (Browser.CoreWebView2?.Profile == null)
		{
			throw new InvalidOperationException("Open a web tab before clearing WebView2 data.");
		}
		await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
	}

	public bool DeleteBrowsingProfile()
	{
		if (BrowserProfileId is null || Browser.CoreWebView2?.Profile == null)
		{
			return false;
		}
		Browser.CoreWebView2.Profile.Delete();
		return true;
	}

	private void RetryButton_Click(object sender, RoutedEventArgs e)
	{
		NavigateTo(CurrentUrl);
	}

	private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = CurrentUrl,
				UseShellExecute = true
			});
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			ShowLoadError("Could not open browser", ex.Message);
		}
	}

	private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Clipboard.SetText(CurrentUrl);
		}
		catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.ExternalException)
		{
			ShowLoadError("Could not copy URL", ex.Message);
		}
	}

	private void NavigateTo(string url)
	{
		_lastRequestedUrl = url;
		if (Browser.CoreWebView2 != null)
		{
			Browser.CoreWebView2.Navigate(url);
			return;
		}
		Browser.Source = new Uri(url);
	}

	private void ShowLoading()
	{
		LoadingTitle.Text = "Loading " + _tab.Name;
		LoadingText.Text = CurrentUrl;
		LoadingProgress.Visibility = Visibility.Visible;
		ErrorActionsPanel.Visibility = Visibility.Collapsed;
		LoadingOverlay.Visibility = Visibility.Visible;
	}

	private void ShowLoadError(string title, string message)
	{
		LoadingTitle.Text = title;
		LoadingText.Text = message + Environment.NewLine + CurrentUrl;
		LoadingProgress.Visibility = Visibility.Collapsed;
		ErrorActionsPanel.Visibility = Visibility.Visible;
		LoadingOverlay.Visibility = Visibility.Visible;
	}

	private void UpdateBrowserControls()
	{
		NavigationStateChanged?.Invoke(this, EventArgs.Empty);
	}

	private static bool UrlsMatch(string left, string right)
	{
		return Uri.TryCreate(left, UriKind.Absolute, out Uri? leftUri) &&
			Uri.TryCreate(right, UriKind.Absolute, out Uri? rightUri) &&
			Uri.Compare(leftUri, rightUri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
	}

	public void Dispose()
	{
		_disposed = true;
		Interlocked.Increment(ref _faviconRequestVersion);
		base.Loaded -= BrowserTabView_Loaded;
		if (Browser.CoreWebView2 != null)
		{
			Browser.CoreWebView2.HistoryChanged -= Browser_HistoryChanged;
			Browser.CoreWebView2.FaviconChanged -= Browser_FaviconChanged;
		}
		Browser.Dispose();
		GC.SuppressFinalize(this);
	}
}
