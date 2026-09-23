using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QuickPanel.Services;

public sealed class PerformanceSettings
{
    public int InactiveTabSleepMinutes { get; init; } = 15;

    // Missing key = use global default, 0 = never sleep, positive value = custom minutes.
    public Dictionary<string, int> TabSleepOverridesMinutes { get; init; } = new(StringComparer.Ordinal);
}

public sealed class PerformanceSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public PerformanceSettingsService()
        : this(Path.Combine(PortableDataPaths.DataDirectory, "performance.json"))
    {
    }

    public PerformanceSettingsService(string path)
    {
        _path = path;
    }

    public PerformanceSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new PerformanceSettings();
            }

            PerformanceSettings settings = JsonSerializer.Deserialize<PerformanceSettings>(File.ReadAllText(_path), JsonOptions)
                ?? new PerformanceSettings();
            return Normalize(settings);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is NotSupportedException)
        {
            return new PerformanceSettings();
        }
    }

    public int GetEffectiveSleepMinutes(string? tabId)
    {
        PerformanceSettings settings = Load();
        if (!string.IsNullOrWhiteSpace(tabId) &&
            settings.TabSleepOverridesMinutes.TryGetValue(tabId, out int value))
        {
            return Math.Clamp(value, 0, 1440);
        }

        return settings.InactiveTabSleepMinutes;
    }

    public int? GetTabSleepOverrideMinutes(string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return null;
        }

        PerformanceSettings settings = Load();
        return settings.TabSleepOverridesMinutes.TryGetValue(tabId, out int value)
            ? Math.Clamp(value, 0, 1440)
            : null;
    }

    public void SaveInactiveTabSleepMinutes(int minutes)
    {
        PerformanceSettings current = Load();
        Save(new PerformanceSettings
        {
            InactiveTabSleepMinutes = Math.Clamp(minutes, 0, 1440),
            TabSleepOverridesMinutes = new Dictionary<string, int>(current.TabSleepOverridesMinutes, StringComparer.Ordinal)
        });
    }

    public void SaveTabSleepOverrideMinutes(string tabId, int? minutes)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        PerformanceSettings current = Load();
        Dictionary<string, int> overrides = new(current.TabSleepOverridesMinutes, StringComparer.Ordinal);
        if (minutes == null)
        {
            overrides.Remove(tabId);
        }
        else
        {
            overrides[tabId] = Math.Clamp(minutes.Value, 0, 1440);
        }

        Save(new PerformanceSettings
        {
            InactiveTabSleepMinutes = current.InactiveTabSleepMinutes,
            TabSleepOverridesMinutes = overrides
        });
    }

    private void Save(PerformanceSettings settings)
    {
        PerformanceSettings normalized = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? PortableDataPaths.DataDirectory);
        string tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(normalized, JsonOptions));

            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(tempPath, _path, null);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    File.Copy(tempPath, _path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static PerformanceSettings Normalize(PerformanceSettings settings)
    {
        Dictionary<string, int> overrides = new(StringComparer.Ordinal);
        if (settings.TabSleepOverridesMinutes != null)
        {
            foreach ((string key, int value) in settings.TabSleepOverridesMinutes)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    overrides[key.Trim()] = Math.Clamp(value, 0, 1440);
                }
            }
        }

        return new PerformanceSettings
        {
            InactiveTabSleepMinutes = Math.Clamp(settings.InactiveTabSleepMinutes, 0, 1440),
            TabSleepOverridesMinutes = overrides
        };
    }
}
