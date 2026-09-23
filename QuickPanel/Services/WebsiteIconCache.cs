using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuickPanel.Services;

public sealed record WebsiteIconCacheEntry(string FilePath, string ContentHash);

public sealed class WebsiteIconCache
{
    public const int MaxPngBytes = 512 * 1024;
    public const int MaxDimension = 1024;

    private const string IndexFileName = "index.json";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly ConcurrentDictionary<string, object> DirectoryLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _cacheDirectory;
    private readonly object _directoryLock;

    public WebsiteIconCache()
        : this(PortableDataPaths.WebsiteIconCacheDirectory)
    {
    }

    public WebsiteIconCache(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _directoryLock = DirectoryLocks.GetOrAdd(_cacheDirectory, static _ => new object());
    }

    public string CacheDirectory => _cacheDirectory;

    public async Task<WebsiteIconCacheEntry?> StoreAsync(
        Uri pageUri,
        Stream pngStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        ArgumentNullException.ThrowIfNull(pngStream);
        if (!TryNormalizePageIdentity(pageUri, out string? pageIdentity, out string? originIdentity))
        {
            return null;
        }

        byte[]? png = await ReadBoundedAsync(pngStream, cancellationToken).ConfigureAwait(false);
        if (png is null || !IsValidPng(png))
        {
            return null;
        }

        string contentHash = Hash(png);
        string fileName = contentHash + ".png";
        string filePath = Path.Combine(_cacheDirectory, fileName);
        lock (_directoryLock)
        {
            Directory.CreateDirectory(_cacheDirectory);
            WriteContentIfMissing(filePath, png);

            WebsiteIconIndex index = LoadIndex();
            index.Entries[Hash(pageIdentity!)] = fileName;
            index.Entries[Hash(originIdentity!)] = fileName;
            SaveIndex(index);
        }
        return new WebsiteIconCacheEntry(filePath, contentHash);
    }

    public bool TryGetCachedIcon(Uri pageUri, out string? filePath)
    {
        filePath = null;
        if (!TryNormalizePageIdentity(pageUri, out string? pageIdentity, out string? originIdentity))
        {
            return false;
        }

        lock (_directoryLock)
        {
            WebsiteIconIndex index = LoadIndex();
            foreach (string identity in new[] { pageIdentity!, originIdentity! })
            {
                if (!index.Entries.TryGetValue(Hash(identity), out string? fileName) ||
                    !IsSafeContentFileName(fileName))
                {
                    continue;
                }

                string candidate = Path.Combine(_cacheDirectory, fileName);
                try
                {
                    FileInfo file = new(candidate);
                    if (!file.Exists || file.Length <= 0 || file.Length > MaxPngBytes)
                    {
                        continue;
                    }
                    byte[] bytes = File.ReadAllBytes(candidate);
                    if (!IsValidPng(bytes))
                    {
                        continue;
                    }
                    filePath = candidate;
                    return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return false;
    }

    public static string CreateIdentityKey(Uri pageUri)
    {
        if (!TryNormalizePageIdentity(pageUri, out string? pageIdentity, out _))
        {
            throw new ArgumentException("Website icon cache keys require an absolute HTTP or HTTPS URI.", nameof(pageUri));
        }
        return Hash(pageIdentity!);
    }

    internal static bool IsValidPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || bytes.Length > MaxPngBytes || !bytes[..8].SequenceEqual(PngSignature))
        {
            return false;
        }

        int position = 8;
        bool sawHeader = false;
        while (position <= bytes.Length - 12)
        {
            uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position, 4));
            if (lengthValue > int.MaxValue)
            {
                return false;
            }
            int length = (int)lengthValue;
            int chunkTotal;
            try
            {
                chunkTotal = checked(length + 12);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (chunkTotal > bytes.Length - position)
            {
                return false;
            }

            ReadOnlySpan<byte> type = bytes.Slice(position + 4, 4);
            if (!sawHeader)
            {
                if (length != 13 || !type.SequenceEqual("IHDR"u8))
                {
                    return false;
                }
                uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 8, 4));
                uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 12, 4));
                if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension)
                {
                    return false;
                }
                sawHeader = true;
            }
            if (type.SequenceEqual("IEND"u8))
            {
                return sawHeader && length == 0 && position + chunkTotal == bytes.Length;
            }
            position += chunkTotal;
        }
        return false;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        using MemoryStream destination = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (destination.Length + read > MaxPngBytes)
            {
                return null;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.Length == 0 ? null : destination.ToArray();
    }

    private static bool TryNormalizePageIdentity(Uri uri, out string? pageIdentity, out string? originIdentity)
    {
        pageIdentity = null;
        originIdentity = null;
        if (!uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        UriBuilder builder = new(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }
        Uri normalized = builder.Uri;
        pageIdentity = "page:" + normalized.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
        originIdentity = "origin:" + normalized.GetLeftPart(UriPartial.Authority) + "/";
        return true;
    }

    private WebsiteIconIndex LoadIndex()
    {
        string path = Path.Combine(_cacheDirectory, IndexFileName);
        try
        {
            if (!File.Exists(path))
            {
                return new WebsiteIconIndex();
            }
            WebsiteIconIndex? index = JsonSerializer.Deserialize<WebsiteIconIndex>(File.ReadAllText(path), JsonOptions);
            if (index?.SchemaVersion != 1 || index.Entries is null)
            {
                return new WebsiteIconIndex();
            }
            index.Entries = index.Entries
                .Where(entry => IsHash(entry.Key) && IsSafeContentFileName(entry.Value))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            return index;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new WebsiteIconIndex();
        }
    }

    private void SaveIndex(WebsiteIconIndex index)
    {
        string path = Path.Combine(_cacheDirectory, IndexFileName);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(index, JsonOptions), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteContentIfMissing(string path, byte[] contents)
    {
        if (File.Exists(path))
        {
            return;
        }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, contents);
            try
            {
                File.Move(temp, path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsSafeContentFileName(string? fileName)
    {
        return fileName is not null && fileName.Length == 68 &&
               fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
               IsHash(fileName[..64]);
    }

    private static bool IsHash(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class WebsiteIconIndex
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
