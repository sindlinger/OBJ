using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FilterPDF.TjpbDespachoExtractor.Utils;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    internal static class ObjectsFrontBack
    {
        internal enum BandMode
        {
            Front,
            Back
        }

        public static void Execute(string[] args, BandMode mode)
        {
            if (!ParseOptions(args, out var inputFile, out var pages, out var yMin, out var yMax, out var includeXObject, out var limit, out var showLimits, out var showLines, out var lineTol))
            {
                ShowHelp(mode);
                return;
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                Console.WriteLine("Informe um --input valido.");
                ShowHelp(mode);
                return;
            }

            if (!yMin.HasValue || !yMax.HasValue)
            {
                if (mode == BandMode.Front)
                {
                    yMin = 0.65;
                    yMax = 1.0;
                }
                else
                {
                    yMin = 0.0;
                    yMax = 0.35;
                }
            }

            using var reader = new PdfReader(inputFile);
            reader.SetUnethicalReading(true);
            using var doc = new PdfDocument(reader);

            var pageList = pages.Count > 0
                ? pages.Distinct().Where(p => p >= 1 && p <= doc.GetNumberOfPages()).OrderBy(p => p).ToList()
                : Enumerable.Range(1, doc.GetNumberOfPages()).ToList();

            var label = mode == BandMode.Front ? "front_head" : "back_tail";
            Console.WriteLine($"[{label}] input={Path.GetFileName(inputFile)} y={yMin.Value:F2}-{yMax.Value:F2}");

            int shown = 0;
            foreach (var p in pageList)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var pageHeight = page.GetPageSize().GetHeight();

                foreach (var stream in EnumerateStreams(contents))
                {
                    if (limit > 0 && shown >= limit) return;
                    var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (pageHeight <= 0) continue;

                    if (!TryGetTextYRange(stream, resources, pageHeight, out var minNy, out var maxNy))
                        continue;

                    if (!RangesOverlap(minNy, maxNy, yMin.Value, yMax.Value))
                        continue;

                    var opCount = CountTextOperators(stream);
                    var fonts = ExtractFonts(stream);
                    Console.WriteLine($"p{p} obj={id} y={minNy:F2}-{maxNy:F2} len={stream.GetLength()} ops={opCount} fonts={FormatFonts(fonts)}");
                    if (showLines || showLimits)
                        PrintLineAndLimits(stream, resources, lineTol, mode, showLines, showLimits);
                    shown++;
                }

                if (includeXObject)
                {
                    var xobjects = resources.GetResource(PdfName.XObject) as PdfDictionary;
                    if (xobjects == null) continue;
                    foreach (var name in xobjects.KeySet())
                    {
                        if (limit > 0 && shown >= limit) return;
                        var xs = xobjects.GetAsStream(name);
                        if (xs == null) continue;
                        var subtype = xs.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                        if (!string.Equals(subtype, "/Form", StringComparison.OrdinalIgnoreCase)) continue;
                        var xresDict = xs.GetAsDictionary(PdfName.Resources);
                        var xres = xresDict != null ? new PdfResources(xresDict) : resources;
                        var id = xs.GetIndirectReference()?.GetObjNumber() ?? 0;
                        if (!TryGetTextYRange(xs, xres, pageHeight, out var minNy, out var maxNy))
                            continue;
                        if (!RangesOverlap(minNy, maxNy, yMin.Value, yMax.Value))
                            continue;
                        var opCount = CountTextOperators(xs);
                        var fonts = ExtractFonts(xs);
                        Console.WriteLine($"p{p} obj={id} y={minNy:F2}-{maxNy:F2} len={xs.GetLength()} ops={opCount} fonts={FormatFonts(fonts)} xobject={name}");
                        if (showLines || showLimits)
                            PrintLineAndLimits(xs, xres, lineTol, mode, showLines, showLimits);
                        shown++;
                    }
                }
            }
        }

        private static bool ParseOptions(string[] args, out string inputFile, out List<int> pages, out double? yMin, out double? yMax, out bool includeXObject, out int limit, out bool showLimits, out bool showLines, out double lineTol)
        {
            inputFile = "";
            pages = new List<int>();
            yMin = null;
            yMax = null;
            includeXObject = false;
            limit = 0;
            showLimits = false;
            showLines = false;
            lineTol = 2.0;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var p)) pages.Add(p);
                    continue;
                }
                if (string.Equals(arg, "--pages", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    pages.AddRange(ParsePageList(args[++i]));
                    continue;
                }
                if ((string.Equals(arg, "--y-range", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--yrange", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    if (TryParseRange(raw, out var a, out var b))
                    {
                        yMin = Math.Max(0, Math.Min(1, a));
                        yMax = Math.Max(0, Math.Min(1, b));
                        if (yMax < yMin) (yMin, yMax) = (yMax, yMin);
                    }
                    continue;
                }
                if (string.Equals(arg, "--include-xobject", StringComparison.OrdinalIgnoreCase))
                {
                    includeXObject = true;
                    continue;
                }
                if (string.Equals(arg, "--limits", StringComparison.OrdinalIgnoreCase))
                {
                    showLimits = true;
                    continue;
                }
                if (string.Equals(arg, "--lines", StringComparison.OrdinalIgnoreCase))
                {
                    showLines = true;
                    continue;
                }
                if (string.Equals(arg, "--line-tol", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        lineTol = Math.Max(0, v);
                    continue;
                }
                if (string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var n)) limit = Math.Max(0, n);
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = arg;
                }
            }

            return true;
        }

        private static void ShowHelp(BandMode mode)
        {
            var label = mode == BandMode.Front ? "fronthead" : "backtail";
            Console.WriteLine($"tjpdf-cli inspect objects {label} --input file.pdf [--page N|--pages 1,3]");
            Console.WriteLine("                              [--y-range 0.70-1.00] [--include-xobject] [--limit N]");
            Console.WriteLine("                              [--limits] [--lines] [--line-tol N]");
        }

        private static IEnumerable<PdfStream> EnumerateStreams(PdfObject? obj)
        {
            if (obj == null) yield break;
            if (obj is PdfStream s)
            {
                yield return s;
                yield break;
            }
            if (obj is PdfArray arr)
            {
                foreach (var item in arr)
                    if (item is PdfStream ss) yield return ss;
            }
        }

        private static bool TryParseRange(string raw, out double a, out double b)
        {
            a = 0;
            b = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();
            if (raw.Contains('-', StringComparison.Ordinal))
            {
                var parts = raw.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                    && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                    return true;
            }
            if (raw.Contains(',', StringComparison.Ordinal))
            {
                var parts = raw.Split(',', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                    && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                    return true;
            }
            return false;
        }

        private static bool RangesOverlap(double aMin, double aMax, double bMin, double bMax)
        {
            if (aMax < bMin || bMax < aMin) return false;
            return true;
        }

        private static bool TryGetTextYRange(PdfStream stream, PdfResources resources, double pageHeight, out double minNy, out double maxNy)
        {
            minNy = 0;
            maxNy = 0;
            if (pageHeight <= 0) return false;
            var items = PdfTextExtraction.CollectTextItems(stream, resources);
            if (items == null || items.Count == 0) return false;

            double minY = double.MaxValue;
            double maxY = double.MinValue;
            foreach (var item in items)
            {
                var y = item.Y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (minY == double.MaxValue || maxY == double.MinValue) return false;
            minNy = minY / pageHeight;
            maxNy = maxY / pageHeight;
            return true;
        }

        private static int CountTextOperators(PdfStream stream)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return 0;
            var tokens = TokenizeContent(bytes);
            int count = 0;
            foreach (var tok in tokens)
            {
                if (TextOperators.Contains(tok))
                    count++;
            }
            return count;
        }

        private static HashSet<string> ExtractFonts(PdfStream stream)
        {
            var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return fonts;
            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();
            foreach (var tok in tokens)
            {
                if (!Operators.Contains(tok))
                {
                    operands.Add(tok);
                    continue;
                }
                if (tok == "Tf" && operands.Count >= 2)
                {
                    var font = operands[^2];
                    if (!string.IsNullOrWhiteSpace(font) && font.StartsWith("/", StringComparison.Ordinal))
                        fonts.Add(font);
                }
                operands.Clear();
            }
            return fonts;
        }

        private static string FormatFonts(HashSet<string> fonts)
        {
            if (fonts.Count == 0) return "-";
            return string.Join(",", fonts.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }

        private sealed class LineBlock
        {
            public int Index { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Text { get; set; } = "";
            public double? Y { get; set; }
            public bool IsBlank { get; set; }
        }

        private sealed class ParagraphBlock
        {
            public int Index { get; set; }
            public int StartLine { get; set; }
            public int EndLine { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Text { get; set; } = "";
        }

        private static void PrintLineAndLimits(PdfStream stream, PdfResources resources, double lineTol, BandMode mode, bool showLines, bool showLimits)
        {
            var lines = ExtractLineBlocks(stream, resources, lineTol);
            if (lines.Count == 0) return;

            if (showLines)
            {
                Console.WriteLine("lines:");
                foreach (var line in lines)
                {
                    var label = line.StartOp == line.EndOp ? $"op{line.StartOp}" : $"op{line.StartOp}-{line.EndOp}";
                    var y = line.Y.HasValue ? line.Y.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";
                    var text = line.Text.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
                    if (text.Length > 120) text = text.Substring(0, 120) + "...";
                    Console.WriteLine($"  l{line.Index} {label} y={y} {(line.IsBlank ? "<blank>" : text)}");
                }
            }

            if (!showLimits) return;

            var paras = BuildParagraphs(lines);
            if (paras.Count == 0) return;

            Console.WriteLine("paragraphs:");
            foreach (var p in paras)
            {
                var label = p.StartOp == p.EndOp ? $"op{p.StartOp}" : $"op{p.StartOp}-{p.EndOp}";
                var text = p.Text.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 160) text = text.Substring(0, 160) + "...";
                Console.WriteLine($"  p{p.Index} lines={p.StartLine}-{p.EndLine} {label} {text}");
            }

            if (mode == BandMode.Front)
            {
                var head = PickHeadParagraph(paras) ?? paras.First();
                var label = head.StartOp == head.EndOp ? $"op{head.StartOp}" : $"op{head.StartOp}-{head.EndOp}";
                Console.WriteLine($"front_head: p{head.Index} {label}");
            }
            else
            {
                var tail = PickTailParagraph(paras) ?? paras.Last();
                var label = tail.StartOp == tail.EndOp ? $"op{tail.StartOp}" : $"op{tail.StartOp}-{tail.EndOp}";
                Console.WriteLine($"back_tail: p{tail.Index} {label}");
            }
        }

        private static ParagraphBlock? PickHeadParagraph(List<ParagraphBlock> paras)
        {
            if (paras == null || paras.Count == 0) return null;
            var preferred = paras.FirstOrDefault(p => IsPreferredHeadParagraph(p.Text));
            if (preferred != null) return preferred;
            var longPara = paras.FirstOrDefault(p => (p.Text ?? "").Length >= 120 && !IsHeaderOrMetaParagraph(p.Text));
            if (longPara != null) return longPara;
            var candidate = paras.FirstOrDefault(p => !IsHeaderOrMetaParagraph(p.Text));
            return candidate ?? paras.FirstOrDefault();
        }

        private static ParagraphBlock? PickTailParagraph(List<ParagraphBlock> paras)
        {
            if (paras == null || paras.Count == 0) return null;
            var scored = paras
                .Select(p => new { Para = p, Score = ScoreTailParagraph(p.Text) })
                .Where(x => x.Score > 0 && !IsFooterParagraph(x.Para.Text))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Para.Index)
                .ToList();
            if (scored.Count > 0) return scored.First().Para;
            var candidate = paras.LastOrDefault(p => !IsFooterParagraph(p.Text) && (p.Text ?? "").Length >= 60);
            if (candidate != null) return candidate;
            candidate = paras.LastOrDefault(p => !IsFooterParagraph(p.Text));
            return candidate ?? paras.LastOrDefault();
        }

        private static bool IsPreferredHeadParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return false;
            return ContainsLoose(norm, "os presentes autos") ||
                   ContainsLoose(norm, "presentes autos") ||
                   ContainsLoose(norm, "versam sobre");
        }

        private static bool IsHeaderOrMetaParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return true;
            if (norm.Length < 40) return true;
            if (ContainsLoose(norm, "despacho")) return true;
            if (ContainsLoose(norm, "processo")) return true;
            if (ContainsLoose(norm, "requerente")) return true;
            if (ContainsLoose(norm, "interessado")) return true;
            if (ContainsLoose(norm, "poder judiciario")) return true;
            if (ContainsLoose(norm, "tribunal de justica")) return true;
            if (ContainsLoose(norm, "diretoria especial")) return true;
            return false;
        }

        private static bool IsFooterParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return true;
            if (ContainsLoose(norm, "documento assinado eletronicamente")) return true;
            if (ContainsLoose(norm, "assinado eletronicamente")) return true;
            if (ContainsLoose(norm, "diretor") && norm.Length < 120) return true;
            if (ContainsLoose(norm, "juiz") && norm.Length < 120) return true;
            if (ContainsLoose(norm, "diretoria especial") && norm.Length < 120) return true;
            if (ContainsLoose(norm, "joao pessoa") && ContainsLoose(norm, "pb")) return true;
            if (ContainsLoose(norm, "codigo verificador")) return true;
            if (ContainsLoose(norm, "crc")) return true;
            if (ContainsLoose(norm, "autenticidade deste documento")) return true;
            if (ContainsLoose(norm, "sei") && ContainsLoose(norm, "/ pg")) return true;
            return false;
        }

        private static int ScoreTailParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return 0;
            int score = 0;
            if (ContainsLoose(norm, "em razao do exposto")) score += 10;
            if (ContainsLoose(norm, "encaminhem-se")) score += 8;
            if (ContainsLoose(norm, "gerencia de programacao orcamentaria")) score += 6;
            if (ContainsLoose(norm, "georc")) score += 6;
            if (ContainsLoose(norm, "reserva orcamentaria")) score += 4;
            if (ContainsLoose(norm, "arbitrad")) score += 1;
            if (ContainsLoose(norm, "perito")) score += 1;
            return score;
        }

        private static bool ContainsLoose(string norm, string phrase)
        {
            if (string.IsNullOrWhiteSpace(norm) || string.IsNullOrWhiteSpace(phrase)) return false;
            var p = phrase.ToLowerInvariant();
            if (norm.Contains(p)) return true;
            var tightNorm = norm.Replace(" ", "");
            var tightPhrase = p.Replace(" ", "");
            return tightNorm.Contains(tightPhrase);
        }

        private static string NormalizeSimple(string text)
        {
            var collapsed = TextUtils.CollapseSpacedLettersText(text ?? "");
            var t = TextUtils.RemoveDiacritics(collapsed).ToLowerInvariant();
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        private static List<ParagraphBlock> BuildParagraphs(List<LineBlock> lines)
        {
            var ordered = lines.OrderBy(l => l.Index).ToList();
            var nonBlank = ordered.Where(l => !l.IsBlank && l.Y.HasValue).ToList();
            var gaps = new List<double>();
            for (int i = 1; i < nonBlank.Count; i++)
            {
                var dy = nonBlank[i - 1].Y!.Value - nonBlank[i].Y!.Value;
                if (dy > 0) gaps.Add(dy);
            }
            double medianGap = 0;
            if (gaps.Count > 0)
            {
                gaps.Sort();
                medianGap = gaps[gaps.Count / 2];
            }
            var threshold = medianGap > 0 ? medianGap * 1.6 : 0;

            var paras = new List<ParagraphBlock>();
            ParagraphBlock? current = null;
            LineBlock? prev = null;
            int idx = 0;

            foreach (var line in ordered)
            {
                bool newPara = false;
                if (line.IsBlank)
                {
                    if (current != null)
                    {
                        paras.Add(current);
                        current = null;
                    }
                    prev = line;
                    continue;
                }

                if (prev != null && !prev.IsBlank && line.Y.HasValue && prev.Y.HasValue && threshold > 0)
                {
                    var dy = prev.Y.Value - line.Y.Value;
                    if (dy > threshold) newPara = true;
                }

                if (current == null || newPara)
                {
                    if (current != null) paras.Add(current);
                    idx++;
                    current = new ParagraphBlock
                    {
                        Index = idx,
                        StartLine = line.Index,
                        EndLine = line.Index,
                        StartOp = line.StartOp,
                        EndOp = line.EndOp,
                        Text = line.Text
                    };
                }
                else
                {
                    current.EndLine = line.Index;
                    current.EndOp = line.EndOp;
                    current.Text = string.Join("\n", new[] { current.Text, line.Text });
                }

                prev = line;
            }

            if (current != null) paras.Add(current);
            return paras;
        }

        private static List<LineBlock> ExtractLineBlocks(PdfStream stream, PdfResources resources, double lineTol)
        {
            var blocks = new List<LineBlock>();
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return blocks;

            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources));

            var currentTokens = new List<string>();
            int textOpIndex = 0;
            int startOp = 0;
            int endOp = 0;
            double? currentY = null;
            double? lineY = null;
            int lineIndex = 0;

            void Flush(bool blankAllowed)
            {
                var text = string.Concat(currentTokens);
                var isBlank = string.IsNullOrWhiteSpace(text);
                if (isBlank && !blankAllowed)
                {
                    currentTokens.Clear();
                    lineY = null;
                    return;
                }

                lineIndex++;
                blocks.Add(new LineBlock
                {
                    Index = lineIndex,
                    StartOp = startOp,
                    EndOp = endOp,
                    Text = text,
                    Y = lineY,
                    IsBlank = isBlank
                });
                currentTokens.Clear();
                lineY = null;
            }

            foreach (var tok in tokens)
            {
                if (!Operators.Contains(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                bool flushed = false;
                if (tok == "BT" || tok == "ET" || tok == "T*")
                {
                    if (currentTokens.Count > 0)
                    {
                        Flush(true);
                        flushed = true;
                    }
                    currentY = null;
                }
                else if (tok == "Tm" && operands.Count >= 6)
                {
                    if (TryParseNumber(operands[^1], out var y))
                    {
                        var changed = currentY.HasValue && Math.Abs(y - currentY.Value) > lineTol;
                        currentY = y;
                        if (changed && currentTokens.Count > 0)
                        {
                            Flush(true);
                            flushed = true;
                        }
                    }
                }
                else if ((tok == "Td" || tok == "TD") && operands.Count >= 2)
                {
                    if (TryParseNumber(operands[^1], out var ty))
                    {
                        var newY = currentY.HasValue ? currentY.Value + ty : ty;
                        var changed = currentY.HasValue && Math.Abs(newY - currentY.Value) > lineTol;
                        currentY = newY;
                        if (changed && currentTokens.Count > 0)
                        {
                            Flush(true);
                            flushed = true;
                        }
                    }
                }

                if (IsTextShowingOperator(tok))
                {
                    textOpIndex++;
                    var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                    var decoded = DequeueDecodedText(tok, operands, rawLine, textQueue) ?? "";
                    if (currentTokens.Count == 0)
                        startOp = textOpIndex;
                    endOp = textOpIndex;
                    currentTokens.Add(decoded);
                    if (!lineY.HasValue && currentY.HasValue)
                        lineY = currentY;
                }

                if (flushed)
                {
                    operands.Clear();
                    continue;
                }

                operands.Clear();
            }

            if (currentTokens.Count > 0)
                Flush(true);

            return blocks;
        }

        private static bool IsTextShowingOperator(string op)
        {
            return op == "Tj" || op == "TJ" || op == "'" || op == "\"";
        }

        private static string DequeueDecodedText(string op, List<string> operands, string? rawLine, Queue<string> textQueue)
        {
            if (op == "TJ")
            {
                var operandsText = operands.Count > 0 ? string.Join(" ", operands) : "";
                var arrayToken = ExtractArrayToken(operandsText);
                var needed = CountTextChunksInArray(arrayToken);
                if (needed <= 1)
                    return textQueue.Count > 0 ? textQueue.Dequeue() : "";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < needed && textQueue.Count > 0; i++)
                    sb.Append(textQueue.Dequeue());
                return sb.ToString();
            }

            return textQueue.Count > 0 ? textQueue.Dequeue() : "";
        }

        private static string ExtractArrayToken(string operands)
        {
            if (string.IsNullOrWhiteSpace(operands)) return "";
            int start = operands.IndexOf('[');
            int end = operands.LastIndexOf(']');
            if (start >= 0 && end > start)
                return operands.Substring(start, end - start + 1);
            return "";
        }

        private static int CountTextChunksInArray(string? arrayToken)
        {
            if (string.IsNullOrWhiteSpace(arrayToken)) return 0;
            int count = 0;
            int i = 0;
            while (i < arrayToken.Length)
            {
                char c = arrayToken[i];
                if (c == '(')
                {
                    ReadLiteralString(arrayToken, ref i);
                    count++;
                    continue;
                }
                if (c == '<')
                {
                    ReadHexString(arrayToken, ref i);
                    count++;
                    continue;
                }
                i++;
            }
            return count;
        }

        private static string ReadLiteralString(string text, ref int i)
        {
            int start = i;
            i++;
            int depth = 1;
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')') depth--;
                i++;
            }
            return text.Substring(start, i - start);
        }

        private static string ReadHexString(string text, ref int i)
        {
            int start = i;
            i++;
            while (i < text.Length && text[i] != '>') i++;
            if (i < text.Length) i++;
            return text.Substring(start, i - start);
        }

        private static bool TryParseNumber(string token, out double value)
        {
            return double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static byte[] ExtractStreamBytes(PdfStream stream)
        {
            try { return stream.GetBytes(); } catch { return Array.Empty<byte>(); }
        }

        private static List<string> TokenizeContent(byte[] bytes)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c)) { i++; continue; }
                if (c == '%') { i = SkipToEol(bytes, i); continue; }
                if (c == '(') { tokens.Add(ReadLiteralString(bytes, ref i)); continue; }
                if (c == '<')
                {
                    if (i + 1 < bytes.Length && bytes[i + 1] == '<')
                    {
                        tokens.Add(ReadBalanced(bytes, ref i, "<<", ">>"));
                        continue;
                    }
                    tokens.Add(ReadHexString(bytes, ref i));
                    continue;
                }
                if (c == '[') { tokens.Add(ReadArray(bytes, ref i)); continue; }
                if (c == '/') { tokens.Add(ReadName(bytes, ref i)); continue; }
                tokens.Add(ReadToken(bytes, ref i));
            }
            return tokens;
        }

        private static string ReadToken(byte[] bytes, ref int i)
        {
            int start = i;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadName(byte[] bytes, ref int i)
        {
            int start = i;
            i++;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadLiteralString(byte[] bytes, ref int i)
        {
            int start = i;
            i++;
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i++];
                if (b == '\\')
                {
                    if (i < bytes.Length) i++;
                    continue;
                }
                if (b == '(') depth++;
                else if (b == ')') depth--;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadHexString(byte[] bytes, ref int i)
        {
            int start = i;
            i++;
            while (i < bytes.Length && bytes[i] != '>')
                i++;
            if (i < bytes.Length) i++;
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadBalanced(byte[] bytes, ref int i, string startToken, string endToken)
        {
            int start = i;
            i += startToken.Length;
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                if (Match(bytes, i, startToken))
                {
                    depth++;
                    i += startToken.Length;
                    continue;
                }
                if (Match(bytes, i, endToken))
                {
                    depth--;
                    i += endToken.Length;
                    continue;
                }
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadArray(byte[] bytes, ref int i)
        {
            int start = i;
            i++;
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i];
                if (b == '[') depth++;
                else if (b == ']') depth--;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static bool Match(byte[] bytes, int idx, string token)
        {
            if (idx + token.Length > bytes.Length) return false;
            for (int j = 0; j < token.Length; j++)
                if (bytes[idx + j] != token[j]) return false;
            return true;
        }

        private static int SkipToEol(byte[] bytes, int i)
        {
            while (i < bytes.Length)
            {
                byte b = bytes[i++];
                if (b == '\n' || b == '\r') break;
            }
            return i;
        }

        private static bool IsWhite(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        private static bool IsDelimiter(char c)
        {
            return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
        }

        private static readonly HashSet<string> TextOperators = new HashSet<string>
        {
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\""
        };

        private static readonly HashSet<string> Operators = new HashSet<string>
        {
            "q","Q","cm","w","J","j","M","d","ri","i","gs",
            "m","l","c","v","y","h","re","S","s","f","F","f*","B","B*","b","b*","n",
            "W","W*",
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\"",
            "Do","BI","ID","EI"
        };

        private static IEnumerable<int> ParsePageList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) yield break;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), out var n)) yield return n;
        }
    }
}
