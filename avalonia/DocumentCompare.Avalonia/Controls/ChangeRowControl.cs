using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DocumentCompare.Avalonia.Models;
using DocumentCompare.Avalonia.Localization;

namespace DocumentCompare.Avalonia.Controls;

/// <summary>
/// One independent change-review viewport for one aligned article/paragraph row.
/// The outer row moves with A/B/C because MainWindow places both repeaters inside the
/// same master ScrollViewer. Only this row's inner ScrollViewer moves when a marker
/// inside the corresponding article is activated.
/// </summary>
public sealed class ChangeRowControl : UserControl
{
    private readonly ComparisonRowVm _row;
    private readonly UiLanguage _language;
    private readonly Dictionary<int, Border> _items = new();
    private readonly Border _sectionSpacer = new();
    private readonly Border _contentHost = new();
    private readonly ScrollViewer _scroll = new();
    private readonly StackPanel _panel = new();
    private readonly Border _topSpacer = new();
    private readonly Border _bottomSpacer = new();
    private readonly TranslateTransform _navigationTransform = new();
    private readonly List<Control> _messageControls = new();
    private Control? _firstNumbered;
    private bool _resetToFirst = true;
    private bool _runwayQueued;
    private int _navigationGeneration;
    private (int Num, double? PreferredLocalY)? _pendingNavigation;

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

    public ChangeRowControl(ComparisonRowVm row, UiLanguage language = UiLanguage.Korean)
    {
        _row = row;
        _language = language;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = Build();
        SizeChanged += (_, _) => QueueRunwayUpdate();
        _scroll.SizeChanged += (_, _) => QueueRunwayUpdate();
    }

    /// <summary>
    /// Natural, un-clipped height of the actual change messages for this row.
    /// Top/bottom scroll runway spacers are intentionally excluded.
    /// </summary>
    public double NaturalContentHeight
    {
        get
        {
            if (_messageControls.Count == 0) return 1;
            double total = 0;
            var visible = 0;
            foreach (var control in _messageControls)
            {
                if (!control.IsVisible) continue;
                var height = control.Bounds.Height;
                if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
                    height = control.DesiredSize.Height;
                if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
                    continue;
                total += height;
                visible++;
            }
            if (visible == 0) return 0;
            if (visible > 1)
                total += _panel.Spacing * (visible - 1);
            return Math.Max(1, total + 4);
        }
    }

    /// <summary>
    /// Match this change window to the shared A/B/C/change row geometry.
    /// MainWindow chooses the maximum natural content height across the document side
    /// and this change list, so no change item is vertically clipped at row boundaries.
    /// </summary>
    public void SetAlignedLayout(double sectionHeight, double articleHeight)
    {
        var section = Math.Max(0, sectionHeight);
        var article = Math.Max(1, articleHeight);
        if (double.IsNaN(_sectionSpacer.Height) || Math.Abs(_sectionSpacer.Height - section) > 0.5)
            _sectionSpacer.Height = section;
        _sectionSpacer.IsVisible = section > 0.5;
        if (double.IsNaN(_contentHost.Height) || Math.Abs(_contentHost.Height - article) > 0.5)
            _contentHost.Height = article;
        QueueRunwayUpdate();
    }

    public void NavigateTo(int num, double? preferredLocalY = null)
    {
        if (!_items.ContainsKey(num)) return;

        // A marker click always wins over the one-time initial [1] positioning.
        // Otherwise a late layout/runway pass can silently snap the change pane back
        // to the first item after the user clicks a marker such as [79].
        _resetToFirst = false;
        _pendingNavigation = (num, preferredLocalY);
        var generation = ++_navigationGeneration;

        SetHighlight(num, true);
        QueueRunwayUpdate();
        Dispatcher.UIThread.Post(() => TryNavigatePending(generation), DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() => TryNavigatePending(generation), TimeSpan.FromMilliseconds(45));
        DispatcherTimer.RunOnce(() => SetHighlight(num, false), TimeSpan.FromMilliseconds(900));
    }

    private void TryNavigatePending(int generation)
    {
        if (generation != _navigationGeneration || _pendingNavigation is not { } pending) return;
        if (!ScrollMarkerIntoView(pending.Num, pending.PreferredLocalY))
        {
            DispatcherTimer.RunOnce(() => TryNavigatePending(generation), TimeSpan.FromMilliseconds(35));
            return;
        }

        _pendingNavigation = null;
    }

    private Control Build()
    {
        var root = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        _sectionSpacer.Background = new SolidColorBrush(Color.Parse("#EAF1F7"));
        _sectionSpacer.BorderBrush = new SolidColorBrush(Color.Parse("#9AA4B2"));
        _sectionSpacer.BorderThickness = new Thickness(0, 0, 0, 1);
        _sectionSpacer.HorizontalAlignment = HorizontalAlignment.Stretch;
        root.Children.Add(_sectionSpacer);

        _contentHost.BorderBrush = new SolidColorBrush(Color.Parse("#9AA4B2"));
        _contentHost.BorderThickness = new Thickness(0, 0, 0, 1);
        _contentHost.Background = Brushes.White;
        _contentHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _contentHost.ClipToBounds = true;

        var messages = _row.DisplayMessages
            .Where(message => !string.Equals(message, "변경 없음", StringComparison.Ordinal))
            .ToList();

        _panel.Spacing = 2;
        _panel.Margin = new Thickness(6, 0, 6, 0);
        _panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _panel.RenderTransform = _navigationTransform;

        if (messages.Count == 0)
        {
            _resetToFirst = false;
            var empty = new TextBlock
            {
                Text = UiLocalization.T(_language, "변경 없음", "No changes"),
                Foreground = Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(4, 7),
                TextWrapping = TextWrapping.Wrap
            };
            _messageControls.Add(empty);
            _panel.Children.Add(empty);
        }
        else
        {
            _panel.Children.Add(_topSpacer);
            foreach (var message in messages)
            {
                var num = ExtractLeadingMarker(message);
                var item = BuildChangeItem(message, num);
                _messageControls.Add(item);
                _panel.Children.Add(item);
                if (num > 0)
                {
                    _items[num] = item;
                    _firstNumbered ??= item;
                }
            }
            _panel.Children.Add(_bottomSpacer);
        }

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _scroll.Content = _panel;
        _contentHost.Child = _scroll;
        root.Children.Add(_contentHost);
        return root;
    }

    private Border BuildChangeItem(string message, int num)
    {
        var originalMessage = message;
        message = UiLocalization.LocalizeChangeMessage(message, _language);
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var structuralNumber = IsStructuralNumberMessage(originalMessage);
        if (structuralNumber)
            text.FontWeight = FontWeight.SemiBold;
        if (num > 0)
        {
            var prefix = $"[{num}]";
            text.Inlines!.Add(new Run
            {
                Text = prefix,
                Foreground = MarkerBorder(num),
                FontWeight = FontWeight.Bold
            });
            text.Inlines!.Add(new Run
            {
                Text = message.Length > prefix.Length ? message[prefix.Length..] : "",
                FontWeight = structuralNumber ? FontWeight.Bold : FontWeight.Normal
            });
        }
        else
        {
            text.Text = message;
        }

        var border = new Border
        {
            Background = num > 0 ? MarkerFill(num) : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 3),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = text
        };
        if (num > 0)
        {
            border.PointerEntered += (_, _) => SetHighlight(num, true);
            border.PointerExited += (_, _) => SetHighlight(num, false);
            border.PointerPressed += (_, e) =>
            {
                NavigateTo(num, null);
                e.Handled = true;
            };
        }
        return border;
    }

    private void SetHighlight(int num, bool on)
    {
        if (_items.TryGetValue(num, out var border))
            border.BorderBrush = on ? Brushes.Black : Brushes.Transparent;
    }

    private bool ScrollMarkerIntoView(int num, double? preferredLocalY)
    {
        if (!_items.TryGetValue(num, out var target)) return true;

        var viewport = _contentHost.Bounds.Height;
        if (double.IsNaN(viewport) || double.IsInfinity(viewport) || viewport <= 1 ||
            target.Bounds.Height <= 0)
        {
            QueueRunwayUpdate();
            return false;
        }

        // IMPORTANT: after V5.18.3 the article row height is max(A,B,C,changes).
        // That means the inner ScrollViewer viewport can be as tall as the *entire* change
        // list even though the outer document ScrollViewer is only showing a slice of that
        // article.  In that geometry ScrollViewer.Offset is the wrong primitive for marker
        // navigation: the target can be "inside the viewport" while still being off-screen
        // to the user, so V5.18.4~7 could calculate/apply an offset with no visible result.
        //
        // Marker navigation now moves the rendered change stack itself inside the clipped
        // article cell.  The target's layout Y is stable and independent of the outer scroll.
        // RenderTransform changes what the user actually sees, even when the inner viewport
        // already contains every change item.
        var targetTop = target.TranslatePoint(new Point(0, 0), _panel)?.Y;
        if (targetTop is null) return false;

        if (!EnsureRunway(viewport))
            return false;

        var targetCenterInPanel = targetTop.Value + Math.Max(1, target.Bounds.Height) / 2;
        var currentScrollOffset = _scroll.Offset.Y;
        double anchorY;

        if (preferredLocalY is double requestedLocalY)
        {
            // Put the matching change item on the same article-local eye-line as the
            // clicked A/B/C marker.  The article row may be partially hidden by the outer
            // ScrollViewer; local coordinates remain valid because both columns share the
            // same aligned row boundary.
            anchorY = Math.Clamp(requestedLocalY, 0, viewport);
        }
        else
        {
            // Direct click inside the change pane: keep some context above the item.
            anchorY = viewport * 0.35;
        }

        // VisibleY = panelY + renderShift - innerScrollOffset.
        // Solve directly for renderShift so the target is visibly moved even when
        // ScrollViewer.Offset itself has no useful range after max-row-height alignment.
        var desiredShift = anchorY - (targetCenterInPanel - currentScrollOffset);

        // Top/bottom runway is one viewport each, therefore a +/- viewport translation is
        // enough to align any visible article-local eye-line without exposing another row.
        desiredShift = Math.Clamp(desiredShift, -viewport, viewport);
        _navigationTransform.Y = desiredShift;

        // A render transform is synchronous from the user's point of view.  A follow-up
        // layout pass may resize the row, but SetAlignedLayout/UpdateRunway never resets the
        // navigation transform, so the click cannot be silently undone.
        return true;
    }

    private bool EnsureRunway(double viewport)
    {
        if (double.IsNaN(viewport) || double.IsInfinity(viewport) || viewport <= 1)
            return false;

        var resized = false;
        if (double.IsNaN(_topSpacer.Height) || Math.Abs(_topSpacer.Height - viewport) > 0.5)
        {
            _topSpacer.Height = viewport;
            resized = true;
        }
        if (double.IsNaN(_bottomSpacer.Height) || Math.Abs(_bottomSpacer.Height - viewport) > 0.5)
        {
            _bottomSpacer.Height = viewport;
            resized = true;
        }
        if (resized)
        {
            _panel.InvalidateMeasure();
            _scroll.InvalidateMeasure();
            QueueRunwayUpdate();
            return false;
        }
        return true;
    }

    private void QueueRunwayUpdate()
    {
        if (_runwayQueued || !_resetToFirst && _firstNumbered is null) return;
        _runwayQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _runwayQueued = false;
            UpdateRunway();
        }, DispatcherPriority.Background);
    }

    private void UpdateRunway()
    {
        if (_firstNumbered is null) return;
        var viewport = _scroll.Viewport.Height;
        if (double.IsNaN(viewport) || double.IsInfinity(viewport) || viewport <= 1)
        {
            QueueRunwayUpdate();
            return;
        }

        if (!EnsureRunway(viewport))
            return;

        if (_pendingNavigation is { })
        {
            var generation = _navigationGeneration;
            Dispatcher.UIThread.Post(() => TryNavigatePending(generation), DispatcherPriority.Background);
            return;
        }

        if (!_resetToFirst) return;
        if (_firstNumbered.Bounds.Height <= 0)
        {
            QueueRunwayUpdate();
            return;
        }
        var firstTop = _firstNumbered.TranslatePoint(new Point(0, 0), _panel)?.Y;
        if (firstTop is null)
        {
            QueueRunwayUpdate();
            return;
        }
        var maxY = Math.Max(0, _scroll.Extent.Height - viewport);
        _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(firstTop.Value, 0, maxY));
        _resetToFirst = false;
    }

    private static bool IsStructuralNumberMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains("항/호 번호", StringComparison.Ordinal) ||
            text.Contains("항/호 구조", StringComparison.Ordinal) ||
            text.Contains("번호/위치 변경", StringComparison.Ordinal))
            return true;

        // Marker messages such as: [74] A↔B · [2.] 추가: “(8)”
        // Treat only explicit enumerator-shaped payloads as structural, so ordinary numeric
        // values (prices, dates, percentages) do not become bold accidentally.
        return Regex.IsMatch(text,
            "(?:추가|삭제|변경):\\s*[“\"](?:\\(\\d+\\)|\\d+[.)]|[①-⑳]|[가-하A-Za-z][.)])[”\"](?:\\s*→\\s*[“\"](?:\\(\\d+\\)|\\d+[.)]|[①-⑳]|[가-하A-Za-z][.)])[”\"])?\\s*$");
    }

    private static int ExtractLeadingMarker(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var c = text[0];
        if (c is >= '\u2460' and <= '\u2473') return c - '\u2460' + 1;
        var m = Regex.Match(text, @"^\[(\d+)\]");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    private static IBrush MarkerFill(int num) => MarkerFills[(Math.Max(1, num) - 1) % MarkerFills.Length];
    private static IBrush MarkerBorder(int num) => MarkerBorders[(Math.Max(1, num) - 1) % MarkerBorders.Length];
}
