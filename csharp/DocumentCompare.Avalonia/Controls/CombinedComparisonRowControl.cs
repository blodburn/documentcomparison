using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using DocumentCompare.Avalonia.Localization;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Controls;

/// <summary>
/// Renders one aligned comparison row as a single visual tree: document columns on the left
/// and that row's change review on the right.  Keeping both halves in one ItemsControl removes
/// the old duplicate repeater + global O(row-count) height scan and lets each row settle its
/// own height independently.
/// </summary>
public sealed class CombinedComparisonRowControl : UserControl
{
    private readonly ComparisonRowControl _documents;
    private readonly ChangeRowControl _changes;
    private bool _syncQueued;
    private bool _syncRunning;
    private int _syncRetries;

    public CombinedComparisonRowControl(
        ComparisonRowVm row,
        int slotCount,
        int activeDocumentCount,
        UiLanguage language = UiLanguage.Korean)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _documents = new ComparisonRowControl(
            row,
            slotCount,
            OnMarkerActivated,
            language,
            activeDocumentCount);
        _changes = new ChangeRowControl(row, language);

        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(5, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(2, GridUnitType.Star))
            }
        };

        Grid.SetColumn(_documents, 0);
        Grid.SetColumn(_changes, 1);
        root.Children.Add(_documents);
        root.Children.Add(_changes);
        Content = root;

        // Height synchronization is deliberately row-local.  A resize of row 42 no longer
        // causes MainWindow to scan/re-measure every other row in the document.
        _documents.SizeChanged += (_, _) => QueueLocalHeightSync();
        _changes.SizeChanged += (_, _) => QueueLocalHeightSync();
        AttachedToVisualTree += (_, _) => QueueLocalHeightSync();
    }

    private void OnMarkerActivated(int rowId, int num, int sourceDocIndex, double markerLocalY)
    {
        _documents.AlignMarkerAcrossDocuments(sourceDocIndex, num, markerLocalY);
        _changes.NavigateTo(num, markerLocalY);
    }

    private void QueueLocalHeightSync()
    {
        if (_syncQueued || _syncRunning) return;
        _syncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncQueued = false;
            SyncLocalHeights();
        }, DispatcherPriority.Background);
    }

    private void SyncLocalHeights()
    {
        if (_syncRunning) return;
        _syncRunning = true;
        try
        {
            var sectionHeight = _documents.SectionHeight;
            var documentHeight = _documents.ContentHeight;
            var changeHeight = _changes.NaturalContentHeight;

            if (documentHeight <= 0 || changeHeight <= 0)
            {
                if (_syncRetries++ < 8)
                    DispatcherTimer.RunOnce(QueueLocalHeightSync, TimeSpan.FromMilliseconds(18));
                return;
            }

            var alignedHeight = Math.Max(documentHeight, changeHeight);
            _documents.SetAlignedContentHeight(alignedHeight);
            _changes.SetAlignedLayout(sectionHeight, alignedHeight);
            _syncRetries = 0;
        }
        finally
        {
            _syncRunning = false;
        }
    }
}
