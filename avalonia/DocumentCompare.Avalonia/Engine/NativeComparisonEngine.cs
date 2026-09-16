using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

public sealed class NativeComparisonEngine : IComparisonEngine
{
    private static readonly Regex KoreanArticle = new(@"^\s*제\s*(\d+)\s*조(?:\s*의\s*(\d+))?\s*(?:\(([^\n\)]{1,120})\))?\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex EnglishArticle = new(@"^\s*(?:Article|Section)\s+(\d+(?:[-.]\d+)*)\s*(?:[.:-])?\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SectionHeading = new(
        @"^\s*(?:제\s*\d+\s*(?:장|절|관)\b.*|(?:Chapter|Part)\s+\d+(?:[-.]\d+)*\b.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitItem = new(@"^\s*(?<label>(?:[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|\(\d+\)|\d+[.)]|[가-하A-Za-z][.)]))(?<ws>\s+)(?<core>.*)$", RegexOptions.Compiled);
    private static readonly Regex StrongInlineEnglishArticle = new(
        @"(?<![A-Za-z0-9])(?:Article|Section)\s+\d+(?:[-.]\d+)*\s*(?:[.:-])?\s*\([^\n)]{1,180}[)}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StrongInlineKoreanArticle = new(
        @"제\s*\d+\s*조(?:\s*의\s*\d+)?\s*\([^\n)]{1,180}[)}]", RegexOptions.Compiled);
    private static readonly Regex QuotedHead = new("^\\s*[\\\"“‘]([^\\\"”’]{1,96})[\\\"”’]", RegexOptions.Compiled);
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
        s = Regex.Replace(s, @"^[\s.:\-–—]+", string.Empty);
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
        var titled = Regex.Match(raw, @"^[\(\{\[](?<title>[^\)\}\]\n]{1,180})[\)\}\]]\s*(?<tail>.*)$");
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

    private static List<NativeUnit> ParseLegalUnits(string text)
    {
        var units = new List<NativeUnit>();
        var preamble = new List<string>();
        var body = new List<string>();
        string? header = null, number = null, title = null;
        var section = string.Empty;

        void Flush()
        {
            if (header is null)
            {
                if (preamble.Count == 0) return;
                var pre = string.Join("\n", preamble).Trim(); preamble.Clear();
                if (pre.Length > 0) units.Add(NativeUnit.Create(units.Count, $"p{units.Count + 1}", "", "", pre, section));
                return;
            }
            var b = string.Join("\n", body).Trim(); body.Clear();
            units.Add(NativeUnit.Create(units.Count, number ?? "", title ?? "", header, b, section));
            header = number = title = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sm = SectionHeading.Match(line);
            if (sm.Success) { Flush(); section = line; continue; }
            var km = KoreanArticle.Match(line);
            var em = EnglishArticle.Match(line);
            if (km.Success || em.Success)
            {
                Flush(); header = line;
                if (km.Success)
                {
                    number = km.Groups[2].Success ? $"{km.Groups[1].Value}-{km.Groups[2].Value}" : km.Groups[1].Value;
                    title = km.Groups[3].Value.Trim();
                    var tail = km.Groups[4].Value.Trim(); if (tail.Length > 0) body.Add(tail);
                }
                else
                {
                    number = em.Groups[1].Value;
                    var parsed = ParseEnglishArticleRest(em.Groups[2].Value);
                    title = parsed.Title;
                    if (parsed.BodyTail.Length > 0) body.Add(parsed.BodyTail);
                }
                continue;
            }
            if (header is null) preamble.Add(line); else body.Add(line);
        }
        Flush();
        return units;
    }

    private static List<NativeUnit> ParseGeneralUnits(string text)
    {
        var units = new List<NativeUnit>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            units.Add(NativeUnit.Create(units.Count, $"g{units.Count + 1}", "", "", line, ""));
        }
        return units;
    }

    private static List<NativeUnit?[]> BuildRows(IReadOnlyList<List<NativeUnit>> docs, int baseIndex, CancellationToken token)
    {
        var n = docs.Count; var baseline = docs[baseIndex];
        var rows = baseline.Select(u => { var r = new NativeUnit?[n]; r[baseIndex] = u; return r; }).ToList();
        var additions = new List<(int Slot, int Doc, NativeUnit Unit)>();
        for (var d = 0; d < n; d++)
        {
            if (d == baseIndex) continue;
            var matches = MatchUnitSequence(baseline, docs[d]);
            var used = matches.Select(x => x.Other).ToHashSet();
            foreach (var (bi, oi) in matches) rows[bi][d] = docs[d][oi];
            foreach (var oi in Enumerable.Range(0, docs[d].Count).Where(x => !used.Contains(x)))
            {
                token.ThrowIfCancellationRequested();
                var prev = matches.Where(x => x.Other < oi).OrderBy(x => x.Other).LastOrDefault();
                additions.Add((prev == default ? 0 : prev.Base + 1, d, docs[d][oi]));
            }
        }
        foreach (var g in additions.GroupBy(x => x.Slot).OrderByDescending(x => x.Key))
        {
            var pending = new List<NativeUnit?[]>();
            foreach (var x in g.OrderBy(x => x.Unit.Index).ThenBy(x => x.Doc))
            {
                var merge = pending.FirstOrDefault(r => r.FirstOrDefault(u => u is not null) is NativeUnit ex && Similarity(ex.Body, x.Unit.Body) >= .84);
                if (merge is null) { merge = new NativeUnit?[n]; pending.Add(merge); }
                merge[x.Doc] = x.Unit;
            }
            rows.InsertRange(Math.Clamp(g.Key, 0, rows.Count), pending);
        }
        return rows;
    }

    private static List<(int Base, int Other)> MatchUnitSequence(IReadOnlyList<NativeUnit> a, IReadOnlyList<NativeUnit> b)
    {
        var result = new List<(int Base, int Other)>();
        var usedA = new HashSet<int>();
        var usedB = new HashSet<int>();
        void Take(int i, int j)
        {
            if (usedA.Add(i) && usedB.Add(j)) result.Add((i, j));
        }

        // 1) Stable legal identity first: same article number + same normalized title.
        for (var i = 0; i < a.Count; i++)
        {
            if (usedA.Contains(i) || string.IsNullOrWhiteSpace(a[i].Number)) continue;
            var nt = Normalize(CleanArticleTitle(a[i].Title));
            if (nt.Length == 0) continue;
            var cand = Enumerable.Range(0, b.Count)
                .Where(j => !usedB.Contains(j) && a[i].Number == b[j].Number && nt == Normalize(CleanArticleTitle(b[j].Title)))
                .ToList();
            if (cand.Count == 1) Take(i, cand[0]);
        }

        // 2) Exact body/text lineage detects true moved/renumbered blocks without fuzzy guessing.
        for (var i = 0; i < a.Count; i++)
        {
            if (usedA.Contains(i) || a[i].NormalizedBody.Length == 0) continue;
            var cand = Enumerable.Range(0, b.Count)
                .Where(j => !usedB.Contains(j) && a[i].NormalizedBody == b[j].NormalizedBody)
                .OrderBy(j => Math.Abs(i - j)).ToList();
            if (cand.Count > 0) Take(i, cand[0]);
        }

        // 3) Exact title lineage can survive article renumbering even when the body changed.
        for (var i = 0; i < a.Count; i++)
        {
            if (usedA.Contains(i)) continue;
            var nt = Normalize(CleanArticleTitle(a[i].Title)); if (nt.Length == 0) continue;
            var cand = Enumerable.Range(0, b.Count)
                .Where(j => !usedB.Contains(j) && nt == Normalize(CleanArticleTitle(b[j].Title)))
                .OrderBy(j => Math.Abs(i - j)).ToList();
            if (cand.Count == 1) Take(i, cand[0]);
        }

        // 4) Same legal article number is a strong fallback, but only when some content remains
        // recognizably related. Synthetic generic g1/g2 ids are deliberately excluded.
        for (var i = 0; i < a.Count; i++)
        {
            if (usedA.Contains(i) || string.IsNullOrWhiteSpace(a[i].Number) || a[i].Number.StartsWith('g') || a[i].Number.StartsWith('p')) continue;
            var cand = Enumerable.Range(0, b.Count)
                .Where(j => !usedB.Contains(j) && a[i].Number == b[j].Number)
                .Select(j => (J: j, Score: UnitSimilarity(a[i], b[j])))
                .OrderByDescending(x => x.Score).FirstOrDefault();
            if (cand != default && cand.Score >= .28) Take(i, cand.J);
        }

        // 5) Residual fuzzy matching is last and intentionally stricter than the old greedy pass.
        var fuzzy = new List<(double Score, int A, int B)>();
        for (var i = 0; i < a.Count; i++) if (!usedA.Contains(i))
            for (var j = 0; j < b.Count; j++) if (!usedB.Contains(j))
            {
                var score = UnitSimilarity(a[i], b[j]);
                if (score >= .52) fuzzy.Add((score, i, j));
            }
        foreach (var x in fuzzy.OrderByDescending(x => x.Score).ThenBy(x => Math.Abs(x.A - x.B)))
            if (!usedA.Contains(x.A) && !usedB.Contains(x.B)) Take(x.A, x.B);

        result.Sort((x, y) => x.Base.CompareTo(y.Base));
        return result;
    }

    private static double UnitSimilarity(NativeUnit a, NativeUnit b)
    {
        if (a.NormalizedText.Length > 0 && a.NormalizedText == b.NormalizedText) return 1.0;
        if (a.NormalizedBody.Length > 0 && a.NormalizedBody == b.NormalizedBody) return .99;
        var score = Similarity(a.Body, b.Body) * .76 + Similarity(a.Title, b.Title) * .12;
        if (a.Number.Length > 0 && a.Number == b.Number) score += .16;
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
                var key = Normalize(part.Core);
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
            if (old is null) { structural.Add($"{pair} · 조/블록 추가: {revised!.HeaderOrBody}"); continue; }
            if (revised is null) { structural.Add($"{pair} · 조/블록 삭제: {old.HeaderOrBody}"); continue; }
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

        var messages = markers.Select(m => $"[{m.Num}] {m.Pair} · {m.Message}")
            .Concat(structural.Distinct().Select(x => "• " + x)).ToList();
        if (messages.Count == 0) messages.Add("변경 없음");
        return new ComparisonRowVm
        {
            Id = id,
            Members = members.Select(x => x?.ToViewModel()).ToList(),
            HeaderSegments = headers,
            BodySegments = bodies,
            Markers = markers,
            DisplayMessages = messages,
            Changed = messages.Count != 1 || messages[0] != "변경 없음"
        };
    }

    private static List<NativeMarker> ComparePair(
        NativeUnit old, NativeUnit revised, int oldDoc, int newDoc, string pair, int pairOrder,
        bool includePunctuation, List<string> structural,
        IReadOnlyList<Dictionary<string, List<PartLocation>>> globalParts)
    {
        var result = new List<NativeMarker>();
        if (!HeaderEquivalent(old, revised) && !SemanticEqual(old.Header, revised.Header) &&
            (old.Header.Length > 0 || revised.Header.Length > 0))
            result.AddRange(DiffText(old.Header, revised.Header, 0, 0, oldDoc, newDoc, pair, pairOrder, "header", includePunctuation, 0));

        var oldParts = ParseParts(old.Body);
        var newParts = ParseParts(revised.Body);
        var matches = MatchParts(oldParts, newParts);
        var usedOld = matches.Select(x => x.Old).ToHashSet();
        var usedNew = matches.Select(x => x.New).ToHashSet();
        var oldOffset = old.Header.Length + 1;
        var newOffset = revised.Header.Length + 1;

        foreach (var match in matches.OrderBy(x => x.Old))
        {
            var a = oldParts[match.Old]; var b = newParts[match.New];
            if (a.Label != b.Label && IsStructuralLabel(a.Label) && IsStructuralLabel(b.Label))
            {
                // A number/location change is a real visible revision.  Put the marker and
                // decoration on the enumerator itself: old number = red strike, new = blue underline.
                result.Add(NativeMarker.StructuralChange(pair, pairOrder, oldDoc, newDoc,
                    a.Label, b.Label,
                    oldOffset + a.Start, oldOffset + a.Start + a.Label.Length,
                    newOffset + b.Start, newOffset + b.Start + b.Label.Length,
                    "body", match.Old, -1));
            }
            if (!SemanticEqual(a.Core, b.Core))
            {
                var wholePlainRewrite = a.Label == "본문" && b.Label == "본문" &&
                    oldParts.Count == 1 && newParts.Count == 1 && match.Score < .72;
                if (wholePlainRewrite || (match.Score <= .56 && a.Label == b.Label && a.Label != "본문" &&
                    !HasReviewAnchors(a.Core, b.Core)))
                {
                    // One substantially rewritten paragraph/item is one logical replacement.
                    // Higher-similarity edits still use fine-grained review hunks below.
                    result.Add(NativeMarker.Change(pair, pairOrder, oldDoc, newDoc,
                        a.Core.Trim(), b.Core.Trim(), oldOffset + a.CoreStart, oldOffset + a.CoreEnd,
                        newOffset + b.CoreStart, newOffset + b.CoreEnd, "body", match.Old, 0));
                }
                else
                {
                    result.AddRange(DiffText(
                        a.Core, b.Core, oldOffset + a.CoreStart, newOffset + b.CoreStart,
                        oldDoc, newDoc, pair, pairOrder, "body", includePunctuation, match.Old));
                }
            }
        }

        foreach (var i in Enumerable.Range(0, oldParts.Count).Where(i => !usedOld.Contains(i)))
        {
            var p = oldParts[i]; if (string.IsNullOrWhiteSpace(p.Core)) continue;
            var key = Normalize(p.Core);
            if (TryUniqueMovedPart(globalParts[newDoc], key, revised.Index, out var movedTo))
            {
                structural.Add($"{pair} · 항/호 번호·위치 변경: {p.Label} → {movedTo.Label} ({movedTo.Header})");
                continue;
            }
            var start = oldOffset + p.CoreStart; var end = oldOffset + p.CoreEnd;
            // Whole-item deletion: counterpart belongs at the structural gap between the
            // nearest matched items, not at a repeated lexical token elsewhere in the article.
            var newAnchor = newOffset + StructuralGapAnchor(oldParts, newParts, matches, i, sourceIsNew: false);
            result.Add(NativeMarker.Delete(pair, pairOrder, oldDoc, newDoc, p.Core.Trim(), start, end, newAnchor, "body", i));
        }
        foreach (var j in Enumerable.Range(0, newParts.Count).Where(j => !usedNew.Contains(j)))
        {
            var p = newParts[j]; if (string.IsNullOrWhiteSpace(p.Core)) continue;
            var key = Normalize(p.Core);
            // The source-side row reports the move. Suppress a duplicate insertion when the
            // exact item exists uniquely elsewhere in the old document.
            if (TryUniqueMovedPart(globalParts[oldDoc], key, old.Index, out _))
                continue;
            var start = newOffset + p.CoreStart; var end = newOffset + p.CoreEnd;
            // Whole-item insertion: use the same structural gap on the old side. This keeps
            // appended clauses after the previous item and inserted clauses before the next
            // matched item instead of anchoring on generic words such as 회사/회원/서비스.
            var oldAnchor = oldOffset + StructuralGapAnchor(newParts, oldParts, matches, j, sourceIsNew: true);
            result.Add(NativeMarker.Insert(pair, pairOrder, oldDoc, newDoc, p.Core.Trim(), oldAnchor, start, end, "body", j));
        }
        return result;
    }

    private static List<NativeMarker> DiffText(
        string oldText, string newText, int oldBase, int newBase, int oldDoc, int newDoc,
        string pair, int pairOrder, string part, bool includePunctuation, int itemOrder)
    {
        if (SemanticEqual(oldText, newText)) return new();
        var korean = TryKoreanReviewDiff(oldText, newText, oldBase, newBase, oldDoc, newDoc, pair, pairOrder, part, itemOrder);
        if (korean is not null) return korean;
        var a = LexTokens(oldText); var b = LexTokens(newText);
        var matches = LcsMatches(a.Select(x => TokenKey(x.Text)).ToArray(), b.Select(x => TokenKey(x.Text)).ToArray());
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
                result.Add(NativeMarker.Delete(
                    pair, pairOrder, oldDoc, newDoc, ot,
                    oldBase + oldStart, oldBase + oldEnd, newBase + newStart, part, itemOrder, k));
            }
            else if (nt.Length > 0)
            {
                result.Add(NativeMarker.Insert(
                    pair, pairOrder, oldDoc, newDoc, nt,
                    oldBase + oldStart, newBase + newStart, newBase + newEnd, part, itemOrder, k));
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
        if (Math.Max(oldText.Length, newText.Length) > 120 ||
            !Regex.IsMatch(oldText, "[가-힣]") || !Regex.IsMatch(newText, "[가-힣]"))
            return null;

        var a = Regex.Matches(oldText, @"\S+").Select(m => new WordSpan(m.Index, m.Index + m.Length, m.Value)).ToList();
        var b = Regex.Matches(newText, @"\S+").Select(m => new WordSpan(m.Index, m.Index + m.Length, m.Value)).ToList();
        if (a.Count == 0 || b.Count == 0 || Math.Max(a.Count, b.Count) > 24) return null;

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

        var reversed = new List<WordOp>();
        var x = a.Count; var y = b.Count;
        while (x > 0 || y > 0)
        {
            var op = prev[x, y];
            if (op == 'M')
            {
                var (kind, _) = KoreanWordRelation(a[x - 1].Text, b[y - 1].Text);
                if (kind == "none") return null;
                reversed.Add(new WordOp(kind, x - 1, y - 1)); x--; y--;
            }
            else if (op == 'D') { reversed.Add(new WordOp("delete", x - 1, null)); x--; }
            else if (op == 'I') { reversed.Add(new WordOp("insert", null, y - 1)); y--; }
            else return null;
        }
        reversed.Reverse();
        var exact = reversed.Count(o => o.Kind == "equal");
        var fuzzy = reversed.Count(o => o.Kind == "fuzzy");
        if (exact < 2 || fuzzy < 2) return null;

        var result = new List<NativeMarker>();
        // Convert each contiguous run of non-equal word operations into ONE review hunk.
        // This is the C# equivalent of the mature Python replacement coalescer: a local
        // delete+insert sequence is a replacement, not a spray of independent markers.
        for (var k = 0; k < reversed.Count;)
        {
            if (reversed[k].Kind == "equal") { k++; continue; }
            var begin = k;
            while (k < reversed.Count && reversed[k].Kind != "equal") k++;
            var end = k;
            var oldIds = reversed.GetRange(begin, end - begin).Where(o => o.OldIndex.HasValue).Select(o => o.OldIndex!.Value).ToList();
            var newIds = reversed.GetRange(begin, end - begin).Where(o => o.NewIndex.HasValue).Select(o => o.NewIndex!.Value).ToList();
            if (oldIds.Count > 0 && newIds.Count > 0)
            {
                var os = a[oldIds.Min()].Start; var oe = a[oldIds.Max()].End;
                var ns = b[newIds.Min()].Start; var ne = b[newIds.Max()].End;
                var oldRaw = oldText[os..oe]; var newRaw = newText[ns..ne];
                var (ot, nt, od, nd) = TrimSharedHangulPrefix(oldRaw, newRaw);
                if (!SemanticEqual(ot, nt))
                    result.Add(NativeMarker.Change(pair, pairOrder, oldDoc, newDoc, ot, nt,
                        oldBase + os + od, oldBase + oe, newBase + ns + nd, newBase + ne,
                        part, itemOrder, begin));
            }
            else if (oldIds.Count > 0)
            {
                var os = a[oldIds.Min()].Start; var oe = a[oldIds.Max()].End;
                var anchor = LocalNewAnchor(reversed, begin, a, b);
                result.Add(NativeMarker.Delete(pair, pairOrder, oldDoc, newDoc, oldText[os..oe],
                    oldBase + os, oldBase + oe, newBase + anchor, part, itemOrder, begin));
            }
            else if (newIds.Count > 0)
            {
                var ns = b[newIds.Min()].Start; var ne = b[newIds.Max()].End;
                var anchor = LocalOldAnchor(reversed, begin, a, b);
                result.Add(NativeMarker.Insert(pair, pairOrder, oldDoc, newDoc, newText[ns..ne],
                    oldBase + anchor, newBase + ns, newBase + ne, part, itemOrder, begin));
            }
        }
        return result.Count is >= 1 and <= 8 ? result : null;
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

    private static int LocalNewAnchor(IReadOnlyList<WordOp> ops, int at, IReadOnlyList<WordSpan> oldWords, IReadOnlyList<WordSpan> newWords)
    {
        for (var i = at + 1; i < ops.Count; i++) if (ops[i].NewIndex is int n) return newWords[n].Start;
        for (var i = at - 1; i >= 0; i--) if (ops[i].NewIndex is int n) return newWords[n].End;
        return 0;
    }

    private static int LocalOldAnchor(IReadOnlyList<WordOp> ops, int at, IReadOnlyList<WordSpan> oldWords, IReadOnlyList<WordSpan> newWords)
    {
        for (var i = at + 1; i < ops.Count; i++) if (ops[i].OldIndex is int n) return oldWords[n].Start;
        for (var i = at - 1; i >= 0; i--) if (ops[i].OldIndex is int n) return oldWords[n].End;
        return 0;
    }

    private static List<NativePart> ParseParts(string text)
    {
        text = NativeDocumentReader.NormalizeNewlines(text);
        var parts = new List<NativePart>();
        var lines = text.Split('\n');
        var cursor = 0;
        int? plainStart = null;
        int plainEnd = 0;

        void FlushPlain()
        {
            if (plainStart is not int st) return;
            var raw = text[st..plainEnd];
            var leading = raw.Length - raw.TrimStart().Length;
            var trailing = raw.Length - raw.TrimEnd().Length;
            var coreStart = st + leading;
            var coreEnd = Math.Max(coreStart, plainEnd - trailing);
            if (coreEnd > coreStart)
                parts.Add(new NativePart("본문", coreStart, coreEnd, coreStart, coreEnd, text[coreStart..coreEnd]));
            plainStart = null; plainEnd = 0;
        }

        foreach (var line in lines)
        {
            var m = ExplicitItem.Match(line);
            if (m.Success)
            {
                FlushPlain();
                var core = m.Groups["core"].Value.TrimEnd();
                var coreStart = cursor + m.Groups["core"].Index;
                var itemStart = cursor + Math.Max(0, line.Length - line.TrimStart().Length);
                parts.Add(new NativePart(m.Groups["label"].Value, itemStart, cursor + line.Length,
                    coreStart, coreStart + core.Length, core));
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                var trim = line.Trim();
                var delta = line.IndexOf(trim, StringComparison.Ordinal);
                var st = cursor + Math.Max(0, delta);
                if (plainStart is null) plainStart = st;
                plainEnd = cursor + line.Length;
            }
            cursor += line.Length + 1;
        }
        FlushPlain();
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            var trim = text.Trim(); var st = text.IndexOf(trim, StringComparison.Ordinal);
            parts.Add(new NativePart("본문", st, st + trim.Length, st, st + trim.Length, trim));
        }
        return parts;
    }

    private static List<PartMatch> MatchParts(IReadOnlyList<NativePart> a, IReadOnlyList<NativePart> b)
    {
        var result = new List<PartMatch>(); var usedA = new HashSet<int>(); var usedB = new HashSet<int>();
        void Take(int i, int j, double score) { if (usedA.Add(i) && usedB.Add(j)) result.Add(new PartMatch(i, j, score)); }

        // Exact text first: this is what detects pure moves/renumbering such as 3. -> 4.
        for (var i = 0; i < a.Count; i++)
        {
            var exact = Enumerable.Range(0, b.Count)
                .Where(j => !usedB.Contains(j) && Normalize(a[i].Core) == Normalize(b[j].Core))
                .OrderBy(j => Math.Abs(i - j)).ToList();
            if (exact.Count > 0) Take(i, exact[0], 1.0);
        }

        // Definition heads are stable identities even if the number changes.
        for (var i = 0; i < a.Count; i++)
        {
            if (usedA.Contains(i)) continue;
            var head = DefinitionHead(a[i].Core); if (head.Length == 0) continue;
            var cand = Enumerable.Range(0, b.Count)
                .Where(j => !usedB.Contains(j) && DefinitionHead(b[j].Core) == head)
                .Select(j => (J: j, S: PartSimilarity(a[i], b[j])))
                .OrderByDescending(x => x.S).ThenBy(x => Math.Abs(i - x.J)).FirstOrDefault();
            if (cand != default) Take(i, cand.J, Math.Max(.9, cand.S));
        }

        // A single unnumbered paragraph on each side of the same aligned article is one
        // structural slot even when it was substantially rewritten. Without this guard the
        // C# port emitted a detached delete + insert and anchored many later badges at the
        // beginning of the revised paragraph.
        var plainA = Enumerable.Range(0, a.Count)
            .Where(i => !usedA.Contains(i) && a[i].Label == "본문").ToList();
        var plainB = Enumerable.Range(0, b.Count)
            .Where(j => !usedB.Contains(j) && b[j].Label == "본문").ToList();
        if (plainA.Count == 1 && plainB.Count == 1 && a.Count == 1 && b.Count == 1)
        {
            var i = plainA[0]; var j = plainB[0];
            Take(i, j, Math.Max(.50, PartSimilarity(a[i], b[j])));
        }

        // A completely rewritten item can have almost no lexical overlap.  Treat it as one
        // replacement only when it is an isolated same-number slot bounded by already matched
        // neighbours on both sides.  This captures a true "item 3 rewritten" without turning
        // a whole newly inserted 3./4./5./... block into bogus replacements.
        for (var i = 1; i + 1 < a.Count; i++)
        {
            if (usedA.Contains(i) || a[i].Label == "본문") continue;
            var j = Enumerable.Range(1, Math.Max(0, b.Count - 2))
                .FirstOrDefault(x => !usedB.Contains(x) && b[x].Label == a[i].Label, -1);
            if (j < 1 || j + 1 >= b.Count) continue;
            var left = result.Any(m => m.Old == i - 1 && m.New == j - 1);
            var right = result.Any(m => m.Old == i + 1 && m.New == j + 1);
            if (left && right) Take(i, j, .55);
        }

        // Same item number is only a hint. Unrelated new items sharing 3./4./5. stay additions.
        for (var i = 0; i < a.Count; i++)
        {
            if (usedA.Contains(i) || a[i].Label == "본문") continue;
            var cand = Enumerable.Range(0, b.Count)
                .Where(j => !usedB.Contains(j) && b[j].Label == a[i].Label)
                .Select(j => (J: j, S: PartSimilarity(a[i], b[j])))
                .OrderByDescending(x => x.S).FirstOrDefault();
            if (cand != default)
            {
                var stableEdge = HasStableEdgeContext(a[i].Core, b[cand.J].Core);
                var isolatedPair = a.Count == 1 && b.Count == 1;
                // Python V5.7 treated an explicit same enumerator as the normal legal anchor
                // once some lexical relationship remained.  The C# port used .46 here, which
                // was too strict and turned rewritten definitions into detached delete+insert.
                if (cand.S >= .24 || stableEdge || isolatedPair)
                    Take(i, cand.J, Math.Max(cand.S, stableEdge ? .60 : (isolatedPair ? .55 : .24)));
            }
        }

        // Strong residual content match catches edited moved items.
        var fuzzy = new List<(double Score, int A, int B)>();
        for (var i = 0; i < a.Count; i++) if (!usedA.Contains(i))
            for (var j = 0; j < b.Count; j++) if (!usedB.Contains(j))
            {
                var score = PartSimilarity(a[i], b[j]);
                if (score >= .70) fuzzy.Add((score, i, j));
            }
        foreach (var x in fuzzy.OrderByDescending(x => x.Score))
            if (!usedA.Contains(x.A) && !usedB.Contains(x.B)) Take(x.A, x.B, x.Score);
        return result;
    }

    private static bool HasReviewAnchors(string a, string b)
    {
        var aw = Regex.Matches(a ?? string.Empty, @"[가-힣A-Za-z0-9_]+")
            .Select(m => TokenKey(m.Value)).Where(x => x.Length > 0).ToList();
        var bw = Regex.Matches(b ?? string.Empty, @"[가-힣A-Za-z0-9_]+")
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
        var seq = Similarity(a.Core, b.Core);
        var aw = WordSet(a.Core); var bw = WordSet(b.Core);
        var shared = aw.Intersect(bw).Count();
        var contain = shared / (double)Math.Max(1, Math.Min(aw.Count, bw.Count));
        return Math.Max(seq, .9 * contain);
    }

    private static List<TokenSpan> LexTokens(string text)
    {
        var result = new List<TokenSpan>();
        foreach (Match m in Regex.Matches(text ?? "", @"[가-힣A-Za-z0-9_]+|[^\s가-힣A-Za-z0-9_]"))
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

    private static int MapBoundary(string src, string dst, int pos)
    {
        pos = Math.Clamp(pos, 0, src.Length);
        var a = LexTokens(src); var b = LexTokens(dst);
        var matches = LcsMatches(a.Select(x => TokenKey(x.Text)).ToArray(), b.Select(x => TokenKey(x.Text)).ToArray());
        var left = matches.Where(x => a[x.A].End <= pos).LastOrDefault((-1, -1));
        var right = matches.Where(x => a[x.A].Start >= pos).FirstOrDefault((-1, -1));
        if (left.Item1 >= 0 && right.Item1 >= 0) return b[left.Item2].End;
        if (left.Item1 >= 0) return b[left.Item2].End;
        if (right.Item1 >= 0) return b[right.Item2].Start;
        return Math.Clamp((int)Math.Round(dst.Length * (pos / (double)Math.Max(1, src.Length))), 0, dst.Length);
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

    internal static bool SemanticEqual(string a, string b) => Normalize(a) == Normalize(b);
    private static string TokenKey(string value) => Normalize(value);
    private static string DefinitionHead(string value)
    {
        var m = QuotedHead.Match(value ?? string.Empty); return m.Success ? Normalize(m.Groups[1].Value) : string.Empty;
    }
    private static HashSet<string> WordSet(string value) =>
        Regex.Matches(Normalize(value), @"[가-힣A-Za-z0-9_]+", RegexOptions.CultureInvariant)
            .Select(x => x.Value).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);

    internal static double Similarity(string a, string b)
    {
        var x = Normalize(a); var y = Normalize(b);
        if (x == y) return 1; if (x.Length == 0 || y.Length == 0) return 0;
        var aa = Regex.Matches(x, @"[가-힣A-Za-z0-9_]+|[^\s]").Select(m => m.Value).ToArray();
        var bb = Regex.Matches(y, @"[가-힣A-Za-z0-9_]+|[^\s]").Select(m => m.Value).ToArray();
        var lcs = LcsMatches(aa, bb).Count;
        return 2.0 * lcs / Math.Max(1, aa.Length + bb.Length);
    }

    private static bool IsPunctuationOnly(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Where(x => !char.IsWhiteSpace(x)).All(x => !char.IsLetterOrDigit(x));
    private static bool IsStructuralLabel(string label) => label != "본문" && label.Length > 0;

    private sealed record NativePart(string Label, int Start, int End, int CoreStart, int CoreEnd, string Core);
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
