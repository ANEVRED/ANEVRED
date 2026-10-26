namespace ANEVRED.Services;

/// <summary>Prevents concurrent application instances from overwriting shared settings.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;
    public bool Acquired { get; }

    public SingleInstanceService(string name = @"Local\ANEVRED.SingleInstance")
    {
        _mutex = new Mutex(false, name);
        try { Acquired = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Acquired = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Acquired) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
