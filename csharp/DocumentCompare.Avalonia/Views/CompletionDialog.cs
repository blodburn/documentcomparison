using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentCompare.Avalonia.Localization;

namespace DocumentCompare.Avalonia.Views;

/// <summary>
/// Small modal confirmation shown only after a comparison/export operation has completed
/// successfully. Failures and cancellations continue to use the existing status/error flow.
/// </summary>
public sealed class CompletionDialog : Window
{
    private CompletionDialog(UiLanguage language, string koreanMessage, string englishMessage)
    {
        Title = UiLocalization.T(language, "완료", "Completed");
        Width = 420;
        Height = 180;
        MinWidth = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button
        {
            Content = UiLocalization.T(language, "확인", "OK"),
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = UiLocalization.T(language, koreanMessage, englishMessage),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 15
                },
                ok
            }
        };
    }

    public static Task ShowAsync(Window owner, UiLanguage language, string koreanMessage, string englishMessage)
    {
        var dialog = new CompletionDialog(language, koreanMessage, englishMessage);
        return dialog.ShowDialog(owner);
    }
}
