using System.Text.Json;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

public interface IComparisonEngine : IAsyncDisposable
{
    Task<JsonDocument> SendAsync(CompareRequest request, CancellationToken cancellationToken = default, IProgress<int>? progress = null);
    Task PingAsync(CancellationToken cancellationToken = default);
    void AbortCurrentOperation();
}
