using System.IO;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class DataStorageService
{
    private readonly string _appDataDirectory;
    private readonly AppSettings _settings;
    private readonly Action<string>? _log;

    public DataStorageService(string appDataDirectory, AppSettings settings, Action<string>? log = null)
    {
        _appDataDirectory = appDataDirectory;
        _settings = settings;
        _log = log;
    }

    public IReadOnlyList<DataStorageItem> GetItems()
    {
        Directory.CreateDirectory(_appDataDirectory);
        var items = new List<DataStorageItem>();

        AddFile(items, "settings.json", "Settings", isSettings: true);
        AddFile(items, "learning-history.json", "Work data", isSettings: false);
        AddFile(items, "starcitizen-sessions.json", "Work data", isSettings: false);
        AddMatchingFiles(items, "logs-*.txt", "Exported logs");
        AddDirectory(items, "ShaderCacheBackups", "Backup cache");
        AddDirectory(items, "OcrDebug", "Debug images");
        AddDirectory(items, "ChromeTranslatorProfile", "Temporary profile");

        return items
            .OrderByDescending(item => item.IsSettings)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int CleanupExpired()
    {
        return new DataRetentionCleanupService(_appDataDirectory, _settings, _log).Cleanup();
    }

    public int DeleteWorkData(bool keepSettings)
    {
        Directory.CreateDirectory(_appDataDirectory);
        var deleted = 0;

        deleted += DeleteFile(Path.Combine(_appDataDirectory, "learning-history.json"));
        deleted += DeleteFile(Path.Combine(_appDataDirectory, "starcitizen-sessions.json"));

        foreach (var logFile in Directory.EnumerateFiles(_appDataDirectory, "logs-*.txt", SearchOption.TopDirectoryOnly))
        {
            deleted += DeleteFile(logFile);
        }

        deleted += DeleteDirectory(Path.Combine(_appDataDirectory, "OcrDebug"));
        deleted += DeleteDirectory(Path.Combine(_appDataDirectory, "ChromeTranslatorProfile"));

        if (!keepSettings)
        {
            deleted += DeleteFile(Path.Combine(_appDataDirectory, "settings.json"));
        }

        return deleted;
    }

    public int DeleteAllNonSettingsData()
    {
        var deleted = DeleteWorkData(keepSettings: true);
        deleted += DeleteDirectory(Path.Combine(_appDataDirectory, "ShaderCacheBackups"));
        return deleted;
    }

    private void AddFile(List<DataStorageItem> items, string fileName, string kind, bool isSettings)
    {
        var path = Path.Combine(_appDataDirectory, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        var info = new FileInfo(path);
        items.Add(new DataStorageItem
        {
            Name = fileName,
            Kind = kind,
            Path = path,
            SizeBytes = info.Length,
            LastModifiedUtc = info.LastWriteTimeUtc,
            IsSettings = isSettings
        });
    }

    private void AddMatchingFiles(List<DataStorageItem> items, string pattern, string kind)
    {
        if (!Directory.Exists(_appDataDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_appDataDirectory, pattern, SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            items.Add(new DataStorageItem
            {
                Name = info.Name,
                Kind = kind,
                Path = path,
                SizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc
            });
        }
    }

    private void AddDirectory(List<DataStorageItem> items, string directoryName, string kind)
    {
        var path = Path.Combine(_appDataDirectory, directoryName);
        if (!Directory.Exists(path))
        {
            return;
        }

        var info = new DirectoryInfo(path);
        items.Add(new DataStorageItem
        {
            Name = directoryName,
            Kind = kind,
            Path = path,
            SizeBytes = GetDirectorySize(path),
            LastModifiedUtc = info.LastWriteTimeUtc
        });
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                });
        }
        catch
        {
            return 0;
        }
    }

    private int DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return 1;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Storage cleanup skipped {path}: {ex.Message}");
        }

        return 0;
    }

    private int DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return 1;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Storage cleanup skipped {path}: {ex.Message}");
        }

        return 0;
    }
}
