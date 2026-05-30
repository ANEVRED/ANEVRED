using System.IO;
using ANEVRED.Models;

namespace ANEVRED.Services;

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
        "code 30016",
        "disconnect",
        "connection lost",
        "timeout",
        "server error",
        "kicked",
        "wsaenobufs",
        "socket send error"
    ];

    private static readonly string[] NormalExitKeywords =
    [
        "player requested disconnect",
        "client quitting game",
        "systemquit",
        "system quit",
        "quit requested",
        "exitcode=0",
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

    public StarCitizenExitAnalysis AnalyzeExit(
        string configuredPath,
        long sessionLogOffset,
        DateTime sessionStartedUtc,
        DateTime sessionEndedUtc)
    {
        var primaryLogPath = FindLogPath(configuredPath);
        var logPaths = FindLogCandidates(configuredPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => IsPotentialSessionLog(path, sessionStartedUtc, sessionEndedUtc))
            .ThenByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .Take(12)
            .ToList();
        if (logPaths.Count == 0)
        {
            return new StarCitizenExitAnalysis
            {
                StatusKey = "StarCitizenExitLogMissing",
                Summary = "Game.log not found."
            };
        }

        var lines = logPaths
            .SelectMany(path => ReadRelevantLines(
                path,
                path.Equals(primaryLogPath, StringComparison.OrdinalIgnoreCase) ? sessionLogOffset : 0,
                sessionStartedUtc,
                sessionEndedUtc))
            .TakeLast(350)
            .ToList();
        var evidence = FindEvidence(lines);
        var status = Classify(evidence, lines);

        return new StarCitizenExitAnalysis
        {
            StatusKey = status,
            LogPath = string.IsNullOrWhiteSpace(primaryLogPath) ? logPaths[0] : primaryLogPath,
            Evidence = evidence.Take(5).ToArray(),
            Summary = evidence.Count == 0
                ? Path.GetFileName(string.IsNullOrWhiteSpace(primaryLogPath) ? logPaths[0] : primaryLogPath)
                : string.Join(" | ", evidence.Take(3))
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
            .Take(12))
        {
            yield return candidate;
        }

        foreach (var candidate in Directory.EnumerateDirectories(path, "logbackups", SearchOption.AllDirectories)
            .SelectMany(BackupLogs)
            .OrderByDescending(file => new FileInfo(file).LastWriteTimeUtc)
            .Take(12))
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
                foreach (var backup in BackupLogs(channelDirectory).Take(6))
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
            .Take(20);
    }

    private static bool IsPotentialSessionLog(string logPath, DateTime sessionStartedUtc, DateTime sessionEndedUtc)
    {
        try
        {
            var info = new FileInfo(logPath);
            return info.LastWriteTimeUtc >= sessionStartedUtc.AddMinutes(-10)
                && info.CreationTimeUtc <= sessionEndedUtc.AddMinutes(10);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ReadRelevantLines(
        string logPath,
        long sessionLogOffset,
        DateTime sessionStartedUtc,
        DateTime sessionEndedUtc)
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
            var lines = reader.ReadToEnd()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            var windowStart = sessionStartedUtc.AddSeconds(-30);
            var windowEnd = sessionEndedUtc.AddSeconds(30);
            var windowLines = lines
                .Where(line => IsInTimeWindow(line, windowStart, windowEnd))
                .TakeLast(500)
                .ToList();

            return windowLines;
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

        if (evidence.Any(line => ContainsAny(line, NormalExitKeywords)))
        {
            return "StarCitizenExitNormalLikely";
        }

        if (evidence.Any(line => ContainsAny(line, NetworkKeywords)))
        {
            return "StarCitizenExitNetworkLikely";
        }

        return "StarCitizenExitUnknown";
    }

    private static bool IsInTimeWindow(string line, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var timestamp = TryReadTimestamp(line);
        return timestamp is not null && timestamp.Value >= windowStartUtc && timestamp.Value <= windowEndUtc;
    }

    private static DateTime? TryReadTimestamp(string line)
    {
        if (!line.StartsWith('<') || line.Length < 22)
        {
            return null;
        }

        var end = line.IndexOf('>');
        if (end <= 1)
        {
            return null;
        }

        return DateTime.TryParse(
            line[1..end],
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;
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
