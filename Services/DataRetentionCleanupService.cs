using System.IO;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class DataRetentionCleanupService
{
    private readonly string _appDataDirectory;
    private readonly AppSettings _settings;
    private readonly Action<string>? _log;

    public DataRetentionCleanupService(string appDataDirectory, AppSettings settings, Action<string>? log = null)
    {
        _appDataDirectory = appDataDirectory;
        _settings = settings;
        _log = log;
    }

    public int Cleanup()
    {
        Directory.CreateDirectory(_appDataDirectory);

        var deleted = 0;
        deleted += DataRetentionPolicy.DeleteOldFiles(
            _appDataDirectory,
            "logs-*.txt",
            maxFiles: 10,
            _log,
            _settings);

        deleted += DataRetentionPolicy.DeleteOldFiles(
            Path.Combine(_appDataDirectory, "ShaderCacheBackups"),
            "star-citizen-shader-cache-*.zip",
            DataRetentionPolicy.MaxShaderCacheBackups,
            _log,
            _settings);

        deleted += DataRetentionPolicy.DeleteOldFiles(
            Path.Combine(_appDataDirectory, "OcrDebug"),
            "*.png",
            int.MaxValue,
            _log,
            _settings);

        deleted += DataRetentionPolicy.DeleteDirectory(
            Path.Combine(_appDataDirectory, "ChromeTranslatorProfile"),
            _log);

        return deleted;
    }
}
