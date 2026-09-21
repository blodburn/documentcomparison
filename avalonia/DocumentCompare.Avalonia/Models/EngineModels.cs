namespace DocumentCompare.Avalonia.Models;

public sealed class SegmentVm
{
    public string Text { get; init; } = "";
    public string Style { get; init; } = "normal";
}

public sealed class MemberVm
{
    public int Index { get; init; }
    public string Kind { get; init; } = "";
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public string Header { get; init; } = "";
    public string Body { get; init; } = "";
    public string Text { get; init; } = "";
    public string Section { get; init; } = "";
}

public sealed class MarkerEndpointVm
{
    public int TargetDoc { get; init; }
    public int CharStart { get; init; }
    public int CharEnd { get; init; }
}

public sealed class MarkerVm
{
    public int Num { get; init; }
    public int RelativeDoc { get; init; }
    public int TargetDoc { get; init; }
    public string Action { get; init; } = "";
    public string Text { get; init; } = "";
    public int CharStart { get; init; }
    public int CharEnd { get; init; }
    public string Part { get; init; } = "body";
    public string Message { get; init; } = "";
    public string Label { get; init; } = "";
    public string Pair { get; init; } = "";
    public bool StructuralNumber { get; init; }
    public List<MarkerEndpointVm> Endpoints { get; init; } = new();
}

public sealed class MarkerPlacementVm
{
    public int Num { get; init; }
    public int DocIndex { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public string Part { get; init; } = "body";
    public string Role { get; init; } = "change";
    public string Message { get; init; } = "";
    public string Pair { get; init; } = "";
    public bool StructuralNumber { get; init; }
}

public sealed class ComparisonRowVm
{
    public int Id { get; init; }
    public List<MemberVm?> Members { get; init; } = new();
    public List<List<SegmentVm>> HeaderSegments { get; init; } = new();
    public List<List<SegmentVm>> BodySegments { get; init; } = new();
    public List<MarkerVm> Markers { get; init; } = new();
    public List<string> DisplayMessages { get; init; } = new();
    public List<string?> SectionHeaders { get; set; } = new();
    public bool Changed { get; init; }

    public IEnumerable<MarkerPlacementVm> PlacementsFor(int docIndex, string part)
    {
        // The comparison engine stores body marker offsets against one combined text buffer:
        //     header + "\n" + body
        // Avalonia renders header and body in separate TextBlocks.  Therefore body
        // offsets must be converted to body-local coordinates, and a marker must be
        // emitted only into the TextBlock that owns its `part`.  V5.3 skipped both
        // rules, which duplicated every marker in the article title and body and
        // shifted body markers by the header length.
        var member = Members.Count > docIndex ? Members[docIndex] : null;
        var partOffset = string.Equals(part, "body", StringComparison.OrdinalIgnoreCase)
            ? (member?.Header?.Length ?? 0) + 1
            : 0;

        foreach (var marker in Markers)
        {
            if (!string.Equals(marker.Part, part, StringComparison.OrdinalIgnoreCase))
                continue;

            if (marker.Endpoints.Count > 0)
            {
                foreach (var ep in marker.Endpoints)
                {
                    if (ep.TargetDoc != docIndex) continue;
                    var role = ep.CharStart == ep.CharEnd ? "anchor" :
                        marker.Action.Contains("삭제") ? "delete" :
                        marker.Action.Contains("추가") ? "insert" :
                        (docIndex == marker.TargetDoc ? "insert" : "delete");
                    yield return new MarkerPlacementVm
                    {
                        Num = marker.Num,
                        DocIndex = docIndex,
                        Start = Math.Max(0, ep.CharStart - partOffset),
                        End = Math.Max(0, ep.CharEnd - partOffset),
                        Part = marker.Part,
                        Role = role,
                        Message = marker.Message,
                        Pair = marker.Pair,
                        StructuralNumber = marker.StructuralNumber
                    };
                }
            }
            else if (marker.TargetDoc == docIndex)
            {
                var role = marker.Action.Contains("삭제") ? "delete" :
                    marker.Action.Contains("추가") ? "insert" : "change";
                yield return new MarkerPlacementVm
                {
                    Num = marker.Num,
                    DocIndex = docIndex,
                    Start = Math.Max(0, marker.CharStart - partOffset),
                    End = Math.Max(0, marker.CharEnd - partOffset),
                    Part = marker.Part,
                    Role = role,
                    Message = marker.Message,
                    Pair = marker.Pair,
                    StructuralNumber = marker.StructuralNumber
                };
            }
        }
    }
}

public sealed class SourceFileStateVm
{
    public string Path { get; init; } = "";
    public long Length { get; init; }
    public long LastWriteTimeUtcTicks { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed class ComparisonResultVm
{
    public List<string> Names { get; init; } = new();
    public int BaseIndex { get; init; }
    public List<ComparisonRowVm> Rows { get; init; } = new();
    public List<int> UnitCounts { get; init; } = new();
    public List<SourceFileStateVm> SourceFiles { get; init; } = new();
}
