using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Compara objetos de conteúdo por fingerprint estrutural (sem usar texto).
    /// Uso:
    ///   tjpdf-cli inspect objects objdiff --input target.pdf --inputs a.pdf,b.pdf [--input-dir <dir>] [--page N|--pages 1,3]
    /// </summary>
    internal static class ObjectsObjDiff
    {
        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var targetFile, out var templateFiles, out var pages, out var top, out var minScore, out var textOnly, out var yMin, out var yMax))
            {
                ShowHelp();
                return;
            }

            if (string.IsNullOrWhiteSpace(targetFile) || !File.Exists(targetFile))
            {
                Console.WriteLine("Informe um --input válido (arquivo alvo).");
                ShowHelp();
                return;
            }
            if (templateFiles.Count == 0)
            {
                Console.WriteLine("Informe templates via --inputs ou --input-dir.");
                ShowHelp();
                return;
            }

            var templates = new List<ObjectFingerprint>();
            foreach (var file in templateFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(file)) continue;
                try
                {
                    templates.AddRange(BuildContentFingerprints(file, pages, textOnly, yMin, yMax));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[template erro] {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            if (templates.Count == 0)
            {
                Console.WriteLine("Nenhum template válido para comparar.");
                return;
            }

            var targets = BuildContentFingerprints(targetFile, pages, textOnly, yMin, yMax);
            if (targets.Count == 0)
            {
                Console.WriteLine("Nenhum objeto de conteúdo encontrado no alvo.");
                return;
            }

            Console.WriteLine($"alvo: {Path.GetFileName(targetFile)} objects={targets.Count} templates={templates.Count}");
            foreach (var target in targets)
            {
                var matches = templates.Select(t => BuildMatch(target, t))
                                       .OrderByDescending(m => m.Score)
                                       .Take(top)
                                       .ToList();
                var best = matches.FirstOrDefault();
                if (best == null || best.Score < minScore)
                    continue;

                var yLabel = target.MinNy.HasValue && target.MaxNy.HasValue
                    ? $" y={target.MinNy.Value:F2}-{target.MaxNy.Value:F2}"
                    : "";
                Console.WriteLine($"p{target.Page} obj={target.ObjId}{yLabel} len={target.Length} filters={target.Filters} fonts={FormatFonts(target.Fonts)}");
                foreach (var m in matches)
                {
                    Console.WriteLine($"  score={m.Score:F2} ops={m.OpsScore:F2} len={m.LenScore:F2} fonts={m.FontScore:F2} filt={m.FilterScore:F2} :: {Path.GetFileName(m.Template.File)} p{m.Template.Page} obj={m.Template.ObjId}");
                }
            }
        }

        private static bool ParseOptions(string[] args, out string targetFile, out List<string> templateFiles, out List<int> pages, out int top, out double minScore, out bool textOnly, out double? yMin, out double? yMax)
        {
            targetFile = "";
            templateFiles = new List<string>();
            pages = new List<int>();
            top = 3;
            minScore = 0.70;
            textOnly = true;
            yMin = null;
            yMax = null;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    targetFile = args[++i];
                    continue;
                }
                if ((string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--templates", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        templateFiles.Add(raw.Trim());
                    continue;
                }
                if ((string.Equals(arg, "--input-dir", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--templates-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var dir = args[++i];
                    if (Directory.Exists(dir))
                        templateFiles.AddRange(Directory.GetFiles(dir, "*.pdf", SearchOption.AllDirectories));
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
                if (string.Equals(arg, "--top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var n)) top = Math.Max(1, n);
                    continue;
                }
                if (string.Equals(arg, "--min-score", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        minScore = Math.Max(0, Math.Min(1, v));
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
                if (string.Equals(arg, "--band", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var band = args[++i].Trim().ToLowerInvariant();
                    if (band == "front" || band == "top")
                    {
                        yMin = 0.65;
                        yMax = 1.0;
                    }
                    else if (band == "back" || band == "bottom" || band == "tail")
                    {
                        yMin = 0.0;
                        yMax = 0.35;
                    }
                    continue;
                }
                if (string.Equals(arg, "--all-contents", StringComparison.OrdinalIgnoreCase))
                {
                    textOnly = false;
                    continue;
                }
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects objdiff --input target.pdf --inputs a.pdf,b.pdf [--input-dir <dir>]");
            Console.WriteLine("                                     [--page N|--pages 1,3] [--top N] [--min-score 0.70] [--all-contents]");
            Console.WriteLine("                                     [--band front|back] [--y-range 0.70-1.00]");
            Console.WriteLine("Compara objetos de conteúdo por fingerprint estrutural (sem usar texto).");
        }

        private static List<ObjectFingerprint> BuildContentFingerprints(string file, List<int> pageFilter, bool textOnly, double? yMin, double? yMax)
        {
            var list = new List<ObjectFingerprint>();
            using var reader = new PdfReader(file);
            reader.SetUnethicalReading(true);
            using var doc = new PdfDocument(reader);
            var pages = pageFilter.Count > 0
                ? pageFilter.Distinct().Where(p => p >= 1 && p <= doc.GetNumberOfPages()).OrderBy(p => p).ToList()
                : Enumerable.Range(1, doc.GetNumberOfPages()).ToList();

            foreach (var p in pages)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var pageHeight = page.GetPageSize().GetHeight();
                foreach (var stream in EnumerateStreams(contents))
                {
                    var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    var fp = BuildFingerprint(file, p, id, stream, resources);
                    if (pageHeight > 0 && TryGetTextYRange(stream, resources, pageHeight, out var minNy, out var maxNy))
                    {
                        fp.MinNy = minNy;
                        fp.MaxNy = maxNy;
                        if (yMin.HasValue && yMax.HasValue && !RangesOverlap(minNy, maxNy, yMin.Value, yMax.Value))
                            continue;
                    }
                    else if (yMin.HasValue && yMax.HasValue)
                    {
                        continue;
                    }
                    if (textOnly && fp.OpsCount == 0)
                        continue;
                    list.Add(fp);
                }
            }

            return list;
        }

        private static ObjectFingerprint BuildFingerprint(string file, int page, int objId, PdfStream stream, PdfResources resources)
        {
            var ops = new Dictionary<string, int>(StringComparer.Ordinal);
            var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bytes = ExtractStreamBytes(stream);
            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (TextOperators.Contains(tok))
                {
                    ops[tok] = ops.TryGetValue(tok, out var c) ? c + 1 : 1;
                    if (tok == "Tf" && operands.Count >= 2)
                    {
                        var font = operands[^2];
                        if (!string.IsNullOrWhiteSpace(font) && font.StartsWith("/", StringComparison.Ordinal))
                            fonts.Add(font);
                    }
                }

                operands.Clear();
            }

            var filters = GetStreamFilters(stream);
            return new ObjectFingerprint
            {
                File = file,
                Page = page,
                ObjId = objId,
                Length = stream.GetLength(),
                Filters = filters,
                Ops = ops,
                Fonts = fonts
            };
        }

        private static MatchResult BuildMatch(ObjectFingerprint target, ObjectFingerprint template)
        {
            var opsScore = ComputeOpsSimilarity(target.Ops, template.Ops);
            var lenScore = ComputeLenScore(target.Length, template.Length);
            var fontScore = ComputeFontScore(target.Fonts, template.Fonts);
            var filterScore = string.Equals(target.Filters, template.Filters, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

            var score = 0.60 * opsScore + 0.20 * fontScore + 0.10 * lenScore + 0.10 * filterScore;
            return new MatchResult
            {
                Target = target,
                Template = template,
                Score = score,
                OpsScore = opsScore,
                LenScore = lenScore,
                FontScore = fontScore,
                FilterScore = filterScore
            };
        }

        private static double ComputeOpsSimilarity(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            var all = TextOperators;
            double sumMax = 0;
            double sumDiff = 0;
            foreach (var op in all)
            {
                a.TryGetValue(op, out var ca);
                b.TryGetValue(op, out var cb);
                var max = Math.Max(ca, cb);
                sumMax += max;
                sumDiff += Math.Abs(ca - cb);
            }
            if (sumMax == 0) return 0;
            return Math.Max(0, 1.0 - (sumDiff / sumMax));
        }

        private static double ComputeLenScore(long a, long b)
        {
            if (a <= 0 && b <= 0) return 1.0;
            var max = Math.Max(a, b);
            if (max <= 0) return 0;
            return Math.Max(0, 1.0 - (Math.Abs(a - b) / (double)max));
        }

        private static double ComputeFontScore(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 0;
            var inter = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
            var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
            if (union == 0) return 0;
            return inter / (double)union;
        }

        private static string FormatFonts(HashSet<string> fonts)
        {
            if (fonts.Count == 0) return "-";
            return string.Join(",", fonts.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
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

        private static byte[] ExtractStreamBytes(PdfStream stream)
        {
            try { return stream.GetBytes(); } catch { return Array.Empty<byte>(); }
        }

        private static string GetStreamFilters(PdfStream stream)
        {
            var filterObj = stream.Get(PdfName.Filter);
            if (filterObj == null) return "none";
            if (filterObj is PdfName name) return name.GetValue();
            if (filterObj is PdfArray arr)
            {
                var parts = new List<string>();
                foreach (var item in arr)
                {
                    if (item is PdfName n) parts.Add(n.GetValue());
                    else if (item != null) parts.Add(item.ToString() ?? "");
                }
                return string.Join(",", parts);
            }
            return filterObj.ToString() ?? "unknown";
        }

        private static List<string> TokenizeContent(byte[] bytes)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c))
                {
                    i++;
                    continue;
                }
                if (c == '%')
                {
                    i = SkipToEol(bytes, i);
                    continue;
                }
                if (c == '(')
                {
                    tokens.Add(ReadLiteralString(bytes, ref i));
                    continue;
                }
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
                if (c == '[')
                {
                    tokens.Add(ReadArray(bytes, ref i));
                    continue;
                }
                if (c == '/')
                {
                    tokens.Add(ReadName(bytes, ref i));
                    continue;
                }
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
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadName(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // skip '/'
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadLiteralString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '('
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
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadHexString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < bytes.Length && bytes[i] != '>')
                i++;
            if (i < bytes.Length) i++;
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
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
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadArray(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '['
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i];
                if (b == '[') depth++;
                else if (b == ']') depth--;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
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

        private static bool IsOperatorToken(string token)
        {
            return Operators.Contains(token);
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

        private sealed class ObjectFingerprint
        {
            public string File { get; set; } = "";
            public int Page { get; set; }
            public int ObjId { get; set; }
            public long Length { get; set; }
            public string Filters { get; set; } = "";
            public Dictionary<string, int> Ops { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
            public HashSet<string> Fonts { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public double? MinNy { get; set; }
            public double? MaxNy { get; set; }
            public int OpsCount => Ops.Values.Sum();
        }

        private sealed class MatchResult
        {
            public ObjectFingerprint Target { get; set; } = null!;
            public ObjectFingerprint Template { get; set; } = null!;
            public double Score { get; set; }
            public double OpsScore { get; set; }
            public double LenScore { get; set; }
            public double FontScore { get; set; }
            public double FilterScore { get; set; }
        }
    }
}
