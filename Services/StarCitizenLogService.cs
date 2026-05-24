using System.IO;
using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

public sealed class StarCitizenLogService
{
    private static readonly string[] Channels = ["LIVE", "PTU", "EPTU", "TECH-PREVIEW"];
    private static readonly string[] CrashKeywords =
    [
        "crash",
        "fatal",
        "exception",
        "access violation",
        "out of memory",
        "device removed",
        "dxgi_error",
        "gpu crash",
        "hung",
        "failed to allocate"
    ];

    private static readonly string[] NetworkKeywords =
    [
        "network error",
        "code 30000",
        "disconnect",
        "connection lost",
        "timeout",
        "server error",
        "kicked"
    ];

    private static readonly string[] NormalExitKeywords =
    [
        "system quit",
        "quit requested",
        "shutdown",
        "game shutdown",
        "closing"
    ];

    public string FindLogPath(string configuredPath)
    {
        return FindLogCandidates(configuredPath)
            .Where(File.Exists)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .FirstOrDefault() ?? string.Empty;
    }

    public StarCitizenExitAnalysis AnalyzeExit(string configuredPath, long sessionLogOffset)
    {
        var logPaths = FindLogCandidates(configuredPath)
            .Where(File.Exists)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .Take(3)
            .ToList();
        if (logPaths.Count == 0)
        {
            return new StarCitizenExitAnalysis
            {
                StatusKey = "StarCitizenExitLogMissing",
                Summary = "Game.log not found."
            };
        }

        var primaryLogPath = logPaths[0];
        var lines = logPaths
            .SelectMany((path, index) => ReadRelevantLines(path, index == 0 ? sessionLogOffset : 0))
            .TakeLast(350)
            .ToList();
        var evidence = FindEvidence(lines);
        var status = Classify(evidence, lines);

        return new StarCitizenExitAnalysis
        {
            StatusKey = status,
            LogPath = primaryLogPath,
            Evidence = evidence.Take(5).ToArray(),
            Summary = evidence.Count == 0 ? Path.GetFileName(primaryLogPath) : string.Join(" | ", evidence.Take(3))
        };
    }

    public long GetCurrentLogLength(string configuredPath)
    {
        var logPath = FindLogPath(configuredPath);
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return 0;
        }

        try
        {
            return new FileInfo(logPath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> FindLogCandidates(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            foreach (var candidate in ResolveLogCandidates(configuredPath))
            {
                yield return candidate;
            }
        }

        foreach (var candidate in GuessLogPaths())
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> ResolveLogCandidates(string path)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }

        if (!Directory.Exists(path))
        {
            yield break;
        }

        var direct = Path.Combine(path, "Game.log");
        if (File.Exists(direct))
        {
            yield return direct;
        }

        foreach (var backup in BackupLogs(path))
        {
            yield return backup;
        }

        foreach (var channel in Channels)
        {
            var channelDirectory = Path.Combine(path, channel);
            var candidate = Path.Combine(channelDirectory, "Game.log");
            if (File.Exists(candidate))
            {
                yield return candidate;
            }

            foreach (var backup in BackupLogs(channelDirectory))
            {
                yield return backup;
            }
        }

        foreach (var candidate in Directory.EnumerateFiles(path, "Game.log", SearchOption.AllDirectories)
            .OrderByDescending(file => new FileInfo(file).LastWriteTimeUtc)
            .Take(3))
        {
            yield return candidate;
        }

        foreach (var candidate in Directory.EnumerateDirectories(path, "logbackups", SearchOption.AllDirectories)
            .SelectMany(BackupLogs)
            .OrderByDescending(file => new FileInfo(file).LastWriteTimeUtc)
            .Take(3))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> GuessLogPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var commonRoots = new[]
        {
            Path.Combine(programFiles, "Roberts Space Industries", "StarCitizen"),
            Path.Combine(programFilesX86, "Roberts Space Industries", "StarCitizen"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StarCitizen")
        };

        foreach (var root in commonRoots.Where(Directory.Exists))
        {
            foreach (var channel in Channels)
            {
                var channelDirectory = Path.Combine(root, channel);
                yield return Path.Combine(channelDirectory, "Game.log");
                foreach (var backup in BackupLogs(channelDirectory).Take(2))
                {
                    yield return backup;
                }
            }
        }
    }

    private static IEnumerable<string> BackupLogs(string directory)
    {
        var backupDirectory = Path.Combine(directory, "logbackups");
        if (!Directory.Exists(backupDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(backupDirectory, "*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => new FileInfo(file).LastWriteTimeUtc)
            .Take(3);
    }

    private static List<string> ReadRelevantLines(string logPath, long sessionLogOffset)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (sessionLogOffset > 0 && sessionLogOffset < stream.Length)
            {
                stream.Seek(sessionLogOffset, SeekOrigin.Begin);
            }
            else if (stream.Length > 512 * 1024)
            {
                stream.Seek(-512 * 1024, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(250)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<string> FindEvidence(IReadOnlyList<string> lines)
    {
        return lines
            .Where(line => ContainsAny(line, CrashKeywords)
                || ContainsAny(line, NetworkKeywords)
                || ContainsAny(line, NormalExitKeywords))
            .Select(TrimEvidence)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(12)
            .Reverse()
            .ToList();
    }

    private static string Classify(IReadOnlyList<string> evidence, IReadOnlyList<string> lines)
    {
        if (evidence.Any(line => ContainsAny(line, CrashKeywords)))
        {
            return "StarCitizenExitCrashLikely";
        }

        if (evidence.Any(line => ContainsAny(line, NetworkKeywords)))
        {
            return "StarCitizenExitNetworkLikely";
        }

        if (evidence.Any(line => ContainsAny(line, NormalExitKeywords))
            || lines.TakeLast(30).Any(line => ContainsAny(line, NormalExitKeywords)))
        {
            return "StarCitizenExitNormalLikely";
        }

        return "StarCitizenExitUnknown";
    }

    private static bool ContainsAny(string text, IEnumerable<string> keywords)
    {
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string TrimEvidence(string line)
    {
        var cleaned = line.Trim();
        return cleaned.Length > 220 ? cleaned[..220] : cleaned;
    }
}
