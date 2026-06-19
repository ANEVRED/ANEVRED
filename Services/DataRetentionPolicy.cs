using System.IO;
using ANEVRED.Models;

namespace ANEVRED.Services;

public static class DataRetentionPolicy
{
    public static readonly DateTime SessionStartedUtc = DateTime.UtcNow;
    public static readonly TimeSpan DefaultWorkDataRetention = TimeSpan.FromDays(14);
    public const int MaxLearningSamples = 7200;
    public const int MaxStarCitizenSessions = 250;
    public const int MaxShaderCacheBackups = 3;

    public static DateTime WorkDataCutoffUtc => DateTime.UtcNow - DefaultWorkDataRetention;

    public static DateTime GetWorkDataCutoffUtc(AppSettings settings)
    {
        return settings.DataRetentionMode == DataRetentionMode.Session
            ? SessionStartedUtc
            : DateTime.UtcNow - GetRetention(settings);
    }

    public static TimeSpan GetRetention(AppSettings settings)
    {
        return settings.DataRetentionMode switch
        {
            DataRetentionMode.Session => TimeSpan.Zero,
            DataRetentionMode.OneDay => TimeSpan.FromDays(1),
            DataRetentionMode.SevenDays => TimeSpan.FromDays(7),
            DataRetentionMode.ThirtyDays => TimeSpan.FromDays(30),
            _ => DefaultWorkDataRetention
        };
    }

    public static string GetRetentionLabel(AppSettings settings)
    {
        return settings.DataRetentionMode switch
        {
            DataRetentionMode.Session => "current session",
            DataRetentionMode.OneDay => "24 hours",
            DataRetentionMode.SevenDays => "7 days",
            DataRetentionMode.ThirtyDays => "30 days",
            _ => "14 days"
        };
    }

    public static bool IsRetainedUtc(DateTime timestampUtc)
    {
        return timestampUtc == default || timestampUtc >= WorkDataCutoffUtc;
    }

    public static bool IsRetainedUtc(DateTime timestampUtc, AppSettings settings)
    {
        return timestampUtc == default || timestampUtc >= GetWorkDataCutoffUtc(settings);
    }

    public static int DeleteOldFiles(
        string directory,
        string searchPattern,
        int maxFiles,
        Action<string>? log = null,
        AppSettings? settings = null)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoffUtc = settings is null ? WorkDataCutoffUtc : GetWorkDataCutoffUtc(settings);
        var files = Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var deleted = 0;
        foreach (var file in files.Where((file, index) => index >= maxFiles || file.LastWriteTimeUtc < cutoffUtc))
        {
            if (TryDeleteFile(file.FullName, log))
            {
                deleted++;
            }
        }

        return deleted;
    }

    public static int DeleteOldDirectories(
        string directory,
        AppSettings settings,
        Action<string>? log = null)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        var cutoffUtc = GetWorkDataCutoffUtc(settings);
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new DirectoryInfo(child);
                if (info.LastWriteTimeUtc < cutoffUtc)
                {
                    Directory.Delete(child, recursive: true);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Data retention cleanup skipped {child}: {ex.Message}");
            }
        }

        return deleted;
    }

    public static int DeleteDirectory(string path, Action<string>? log = null)
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
            log?.Invoke($"Data retention cleanup skipped {path}: {ex.Message}");
        }

        return 0;
    }

    private static bool TryDeleteFile(string path, Action<string>? log)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Data retention cleanup skipped {path}: {ex.Message}");
        }

        return false;
    }
}
