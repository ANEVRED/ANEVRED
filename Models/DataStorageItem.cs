namespace ANEVRED.Models;

public sealed class DataStorageItem
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastModifiedUtc { get; init; }
    public bool IsSettings { get; init; }

    public string SizeText => FormatBytes(SizeBytes);
    public string LastModifiedText => LastModifiedUtc == default
        ? "-"
        : LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0 ? $"{display:0} {units[unit]}" : $"{display:0.0} {units[unit]}";
    }
}
