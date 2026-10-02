using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Controls;

public sealed class VersionPreviewRowControl : UserControl
{
    private readonly ComparisonRowVm _row;
    private readonly int _docIndex;

    private static readonly IBrush DeleteBrush = new SolidColorBrush(Color.Parse("#C62828"));
    private static readonly IBrush InsertBrush = new SolidColorBrush(Color.Parse("#1565C0"));
    private static readonly IBrush SectionBrush = new SolidColorBrush(Color.Parse("#EAF1F7"));
    private static readonly IBrush ArticleBrush = new SolidColorBrush(Color.Parse("#F4F5F7"));
    private static readonly IBrush CellBorderBrush = new SolidColorBrush(Color.Parse("#E1E7EF"));
    private static readonly IBrush[] MarkerFills =
    {
        new SolidColorBrush(Color.FromArgb(0x58, 0xFF, 0xE0, 0x66)),
        new SolidColorBrush(Color.FromArgb(0x58, 0x8D, 0xD7, 0xFF)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xA8, 0xDB, 0x91)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xB5, 0x9A, 0xE6)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xFF, 0xB4, 0x7A)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xF2, 0xA3, 0xB3)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xA8, 0xB4, 0xC5)),
    };

    public VersionPreviewRowControl(ComparisonRowVm row, int docIndex)
    {
        _row = row;
        _docIndex = docIndex;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = Build();
    }

    private Control Build()
    {
        var member = _row.Members.Count > _docIndex ? _row.Members[_docIndex] : null;
        if (member is null)
        {
            if (!_row.Changed) return new Border();
            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#E4B4B4")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = new SolidColorBrush(Color.Parse("#FFF7F7")),
                Padding = new Thickness(12, 7),
                Child = new TextBlock
                {
                    Text = "이 위치의 내용이 이전 버전에서 삭제되었습니다.",
                    Foreground = DeleteBrush,
                    FontStyle = FontStyle.Italic,
                    FontSize = 12
                }
            };
        }

        var stack = new StackPanel { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(member.Header))
        {
            var header = BuildText(member.Header, "header", true);
            stack.Children.Add(new Border
            {
                Background = member.Kind == "section" ? SectionBrush : member.Kind == "article" ? ArticleBrush : Brushes.Transparent,
                Padding = member.Kind is "section" or "article" ? new Thickness(6, 3) : new Thickness(0),
                Child = header
            });
        }
        if (!string.IsNullOrWhiteSpace(member.Body))
            stack.Children.Add(BuildText(member.Body, "body", false));

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 9),
            Child = stack
        };
    }

    private TextBlock BuildText(string raw, string part, bool bold)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            LineHeight = 24
        };
        var placements = _row.PlacementsFor(_docIndex, part)
            .OrderBy(x => x.Start).ThenBy(x => x.Num).ToList();
        if (placements.Count == 0)
        {
            tb.Text = raw;
            return tb;
        }

        var cuts = new SortedSet<int> { 0, raw.Length };
        foreach (var p in placements)
        {
            cuts.Add(Math.Clamp(p.Start, 0, raw.Length));
            cuts.Add(Math.Clamp(p.End, 0, raw.Length));
        }
        var points = cuts.ToArray();
        var emittedAnchors = new HashSet<(int, int)>();
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var start = points[i];
            foreach (var p in placements.Where(x => x.Start == start))
            {
                if (!emittedAnchors.Add((start, p.Num))) continue;
                tb.Inlines!.Add(new InlineUIContainer(new Border
                {
                    Background = MarkerFill(p.Num),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(3, 0),
                    Margin = new Thickness(1, 0, 2, 0),
                    Child = new TextBlock { Text = $"[{p.Num}]", FontSize = 9, FontWeight = FontWeight.SemiBold }
                }) { BaselineAlignment = BaselineAlignment.Baseline });
            }

            var end = points[i + 1];
            if (end <= start) continue;
            var relevant = placements.Where(p => p.End > p.Start && start < p.End && p.Start < end).ToArray();
            var roles = relevant.Select(x => x.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var style = roles.Contains("insert") ? "insert" : roles.Contains("delete") ? "delete" : "normal";
            tb.Inlines!.Add(new Run
            {
                Text = raw[start..end],
                Foreground = style == "insert" ? InsertBrush : style == "delete" ? DeleteBrush : Brushes.Black,
                Background = relevant.Length > 0 ? MarkerFill(relevant[0].Num) : Brushes.Transparent,
                TextDecorations = style == "insert" ? TextDecorations.Underline : style == "delete" ? TextDecorations.Strikethrough : null,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal
            });
        }

        foreach (var p in placements.Where(x => x.Start == raw.Length))
        {
            if (!emittedAnchors.Add((raw.Length, p.Num))) continue;
            tb.Inlines!.Add(new InlineUIContainer(new Border
            {
                Background = MarkerFill(p.Num), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0),
                Child = new TextBlock { Text = $"[{p.Num}]", FontSize = 9, FontWeight = FontWeight.SemiBold }
            }) { BaselineAlignment = BaselineAlignment.Baseline });
        }
        return tb;
    }

    private static IBrush MarkerFill(int num) => MarkerFills[(Math.Max(1, num) - 1) % MarkerFills.Length];
}
