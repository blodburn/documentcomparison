namespace DocumentCompare.Avalonia.Models;

/// <summary>
/// Produces one document-order review sequence for Version History.
/// Text, structural, formatting, table and style changes all share the same numbering space.
/// </summary>
public static class VersionHistoryChangeSequencer
{
    private sealed class Entry
    {
        public int RowOrder { get; init; }
        public int Position { get; init; }
        public int Priority { get; init; }
        public int StableOrder { get; init; }
        public int? RowId { get; init; }
        public string Category { get; init; } = "내용";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string? AnchorText { get; init; }
        public MarkerVm? Marker { get; init; }
    }

    public static List<VersionChangeVm> Sequence(
        ComparisonResultVm result,
        IReadOnlyList<VersionChangeVm> formattingItems,
        int displayDocIndex)
    {
        var entries = new List<Entry>();
        var stable = 0;
        var rowOrderById = result.Rows.Select((row, index) => (row.Id, index))
            .ToDictionary(x => x.Id, x => x.index);

        // Build document-positioned entries row by row. Row-level structure is deliberately
        // placed before text changes inside that same row because it describes the container
        // the following text belongs to (article/section/item movement or hierarchy change).
        foreach (var (row, rowOrder) in result.Rows.Select((row, index) => (row, index)))
        {
            foreach (var message in StructuralMessages(row))
            {
                var member = MemberAt(row, displayDocIndex) ?? MemberAt(row, 0);
                entries.Add(new Entry
                {
                    RowOrder = rowOrder,
                    Position = -1000,
                    Priority = 0,
                    StableOrder = stable++,
                    RowId = row.Id,
                    Category = "구조",
                    Title = "구조 변경",
                    Detail = message,
                    AnchorText = member?.Header ?? member?.Body
                });
            }

            foreach (var marker in row.Markers)
            {
                entries.Add(new Entry
                {
                    RowOrder = rowOrder,
                    Position = MarkerPosition(row, marker, displayDocIndex),
                    Priority = marker.StructuralNumber ? 1 : 2,
                    StableOrder = stable++,
                    RowId = row.Id,
                    Category = marker.StructuralNumber ? "구조" : "내용",
                    Title = marker.Action,
                    Detail = marker.Message,
                    Marker = marker
                });
            }
        }

        // Formatting/table changes are anchored to their paragraph/table when possible.
        // Truly document-global changes (for example a named style definition with no unique
        // paragraph owner) intentionally remain after the positioned document changes.
        foreach (var source in formattingItems)
        {
            var located = LocateAnchor(result, source.AnchorText, displayDocIndex, rowOrderById);
            entries.Add(new Entry
            {
                RowOrder = located.RowOrder,
                // Supplemental formatting/table/style badges are rendered at the top of
                // their owning row, so sequence them before that row's inline text markers.
                Position = located.RowId.HasValue ? -500 : located.Position,
                Priority = 1,
                StableOrder = stable++,
                RowId = located.RowId,
                Category = source.Category,
                Title = source.Title,
                Detail = source.Detail,
                AnchorText = source.AnchorText
            });
        }

        var ordered = entries
            .OrderBy(x => x.RowOrder)
            .ThenBy(x => x.Position)
            .ThenBy(x => x.Priority)
            .ThenBy(x => x.StableOrder)
            .ToList();

        var numberByMarker = new Dictionary<MarkerVm, int>(ReferenceEqualityComparer.Instance);
        var changes = new List<VersionChangeVm>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var number = i + 1;
            if (entry.Marker is not null) numberByMarker[entry.Marker] = number;
            changes.Add(new VersionChangeVm
            {
                Category = entry.Category,
                Title = entry.Title,
                Detail = entry.Detail,
                MarkerNumber = number,
                AnchorText = entry.AnchorText,
                RowId = entry.RowId
            });
        }

        // Placements read MarkerVm.Num at render time, so clone the original marker objects
        // with their document-order number after the unified sequence has been computed.
        foreach (var row in result.Rows)
        {
            if (row.Markers.Count == 0) continue;
            var replacements = new List<MarkerVm>(row.Markers.Count);
            foreach (var marker in row.Markers)
            {
                var number = numberByMarker.TryGetValue(marker, out var mapped) ? mapped : marker.Num;
                replacements.Add(CloneMarker(marker, number));
            }
            row.Markers.Clear();
            row.Markers.AddRange(replacements);
        }

        return changes;
    }

    private static IEnumerable<string> StructuralMessages(ComparisonRowVm row)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in row.DisplayMessages)
        {
            if (!raw.Contains("상태:", StringComparison.Ordinal) &&
                !raw.Contains("이동", StringComparison.Ordinal) &&
                !raw.Contains("구조", StringComparison.Ordinal))
                continue;
            var detail = raw.TrimStart('•', ' ');
            if (detail.Length > 0 && seen.Add(detail)) yield return detail;
        }
    }

    private static int MarkerPosition(ComparisonRowVm row, MarkerVm marker, int displayDocIndex)
    {
        var endpoint = marker.Endpoints
            .Where(x => x.TargetDoc == displayDocIndex)
            .Select(x => x.CharStart)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        if (endpoint != int.MaxValue) return Math.Max(0, endpoint);
        if (marker.TargetDoc == displayDocIndex) return Math.Max(0, marker.CharStart);

        // A pure deletion can have no endpoint in the after document. It still belongs to this
        // row, so keep it near the row start instead of pushing it to the end of the document.
        return 0;
    }

    private static (int RowOrder, int Position, int? RowId) LocateAnchor(
        ComparisonResultVm result,
        string? anchorText,
        int displayDocIndex,
        IReadOnlyDictionary<int, int> rowOrderById)
    {
        if (string.IsNullOrWhiteSpace(anchorText))
            return (int.MaxValue, int.MaxValue, null);

        var anchor = anchorText.Trim();
        foreach (var docIndex in new[] { displayDocIndex, 0 }.Distinct())
        {
            foreach (var row in result.Rows)
            {
                var member = MemberAt(row, docIndex);
                if (member is null) continue;
                var pos = FindPosition(member, anchor);
                if (pos < 0) continue;
                return (rowOrderById[row.Id], pos, row.Id);
            }
        }

        var normalizedAnchor = Normalize(anchor);
        if (normalizedAnchor.Length > 0)
        {
            foreach (var docIndex in new[] { displayDocIndex, 0 }.Distinct())
            {
                foreach (var row in result.Rows)
                {
                    var member = MemberAt(row, docIndex);
                    if (member is null) continue;
                    var normalizedText = Normalize(member.Text);
                    if (normalizedText.Contains(normalizedAnchor, StringComparison.Ordinal) ||
                        normalizedAnchor.Contains(normalizedText, StringComparison.Ordinal))
                        return (rowOrderById[row.Id], 0, row.Id);
                }
            }
        }
        return (int.MaxValue, int.MaxValue, null);
    }

    private static int FindPosition(MemberVm member, string anchor)
    {
        var index = member.Text.IndexOf(anchor, StringComparison.Ordinal);
        if (index >= 0) return index;
        index = member.Header.IndexOf(anchor, StringComparison.Ordinal);
        if (index >= 0) return index;
        index = member.Body.IndexOf(anchor, StringComparison.Ordinal);
        return index < 0 ? -1 : member.Header.Length + 1 + index;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : string.Concat(value.Where(ch => !char.IsWhiteSpace(ch))).Trim();

    private static MemberVm? MemberAt(ComparisonRowVm row, int docIndex) =>
        docIndex >= 0 && docIndex < row.Members.Count ? row.Members[docIndex] : null;

    private static MarkerVm CloneMarker(MarkerVm marker, int number) => new()
    {
        Num = number,
        RelativeDoc = marker.RelativeDoc,
        TargetDoc = marker.TargetDoc,
        Action = marker.Action,
        Text = marker.Text,
        CharStart = marker.CharStart,
        CharEnd = marker.CharEnd,
        Part = marker.Part,
        Message = marker.Message,
        Label = marker.Label,
        Pair = marker.Pair,
        StructuralNumber = marker.StructuralNumber,
        Endpoints = marker.Endpoints.Select(ep => new MarkerEndpointVm
        {
            TargetDoc = ep.TargetDoc,
            CharStart = ep.CharStart,
            CharEnd = ep.CharEnd
        }).ToList()
    };
}
