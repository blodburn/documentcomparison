using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

public interface IComparisonEngine : IAsyncDisposable
{
    Task<ComparisonResultVm> CompareAsync(
        IReadOnlyList<string> paths,
        int baseIndex,
        string mode,
        bool includeAC,
        bool includePunctuation,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null);

    Task ExportExcelAsync(
        ComparisonResultVm result,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task ExportWordAsync(
        string originalPath,
        string revisedPath,
        string outputPath,
        string author,
        bool includePunctuation,
        CancellationToken cancellationToken = default,
        ComparisonResultVm? comparisonResult = null,
        int originalDocumentIndex = -1,
        int revisedDocumentIndex = -1);

    Task PingAsync(CancellationToken cancellationToken = default);
    void AbortCurrentOperation();
}
