using System.IO;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class StarCitizenLogService
{
    private static readonly string[] Channels = ["LIVE", "PTU", "EPTU", "TECH-PREVIEW"];
    private static readonly string[] CrashKeywords =
    [
        "crash",
        "crash handler",
        "public crash handler",
        "fatal",
        "exception",
        "access violation",
        "exception_access_violation",
        "out of memory",
        "device removed",
        "dxgi_error",
        "gpu crash",
        "hung",
        "saved dump file",
        "failed to allocate"
    ];

    private static readonly string[] CrashContextKeywords =
    [
        "process memory status",
        "system memory status",
        "level profile statistics",
        "saved dump file",
        "saved screenshot",
        "saved cvar dump",
        "saved build info",
        "copied game.log",
        "is fatal error",
        "is gpu crash",
        "is timeout",
        "is out of system memory",
        "is out of video memory",
        "all crash related data"
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
        "system fast shutdown",
        "ccigbroker::fastshutdown",
        "cgame::ondisconnected client quitting game",
        "quit requested",
        "exitcode=0",
        "shutdown",
        "game shutdown"
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
        var crashArtifactEvidence = FindCrashArtifactEvidence(sessionStartedUtc, sessionEndedUtc);
        var logPaths = FindLogCandidates(configuredPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => IsPotentialSessionLog(path, sessionStartedUtc, sessionEndedUtc))
            .ThenByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .Take(80)
            .ToList();
        if (logPaths.Count == 0)
        {
            if (crashArtifactEvidence.Count > 0)
            {
                return new StarCitizenExitAnalysis
                {
                    StatusKey = "StarCitizenExitCrashLikely",
                    Evidence = crashArtifactEvidence,
                    Summary = string.Join(" | ", crashArtifactEvidence.Take(3))
                };
            }

            return new StarCitizenExitAnalysis
            {
                StatusKey = "StarCitizenExitLogMissing",
                Summary = "Game.log not found."
            };
        }

        var lines = logPaths
            .SelectMany(path => ReadRelevantLines(
                path,
                0,
                sessionStartedUtc,
                sessionEndedUtc))
            .ToList();
        lines.AddRange(crashArtifactEvidence);
        var evidence = FindEvidence(lines);
        var status = Classify(evidence, lines);
        var displayLogPath = logPaths
            .FirstOrDefault(path => IsPotentialSessionLog(path, sessionStartedUtc, sessionEndedUtc))
            ?? logPaths[0];

        return new StarCitizenExitAnalysis
        {
            StatusKey = status,
            LogPath = displayLogPath,
            Evidence = evidence.Take(8).ToArray(),
            Summary = evidence.Count == 0
                ? Path.GetFileName(displayLogPath)
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

    public IReadOnlyList<StarCitizenLogSession> DiscoverSessions(string configuredPath, DateTime importSinceUtc)
    {
        var sessions = new List<StarCitizenLogSession>();
        var logPaths = FindLogCandidates(configuredPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .Take(40)
            .ToList();

        foreach (var logPath in logPaths)
        {
            var session = TryReadSessionFromLog(logPath, importSinceUtc);
            if (session is not null)
            {
                var analysis = AnalyzeExit(configuredPath, 0, session.StartedUtc, session.EndedUtc);
                sessions.Add(new StarCitizenLogSession
                {
                    StartedUtc = session.StartedUtc,
                    EndedUtc = session.EndedUtc,
                    StatusKey = analysis.StatusKey,
                    LogPath = string.IsNullOrWhiteSpace(analysis.LogPath) ? session.LogPath : analysis.LogPath,
                    Evidence = analysis.Evidence.Count == 0 ? session.Evidence : analysis.Evidence
                });
            }
        }

        return sessions
            .OrderByDescending(session => session.StartedUtc)
            .ToList();
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

        foreach (var candidate in CrashLogPaths())
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
        var commonRoots = new List<string>
        {
            Path.Combine(programFiles, "Roberts Space Industries", "StarCitizen"),
            Path.Combine(programFilesX86, "Roberts Space Industries", "StarCitizen"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StarCitizen")
        };
        commonRoots.AddRange(DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .SelectMany(drive => new[]
            {
                Path.Combine(drive.RootDirectory.FullName, "Roberts Space Industries", "StarCitizen"),
                Path.Combine(drive.RootDirectory.FullName, "Cloud Imperium Games", "StarCitizen"),
                Path.Combine(drive.RootDirectory.FullName, "games", "Cloud Imperium Games", "StarCitizen"),
                Path.Combine(drive.RootDirectory.FullName, "Games", "Cloud Imperium Games", "StarCitizen")
            }));

        foreach (var root in commonRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in LogsUnderRoot(root))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> LogsUnderRoot(string root)
    {
        var direct = Path.Combine(root, "Game.log");
        if (File.Exists(direct))
        {
            yield return direct;
        }

        foreach (var backup in BackupLogs(root).Take(8))
        {
            yield return backup;
        }

        foreach (var channel in Channels)
        {
            var channelDirectory = Path.Combine(root, channel);
            yield return Path.Combine(channelDirectory, "Game.log");
            foreach (var backup in BackupLogs(channelDirectory).Take(8))
            {
                yield return backup;
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

    private static List<string> FindCrashArtifactEvidence(DateTime sessionStartedUtc, DateTime sessionEndedUtc)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return [];
        }

        var crashDirectory = Path.Combine(localAppData, "Star Citizen", "Crashes");
        if (!Directory.Exists(crashDirectory))
        {
            return [];
        }

        var windowStart = sessionEndedUtc.AddMinutes(-10);
        var windowEnd = sessionEndedUtc.AddMinutes(10);
        var names = new[] { "error.dmp", "game.log", "screenshot.jpg", "cvar.dump", "build.info" };

        return names
            .Select(name => Path.Combine(crashDirectory, name))
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Where(info => info.LastWriteTimeUtc >= windowStart && info.LastWriteTimeUtc <= windowEnd)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => $"Crash artifact: {info.Name} saved at {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}")
            .ToList();
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
            return LinesInSessionWindow(lines, sessionStartedUtc, sessionEndedUtc);
        }
        catch
        {
            return [];
        }
    }

    private static StarCitizenLogSession? TryReadSessionFromLog(string logPath, DateTime importSinceUtc)
    {
        try
        {
            var lines = File.ReadLines(logPath).ToList();
            DateTime? startedUtc = null;
            DateTime? endedUtc = null;
            DateTime? lastTimestampUtc = null;

            foreach (var line in lines)
            {
                var timestamp = TryReadTimestamp(line);
                if (timestamp is not null)
                {
                    lastTimestampUtc = timestamp.Value;
                }

                if (startedUtc is null && line.Contains("OnClientEnteredGame", StringComparison.OrdinalIgnoreCase))
                {
                    startedUtc = timestamp;
                }

                if (line.Contains("CCIGBroker::FastShutdown", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("System Fast Shutdown", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("<SystemQuit>", StringComparison.OrdinalIgnoreCase))
                {
                    endedUtc = timestamp ?? lastTimestampUtc ?? File.GetLastWriteTimeUtc(logPath);
                }
                else if (startedUtc is not null && ContainsAny(line, CrashKeywords))
                {
                    endedUtc = timestamp ?? lastTimestampUtc ?? File.GetLastWriteTimeUtc(logPath);
                }
            }

            if (startedUtc is null
                || endedUtc is null
                || endedUtc.Value < importSinceUtc
                || endedUtc.Value <= startedUtc.Value.AddSeconds(20))
            {
                return null;
            }

            var windowStart = startedUtc.Value.AddSeconds(-30);
            var windowEnd = endedUtc.Value.AddSeconds(30);
            var windowLines = LinesInWindow(lines, windowStart, windowEnd);
            var evidence = FindEvidence(windowLines);

            return new StarCitizenLogSession
            {
                StartedUtc = startedUtc.Value,
                EndedUtc = endedUtc.Value,
                StatusKey = Classify(evidence, windowLines),
                LogPath = logPath,
                Evidence = evidence.Take(8).ToArray()
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> CrashLogPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        var crashDirectory = Path.Combine(localAppData, "Star Citizen", "Crashes");
        yield return Path.Combine(crashDirectory, "game.log");
    }

    private static List<string> LinesInSessionWindow(
        IReadOnlyList<string> lines,
        DateTime sessionStartedUtc,
        DateTime sessionEndedUtc)
    {
        return LinesInWindow(lines, sessionStartedUtc.AddSeconds(-30), sessionEndedUtc.AddMinutes(5));
    }

    private static List<string> LinesInWindow(
        IReadOnlyList<string> lines,
        DateTime windowStartUtc,
        DateTime windowEndUtc)
    {
        var result = new List<string>();
        var includeContinuations = false;

        foreach (var line in lines)
        {
            var timestamp = TryReadTimestamp(line);
            if (timestamp is not null)
            {
                includeContinuations = timestamp.Value >= windowStartUtc && timestamp.Value <= windowEndUtc;
            }

            if (includeContinuations)
            {
                result.Add(line);
            }
        }

        return result.TakeLast(700).ToList();
    }

    private static List<string> FindEvidence(IReadOnlyList<string> lines)
    {
        var evidence = lines
            .Where(line => ContainsAny(line, CrashKeywords)
                || ContainsAny(line, NetworkKeywords)
                || ContainsAny(line, NormalExitKeywords)
                || ContainsAny(line, CrashContextKeywords))
            .Select(TrimEvidence)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var strongCrashEvidence = evidence
            .Where(IsStrongCrashEvidence)
            .Take(8)
            .ToList();
        var supportingEvidence = evidence
            .Where(line => !strongCrashEvidence.Contains(line, StringComparer.OrdinalIgnoreCase))
            .TakeLast(12 - strongCrashEvidence.Count)
            .ToList();

        return strongCrashEvidence
            .Concat(supportingEvidence)
            .Take(12)
            .ToList();
    }

    private static bool IsStrongCrashEvidence(string line)
    {
        return !line.StartsWith("Crash artifact:", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("crash handler", StringComparison.OrdinalIgnoreCase)
                || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                || line.Contains("access violation", StringComparison.OrdinalIgnoreCase)
                || line.Contains("saved dump file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("is fatal error: Yes", StringComparison.OrdinalIgnoreCase)
                || line.Contains("is gpu crash: Yes", StringComparison.OrdinalIgnoreCase)
                || line.Contains("is out of system memory: Yes", StringComparison.OrdinalIgnoreCase)
                || line.Contains("is out of video memory: Yes", StringComparison.OrdinalIgnoreCase));
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
        var cleaned = FormatEvidenceTimestamp(line.Trim());
        return cleaned.Length > 220 ? cleaned[..220] : cleaned;
    }

    private static string FormatEvidenceTimestamp(string line)
    {
        var timestamp = TryReadTimestamp(line);
        if (timestamp is null)
        {
            return line;
        }

        var end = line.IndexOf('>');
        if (end <= 0 || end >= line.Length - 1)
        {
            return $"[{timestamp.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}]";
        }

        return $"[{timestamp.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {line[(end + 1)..].TrimStart()}";
    }
}
