namespace DocumentCompare.Avalonia.Models;

public sealed class VersionFileVm
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class VersionHistoryProjectVm
{
    public string Name { get; set; } = "문서 버전";
    public List<VersionFileVm> Versions { get; set; } = new();
}

public sealed class VersionChangeVm
{
    public string Category { get; init; } = "내용";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public int? MarkerNumber { get; init; }
    public string? AnchorText { get; init; }
}
