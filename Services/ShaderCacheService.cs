using System.IO;
using System.IO.Compression;

namespace ANEVRED.Services;

public sealed class ShaderCacheService
{
    private readonly string _backupDirectory;

    public ShaderCacheService(string appDataDirectory)
    {
        _backupDirectory = Path.Combine(appDataDirectory, "ShaderCacheBackups");
        Directory.CreateDirectory(_backupDirectory);
    }

    public string BackupDirectory => _backupDirectory;

    public IReadOnlyList<ShaderCacheTarget> GetTargets(string? starCitizenPath)
    {
        var targets = new List<ShaderCacheTarget>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddLocalStarCitizenTargets(targets, Path.Combine(localAppData, "Star Citizen"));
        AddLocalStarCitizenTargets(targets, Path.Combine(localAppData, "StarCitizen"));

        if (!string.IsNullOrWhiteSpace(starCitizenPath) && Directory.Exists(starCitizenPath))
        {
            AddIfExists(targets, "Game shader cache", Path.Combine(starCitizenPath, "USER", "Client", "0", "shaders"));
            AddIfExists(targets, "Game shader cache", Path.Combine(starCitizenPath, "USER", "ShaderCache"));
            AddIfExists(targets, "Game pipeline cache", Path.Combine(starCitizenPath, "USER", "Client", "0", "pipelinecache"));
        }

        return targets
            .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ShaderCacheSnapshot Scan(string? starCitizenPath)
    {
        var targets = GetTargets(starCitizenPath);
        var totalBytes = targets.Sum(target => GetDirectorySizeSafe(target.Path));
        var latestBackup = GetLatestBackup();
        return new ShaderCacheSnapshot(targets, totalBytes, latestBackup);
    }

    public string Backup(string? starCitizenPath)
    {
        var targets = GetTargets(starCitizenPath);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("No Star Citizen shader cache folders were found.");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var staging = Path.Combine(Path.GetTempPath(), $"ANEVRED_shader_cache_{timestamp}");
        Directory.CreateDirectory(staging);

        try
        {
            using var manifest = new StreamWriter(Path.Combine(staging, "targets.txt"));
            for (var i = 0; i < targets.Count; i++)
            {
                var targetFolder = Path.Combine(staging, $"Target_{i:00}");
                CopyDirectory(targets[i].Path, targetFolder);
                manifest.WriteLine($"Target_{i:00}|{targets[i].Path}");
            }
        }
        finally
        {
            // The manifest writer is disposed before the zip is created.
        }

        var backupPath = Path.Combine(_backupDirectory, $"star-citizen-shader-cache-{timestamp}.zip");
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        ZipFile.CreateFromDirectory(staging, backupPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        TryDeleteDirectory(staging);
        return backupPath;
    }

    public int Clear(string? starCitizenPath)
    {
        var targets = GetTargets(starCitizenPath);
        foreach (var target in targets)
        {
            TryDeleteDirectory(target.Path);
        }

        return targets.Count;
    }

    public string RestoreLatest()
    {
        var latestBackup = GetLatestBackup();
        if (latestBackup is null)
        {
            throw new InvalidOperationException("No shader cache backup is available.");
        }

        Restore(latestBackup);
        return latestBackup;
    }

    private void Restore(string backupPath)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"ANEVRED_shader_restore_{DateTime.Now:yyyyMMdd-HHmmss}");
        ZipFile.ExtractToDirectory(backupPath, staging);
        var manifestPath = Path.Combine(staging, "targets.txt");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException("The selected shader cache backup does not contain a restore manifest.");
        }

        foreach (var line in File.ReadAllLines(manifestPath))
        {
            var parts = line.Split('|', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var source = Path.Combine(staging, parts[0]);
            var destination = parts[1];
            if (!Directory.Exists(source))
            {
                continue;
            }

            TryDeleteDirectory(destination);
            CopyDirectory(source, destination);
        }

        TryDeleteDirectory(staging);
    }

    private string? GetLatestBackup()
    {
        return Directory.EnumerateFiles(_backupDirectory, "star-citizen-shader-cache-*.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void AddLocalStarCitizenTargets(List<ShaderCacheTarget> targets, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        AddLocalCacheFolders(targets, root, "Local");

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (IsStarCitizenCacheContainer(name))
            {
                AddLocalCacheFolders(targets, directory, name);
            }

            if (IsCacheFolderName(name))
            {
                targets.Add(new ShaderCacheTarget($"Local cache: {name}", directory));
            }
        }
    }

    private static void AddLocalCacheFolders(List<ShaderCacheTarget> targets, string root, string sourceName)
    {
        AddIfExists(targets, $"Local shader cache: {sourceName}", Path.Combine(root, "Shaders"));
        AddIfExists(targets, $"Local shader cache: {sourceName}", Path.Combine(root, "shaders"));
        AddIfExists(targets, $"Local pipeline cache: {sourceName}", Path.Combine(root, "PipelineCache"));
        AddIfExists(targets, $"Local pipeline cache: {sourceName}", Path.Combine(root, "pipelinecache"));
        AddIfExists(targets, $"Local Vulkan shader cache: {sourceName}", Path.Combine(root, "vulkanshadercache"));
        AddIfExists(targets, $"Local Vulkan shader cache: {sourceName}", Path.Combine(root, "VulkanShaderCache"));
    }

    private static bool IsStarCitizenCacheContainer(string name)
    {
        return name.StartsWith("sc-", StringComparison.OrdinalIgnoreCase)
            || name.Contains("sc-alpha", StringComparison.OrdinalIgnoreCase)
            || name.Contains("starcitizen_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("star citizen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCacheFolderName(string name)
    {
        return name.Contains("shader", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cache", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfExists(List<ShaderCacheTarget> targets, string name, string path)
    {
        if (Directory.Exists(path))
        {
            targets.Add(new ShaderCacheTarget(name, path));
        }
    }

    private static long GetDirectorySizeSafe(string path)
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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Deleting shader cache can fail when the game or driver keeps files open. The caller logs the high-level action.
        }
    }
}

public sealed record ShaderCacheTarget(string Name, string Path);

public sealed record ShaderCacheSnapshot(IReadOnlyList<ShaderCacheTarget> Targets, long TotalBytes, string? LatestBackupPath);
