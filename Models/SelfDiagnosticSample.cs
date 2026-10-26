namespace ANEVRED.Models;

public sealed record SelfDiagnosticSample(
    DateTime TimeUtc,
    double PrivateMemoryMb,
    double WorkingSetMb,
    double ManagedMemoryMb,
    double CpuPercent,
    int Handles,
    int Threads);
