using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentCompare.Avalonia.Localization;

namespace DocumentCompare.Avalonia.Views;

public sealed class WordPairDialog : Window
{
    private readonly ComboBox _pairCombo = new();
    private readonly List<(int Original, int Revised)> _pairs = new();
    private (int Original, int Revised)? _result;

    private WordPairDialog(IReadOnlyList<string> names, int baseIndex, UiLanguage language)
    {
        Title = UiLocalization.T(language, "Word 변경추적 · 비교 문서 선택", "Word Track Changes · Select documents");
        Width = 660;
        Height = 255;
        MinWidth = 560;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Forward revision pairs first (A→B, B→C, A→C), then their reverse directions.
        AddPair(names, 0, 1);
        AddPair(names, 1, 2);
        AddPair(names, 0, 2);
        AddPair(names, 1, 0);
        AddPair(names, 2, 1);
        AddPair(names, 2, 0);

        _pairCombo.ItemsSource = _pairs.Select(p => PairLabel(names, p.Original, p.Revised)).ToArray();
        _pairCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pairCombo.MinWidth = 560;

        var defaultRevised = Enumerable.Range(0, names.Count).FirstOrDefault(i => i != Math.Clamp(baseIndex, 0, names.Count - 1));
        var defaultPair = (Original: Math.Clamp(baseIndex, 0, names.Count - 1), Revised: defaultRevised);
        var defaultIndex = _pairs.FindIndex(p => p == defaultPair);
        _pairCombo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;

        var ok = new Button
        {
            Content = UiLocalization.T(language, "변경추적 문서 만들기", "Create tracked document"),
            MinWidth = 160,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ok.Click += Ok_Click;
        var cancel = new Button
        {
            Content = UiLocalization.T(language, "취소", "Cancel"),
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = UiLocalization.T(language, "변경 전 → 변경 후 방향을 드롭다운에서 선택하세요.", "Choose the original → revised direction from the dropdown."),
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = UiLocalization.T(language, "비교 조합", "Comparison pair"), FontWeight = FontWeight.SemiBold },
                _pairCombo,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 10,
                    Children = { cancel, ok }
                }
            }
        };
    }

    private void AddPair(IReadOnlyList<string> names, int original, int revised)
    {
        if (original < 0 || revised < 0 || original >= names.Count || revised >= names.Count || original == revised)
            return;
        _pairs.Add((original, revised));
    }

    private static string PairLabel(IReadOnlyList<string> names, int original, int revised)
    {
        var a = (char)('A' + original);
        var b = (char)('A' + revised);
        return $"{a} → {b}    {names[original]}  →  {names[revised]}";
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (_pairCombo.SelectedIndex < 0 || _pairCombo.SelectedIndex >= _pairs.Count)
            return;
        _result = _pairs[_pairCombo.SelectedIndex];
        Close(_result);
    }

    public static async Task<(int Original, int Revised)?> ShowAsync(Window owner, IReadOnlyList<string> names, int baseIndex, UiLanguage language)
    {
        var dialog = new WordPairDialog(names, baseIndex, language);
        return await dialog.ShowDialog<(int Original, int Revised)?>(owner);
    }
}
