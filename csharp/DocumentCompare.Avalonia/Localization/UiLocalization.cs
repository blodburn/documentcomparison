using System.Globalization;
using System.Text;

namespace DocumentCompare.Avalonia.Localization;

public enum UiLanguage
{
    Korean,
    English
}

public static class UiLocalization
{
    public static UiLanguage DetectSystemLanguage()
    {
        var ui = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (string.Equals(ui, "ko", StringComparison.OrdinalIgnoreCase))
            return UiLanguage.Korean;

        var culture = CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
        return string.Equals(culture, "ko", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Korean
            : UiLanguage.English;
    }

    public static string T(UiLanguage language, string korean, string english) =>
        language == UiLanguage.Korean ? korean : english;

    public static string LocalizeChangeMessage(string message, UiLanguage language)
    {
        if (language == UiLanguage.Korean || string.IsNullOrEmpty(message))
            return message;

        // Translate only the UI/grammar outside quoted source text.  Text inside curly or
        // ASCII quotes is document content and must remain byte-for-byte visible to the user.
        var result = new StringBuilder(message.Length + 32);
        var chunk = new StringBuilder();
        var inCurlyQuote = false;
        var inAsciiQuote = false;

        void Flush(bool translate)
        {
            if (chunk.Length == 0) return;
            var value = chunk.ToString();
            result.Append(translate ? TranslateOutsideQuotedText(value) : value);
            chunk.Clear();
        }

        foreach (var ch in message)
        {
            if (ch == '“' && !inAsciiQuote)
            {
                Flush(translate: !inCurlyQuote);
                inCurlyQuote = true;
                result.Append(ch);
                continue;
            }
            if (ch == '”' && inCurlyQuote)
            {
                Flush(translate: false);
                inCurlyQuote = false;
                result.Append(ch);
                continue;
            }
            if (ch == '"' && !inCurlyQuote)
            {
                Flush(translate: !inAsciiQuote);
                inAsciiQuote = !inAsciiQuote;
                result.Append(ch);
                continue;
            }
            chunk.Append(ch);
        }
        Flush(translate: !inCurlyQuote && !inAsciiQuote);
        return result.ToString();
    }

    private static string TranslateOutsideQuotedText(string value)
    {
        var replacements = new (string Ko, string En)[]
        {
            ("항/호 번호·위치 변경", "Item/subitem number/position changed"),
            ("항/호 구조 변경", "Item/subitem structure changed"),
            ("조 번호/위치 변경", "Article number/position changed"),
            ("제목/번호 표기 변경", "Title/number notation changed"),
            ("조/블록 삭제", "Article/block deleted"),
            ("조/블록 추가", "Article/block added"),
            ("기호 위치 변경", "Symbol moved"),
            ("기호 변경", "Symbol changed"),
            ("기호 삭제", "Symbol deleted"),
            ("기호 추가", "Symbol added"),
            ("편제 변경", "Section structure changed"),
            ("문장 위치 이동", "Sentence moved"),
            ("기준문서 외 신설", "Added outside the base document"),
            ("신설 조항", "New article"),
            ("조항 삭제", "Article deleted"),
            ("비교문서에서 이 조항이 삭제", "This article is deleted in the compared document"),
            ("서로 다른 수정 내용", "Different revisions"),
            ("문장 경계", "sentence boundary"),
            ("변경 없음", "No changes"),
            ("문서 A", "Document A"),
            ("문서 B", "Document B"),
            ("문서 C", "Document C"),
            ("본문", "Body"),
            (" 앞", " before"),
            (" 뒤", " after"),
            ("삭제:", "Deleted:"),
            ("추가:", "Added:"),
            ("변경:", "Changed:"),
            ("없음", "None")
        };

        foreach (var (ko, en) in replacements)
            value = value.Replace(ko, en, StringComparison.Ordinal);
        return value;
    }
}
