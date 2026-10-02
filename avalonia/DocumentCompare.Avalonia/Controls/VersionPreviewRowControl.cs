using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentCompare.Avalonia.Engine;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Controls;

public sealed class VersionPreviewRowControl : UserControl
{
    private sealed record LocalMark(int Num, int Start, int End, string Role);
    private readonly ComparisonRowVm _row;
    private readonly int _docIndex;
    private readonly VersionDocumentStyleMap? _styleMap;
    private readonly IReadOnlySet<string> _formatAnchors;

    private static readonly IBrush DeleteBrush = new SolidColorBrush(Color.Parse("#C62828"));
    private static readonly IBrush InsertBrush = new SolidColorBrush(Color.Parse("#1565C0"));
    private static readonly IBrush CellBorderBrush = new SolidColorBrush(Color.Parse("#E1E7EF"));
    private static readonly IBrush FormatBrush = new SolidColorBrush(Color.Parse("#7C3AED"));
    private static readonly IBrush[] MarkerFills =
    {
        new SolidColorBrush(Color.FromArgb(0x58,0xFF,0xE0,0x66)), new SolidColorBrush(Color.FromArgb(0x58,0x8D,0xD7,0xFF)),
        new SolidColorBrush(Color.FromArgb(0x58,0xA8,0xDB,0x91)), new SolidColorBrush(Color.FromArgb(0x58,0xB5,0x9A,0xE6)),
        new SolidColorBrush(Color.FromArgb(0x58,0xFF,0xB4,0x7A)), new SolidColorBrush(Color.FromArgb(0x58,0xF2,0xA3,0xB3)),
        new SolidColorBrush(Color.FromArgb(0x58,0xA8,0xB4,0xC5))
    };

    public VersionPreviewRowControl(ComparisonRowVm row, int docIndex, VersionDocumentStyleMap? styleMap = null, IReadOnlySet<string>? formatAnchors = null)
    {
        _row = row; _docIndex = docIndex; _styleMap = styleMap;
        _formatAnchors = formatAnchors ?? new HashSet<string>(StringComparer.Ordinal);
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
                BorderBrush = new SolidColorBrush(Color.Parse("#E4B4B4")), BorderThickness = new Thickness(0,0,0,1),
                Background = new SolidColorBrush(Color.Parse("#FFF7F7")), Padding = new Thickness(12,7),
                Child = new TextBlock { Text = "이 위치의 내용이 이전 버전에서 삭제되었습니다.", Foreground = DeleteBrush, FontStyle = FontStyle.Italic, FontSize = 12 }
            };
        }

        var stack = new StackPanel { Spacing = 2 };
        if (!string.IsNullOrWhiteSpace(member.Header)) stack.Children.Add(BuildPart(member.Header, "header", true));
        if (!string.IsNullOrWhiteSpace(member.Body)) stack.Children.Add(BuildPart(member.Body, "body", false));
        return new Border
        {
            Background = Brushes.White, BorderBrush = CellBorderBrush, BorderThickness = new Thickness(0,0,0,1),
            Padding = new Thickness(12,7), Child = stack
        };
    }

    private Control BuildPart(string raw, string part, bool fallbackBold)
    {
        var all = _row.PlacementsFor(_docIndex, part).OrderBy(x => x.Start).ThenBy(x => x.Num).ToList();
        var panel = new StackPanel { Spacing = 0 };
        var offset = 0;
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var lineEnd = offset + line.Length;
            var marks = all.Where(p => (p.End > p.Start && p.Start < lineEnd && p.End > offset) || (p.Start == p.End && p.Start >= offset && p.Start <= lineEnd))
                .Select(p => new LocalMark(p.Num, Math.Clamp(p.Start - offset, 0, line.Length), Math.Clamp(p.End - offset, 0, line.Length), p.Role)).ToList();
            var style = _styleMap?.Take(line);
            var text = BuildLine(line, marks, fallbackBold, style);
            Control item = text;
            if (_formatAnchors.Contains(VersionDocumentStyleMap.Normalize(line)))
            {
                item = new Border
                {
                    BorderBrush = FormatBrush, BorderThickness = new Thickness(3,0,0,0),
                    Background = new SolidColorBrush(Color.FromArgb(0x16,0x7C,0x3A,0xED)),
                    Padding = new Thickness(6,2,4,2), Child = text
                };
            }
            panel.Children.Add(item);
            offset = lineEnd + 1;
        }
        return panel;
    }

    private static TextBlock BuildLine(string raw, List<LocalMark> marks, bool fallbackBold, VersionParagraphVisualStyle? paragraph)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = paragraph?.DefaultRun.FontSize ?? 13,
            FontWeight = paragraph is null ? (fallbackBold ? FontWeight.SemiBold : FontWeight.Normal) : (paragraph.DefaultRun.Bold ? FontWeight.Bold : FontWeight.Normal),
            TextAlignment = paragraph?.Alignment ?? TextAlignment.Left,
            Margin = paragraph is null ? new Thickness(0) : new Thickness(paragraph.LeftIndent, paragraph.Before, 0, paragraph.After)
        };
        if (paragraph?.LineHeight is double lh && lh > 0) tb.LineHeight = lh;

        var exactStyleCoordinates = paragraph is not null && string.Equals(raw, paragraph.Text, StringComparison.Ordinal);
        var cuts = new SortedSet<int> { 0, raw.Length };
        foreach (var m in marks) { cuts.Add(m.Start); cuts.Add(m.End); }
        if (exactStyleCoordinates && paragraph is not null)
            foreach (var span in paragraph.Runs) { cuts.Add(Math.Clamp(span.Start,0,raw.Length)); cuts.Add(Math.Clamp(span.End,0,raw.Length)); }

        var emitted = new HashSet<(int,int)>();
        var points = cuts.ToArray();
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var start = points[i];
            AddBadges(tb, marks, emitted, start);
            var end = points[i + 1]; if (end <= start) continue;
            var relevant = marks.Where(m => m.End > m.Start && start < m.End && m.Start < end).ToArray();
            var style = exactStyleCoordinates && paragraph is not null
                ? paragraph.Runs.FirstOrDefault(x => x.Start <= start && x.End > start)?.Style ?? paragraph.DefaultRun
                : paragraph?.DefaultRun;
            tb.Inlines!.Add(MakeRun(raw[start..end], style, fallbackBold, relevant));
        }
        AddBadges(tb, marks, emitted, raw.Length);
        if (raw.Length == 0) tb.Text = " ";
        return tb;
    }

    private static void AddBadges(TextBlock tb, List<LocalMark> marks, HashSet<(int,int)> emitted, int position)
    {
        foreach (var m in marks.Where(x => x.Start == position))
        {
            if (!emitted.Add((position,m.Num))) continue;
            tb.Inlines!.Add(new InlineUIContainer(new Border
            {
                Background = MarkerFill(m.Num), CornerRadius = new CornerRadius(3), Padding = new Thickness(3,0), Margin = new Thickness(1,0,2,0),
                Child = new TextBlock { Text = $"[{m.Num}]", FontSize = 9, FontWeight = FontWeight.SemiBold }
            }) { BaselineAlignment = BaselineAlignment.Baseline });
        }
    }

    private static Run MakeRun(string text, VersionRunVisualStyle? style, bool fallbackBold, IReadOnlyList<LocalMark> marks)
    {
        var roles = marks.Select(x => x.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var change = roles.Contains("insert") ? "insert" : roles.Contains("delete") ? "delete" : "normal";
        var run = new Run
        {
            Text = text,
            FontSize = style?.FontSize ?? 13,
            FontWeight = style is null ? (fallbackBold ? FontWeight.SemiBold : FontWeight.Normal) : (style.Bold ? FontWeight.Bold : FontWeight.Normal),
            FontStyle = style?.Italic == true ? FontStyle.Italic : FontStyle.Normal,
            Foreground = change == "insert" ? InsertBrush : change == "delete" ? DeleteBrush : Brush(style?.Color, Brushes.Black),
            Background = marks.Count > 0 ? MarkerFill(marks[0].Num) : Brushes.Transparent,
            TextDecorations = change == "insert" ? TextDecorations.Underline : change == "delete" ? TextDecorations.Strikethrough :
                style?.Underline == true ? TextDecorations.Underline : style?.Strike == true ? TextDecorations.Strikethrough : null
        };
        if (!string.IsNullOrWhiteSpace(style?.FontFamily)) run.FontFamily = new FontFamily(style.FontFamily);
        return run;
    }

    private static IBrush Brush(string? value, IBrush fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try { return new SolidColorBrush(Color.Parse(value)); } catch { return fallback; }
    }

    private static IBrush MarkerFill(int num) => MarkerFills[(Math.Max(1,num)-1)%MarkerFills.Length];
}
