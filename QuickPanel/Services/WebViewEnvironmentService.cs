using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace QuickPanel.Services;

public static class WebViewEnvironmentService
{
	public const string MissingRuntimeMessage =
		"Microsoft Edge WebView2 Runtime is required to open website tabs. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and restart Quick Panel.";

	private static readonly Lazy<Task<CoreWebView2Environment>> EnvironmentTask = new Lazy<Task<CoreWebView2Environment>>(CreateEnvironmentAsync);

	public static Task<CoreWebView2Environment> GetAsync()
	{
		return EnvironmentTask.Value;
	}

	private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
	{
		// DATA-SAFETY INVARIANT: WebView2 must always use this stable per-user directory.
		// Never change it to AppContext.BaseDirectory, the install directory, a build folder,
		// or a version-specific path. Doing so creates a fresh browser profile and appears to
		// log every web tab out during an application update.
		string text = PortableDataPaths.WebView2Directory;
		Directory.CreateDirectory(text);
		try
		{
			return await CoreWebView2Environment.CreateAsync(null, text);
		}
		catch (WebView2RuntimeNotFoundException exception)
		{
			throw new InvalidOperationException(MissingRuntimeMessage, exception);
		}
	}
}
