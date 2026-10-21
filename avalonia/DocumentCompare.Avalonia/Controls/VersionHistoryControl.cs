using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using DocumentCompare.Avalonia.Engine;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Controls;

public sealed class VersionHistoryControl : UserControl
{
    private readonly IComparisonEngine _engine = new NativeComparisonEngine();
    private VersionHistoryProjectVm _project = new();
    private VersionSnapshotStore _snapshotStore = VersionSnapshotStore.CreateTemporary();
    private int _beforeIndex = -1;
    private int _afterIndex = -1;
    private int _activeIndex = -1;
    private string? _projectPath;
    private CancellationTokenSource? _loadCts;

    private readonly StackPanel _tree = new() { Spacing = 4 };
    private readonly StackPanel _preview = new() { Spacing = 0 };
    private readonly StackPanel _changes = new() { Spacing = 8 };
    private readonly ScrollViewer _previewScroll = new();
    private readonly ScrollViewer _changesScroll = new();
    private readonly Dictionary<int, Control> _changeTargets = new();
    private readonly Dictionary<int, Control> _previewTargets = new();
    private readonly TextBlock _projectTitle = new() { Text = "문서 버전", FontSize = 17, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _previewTitle = new() { Text = "버전을 선택하세요", FontSize = 17, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _previewSubtitle = new() { Text = "왼쪽 버전 트리에서 문서를 선택하면 해당 버전이 표시됩니다.", Foreground = Brushes.Gray, FontSize = 12 };
    private readonly TextBlock _changeTitle = new() { Text = "변경사항", FontSize = 17, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _changeSummary = new() { Text = "선택된 버전이 없습니다.", Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Text = "버전 파일을 추가하세요.", Foreground = new SolidColorBrush(Color.Parse("#64748B")), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Border _dropHint = new()
    {
        Background = new SolidColorBrush(Color.Parse("#F6F9FC")),
        BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8),
        Margin = new Thickness(12, 0, 12, 8),
        Child = new TextBlock
        {
            Text = "DOCX/TXT/코드 파일을 이 영역에 드롭하여 버전 추가",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#64748B")),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        }
    };
    private readonly Button _moveUp = Secondary("위로");
    private readonly Button _moveDown = Secondary("아래로");
    private readonly Button _delete = Secondary("삭제");

    public VersionHistoryControl()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Content = Build();
        _moveUp.Click += (_, _) => MoveSelected(-1);
        _moveDown.Click += (_, _) => MoveSelected(1);
        _delete.Click += (_, _) => DeleteSelected();
        RefreshTree();
        DetachedFromVisualTree += (_, _) =>
        {
            _loadCts?.Cancel();
            _ = _engine.DisposeAsync();
        };
    }

    private Control Build()
    {
        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,6,*,6,340"),
            Margin = new Thickness(4, 10, 4, 4)
        };

        var left = Card();
        DragDrop.SetAllowDrop(left, true);
        DragDrop.AddDragOverHandler(left, OnVersionDragOver);
        DragDrop.AddDropHandler(left, OnVersionDrop);
        var leftRoot = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        leftRoot.Children.Add(new Border
        {
            Padding = new Thickness(14, 12, 14, 8),
            Child = _projectTitle
        });
        var actions = new WrapPanel { Margin = new Thickness(12, 0, 12, 10), ItemHeight = 34 };
        var add = Primary("+ 버전 추가"); add.Click += AddVersions_Click;
        var open = Secondary("열기"); open.Click += OpenProject_Click;
        var save = Secondary("저장"); save.Click += SaveProject_Click;
        actions.Children.Add(add); actions.Children.Add(open); actions.Children.Add(save);
        Grid.SetRow(actions, 1); leftRoot.Children.Add(actions);
        Grid.SetRow(_dropHint, 2); leftRoot.Children.Add(_dropHint);

        var treeScroll = new ScrollViewer
        {
            Content = _tree,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(10, 0, 8, 6)
        };
        Grid.SetRow(treeScroll, 3); leftRoot.Children.Add(treeScroll);

        var reorder = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 4, 12, 8) };
        reorder.Children.Add(_moveUp); reorder.Children.Add(_moveDown); reorder.Children.Add(_delete);
        Grid.SetRow(reorder, 4); leftRoot.Children.Add(reorder);
        var statusBorder = new Border { Background = new SolidColorBrush(Color.Parse("#F7F9FC")), Padding = new Thickness(12, 9), Child = _status };
        Grid.SetRow(statusBorder, 5); leftRoot.Children.Add(statusBorder);
        left.Child = leftRoot;
        Grid.SetColumn(left, 0); root.Children.Add(left);

        var splitter1 = new GridSplitter { Width = 8, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter1, 1); root.Children.Add(splitter1);

        var center = Card();
        var centerRoot = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var centerHeader = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EEF3F8")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D6E0EA")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(15, 11),
            Child = new StackPanel { Spacing = 3, Children = { _previewTitle, _previewSubtitle } }
        };
        centerRoot.Children.Add(centerHeader);
        _previewScroll.Content = _preview;
        _previewScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _previewScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Grid.SetRow(_previewScroll, 1); centerRoot.Children.Add(_previewScroll);
        center.Child = centerRoot;
        Grid.SetColumn(center, 2); root.Children.Add(center);

        var splitter2 = new GridSplitter { Width = 8, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter2, 3); root.Children.Add(splitter2);

        var right = Card();
        var rightRoot = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        rightRoot.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F3F6FA")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D6E0EA")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 11),
            Child = new StackPanel { Spacing = 4, Children = { _changeTitle, _changeSummary } }
        });
        _changesScroll.Content = _changes;
        _changesScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _changesScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _changesScroll.Margin = new Thickness(10);
        Grid.SetRow(_changesScroll, 1); rightRoot.Children.Add(_changesScroll);
        right.Child = rightRoot;
        Grid.SetColumn(right, 4); root.Children.Add(right);
        return root;
    }

    private async void AddVersions_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "버전 문서 추가",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("문서 및 코드") { Patterns = new[] { "*.docx", "*.txt", "*.rs", "*.cs", "*.py", "*.js", "*.jsx", "*.ts", "*.tsx", "*.java", "*.c", "*.h", "*.cc", "*.cpp", "*.hpp", "*.go", "*.rb", "*.php", "*.swift", "*.kt", "*.sql", "*.sh", "*.ps1", "*.json", "*.yaml", "*.yml", "*.toml", "*.xml", "*.html", "*.css", "*.md" } }
            }
        });
        var paths = files.Select(x => x.TryGetLocalPath())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>();
        try
        {
            var result = VersionFileImportPolicy.AddPaths(_project, paths, _snapshotStore);
            if (result.Added == 0)
            {
                _status.Text = ImportStatus(result, "추가된 버전이 없습니다.");
                return;
            }
            RefreshTree();
            _status.Text = ImportStatus(result, $"{result.Added}개 버전을 추가했습니다.");
            await ShowSelectedAsync();
        }
        catch (Exception ex)
        {
            RefreshTree();
            _status.Text = "버전 파일 보관 실패: " + ex.Message;
        }
    }

    private void OnVersionDragOver(object? sender, DragEventArgs e)
    {
        var hasSupported = e.DataTransfer.TryGetFiles()?
            .Select(x => x.TryGetLocalPath())
            .Any(x => !string.IsNullOrWhiteSpace(x) && VersionFileImportPolicy.IsSupported(x)) == true;
        e.DragEffects = hasSupported ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnVersionDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?
            .Select(x => x.TryGetLocalPath())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray() ?? Array.Empty<string>();
        try
        {
            var result = VersionFileImportPolicy.AddPaths(_project, paths, _snapshotStore);
            if (result.Added > 0)
            {
                RefreshTree();
                await ShowSelectedAsync();
            }
            _status.Text = ImportStatus(result, result.Added > 0 ? $"드롭으로 {result.Added}개 버전을 추가했습니다." : "추가된 버전이 없습니다.");
        }
        catch (Exception ex)
        {
            RefreshTree();
            _status.Text = "드롭 파일 보관 실패: " + ex.Message;
        }
    }

    private static string ImportStatus(VersionFileImportResult result, string prefix)
    {
        var details = new List<string>();
        if (result.Duplicates > 0) details.Add($"중복 {result.Duplicates}");
        if (result.Unsupported > 0) details.Add($"미지원 {result.Unsupported}");
        if (result.Missing > 0) details.Add($"찾을 수 없음 {result.Missing}");
        return details.Count == 0 ? prefix + " 순서는 위/아래 버튼으로 조정할 수 있습니다." : prefix + " " + string.Join(" · ", details);
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "버전 프로젝트 열기",
            FileTypeFilter = new[] { new FilePickerFileType("DocumentCompare Version Project") { Patterns = new[] { "*.dcv.json", "*.json" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<VersionHistoryProjectVm>(await File.ReadAllTextAsync(path));
            if (loaded is null) throw new InvalidDataException("프로젝트 내용을 읽을 수 없습니다.");
            var snapshotStore = VersionSnapshotStore.ForProject(path);
            foreach (var version in loaded.Versions.Where(v => !string.IsNullOrWhiteSpace(v.SnapshotBlobId)))
                snapshotStore.Resolve(version); // Reject broken sidecars instead of showing a false history.
            var oldStore = _snapshotStore;
            _project = loaded;
            _snapshotStore = snapshotStore;
            try { oldStore.CleanupTemporary(); } catch { }
            _projectPath = path;
            _beforeIndex = -1;
            _afterIndex = -1;
            _activeIndex = -1;
            _projectTitle.Text = string.IsNullOrWhiteSpace(_project.Name) ? "문서 버전" : _project.Name;
            RefreshTree();
            _status.Text = $"프로젝트를 열었습니다: {Path.GetFileName(path)}";
            await ShowSelectedAsync();
        }
        catch (Exception ex)
        {
            _status.Text = "프로젝트 열기 실패: " + ex.Message;
        }
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;
        var path = _projectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "버전 프로젝트 저장",
                SuggestedFileName = "document-versions.dcv.json",
                FileTypeChoices = new[] { new FilePickerFileType("DocumentCompare Version Project") { Patterns = new[] { "*.dcv.json" } } }
            });
            path = file?.TryGetLocalPath();
        }
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var oldStore = _snapshotStore;
            var saved = await VersionHistoryProjectStore.SaveAsync(_project, oldStore, path);
            _project = saved;
            _snapshotStore = VersionSnapshotStore.ForProject(path);
            try { oldStore.CleanupTemporary(); } catch { }
            _projectPath = path;
            var unavailable = saved.Versions.Count(v => string.IsNullOrWhiteSpace(v.SnapshotBlobId));
            _status.Text = unavailable > 0
                ? $"프로젝트 저장 완료 · 원본을 찾을 수 없는 이전 형식 버전 {unavailable}개는 보존하지 못했습니다."
                : $"버전 원본과 프로젝트를 저장했습니다: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _status.Text = "프로젝트 저장 실패: " + ex.Message;
        }
    }

    private void RefreshTree()
    {
        _tree.Children.Clear();
        if (_project.Versions.Count == 0)
        {
            _tree.Children.Add(new TextBlock
            {
                Text = "버전 파일이 없습니다.\n‘+ 버전 추가’로 파일을 순서대로 추가하세요.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 14)
            });
        }

        for (var i = 0; i < _project.Versions.Count; i++)
        {
            if (i > 0)
                _tree.Children.Add(new Border { Width = 2, Height = 8, Background = new SolidColorBrush(Color.Parse("#CBD5E1")), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 0, 0, 0) });
            var index = i;
            var isBefore = i == _beforeIndex;
            var isAfter = i == _afterIndex;
            var tag = isAfter ? "후" : isBefore ? "전" : "";
            var bg = isAfter ? "#E8F1FF" : isBefore ? "#F1F3F5" : "#FFFFFF";
            var border = isAfter ? "#4C86D9" : isBefore ? "#AEB8C4" : "#DDE4EC";
            var btn = new Button
            {
                Background = new SolidColorBrush(Color.Parse(bg)),
                BorderBrush = new SolidColorBrush(Color.Parse(border)),
                BorderThickness = new Thickness(isBefore || isAfter ? 2 : 1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = VersionNodeContent(_project.Versions[i], tag, isAfter)
            };
            btn.Click += async (_, _) =>
            {
                ToggleVersionSelection(index);
                RefreshTree();
                await ShowSelectedAsync();
            };
            _tree.Children.Add(btn);
        }

        _moveUp.IsEnabled = _activeIndex > 0;
        _moveDown.IsEnabled = _activeIndex >= 0 && _activeIndex < _project.Versions.Count - 1;
        _delete.IsEnabled = _activeIndex >= 0 && _activeIndex < _project.Versions.Count;
    }

    private void ToggleVersionSelection(int index)
    {
        _activeIndex = index;
        var next = VersionSelectionPolicy.Toggle(_beforeIndex, _afterIndex, index);
        _beforeIndex = next.Before;
        _afterIndex = next.After;
        if (!next.Changed)
            _status.Text = "전/후 문서가 모두 지정되어 있습니다. 바꿀 문서의 전 또는 후 항목을 먼저 눌러 해제하세요.";
    }

    private static Control VersionNodeContent(VersionFileVm version, string tag, bool isAfter)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(version.Path),
            FontSize = 11.5,
            FontWeight = tag.Length > 0 ? FontWeight.SemiBold : FontWeight.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.Parse(isAfter ? "#2563EB" : "#E2E8F0")),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = tag, FontSize = 10, Foreground = isAfter ? Brushes.White : new SolidColorBrush(Color.Parse("#475569")), FontWeight = FontWeight.SemiBold }
            };
            Grid.SetColumn(badge, 1); grid.Children.Add(badge);
        }
        return grid;
    }

    private async Task ShowSelectedAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        _preview.Children.Clear();
        _previewScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _changes.Children.Clear();
        _previewTargets.Clear();
        _changeTargets.Clear();

        var displayIndex = _afterIndex >= 0 ? _afterIndex : _beforeIndex;
        if (displayIndex < 0 || displayIndex >= _project.Versions.Count)
        {
            _previewTitle.Text = "버전을 선택하세요";
            _previewSubtitle.Text = "첫 번째 클릭은 전, 두 번째 다른 문서 클릭은 후로 지정됩니다.";
            _changeSummary.Text = "전/후 문서를 지정하세요.";
            _changes.Children.Add(Message("전/후 지정은 독립적으로 해제할 수 있습니다. 지정된 문서를 다시 누르면 해당 역할이 해제됩니다.", "#64748B"));
            return;
        }

        var display = _project.Versions[displayIndex];
        _previewTitle.Text = Path.GetFileName(display.Path);
        try
        {
            var displayPath = _snapshotStore.Resolve(display);
            if (!File.Exists(displayPath))
                throw new FileNotFoundException("버전 파일을 찾을 수 없습니다. 이전 형식으로 저장된 프로젝트라면 원본 파일이 필요합니다.");
            if (VersionFileImportPolicy.IsCodeFile(display.Path))
            {
                await ShowCodeVersionsAsync(display, displayPath, token);
                return;
            }
            if (_beforeIndex >= 0 && _afterIndex >= 0 &&
                (VersionFileImportPolicy.IsCodeFile(_project.Versions[_beforeIndex].Path) ||
                 VersionFileImportPolicy.IsCodeFile(_project.Versions[_afterIndex].Path)))
                throw new InvalidDataException("코드 파일과 일반 문서는 비교할 수 없습니다. 같은 유형의 버전을 선택하세요.");
            if (_beforeIndex < 0 || _afterIndex < 0)
            {
                var role = _afterIndex >= 0 ? "후" : "전";
                var waiting = _afterIndex >= 0 ? "전" : "후";
                _previewSubtitle.Text = $"{role} 문서 지정됨 · {waiting} 문서를 선택하면 비교를 시작합니다.";
                _changeSummary.Text = $"{waiting} 문서 선택 대기";
                var standaloneStyleMap = VersionDocumentStyleMap.Load(displayPath);
                var standaloneTableMap = VersionDocxTableMap.Load(displayPath);
                var self = await _engine.CompareAsync(new[] { displayPath, displayPath }, 1, "auto", false, true, token);
                foreach (var row in self.Rows)
                    _preview.Children.Add(new VersionPreviewRowControl(row, 1, standaloneStyleMap, null, standaloneTableMap));
                _changes.Children.Add(Message($"현재 {role} 문서만 지정되어 있습니다. 트리에서 다른 문서를 선택하면 {waiting} 문서로 지정됩니다.", "#64748B"));
                return;
            }

            var previous = _project.Versions[_beforeIndex];
            var current = _project.Versions[_afterIndex];
            var previousPath = _snapshotStore.Resolve(previous);
            var currentPath = _snapshotStore.Resolve(current);
            if (!File.Exists(previousPath) || !File.Exists(currentPath))
                throw new FileNotFoundException("전/후 버전 파일을 찾을 수 없습니다.");

            _previewTitle.Text = Path.GetFileName(current.Path);
            _previewSubtitle.Text = $"{Path.GetFileName(previous.Path)} [전]  →  {Path.GetFileName(current.Path)} [후] · 후 문서 표시 중";
            _changeSummary.Text = "비교 중...";
            var formattingItems = VersionFormattingInspector.Compare(previousPath, currentPath);
            var formatAnchors = formattingItems
                .Where(x => !string.IsNullOrWhiteSpace(x.AnchorText))
                .Select(x => VersionDocumentStyleMap.Normalize(x.AnchorText))
                .Where(x => x.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var styleMap = VersionDocumentStyleMap.Load(currentPath);
            var tableMap = VersionDocxTableMap.Load(currentPath);
            var result = await _engine.CompareAsync(new[] { previousPath, currentPath }, 1, "auto", false, true, token);
            var changeItems = VersionHistoryChangeSequencer.Sequence(result, formattingItems, 1);
            var contentNumbers = result.Rows.SelectMany(x => x.Markers).Select(x => x.Num).ToHashSet();
            var supplementalByRow = changeItems
                .Where(x => x.RowId.HasValue && x.MarkerNumber.HasValue && !contentNumbers.Contains(x.MarkerNumber.Value))
                .GroupBy(x => x.RowId!.Value)
                .ToDictionary(x => x.Key, x => (IReadOnlyList<int>)x.Select(v => v.MarkerNumber!.Value).Distinct().OrderBy(v => v).ToList());

            foreach (var row in result.Rows)
            {
                supplementalByRow.TryGetValue(row.Id, out var supplemental);
                var control = new VersionPreviewRowControl(row, 1, styleMap, formatAnchors, tableMap, supplemental, ScrollToChange);
                _preview.Children.Add(control);
                foreach (var num in row.Markers.Select(x => x.Num).Concat(supplemental ?? Array.Empty<int>()).Distinct())
                    _previewTargets[num] = control;
            }

            RenderChanges(changeItems);
            _status.Text = $"{Path.GetFileName(previous.Path)} → {Path.GetFileName(current.Path)} 비교 완료";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _changeSummary.Text = "버전 비교 실패";
            _changes.Children.Add(Message(ex.Message, "#C62828"));
        }
    }

    private async Task ShowCodeVersionsAsync(VersionFileVm display, string displayPath, CancellationToken token)
    {
        _previewScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        var afterText = await Task.Run(() => CodeVersionComparer.ReadCodeFile(displayPath), token);
        token.ThrowIfCancellationRequested();
        CodeVersionComparer.Result? comparison = null;
        var previousName = "";
        if (_beforeIndex >= 0 && _afterIndex >= 0)
        {
            var previous = _project.Versions[_beforeIndex];
            if (!VersionFileImportPolicy.IsCodeFile(previous.Path))
                throw new InvalidDataException("코드 파일과 문서 파일은 비교할 수 없습니다.");
            var previousPath = _snapshotStore.Resolve(previous);
            var beforeText = await Task.Run(() => CodeVersionComparer.ReadCodeFile(previousPath), token);
            comparison = await Task.Run(() => CodeVersionComparer.Compare(beforeText, afterText), token);
            previousName = Path.GetFileName(previous.Path);
        }
        token.ThrowIfCancellationRequested();
        var lines = comparison?.AfterLines ?? (afterText.Length == 0
            ? Array.Empty<string>() : afterText.Split('\n'));
        var changeItems = new List<VersionChangeVm>();
        var marks = new Dictionary<int, List<int>>();
        if (comparison is not null)
        {
            for (var i = 0; i < comparison.Changes.Count; i++)
            {
                var change = comparison.Changes[i];
                var num = i + 1;
                var line = Math.Min(change.AfterStart, Math.Max(0, lines.Length - 1));
                if (!marks.TryGetValue(line, out var nums)) marks[line] = nums = new List<int>();
                nums.Add(num);
                var oldRange = change.BeforeEnd == change.BeforeStart
                    ? $"{change.BeforeStart + 1}행 앞(삽입 위치)" : $"{change.BeforeStart + 1}~{change.BeforeEnd}행";
                var newRange = change.AfterEnd == change.AfterStart
                    ? $"{change.AfterStart + 1}행 앞(삭제 위치)" : $"{change.AfterStart + 1}~{change.AfterEnd}행";
                var oldExcerpt = CodeExcerpt(comparison.BeforeLines, change.BeforeStart, change.BeforeEnd);
                var newExcerpt = CodeExcerpt(comparison.AfterLines, change.AfterStart, change.AfterEnd);
                changeItems.Add(new VersionChangeVm
                {
                    Category = "코드", Title = change.Kind, MarkerNumber = num,
                    Detail = $"이전 {oldRange} → 이후 {newRange}\n전: {oldExcerpt}\n후: {newExcerpt}"
                });
            }
        }
        _previewTitle.Text = Path.GetFileName(display.Path);
        _previewSubtitle.Text = comparison is null
            ? "코드 파일을 표시 중입니다. 전/후 버전을 지정하면 변경사항을 비교합니다."
            : $"{previousName} [전] → {Path.GetFileName(display.Path)} [후] · 코드 줄 비교";
        const int displayLimit = 10000;
        var highlighted = new HashSet<int>();
        if (comparison is not null) foreach (var change in comparison.Changes)
            for (var rowNumber = change.AfterStart; rowNumber < Math.Min(change.AfterEnd, displayLimit); rowNumber++)
                highlighted.Add(rowNumber);
        if (lines.Length == 0)
        {
            var empty = new Border { Padding = new Thickness(8, 5),
                Child = new TextBlock { Text = "(빈 코드 파일)", Foreground = Brushes.Gray } };
            _preview.Children.Add(empty);
            if (marks.TryGetValue(0, out var emptyMarkers))
                foreach (var num in emptyMarkers) _previewTargets[num] = empty;
        }
        for (var i = 0; i < Math.Min(lines.Length, displayLimit); i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (marks.TryGetValue(i, out var nums))
                foreach (var number in nums)
                {
                    var n = number;
                    var badge = new Button { Content = $"[{n}]", FontSize = 10, Padding = new Thickness(4, 1),
                        Background = new SolidColorBrush(Color.Parse("#DBEAFE")),
                        BorderThickness = new Thickness(0) };
                    badge.Click += (_, _) => ScrollToChange(n);
                    row.Children.Add(badge);
                }
            row.Children.Add(new TextBlock { Text = $"{i + 1,5}  {lines[i]}",
                FontFamily = FontFamily.Parse("Consolas, Courier New, monospace"),
                FontSize = 12.5, TextWrapping = TextWrapping.NoWrap,
                Foreground = new SolidColorBrush(Color.Parse("#1E293B")) });
            var border = new Border { Padding = new Thickness(8, 3), Child = row,
                Background = highlighted.Contains(i) || marks.ContainsKey(i)
                    ? new SolidColorBrush(Color.Parse("#EFF6FF")) : Brushes.Transparent };
            _preview.Children.Add(border);
            if (nums is not null) foreach (var n in nums) _previewTargets[n] = border;
        }
        if (lines.Length > displayLimit)
            _preview.Children.Add(Message($"파일이 너무 길어 앞부분 {displayLimit:N0}행만 표시합니다.", "#B45309"));
        if (comparison is not null)
        {
            RenderChanges(changeItems);
            _status.Text = $"코드 비교 완료 · {comparison.Changes.Count}개 변경 구간";
        }
        else
        {
            _changeSummary.Text = "전/후 버전 선택 대기";
            _changes.Children.Add(Message("비교할 두 코드 버전을 선택하세요.", "#64748B"));
        }
    }

    private static string CodeExcerpt(string[] lines, int start, int end)
    {
        if (start == end) return "(없음)";
        var excerpt = string.Join(" ↵ ", lines.Skip(start).Take(Math.Min(end - start, 3)));
        return excerpt.Length <= 180 ? excerpt : excerpt[..180] + "…";
    }

    private void RenderChanges(List<VersionChangeVm> items)
    {
        _changes.Children.Clear();
        _changeTargets.Clear();
        if (items.Count == 0)
        {
            _changeSummary.Text = "변경사항 없음";
            _changes.Children.Add(Message("직전 버전과 내용/서식 차이가 없습니다.", "#64748B"));
            return;
        }
        var groups = items.GroupBy(x => x.Category).ToDictionary(x => x.Key, x => x.Count());
        _changeSummary.Text = $"전체 {items.Count}건 · " + string.Join(" · ", groups.Select(x => $"{x.Key} {x.Value}"));
        foreach (var item in items.OrderBy(x => x.MarkerNumber ?? int.MaxValue))
        {
            var color = item.Category switch
            {
                "문자서식" or "문단서식" or "스타일" => "#7C3AED",
                "표" => "#0F8B8D",
                "구조" => "#C26A18",
                _ => "#2563EB"
            };
            var body = new StackPanel { Spacing = 4 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            header.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(color)), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2),
                Child = new TextBlock { Text = item.Category, Foreground = Brushes.White, FontSize = 9.5, FontWeight = FontWeight.SemiBold }
            });
            var numberText = item.MarkerNumber is int num ? $"[{num}] " : "";
            header.Children.Add(new TextBlock
            {
                Text = numberText + item.Title,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(header);
            if (!string.IsNullOrWhiteSpace(item.Detail))
                body.Children.Add(BuildDetailText(item.Detail));

            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#DCE3EC")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 8), Child = body,
                Cursor = item.MarkerNumber.HasValue ? new Cursor(StandardCursorType.Hand) : Cursor.Default
            };
            if (item.MarkerNumber is int markerNumber)
            {
                _changeTargets[markerNumber] = card;
                card.PointerPressed += (_, e) =>
                {
                    ScrollToPreview(markerNumber);
                    e.Handled = true;
                };
            }
            _changes.Children.Add(card);
        }
    }

    private static TextBlock BuildDetailText(string detail)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#475569"))
        };
        var i = 0;
        while (i < detail.Length)
        {
            var hash = detail.IndexOf('#', i);
            if (hash < 0 || hash + 7 > detail.Length)
            {
                tb.Inlines!.Add(new Run(detail[i..]));
                break;
            }
            var code = detail.Substring(hash, 7);
            var valid = code.Skip(1).All(Uri.IsHexDigit);
            if (!valid)
            {
                tb.Inlines!.Add(new Run(detail[i..(hash + 1)]));
                i = hash + 1;
                continue;
            }
            if (hash > i) tb.Inlines!.Add(new Run(detail[i..hash]));
            tb.Inlines!.Add(new InlineUIContainer(new Border
            {
                Width = 11,
                Height = 11,
                Background = new SolidColorBrush(Color.Parse(code)),
                BorderBrush = new SolidColorBrush(Color.Parse("#94A3B8")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(2, 0, 3, 0)
            }));
            tb.Inlines!.Add(new Run(code.ToUpperInvariant()));
            i = hash + 7;
        }
        return tb;
    }

    private void ScrollToChange(int markerNumber)
    {
        if (!_changeTargets.TryGetValue(markerNumber, out var target)) return;
        ScrollControlIntoView(_changesScroll, _changes, target);
        FlashTarget(target);
    }

    private void ScrollToPreview(int markerNumber)
    {
        if (!_previewTargets.TryGetValue(markerNumber, out var target)) return;
        ScrollControlIntoView(_previewScroll, _preview, target);
        FlashTarget(target);
    }

    private static void ScrollControlIntoView(ScrollViewer scroll, Control contentRoot, Control target)
    {
        var point = target.TranslatePoint(new Point(0, 0), contentRoot);
        if (point is null) return;
        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var desired = Math.Clamp(point.Value.Y - 18, 0, maxY);
        scroll.Offset = new Vector(scroll.Offset.X, desired);
    }

    private static void FlashTarget(Control target)
    {
        if (target is not Border border) return;
        var original = border.BorderBrush;
        border.BorderBrush = new SolidColorBrush(Color.Parse("#2563EB"));
        border.BorderThickness = new Thickness(2);
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                border.BorderBrush = original;
                border.BorderThickness = new Thickness(1);
            });
        });
    }

    private void MoveSelected(int delta)
    {
        if (_activeIndex < 0 || _activeIndex >= _project.Versions.Count) return;
        var from = _activeIndex;
        var target = from + delta;
        if (target < 0 || target >= _project.Versions.Count) return;
        (_project.Versions[from], _project.Versions[target]) = (_project.Versions[target], _project.Versions[from]);
        _beforeIndex = SwapRoleIndex(_beforeIndex, from, target);
        _afterIndex = SwapRoleIndex(_afterIndex, from, target);
        _activeIndex = target;
        RefreshTree();
        _ = ShowSelectedAsync();
    }

    private void DeleteSelected()
    {
        if (_activeIndex < 0 || _activeIndex >= _project.Versions.Count) return;
        var deleted = _activeIndex;
        _project.Versions.RemoveAt(deleted);
        _beforeIndex = AfterDeleteIndex(_beforeIndex, deleted);
        _afterIndex = AfterDeleteIndex(_afterIndex, deleted);
        _activeIndex = _project.Versions.Count == 0 ? -1 : Math.Min(deleted, _project.Versions.Count - 1);
        RefreshTree();
        _ = ShowSelectedAsync();
    }

    private static int SwapRoleIndex(int role, int a, int b) => role == a ? b : role == b ? a : role;
    private static int AfterDeleteIndex(int role, int deleted) => role == deleted ? -1 : role > deleted ? role - 1 : role;


    private static Border Card() => new()
    {
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.Parse("#B7C4D2")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        ClipToBounds = true
    };

    private static Button Primary(string text) => new()
    {
        Content = text, Background = new SolidColorBrush(Color.Parse("#2563EB")), Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.Parse("#1D4ED8")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 7), Margin = new Thickness(0, 0, 6, 0)
    };

    private static Button Secondary(string text) => new()
    {
        Content = text, Background = Brushes.White, Foreground = new SolidColorBrush(Color.Parse("#334155")),
        BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 6, 0)
    };

    private static Border Message(string text, string color) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#F8FAFC")), Padding = new Thickness(12),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse(color)) }
    };
}
