using System.Text;

namespace DocumentCompare.Avalonia.Engine;

/// <summary>
/// Maps Tree-sitter UTF-8 byte offsets to physical line numbers without
/// rescanning the entire file for every AST declaration.
/// </summary>
public sealed class CodeAstLineLocation
{
    private readonly int[] _lineStarts;

    private CodeAstLineLocation(int[] lineStarts) => _lineStarts = lineStarts;

    public static CodeAstLineLocation Index(string source)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(source);
        var starts = new List<int> { 0 };
        for (var i = 0; i + 1 < bytes.Length; i++)
            if (bytes[i] == (byte)'\n') starts.Add(i + 1);
        return new CodeAstLineLocation(starts.ToArray());
    }

    public int LineForByte(int byteOffset)
    {
        if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        var index = Array.BinarySearch(_lineStarts, byteOffset);
        return index >= 0 ? index : ~index - 1;
    }
}
