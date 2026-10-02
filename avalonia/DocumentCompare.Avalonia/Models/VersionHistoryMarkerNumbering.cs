namespace DocumentCompare.Avalonia.Models;

public static class VersionHistoryMarkerNumbering
{
    public static void ReindexGlobally(ComparisonResultVm result)
    {
        var next = 1;
        foreach (var row in result.Rows)
        {
            if (row.Markers.Count == 0) continue;
            var replaced = new List<MarkerVm>(row.Markers.Count);
            foreach (var marker in row.Markers)
            {
                replaced.Add(new MarkerVm
                {
                    Num = next++,
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
                });
            }
            row.Markers.Clear();
            row.Markers.AddRange(replaced);
        }
    }
}
