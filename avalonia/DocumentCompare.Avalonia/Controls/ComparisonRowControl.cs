using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DocumentCompare.Avalonia.Models;
using DocumentCompare.Avalonia.Localization;

namespace DocumentCompare.Avalonia.Controls;

public sealed class ComparisonRowControl : UserControl
{
    private readonly ComparisonRowVm _row;
    private readonly int _docCount;
    private readonly Action<int, int, int, double>? _markerActivated;
    private readonly UiLanguage _language;
    private readonly Dictionary<int, List<(Control Control, string Role, string Pair)>> _markerVisuals = new();
    private readonly Dictionary<int, List<Run>> _markerRuns = new();
    private readonly Dictionary<int, bool> _hoverInside = new();
    private readonly Dictionary<int, TranslateTransform> _docNavigationTransforms = new();
    private readonly Dictionary<(int DocIndex, int Num), List<Control>> _docMarkerControls = new();
    private Grid? _contentGrid;
    private Grid? _sectionGrid;
    private Control? _paragraphAnchor;

    // ContentAnchor is the visual top of the paragraph text (inside the cell padding),
    // while ContentTop/ContentHeight describe the whole A/B/C content grid for row sizing.
    public Control? ContentAnchor => _paragraphAnchor ?? _contentGrid;
    public double ContentTop => _contentGrid?.TranslatePoint(new Point(0, 0), this)?.Y ?? 0;
    public double SectionHeight => _sectionGrid?.Bounds.Height ?? 0;
    public double ContentHeight => _contentGrid?.Bounds.Height ?? 0;

    public void SetAlignedContentHeight(double height)
    {
        if (_contentGrid is not null)
            _contentGrid.MinHeight = Math.Max(0, height);
    }
    private static readonly IBrush DeleteBrush = new SolidColorBrush(Color.Parse("#C62828"));
    private static readonly IBrush InsertBrush = new SolidColorBrush(Color.Parse("#1565C0"));
    private static readonly IBrush ChangeBrush = new SolidColorBrush(Color.Parse("#8A5A00"));
    private static readonly IBrush BothBrush = new SolidColorBrush(Color.Parse("#7B1FA2"));
    // Seven soft highlighter colors repeat by logical change number.  The same number uses
    // the same fill in both documents and in the summary, so correspondence is visible
    // without adding AB/BC text to the chip.
    private static readonly IBrush[] MarkerFills =
    {
        // Translucent highlighter fills: the source characters stay readable.
        new SolidColorBrush(Color.FromArgb(0x58, 0xFF, 0xE0, 0x66)),
        new SolidColorBrush(Color.FromArgb(0x58, 0x8D, 0xD7, 0xFF)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xA8, 0xDB, 0x91)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xB5, 0x9A, 0xE6)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xFF, 0xB4, 0x7A)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xF2, 0xA3, 0xB3)),
        new SolidColorBrush(Color.FromArgb(0x58, 0xA8, 0xB4, 0xC5)),
    };
    private static readonly IBrush[] MarkerBorders =
    {
        new SolidColorBrush(Color.Parse("#A67C00")),
        new SolidColorBrush(Color.Parse("#2B78A6")),
        new SolidColorBrush(Color.Parse("#4E7D36")),
        new SolidColorBrush(Color.Parse("#7056A8")),
        new SolidColorBrush(Color.Parse("#B8652A")),
        new SolidColorBrush(Color.Parse("#B14D63")),
        new SolidColorBrush(Color.Parse("#64748B")),
    };
    private static readonly IBrush SectionBrush = new SolidColorBrush(Color.Parse("#EAF1F7"));
    private static readonly IBrush CellBorderBrush = new SolidColorBrush(Color.Parse("#9AA4B2"));

    public ComparisonRowControl(ComparisonRowVm row, int docCount, Action<int, int, int, double>? markerActivated = null, UiLanguage language = UiLanguage.Korean)
    {
        _row = row;
        _docCount = docCount;
        _markerActivated = markerActivated;
        _language = language;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = Build();
    }

    private Control Build()
    {
        var root = new StackPanel { Spacing = 0 };
        if (_row.SectionHeaders.Any(x => !string.IsNullOrWhiteSpace(x)))
            root.Children.Add(BuildSectionGrid());
        root.Children.Add(BuildContentGrid());
        return root;
    }

    private Grid CreateColumns()
    {
        var grid = new Grid();
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        for (var i = 0; i < _docCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        return grid;
    }

    private Control BuildSectionGrid()
    {
        var grid = CreateColumns();
        _sectionGrid = grid;
        grid.Background = SectionBrush;
        for (var i = 0; i < _docCount; i++)
        {
            var border = CellBorder(i, isSection: true);
            border.Padding = new Thickness(8, 3);
            border.Child = new TextBlock
            {
                Text = _row.SectionHeaders.Count > i ? _row.SectionHeaders[i] ?? "" : "",
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(border, i);
            grid.Children.Add(border);
        }
        return grid;
    }

    private Control BuildContentGrid()
    {
        var grid = CreateColumns();
        _contentGrid = grid;
        for (var i = 0; i < _docCount; i++)
        {
            var border = CellBorder(i);
            border.Padding = new Thickness(8, 7);
            var cell = BuildDocumentCell(i);
            border.ClipToBounds = true;
            border.Child = cell;
            if (_paragraphAnchor is null && i < _row.Members.Count && _row.Members[i] is not null)
                _paragraphAnchor = cell;
            Grid.SetColumn(border, i);
            grid.Children.Add(border);
        }

        // V5.12 intentionally renders only the three document columns here.  The global
        // change list lives in MainWindow under its own ScrollViewer so marker navigation
        // cannot move the A/B/C document viewport.
        return grid;
    }

    private Border CellBorder(int column, bool isSection = false)
    {
        return new Border
        {
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(column == 0 ? 1 : 0, isSection ? 1 : 0, 1, 1),
            Background = isSection ? SectionBrush : Brushes.White
        };
    }

    private Control BuildDocumentCell(int docIndex)
    {
        if (docIndex >= _row.Members.Count)
            return new Border();

        var member = _row.Members[docIndex];
        if (member is null)
        {
            return new TextBlock
            {
                Text = UiLocalization.T(_language, "[해당 조/블록 없음]", "[No corresponding article/block]"),
                Foreground = Brushes.Gray,
                FontStyle = FontStyle.Italic,
                FontSize = 12
            };
        }

        var stack = new StackPanel { Spacing = 5 };
        var navigationTransform = new TranslateTransform();
        stack.RenderTransform = navigationTransform;
        _docNavigationTransforms[docIndex] = navigationTransform;
        var headerSegments = _row.HeaderSegments.Count > docIndex ? _row.HeaderSegments[docIndex] : new List<SegmentVm>();
        var bodySegments = _row.BodySegments.Count > docIndex ? _row.BodySegments[docIndex] : new List<SegmentVm>();
        if (!string.IsNullOrWhiteSpace(member.Header))
            stack.Children.Add(BuildRichText(member.Header, headerSegments, docIndex, "header", true));
        if (!string.IsNullOrWhiteSpace(member.Body))
            stack.Children.Add(BuildRichText(member.Body, bodySegments, docIndex, "body", false));
        return stack;
    }

    private TextBlock BuildRichText(string raw, List<SegmentVm> segments, int docIndex, string part, bool bold)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            LineHeight = 24
        };
        var placements = _row.PlacementsFor(docIndex, part)
            .Select(p => new MarkerPlacementVm
            {
                Num = p.Num,
                DocIndex = p.DocIndex,
                Start = MarkerAnchor(raw, p.Start, p.End),
                End = p.End,
                Part = p.Part,
                Role = p.Role,
                Message = p.Message,
                Pair = p.Pair,
                StructuralNumber = p.StructuralNumber
            })
            .OrderBy(p => p.Start).ThenBy(p => p.Num).ToList();

        if (segments.Count == 0)
            segments = new List<SegmentVm> { new() { Text = raw, Style = "normal" } };

        var byAnchor = placements.GroupBy(x => Math.Clamp(x.Start, 0, raw.Length))
            .ToDictionary(g => g.Key, g => g.ToList());
        var emittedButtons = new HashSet<(int Anchor, int Num)>();
        var cursor = 0;
        foreach (var seg in segments)
        {
            var segStart = cursor;
            var segEnd = Math.Min(raw.Length, segStart + seg.Text.Length);
            var local = segStart;
            var anchors = byAnchor.Keys.Where(a => a >= segStart && a <= segEnd).OrderBy(a => a).ToList();
            foreach (var anchor in anchors)
            {
                if (anchor > local)
                    AddStyledPiece(tb, raw.Substring(local, anchor - local), seg.Style, local, placements, bold);
                foreach (var placement in byAnchor[anchor])
                    if (emittedButtons.Add((anchor, placement.Num)))
                        tb.Inlines!.Add(new InlineUIContainer(BuildMarkerBadge(placement)) { BaselineAlignment = BaselineAlignment.Baseline });
                local = anchor;
            }
            if (segEnd > local)
                AddStyledPiece(tb, raw.Substring(local, segEnd - local), seg.Style, local, placements, bold);
            cursor = segEnd;
        }
        if (cursor < raw.Length)
            AddStyledPiece(tb, raw[cursor..], "normal", cursor, placements, bold);

        // Markers anchored exactly at the end of the string.
        if (byAnchor.TryGetValue(raw.Length, out var tail))
        {
            foreach (var placement in tail)
                if (emittedButtons.Add((raw.Length, placement.Num)))
                    tb.Inlines!.Add(new InlineUIContainer(BuildMarkerBadge(placement)) { BaselineAlignment = BaselineAlignment.Baseline });
        }
        return tb;
    }

    /// <summary>
    /// Render exact source characters while projecting a continuous logical-change highlighter.
    /// Zero-width counterpart anchors are badge-only and never shade a neighboring character.
    /// Whitespace inside an inserted/deleted phrase inherits the same underline/strike so a
    /// phrase such as "윤리경영 파트" reads as one continuous edit including the space.
    /// </summary>
    private void AddStyledPiece(TextBlock tb, string text, string style, int globalStart,
        List<MarkerPlacementVm> placements, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return;

        var globalEnd = globalStart + text.Length;
        var overlapping = placements
            .Where(p => p.End > p.Start && RangesOverlap(globalStart, globalEnd, p.Start, p.End))
            .ToArray();

        // Split only at actual marker boundaries.  Every visible character remains a Run,
        // including highlighted text and spaces, so highlighted and unhighlighted lines use
        // the exact same font metrics and line height.  V5.9 used embedded TextBlocks for
        // highlighted fragments, which changed the inline box height and produced uneven
        // baselines/line spacing.
        var cuts = new SortedSet<int> { globalStart, globalEnd };
        foreach (var marker in overlapping)
        {
            cuts.Add(Math.Clamp(marker.Start, globalStart, globalEnd));
            cuts.Add(Math.Clamp(marker.End, globalStart, globalEnd));
        }
        var points = cuts.ToArray();
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var start = points[i];
            var end = points[i + 1];
            if (end <= start) continue;

            var piece = text.Substring(start - globalStart, end - start);
            var relevant = overlapping
                .Where(p => RangesOverlap(start, end, p.Start, p.End))
                .GroupBy(p => p.Num)
                .Select(g => g.First())
                .ToArray();

            var effectiveStyle = style;
            if (relevant.Length > 0 && style == "normal")
            {
                // Apply underline/strikethrough to the whole logical phrase, including spaces.
                // This makes a phrase such as "윤리경영 파트" read as one continuous change.
                var primaryRole = relevant[0].Role;
                if (primaryRole == "insert") effectiveStyle = "insert";
                else if (primaryRole == "delete") effectiveStyle = "delete";
            }

            var structuralNumber = relevant.Any(p => p.StructuralNumber);
            var run = new Run
            {
                Text = piece,
                Foreground = StyleBrush(effectiveStyle),
                // Enumerator-only edits such as an inserted/changed "(8)" need to read as
                // structural changes, not as ordinary prose. Keep red/blue semantics but make
                // the exact changed number visually stronger.
                FontWeight = structuralNumber ? FontWeight.Bold : (bold ? FontWeight.SemiBold : FontWeight.Normal),
                TextDecorations = StyleDecorations(effectiveStyle),
                Background = relevant.Length > 0 ? MarkerFill(relevant[0].Num) : Brushes.Transparent,
                BaselineAlignment = BaselineAlignment.Baseline
            };
            tb.Inlines!.Add(run);

            foreach (var marker in relevant)
                RegisterRun(marker.Num, run);
        }
    }

    private Border BuildMarkerBadge(MarkerPlacementVm placement)
    {
        var label = new TextBlock
        {
            Text = $"[{placement.Num}]",
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 15
        };
        var badge = new Border
        {
            Child = label,
            Background = MarkerFill(placement.Num),
            BorderBrush = MarkerBorder(placement.Num),
            BorderThickness = new Thickness(1),
            MinHeight = 17,
            MaxHeight = 17,
            Padding = new Thickness(2, 0),
            Margin = new Thickness(0, 0, 1, 0),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center
        };
        RegisterVisual(placement.Num, badge, "badge", placement.Pair);
        if (!_docMarkerControls.TryGetValue((placement.DocIndex, placement.Num), out var docMarkers))
            _docMarkerControls[(placement.DocIndex, placement.Num)] = docMarkers = new List<Control>();
        docMarkers.Add(badge);
        badge.PointerEntered += (_, _) => HoverMarkers(new[] { placement.Num }, true);
        badge.PointerExited += (_, _) => HoverMarkers(new[] { placement.Num }, false);
        badge.PointerPressed += (_, e) =>
        {
            FlashMarkers(new[] { placement.Num });
            // Marker navigation is article-local, not screen-global.  V5.18.4~6
            // measured TopLevel coordinates while the change window itself could be much
            // taller than the visible application viewport.  That made the requested
            // movement collapse to the current offset for late markers such as [79].
            // The source and change pane already share one aligned article row, so the
            // stable coordinate is the marker's Y inside this row's content grid.
            var contentGrid = _contentGrid;
            if (contentGrid is null)
            {
                e.Handled = true;
                return;
            }

            var markerLocalY = badge.TranslatePoint(
                new Point(0, Math.Max(0, badge.Bounds.Height / 2)), contentGrid)?.Y
                ?? Math.Max(0, contentGrid.Bounds.Height / 2);
            _markerActivated?.Invoke(_row.Id, placement.Num, placement.DocIndex, markerLocalY);
            e.Handled = true;
        };
        return badge;
    }

    public void AlignMarkerAcrossDocuments(int sourceDocIndex, int num, double sourceLocalY)
    {
        if (_contentGrid is null) return;

        // The clicked document is the reference column.  Every other document that owns
        // the same logical marker is translated inside this aligned article row so its
        // matching marker sits on exactly the same row-local Y axis.  Columns without a
        // counterpart marker are intentionally left untouched.
        for (var docIndex = 0; docIndex < _docCount; docIndex++)
        {
            if (docIndex == sourceDocIndex) continue;
            if (!_docNavigationTransforms.TryGetValue(docIndex, out var transform)) continue;
            if (!_docMarkerControls.TryGetValue((docIndex, num), out var candidates)) continue;

            Control? target = null;
            double? targetLocalY = null;
            foreach (var candidate in candidates)
            {
                if (candidate.Bounds.Height <= 0) continue;
                var y = candidate.TranslatePoint(
                    new Point(0, Math.Max(0, candidate.Bounds.Height / 2)), _contentGrid)?.Y;
                if (y is null || double.IsNaN(y.Value) || double.IsInfinity(y.Value)) continue;
                target = candidate;
                targetLocalY = y.Value;
                break;
            }

            if (target is null || targetLocalY is null) continue;

            // targetLocalY already includes the column's current RenderTransform.  Apply
            // only the remaining delta, making repeated clicks stable rather than cumulative.
            var delta = sourceLocalY - targetLocalY.Value;
            if (Math.Abs(delta) < 0.5) continue;
            transform.Y += delta;
        }
    }

    private void RegisterVisual(int num, Control control, string role, string pair = "")
    {
        if (!_markerVisuals.TryGetValue(num, out var list))
            _markerVisuals[num] = list = new List<(Control, string, string)>();
        list.Add((control, role, pair));
    }

    private void RegisterRun(int num, Run run)
    {
        if (!_markerRuns.TryGetValue(num, out var list))
            _markerRuns[num] = list = new List<Run>();
        list.Add(run);
    }

    private void HoverMarkers(IEnumerable<int> nums, bool entered)
    {
        foreach (var num in nums.Distinct())
        {
            _hoverInside[num] = entered;
            if (entered)
            {
                SetMarkerHighlight(new[] { num }, true);
                continue;
            }
            DispatcherTimer.RunOnce(() =>
            {
                if (_hoverInside.TryGetValue(num, out var stillInside) && stillInside) return;
                SetMarkerHighlight(new[] { num }, false);
            }, TimeSpan.FromMilliseconds(55));
        }
    }

    private void SetMarkerHighlight(IEnumerable<int> nums, bool on)
    {
        foreach (var num in nums)
        {
            if (_markerVisuals.TryGetValue(num, out var controls))
            {
                foreach (var (control, role, _) in controls)
                {
                    if (control is not Border border) continue;
                    if (role == "badge")
                        border.BorderBrush = on ? Brushes.Black : MarkerBorder(num);
                }
            }

            // Runs keep native text metrics.  Hover/focus changes only their background alpha,
            // never font size, margin, or layout, so the document cannot jitter.
            if (_markerRuns.TryGetValue(num, out var runs))
                foreach (var run in runs)
                    run.Background = on ? MarkerFocusFill(num) : MarkerFill(num);
        }
    }

    private void FlashMarkers(IEnumerable<int> nums)
    {
        var n = nums.Distinct().ToArray();
        SetMarkerHighlight(n, true);
        DispatcherTimer.RunOnce(() => SetMarkerHighlight(n, false), TimeSpan.FromMilliseconds(750));
    }

    private static IBrush StyleBrush(string style) => style switch
    {
        "delete" => DeleteBrush,
        "insert" => InsertBrush,
        "both" => BothBrush,
        _ => Brushes.Black
    };

    private static TextDecorationCollection? StyleDecorations(string style) => style switch
    {
        "delete" => TextDecorations.Strikethrough,
        "insert" => TextDecorations.Underline,
        // Intermediate-only wording: added from A→B and removed again from B→C.
        // Underline is retained; the pair-colored chips disclose both revision edges.
        "both" => TextDecorations.Underline,
        _ => null
    };

    private static IBrush MarkerFill(int num) => MarkerFills[(Math.Max(1, num) - 1) % MarkerFills.Length];
    private static IBrush MarkerFocusFill(int num)
    {
        var baseBrush = MarkerFills[(Math.Max(1, num) - 1) % MarkerFills.Length] as SolidColorBrush;
        var c = baseBrush?.Color ?? Colors.Gold;
        return new SolidColorBrush(Color.FromArgb(0x88, c.R, c.G, c.B));
    }
    private static IBrush MarkerBorder(int num) => MarkerBorders[(Math.Max(1, num) - 1) % MarkerBorders.Length];

    private static IBrush RoleBrush(string role) => role switch
    {
        "delete" => DeleteBrush,
        "insert" => InsertBrush,
        _ => ChangeBrush
    };

    private static bool RangesOverlap(int a1, int a2, int b1, int b2) => a1 < b2 && b1 < a2;

    private static int MarkerAnchor(string text, int start, int end)
    {
        var p = Math.Clamp(start, 0, text.Length);
        var e = Math.Clamp(end, p, text.Length);
        // A paired counterpart endpoint is deliberately zero-width. Preserve its exact
        // mapped boundary (e.g. after "used") instead of snapping it into the following
        // word. Non-zero changed spans keep the reader-friendly word-start behaviour.
        if (e == p) return p;
        var changed = text[p..e].Trim();
        // Pure punctuation markers stay at their exact character position.
        if (changed.Length > 0 && changed.All(ch => !char.IsLetterOrDigit(ch) && !IsKorean(ch)))
            return p;

        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
        if (p >= text.Length) return text.Length;
        if (IsWordChar(text[p]))
            while (p > 0 && IsWordChar(text[p - 1])) p--;
        return p;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || IsKorean(c);
    private static bool IsKorean(char c) => c is >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F' or >= '\uAC00' and <= '\uD7AF';

}
