namespace DocumentCompare.Avalonia.Engine;

public static class VersionPreviewLayoutPolicy
{
    private const double NaturalLineFactor = 1.50;
    private const double MinimumLineHeight = 20.0;

    public static double ResolveLineHeight(VersionParagraphVisualStyle? paragraph, double fallbackFontSize = 13.0)
    {
        var maxFont = paragraph?.DefaultRun.FontSize ?? fallbackFontSize;
        if (paragraph is not null)
        {
            foreach (var span in paragraph.Runs)
            {
                if (span.Style.FontSize is double size && size > maxFont)
                    maxFont = size;
            }
        }

        var natural = Math.Max(MinimumLineHeight, maxFont * NaturalLineFactor);
        return paragraph?.LineHeight is double requested && requested > 0
            ? Math.Max(requested, natural)
            : natural;
    }
}
