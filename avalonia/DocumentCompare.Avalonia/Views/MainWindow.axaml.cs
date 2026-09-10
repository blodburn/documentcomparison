using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DocumentCompare.Avalonia.Controls;
using DocumentCompare.Avalonia.Engine;
using DocumentCompare.Avalonia.Models;
using DocumentCompare.Avalonia.Localization;

namespace DocumentCompare.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly IComparisonEngine _engine = new PythonBridgeComparisonEngine();
    private CancellationTokenSource? _operationCts;
    private ComparisonResultVm? _result;
    private readonly ObservableCollection<ComparisonRowVm> _visibleRows = new();
    private readonly string?[] _selectedPaths = new string?[3];
    private readonly Dictionary<int, ComparisonRowControl> _documentRows = new();
    private readonly Dictionary<int, ChangeRowControl> _changeRows = new();
    private string[] _lastPaths = Array.Empty<string>();
    private int _lastBaseIndex;
    private string _lastMode = "auto";
    private bool _lastIncludeAc;
    private bool _lastIncludePunctuation = true;
    private UiLanguage _language = UiLocalization.DetectSystemLanguage();


    // Named XAML controls are resolved explicitly. This intentionally avoids Avalonia's
    // Roslyn name-source-generator so the project also builds with the .NET 8 SDK.
    private MenuItem HelpMenuItem = null!;
    private MenuItem QuickGuideMenuItem = null!;
    private MenuItem LanguageMenuItem = null!;
    private MenuItem KoreanLanguageMenuItem = null!;
    private MenuItem EnglishLanguageMenuItem = null!;
    private TextBlock AppTitleText = null!;
    private TextBlock ModeLabel = null!;
    private ComboBoxItem ModeAutoItem = null!;
    private ComboBoxItem ModeGeneralItem = null!;
    private ComboBoxItem ModeLegalItem = null!;
    private TextBlock SearchLabel = null!;
    private TextBlock ChangeHeaderText = null!;
    private Border DropA = null!;
    private Border DropB = null!;
    private Border DropC = null!;
    private Button ChooseAButton = null!;
    private Button ChooseBButton = null!;
    private Button ChooseCButton = null!;
    private TextBlock HeaderAText = null!;
    private TextBlock HeaderBText = null!;
    private TextBlock HeaderCText = null!;
    private RadioButton BaseA = null!;
    private RadioButton BaseB = null!;
    private RadioButton BaseC = null!;
    private Button CompareButton = null!;
    private Button CancelButton = null!;
    private Button ExcelButton = null!;
    private Button WordButton = null!;
    private ComboBox ModeBox = null!;
    private CheckBox CompareACBox = null!;
    private CheckBox PunctuationBox = null!;
    private TextBox SearchBox = null!;
    private TextBlock StatusText = null!;
    private ProgressBar CompareProgress = null!;
    private Grid ResultBody = null!;
    private ScrollViewer DocumentScroll = null!;
    private ItemsControl RowsRepeater = null!;
    private ItemsControl ChangeRowsRepeater = null!;
    private bool _rowHeightSyncQueued;
    private bool _rowHeightSyncRunning;
    private int _rowHeightSyncRetries;

    public MainWindow()
    {
        InitializeComponent();
        RowsRepeater.ItemsSource = _visibleRows;
        ChangeRowsRepeater.ItemsSource = _visibleRows;
        RegisterDropZone(DropA, 0, "A");
        RegisterDropZone(DropB, 1, "B");
        RegisterDropZone(DropC, 2, "C");
        Opened += async (_, _) => await WarmEngineAsync();
        Closed += (_, _) => _ = _engine.DisposeAsync();
        ResultBody.SizeChanged += (_, _) => ResetAndQueueRowHeightSync();
        ApplyLanguage();
        UpdateBaseRadios();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        HelpMenuItem = Require<MenuItem>("HelpMenuItem");
        QuickGuideMenuItem = Require<MenuItem>("QuickGuideMenuItem");
        LanguageMenuItem = Require<MenuItem>("LanguageMenuItem");
        KoreanLanguageMenuItem = Require<MenuItem>("KoreanLanguageMenuItem");
        EnglishLanguageMenuItem = Require<MenuItem>("EnglishLanguageMenuItem");
        AppTitleText = Require<TextBlock>("AppTitleText");
        ModeLabel = Require<TextBlock>("ModeLabel");
        ModeAutoItem = Require<ComboBoxItem>("ModeAutoItem");
        ModeGeneralItem = Require<ComboBoxItem>("ModeGeneralItem");
        ModeLegalItem = Require<ComboBoxItem>("ModeLegalItem");
        SearchLabel = Require<TextBlock>("SearchLabel");
        ChangeHeaderText = Require<TextBlock>("ChangeHeaderText");
        DropA = Require<Border>("DropA");
        DropB = Require<Border>("DropB");
        DropC = Require<Border>("DropC");
        ChooseAButton = Require<Button>("ChooseAButton");
        ChooseBButton = Require<Button>("ChooseBButton");
        ChooseCButton = Require<Button>("ChooseCButton");
        HeaderAText = Require<TextBlock>("HeaderAText");
        HeaderBText = Require<TextBlock>("HeaderBText");
        HeaderCText = Require<TextBlock>("HeaderCText");
        BaseA = Require<RadioButton>("BaseA");
        BaseB = Require<RadioButton>("BaseB");
        BaseC = Require<RadioButton>("BaseC");
        CompareButton = Require<Button>("CompareButton");
        CancelButton = Require<Button>("CancelButton");
        ExcelButton = Require<Button>("ExcelButton");
        WordButton = Require<Button>("WordButton");
        ModeBox = Require<ComboBox>("ModeBox");
        CompareACBox = Require<CheckBox>("CompareACBox");
        PunctuationBox = Require<CheckBox>("PunctuationBox");
        SearchBox = Require<TextBox>("SearchBox");
        StatusText = Require<TextBlock>("StatusText");
        CompareProgress = Require<ProgressBar>("CompareProgress");
        ResultBody = Require<Grid>("ResultBody");
        DocumentScroll = Require<ScrollViewer>("DocumentScroll");
        RowsRepeater = Require<ItemsControl>("RowsRepeater");
        ChangeRowsRepeater = Require<ItemsControl>("ChangeRowsRepeater");
    }

    private T Require<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"XAML control '{name}' was not found.");

    private string L(string korean, string english) => UiLocalization.T(_language, korean, english);

    private async void QuickGuide_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new HelpDialog(_language);
        await dialog.ShowDialog(this);
    }

    private void KoreanLanguage_Click(object? sender, RoutedEventArgs e) => SetLanguage(UiLanguage.Korean);
    private void EnglishLanguage_Click(object? sender, RoutedEventArgs e) => SetLanguage(UiLanguage.English);

    private void SetLanguage(UiLanguage language)
    {
        if (_language == language) return;
        _language = language;
        ApplyLanguage();
        if (_result is not null)
            RenderResult(_result);
        StatusText.Text = L("표시 언어를 한국어로 변경했습니다.", "Display language changed to English.");
    }

    private void ApplyLanguage()
    {
        Title = L("문서 비교기 V5.19.4", "Document Compare V5.19.4");
        AppTitleText.Text = L("문서 비교기", "Document Compare");
        CompareButton.Content = L("비교 시작", "Compare");
        CancelButton.Content = L("취소", "Cancel");
        ExcelButton.Content = L("Excel 내보내기", "Export Excel");
        WordButton.Content = L("Word 변경추적", "Word Track Changes");
        ModeLabel.Text = L("비교 방식:", "Mode:");
        ModeAutoItem.Content = L("자동", "Auto");
        ModeGeneralItem.Content = L("일반 문서", "General document");
        ModeLegalItem.Content = L("법률·규정", "Legal / policy");
        CompareACBox.Content = L("A↔C 추가 비교", "Also compare A↔C");
        PunctuationBox.Content = L("특수문자 포함", "Include punctuation");
        SearchLabel.Text = L("검색:", "Search:");
        SearchBox.PlaceholderText = L("본문/변경사항 검색", "Search text / changes");
        BaseA.Content = BaseB.Content = BaseC.Content = L("기준", "Base");
        ChangeHeaderText.Text = L("변경사항", "Changes");
        HelpMenuItem.Header = L("도움말", "Help");
        QuickGuideMenuItem.Header = L("간단 사용설명서", "Quick Guide");
        LanguageMenuItem.Header = L("언어", "Language");
        KoreanLanguageMenuItem.Header = (_language == UiLanguage.Korean ? "✓ " : "") + "한국어 (KR)";
        EnglishLanguageMenuItem.Header = (_language == UiLanguage.English ? "✓ " : "") + "English (EN)";
        UpdateHeaderLabels();
    }

    private static string CompactError(Exception ex)
    {
        var text = (ex.Message ?? ex.GetType().Name).Replace("\r", " ").Replace("\n", " ");
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length <= 280 ? text : text[..277] + "...";
    }

    private async Task WarmEngineAsync()
    {
        try
        {
            StatusText.Text = L("비교 엔진 준비 중...", "Preparing comparison engine...");
            await _engine.PingAsync();
            StatusText.Text = L("준비됨 · A/B/C 헤더에 파일을 클릭 또는 드롭하세요.", "Ready · Click or drop files onto the A/B/C headers.");
        }
        catch (Exception ex)
        {
            StatusText.Text = L("엔진 준비 필요: ", "Engine setup required: ") + CompactError(ex);
        }
    }

    private async Task ChooseAsync(int docIndex)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = L($"문서 {(char)('A' + docIndex)} 선택", $"Select document {(char)('A' + docIndex)}"),
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L("지원 문서", "Supported documents")) { Patterns = new[] { "*.docx", "*.txt" } }
            }
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is string path)
            SetDocumentPath(docIndex, path, L($"문서 {(char)('A' + docIndex)} 선택 완료", $"Document {(char)('A' + docIndex)} selected"));
    }

    private async void ChooseA_Click(object? sender, RoutedEventArgs e) => await ChooseAsync(0);
    private async void ChooseB_Click(object? sender, RoutedEventArgs e) => await ChooseAsync(1);
    private async void ChooseC_Click(object? sender, RoutedEventArgs e) => await ChooseAsync(2);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void RegisterDropZone(Border zone, int docIndex, string label)
    {
        DragDrop.AddDragOverHandler(zone, OnDragOver);
        DragDrop.AddDropHandler(zone, (_, e) => DropInto(docIndex, label, e));
    }

    private void DropInto(int docIndex, string label, DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFiles()?
            .Select(x => x.TryGetLocalPath())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .FirstOrDefault(IsSupported);
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = L("DOCX 또는 TXT 파일만 사용할 수 있습니다.", "Only DOCX or TXT files are supported.");
            return;
        }
        SetDocumentPath(docIndex, path, L($"문서 {label} 배치 완료", $"Document {label} loaded"));
    }

    private void SetDocumentPath(int docIndex, string path, string statusPrefix)
    {
        if (!IsSupported(path))
        {
            StatusText.Text = L("DOCX 또는 TXT 파일만 사용할 수 있습니다.", "Only DOCX or TXT files are supported.");
            return;
        }
        _selectedPaths[docIndex] = path;
        UpdateHeaderLabels();
        UpdateBaseRadios();
        var count = SelectedDocumentCount();
        StatusText.Text = count >= 2 && !string.IsNullOrWhiteSpace(_selectedPaths[0]) && !string.IsNullOrWhiteSpace(_selectedPaths[1])
            ? L($"{statusPrefix} · 비교 준비됨 ({count}개)", $"{statusPrefix} · Ready to compare ({count} documents)")
            : L($"{statusPrefix} · 문서 A와 B를 채워주세요.", $"{statusPrefix} · Please load documents A and B.");
    }

    private void UpdateHeaderLabels()
    {
        HeaderAText.Text = HeaderLabel(0, false);
        HeaderBText.Text = HeaderLabel(1, false);
        HeaderCText.Text = HeaderLabel(2, true);
        ToolTip.SetTip(ChooseAButton, _selectedPaths[0] ?? L("문서 A: 클릭해서 파일 선택 / 이 칸에 파일 드롭", "Document A: click to choose a file / drop a file here"));
        ToolTip.SetTip(ChooseBButton, _selectedPaths[1] ?? L("문서 B: 클릭해서 파일 선택 / 이 칸에 파일 드롭", "Document B: click to choose a file / drop a file here"));
        ToolTip.SetTip(ChooseCButton, _selectedPaths[2] ?? L("문서 C(선택): 클릭해서 파일 선택 / 이 칸에 파일 드롭", "Document C (optional): click to choose a file / drop a file here"));
    }

    private string HeaderLabel(int index, bool optional)
    {
        var path = _selectedPaths[index];
        var letter = (char)('A' + index);
        if (!string.IsNullOrWhiteSpace(path))
            return $"{letter} · {Path.GetFileName(path)}";
        return optional
            ? L($"{letter} · 파일 선택 또는 드롭 (선택)", $"{letter} · Choose or drop a file (optional)")
            : L($"{letter} · 파일 선택 또는 드롭", $"{letter} · Choose or drop a file");
    }

    private static bool IsSupported(string path) =>
        string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);

    private int SelectedDocumentCount() => _selectedPaths.Count(x => !string.IsNullOrWhiteSpace(x));

    private void UpdateBaseRadios()
    {
        BaseA.IsEnabled = !string.IsNullOrWhiteSpace(_selectedPaths[0]);
        BaseB.IsEnabled = !string.IsNullOrWhiteSpace(_selectedPaths[1]);
        BaseC.IsEnabled = !string.IsNullOrWhiteSpace(_selectedPaths[2]);
        CompareACBox.IsEnabled = BaseA.IsEnabled && BaseB.IsEnabled && BaseC.IsEnabled;
        if (!CompareACBox.IsEnabled) CompareACBox.IsChecked = false;

        var selectedIsValid = BaseA.IsChecked == true && BaseA.IsEnabled ||
                              BaseB.IsChecked == true && BaseB.IsEnabled ||
                              BaseC.IsChecked == true && BaseC.IsEnabled;
        if (!selectedIsValid)
        {
            if (BaseA.IsEnabled) BaseA.IsChecked = true;
            else if (BaseB.IsEnabled) BaseB.IsChecked = true;
            else if (BaseC.IsEnabled) BaseC.IsChecked = true;
        }
    }

    private string[] CurrentPaths()
    {
        // Slot identity is intentional: A and B are mandatory; C is optional.  Do not
        // collapse a lone C into B by filtering nulls because marker/document indices
        // must keep matching the visible A/B/C columns.
        if (string.IsNullOrWhiteSpace(_selectedPaths[0]) || string.IsNullOrWhiteSpace(_selectedPaths[1]))
            return Array.Empty<string>();
        var paths = new List<string> { _selectedPaths[0]!, _selectedPaths[1]! };
        if (!string.IsNullOrWhiteSpace(_selectedPaths[2])) paths.Add(_selectedPaths[2]!);
        return paths.ToArray();
    }

    private int CurrentBaseIndex(int count)
    {
        if (BaseB.IsChecked == true) return Math.Min(1, count - 1);
        if (BaseC.IsChecked == true && count > 2) return 2;
        return 0;
    }

    private string CurrentMode() => ModeBox.SelectedIndex switch
    {
        1 => "general",
        2 => "legal",
        _ => "auto"
    };

    private async void Compare_Click(object? sender, RoutedEventArgs e)
    {
        var paths = CurrentPaths();
        if (paths.Length is < 2 or > 3)
        {
            StatusText.Text = L("문서 A와 B를 선택하세요. 문서 C는 선택사항입니다.", "Select documents A and B. Document C is optional.");
            return;
        }
        var baseIndex = CurrentBaseIndex(paths.Length);
        var mode = CurrentMode();
        var includeAc = paths.Length == 3 && CompareACBox.IsChecked == true;
        var includePunctuation = PunctuationBox.IsChecked != false;
        _operationCts?.Cancel();
        _operationCts = new CancellationTokenSource();
        SetBusy(true, L("비교 중... 0%", "Comparing... 0%"));
        CompareProgress.Value = 0;
        CompareProgress.IsVisible = true;
        var progress = new Progress<int>(value =>
        {
            CompareProgress.Value = value;
            StatusText.Text = L($"비교 중... {value}%", $"Comparing... {value}%");
        });
        try
        {
            using var response = await _engine.SendAsync(
                new CompareRequest("compare", paths, baseIndex, mode,
                    IncludeAC: includeAc, IncludePunctuation: includePunctuation, WantProgress: true), _operationCts.Token, progress);
            _result = EngineResultMapper.Parse(response.RootElement);
            _lastPaths = paths;
            _lastBaseIndex = baseIndex;
            _lastMode = mode;
            _lastIncludeAc = includeAc;
            _lastIncludePunctuation = includePunctuation;
            RenderResult(_result);
            var counts = _result.UnitCounts.Count == paths.Length
                ? string.Join(" / ", _result.UnitCounts.Select((x, i) => L($"{(char)('A' + i)} {x}단위", $"{(char)('A' + i)} {x} units")))
                : L($"{_result.Rows.Count}행", $"{_result.Rows.Count} rows");
            var pairText = paths.Length == 3 ? (includeAc ? "A↔B / B↔C / A↔C" : "A↔B / B↔C") : "A↔B";
            var punctText = includePunctuation ? L("특수문자 포함", "punctuation included") : L("특수문자 제외", "punctuation excluded");
            CompareProgress.Value = 100;
            StatusText.Text = L($"비교 완료 · {pairText} · {punctText} · {counts}", $"Comparison complete · {pairText} · {punctText} · {counts}");
            ExcelButton.IsEnabled = true;
            WordButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("비교가 취소되었습니다.", "Comparison canceled.");
        }
        catch (Exception ex)
        {
            StatusText.Text = L("비교 실패: ", "Comparison failed: ") + CompactError(ex);
        }
        finally
        {
            SetBusy(false);
            CompareProgress.IsVisible = false;
        }
    }

    private void RenderResult(ComparisonResultVm result)
    {
        ResultBody.IsVisible = true;
        // The left document viewport always has three fixed columns, even for a two-document
        // comparison, so A/B/C stay aligned with the permanent fourth 변경사항 column.
        _documentRows.Clear();
        _changeRows.Clear();
        RowsRepeater.ItemTemplate = new FuncDataTemplate<ComparisonRowVm>(
            (row, _) =>
            {
                if (row is null) return new Border();
                var control = new ComparisonRowControl(row, 3, NavigateMarker, _language);
                _documentRows[row.Id] = control;
                control.SizeChanged += (_, _) => QueueRowHeightSync();
                return control;
            }, true);
        ChangeRowsRepeater.ItemTemplate = new FuncDataTemplate<ComparisonRowVm>(
            (row, _) =>
            {
                if (row is null) return new Border();
                var control = new ChangeRowControl(row, _language);
                _changeRows[row.Id] = control;
                control.SizeChanged += (_, _) => QueueRowHeightSync();
                return control;
            }, true);
        ApplySearch();
        DocumentScroll.Offset = Vector.Zero;
        Dispatcher.UIThread.Post(QueueRowHeightSync, DispatcherPriority.Background);
    }

    private void NavigateMarker(int rowId, int num, int sourceDocIndex, double markerLocalY)
    {
        // The document column that was clicked becomes the reference axis.  First align
        // matching markers in the other A/B/C columns inside this article row, then align
        // the article-scoped change pane to the same row-local eye-line.  The outer master
        // document scroll is never moved.
        if (_documentRows.TryGetValue(rowId, out var documentRow))
            documentRow.AlignMarkerAcrossDocuments(sourceDocIndex, num, markerLocalY);
        if (_changeRows.TryGetValue(rowId, out var changeRow))
            changeRow.NavigateTo(num, markerLocalY);
    }

    private void ResetAndQueueRowHeightSync()
    {
        foreach (var row in _documentRows.Values)
            row.SetAlignedContentHeight(0);
        _rowHeightSyncRetries = 0;
        QueueRowHeightSync();
    }

    private void QueueRowHeightSync()
    {
        if (_rowHeightSyncQueued || _rowHeightSyncRunning) return;
        _rowHeightSyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rowHeightSyncQueued = false;
            SyncRowHeights();
        }, DispatcherPriority.Background);
    }

    private void SyncRowHeights()
    {
        if (_rowHeightSyncRunning) return;
        _rowHeightSyncRunning = true;
        try
        {
            var waiting = false;
            foreach (var row in _visibleRows)
            {
                if (!_documentRows.TryGetValue(row.Id, out var documentRow) ||
                    !_changeRows.TryGetValue(row.Id, out var changeRow) ||
                    documentRow.ContentAnchor is null)
                {
                    waiting = true;
                    continue;
                }

                var sectionHeight = documentRow.SectionHeight;
                var documentHeight = documentRow.ContentHeight;
                var hasSection = row.SectionHeaders.Any(x => !string.IsNullOrWhiteSpace(x));
                if (documentHeight <= 0 || hasSection && sectionHeight <= 0)
                {
                    waiting = true;
                    continue;
                }

                // All four columns share one row height.  Use the tallest natural content
                // among A/B/C and the full change-message stack so wrapped change text is
                // never clipped by the next article boundary.  The change pane keeps its
                // internal runway/scroll behavior, but its viewport is at least tall enough
                // for every change item in this row.
                var changeHeight = changeRow.NaturalContentHeight;
                if (changeHeight <= 0)
                {
                    waiting = true;
                    continue;
                }
                var alignedHeight = Math.Max(documentHeight, changeHeight);
                documentRow.SetAlignedContentHeight(alignedHeight);
                changeRow.SetAlignedLayout(sectionHeight, alignedHeight);
            }

            if (waiting && _rowHeightSyncRetries++ < 16)
                DispatcherTimer.RunOnce(QueueRowHeightSync, TimeSpan.FromMilliseconds(20));
            else
                _rowHeightSyncRetries = 0;
        }
        finally
        {
            _rowHeightSyncRunning = false;
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        CompareButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ExcelButton.IsEnabled = !busy && _result is not null;
        WordButton.IsEnabled = !busy && _result is not null;
        ChooseAButton.IsEnabled = !busy;
        ChooseBButton.IsEnabled = !busy;
        ChooseCButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(message)) StatusText.Text = message;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        _engine.AbortCurrentOperation();
        StatusText.Text = L("취소 중...", "Canceling...");
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

    private void ApplySearch()
    {
        if (_result is null) return;
        var query = SearchBox.Text?.Trim();
        IEnumerable<ComparisonRowVm> filtered = _result.Rows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(r =>
                r.Members.Any(m => m?.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true) ||
                r.DisplayMessages.Any(m => m.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        }
        var rows = filtered.ToList();
        _documentRows.Clear();
        _changeRows.Clear();
        _visibleRows.Clear();
        foreach (var row in rows) _visibleRows.Add(row);
        _rowHeightSyncRetries = 0;
        QueueRowHeightSync();
    }

    private async void Excel_Click(object? sender, RoutedEventArgs e)
    {
        if (_result is null || _lastPaths.Length < 2) return;
        var save = await PickSavePathAsync(L("Excel 비교 결과 저장", "Save Excel comparison"), L("문서비교.xlsx", "DocumentComparison.xlsx"), L("Excel 파일", "Excel file"), "*.xlsx");
        if (save is null) return;
        SetBusy(true, L("Excel 생성 중...", "Creating Excel file..."));
        try
        {
            using var _ = await _engine.SendAsync(new CompareRequest(
                "export_xlsx", _lastPaths, _lastBaseIndex, _lastMode, OutputPath: save,
                IncludeAC: _lastIncludeAc, IncludePunctuation: _lastIncludePunctuation));
            StatusText.Text = L("Excel 저장 완료: ", "Excel saved: ") + save;
        }
        catch (Exception ex) { StatusText.Text = L("Excel 저장 실패: ", "Excel save failed: ") + CompactError(ex); }
        finally { SetBusy(false); }
    }

    private async void Word_Click(object? sender, RoutedEventArgs e)
    {
        if (_result is null || _lastPaths.Length < 2) return;
        var pair = _lastPaths.Length == 2
            ? DefaultWordPair()
            : await WordPairDialog.ShowAsync(this, _result.Names, _lastBaseIndex, _language);
        if (pair is null) return;
        var original = _lastPaths[pair.Value.Original];
        var revised = _lastPaths[pair.Value.Revised];
        var defaultName = $"{Path.GetFileNameWithoutExtension(original)}_to_{Path.GetFileNameWithoutExtension(revised)}_Tracked.docx";
        var save = await PickSavePathAsync(L("Word 변경추적 문서 저장", "Save Word tracked-changes document"), defaultName, L("Word 문서", "Word document"), "*.docx");
        if (save is null) return;
        SetBusy(true, L("Word 변경추적 문서 생성 중...", "Creating Word tracked-changes document..."));
        try
        {
            using var _ = await _engine.SendAsync(new CompareRequest(
                "export_word", Array.Empty<string>(), 0, _lastMode,
                OutputPath: save, OriginalPath: original, RevisedPath: revised,
                Author: Path.GetFileNameWithoutExtension(revised),
                IncludePunctuation: _lastIncludePunctuation));
            StatusText.Text = L($"Word 저장 완료 · 변경 전 {Path.GetFileName(original)} → 최종 {Path.GetFileName(revised)}", $"Word saved · Original {Path.GetFileName(original)} → Revised {Path.GetFileName(revised)}");
        }
        catch (Exception ex) { StatusText.Text = L("Word 저장 실패: ", "Word save failed: ") + CompactError(ex); }
        finally { SetBusy(false); }
    }

    private (int Original, int Revised)? DefaultWordPair()
    {
        if (_lastPaths.Length != 2) return null;
        return _lastBaseIndex == 0 ? (0, 1) : (1, 0);
    }

    private async Task<string?> PickSavePathAsync(string title, string suggestedName, string typeName, string pattern)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { pattern } } }
        });
        return file?.TryGetLocalPath();
    }
}
