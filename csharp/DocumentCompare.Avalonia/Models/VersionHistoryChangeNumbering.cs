namespace DocumentCompare.Avalonia.Models;

public static class VersionHistoryChangeNumbering
{
    public static List<VersionChangeVm> EnsureNumbered(IEnumerable<VersionChangeVm> changes)
    {
        var source = changes.ToList();
        var next = source.Where(x => x.MarkerNumber.HasValue).Select(x => x.MarkerNumber!.Value).DefaultIfEmpty(0).Max() + 1;
        var result = new List<VersionChangeVm>(source.Count);
        foreach (var item in source)
        {
            var number = item.MarkerNumber ?? next++;
            result.Add(new VersionChangeVm
            {
                Category = item.Category,
                Title = item.Title,
                Detail = item.Detail,
                MarkerNumber = number,
                AnchorText = item.AnchorText,
                RowId = item.RowId
            });
        }
        return result.OrderBy(x => x.MarkerNumber).ToList();
    }
}
