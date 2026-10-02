using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Interactivity;
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
    private int _selectedIndex = -1;
    private string? _projectPath;
    private CancellationTokenSource? _loadCts;

    private readonly StackPanel _tree = new() { Spacing = 4 };
    private readonly StackPanel _preview = new() { Spacing = 0 };
    private readonly StackPanel _changes = new() { Spacing = 8 };
    private readonly TextBlock _projectTitle = new() { Text = "문서 버전", FontSize = 17, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _previewTitle = new() { Text = "버전을 선택하세요", FontSize = 17, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _previewSubtitle = new() { Text = "왼쪽 버전 트리에서 문서를 선택하면 해당 버전이 표시됩니다.", Foreground = Brushes.Gray, FontSize = 12 };
    private readonly TextBlock _changeTitle = new() { Text = "변경사항", FontSize = 17, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _changeSummary = new() { Text = "선택된 버전이 없습니다.", Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Text = "버전 파일을 추가하세요.", Foreground = new SolidColorBrush(Color.Parse("#64748B")), FontSize = 12, TextWrapping = TextWrapping.Wrap };
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
            ColumnDefinitions = new ColumnDefinitions("285,8,5*,8,2.25*"),
            Margin = new Thickness(4, 10, 4, 4)
        };

        var left = Card();
        var leftRoot = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
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

        var treeScroll = new ScrollViewer
        {
            Content = _tree,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(10, 0, 8, 6)
        };
        Grid.SetRow(treeScroll, 2); leftRoot.Children.Add(treeScroll);

        var reorder = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 4, 12, 8) };
        reorder.Children.Add(_moveUp); reorder.Children.Add(_moveDown); reorder.Children.Add(_delete);
        Grid.SetRow(reorder, 3); leftRoot.Children.Add(reorder);
        var statusBorder = new Border { Background = new SolidColorBrush(Color.Parse("#F7F9FC")), Padding = new Thickness(12, 9), Child = _status };
        Grid.SetRow(statusBorder, 4); leftRoot.Children.Add(statusBorder);
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
        var previewScroll = new ScrollViewer
        {
            Content = _preview,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(previewScroll, 1); centerRoot.Children.Add(previewScroll);
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
        var changesScroll = new ScrollViewer
        {
            Content = _changes,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(10)
        };
        Grid.SetRow(changesScroll, 1); rightRoot.Children.Add(changesScroll);
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
                new FilePickerFileType("지원 문서") { Patterns = new[] { "*.docx", "*.txt" } }
            }
        });
        var added = 0;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || _project.Versions.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            _project.Versions.Add(new VersionFileVm
            {
                Label = Path.GetFileNameWithoutExtension(path),
                Path = path,
                Sha256 = Hash(path),
                AddedAtUtc = DateTime.UtcNow
            });
            added++;
        }
        if (added == 0) return;
        _selectedIndex = _project.Versions.Count - 1;
        RefreshTree();
        _status.Text = $"{added}개 버전을 추가했습니다. 순서는 위/아래 버튼으로 조정할 수 있습니다.";
        await ShowSelectedAsync();
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
            _project = loaded;
            _projectPath = path;
            _selectedIndex = _project.Versions.Count > 0 ? _project.Versions.Count - 1 : -1;
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
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(_project, new JsonSerializerOptions { WriteIndented = true }));
            _projectPath = path;
            _status.Text = $"프로젝트를 저장했습니다: {Path.GetFileName(path)}";
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
                Text = "버전 파일이 없습니다.\n‘+ 버전 추가’로 v0.1부터 순서대로 추가하세요.",
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
            var current = i == _selectedIndex;
            var previous = _selectedIndex > 0 && i == _selectedIndex - 1;
            var version = _project.Versions[i];
            var tag = current ? "후" : previous ? "전" : i == 0 && _selectedIndex == 0 ? "최초본" : "";
            var bg = current ? "#E8F1FF" : previous ? "#F1F3F5" : "#FFFFFF";
            var border = current ? "#4C86D9" : previous ? "#AEB8C4" : "#DDE4EC";
            var btn = new Button
            {
                Background = new SolidColorBrush(Color.Parse(bg)),
                BorderBrush = new SolidColorBrush(Color.Parse(border)),
                BorderThickness = new Thickness(current ? 2 : 1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = VersionNodeContent(version, tag, current)
            };
            btn.Click += async (_, _) =>
            {
                _selectedIndex = index;
                RefreshTree();
                await ShowSelectedAsync();
            };
            _tree.Children.Add(btn);
        }

        _moveUp.IsEnabled = _selectedIndex > 0;
        _moveDown.IsEnabled = _selectedIndex >= 0 && _selectedIndex < _project.Versions.Count - 1;
        _delete.IsEnabled = _selectedIndex >= 0;
    }

    private static Control VersionNodeContent(VersionFileVm version, string tag, bool current)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = version.Label, FontWeight = current ? FontWeight.SemiBold : FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = Path.GetFileName(version.Path), FontSize = 10.5, Foreground = new SolidColorBrush(Color.Parse("#64748B")), TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(text);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.Parse(current ? "#2563EB" : "#E2E8F0")),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = tag, FontSize = 10, Foreground = current ? Brushes.White : new SolidColorBrush(Color.Parse("#475569")), FontWeight = FontWeight.SemiBold }
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
        _changes.Children.Clear();

        if (_selectedIndex < 0 || _selectedIndex >= _project.Versions.Count)
        {
            _previewTitle.Text = "버전을 선택하세요";
            _previewSubtitle.Text = "왼쪽 버전 트리에서 문서를 선택하면 해당 버전이 표시됩니다.";
            _changeSummary.Text = "선택된 버전이 없습니다.";
            return;
        }

        var current = _project.Versions[_selectedIndex];
        if (!File.Exists(current.Path))
        {
            _previewTitle.Text = current.Label;
            _previewSubtitle.Text = "원본 파일을 찾을 수 없습니다.";
            _changeSummary.Text = "파일 경로를 확인하세요.";
            _preview.Children.Add(Message("원본 파일이 이동되었거나 삭제되었습니다.", "#C62828"));
            return;
        }

        try
        {
            _previewTitle.Text = current.Label;
            if (_selectedIndex == 0)
            {
                _previewSubtitle.Text = "최초 등록 버전";
                _changeSummary.Text = "최초본 · 비교할 이전 버전이 없습니다.";
                var text = await NativeDocumentReader.ReadAsync(current.Path, token);
                _preview.Children.Add(new Border
                {
                    Padding = new Thickness(18), Background = Brushes.White,
                    Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 24 }
                });
                _changes.Children.Add(Message("최초 등록 버전입니다.\n다음 버전을 선택하면 직전 버전과의 차이를 표시합니다.", "#64748B"));
                return;
            }

            var previous = _project.Versions[_selectedIndex - 1];
            if (!File.Exists(previous.Path))
            {
                _previewSubtitle.Text = $"비교 기준 {previous.Label} 파일을 찾을 수 없습니다.";
                _changeSummary.Text = "이전 버전 파일이 없습니다.";
                return;
            }

            _previewSubtitle.Text = $"{previous.Label} [전]  →  {current.Label} [후] · 선택한 ‘후’ 문서 표시 중";
            _changeSummary.Text = "비교 중...";
            var result = await _engine.CompareAsync(new[] { previous.Path, current.Path }, 1, "auto", false, true, token);
            foreach (var row in result.Rows)
                _preview.Children.Add(new VersionPreviewRowControl(row, 1));

            var changeItems = BuildChangeItems(result);
            changeItems.AddRange(VersionFormattingInspector.Compare(previous.Path, current.Path));
            RenderChanges(changeItems);
            _status.Text = $"{previous.Label} → {current.Label} 비교 완료";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _changeSummary.Text = "버전 비교 실패";
            _changes.Children.Add(Message(ex.Message, "#C62828"));
        }
    }

    private static List<VersionChangeVm> BuildChangeItems(ComparisonResultVm result)
    {
        var items = result.Rows.SelectMany(x => x.Markers)
            .GroupBy(x => x.Num)
            .Select(g => g.First())
            .OrderBy(x => x.Num)
            .Select(x => new VersionChangeVm
            {
                Category = x.StructuralNumber ? "구조" : "내용",
                Title = $"[{x.Num}] {x.Action}",
                Detail = x.Message,
                MarkerNumber = x.Num
            }).ToList();

        var structural = result.Rows.SelectMany(x => x.DisplayMessages)
            .Where(x => x.Contains("상태:", StringComparison.Ordinal) || x.Contains("이동", StringComparison.Ordinal) || x.Contains("구조", StringComparison.Ordinal))
            .Distinct();
        items.AddRange(structural.Select(x => new VersionChangeVm { Category = "구조", Title = "구조 변경", Detail = x.TrimStart('•', ' ') }));
        return items;
    }

    private void RenderChanges(List<VersionChangeVm> items)
    {
        _changes.Children.Clear();
        if (items.Count == 0)
        {
            _changeSummary.Text = "변경사항 없음";
            _changes.Children.Add(Message("직전 버전과 내용/서식 차이가 없습니다.", "#64748B"));
            return;
        }
        var groups = items.GroupBy(x => x.Category).ToDictionary(x => x.Key, x => x.Count());
        _changeSummary.Text = $"전체 {items.Count}건 · " + string.Join(" · ", groups.Select(x => $"{x.Key} {x.Value}"));
        foreach (var item in items)
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
            header.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(header);
            if (!string.IsNullOrWhiteSpace(item.Detail))
                body.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#475569")) });
            _changes.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#DCE3EC")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 8), Child = body
            });
        }
    }

    private void MoveSelected(int delta)
    {
        if (_selectedIndex < 0) return;
        var target = _selectedIndex + delta;
        if (target < 0 || target >= _project.Versions.Count) return;
        (_project.Versions[_selectedIndex], _project.Versions[target]) = (_project.Versions[target], _project.Versions[_selectedIndex]);
        _selectedIndex = target;
        RefreshTree();
        _ = ShowSelectedAsync();
    }

    private void DeleteSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _project.Versions.Count) return;
        _project.Versions.RemoveAt(_selectedIndex);
        _selectedIndex = _project.Versions.Count == 0 ? -1 : Math.Min(_selectedIndex, _project.Versions.Count - 1);
        RefreshTree();
        _ = ShowSelectedAsync();
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

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
