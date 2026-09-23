using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using QuickPanel.Models;
using DrawingIcon = System.Drawing.Icon;

namespace QuickPanel.Services;

public sealed class ExternalAppIconService
{
	private readonly TabIconService _tabIconService = new();
	private readonly ExternalAppLauncher _launcher = new();
	private readonly ExternalAppWindowFinder _windowFinder = new();
	private readonly ConcurrentDictionary<string, ImageSource> _nativeIconCache = new(StringComparer.OrdinalIgnoreCase);

	public Image CreateImage(ExternalAppDefinition definition, double size = 18)
	{
		Image image = CreateImageControl(size);
		_ = ApplyIconAsync(image, definition);
		return image;
	}

	public Image CreateImage(ExternalAppWindowCandidate candidate, double size = 18)
	{
		Image image = CreateImageControl(size);
		_ = ApplyWindowIconAsync(image, candidate);
		return image;
	}

	public Image CreateImage(ExternalAppInstalledApp app, double size = 18)
	{
		Image image = CreateImageControl(size);
		_ = ApplyShellIconAsync(image, app.ShellTarget);
		return image;
	}

	private static Image CreateImageControl(double size)
	{
		return new Image
		{
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true,
			Source = CreateFallbackIcon()
		};
	}

	private async Task ApplyIconAsync(Image image, ExternalAppDefinition definition)
	{
		try
		{
			ImageSource? source = await ResolveIconAsync(definition);
			if (source != null) image.Source = source;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or HttpRequestException or XmlException or ArgumentException or FormatException or ExternalException)
		{
		}
	}

	private async Task ApplyWindowIconAsync(Image image, ExternalAppWindowCandidate candidate)
	{
		try
		{
			ImageSource? source = await Task.Run(() => ResolveWindowIcon(candidate));
			if (source != null) image.Source = source;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or ExternalException)
		{
		}
	}

	private async Task ApplyShellIconAsync(Image image, string shellTarget)
	{
		try
		{
			ImageSource? source = await Task.Run(() => ResolveShellIcon(shellTarget));
			if (source != null) image.Source = source;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or ExternalException)
		{
		}
	}

	private ImageSource? ResolveWindowIcon(ExternalAppWindowCandidate candidate)
	{
		string windowKey = "window:" + candidate.Handle.ToInt64().ToString("X");
		if (_nativeIconCache.TryGetValue(windowKey, out ImageSource? cached)) return cached;
		using DrawingIcon? windowIcon = ExternalAppNativeMethods.TryGetWindowIcon(candidate.Handle);
		if (windowIcon != null)
		{
			ImageSource source = CreateImageSource(windowIcon);
			_nativeIconCache[windowKey] = source;
			return source;
		}
		ExternalAppDefinition definition = ExternalAppDefinitionFactory.CreateForWindow(candidate);
		foreach (string target in ExternalAppIconCandidateResolver.BuildShellTargets(
			definition,
			candidate.ProcessPath,
			candidate.AppUserModelId))
		{
			ImageSource? source = ResolveShellIcon(target);
			if (source != null) return source;
		}
		return null;
	}

	private async Task<ImageSource?> ResolveIconAsync(ExternalAppDefinition definition)
	{
		string? configuredIcon = definition.Icon?.Trim();
		if (!string.IsNullOrWhiteSpace(configuredIcon) && !configuredIcon.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			ImageSource? bundled = _tabIconService.ResolveBundledIcon(TabIconResolver.NormalizeIconSlug(configuredIcon));
			if (bundled != null) return bundled;
			ImageSource? custom = await _tabIconService.ResolveCustomIconAsync(configuredIcon);
			if (custom != null) return custom;
			ImageSource? configuredShellIcon = await Task.Run(() => ResolveShellIcon(configuredIcon));
			if (configuredShellIcon != null) return configuredShellIcon;
		}

		return await Task.Run(() => ResolveInstalledIcon(definition));
	}

	private ImageSource? ResolveInstalledIcon(ExternalAppDefinition definition)
	{
		ExternalAppWindowMatch? running = _windowFinder.FindBest(definition);
		ExternalAppStartTarget? startTarget = NeedsStartAppLookup(definition)
			? _launcher.FindStartApps(definition).FirstOrDefault()
			: null;
		foreach (string target in ExternalAppIconCandidateResolver.BuildShellTargets(
			definition,
			running?.Candidate.ProcessPath,
			startTarget?.AppUserModelId))
		{
			ImageSource? source = ResolveShellIcon(target);
			if (source != null) return source;
		}
		return null;
	}

	private static bool NeedsStartAppLookup(ExternalAppDefinition definition)
	{
		return definition.LaunchKind == ExternalAppLaunchKind.StartApp ||
			!string.IsNullOrWhiteSpace(definition.StartAppNameMatch) ||
			!string.IsNullOrWhiteSpace(definition.PackageIdentity) ||
			definition.AlternativePackageIdentities.Any(value => !string.IsNullOrWhiteSpace(value));
	}

	private ImageSource? ResolveShellIcon(string target)
	{
		string cacheKey = Environment.ExpandEnvironmentVariables(target.Trim());
		if (_nativeIconCache.TryGetValue(cacheKey, out ImageSource? cached)) return cached;
		using DrawingIcon? icon = ExternalAppNativeMethods.TryGetShellIcon(cacheKey);
		if (icon == null) return null;
		ImageSource source = CreateImageSource(icon);
		_nativeIconCache[cacheKey] = source;
		return source;
	}

	private static ImageSource CreateFallbackIcon()
	{
		using DrawingIcon icon = (DrawingIcon)System.Drawing.SystemIcons.Application.Clone();
		return CreateImageSource(icon);
	}

	private static BitmapSource CreateImageSource(DrawingIcon icon)
	{
		BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
			icon.Handle,
			Int32Rect.Empty,
			BitmapSizeOptions.FromWidthAndHeight(32, 32));
		source.Freeze();
		return source;
	}
}
