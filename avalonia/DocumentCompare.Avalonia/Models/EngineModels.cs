using System.Text.Json;

namespace DocumentCompare.Avalonia.Models;

public sealed record CompareRequest(
    string Command,
    IReadOnlyList<string> Paths,
    int BaseIndex,
    string Mode,
    string? OutputPath = null,
    string? OriginalPath = null,
    string? RevisedPath = null,
    string? Author = null,
    bool IncludeAC = false,
    bool IncludePunctuation = true,
    bool WantProgress = false);

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
        // Python/Tk stores body marker offsets against one combined text buffer:
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

public sealed class ComparisonResultVm
{
    public List<string> Names { get; init; } = new();
    public int BaseIndex { get; init; }
    public List<ComparisonRowVm> Rows { get; init; } = new();
    public List<int> UnitCounts { get; init; } = new();
}

public static class EngineResultMapper
{
    public static ComparisonResultVm Parse(JsonElement root)
    {
        var result = root.GetProperty("result");
        var names = result.GetProperty("names").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        var baseIndex = result.TryGetProperty("base_index", out var bi) ? bi.GetInt32() : 0;
        var unitCounts = result.TryGetProperty("unit_counts", out var uc)
            ? uc.EnumerateArray().Select(x => x.GetInt32()).ToList()
            : new List<int>();

        var rows = new List<ComparisonRowVm>();
        foreach (var r in result.GetProperty("rows").EnumerateArray())
        {
            rows.Add(ParseRow(r, names.Count));
        }

        // Chapter/section labels are emitted only when that document's section changes.
        var previousSections = new string?[names.Count];
        foreach (var row in rows)
        {
            var labels = new List<string?>();
            for (var i = 0; i < names.Count; i++)
            {
                var section = row.Members.Count > i ? row.Members[i]?.Section : null;
                if (!string.IsNullOrWhiteSpace(section) && section != previousSections[i])
                {
                    labels.Add(section);
                    previousSections[i] = section;
                }
                else
                {
                    labels.Add(null);
                }
            }
            row.SectionHeaders = labels;
        }

        return new ComparisonResultVm { Names = names, BaseIndex = baseIndex, Rows = rows, UnitCounts = unitCounts };
    }

    private static ComparisonRowVm ParseRow(JsonElement r, int docCount)
    {
        var members = new List<MemberVm?>();
        if (r.TryGetProperty("members", out var mEl))
        {
            foreach (var m in mEl.EnumerateArray())
                members.Add(m.ValueKind == JsonValueKind.Null ? null : ParseMember(m));
        }
        while (members.Count < docCount) members.Add(null);

        var headers = ParseSegmentMatrix(r, "header_segments", docCount);
        var bodies = ParseSegmentMatrix(r, "body_segments", docCount);

        var markers = new List<MarkerVm>();
        if (r.TryGetProperty("markers", out var markerEl))
        {
            foreach (var m in markerEl.EnumerateArray())
            {
                var eps = new List<MarkerEndpointVm>();
                if (m.TryGetProperty("endpoints", out var epEl))
                {
                    foreach (var ep in epEl.EnumerateArray())
                    {
                        eps.Add(new MarkerEndpointVm
                        {
                            TargetDoc = GetInt(ep, "target_doc"),
                            CharStart = GetInt(ep, "char_start"),
                            CharEnd = GetInt(ep, "char_end")
                        });
                    }
                }
                markers.Add(new MarkerVm
                {
                    Num = GetInt(m, "num"),
                    RelativeDoc = GetInt(m, "relative_doc"),
                    TargetDoc = GetInt(m, "target_doc"),
                    Action = GetString(m, "action"),
                    Text = GetString(m, "text"),
                    CharStart = GetInt(m, "char_start"),
                    CharEnd = GetInt(m, "char_end"),
                    Part = GetString(m, "part", "body"),
                    Message = GetString(m, "message"),
                    Label = GetString(m, "label"),
                    Pair = GetString(m, "pair"),
                    StructuralNumber = m.TryGetProperty("structural_number", out var sn) && sn.ValueKind == JsonValueKind.True,
                    Endpoints = eps
                });
            }
        }

        var messages = r.TryGetProperty("display_messages", out var msgEl)
            ? msgEl.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();

        return new ComparisonRowVm
        {
            Id = GetInt(r, "id"),
            Members = members,
            HeaderSegments = headers,
            BodySegments = bodies,
            Markers = markers,
            DisplayMessages = messages,
            Changed = r.TryGetProperty("changed", out var changed) && changed.GetBoolean()
        };
    }

    private static MemberVm ParseMember(JsonElement m) => new()
    {
        Index = GetInt(m, "index"),
        Kind = GetString(m, "kind"),
        Number = GetString(m, "number"),
        Title = GetString(m, "title"),
        Header = GetString(m, "header"),
        Body = GetString(m, "body"),
        Text = GetString(m, "text"),
        Section = GetString(m, "section")
    };

    private static List<List<SegmentVm>> ParseSegmentMatrix(JsonElement r, string property, int docCount)
    {
        var matrix = new List<List<SegmentVm>>();
        if (r.TryGetProperty(property, out var root))
        {
            foreach (var doc in root.EnumerateArray())
            {
                var row = new List<SegmentVm>();
                foreach (var seg in doc.EnumerateArray())
                {
                    if (seg.ValueKind != JsonValueKind.Array) continue;
                    var arr = seg.EnumerateArray().ToArray();
                    if (arr.Length >= 2)
                        row.Add(new SegmentVm { Text = arr[0].GetString() ?? "", Style = arr[1].GetString() ?? "normal" });
                }
                matrix.Add(row);
            }
        }
        while (matrix.Count < docCount) matrix.Add(new List<SegmentVm>());
        return matrix;
    }

    private static string GetString(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() ?? fallback : fallback;

    private static int GetInt(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;
}
