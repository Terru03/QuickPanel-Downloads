using System;
using System.Globalization;
using System.IO;

namespace QuickPanel.Services;

public sealed class LogService
{
    private const long MaxLogBytes = 512 * 1024;

    private readonly string _logDirectory;

    public LogService()
        : this(PortableDataPaths.LogDirectory)
    {
    }

    public LogService(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public string LogDirectory => _logDirectory;

    public string CurrentLogPath => Path.Combine(_logDirectory, "QuickPanel-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    public void Info(string message)
    {
        Write("INFO", message, null);
    }

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        text = RedactKeyValue(text, "token");
        text = RedactKeyValue(text, "access_token");
        text = RedactKeyValue(text, "refresh_token");
        text = RedactKeyValue(text, "authorization");
        text = RedactKeyValue(text, "cookie");
        return text;
    }

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            TrimIfNeeded();
            string line = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " [" + level + "] " + Redact(message);
            if (exception != null)
            {
                line += " | " + Redact(exception.GetType().Name + ": " + exception.Message);
            }
            File.AppendAllText(CurrentLogPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void TrimIfNeeded()
    {
        string path = CurrentLogPath;
        FileInfo info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxLogBytes)
        {
            return;
        }
        string archivedPath = Path.Combine(_logDirectory, Path.GetFileNameWithoutExtension(path) + ".old.log");
        if (File.Exists(archivedPath))
        {
            File.Delete(archivedPath);
        }
        File.Move(path, archivedPath);
    }

    private static string RedactKeyValue(string text, string key)
    {
        int searchStart = 0;
        while (searchStart < text.Length)
        {
            int keyIndex = text.IndexOf(key, searchStart, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                return text;
            }
            int separatorIndex = keyIndex + key.Length;
            while (separatorIndex < text.Length && char.IsWhiteSpace(text[separatorIndex]))
            {
                separatorIndex++;
            }
            if (separatorIndex >= text.Length || (text[separatorIndex] != '=' && text[separatorIndex] != ':'))
            {
                searchStart = keyIndex + key.Length;
                continue;
            }
            int valueStart = separatorIndex + 1;
            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
            {
                valueStart++;
            }
            int valueEnd = valueStart;
            while (valueEnd < text.Length && !char.IsWhiteSpace(text[valueEnd]) && text[valueEnd] != ';' && text[valueEnd] != ',')
            {
                valueEnd++;
            }
            text = text[..valueStart] + "[redacted]" + text[valueEnd..];
            searchStart = valueStart + "[redacted]".Length;
        }
        return text;
    }
}
