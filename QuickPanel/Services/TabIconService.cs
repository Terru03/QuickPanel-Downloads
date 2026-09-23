using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using QuickPanel.Models;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace QuickPanel.Services;

public sealed class TabIconService
{
	private const int MaxIconBytes = 256 * 1024;
	private const int MaxRasterIconBytes = 2 * 1024 * 1024;

	private static readonly HttpClient HttpClient = new()
	{
		Timeout = TimeSpan.FromSeconds(8)
	};

	private readonly Dictionary<string, ImageSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

	private readonly string _cacheDirectory = PortableDataPaths.TabIconCacheDirectory;
	private readonly WebsiteIconCache _websiteIconCache = new();

	public string CacheDirectory => _cacheDirectory;

	public Image CreateImage(AiTab tab, double size = 18)
	{
		Image image = NewImage(size);
		image.Source = CreateFallbackIcon(tab);
		_ = ApplyIconAsync(image, tab);
		return image;
	}

	public bool TryApplyWebsiteIcon(Image image, string cachePath)
	{
		ArgumentNullException.ThrowIfNull(image);
		if (string.IsNullOrWhiteSpace(cachePath))
		{
			return false;
		}
		ImageSource? source = LoadBitmapFile(cachePath);
		if (source == null)
		{
			return false;
		}
		image.Source = source;
		return true;
	}

	public Image CreateBrandImage(string brandKey, double size = 18)
	{
		Image image = NewImage(size);
		image.Source = LoadBundledSvg(brandKey) ?? LoadBundledSvg("globe");
		return image;
	}

	private static Image NewImage(double size)
	{
		return new Image
		{
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform,
			SnapsToDevicePixels = true
		};
	}

	private async Task ApplyIconAsync(Image image, AiTab tab)
	{
		try
		{
			ImageSource? source = await ResolveIconAsync(tab);
			if (source != null)
			{
				image.Source = source;
			}
		}
		catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or
			XmlException or InvalidOperationException or NotSupportedException or OperationCanceledException)
		{
		}
	}

	private async Task<ImageSource?> ResolveIconAsync(AiTab tab)
	{
		if (TabIconResolver.HasManualOverride(tab))
		{
			ImageSource? custom = await ResolveCustomIconAsync(tab.Icon!.Trim());
			if (custom != null)
			{
				return custom;
			}
		}
		else if (Uri.TryCreate(tab.Url, UriKind.Absolute, out Uri? siteUri) &&
			_websiteIconCache.TryGetCachedIcon(siteUri, out string? cachePath))
		{
			ImageSource? cached = LoadBitmapFile(cachePath!);
			if (cached != null)
			{
				return cached;
			}
		}

		string? brandKey = TabIconResolver.ResolveBrandKey(tab);
		if (brandKey != null)
		{
			return LoadBundledSvg(brandKey);
		}
		return CreateFallbackIcon(tab);
	}

	internal async Task<ImageSource?> ResolveCustomIconAsync(string icon)
	{
		if (TabIconResolver.TryResolveBrandAlias(icon, out string? brand) && brand != null)
		{
			return LoadBundledSvg(brand);
		}
		if (icon.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
		{
			return LoadSvgText(icon);
		}
		if (Uri.TryCreate(icon, UriKind.Absolute, out Uri? uri) &&
			(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
		{
			return await TryLoadRemoteIconAsync(uri);
		}
		if (File.Exists(icon))
		{
			FileInfo file = new(icon);
			if (Path.GetExtension(icon).Equals(".svg", StringComparison.OrdinalIgnoreCase))
			{
				if (file.Length > MaxIconBytes) return null;
				return LoadSvgText(await File.ReadAllTextAsync(icon));
			}
			if (file.Length > MaxRasterIconBytes) return null;
			return LoadBitmapFile(icon);
		}
		string slug = TabIconResolver.NormalizeIconSlug(icon);
		if (!string.IsNullOrWhiteSpace(slug))
		{
			return await TryLoadRemoteSvgAsync(
				new Uri($"https://cdn.simpleicons.org/{Uri.EscapeDataString(slug)}/DDE3F0"));
		}
		return null;
	}

	internal ImageSource? ResolveBundledIcon(string brandKey) => LoadBundledSvg(brandKey);

	private ImageSource? LoadBundledSvg(string brandKey)
	{
		string key = "bundled:" + brandKey;
		if (_memoryCache.TryGetValue(key, out ImageSource? cached))
		{
			return cached;
		}
		string path = Path.Combine(AppContext.BaseDirectory, "Assets", "TabIcons", brandKey + ".svg");
		if (!File.Exists(path))
		{
			return null;
		}
		string svg = File.ReadAllText(path);
		if (brandKey.Equals("openai", StringComparison.OrdinalIgnoreCase))
		{
			svg = svg.Replace("<svg ", "<svg fill=\"#DDE3F0\" ", StringComparison.Ordinal);
		}
		else if (brandKey.Equals("globe", StringComparison.OrdinalIgnoreCase))
		{
			svg = svg.Replace("currentColor", "#DDE3F0", StringComparison.Ordinal);
		}
		ImageSource? image = LoadSvgText(svg);
		if (image != null)
		{
			_memoryCache[key] = image;
		}
		return image;
	}

	private async Task<ImageSource?> TryLoadRemoteSvgAsync(Uri uri)
	{
		string key = "svg:" + uri.AbsoluteUri;
		if (_memoryCache.TryGetValue(key, out ImageSource? cached))
		{
			return cached;
		}
		Directory.CreateDirectory(_cacheDirectory);
		string cachePath = Path.Combine(_cacheDirectory, Hash(uri.AbsoluteUri) + ".svg");
		string svg;
		string? cachedSvg = await TryReadCachedSvgAsync(cachePath);
		if (cachedSvg != null)
		{
			svg = cachedSvg;
		}
		else
		{
			using HttpResponseMessage response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
			if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxIconBytes)
			{
				return null;
			}
			byte[]? bytes = await ReadBoundedContentAsync(response.Content, MaxIconBytes);
			if (bytes == null)
			{
				return null;
			}
			svg = Encoding.UTF8.GetString(bytes);
			if (!IsSafeSvg(svg))
			{
				return null;
			}
			await File.WriteAllTextAsync(cachePath, svg);
		}
		ImageSource? image = LoadSvgText(svg);
		if (image != null)
		{
			_memoryCache[key] = image;
		}
		return image;
	}

	private async Task<ImageSource?> TryLoadRemoteIconAsync(Uri uri)
	{
		string key = "remote:" + uri.AbsoluteUri;
		if (_memoryCache.TryGetValue(key, out ImageSource? cached))
		{
			return cached;
		}
		Directory.CreateDirectory(_cacheDirectory);
		string svgCachePath = Path.Combine(_cacheDirectory, Hash(uri.AbsoluteUri) + ".svg");
		string rasterCachePath = Path.Combine(_cacheDirectory, Hash(uri.AbsoluteUri) + ".image");
		string? cachedSvg = await TryReadCachedSvgAsync(svgCachePath);
		if (cachedSvg != null)
		{
			ImageSource? source = LoadSvgText(cachedSvg);
			if (source != null)
			{
				_memoryCache[key] = source;
			}
			return source;
		}
		if (File.Exists(rasterCachePath))
		{
			ImageSource? source = LoadBitmapFile(rasterCachePath);
			if (source != null)
			{
				_memoryCache[key] = source;
			}
			return source;
		}

		using HttpResponseMessage response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
		if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxRasterIconBytes)
		{
			return null;
		}
		byte[]? bytes = await ReadBoundedContentAsync(response.Content, MaxRasterIconBytes);
		if (bytes == null)
		{
			return null;
		}
		string text = Encoding.UTF8.GetString(bytes);
		if (text.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
		{
			if (bytes.Length > MaxIconBytes || !IsSafeSvg(text))
			{
				return null;
			}
			ImageSource? source = LoadSvgText(text);
			if (source != null)
			{
				await File.WriteAllTextAsync(svgCachePath, text);
				_memoryCache[key] = source;
			}
			return source;
		}

		ImageSource? raster = LoadBitmapBytes(bytes);
		if (raster != null)
		{
			await File.WriteAllBytesAsync(rasterCachePath, bytes);
			_memoryCache[key] = raster;
		}
		return raster;
	}

	private static async Task<string?> TryReadCachedSvgAsync(string path)
	{
		try
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length <= 0 || file.Length > MaxIconBytes)
			{
				return null;
			}
			string svg = await File.ReadAllTextAsync(path);
			return IsSafeSvg(svg) ? svg : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return null;
		}
	}

	private static async Task<byte[]?> ReadBoundedContentAsync(HttpContent content, int maximumBytes)
	{
		if (content.Headers.ContentLength is long declaredLength &&
			(declaredLength <= 0 || declaredLength > maximumBytes))
		{
			return null;
		}

		await using Stream source = await content.ReadAsStreamAsync();
		using MemoryStream destination = new();
		byte[] buffer = new byte[Math.Min(81920, maximumBytes + 1)];
		while (true)
		{
			int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length));
			if (read == 0)
			{
				break;
			}
			if (destination.Length + read > maximumBytes)
			{
				return null;
			}
			await destination.WriteAsync(buffer.AsMemory(0, read));
		}
		return destination.Length == 0 ? null : destination.ToArray();
	}

	private static ImageSource CreateFallbackIcon(AiTab tab)
	{
		TabIconFallbackDescriptor descriptor = TabIconResolver.CreateFallbackDescriptor(tab);
		Color background = (Color)ColorConverter.ConvertFromString(descriptor.BackgroundHex);
		const int pixels = 64;
		DrawingVisual visual = new();
		using (DrawingContext drawing = visual.RenderOpen())
		{
			drawing.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(2, 2, 60, 60), 14, 14);
			FormattedText text = new(
				descriptor.Monogram,
				CultureInfo.InvariantCulture,
				FlowDirection.LeftToRight,
				new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
				32,
				Brushes.White,
				1.0);
			drawing.DrawText(text, new Point((pixels - text.Width) / 2, (pixels - text.Height) / 2 - 1));
		}
		RenderTargetBitmap bitmap = new(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(visual);
		bitmap.Freeze();
		return bitmap;
	}

	private static ImageSource? LoadSvgText(string svg)
	{
		if (!IsSafeSvg(svg))
		{
			return null;
		}
		WpfDrawingSettings settings = new()
		{
			IncludeRuntime = false,
			TextAsGeometry = true
		};
		using FileSvgReader reader = new(settings);
		using StringReader textReader = new(svg);
		DrawingGroup? drawing = reader.Read(textReader);
		if (drawing == null)
		{
			return null;
		}
		drawing.Freeze();
		DrawingImage image = new(drawing);
		image.Freeze();
		return image;
	}

	private static ImageSource? LoadBitmapFile(string path)
	{
		try
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length <= 0 || file.Length > MaxRasterIconBytes)
			{
				return null;
			}
			BitmapImage bitmap = new();
			bitmap.BeginInit();
			bitmap.CacheOption = BitmapCacheOption.OnLoad;
			bitmap.DecodePixelWidth = WebsiteIconCache.MaxDimension;
			bitmap.DecodePixelHeight = WebsiteIconCache.MaxDimension;
			bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
			bitmap.EndInit();
			if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 ||
				bitmap.PixelWidth > WebsiteIconCache.MaxDimension || bitmap.PixelHeight > WebsiteIconCache.MaxDimension)
			{
				return null;
			}
			bitmap.Freeze();
			return bitmap;
		}
		catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
		{
			return null;
		}
	}

	private static ImageSource? LoadBitmapBytes(byte[] bytes)
	{
		try
		{
			using MemoryStream stream = new(bytes, writable: false);
			BitmapImage bitmap = new();
			bitmap.BeginInit();
			bitmap.CacheOption = BitmapCacheOption.OnLoad;
			bitmap.DecodePixelWidth = WebsiteIconCache.MaxDimension;
			bitmap.DecodePixelHeight = WebsiteIconCache.MaxDimension;
			bitmap.StreamSource = stream;
			bitmap.EndInit();
			if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 ||
				bitmap.PixelWidth > WebsiteIconCache.MaxDimension || bitmap.PixelHeight > WebsiteIconCache.MaxDimension)
			{
				return null;
			}
			bitmap.Freeze();
			return bitmap;
		}
		catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
		{
			return null;
		}
	}

	private static bool IsSafeSvg(string svg)
	{
		if (string.IsNullOrWhiteSpace(svg) || Encoding.UTF8.GetByteCount(svg) > MaxIconBytes)
		{
			return false;
		}
		XmlReaderSettings settings = new()
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			MaxCharactersInDocument = MaxIconBytes
		};
		using StringReader textReader = new(svg);
		using XmlReader reader = XmlReader.Create(textReader, settings);
		XDocument document = XDocument.Load(reader, LoadOptions.None);
		XElement? root = document.Root;
		if (root == null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string[] blockedElements = ["script", "style", "foreignObject", "iframe", "object", "embed", "image"];
		foreach (XElement element in root.DescendantsAndSelf())
		{
			if (blockedElements.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))
			{
				return false;
			}
			foreach (XAttribute attribute in element.Attributes())
			{
				string name = attribute.Name.LocalName;
				string value = attribute.Value.Trim();
				if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
					value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
					ContainsUnsafeCssReference(value) ||
					value.Contains("expression(", StringComparison.OrdinalIgnoreCase) ||
					(name.Equals("href", StringComparison.OrdinalIgnoreCase) &&
					 !string.IsNullOrEmpty(value) && !value.StartsWith("#", StringComparison.Ordinal)))
				{
					return false;
				}
			}
		}
		return true;
	}

	private static bool ContainsUnsafeCssReference(string value)
	{
		string compact = string.Concat(value.Where(character =>
			!char.IsWhiteSpace(character) && character is not '\'' and not '"'));
		if (compact.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
			compact.Contains("behavior:", StringComparison.OrdinalIgnoreCase) ||
			compact.Contains("-moz-binding", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		int searchIndex = 0;
		while ((searchIndex = compact.IndexOf("url", searchIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
		{
			if (searchIndex + 4 > compact.Length || compact[searchIndex + 3] != '(')
			{
				return true;
			}
			int closeIndex = compact.IndexOf(')', searchIndex + 4);
			if (closeIndex < 0)
			{
				return true;
			}
			string target = compact[(searchIndex + 4)..closeIndex];
			if (!target.StartsWith('#') || target.Length == 1)
			{
				return true;
			}
			searchIndex = closeIndex + 1;
		}
		return false;
	}

	private static string Hash(string value)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
	}
}
