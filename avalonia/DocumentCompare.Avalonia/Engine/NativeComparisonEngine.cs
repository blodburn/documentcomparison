using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

public sealed class NativeComparisonEngine : IComparisonEngine
{
    private static readonly Regex KoreanArticle = new(
        """^[\s\p{Cf}]*제\s*(\d+)\s*조(?:\s*의\s*(\d+))?\s*(?:\(([^\n)]{1,120})\))?\s*(.*)$""",
        RegexOptions.Compiled);
    private static readonly Regex EnglishArticle = new(
        """^[\s\p{Cf}]*(?:Article|Section)\s+(\d+(?:[-.]\d+)*)\s*(?:[.:-])?\s*(.*)$""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SectionHeading = new(
        """^[\s\p{Cf}]*(?:제\s*\d+\s*(?:장|절|관)\b.*|(?:Chapter|Part)\s+\d+(?:[-.]\d+)*\b.*)$""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EnglishHierarchy = new(
        """^[\s\p{Cf}]*(?:[•●▪◦·*]+\s*)?(?<level>Chapter|Part)\s+(?<num>(?:\d+(?:[-.]\d+)*)|(?:[IVXLCDM]+))\s*[.\-:–—]?\s*(?<title>.*?)\s*$""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KoreanHierarchy = new(
        """^[\s\p{Cf}]*(?:[•●▪◦·*]+\s*)?제\s*(?<num>\d+)\s*(?<level>장|절|관)\s*(?:\((?<p>[^)\n]*)\)|\[(?<b>[^]\n]*)\]|(?<title>.*?))\s*$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitItem = new(
        """^[ \t\p{Cf}]*(?<label>(?:[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|\(\d+\)|\d+[.)]|[가-하A-Za-z][.)]))(?<ws>[ \t]+)""",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex StrongInlineEnglishArticle = new(
        """(?<![A-Za-z0-9])(?:Article|Section)\s+\d+(?:[-.]\d+)*\s*(?:[.:-])?\s*\([^\n)]{1,180}[)}]""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StrongInlineKoreanArticle = new(
        """제\s*\d+\s*조(?:\s*의\s*\d+)?\s*\([^\n)]{1,180}[)}]""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedHead = new(
        "^\\s*[\\x22“‘]([^\\x22”’]{1,96})[\\x22”’]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EmbeddedExplicitItemCandidate = new(
        @"(?m)(?<gap>^[ \t]*|[ \t]+)(?<label>(?:[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|\(\d+\)|\d+[.)]|[가-하A-Za-z][.)]))(?=[ \t]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KoreanLexToken = new(
        @"[가-힣A-Za-z]+|\d+(?:,\d{3})*(?:\.\d+)?%?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WordSetToken = new(
        @"[가-힣A-Za-z]+|\d+(?:,\d{3})*(?:\.\d+)?%?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SimilarityToken = new(
        """[가-힣A-Za-z0-9_]+|[^\s]""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BoundaryLexToken = new(
        @"[가-힣A-Za-z]+(?:['’][A-Za-z]+)?|\d+(?:,\d{3})*(?:\.\d+)?%?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReviewWordToken = new(
        """[가-힣A-Za-z0-9_]+""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DiffToken = new(
        """[가-힣A-Za-z0-9_]+|[^\s가-힣A-Za-z0-9_]""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KoreanPresence = new("[가-힣]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AlnumPresence = new("[가-힣A-Za-z0-9]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SentenceSeparator = new(@"[.!?。！？;:]|\n", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _cancelLock = new();
    private CancellationTokenSource? _activeOperation;

    public async Task<ComparisonResultVm> CompareAsync(
        IReadOnlyList<string> paths, int baseIndex, string mode, bool includeAC, bool includePunctuation,
        CancellationToken cancellationToken = default, IProgress<int>? progress = null)
    {
        if (paths.Count is < 2 or > 3) throw new ArgumentException("문서는 2개 또는 3개여야 합니다.", nameof(paths));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_cancelLock) _activeOperation = linked;
        try
        {
            var names = paths.Select(x => Path.GetFileName(x) ?? string.Empty).ToList();
            var texts = new List<string>(paths.Count);
            for (var i = 0; i < paths.Count; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                texts.Add(await NativeDocumentReader.ReadAsync(paths[i], linked.Token));
                progress?.Report(5 + (i + 1) * 15 / paths.Count);
            }

            var docs = new List<List<NativeUnit>>(paths.Count);
            for (var i = 0; i < texts.Count; i++)
            {
                docs.Add(ParseUnits(texts[i], mode));
                progress?.Report(20 + (i + 1) * 15 / texts.Count);
            }

            baseIndex = Math.Clamp(baseIndex, 0, paths.Count - 1);
            var aligned = BuildRows(docs, baseIndex, linked.Token);
            var globalParts = BuildGlobalPartIndex(docs);
            progress?.Report(45);
            var rows = new List<ComparisonRowVm>(aligned.Count);
            for (var i = 0; i < aligned.Count; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                rows.Add(BuildRow(i + 1, aligned[i], paths.Count, baseIndex, includeAC, includePunctuation, globalParts));
                if ((i & 7) == 0) progress?.Report(45 + (int)(50.0 * (i + 1) / Math.Max(1, aligned.Count)));
            }
            ApplySectionHeaders(rows, paths.Count);
            progress?.Report(100);
            return new ComparisonResultVm
            {
                Names = names,
                BaseIndex = baseIndex,
                Rows = rows,
                UnitCounts = docs.Select(x => x.Count).ToList()
            };
        }
        finally
        {
            lock (_cancelLock) if (ReferenceEquals(_activeOperation, linked)) _activeOperation = null;
        }
    }

    public Task ExportExcelAsync(ComparisonResultVm result, string outputPath, CancellationToken cancellationToken = default) =>
        NativeOfficeExporter.WriteXlsxAsync(result, outputPath, cancellationToken);

    public Task ExportWordAsync(string originalPath, string revisedPath, string outputPath, string author, bool includePunctuation,
        CancellationToken cancellationToken = default) =>
        NativeOfficeExporter.WriteTrackedDocxAsync(originalPath, revisedPath, outputPath, author, includePunctuation, cancellationToken);

    public Task PingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void AbortCurrentOperation()
    {
        lock (_cancelLock) { try { _activeOperation?.Cancel(); } catch { } }
    }

    public ValueTask DisposeAsync()
    {
        AbortCurrentOperation();
        lock (_cancelLock) { _activeOperation?.Dispose(); _activeOperation = null; }
        return ValueTask.CompletedTask;
    }

    private static List<NativeUnit> ParseUnits(string text, string mode)
    {
        text = NormalizeLegalBoundaries(NativeDocumentReader.NormalizeNewlines(text));
        var legal = mode.Equals("legal", StringComparison.OrdinalIgnoreCase) ||
                    (!mode.Equals("general", StringComparison.OrdinalIgnoreCase) && LooksLegal(text));
        return legal ? ParseLegalUnits(text) : ParseGeneralUnits(text);
    }

    private static string NormalizeLegalBoundaries(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var starts = new SortedSet<int>();
        void Collect(Regex regex)
        {
            foreach (Match m in regex.Matches(text))
            {
                var at = m.Index;
                if (at <= 0 || text[at - 1] == '\n') continue;
                var q = at - 1;
                while (q >= 0 && char.IsWhiteSpace(text[q]) && text[q] != '\n') q--;
                if (q < 0 || text[q] == '\n') continue;
                // Strong titled headings flattened after a sentence/table delimiter are structural.
                // Ordinary references such as "under Article 5 (Fees)" are deliberately left alone.
                if (text[q] is '.' or '!' or '?' or ';' or '|' or '}' or ')' or '。' or '！' or '？')
                    starts.Add(at);
            }
        }
        Collect(StrongInlineEnglishArticle);
        Collect(StrongInlineKoreanArticle);
        if (starts.Count == 0) return text;
        var sb = new StringBuilder(text);
        foreach (var at in starts.Reverse()) sb.Insert(at, '\n');
        return sb.ToString();
    }

    private static string CleanArticleTitle(string value)
    {
        var s = (value ?? string.Empty).Trim();
        s = Regex.Replace(s, """^[\s.:\-–—]+""", string.Empty);
        s = s.Trim();
        // Word revisions sometimes change only the title wrapper, e.g. (Purpose) -> : Purpose
        // or even leave a mismatched brace. Identity is the lexical title, not its wrapper.
        s = s.Trim('(', ')', '[', ']', '{', '}', ' ', '\t', '.', ':', '-', '–', '—');
        return s.Trim();
    }

    private static (string Title, string BodyTail) ParseEnglishArticleRest(string value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return (string.Empty, string.Empty);
        var titled = Regex.Match(raw, """^[\(\{\[](?<title>[^\)\}\]\n]{1,180})[\)\}\]]\s*(?<tail>.*)$""");
        if (titled.Success)
            return (CleanArticleTitle(titled.Groups["title"].Value), titled.Groups["tail"].Value.Trim());
        return (CleanArticleTitle(raw), string.Empty);
    }

    private static bool HeaderEquivalent(NativeUnit a, NativeUnit b)
    {
        if (!string.Equals(a.Number, b.Number, StringComparison.OrdinalIgnoreCase)) return false;
        var at = Normalize(CleanArticleTitle(a.Title));
        var bt = Normalize(CleanArticleTitle(b.Title));
        return (at.Length > 0 || bt.Length > 0) && at == bt;
    }

    private static (int Start, int End)? HeaderNumberSpan(NativeUnit unit)
    {
        if (unit.Header.Length == 0 || unit.Number.Length == 0) return null;
        var em = EnglishArticle.Match(unit.Header);
        if (em.Success && em.Groups[1].Success)
            return (em.Groups[1].Index, em.Groups[1].Index + em.Groups[1].Length);
        var km = KoreanArticle.Match(unit.Header);
        if (km.Success && km.Groups[1].Success)
        {
            var start = km.Groups[1].Index;
            var end = km.Groups[2].Success ? km.Groups[2].Index + km.Groups[2].Length : km.Groups[1].Index + km.Groups[1].Length;
            return (start, end);
        }
        var at = unit.Header.IndexOf(unit.Number, StringComparison.OrdinalIgnoreCase);
        return at >= 0 ? (at, at + unit.Number.Length) : null;
    }

    private static (int Start, int End) HeaderTitleSpan(NativeUnit unit)
    {
        if (unit.Title.Length > 0)
        {
            var at = unit.Header.IndexOf(unit.Title, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return (at, at + unit.Title.Length);
        }
        var number = HeaderNumberSpan(unit);
        var anchor = number?.End ?? unit.Header.Length;
        return (anchor, anchor);
    }

    private static bool LooksLegal(string text)
    {
        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            if (KoreanArticle.IsMatch(line) || EnglishArticle.IsMatch(line)) count++;
            if (count >= 2) return true;
        }
        return false;
    }

    private static bool TitleCandidate(string value)
    {
        var s = (value ?? string.Empty).Trim(' ', '\t', '|', ':', '-', '–', '—');
        if (s.Length == 0 || s.Length > 160) return false;
        if (EnglishArticle.IsMatch(s) || KoreanArticle.IsMatch(s) || EnglishHierarchy.IsMatch(s) || KoreanHierarchy.IsMatch(s)) return false;
        if (ExplicitItem.IsMatch(s)) return false;
        var words = Regex.Matches(s, "[A-Za-z가-힣]+").Count;
        if (words == 0 || words > 16) return false;
        if (Regex.IsMatch(s, "[!?;]\\s*$")) return false;
        if (words > 8 && Regex.IsMatch(s, "^(?:the|a|an|if|when|where|provided|notwithstanding|company|member|members|user|users)\\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(s, "\\b(?:shall|must|may|means|will|hereby)\\b", RegexOptions.IgnoreCase)) return false;
        return true;
    }

    private static double? ArticleNumberValue(string value)
    {
        var s = (value ?? string.Empty).Trim();
        if (Regex.IsMatch(s, "^[IVXLCDM]+$", RegexOptions.IgnoreCase))
        {
            var vals = new Dictionary<char, int> { ['I']=1,['V']=5,['X']=10,['L']=50,['C']=100,['D']=500,['M']=1000 };
            var total = 0; var prev = 0;
            foreach (var ch in s.ToUpperInvariant().Reverse())
            {
                var v = vals[ch]; if (v < prev) total -= v; else { total += v; prev = v; }
            }
            return total;
        }
        var m = Regex.Match(s, @"^(\d+)(?:[.-](\d+))?");
        if (!m.Success) return null;
        var a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (m.Groups[2].Success) a += double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 1000.0;
        return a;
    }

    private static HashSet<int> FalseEnglishArticleHeaderLines(string[] lines)
    {
        var markers = new List<(int Line, string Number)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = EnglishArticle.Match(lines[i].Trim());
            if (m.Success) markers.Add((i, m.Groups[1].Value));
        }
        if (markers.Count <= 2) return new();

        var keep = Enumerable.Repeat(true, markers.Count).ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            var active = Enumerable.Range(0, markers.Count).Where(i => keep[i]).ToList();
            if (active.Count <= 2) break;
            for (var pos = 1; pos + 1 < active.Count; pos++)
            {
                var i = active[pos]; var pi = active[pos - 1]; var ni = active[pos + 1];
                var p = ArticleNumberValue(markers[pi].Number);
                var c = ArticleNumberValue(markers[i].Number);
                var n = ArticleNumberValue(markers[ni].Number);
                if (p is null || c is null || n is null) continue;
                if (Math.Abs((n.Value - p.Value) - 1.0) > 1e-9) continue;
                if (!(c.Value < p.Value || c.Value > n.Value)) continue;
                var jump = Math.Min(Math.Abs(c.Value - p.Value), Math.Abs(c.Value - n.Value));
                var duplicateLater = active.Skip(pos + 1)
                    .Any(j => keep[j] && string.Equals(markers[j].Number, markers[i].Number, StringComparison.OrdinalIgnoreCase));
                if (jump >= 3 && (duplicateLater || jump >= 6))
                {
                    keep[i] = false;
                    changed = true;
                    break;
                }
            }
        }
        return Enumerable.Range(0, markers.Count).Where(i => !keep[i]).Select(i => markers[i].Line).ToHashSet();
    }

    private static List<NativeUnit> ParseLegalUnits(string text)
    {
        var units = new List<NativeUnit>();
        var body = new List<string>();
        string? header = null, number = null, title = null;
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preamble = new List<string>();
        var lines = text.Split('\n');
        var falseArticleLines = FalseEnglishArticleHeaderLines(lines);

        string CurrentSection()
        {
            var order = new[] { "part", "chapter", "장", "절", "관" };
            return string.Join(" > ", order.Where(sections.ContainsKey).Select(k => sections[k]).Distinct());
        }
        void SetSection(string level, string label)
        {
            sections[level] = label;
            if (level is "part" or "장") { sections.Remove("chapter"); sections.Remove("절"); sections.Remove("관"); }
            else if (level is "chapter" or "절") sections.Remove("관");
        }
        void FlushArticle()
        {
            if (header is null) return;
            var b = string.Join("\n", body).Trim(); body.Clear();
            units.Add(NativeUnit.Create(units.Count, number ?? string.Empty, title ?? string.Empty, header, b, CurrentSection()));
            header = number = title = null;
        }
        string? NextTitle(ref int i)
        {
            for (var j = i + 1; j < lines.Length && j <= i + 2; j++)
            {
                var cand = lines[j].Trim(' ', '\t', '|');
                if (cand.Length == 0) continue;
                if (!TitleCandidate(cand)) return null;
                i = j;
                return cand.Trim('(', ')', '[', ']', '{', '}', ' ', '\t', '|', ':', '-', '–', '—');
            }
            return null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            var eh = EnglishHierarchy.Match(line);
            var kh = KoreanHierarchy.Match(line);
            if (eh.Success || kh.Success)
            {
                FlushArticle();
                if (eh.Success)
                {
                    var level = eh.Groups["level"].Value.ToLowerInvariant();
                    var num = eh.Groups["num"].Value;
                    var t = eh.Groups["title"].Value.Trim();
                    if (t.Length == 0) t = NextTitle(ref i) ?? string.Empty;
                    SetSection(level, char.ToUpperInvariant(level[0]) + level[1..] + " " + num + (t.Length > 0 ? ". " + t : string.Empty));
                }
                else
                {
                    var level = kh.Groups["level"].Value;
                    var num = kh.Groups["num"].Value;
                    var t = (kh.Groups["p"].Value + kh.Groups["b"].Value + kh.Groups["title"].Value).Trim();
                    if (t.Length == 0) t = NextTitle(ref i) ?? string.Empty;
                    SetSection(level, $"제{num}{level}" + (t.Length > 0 ? " " + t : string.Empty));
                }
                continue;
            }

            var km = KoreanArticle.Match(line); var em = falseArticleLines.Contains(i) ? Match.Empty : EnglishArticle.Match(line);
            if (km.Success || em.Success)
            {
                FlushArticle();
                if (km.Success)
                {
                    number = km.Groups[2].Success ? $"{km.Groups[1].Value}의{km.Groups[2].Value}" : km.Groups[1].Value;
                    title = km.Groups[3].Value.Trim();
                    var tail = km.Groups[4].Value.Trim();
                    if (title.Length == 0 && tail.Length == 0) title = NextTitle(ref i) ?? string.Empty;
                    else if (title.Length == 0 && tail.Length > 0 && TitleCandidate(tail)) { title = CleanArticleTitle(tail); tail = string.Empty; }
                    header = $"제{number}조" + (title.Length > 0 ? $"({title})" : string.Empty);
                    if (tail.Length > 0) body.Add(tail);
                }
                else
                {
                    number = em.Groups[1].Value;
                    var parsed = ParseEnglishArticleRest(em.Groups[2].Value);
                    title = parsed.Title; var tail = parsed.BodyTail;
                    if (title.Length == 0 && tail.Length == 0) title = NextTitle(ref i) ?? string.Empty;
                    header = $"Article {number}" + (title.Length > 0 ? $" ({title})" : string.Empty);
                    if (tail.Length > 0) body.Add(tail);
                }
                continue;
            }

            if (header is null) preamble.Add(line); else body.Add(line);
        }
        FlushArticle();

        // Python V2.8 excludes preamble from legal article comparison once real articles exist.
        if (units.Count > 0) return units;
        foreach (var line in preamble)
        {
            if (EnglishHierarchy.IsMatch(line) || KoreanHierarchy.IsMatch(line)) continue;
            units.Add(NativeUnit.Create(units.Count, $"p{units.Count + 1}", $"문단 {units.Count + 1}", $"문단 {units.Count + 1}", line, string.Empty));
        }
        return units;
    }

    private static (string Number, string Header)? GenericHeading(string line)
    {
        var s = (line ?? string.Empty).Trim();
        if (s.Length == 0 || s.Length > 180) return null;
        var numbered = Regex.Match(s, @"^\s*((?:\d+(?:\.\d+){0,5})|(?:[IVXLCDM]+)|(?:[A-Z]))[.)]?\s+(.{1,140})$", RegexOptions.IgnoreCase);
        if (numbered.Success)
        {
            var title = numbered.Groups[2].Value.Trim();
            if (title.Length <= 110 && !Regex.IsMatch(title, @"[.!?;]\s*$"))
                return (numbered.Groups[1].Value, s);
        }

        var words = Regex.Matches(s, "[A-Za-z가-힣0-9]+").Cast<Match>().Select(m => m.Value).ToList();
        if (words.Count is >= 1 and <= 10 && s.Length <= 80 && !Regex.IsMatch(s, @"[.!?;:]\s*$"))
        {
            var latin = Regex.IsMatch(s, "[A-Za-z]");
            var uppercase = s.Any(char.IsLetter) && s.Where(char.IsLetter).All(ch => !char.IsLower(ch));
            var titleCase = latin && words.Count(w => w.Length > 0 && char.IsUpper(w[0])) >= Math.Max(1, words.Count / 2);
            if (uppercase || titleCase) return (string.Empty, s);
        }
        return null;
    }

    private static List<NativeUnit> ParseGeneralUnits(string text)
    {
        text = NativeDocumentReader.NormalizeNewlines(text ?? string.Empty);
        var paras = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (paras.Count == 0) return new();

        var units = new List<NativeUnit>();
        (string Number, string Header)? current = null;
        var body = new List<string>();
        var seq = 0;
        void Flush()
        {
            if (current is null && body.Count == 0) return;
            seq++;
            if (current is not null)
            {
                var b = string.Join("\n", body).Trim();
                var num = current.Value.Number;
                var header = current.Value.Header;
                var title = num.Length > 0
                    ? Regex.Replace(header, "^\\s*" + Regex.Escape(num) + @"[.)]?\s*", string.Empty).Trim()
                    : header;
                units.Add(NativeUnit.Create(units.Count, num.Length > 0 ? num : $"g{seq}", title, header, b, string.Empty));
            }
            else
            {
                var b = string.Join("\n", body).Trim();
                units.Add(NativeUnit.Create(units.Count, $"g{seq}", string.Empty, string.Empty, b, string.Empty));
            }
            current = null; body.Clear();
        }

        foreach (var para in paras)
        {
            var heading = GenericHeading(para);
            if (heading is not null)
            {
                Flush(); current = heading;
            }
            else
            {
                if (current is null && body.Count > 0) Flush();
                body.Add(para);
            }
        }
        Flush();
        return units;
    }

    private static List<NativeUnit?[]> BuildRows(IReadOnlyList<List<NativeUnit>> docs, int baseIndex, CancellationToken token)
    {
        var n = docs.Count;
        var baseline = docs[baseIndex];
        var rows = baseline.Select(u => { var r = new NativeUnit?[n]; r[baseIndex] = u; return r; }).ToList();
        var additions = new List<(int Slot, int Doc, NativeUnit Unit)>();

        for (var d = 0; d < n; d++)
        {
            if (d == baseIndex) continue;
            token.ThrowIfCancellationRequested();
            var matches = MatchUnitSequence(baseline, docs[d], token);
            var used = matches.Select(x => x.Other).ToHashSet();
            foreach (var match in matches) rows[match.Base][d] = docs[d][match.Other];

            foreach (var oi in Enumerable.Range(0, docs[d].Count).Where(x => !used.Contains(x)))
            {
                token.ThrowIfCancellationRequested();
                additions.Add((AdditionSlot(oi, matches, baseline.Count), d, docs[d][oi]));
            }
        }

        // Python V2.9 keeps the selected baseline as the immutable vertical axis. Comparison-only
        // additions are inserted only into the gap determined by their mapped neighbours.
        foreach (var g in additions.GroupBy(x => x.Slot).OrderByDescending(x => x.Key))
        {
            var pending = new List<NativeUnit?[]>();
            foreach (var x in g.OrderBy(x => x.Unit.Index).ThenBy(x => x.Doc))
            {
                var merge = pending.FirstOrDefault(r =>
                {
                    var ex = r.FirstOrDefault(u => u is not null);
                    return ex is not null && UnitLineageSimilarity(ex, x.Unit) >= .84;
                });
                if (merge is null) { merge = new NativeUnit?[n]; pending.Add(merge); }
                merge[x.Doc] = x.Unit;
            }
            rows.InsertRange(Math.Clamp(g.Key, 0, rows.Count), pending);
        }
        return rows;
    }

    private static int AdditionSlot(int otherIndex, IReadOnlyList<UnitMatch> matches, int baseCount)
    {
        var prev = matches.Where(x => x.Other < otherIndex).OrderByDescending(x => x.Other).FirstOrDefault();
        var next = matches.Where(x => x.Other > otherIndex).OrderBy(x => x.Other).FirstOrDefault();
        if (prev is not null && next is not null && prev.Base < next.Base) return next.Base;
        if (prev is not null) return Math.Min(baseCount, prev.Base + 1);
        if (next is not null) return Math.Max(0, next.Base);
        return baseCount;
    }

    private sealed record UnitMatch(int Base, int Other, double Score, string Mode);

    private static List<UnitMatch> MatchUnitSequence(IReadOnlyList<NativeUnit> a, IReadOnlyList<NativeUnit> b, CancellationToken token)
    {
        var articleA = Enumerable.Range(0, a.Count).Where(i => IsLegalArticle(a[i])).ToList();
        var articleB = Enumerable.Range(0, b.Count).Where(i => IsLegalArticle(b[i])).ToList();

        // Paragraph/general fallback is intentionally separate, mirroring Python's fallback.
        if (articleA.Count == 0 || articleB.Count == 0)
            return MatchGenericUnits(a, b);

        var n = articleA.Count;
        var m = articleB.Count;
        const double gap = .48;
        var dp = new double[n + 1, m + 1];
        var prev = new char[n + 1, m + 1];
        for (var i = 1; i <= n; i++) { dp[i, 0] = dp[i - 1, 0] + gap; prev[i, 0] = 'D'; }
        for (var j = 1; j <= m; j++) { dp[0, j] = dp[0, j - 1] + gap; prev[0, j] = 'I'; }

        var simCache = new Dictionary<(int, int), double>();
        double Sim(int ai, int bj)
        {
            var key = (ai, bj);
            if (!simCache.TryGetValue(key, out var s0))
            {
                s0 = UnitLineageSimilarity(a[articleA[ai]], b[articleB[bj]]);
                simCache[key] = s0;
            }
            return s0;
        }

        for (var i = 1; i <= n; i++)
        {
            token.ThrowIfCancellationRequested();
            for (var j = 1; j <= m; j++)
            {
                var sim = Sim(i - 1, j - 1);
                var matchCost = sim >= .43 ? .94 * (1.0 - sim) : 1.08;
                if (string.Equals(a[articleA[i - 1]].Number, b[articleB[j - 1]].Number, StringComparison.OrdinalIgnoreCase))
                    matchCost -= .015;
                var mc = dp[i - 1, j - 1] + matchCost;
                var dc = dp[i - 1, j] + gap;
                var ic = dp[i, j - 1] + gap;
                if (mc <= dc && mc <= ic) { dp[i, j] = mc; prev[i, j] = 'M'; }
                else if (dc <= ic) { dp[i, j] = dc; prev[i, j] = 'D'; }
                else { dp[i, j] = ic; prev[i, j] = 'I'; }
            }
        }

        var ops = new List<(char Op, int A, int B)>();
        var x = n; var y = m;
        while (x > 0 || y > 0)
        {
            var op = prev[x, y];
            if (op == 'M') { ops.Add(('M', x - 1, y - 1)); x--; y--; }
            else if (op == 'D') { ops.Add(('D', x - 1, -1)); x--; }
            else { ops.Add(('I', -1, y - 1)); y--; }
        }
        ops.Reverse();

        var mapping = new Dictionary<int, UnitMatch>();
        var usedB = new HashSet<int>();
        foreach (var op in ops)
        {
            if (op.Op != 'M') continue;
            var bi = articleA[op.A]; var oj = articleB[op.B]; var sim = Sim(op.A, op.B);
            if (sim < .43) continue;
            var mode = string.Equals(a[bi].Number, b[oj].Number, StringComparison.OrdinalIgnoreCase) ? "same" : "renumbered";
            mapping[bi] = new UnitMatch(bi, oj, sim, mode);
            usedB.Add(oj);
        }

        // Python V2.9 residual pass: high-confidence non-monotonic moves are allowed after
        // sequence alignment.  This is what keeps a genuine moved article from becoming
        // detached delete/add records.
        var residual = new List<(double Rank, int A, int B, double Sim)>();
        foreach (var bi in articleA)
        {
            if (mapping.ContainsKey(bi)) continue;
            foreach (var oj in articleB)
            {
                if (usedB.Contains(oj)) continue;
                var sim = UnitLineageSimilarity(a[bi], b[oj]);
                var tr = TitleSimilarity(a[bi], b[oj]);
                var br = BodySimilarity(a[bi], b[oj]);
                if (sim >= .70 || tr >= .82 || (tr >= .68 && br >= .48) || br >= .84)
                    residual.Add((sim + .10 * tr + .04 * br, bi, oj, sim));
            }
        }
        foreach (var r in residual.OrderByDescending(z => z.Rank))
        {
            if (mapping.ContainsKey(r.A) || usedB.Contains(r.B)) continue;
            mapping[r.A] = new UnitMatch(r.A, r.B, r.Sim, "moved");
            usedB.Add(r.B);
        }

        // Mark inverted pairs as positional moves, like the Python implementation.
        var ordered = mapping.Values.OrderBy(z => z.Base).ToList();
        var movedBase = new HashSet<int>();
        for (var i = 0; i < ordered.Count; i++)
        for (var j = i + 1; j < ordered.Count; j++)
            if (ordered[i].Other > ordered[j].Other)
            {
                movedBase.Add(ordered[i].Base); movedBase.Add(ordered[j].Base);
            }
        foreach (var bi in movedBase)
        {
            var r = mapping[bi]; mapping[bi] = r with { Mode = "moved" };
        }

        // Preamble maps only to preamble and never participates in article lineage.
        var preA = Enumerable.Range(0, a.Count).Where(i => !IsLegalArticle(a[i])).ToList();
        var preB = Enumerable.Range(0, b.Count).Where(i => !IsLegalArticle(b[i]) && !usedB.Contains(i)).ToList();
        foreach (var ai in preA)
        {
            var best = preB.Where(j => !usedB.Contains(j))
                .Select(j => (J: j, S: Similarity(a[ai].Body, b[j].Body)))
                .OrderByDescending(z => z.S).FirstOrDefault();
            if (best != default && best.S >= .48)
            {
                mapping[ai] = new UnitMatch(ai, best.J, best.S, "same"); usedB.Add(best.J);
            }
        }

        return mapping.Values.OrderBy(z => z.Base).ToList();
    }

    private static List<UnitMatch> MatchGenericUnits(IReadOnlyList<NativeUnit> a, IReadOnlyList<NativeUnit> b)
    {
        var candidates = new List<(double Rank, int A, int B, double Score)>();
        for (var i = 0; i < a.Count; i++)
        for (var j = 0; j < b.Count; j++)
        {
            var score = Similarity(a[i].Body, b[j].Body);
            if (score < .48) continue;
            var proximity = 1.0 - Math.Min(Math.Abs(i - j) / (double)Math.Max(1, Math.Max(a.Count, b.Count)), 1.0);
            candidates.Add((score + proximity * .015, i, j, score));
        }
        var usedA = new HashSet<int>(); var usedB = new HashSet<int>(); var result = new List<UnitMatch>();
        foreach (var c in candidates.OrderByDescending(z => z.Rank))
            if (usedA.Add(c.A) && usedB.Add(c.B)) result.Add(new UnitMatch(c.A, c.B, c.Score, "same"));
        return result.OrderBy(z => z.Base).ToList();
    }

    private static bool IsLegalArticle(NativeUnit u) =>
        u.Header.Length > 0 && u.Number.Length > 0 && !u.Number.StartsWith('p') && !u.Number.StartsWith('g');

    private static readonly HashSet<string> TitleStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Python 5.19.4.4 _TITLE_STOPWORDS_V17: keep this set exact.
        "the","a","an","of","for","to","and","or","etc","etcetera","on","in","regarding","concerning",
        "article","section","clause"
    };

    private static HashSet<string> TitleConcepts(string title)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches((title ?? string.Empty).ToLowerInvariant(), "[A-Za-z]+|[가-힣]+"))
        {
            var w = m.Value;
            if (TitleStopWords.Contains(w)) continue;
            if (w.StartsWith("provid") || w.StartsWith("provis")) w = "provide";
            else if (w.StartsWith("inform")) w = "information";
            else if (w.StartsWith("operat") || w.StartsWith("policy")) w = "operation";
            else if (w.StartsWith("protect") || w.StartsWith("privacy") || w.StartsWith("person")) w = "privacy";
            else if (w.StartsWith("obligat") || w.StartsWith("duti")) w = "obligation";
            else if (w.StartsWith("terminat") || w.StartsWith("cancel")) w = "termination";
            else if (w.StartsWith("compensat") || w.StartsWith("damage")) w = "damages";
            else if (w.StartsWith("confidenti") || w.StartsWith("secret")) w = "confidentiality";
            else if (w.StartsWith("use") || w.StartsWith("usage")) w = "use";
            else if (w.StartsWith("pay") || w.StartsWith("payment")) w = "payment";
            else if (w.StartsWith("applic") || w.StartsWith("scope")) w = "scope";
            else if (w.StartsWith("defin") || w.StartsWith("meaning")) w = "definition";
            else if (w.StartsWith("amend") || w.StartsWith("revis") || w.StartsWith("modif") || w.StartsWith("chang")) w = "amendment";
            else if (w.StartsWith("company") || w.StartsWith("corporat")) w = "company";
            else if (w.StartsWith("member") || w.StartsWith("user")) w = "user";
            w = Regex.Replace(w, "(등|관련|관한|관하여|사항)$", string.Empty);
            if (w.Length > 0) result.Add(w);
        }
        return result;
    }

    private static string LineageNormalize(string value)
    {
        var s = (value ?? string.Empty).Normalize(NormalizationForm.FormC);
        s = Regex.Replace(s, @"(?i)^\s*Article\s+(?:\d+(?:[-.]\d+)*[A-Za-z]?|[IVXLCDM]+)\b", " ");
        s = Regex.Replace(s, @"^\s*제\s*\d+\s*조(?:\s*의\s*\d+)?", " ");
        s = Regex.Replace(s, @"\s+", string.Empty);
        s = Regex.Replace(s, @"[^0-9A-Za-z가-힣%]", string.Empty);
        return s.ToLowerInvariant();
    }

    private static double TitleSimilarity(NativeUnit a, NativeUnit b)
    {
        var literal = IndelRatio(LineageNormalize(a.Title), LineageNormalize(b.Title));
        var ta = TitleConcepts(a.Title); var tb = TitleConcepts(b.Title);
        if (ta.Count == 0 || tb.Count == 0) return literal;
        var shared = ta.Intersect(tb).Count();
        var jac = shared / (double)Math.Max(1, ta.Union(tb).Count());
        var contain = shared / (double)Math.Max(1, Math.Min(ta.Count, tb.Count));
        return Math.Max(literal, Math.Max(jac, .94 * contain));
    }

    private static double BodySimilarity(NativeUnit a, NativeUnit b)
    {
        if (LineageNormalize(a.Body) is var na && na.Length > 0 && na == LineageNormalize(b.Body)) return 1.0;
        var seq = IndelRatio(LineageNormalize(a.Body), LineageNormalize(b.Body));
        var wa = WordSet(a.Body); var wb = WordSet(b.Body);
        if (wa.Count == 0 || wb.Count == 0) return seq;
        var shared = wa.Intersect(wb).Count();
        var jac = shared / (double)Math.Max(1, wa.Union(wb).Count());
        var contain = shared / (double)Math.Max(1, Math.Min(wa.Count, wb.Count));
        return Math.Max(seq, Math.Max(.88 * contain, .82 * jac));
    }

    private static (int Circled, int Numeric, int Korean) StructureSignature(string body)
    {
        var circled = Regex.Matches(body ?? string.Empty, "[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]").Count;
        var numeric = Regex.Matches(body ?? string.Empty, "(?m)^\\s*\\d+\\s*[.)]\\s*").Count;
        var korean = Regex.Matches(body ?? string.Empty, "(?m)^\\s*[가-하]\\s*[.)]\\s*").Count;
        return (circled, numeric, korean);
    }

    private static double StructureSimilarity(string a, string b)
    {
        var x = StructureSignature(a); var y = StructureSignature(b);
        var diff = Math.Abs(x.Circled - y.Circled) + Math.Abs(x.Numeric - y.Numeric) + Math.Abs(x.Korean - y.Korean);
        var den = Math.Max(1, x.Circled + x.Numeric + x.Korean + y.Circled + y.Numeric + y.Korean);
        return Math.Max(0.0, 1.0 - diff / (double)den);
    }

    private static double UnitLineageSimilarity(NativeUnit a, NativeUnit b)
    {
        var tr = a.Title.Length > 0 && b.Title.Length > 0 ? TitleSimilarity(a, b) : 0.0;
        var br = BodySimilarity(a, b);
        var st = StructureSimilarity(a.Body, b.Body);
        double score = a.Title.Length > 0 && b.Title.Length > 0
            ? .54 * tr + .40 * br + .06 * st
            : .90 * br + .10 * st;
        if (a.Number.Length > 0 && string.Equals(a.Number, b.Number, StringComparison.OrdinalIgnoreCase)) score += .015;
        if (tr >= .88) score = Math.Max(score, .84);
        if (br >= .88) score = Math.Max(score, .84);
        if (tr >= .72 && br >= .35) score = Math.Max(score, .74);
        return Math.Min(1.0, score);
    }

    private sealed record PartLocation(int UnitIndex, string Label, string Header);

    private static List<Dictionary<string, List<PartLocation>>> BuildGlobalPartIndex(IReadOnlyList<List<NativeUnit>> docs)
    {
        var result = new List<Dictionary<string, List<PartLocation>>>(docs.Count);
        foreach (var doc in docs)
        {
            var index = new Dictionary<string, List<PartLocation>>(StringComparer.Ordinal);
            foreach (var unit in doc)
            foreach (var part in ParseParts(unit.Body))
            {
                var key = LineageNormalize(part.Core);
                if (key.Length < 4) continue;
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<PartLocation>();
                list.Add(new PartLocation(unit.Index, part.Label, unit.HeaderOrBody));
            }
            result.Add(index);
        }
        return result;
    }

    private static bool TryUniqueMovedPart(Dictionary<string, List<PartLocation>> index, string key, int currentUnitIndex, out PartLocation location)
    {
        location = null!;
        if (key.Length < 4 || !index.TryGetValue(key, out var list) || list.Count != 1) return false;
        if (list[0].UnitIndex == currentUnitIndex) return false;
        location = list[0];
        return true;
    }

    private static ComparisonRowVm BuildRow(int id, NativeUnit?[] members, int docCount, int baseIndex, bool includeAC, bool includePunctuation,
        IReadOnlyList<Dictionary<string, List<PartLocation>>> globalParts)
    {
        var nativeMarkers = new List<NativeMarker>();
        var structural = new List<string>();
        foreach (var (oldDoc, newDoc, pair, order) in PairPlan(docCount, baseIndex, includeAC))
        {
            var old = members[oldDoc]; var revised = members[newDoc];
            if (old is null && revised is null) continue;
            if (old is null)
            {
                structural.Add($"{pair} · 상태: 조 신규");
                structural.Add($"{pair} · 조 추가: {revised!.HeaderOrBody}");
                if (revised.Header.Length > 0)
                    nativeMarkers.Add(NativeMarker.Insert(pair, order, oldDoc, newDoc, revised.Header, 0, 0, revised.Header.Length, "header", -2, -2));
                if (revised.Body.Length > 0)
                {
                    var offset = revised.Header.Length + 1;
                    nativeMarkers.Add(NativeMarker.Insert(pair, order, oldDoc, newDoc, revised.Body, 0, offset, offset + revised.Body.Length, "body", 0, 0));
                }
                continue;
            }
            if (revised is null)
            {
                structural.Add($"{pair} · 상태: 조 삭제");
                structural.Add($"{pair} · 조 삭제: {old.HeaderOrBody}");
                if (old.Header.Length > 0)
                    nativeMarkers.Add(NativeMarker.Delete(pair, order, oldDoc, newDoc, old.Header, 0, old.Header.Length, 0, "header", -2, -2));
                if (old.Body.Length > 0)
                {
                    var offset = old.Header.Length + 1;
                    nativeMarkers.Add(NativeMarker.Delete(pair, order, oldDoc, newDoc, old.Body, offset, offset + old.Body.Length, 0, "body", 0, 0));
                }
                continue;
            }
            nativeMarkers.AddRange(ComparePair(old, revised, oldDoc, newDoc, pair, order, includePunctuation, structural, globalParts));
        }
        nativeMarkers.Sort(NativeMarkerComparer.Instance);
        for (var i = 0; i < nativeMarkers.Count; i++) nativeMarkers[i].Num = i + 1;
        var markers = nativeMarkers.Select(x => x.ToViewModel()).ToList();
        var headers = new List<List<SegmentVm>>(docCount);
        var bodies = new List<List<SegmentVm>>(docCount);
        for (var d = 0; d < docCount; d++)
        {
            var member = members[d];
            headers.Add(BuildSegments(member?.Header ?? "", markers, d, "header", 0));
            bodies.Add(BuildSegments(member?.Body ?? "", markers, d, "body", (member?.Header.Length ?? 0) + 1));
        }
        var messages = structural.Distinct().Select(x => "• " + x)
            .Concat(markers.Select(m => $"[{m.Num}] {m.Pair} · {m.Message}"))
            .ToList();
        if (messages.Count == 0) messages.Add("변경 없음");
        return new ComparisonRowVm
        {
            Id = id, Members = members.Select(x => x?.ToViewModel()).ToList(), HeaderSegments = headers,
            BodySegments = bodies, Markers = markers, DisplayMessages = messages,
            Changed = messages.Count != 1 || messages[0] != "변경 없음"
        };
    }

    private static List<NativeMarker> ComparePair(
        NativeUnit old, NativeUnit revised, int oldDoc, int newDoc, string pair, int pairOrder,
        bool includePunctuation, List<string> structural,
        IReadOnlyList<Dictionary<string, List<PartLocation>>> globalParts)
    {
        var result = new List<NativeMarker>();
        var articleMoved = false; var titleChanged = false; var bodyChanged = false; var structureChanged = false;
        if (IsLegalArticle(old) && IsLegalArticle(revised))
        {
            if (!string.Equals(old.Number, revised.Number, StringComparison.OrdinalIgnoreCase))
            {
                articleMoved = true;
                var os = HeaderNumberSpan(old); var ns = HeaderNumberSpan(revised);
                if (os is not null && ns is not null)
                    result.Add(NativeMarker.ArticleNumberChange(pair, pairOrder, oldDoc, newDoc,
                        old.Number, revised.Number, os.Value.Start, os.Value.End, ns.Value.Start, ns.Value.End));
                structural.Add($"{pair} · 조 이동/재번호화: {old.Header} → {revised.Header}");
            }
            if (!SemanticEqual(CleanArticleTitle(old.Title), CleanArticleTitle(revised.Title)))
            {
                titleChanged = true;
                var ot = HeaderTitleSpan(old); var nt = HeaderTitleSpan(revised);
                result.AddRange(DiffText(old.Title, revised.Title, ot.Start, nt.Start,
                    oldDoc, newDoc, pair, pairOrder, "header", includePunctuation, -1));
                structural.Add($"{pair} · 조 제목 변경: {old.Title} → {revised.Title}");
            }
        }
        else if (!HeaderEquivalent(old, revised) && !SemanticEqual(old.Header, revised.Header) && (old.Header.Length > 0 || revised.Header.Length > 0))
        {
            titleChanged = true;
            result.AddRange(DiffText(old.Header, revised.Header, 0, 0, oldDoc, newDoc, pair, pairOrder, "header", includePunctuation, -1));
        }

        var oldParts = ParseParts(old.Body); var newParts = ParseParts(revised.Body);
        var matched = MatchParts(oldParts, newParts);
        var usedOld = matched.Select(x => x.Old).ToHashSet(); var usedNew = matched.Select(x => x.New).ToHashSet();
        var oldNodes = BuildPartHierarchy(oldParts); var newNodes = BuildPartHierarchy(newParts);
        var bodyOffsetOld = old.Header.Length + 1; var bodyOffsetNew = revised.Header.Length + 1;
        foreach (var msg in DescribePartStructure(oldParts, newParts, oldNodes, newNodes))
        { structureChanged = true; structural.Add($"{pair} · {msg}"); }

        foreach (var m in matched.OrderBy(x => x.Old))
        {
            var ap = oldParts[m.Old]; var bp = newParts[m.New];
            var parentMoved = !ParentsEquivalent(m.Old, m.New, oldNodes, newNodes, matched);
            var labelsDiffer = IsStructuralLabel(ap.Label) && IsStructuralLabel(bp.Label) && ap.Label != bp.Label;
            var sameOrdinal = labelsDiffer && PartOrdinal(ap.Label) is int ao && PartOrdinal(bp.Label) is int bo && ao == bo;
            var levelChanged = oldNodes[m.Old].Level != newNodes[m.New].Level;
            var notationChanged = labelsDiffer && sameOrdinal;
            var labelMoved = labelsDiffer && !sameOrdinal;
            var itemMoved = parentMoved || labelMoved;
            var contentChanged = !SemanticEqual(ap.Core, bp.Core);
            if (labelsDiffer)
                result.Add(NativeMarker.StructuralChange(pair, pairOrder, oldDoc, newDoc, ap.Label, bp.Label,
                    bodyOffsetOld + ap.Start, bodyOffsetOld + ap.Start + ap.Label.Length,
                    bodyOffsetNew + bp.Start, bodyOffsetNew + bp.Start + bp.Label.Length, "body", m.Old, -2));
            if (notationChanged || levelChanged)
                structural.Add($"{pair} · 항/호 표기{(levelChanged ? "/레벨" : string.Empty)} 변경: {ap.Label} → {bp.Label}");
            else if (itemMoved)
                structural.Add($"{pair} · {(contentChanged ? "항/호 이동+변경" : "항/호 이동")}: {ap.Label} → {bp.Label}");
            if (!contentChanged) continue;
            bodyChanged = true;
            var wholePlainRewrite = ap.Label == "본문" && bp.Label == "본문" && oldParts.Count == 1 && newParts.Count == 1 && m.Score < .72;
            var wholeItemRewrite = IsStructuralLabel(ap.Label) && IsStructuralLabel(bp.Label) && m.Score <= .56 && !HasReviewAnchors(ap.Core, bp.Core) && !HasStableEdgeContext(ap.Core, bp.Core);
            if (wholePlainRewrite || wholeItemRewrite)
                result.Add(NativeMarker.Change(pair, pairOrder, oldDoc, newDoc, ap.Core.Trim(), bp.Core.Trim(),
                    bodyOffsetOld + ap.CoreStart, bodyOffsetOld + ap.CoreEnd,
                    bodyOffsetNew + bp.CoreStart, bodyOffsetNew + bp.CoreEnd, "body", m.Old, 0));
            else
                result.AddRange(DiffText(ap.Core, bp.Core, bodyOffsetOld + ap.CoreStart, bodyOffsetNew + bp.CoreStart,
                    oldDoc, newDoc, pair, pairOrder, "body", includePunctuation, m.Old));
        }

        foreach (var i in Enumerable.Range(0, oldParts.Count).Where(i => !usedOld.Contains(i)))
        {
            var part = oldParts[i]; if (string.IsNullOrWhiteSpace(part.Core)) continue;
            var key = LineageNormalize(part.Core);
            if (TryUniqueMovedPart(globalParts[newDoc], key, revised.Index, out var to))
            { structural.Add($"{pair} · 항/호 이동: {old.HeaderOrBody} {part.Label} → {to.Header} {to.Label}"); continue; }
            bodyChanged = true;
            var newAnchor = bodyOffsetNew + StructuralGapAnchor(oldParts, newParts, matched, i, sourceIsNew: false);
            result.Add(NativeMarker.Delete(pair, pairOrder, oldDoc, newDoc, part.Core.Trim(),
                bodyOffsetOld + part.Start, bodyOffsetOld + part.CoreEnd, newAnchor, "body", i));
        }
        foreach (var j in Enumerable.Range(0, newParts.Count).Where(j => !usedNew.Contains(j)))
        {
            var part = newParts[j]; if (string.IsNullOrWhiteSpace(part.Core)) continue;
            var key = LineageNormalize(part.Core);
            if (TryUniqueMovedPart(globalParts[oldDoc], key, old.Index, out var from))
            { structural.Add($"{pair} · 항/호 이동: {from.Header} {from.Label} → {revised.HeaderOrBody} {part.Label}"); continue; }
            bodyChanged = true;
            var oldAnchor = bodyOffsetOld + StructuralGapAnchor(newParts, oldParts, matched, j, sourceIsNew: true);
            result.Add(NativeMarker.Insert(pair, pairOrder, oldDoc, newDoc, part.Core.Trim(), oldAnchor,
                bodyOffsetNew + part.Start, bodyOffsetNew + part.CoreEnd, "body", j));
        }
        result = CoalesceLocationReplacements(result, oldDoc, newDoc);
        var substantive = titleChanged || bodyChanged || structureChanged;
        if (articleMoved) structural.Insert(0, $"{pair} · 상태: {(substantive ? "조 이동+변경" : "조 이동")}");
        else if (structureChanged) structural.Insert(0, $"{pair} · 상태: 조 구조변경");
        else if (titleChanged || bodyChanged) structural.Insert(0, $"{pair} · 상태: 조 변경");
        return result;
    }

    private static List<NativeMarker> CoalesceLocationReplacements(List<NativeMarker> events, int oldDoc, int newDoc)
    {
        static MarkerEndpointVm? Endpoint(NativeMarker e, int doc) => e.Endpoints.FirstOrDefault(x => x.TargetDoc == doc);
        static int SpanDistance(int pos, MarkerEndpointVm ep)
        {
            var lo = Math.Min(ep.CharStart, ep.CharEnd); var hi = Math.Max(ep.CharStart, ep.CharEnd);
            return pos >= lo && pos <= hi ? 0 : Math.Min(Math.Abs(pos - lo), Math.Abs(pos - hi));
        }
        static bool Lexical(string value) => (value ?? string.Empty).Any(char.IsLetterOrDigit);

        var deletes = events.Select((e, i) => (Event: e, Index: i))
            .Where(x => x.Event.Action == "삭제" && x.Event.RelativeDoc == newDoc && Lexical(x.Event.Text)).ToList();
        var adds = events.Select((e, i) => (Event: e, Index: i))
            .Where(x => x.Event.Action == "추가" && x.Event.RelativeDoc == newDoc && Lexical(x.Event.Text)).ToList();
        if (deletes.Count == 0 || adds.Count == 0) return events;

        var candidates = new List<(int Score, int D, int A, NativeMarker Del, NativeMarker Add, MarkerEndpointVm OldEp, MarkerEndpointVm NewEp)>();
        foreach (var d in deletes)
        {
            var dOld = Endpoint(d.Event, oldDoc); var dNew = Endpoint(d.Event, newDoc);
            if (dOld is null || dNew is null || dOld.CharEnd <= dOld.CharStart || dNew.CharEnd != dNew.CharStart) continue;
            foreach (var a in adds)
            {
                if (d.Event.Part != a.Event.Part) continue;
                var aOld = Endpoint(a.Event, oldDoc); var aNew = Endpoint(a.Event, newDoc);
                if (aOld is null || aNew is null || aOld.CharEnd != aOld.CharStart || aNew.CharEnd <= aNew.CharStart) continue;
                if (SemanticEqual(d.Event.Text, a.Event.Text)) continue;
                var oldDist = SpanDistance(aOld.CharStart, dOld);
                var newDist = SpanDistance(dNew.CharStart, aNew);
                var tolerance = Math.Max(6, Math.Min(28, (int)Math.Round((d.Event.Text.Length + a.Event.Text.Length) * .10)));
                var sameStruct = d.Event.ItemOrder == a.Event.ItemOrder;
                if (oldDist > tolerance || newDist > tolerance)
                {
                    if (!sameStruct || oldDist > tolerance * 2 || newDist > tolerance * 2) continue;
                }
                candidates.Add((oldDist + newDist - (sameStruct ? 4 : 0), d.Index, a.Index, d.Event, a.Event, dOld, aNew));
            }
        }
        if (candidates.Count == 0) return events;

        var usedD = new HashSet<int>(); var usedA = new HashSet<int>();
        var replacements = new Dictionary<int, NativeMarker>();
        foreach (var c in candidates.OrderBy(x => x.Score).ThenBy(x => x.D).ThenBy(x => x.A))
        {
            if (!usedD.Add(c.D) || !usedA.Add(c.A)) continue;
            replacements[c.D] = NativeMarker.Change(c.Add.Pair, c.Add.PairOrder, oldDoc, newDoc,
                c.Del.Text.Trim(), c.Add.Text.Trim(), c.OldEp.CharStart, c.OldEp.CharEnd,
                c.NewEp.CharStart, c.NewEp.CharEnd, c.Add.Part,
                Math.Min(c.Del.ItemOrder, c.Add.ItemOrder), Math.Min(c.Del.HunkOrder, c.Add.HunkOrder));
        }
        if (replacements.Count == 0) return events;
        var output = new List<NativeMarker>();
        for (var i = 0; i < events.Count; i++)
        {
            if (replacements.TryGetValue(i, out var replacement)) output.Add(replacement);
            else if (!usedA.Contains(i)) output.Add(events[i]);
        }
        return output;
    }

    private sealed record EventPiece(int Start, int End, string Text);

    private static List<EventPiece> EventParts(string fullText, int start, int end)
    {
        // Python V3.4 _v34_event_parts: keep a logical phrase together, splitting only a
        // detached leading sentence delimiter separated from lexical text by whitespace.
        start = Math.Clamp(start, 0, fullText.Length); end = Math.Clamp(end, start, fullText.Length);
        while (start < end && char.IsWhiteSpace(fullText[start])) start++;
        while (end > start && char.IsWhiteSpace(fullText[end - 1])) end--;
        if (end <= start) return new();
        var raw = fullText[start..end];
        if (!raw.Any(char.IsLetterOrDigit)) return new() { new EventPiece(start, end, raw) };
        var m = Regex.Match(raw, @"^([.!?;:]+)(\s+)(.+)$", RegexOptions.Singleline);
        if (!m.Success) return new() { new EventPiece(start, end, raw) };
        var punctEnd = start + m.Groups[1].Length;
        var lexicalStart = punctEnd + m.Groups[2].Length;
        return new()
        {
            new EventPiece(start, punctEnd, fullText[start..punctEnd]),
            new EventPiece(lexicalStart, end, fullText[lexicalStart..end])
        };
    }

    private static List<NativeMarker> DiffText(
        string oldText, string newText, int oldBase, int newBase, int oldDoc, int newDoc,
        string pair, int pairOrder, string part, bool includePunctuation, int itemOrder)
    {
        if (SemanticEqual(oldText, newText)) return new();
        var korean = TryKoreanReviewDiff(oldText, newText, oldBase, newBase, oldDoc, newDoc, pair, pairOrder, part, itemOrder);
        if (korean is not null) return korean;
        var a = LexTokens(oldText); var b = LexTokens(newText);
        var matches = MatcherPairs(a.Select(x => TokenKey(x.Text)).ToArray(), b.Select(x => TokenKey(x.Text)).ToArray());
        // Common glue words must not split one logical rewrite into dozens of markers.
        // If the fine LCS would create a marker storm, keep only meaningful equal runs as
        // anchors (the/of/and/및/또는 etc. stay inside the surrounding replacement hunk).
        if (RawChangeGapCount(a.Count, b.Count, matches) >= 4)
            matches = StrongReviewMatches(a, b, matches);
        var anchors = new List<(int A, int B)> { (-1, -1) };
        anchors.AddRange(matches);
        anchors.Add((a.Count, b.Count));
        var result = new List<NativeMarker>();

        for (var k = 0; k < anchors.Count - 1; k++)
        {
            var left = anchors[k]; var right = anchors[k + 1];
            var ai = left.A + 1; var aj = right.A; var bi = left.B + 1; var bj = right.B;
            if (ai >= aj && bi >= bj) continue;
            var oldStart = ai < a.Count ? a[ai].Start : (left.A >= 0 ? a[left.A].End : 0);
            var oldEnd = aj > ai ? a[aj - 1].End : oldStart;
            var newStart = bi < b.Count ? b[bi].Start : (left.B >= 0 ? b[left.B].End : 0);
            var newEnd = bj > bi ? b[bj - 1].End : newStart;
            var ot = oldText[oldStart..oldEnd].Trim();
            var nt = newText[newStart..newEnd].Trim();
            if (!includePunctuation && (ot.Length == 0 || IsPunctuationOnly(ot)) && (nt.Length == 0 || IsPunctuationOnly(nt))) continue;

            if (ot.Length > 0 && nt.Length > 0)
            {
                var (oTrim, nTrim, oDelta, nDelta) = TrimSharedHangulPrefix(ot, nt);
                result.Add(NativeMarker.Change(
                    pair, pairOrder, oldDoc, newDoc, oTrim, nTrim,
                    oldBase + oldStart + oDelta, oldBase + oldEnd,
                    newBase + newStart + nDelta, newBase + newEnd,
                    part, itemOrder, k));
            }
            else if (ot.Length > 0)
            {
                foreach (var piece in EventParts(oldText, oldStart, oldEnd))
                {
                    if (!includePunctuation && IsPunctuationOnly(piece.Text)) continue;
                    var anchor = MapBoundary(oldText, newText, piece.Start);
                    result.Add(NativeMarker.Delete(
                        pair, pairOrder, oldDoc, newDoc, piece.Text.Trim(),
                        oldBase + piece.Start, oldBase + piece.End, newBase + anchor, part, itemOrder, k));
                }
            }
            else if (nt.Length > 0)
            {
                foreach (var piece in EventParts(newText, newStart, newEnd))
                {
                    if (!includePunctuation && IsPunctuationOnly(piece.Text)) continue;
                    var anchor = MapBoundary(newText, oldText, piece.Start);
                    result.Add(NativeMarker.Insert(
                        pair, pairOrder, oldDoc, newDoc, piece.Text.Trim(),
                        oldBase + anchor, newBase + piece.Start, newBase + piece.End, part, itemOrder, k));
                }
            }
        }
        return result;
    }

    private static (string Old, string New, int OldDelta, int NewDelta) TrimSharedHangulPrefix(string oldText, string newText)
    {
        var n = Math.Min(oldText.Length, newText.Length); var i = 0;
        while (i < n && oldText[i] == newText[i]) i++;
        if (i < 2) return (oldText, newText, 0, 0);
        var prefix = oldText[..i];
        if (prefix.Any(char.IsWhiteSpace) || prefix.Count(ch => ch is >= '가' and <= '힣') < 2)
            return (oldText, newText, 0, 0);
        var o = oldText[i..]; var nn = newText[i..];
        if (string.IsNullOrWhiteSpace(o) || string.IsNullOrWhiteSpace(nn)) return (oldText, newText, 0, 0);
        var ol = o.Length - o.TrimStart().Length; var nl = nn.Length - nn.TrimStart().Length;
        return (o.TrimStart(), nn.TrimStart(), i + ol, i + nl);
    }

    private sealed record WordSpan(int Start, int End, string Text);
    private sealed record WordOp(string Kind, int? OldIndex, int? NewIndex);

    private static List<NativeMarker>? TryKoreanReviewDiff(
        string oldText, string newText, int oldBase, int newBase, int oldDoc, int newDoc,
        string pair, int pairOrder, string part, int itemOrder)
    {
        // Exact V5.19.4.4k gate: only compact Korean edits are morph-refined.
        if (Math.Max(oldText.Length, newText.Length) > 96 ||
            !KoreanPresence.IsMatch(oldText) || !KoreanPresence.IsMatch(newText))
            return null;

        var a = KoreanLexToken.Matches(oldText)
            .Select(m => new WordSpan(m.Index, m.Index + m.Length, m.Value)).ToList();
        var b = KoreanLexToken.Matches(newText)
            .Select(m => new WordSpan(m.Index, m.Index + m.Length, m.Value)).ToList();
        if (a.Count == 0 || b.Count == 0) return null;

        const double gap = .68;
        var dp = new double[a.Count + 1, b.Count + 1];
        var prev = new char[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++) { dp[i, 0] = dp[i - 1, 0] + gap; prev[i, 0] = 'D'; }
        for (var j = 1; j <= b.Count; j++) { dp[0, j] = dp[0, j - 1] + gap; prev[0, j] = 'I'; }
        for (var i = 1; i <= a.Count; i++)
        for (var j = 1; j <= b.Count; j++)
        {
            var (_, cost) = KoreanWordRelation(a[i - 1].Text, b[j - 1].Text);
            var match = dp[i - 1, j - 1] + cost;
            var del = dp[i - 1, j] + gap;
            var ins = dp[i, j - 1] + gap;
            if (match <= del && match <= ins) { dp[i, j] = match; prev[i, j] = 'M'; }
            else if (del <= ins) { dp[i, j] = del; prev[i, j] = 'D'; }
            else { dp[i, j] = ins; prev[i, j] = 'I'; }
        }

        var ops = new List<WordOp>();
        var x = a.Count; var y = b.Count;
        while (x > 0 || y > 0)
        {
            var op = prev[x, y];
            if (op == 'M')
            {
                var (kind, _) = KoreanWordRelation(a[x - 1].Text, b[y - 1].Text);
                ops.Add(new WordOp(kind, x - 1, y - 1)); x--; y--;
            }
            else if (op == 'D') { ops.Add(new WordOp("delete", x - 1, null)); x--; }
            else { ops.Add(new WordOp("insert", null, y - 1)); y--; }
        }
        ops.Reverse();
        var exact = ops.Count(o => o.Kind == "equal");
        var fuzzy = ops.Count(o => o.Kind == "fuzzy");
        if (exact < 2 || fuzzy < 2) return null;

        var result = new List<NativeMarker>();
        for (var k = 0; k < ops.Count; k++)
        {
            var op = ops[k];
            if (op.Kind == "equal") continue;
            if (op.Kind == "fuzzy")
            {
                var ai = op.OldIndex!.Value; var bj = op.NewIndex!.Value;
                var oldStart = a[ai].Start; var oldEnd = a[ai].End;
                var newStart = b[bj].Start; var newEnd = b[bj].End;

                if (k + 1 < ops.Count && ops[k + 1].Kind == "delete")
                {
                    var dai = ops[k + 1].OldIndex!.Value;
                    if (dai == ai + 1 && !AlnumPresence.IsMatch(oldText[a[ai].End..a[dai].Start]))
                    {
                        oldEnd = a[dai].End;
                        k++;
                    }
                }
                else if (k + 1 < ops.Count && ops[k + 1].Kind == "insert")
                {
                    var ibj = ops[k + 1].NewIndex!.Value;
                    if (ibj == bj + 1 && !AlnumPresence.IsMatch(newText[b[bj].End..b[ibj].Start]))
                    {
                        newEnd = b[ibj].End;
                        k++;
                    }
                }

                var oldRaw = oldText[oldStart..oldEnd];
                var newRaw = newText[newStart..newEnd];
                var (ot, nt, od, nd) = TrimSharedHangulPrefix(oldRaw, newRaw);
                result.Add(NativeMarker.Change(pair, pairOrder, oldDoc, newDoc,
                    ot.Trim(), nt.Trim(), oldBase + oldStart + od, oldBase + oldEnd,
                    newBase + newStart + nd, newBase + newEnd, part, itemOrder, k));
            }
            else if (op.Kind == "delete")
            {
                var ai = op.OldIndex!.Value;
                var os = a[ai].Start; var oe = a[ai].End;
                var anchor = MapBoundary(oldText, newText, os);
                result.Add(NativeMarker.Delete(pair, pairOrder, oldDoc, newDoc,
                    oldText[os..oe], oldBase + os, oldBase + oe, newBase + anchor, part, itemOrder, k));
            }
            else if (op.Kind == "insert")
            {
                var bj = op.NewIndex!.Value;
                var ns = b[bj].Start; var ne = b[bj].End;
                var anchor = MapBoundary(newText, oldText, ns);
                result.Add(NativeMarker.Insert(pair, pairOrder, oldDoc, newDoc,
                    newText[ns..ne], oldBase + anchor, newBase + ns, newBase + ne, part, itemOrder, k));
            }
            else
            {
                // Python returns None if an unrelated substitution survived the DP alignment.
                return null;
            }
        }
        return result.Count is >= 2 and <= 8 ? result : null;
    }

    private static (string Kind, double Cost) KoreanWordRelation(string a, string b)
    {
        if (SemanticEqual(a, b)) return ("equal", 0);
        var cut = SharedHangulPrefixLength(a, b);
        if (cut >= 2) return ("fuzzy", .24 + .03 * Math.Abs(a.Length - b.Length));
        return ("none", 1.45);
    }

    private static int SharedHangulPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length); var i = 0;
        while (i < n && a[i] == b[i]) i++;
        if (i < 2) return 0;
        var p = a[..i];
        if (p.Any(char.IsWhiteSpace) || p.Count(ch => ch is >= '가' and <= '힣') < 2) return 0;
        return i;
    }

    private static string RecoverEmbeddedExplicitItemBoundaries(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        // Word can flatten an explicit list into one paragraph.  Python's historical recovery
        // only recognized 2+ spaces, but real DOCX text commonly has exactly one space:
        //   ... follows. (1) Company ... (2) Member ... (3) ...
        // Do not split on one isolated "(1)" reference.  A single-space recovery is accepted
        // only for a consecutive enumerator sequence (normally 1,2,3...) and therefore keeps
        // prose references such as "Article 5 (1)" intact.
        var chars = text.ToCharArray();
        var candidates = new List<(int Boundary, int GapLength, int LineStart, bool AtLineStart, string Family, int Ordinal)>();

        static (string Family, int Ordinal)? EnumeratorKey(string label)
        {
            if (label.Length == 1)
            {
                const string circled = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
                var ci = circled.IndexOf(label[0]);
                if (ci >= 0) return ("circled", ci + 1);
            }
            if (label.Length >= 3 && label[0] == '(' && label[^1] == ')' &&
                int.TryParse(label[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var pn))
                return ("paren-number", pn);
            if (char.IsDigit(label[0]))
            {
                var digits = new string(label.TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    return ($"number-{label[^1]}", n);
            }
            if (label.Length == 2 && char.IsAsciiLetter(label[0]) && label[1] is '.' or ')')
                return ($"alpha-{label[1]}", char.ToUpperInvariant(label[0]) - 'A' + 1);
            if (label.Length == 2 && label[1] is '.' or ')')
            {
                const string korean = "가나다라마바사아자차카타파하";
                var ki = korean.IndexOf(label[0]);
                if (ki >= 0) return ($"korean-{label[1]}", ki + 1);
            }
            return null;
        }

        foreach (Match m in EmbeddedExplicitItemCandidate.Matches(text))
        {
            var label = m.Groups["label"].Value;
            var key = EnumeratorKey(label);
            if (key is null) continue;
            var gap = m.Groups["gap"];
            var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            var atLineStart = gap.Index == lineStart;
            var boundary = atLineStart || gap.Length == 0 ? -1 : gap.Index + gap.Length - 1;
            candidates.Add((boundary, gap.Length, lineStart, atLineStart, key.Value.Family, key.Value.Ordinal));

            // Preserve the mature Python behavior: 2+ spaces are already a strong structural boundary.
            if (!atLineStart && gap.Length >= 2 && boundary >= 0)
                chars[boundary] = '\n';
        }

        foreach (var lineGroup in candidates.GroupBy(x => x.LineStart))
        {
            var items = lineGroup.OrderBy(x => x.Boundary < 0 ? x.LineStart : x.Boundary).ToList();
            for (var i = 0; i < items.Count;)
            {
                var j = i + 1;
                while (j < items.Count && items[j].Family == items[j - 1].Family &&
                       items[j].Ordinal == items[j - 1].Ordinal + 1)
                    j++;

                var count = j - i;
                var first = items[i];
                var firstHasStrongLead = first.AtLineStart;
                if (!firstHasStrongLead && first.Boundary >= 0)
                {
                    var q = first.Boundary - 1;
                    while (q >= first.LineStart && char.IsWhiteSpace(text[q])) q--;
                    firstHasStrongLead = q < first.LineStart || text[q] is '.' or ':' or ';' or '!' or '?' or ')' or ']' or '}';
                }

                // For one-space inline recovery require a real sequence.  Three inline markers
                // are strong evidence by themselves; two are accepted when the sequence begins
                // at the physical line start.  This avoids splitting ordinary single references.
                var qualifies = (count >= 3 && firstHasStrongLead) || (count >= 2 && first.AtLineStart);
                if (qualifies)
                {
                    for (var k = i; k < j; k++)
                        if (items[k].Boundary >= 0) chars[items[k].Boundary] = '\n';
                }
                i = j;
            }
        }
        return new string(chars);
    }

    private static string StructuralParseView(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            // Word documents frequently contain invisible formatting characters such as
            // ZWSP/ZWNJ/ZWJ/WORD JOINER/BOM before legal enumerators.  Replacing them rather
            // than removing them preserves every source offset used by review markers.
            if (CharUnicodeInfo.GetUnicodeCategory(chars[i]) == UnicodeCategory.Format)
                chars[i] = ' ';
        }
        return new string(chars);
    }

    private static List<NativePart> ParseParts(string text)
    {
        text = NativeDocumentReader.NormalizeNewlines(text ?? string.Empty);
        var scanText = RecoverEmbeddedExplicitItemBoundaries(StructuralParseView(text));
        var matches = ExplicitItem.Matches(scanText).Cast<Match>().ToList();
        if (matches.Count == 0)
        {
            var st = 0; var en = text.Length;
            while (st < en && char.IsWhiteSpace(text[st])) st++;
            while (en > st && char.IsWhiteSpace(text[en - 1])) en--;
            return en <= st
                ? new List<NativePart>()
                : new List<NativePart> { new("본문", st, en, st, en, text[st..en]) };
        }

        var parts = new List<NativePart>();
        var first = matches[0];
        if (first.Index > 0 && !string.IsNullOrWhiteSpace(text[..first.Index]))
        {
            var st = 0; var en = first.Index;
            while (st < en && char.IsWhiteSpace(text[st])) st++;
            while (en > st && char.IsWhiteSpace(text[en - 1])) en--;
            if (en > st) parts.Add(new NativePart("본문", st, en, st, en, text[st..en]));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var segStart = m.Groups["label"].Index;
            var segEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var coreStart = m.Index + m.Length;
            var coreEnd = segEnd;
            while (coreStart < coreEnd && char.IsWhiteSpace(text[coreStart])) coreStart++;
            while (coreEnd > coreStart && char.IsWhiteSpace(text[coreEnd - 1])) coreEnd--;
            parts.Add(new NativePart(
                m.Groups["label"].Value,
                segStart,
                segEnd,
                coreStart,
                coreEnd,
                text[coreStart..coreEnd]));
        }
        return parts;
    }

    private static List<PartMatch> MatchParts(IReadOnlyList<NativePart> a, IReadOnlyList<NativePart> b)
    {
        var nodesA = BuildPartHierarchy(a); var nodesB = BuildPartHierarchy(b);
        var structuredA = Enumerable.Range(0, a.Count).Where(i => IsStructuralLabel(a[i].Label)).ToList();
        var structuredB = Enumerable.Range(0, b.Count).Where(i => IsStructuralLabel(b[i].Label)).ToList();
        var oneSided = (structuredA.Count >= 2 && structuredB.Count == 0) || (structuredB.Count >= 2 && structuredA.Count == 0);
        if (oneSided)
        {
            // The article lineage has already been established. If only one side changes from
            // prose to an explicit list (or the reverse), keep the unique prose body paired even
            // when it was heavily rewritten. Explicit items stay unmatched and are reported as
            // structural additions/deletions instead of being folded into the body replacement.
            var oldBodies = Enumerable.Range(0, a.Count).Where(i => a[i].Label == "본문").ToList();
            var newBodies = Enumerable.Range(0, b.Count).Where(i => b[i].Label == "본문").ToList();
            if (oldBodies.Count == 1 && newBodies.Count == 1)
            {
                var score = PartSimilarity(a[oldBodies[0]], b[newBodies[0]]);
                return new List<PartMatch> { new(oldBodies[0], newBodies[0], Math.Max(score, .50)) };
            }
            return new List<PartMatch>();
        }
        var result = new List<PartMatch>(); var usedA = new HashSet<int>(); var usedB = new HashSet<int>();
        void Take(int i,int j,double score){ if(usedA.Add(i)&&usedB.Add(j)) result.Add(new PartMatch(i,j,score)); }
        for (var i=0;i<a.Count;i++)
        {
            var norm=LineageNormalize(a[i].Core); if(norm.Length==0) continue;
            var cand=Enumerable.Range(0,b.Count).Where(j=>!usedB.Contains(j)&&LineageNormalize(b[j].Core)==norm).OrderBy(j=>Math.Abs(i-j)).FirstOrDefault(-1);
            if(cand>=0) Take(i,cand,1.0);
        }
        for (var i=0;i<a.Count;i++)
        {
            if(usedA.Contains(i)) continue; var head=DefinitionHead(a[i].Core); if(head.Length==0) continue;
            var cand=Enumerable.Range(0,b.Count).Where(j=>!usedB.Contains(j)&&DefinitionHead(b[j].Core)==head)
                .Select(j=>(J:j,S:PartSimilarity(a[i],b[j]))).OrderByDescending(x=>x.S).ThenBy(x=>Math.Abs(i-x.J)).FirstOrDefault();
            if(cand!=default) Take(i,cand.J,Math.Max(.90,cand.S));
        }
        // Parent-first hierarchy matching. At every level, a strong content move is resolved
        // BEFORE same-enumerator fallback. This prevents an inserted new 3. from stealing the old
        // 3. whose real descendant is new 4.; after moves are consumed, unchanged sibling shapes
        // still guarantee 1->1 ... 5->5 even for a complete wording rewrite.
        for (var level = 1; level <= 4; level++)
        {
            var moveCandidates = new List<(double Rank, int A, int B, double Score)>();
            foreach (var i in Enumerable.Range(0, a.Count).Where(i => !usedA.Contains(i) && nodesA[i].Level == level && IsStructuralLabel(a[i].Label)))
            foreach (var j in Enumerable.Range(0, b.Count).Where(j => !usedB.Contains(j) && nodesB[j].Level == level && IsStructuralLabel(b[j].Label)))
            {
                if (a[i].Label == b[j].Label) continue;
                if (!ParentsEquivalent(i, j, nodesA, nodesB, result)) continue;
                var score = PartSimilarity(a[i], b[j]);
                var contain = Containment(a[i].Core, b[j].Core);
                var childSupport = MatchedChildSupport(i, j, nodesA, nodesB, result);
                if (score >= .74 || contain >= .88 ||
                    childSupport >= 2 ||
                    (childSupport >= 1 && score >= .55))
                {
                    var rank = Math.Max(score, .94 * contain) + Math.Min(.12, childSupport * .06);
                    moveCandidates.Add((rank, i, j, score));
                }
            }
            foreach (var x in moveCandidates.OrderByDescending(x => x.Rank).ThenBy(x => Math.Abs(x.A - x.B)))
                if (!usedA.Contains(x.A) && !usedB.Contains(x.B)) Take(x.A, x.B, Math.Max(x.Score, x.Rank));

            // Same logical ordinal with a different drafting convention (① ↔ 1., (1) ↔ 1., etc.).
            // Treat the sibling sequence as stronger structure evidence than the glyph family.
            foreach (var i in Enumerable.Range(0, a.Count).Where(i => !usedA.Contains(i) && nodesA[i].Level == level && IsStructuralLabel(a[i].Label)))
            {
                var ordinal = PartOrdinal(a[i].Label); if (ordinal is null) continue;
                var candidates = Enumerable.Range(0, b.Count)
                    .Where(j => !usedB.Contains(j) && nodesB[j].Level == level && IsStructuralLabel(b[j].Label) &&
                                PartOrdinal(b[j].Label) == ordinal && a[i].Label != b[j].Label &&
                                ParentsEquivalent(i, j, nodesA, nodesB, result))
                    .Select(j => (J: j, S: PartSimilarity(a[i], b[j]), C: Containment(a[i].Core, b[j].Core),
                                  Shape: OrdinalSiblingShapeEquivalent(i, j, a, b, nodesA, nodesB),
                                  Edge: HasStableEdgeContext(a[i].Core, b[j].Core)))
                    .OrderByDescending(x => x.Shape).ThenByDescending(x => Math.Max(x.S, .94 * x.C)).ToList();
                if (candidates.Count == 0) continue;
                var cand = candidates[0];
                if ((cand.Shape && (cand.S >= .20 || cand.C >= .35 || cand.Edge)) || cand.S >= .62 || cand.C >= .76)
                    Take(i, cand.J, Math.Max(cand.S, cand.Shape ? .58 : .52));
            }

            foreach (var i in Enumerable.Range(0, a.Count).Where(i => !usedA.Contains(i) && nodesA[i].Level == level))
            {
                if (!IsStructuralLabel(a[i].Label)) continue;
                var candidates = Enumerable.Range(0, b.Count)
                    .Where(j => !usedB.Contains(j) && nodesB[j].Level == level && b[j].Label == a[i].Label &&
                                ParentsEquivalent(i, j, nodesA, nodesB, result))
                    .Select(j => (J: j, S: PartSimilarity(a[i], b[j]), Shape: SiblingShapeEquivalent(i, j, a, b, nodesA, nodesB)))
                    .OrderByDescending(x => x.Shape).ThenByDescending(x => x.S).ToList();
                if (candidates.Count == 0) continue;
                var cand = candidates[0];
                if (cand.Shape || cand.S >= .18 || HasStableEdgeContext(a[i].Core, b[cand.J].Core))
                    Take(i, cand.J, Math.Max(cand.S, .52));
            }
        }
        // If one document omits an intermediate 항/호 layer, compare adjacent hierarchy levels
        // only after normal same-level matching has been exhausted.  The skipped parent must be
        // unmatched, which prevents a healthy nested hierarchy from being flattened accidentally.
        var crossLevel = new List<(double Rank, int A, int B, double Score)>();
        foreach (var i in Enumerable.Range(0, a.Count).Where(i => !usedA.Contains(i) && IsStructuralLabel(a[i].Label)))
        foreach (var j in Enumerable.Range(0, b.Count).Where(j => !usedB.Contains(j) && IsStructuralLabel(b[j].Label)))
        {
            if (Math.Abs(nodesA[i].Level - nodesB[j].Level) != 1) continue;
            if (!CollapsedLevelParentsEquivalent(i, j, nodesA, nodesB, result)) continue;
            var oa = PartOrdinal(a[i].Label); var ob = PartOrdinal(b[j].Label);
            var score = PartSimilarity(a[i], b[j]); var contain = Containment(a[i].Core, b[j].Core);
            var sameOrdinal = oa.HasValue && ob.HasValue && oa.Value == ob.Value;
            var edge = HasStableEdgeContext(a[i].Core, b[j].Core);
            if ((sameOrdinal && (score >= .34 || contain >= .50 || edge)) || score >= .74 || contain >= .86)
            {
                var rank = Math.Max(score, .94 * contain) + (sameOrdinal ? .08 : 0.0);
                crossLevel.Add((rank, i, j, score));
            }
        }
        foreach (var x in crossLevel.OrderByDescending(x => x.Rank).ThenBy(x => Math.Abs(x.A - x.B)))
            if (!usedA.Contains(x.A) && !usedB.Contains(x.B)) Take(x.A, x.B, Math.Max(x.Score, .56));

        foreach(var i in Enumerable.Range(0,a.Count).Where(i=>!usedA.Contains(i)&&a[i].Label=="본문"))
        {
            var cand=Enumerable.Range(0,b.Count).Where(j=>!usedB.Contains(j)&&b[j].Label=="본문")
                .Select(j=>(J:j,S:PartSimilarity(a[i],b[j]))).OrderByDescending(x=>x.S).FirstOrDefault();
            if(cand!=default&&cand.S>=.20) Take(i,cand.J,Math.Max(cand.S,.50));
        }
        var residual=new List<(double Rank,int A,int B,double Score)>();
        for(var i=0;i<a.Count;i++)
        {
            if(usedA.Contains(i)) continue;
            for(var j=0;j<b.Count;j++)
            {
                if(usedB.Contains(j)) continue;
                var ha=DefinitionHead(a[i].Core); var hb=DefinitionHead(b[j].Core); if(ha.Length>0&&hb.Length>0&&ha!=hb) continue;
                var score=PartSimilarity(a[i],b[j]); var parentOk=ParentsEquivalent(i,j,nodesA,nodesB,result); var contain=Containment(a[i].Core,b[j].Core);
                var threshold=parentOk?.68:.82; if(score>=threshold||(!parentOk&&contain>=.88)) residual.Add((Math.Max(score,.94*contain),i,j,score));
            }
        }
        foreach(var x in residual.OrderByDescending(x=>x.Rank).ThenBy(x=>Math.Abs(x.A-x.B)))
            if(!usedA.Contains(x.A)&&!usedB.Contains(x.B)) Take(x.A,x.B,Math.Max(x.Score,x.Rank));
        return result.OrderBy(x=>x.Old).ToList();
    }

    private static string PartFamily(string label)
    {
        if(label=="본문"||label.Length==0) return "body";
        if(label.Length==1&&"①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳".Contains(label[0])) return "circled";
        if(Regex.IsMatch(label,@"^\(\d+\)$")) return "paren-number";
        if(Regex.IsMatch(label,@"^\d+[.)]$")) return "number";
        if(Regex.IsMatch(label,@"^[A-Za-z][.)]$")) return "alpha";
        if(Regex.IsMatch(label,@"^[가-하][.)]$")) return "korean";
        return "other";
    }
    private static int? PartOrdinal(string label)
    {
        if (string.IsNullOrEmpty(label) || label == "본문") return null;
        const string circled = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
        if (label.Length == 1)
        {
            var ci = circled.IndexOf(label[0]);
            if (ci >= 0) return ci + 1;
        }
        var m = Regex.Match(label, @"^\((\d+)\)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pn)) return pn;
        m = Regex.Match(label, @"^(\d+)[.)]$");
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        if (Regex.IsMatch(label, @"^[A-Za-z][.)]$")) return char.ToUpperInvariant(label[0]) - 'A' + 1;
        if (Regex.IsMatch(label, @"^[가-하][.)]$"))
        {
            const string korean = "가나다라마바사아자차카타파하";
            var ki = korean.IndexOf(label[0]);
            if (ki >= 0) return ki + 1;
        }
        return null;
    }

    private static List<PartNode> BuildPartHierarchy(IReadOnlyList<NativePart> parts)
    {
        var nodes=new List<PartNode>(parts.Count); var currentL1=-1; var currentL2=-1;
        for(var i=0;i<parts.Count;i++)
        {
            var family=PartFamily(parts[i].Label); if(family=="body"){nodes.Add(new PartNode(i,0,-1,family));continue;}
            var level=1; var parent=-1;
            if(family=="circled"){currentL1=i;currentL2=-1;}
            else if(family=="number")
            { if(currentL1>=0&&nodes[currentL1].Family=="circled"){level=2;parent=currentL1;currentL2=i;} else {currentL1=i;currentL2=-1;} }
            else if(family=="paren-number")
            { if(currentL1>=0&&nodes[currentL1].Family is "number" or "circled"){level=2;parent=currentL1;currentL2=i;} else {currentL1=i;currentL2=-1;} }
            else if(family is "alpha" or "korean")
            { if(currentL2>=0&&nodes[currentL2].Family is "number" or "paren-number"){level=3;parent=currentL2;} else if(currentL1>=0&&nodes[currentL1].Family!=family){level=2;parent=currentL1;} else {currentL1=i;currentL2=-1;} }
            else if(currentL1>=0){level=2;parent=currentL1;} else currentL1=i;
            nodes.Add(new PartNode(i,level,parent,family));
        }
        return nodes;
    }
    private static bool ParentsEquivalent(int ai,int bj,IReadOnlyList<PartNode>a,IReadOnlyList<PartNode>b,IReadOnlyList<PartMatch>matches)
    { var pa=a[ai].Parent;var pb=b[bj].Parent;if(pa<0||pb<0)return pa==pb;return matches.Any(m=>m.Old==pa&&m.New==pb); }
    private static bool ParentPairEquivalent(int pa, int pb, IReadOnlyList<PartMatch> matches)
    {
        if (pa < 0 || pb < 0) return pa == pb;
        return matches.Any(m => m.Old == pa && m.New == pb);
    }

    private static bool CollapsedLevelParentsEquivalent(int ai, int bj, IReadOnlyList<PartNode> a, IReadOnlyList<PartNode> b, IReadOnlyList<PartMatch> matches)
    {
        var la = a[ai].Level; var lb = b[bj].Level;
        if (Math.Abs(la - lb) != 1) return false;
        if (la > lb)
        {
            var extraParent = a[ai].Parent;
            if (extraParent < 0 || matches.Any(m => m.Old == extraParent)) return false;
            return ParentPairEquivalent(a[extraParent].Parent, b[bj].Parent, matches);
        }
        else
        {
            var extraParent = b[bj].Parent;
            if (extraParent < 0 || matches.Any(m => m.New == extraParent)) return false;
            return ParentPairEquivalent(a[ai].Parent, b[extraParent].Parent, matches);
        }
    }

    private static bool OrdinalSiblingShapeEquivalent(int ai, int bj, IReadOnlyList<NativePart> a, IReadOnlyList<NativePart> b, IReadOnlyList<PartNode> na, IReadOnlyList<PartNode> nb)
    {
        var xa = na[ai]; var xb = nb[bj];
        var la = na.Where(x => x.Level == xa.Level && x.Parent == xa.Parent && IsStructuralLabel(a[x.Index].Label))
            .Select(x => PartOrdinal(a[x.Index].Label)).ToList();
        var lb = nb.Where(x => x.Level == xb.Level && x.Parent == xb.Parent && IsStructuralLabel(b[x.Index].Label))
            .Select(x => PartOrdinal(b[x.Index].Label)).ToList();
        return la.Count >= 2 && la.Count == lb.Count && la.All(x => x.HasValue) && lb.All(x => x.HasValue) &&
               la.Select(x => x!.Value).SequenceEqual(lb.Select(x => x!.Value));
    }
    private static int MatchedChildSupport(int ai, int bj, IReadOnlyList<PartNode> a, IReadOnlyList<PartNode> b,
        IReadOnlyList<PartMatch> matches) =>
        matches.Count(m => m.Old >= 0 && m.Old < a.Count && m.New >= 0 && m.New < b.Count &&
                           a[m.Old].Parent == ai && b[m.New].Parent == bj);
    private static bool SiblingShapeEquivalent(int ai,int bj,IReadOnlyList<NativePart>a,IReadOnlyList<NativePart>b,IReadOnlyList<PartNode>na,IReadOnlyList<PartNode>nb)
    {
        var xa=na[ai];var xb=nb[bj];
        var la=na.Where(x=>x.Level==xa.Level&&x.Parent==xa.Parent&&x.Family==xa.Family).Select(x=>a[x.Index].Label).ToList();
        var lb=nb.Where(x=>x.Level==xb.Level&&x.Parent==xb.Parent&&x.Family==xb.Family).Select(x=>b[x.Index].Label).ToList();
        return la.Count==lb.Count&&la.SequenceEqual(lb,StringComparer.Ordinal);
    }
    private static string LabelSummary(IEnumerable<NativePart> parts)
    { var labels=parts.Where(p=>IsStructuralLabel(p.Label)).Select(p=>p.Label).ToList(); if(labels.Count==0)return "본문"; if(labels.Count<=5)return string.Join(", ",labels); return $"{labels[0]}~{labels[^1]} ({labels.Count}개)"; }
    private static List<string> DescribePartStructure(IReadOnlyList<NativePart> oldParts,IReadOnlyList<NativePart> newParts,IReadOnlyList<PartNode> oldNodes,IReadOnlyList<PartNode> newNodes)
    {
        var messages=new List<string>();var oa=oldParts.Where(p=>IsStructuralLabel(p.Label)).ToList();var nb=newParts.Where(p=>IsStructuralLabel(p.Label)).ToList();
        if(oa.Count>=2&&nb.Count==0)messages.Add($"항/호 구조 변경: {LabelSummary(oa)} 열거 구조가 본문으로 통합");
        else if(nb.Count>=2&&oa.Count==0)messages.Add($"항/호 구조 변경: 본문이 {LabelSummary(nb)} 열거 구조로 분할");
        else if(oa.Count>0&&nb.Count>0)
        {
            var od=oldNodes.Count==0?0:oldNodes.Max(x=>x.Level);var nd=newNodes.Count==0?0:newNodes.Max(x=>x.Level);
            var of=oldNodes.Where(x=>x.Level>0).Select(x=>x.Family).ToHashSet();var nf=newNodes.Where(x=>x.Level>0).Select(x=>x.Family).ToHashSet();
            if(od!=nd||!of.SetEquals(nf))messages.Add($"항/호 구조 변경: {LabelSummary(oa)} → {LabelSummary(nb)}");
        }
        return messages;
    }

    private static bool HasReviewAnchors(string a, string b)
    {
        var aw = ReviewWordToken.Matches(a ?? string.Empty)
            .Select(m => TokenKey(m.Value)).Where(x => x.Length > 0).ToList();
        var bw = ReviewWordToken.Matches(b ?? string.Empty)
            .Select(m => TokenKey(m.Value)).Where(x => x.Length > 0).ToList();
        var shared = aw.Intersect(bw).Count(x => !ReviewStopWords.Contains(x));
        if (shared >= 2) return true;
        var fuzzy = 0;
        foreach (var x in aw)
        foreach (var y in bw)
        {
            if (SharedHangulPrefixLength(x, y) >= 2 && ++fuzzy >= 2) return true;
        }
        return false;
    }

    private static bool HasStableEdgeContext(string a, string b)
    {
        var x = Normalize(a); var y = Normalize(b);
        if (Math.Min(x.Length, y.Length) < 40) return false;
        var prefix = 0;
        while (prefix < Math.Min(x.Length, y.Length) && x[prefix] == y[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(x.Length, y.Length) - prefix &&
               x[x.Length - 1 - suffix] == y[y.Length - 1 - suffix]) suffix++;
        // A substantial unchanged beginning/end is strong evidence that a heavily rewritten
        // same-number legal item is still the same item. Short generic endings such as "행위"
        // never reach this threshold, so unrelated same-number list entries stay additions/deletions.
        return prefix >= 18 || suffix >= 18;
    }

    private static double PartSimilarity(NativePart a, NativePart b)
    {
        var ca = a.Core.Trim(); var cb = b.Core.Trim();
        if (ca.Length == 0 || cb.Length == 0) return 0;
        if (LineageNormalize(ca) == LineageNormalize(cb)) return 1;
        var seq = SequenceRatio(LineageNormalize(ca), LineageNormalize(cb));
        var aw = WordSet(ca); var bw = WordSet(cb);
        var shared = aw.Intersect(bw).Count();
        var union = aw.Union(bw).Count();
        var jac = shared / (double)Math.Max(1, union);
        var contain = shared / (double)Math.Max(1, Math.Min(aw.Count, bw.Count));
        var score = Math.Max(.68 * seq + .32 * jac, .90 * contain);
        if (a.Label == b.Label && a.Label != "본문") score = Math.Min(1, score + .08);
        return score;
    }

    private static List<TokenSpan> LexTokens(string text)
    {
        var result = new List<TokenSpan>();
        foreach (Match m in DiffToken.Matches(text ?? ""))
            result.Add(new TokenSpan(m.Index, m.Index + m.Length, m.Value));
        return result;
    }

    private static readonly HashSet<string> ReviewStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","of","to","in","on","at","by","for","from","with","as","is","are","was","were","be","been","being",
        "this","that","these","those","it","its","their","there","here","such","any","all","other","between","within","through","under",
        "및","또는","그","이","저","의","에","에서","으로","로","를","을","은","는","가","이"
    };

    private static int RawChangeGapCount(int aCount, int bCount, IReadOnlyList<(int A, int B)> matches)
    {
        var count = 0; var pa = -1; var pb = -1;
        foreach (var m in matches.Append((aCount, bCount)))
        {
            if (m.Item1 > pa + 1 || m.Item2 > pb + 1) count++;
            pa = m.Item1; pb = m.Item2;
        }
        return count;
    }

    private static List<(int A, int B)> StrongReviewMatches(
        IReadOnlyList<TokenSpan> a, IReadOnlyList<TokenSpan> b, IReadOnlyList<(int A, int B)> matches)
    {
        var kept = new List<(int A, int B)>();
        for (var i = 0; i < matches.Count;)
        {
            var start = i; var end = i + 1;
            while (end < matches.Count && matches[end].A == matches[end - 1].A + 1 && matches[end].B == matches[end - 1].B + 1)
                end++;
            var words = matches.Skip(start).Take(end - start)
                .Select(x => a[x.A].Text)
                .Where(x => x.Any(char.IsLetterOrDigit))
                .ToList();
            var content = words.Where(x => !ReviewStopWords.Contains(TokenKey(x))).ToList();
            var contentChars = content.Sum(x => x.Length);
            var hangulChars = words.Sum(x => x.Count(ch => ch is >= '가' and <= '힣'));
            var strong = words.Count >= 4 ||
                         (content.Count >= 2 && contentChars >= 13) ||
                         (words.Count >= 2 && content.Any(x => x.Length >= 11)) ||
                         (words.Count >= 2 && hangulChars >= 5);
            if (strong)
                for (var k = start; k < end; k++) kept.Add(matches[k]);
            i = end;
        }
        return kept;
    }

    private static List<(int A, int B)> LcsMatches(string[] a, string[] b)
    {
        if ((long)(a.Length + 1) * (b.Length + 1) > 4_000_000L)
            return MatcherPairs(a, b);
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var result = new List<(int, int)>(); var x = 0; var y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add((x, y)); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) x++; else y++;
        }
        return result;
    }

    private static List<(int A, int B, int Size)> MatcherBlocks(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < b.Count; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = new List<int>();
            list.Add(j);
        }
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, a.Count, 0, b.Count));
        var found = new List<(int A, int B, int Size)>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var bestI = alo; var bestJ = blo; var bestSize = 0;
            var j2len = new Dictionary<int, int>();
            var next = new Dictionary<int, int>();
            for (var i = alo; i < ahi; i++)
            {
                next.Clear();
                if (b2j.TryGetValue(a[i], out var js))
                {
                    foreach (var j in js)
                    {
                        if (j < blo) continue;
                        if (j >= bhi) break;
                        var k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                        next[j] = k;
                        if (k > bestSize) { bestI = i - k + 1; bestJ = j - k + 1; bestSize = k; }
                    }
                }
                (j2len, next) = (next, j2len);
            }
            if (bestSize == 0) continue;
            found.Add((bestI, bestJ, bestSize));
            if (alo < bestI && blo < bestJ) queue.Push((alo, bestI, blo, bestJ));
            if (bestI + bestSize < ahi && bestJ + bestSize < bhi)
                queue.Push((bestI + bestSize, ahi, bestJ + bestSize, bhi));
        }
        found.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        var merged = new List<(int A, int B, int Size)>();
        foreach (var block in found)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.A + last.Size == block.A && last.B + last.Size == block.B)
                {
                    merged[^1] = (last.A, last.B, last.Size + block.Size);
                    continue;
                }
            }
            merged.Add(block);
        }
        merged.Add((a.Count, b.Count, 0));
        return merged;
    }

    private static List<(int A, int B)> MatcherPairs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var result = new List<(int A, int B)>();
        foreach (var block in MatcherBlocks(a, b))
            for (var k = 0; k < block.Size; k++) result.Add((block.A + k, block.B + k));
        return result;
    }

    private static List<(string Tag, int A1, int A2, int B1, int B2)> MatcherOpcodes(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var result = new List<(string, int, int, int, int)>();
        var i = 0; var j = 0;
        foreach (var (ai, bj, size) in MatcherBlocks(a, b))
        {
            var tag = i < ai && j < bj ? "replace" : i < ai ? "delete" : j < bj ? "insert" : string.Empty;
            if (tag.Length > 0) result.Add((tag, i, ai, j, bj));
            if (size > 0) result.Add(("equal", ai, ai + size, bj, bj + size));
            i = ai + size; j = bj + size;
        }
        return result;
    }

    private static List<TokenSpan> BoundaryLexSpans(string text) =>
        BoundaryLexToken.Matches(text ?? string.Empty)
            .Select(m => new TokenSpan(m.Index, m.Index + m.Length, m.Value)).ToList();

    private static int BaseMapBoundary(string src, string dst, int pos)
    {
        src ??= string.Empty; dst ??= string.Empty;
        pos = Math.Clamp(pos, 0, src.Length);
        var insideWord = pos > 0 && pos < src.Length && BoundaryWordChar(src[pos - 1]) && BoundaryWordChar(src[pos]);
        if (!insideWord)
        {
            var a = BoundaryLexSpans(src); var b = BoundaryLexSpans(dst);
            if (a.Count > 0 && b.Count > 0)
            {
                var ak = a.Select(x => LineageNormalize(x.Text)).ToArray();
                var bk = b.Select(x => LineageNormalize(x.Text)).ToArray();
                var map = new Dictionary<int, int>();
                foreach (var block in MatcherBlocks(ak, bk))
                    for (var k = 0; k < block.Size; k++) map[block.A + k] = block.B + k;
                var left = Enumerable.Range(0, a.Count).Where(i => a[i].End <= pos && map.ContainsKey(i)).ToList();
                var right = Enumerable.Range(0, a.Count).Where(i => a[i].Start >= pos && map.ContainsKey(i)).ToList();
                int? li = left.Count > 0 ? left.OrderByDescending(i => a[i].End).First() : null;
                int? ri = right.Count > 0 ? right.OrderBy(i => a[i].Start).First() : null;
                if (li is int l && ri is int r)
                {
                    var dl = map[l]; var dr = map[r];
                    if (dl <= dr && b[dl].End <= b[dr].Start) return Math.Clamp(b[dl].End, 0, dst.Length);
                }
                if (li is int l2) return Math.Clamp(b[map[l2]].End, 0, dst.Length);
                if (ri is int r2) return Math.Clamp(b[map[r2]].Start, 0, dst.Length);
            }
        }

        var ac = src.Select(ch => ch.ToString()).ToArray();
        var bc = dst.Select(ch => ch.ToString()).ToArray();
        foreach (var (tag, i1, i2, j1, j2) in MatcherOpcodes(ac, bc))
        {
            if (tag == "equal" && i1 <= pos && pos <= i2)
                return Math.Clamp(j1 + Math.Min(pos - i1, j2 - j1), 0, dst.Length);
            if (i1 <= pos && pos <= i2)
            {
                if (i2 == i1) return Math.Clamp(j1, 0, dst.Length);
                var frac = (pos - i1) / (double)Math.Max(1, i2 - i1);
                return Math.Clamp((int)Math.Round(j1 + frac * (j2 - j1)), 0, dst.Length);
            }
            if (pos < i1) return Math.Clamp(j1, 0, dst.Length);
        }
        return dst.Length;
    }

    private static bool BoundaryWordChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_' || ch is >= '가' and <= '힣';

    private static int MapBoundary(string src, string dst, int pos)
    {
        src ??= string.Empty; dst ??= string.Empty;
        pos = Math.Clamp(pos, 0, src.Length);
        var mapped = BaseMapBoundary(src, dst, pos);

        // Python V5.19.4.3 final wrapper: when the source boundary follows sentence
        // punctuation, move the counterpart across only punctuation/whitespace in the
        // destination. Never consume the next lexical word.
        var left = pos;
        while (left > 0 && !BoundaryWordChar(src[left - 1])) left--;
        var separator = src[left..pos];
        if (separator.Length == 0 || !SentenceSeparator.IsMatch(separator))
            return mapped;
        var q = Math.Clamp(mapped, 0, dst.Length);
        while (q < dst.Length && !BoundaryWordChar(dst[q])) q++;
        return q;
    }

    private static int StructuralGapAnchor(
        IReadOnlyList<NativePart> sourceParts,
        IReadOnlyList<NativePart> targetParts,
        IReadOnlyList<PartMatch> matches,
        int sourceIndex,
        bool sourceIsNew)
    {
        var mapped = matches
            .Select(m => sourceIsNew ? (m.New, m.Old) : (m.Old, m.New))
            .OrderBy(x => x.Item1)
            .ToList();
        var next = mapped.FirstOrDefault(x => x.Item1 > sourceIndex, (-1, -1));
        if (next.Item1 >= 0 && next.Item2 >= 0 && next.Item2 < targetParts.Count)
            return targetParts[next.Item2].Start;
        var previous = mapped.LastOrDefault(x => x.Item1 < sourceIndex, (-1, -1));
        if (previous.Item1 >= 0 && previous.Item2 >= 0 && previous.Item2 < targetParts.Count)
            return targetParts[previous.Item2].End;
        return 0;
    }

    private static List<SegmentVm> BuildSegments(string text, IReadOnlyList<MarkerVm> markers, int doc, string part, int offset)
    {
        if (text.Length == 0) return new();
        var flags = new int[text.Length]; // 1 delete, 2 insert
        foreach (var marker in markers.Where(x => x.Part == part))
        {
            foreach (var ep in marker.Endpoints.Where(x => x.TargetDoc == doc && x.CharEnd > x.CharStart))
            {
                var start = Math.Clamp(ep.CharStart - offset, 0, text.Length);
                var end = Math.Clamp(ep.CharEnd - offset, start, text.Length);
                var role = marker.Action == "삭제" ? 1 : marker.Action == "추가" ? 2 : (doc == marker.TargetDoc ? 2 : 1);
                for (var i = start; i < end; i++) flags[i] |= role;
            }
        }
        var result = new List<SegmentVm>(); var p = 0;
        while (p < text.Length)
        {
            var f = flags[p]; var q = p + 1; while (q < text.Length && flags[q] == f) q++;
            result.Add(new SegmentVm { Text = text[p..q], Style = f == 1 ? "delete" : f == 2 ? "insert" : f == 3 ? "both" : "normal" });
            p = q;
        }
        return result;
    }

    private static IEnumerable<(int Old, int New, string Pair, int Order)> PairPlan(int count, int baseIndex, bool includeAC)
    {
        if (count == 2)
        {
            var old = baseIndex == 0 ? 0 : 1;
            yield return (old, 1 - old, "A↔B", 0); yield break;
        }
        yield return (0, 1, "A↔B", 0);
        yield return (1, 2, "B↔C", 1);
        if (includeAC) yield return (0, 2, "A↔C", 2);
    }

    private static void ApplySectionHeaders(IReadOnlyList<ComparisonRowVm> rows, int count)
    {
        var previous = new string?[count];
        foreach (var row in rows)
        {
            var labels = new List<string?>();
            for (var i = 0; i < count; i++)
            {
                var section = row.Members.Count > i ? row.Members[i]?.Section : null;
                if (!string.IsNullOrWhiteSpace(section) && section != previous[i]) { labels.Add(section); previous[i] = section; }
                else labels.Add(null);
            }
            row.SectionHeaders = labels;
        }
    }

    internal static string Normalize(string value)
    {
        value = (value ?? string.Empty).Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(value.Length);
        var lexical = new StringBuilder();
        var pendingSpace = false;

        void FlushLexical()
        {
            if (lexical.Length == 0) return;
            sb.Append(lexical.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant());
            lexical.Clear();
        }

        foreach (var ch in value)
        {
            // Ignore formatting/variation characters with no review value.
            if (ch is '\u200b' or '\u200c' or '\u200d' or '\u2060' or '\ufeff' ||
                ch is >= '\ufe00' and <= '\ufe0f')
                continue;

            if (char.IsWhiteSpace(ch))
            {
                FlushLexical();
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            var category = char.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                lexical.Append(ch);
            }
            else
            {
                FlushLexical();
                // Punctuation/symbols are intentionally kept code-point exact so the
                // Include punctuation option can still report real punctuation changes.
                sb.Append(ch);
            }
        }
        FlushLexical();
        return sb.ToString().Trim();
    }

    private static string CanonicalVisibleText(string value)
    {
        // Python V5.18.9 semantic no-op guard is deliberately separate from matching
        // normalization: preserve visible case/punctuation, ignore only invisible formatting
        // controls, Unicode decomposition and whitespace-family differences. FE00-FE0F are
        // also ignored because Word can surface variation selectors with no review value.
        value = (value ?? string.Empty).Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            var category = char.GetUnicodeCategory(ch);
            if (ch is '\u200b' or '\u200c' or '\u200d' or '\u2060' or '\ufeff' ||
                ch is >= '\ufe00' and <= '\ufe0f' || category == UnicodeCategory.Format)
                continue;
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    internal static bool SemanticEqual(string a, string b) =>
        string.Equals(CanonicalVisibleText(a), CanonicalVisibleText(b), StringComparison.Ordinal);
    private static string TokenKey(string value) => Normalize(value);
    private static string DefinitionHead(string value)
    {
        var m = QuotedHead.Match(value ?? string.Empty); return m.Success ? Normalize(m.Groups[1].Value) : string.Empty;
    }
    private static HashSet<string> WordSet(string value) =>
        WordSetToken.Matches((value ?? string.Empty).ToLowerInvariant())
            .Select(x => x.Value).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);

    private static double IndelRatio(string a, string b)
    {
        a ??= string.Empty; b ??= string.Empty;
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        // RapidFuzz Indel.normalized_similarity = 2 * LCS(a,b) / (|a|+|b|).
        // Use the Hyyro bit-parallel LCS recurrence so article alignment keeps Python's
        // scoring without the O(n*m) character matrix.
        if (b.Length > a.Length) (a, b) = (b, a);
        if (b.Length <= 64)
        {
            var masks64 = new Dictionary<char, ulong>();
            for (var j = 0; j < b.Length; j++)
            {
                var bit = 1UL << j;
                masks64[b[j]] = masks64.TryGetValue(b[j], out var cur) ? cur | bit : bit;
            }
            var row64 = 0UL;
            var all64 = b.Length == 64 ? ulong.MaxValue : (1UL << b.Length) - 1UL;
            foreach (var ch in a)
            {
                var m = masks64.TryGetValue(ch, out var mask) ? mask : 0UL;
                var x = row64 | m;
                var y = (row64 << 1) | 1UL;
                row64 = x & ~(x - y) & all64;
            }
            var lcs64 = BitOperations.PopCount(row64);
            return 2.0 * lcs64 / Math.Max(1, a.Length + b.Length);
        }

        var masks = new Dictionary<char, BigInteger>();
        for (var j = 0; j < b.Length; j++)
        {
            var bit = BigInteger.One << j;
            masks[b[j]] = masks.TryGetValue(b[j], out var cur) ? cur | bit : bit;
        }
        var row = BigInteger.Zero;
        var allMask = (BigInteger.One << b.Length) - BigInteger.One;
        foreach (var ch in a)
        {
            var m = masks.TryGetValue(ch, out var mask) ? mask : BigInteger.Zero;
            var x = row | m;
            var y = (row << 1) | BigInteger.One;
            row = x & (~(x - y) & allMask);
        }
        var bytes = row.ToByteArray(isUnsigned: true, isBigEndian: false);
        long lcs = 0;
        foreach (var by in bytes) lcs += BitOperations.PopCount((uint)by);
        return 2.0 * lcs / Math.Max(1, a.Length + b.Length);
    }

    private static double SequenceRatio(string a, string b)
    {
        a ??= string.Empty; b ??= string.Empty;
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        // Port of difflib.SequenceMatcher(..., autojunk=False).ratio() for character sequences.
        var b2j = new Dictionary<char, List<int>>();
        for (var j = 0; j < b.Length; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = new List<int>();
            list.Add(j);
        }
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, a.Length, 0, b.Length));
        var matches = 0;
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var bestI = alo; var bestJ = blo; var bestSize = 0;
            var j2len = new Dictionary<int, int>();
            var next = new Dictionary<int, int>();
            for (var i = alo; i < ahi; i++)
            {
                next.Clear();
                if (b2j.TryGetValue(a[i], out var js))
                {
                    foreach (var j in js)
                    {
                        if (j < blo) continue;
                        if (j >= bhi) break;
                        var k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                        next[j] = k;
                        if (k > bestSize) { bestI = i - k + 1; bestJ = j - k + 1; bestSize = k; }
                    }
                }
                (j2len, next) = (next, j2len);
            }
            if (bestSize == 0) continue;
            matches += bestSize;
            if (alo < bestI && blo < bestJ) queue.Push((alo, bestI, blo, bestJ));
            var ai2 = bestI + bestSize; var bj2 = bestJ + bestSize;
            if (ai2 < ahi && bj2 < bhi) queue.Push((ai2, ahi, bj2, bhi));
        }
        return 2.0 * matches / (a.Length + b.Length);
    }

    internal static double Similarity(string a, string b)
    {
        var x = Normalize(a); var y = Normalize(b);
        if (x == y) return 1; if (x.Length == 0 || y.Length == 0) return 0;
        var aa = SimilarityToken.Matches(x).Select(m => m.Value).ToArray();
        var bb = SimilarityToken.Matches(y).Select(m => m.Value).ToArray();
        var lcs = LcsMatches(aa, bb).Count;
        return 2.0 * lcs / Math.Max(1, aa.Length + bb.Length);
    }

    private static double Containment(string a,string b)
    { var x=WordSet(a);var y=WordSet(b);if(x.Count==0||y.Count==0)return 0.0;return x.Intersect(y).Count()/(double)Math.Max(1,Math.Min(x.Count,y.Count)); }

    private static bool IsPunctuationOnly(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Where(x => !char.IsWhiteSpace(x)).All(x => !char.IsLetterOrDigit(x));
    private static bool IsStructuralLabel(string label) => label != "본문" && label.Length > 0;

    private sealed record NativePart(string Label, int Start, int End, int CoreStart, int CoreEnd, string Core);
    private sealed record PartNode(int Index, int Level, int Parent, string Family);
    private sealed record PartMatch(int Old, int New, double Score);
    private sealed record TokenSpan(int Start, int End, string Text);

    private sealed class NativeUnit
    {
        public int Index { get; init; }
        public string Number { get; init; } = "";
        public string Title { get; init; } = "";
        public string Header { get; init; } = "";
        public string Body { get; init; } = "";
        public string Section { get; init; } = "";
        public string Text => Header.Length == 0 ? Body : Body.Length == 0 ? Header : Header + "\n" + Body;
        public string HeaderOrBody => Header.Length > 0 ? Header : Body;
        public string NormalizedBody => Normalize(Body);
        public string NormalizedText => Normalize(Text);
        public static NativeUnit Create(int index, string number, string title, string header, string body, string section) =>
            new() { Index = index, Number = number, Title = title, Header = header, Body = body, Section = section };
        public MemberVm ToViewModel() => new()
        {
            Index = Index, Kind = "article", Number = Number, Title = Title, Header = Header,
            Body = Body, Text = Text, Section = Section
        };
    }

    private sealed class NativeMarker
    {
        public int Num { get; set; }
        public int RelativeDoc { get; init; }
        public int TargetDoc { get; init; }
        public string Action { get; init; } = "";
        public string Text { get; init; } = "";
        public int CharStart { get; init; }
        public int CharEnd { get; init; }
        public string Part { get; init; } = "body";
        public string Message { get; init; } = "";
        public string Pair { get; init; } = "";
        public int PairOrder { get; init; }
        public int ItemOrder { get; init; }
        public int HunkOrder { get; init; }
        public bool StructuralNumber { get; init; }
        public List<MarkerEndpointVm> Endpoints { get; init; } = new();

        public MarkerVm ToViewModel() => new()
        {
            Num = Num, RelativeDoc = RelativeDoc, TargetDoc = TargetDoc, Action = Action, Text = Text,
            CharStart = CharStart, CharEnd = CharEnd, Part = Part, Message = Message, Label = Pair, Pair = Pair,
            StructuralNumber = StructuralNumber, Endpoints = Endpoints
        };

        public static NativeMarker ArticleNumberChange(string pair, int order, int oldDoc, int newDoc, string oldText, string newText,
            int oldStart, int oldEnd, int newStart, int newEnd) => new()
        {
            Pair = pair, PairOrder = order, RelativeDoc = newDoc, TargetDoc = newDoc, Action = "변경", Text = newText,
            CharStart = newStart, CharEnd = newEnd, Part = "header", ItemOrder = -1, HunkOrder = -2,
            StructuralNumber = true,
            Message = $"조 번호 변경: “{oldText}” → “{newText}”",
            Endpoints = new() { new() { TargetDoc = oldDoc, CharStart = oldStart, CharEnd = oldEnd }, new() { TargetDoc = newDoc, CharStart = newStart, CharEnd = newEnd } }
        };

        public static NativeMarker StructuralChange(string pair, int order, int oldDoc, int newDoc, string oldText, string newText,
            int oldStart, int oldEnd, int newStart, int newEnd, string part, int itemOrder, int hunkOrder) => new()
        {
            Pair = pair, PairOrder = order, RelativeDoc = newDoc, TargetDoc = newDoc, Action = "변경", Text = newText,
            CharStart = newStart, CharEnd = newEnd, Part = part, ItemOrder = itemOrder, HunkOrder = hunkOrder,
            StructuralNumber = true,
            Message = $"항/호 번호·위치 변경: “{oldText}” → “{newText}”",
            Endpoints = new() { new() { TargetDoc = oldDoc, CharStart = oldStart, CharEnd = oldEnd }, new() { TargetDoc = newDoc, CharStart = newStart, CharEnd = newEnd } }
        };

        public static NativeMarker Change(string pair, int order, int oldDoc, int newDoc, string oldText, string newText,
            int oldStart, int oldEnd, int newStart, int newEnd, string part, int itemOrder, int hunkOrder) => new()
        {
            Pair = pair, PairOrder = order, RelativeDoc = newDoc, TargetDoc = newDoc, Action = "변경", Text = newText,
            CharStart = newStart, CharEnd = newEnd, Part = part, ItemOrder = itemOrder, HunkOrder = hunkOrder,
            Message = $"변경: “{oldText}” → “{newText}”",
            Endpoints = new() { new() { TargetDoc = oldDoc, CharStart = oldStart, CharEnd = oldEnd }, new() { TargetDoc = newDoc, CharStart = newStart, CharEnd = newEnd } }
        };

        public static NativeMarker Delete(string pair, int order, int oldDoc, int newDoc, string text,
            int oldStart, int oldEnd, int newAnchor, string part, int itemOrder, int hunkOrder = 999) => new()
        {
            Pair = pair, PairOrder = order, RelativeDoc = newDoc, TargetDoc = oldDoc, Action = "삭제", Text = text,
            CharStart = oldStart, CharEnd = oldEnd, Part = part, ItemOrder = itemOrder, HunkOrder = hunkOrder,
            Message = $"삭제: “{text}”",
            Endpoints = new() { new() { TargetDoc = oldDoc, CharStart = oldStart, CharEnd = oldEnd }, new() { TargetDoc = newDoc, CharStart = newAnchor, CharEnd = newAnchor } }
        };

        public static NativeMarker Insert(string pair, int order, int oldDoc, int newDoc, string text,
            int oldAnchor, int newStart, int newEnd, string part, int itemOrder, int hunkOrder = 999) => new()
        {
            Pair = pair, PairOrder = order, RelativeDoc = newDoc, TargetDoc = newDoc, Action = "추가", Text = text,
            CharStart = newStart, CharEnd = newEnd, Part = part, ItemOrder = itemOrder, HunkOrder = hunkOrder,
            Message = $"추가: “{text}”",
            Endpoints = new() { new() { TargetDoc = oldDoc, CharStart = oldAnchor, CharEnd = oldAnchor }, new() { TargetDoc = newDoc, CharStart = newStart, CharEnd = newEnd } }
        };
    }

    private sealed class NativeMarkerComparer : IComparer<NativeMarker>
    {
        public static readonly NativeMarkerComparer Instance = new();
        public int Compare(NativeMarker? x, NativeMarker? y)
        {
            if (ReferenceEquals(x, y)) return 0; if (x is null) return -1; if (y is null) return 1;
            var c = x.PairOrder.CompareTo(y.PairOrder); if (c != 0) return c;
            static int PartRank(string part) => string.Equals(part, "header", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            c = PartRank(x.Part).CompareTo(PartRank(y.Part)); if (c != 0) return c;
            c = x.ItemOrder.CompareTo(y.ItemOrder); if (c != 0) return c;
            c = x.HunkOrder.CompareTo(y.HunkOrder); if (c != 0) return c;
            return x.CharStart.CompareTo(y.CharStart);
        }
    }
}
