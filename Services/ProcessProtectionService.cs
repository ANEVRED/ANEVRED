using System.Text.RegularExpressions;
using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

public sealed class ProcessProtectionService
{
    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "lsm", "svchost", "fontdrvhost", "dwm", "spoolsv", "Memory Compression",
        "SecurityHealthService", "SecurityHealthSystray", "MsMpEng", "NisSrv", "Sense",
        "MpDefenderCoreService", "wsc_proxy", "SgrmBroker", "audiodg",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "BEDaisy",
        "vgc", "vgk", "RiotClientServices"
    };

    private readonly AppSettings _settings;

    public ProcessProtectionService(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsProtectedProcessName(string processName)
    {
        if (IsCriticalProcessName(processName))
        {
            return true;
        }

        var candidates = CandidateNames(processName);
        return _settings.ProtectionRules
            .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.Pattern))
            .Any(rule => candidates.Any(candidate => WildcardMatch(candidate, rule.Pattern)));
    }

    public bool IsCriticalProcessName(string processName)
    {
        var normalized = Normalize(processName);
        return CriticalProcessNames.Contains(normalized) || CriticalProcessNames.Contains(RemoveExe(normalized));
    }

    public bool IsStarCitizen(string processName)
    {
        var normalized = RemoveExe(Normalize(processName));
        return normalized.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsLauncher(string processName)
    {
        var normalized = Normalize(processName);
        return normalized.Contains("RSI", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Roberts", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidateNames(string processName)
    {
        var normalized = Normalize(processName);
        yield return normalized;

        var withoutExe = RemoveExe(normalized);
        yield return withoutExe;
        yield return withoutExe + ".exe";
    }

    private static string Normalize(string value)
    {
        return value.Trim();
    }

    private static string RemoveExe(string value)
    {
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
