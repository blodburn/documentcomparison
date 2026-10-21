using System.Text;

namespace DocumentCompare.Avalonia.Models;

/// <summary>
/// Validated offsets for future Rust (UTF-8 byte) to Avalonia/.NET (UTF-16)
/// source navigation. Offsets are zero-based and may point to the end of text.
/// An offset inside a UTF-8 sequence or UTF-16 surrogate pair is rejected.
/// </summary>
public static class SourceTextOffsets
{
    public static int Utf8ByteToUtf16(string source, int byteOffset)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        var bytes = 0;
        var utf16 = 0;
        foreach (var rune in source.EnumerateRunes())
        {
            if (bytes == byteOffset) return utf16;
            var end = checked(bytes + rune.Utf8SequenceLength);
            if (byteOffset < end)
                throw new ArgumentOutOfRangeException(nameof(byteOffset), "Offset splits a UTF-8 code point.");
            bytes = end;
            utf16 += rune.Utf16SequenceLength;
        }
        if (bytes == byteOffset) return utf16;
        throw new ArgumentOutOfRangeException(nameof(byteOffset));
    }

    public static int Utf16ToUtf8Byte(string source, int utf16Index)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (utf16Index < 0) throw new ArgumentOutOfRangeException(nameof(utf16Index));
        var bytes = 0;
        var utf16 = 0;
        foreach (var rune in source.EnumerateRunes())
        {
            if (utf16 == utf16Index) return bytes;
            var end = checked(utf16 + rune.Utf16SequenceLength);
            if (utf16Index < end)
                throw new ArgumentOutOfRangeException(nameof(utf16Index), "Offset splits a UTF-16 surrogate pair.");
            utf16 = end;
            bytes = checked(bytes + rune.Utf8SequenceLength);
        }
        if (utf16 == utf16Index) return bytes;
        throw new ArgumentOutOfRangeException(nameof(utf16Index));
    }
}
