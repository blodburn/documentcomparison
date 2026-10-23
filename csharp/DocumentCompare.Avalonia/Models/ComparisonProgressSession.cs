using System.Threading;

namespace DocumentCompare.Avalonia.Models;

// UI Dispatcher callbacks may arrive after the async engine has completed.
// This per-invocation gate prevents a late 100% progress event from reviving a terminal status.
public sealed class ComparisonProgressSession
{
    private int _closed;
    public bool TryAccept(int value) => Volatile.Read(ref _closed) == 0 && value is >= 0 and <= 100;
    public void Close() => Interlocked.Exchange(ref _closed, 1);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
}
