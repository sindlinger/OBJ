using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FilterPDF.Models;
using FilterPDF.Utils;
using FilterPDF.TjpbDespachoExtractor.Utils;
using Obj.Align;
using Obj.DocDetector;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    internal static class ObjectsFindDespacho
    {
        private sealed class Hit
        {
            public int Page { get; set; }
            public int Obj { get; set; }
            public string Snippet { get; set; } = "";
        }

        private sealed class StreamInfo
        {
            public int Obj { get; set; }
            public int Len { get; set; }
            public bool HasHit { get; set; }
            public string Snippet { get; set; } = "";
        }

        private static readonly string DefaultOutDir =
            Path.Combine("outputs", "objects_despacho");
        private const string DefaultDoc = "tjpb_despacho";

        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var inputFile, out var inputDir, out var outDir, out var regex, out var rangeStart, out var rangeEnd, out var backtailStart, out var backtailEnd, out var page, out var roiDoc, out var useRoi))
                return;

            if (string.IsNullOrWhiteSpace(outDir))
                outDir = DefaultOutDir;

            if (!string.IsNullOrWhiteSpace(inputDir))
            {
                if (!Directory.Exists(inputDir))
                {
                    ShowHelp();
                    return;
                }

                var files = Directory.GetFiles(inputDir, "*.pdf")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF encontrado no diretorio.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);

                foreach (var file in files)
                {
                    Console.WriteLine("================================================================================");
                    ProcessFile(file, outDir, regex, rangeStart, rangeEnd, backtailStart, backtailEnd, page, roiDoc, useRoi);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                ShowHelp();
                return;
            }

            if (!string.IsNullOrWhiteSpace(outDir))
                Directory.CreateDirectory(outDir);

            ProcessFile(inputFile, outDir, regex, rangeStart, rangeEnd, backtailStart, backtailEnd, page, roiDoc, useRoi);
        }

        private static void ProcessFile(string inputFile, string outDir, string regex, string rangeStart, string rangeEnd, string backtailStart, string backtailEnd, int page, string roiDoc, bool useRoi)
        {
            using var doc = new PdfDocument(new PdfReader(inputFile));
            var pages = ResolveDespachoPages(doc, page, roiDoc);
            if (pages.Count == 0)
            {
                Console.WriteLine("Nenhum despacho encontrado (pages=0).");
                return;
            }

            var hitRegex = BuildHitRegex(regex);
            var totalPages = doc.GetNumberOfPages();
            var multi = pages.Count > 1;

            foreach (var targetPage in pages)
            {
                if (targetPage < 1 || targetPage > totalPages)
                {
                    Console.WriteLine($"Pagina fora do PDF: {targetPage}");
                    continue;
                }

                var (pageObj, streams) = GetStreamsForPage(doc, targetPage, hitRegex);
                if (streams.Count == 0)
                {
                    Console.WriteLine($"Nenhum stream em /Contents na pagina {targetPage}.");
                    continue;
                }

                // Sempre usa o maior stream para o corpo; hits podem vir de streams menores (titulo/linha do despacho).
                var selected = streams.OrderByDescending(s => s.Len).First();

                Console.WriteLine($"PDF: {Path.GetFileName(inputFile)}");
                Console.WriteLine($"page={targetPage} page_obj=[{pageObj}] hits={streams.Count(s => s.HasHit)}");
                Console.WriteLine("streams:");
                var maxLen = streams.Max(s => s.Len);
                foreach (var stream in streams.OrderBy(s => s.Len))
                {
                    var flags = new List<string>();
                    if (stream.Obj == selected.Obj) flags.Add("SELECTED");
                    if (stream.HasHit) flags.Add("HIT");
                    if (stream.Len == maxLen) flags.Add("LARGEST");
                    var flagText = flags.Count > 0 ? $" [{string.Join(",", flags)}]" : "";
                    Console.WriteLine($"  obj={stream.Obj} len={stream.Len}{flagText}");
                    if (stream.HasHit && !string.IsNullOrWhiteSpace(stream.Snippet))
                        Console.WriteLine($"    snippet: {stream.Snippet}");
                }

                Console.WriteLine();
                var useRoiForPage = useRoi;
                if (useRoiForPage)
                {
                    var roiPath = DespachoContentsDetector.ResolveRoiPathForObj(roiDoc, selected.Obj);
                    if (string.IsNullOrWhiteSpace(roiPath))
                    {
                        Console.WriteLine($"ROI nao encontrado para obj={selected.Obj}; pulando pagina (sem regex).");
                        continue;
                    }
                }

                Console.WriteLine("== Operadores (Tj/TJ) - recorte por range (front/head) ==");
                var diffArgs = new List<string>
                {
                    "--inputs", $"{inputFile},{inputFile}",
                    "--obj", selected.Obj.ToString(CultureInfo.InvariantCulture),
                    "--op", "Tj,TJ",
                    "--range-text"
                };
                if (useRoiForPage)
                {
                    diffArgs.Add("--doc");
                    diffArgs.Add(roiDoc);
                }
                else
                {
                    diffArgs.Add("--range-start");
                    diffArgs.Add(rangeStart);
                    diffArgs.Add("--range-end");
                    diffArgs.Add(rangeEnd);
                }
                var frontOut = CaptureOutputPlain(() => ObjectsTextOpsDiff.Execute(diffArgs.ToArray(), ObjectsTextOpsDiff.DiffMode.Both));
                Console.Write(frontOut);
                var frontText = ExtractRangeText(frontOut, inputFile);

                var nextPage = targetPage + 1;
                if (nextPage > totalPages)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Sem backtail: pagina {nextPage} nao existe.");
                    continue;
                }

                var (backPageObj, backStreams) = GetStreamsForPage(doc, nextPage, hitRegex);
                if (backStreams.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Sem backtail: nenhum stream em /Contents na pagina {nextPage}.");
                    continue;
                }

                // Backtail também usa o maior stream; hits podem estar em streams menores.
                var backSelected = backStreams.OrderByDescending(s => s.Len).FirstOrDefault();

                Console.WriteLine();
                Console.WriteLine($"== Backtail (pagina {nextPage}) ==");
                Console.WriteLine($"page={nextPage} page_obj=[{backPageObj}] hits={backStreams.Count(s => s.HasHit)}");
                Console.WriteLine("streams:");
                var maxLenBack = backStreams.Max(s => s.Len);
                foreach (var stream in backStreams.OrderBy(s => s.Len))
                {
                    var flags = new List<string>();
                    if (backSelected != null && stream.Obj == backSelected.Obj) flags.Add("SELECTED");
                    if (stream.HasHit) flags.Add("HIT");
                    if (stream.Len == maxLenBack) flags.Add("LARGEST");
                    var flagText = flags.Count > 0 ? $" [{string.Join(",", flags)}]" : "";
                    Console.WriteLine($"  obj={stream.Obj} len={stream.Len}{flagText}");
                    if (stream.HasHit && !string.IsNullOrWhiteSpace(stream.Snippet))
                        Console.WriteLine($"    snippet: {stream.Snippet}");
                }

                if (backSelected == null)
                {
                    Console.WriteLine("Nenhum stream selecionado para backtail.");
                    continue;
                }

                Console.WriteLine();
                Console.WriteLine("== Operadores (Tj/TJ) - recorte por range (backtail) ==");
                var backDiffArgs = new List<string>
                {
                    "--inputs", $"{inputFile},{inputFile}",
                    "--obj", backSelected.Obj.ToString(CultureInfo.InvariantCulture),
                    "--op", "Tj,TJ",
                    "--range-text"
                };
                if (useRoiForPage)
                {
                    backDiffArgs.Add("--doc");
                    backDiffArgs.Add(roiDoc);
                }
                else
                {
                    backDiffArgs.Add("--range-start");
                    backDiffArgs.Add(backtailStart);
                    if (!string.IsNullOrWhiteSpace(backtailEnd))
                    {
                        backDiffArgs.Add("--range-end");
                        backDiffArgs.Add(backtailEnd);
                    }
                    else
                    {
                        backDiffArgs.Add("--range-end-op");
                        backDiffArgs.Add("999999");
                    }
                }
                var backOut = CaptureOutputPlain(() => ObjectsTextOpsDiff.Execute(backDiffArgs.ToArray(), ObjectsTextOpsDiff.DiffMode.Both));
                Console.Write(backOut);
                var backText = ExtractRangeText(backOut, inputFile);

                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    var baseName = Path.GetFileNameWithoutExtension(inputFile);
                    var suffix = multi ? $"__p{targetPage}" : "";
                    var outPath = Path.Combine(outDir, $"{baseName}{suffix}.txt");
                    var combined = CombineText(frontText, backText);
                    File.WriteAllText(outPath, combined ?? string.Empty);
                    Console.WriteLine();
                    Console.WriteLine($"Saida salva em: {outPath}");
                }

                if (multi)
                    Console.WriteLine("--------------------------------------------------------------------------------");
            }
        }

        private static bool ParseOptions(string[] args, out string inputFile, out string inputDir, out string outDir, out string regex, out string rangeStart, out string rangeEnd, out string backtailStart, out string backtailEnd, out int page, out string roiDoc, out bool useRoi)
        {
            inputFile = "";
            inputDir = "";
            outDir = "";
            page = 0;
            regex = "";
            rangeStart = "";
            rangeEnd = "";
            backtailStart = "";
            backtailEnd = "";
            roiDoc = DefaultDoc;
            useRoi = true;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--input-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputDir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--out-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--regex", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    regex = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--range-start", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rangeStart = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--range-end", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rangeEnd = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--backtail-start", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backtailStart = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--backtail-end", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backtailEnd = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--doc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    roiDoc = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--no-roi", StringComparison.OrdinalIgnoreCase))
                {
                    useRoi = false;
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        page = n;
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(inputFile) && string.IsNullOrWhiteSpace(inputDir))
                {
                    var raw = arg.Trim();
                    if (Directory.Exists(raw))
                        inputDir = raw;
                    else
                        inputFile = raw;
                }
            }
            if (!useRoi)
            {
                if (string.IsNullOrWhiteSpace(rangeStart)
                    || string.IsNullOrWhiteSpace(rangeEnd)
                    || string.IsNullOrWhiteSpace(backtailStart))
                {
                    Console.WriteLine("Modo manual (--no-roi) exige --range-start, --range-end e --backtail-start.");
                    return false;
                }
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects despacho --input file.pdf [--page N]");
            Console.WriteLine("tjpdf-cli inspect objects despacho --input-dir <dir> [--page N]");
            Console.WriteLine("  [--out-dir <dir>]");
            Console.WriteLine("  [--regex <pat>] (manual/debug)");
            Console.WriteLine("  [--range-start <pat>] [--range-end <pat>] (manual)");
            Console.WriteLine("  [--backtail-start <pat>] [--backtail-end <pat>] (manual)");
            Console.WriteLine($"  default out-dir: {DefaultOutDir}");
            Console.WriteLine($"  default doc (ROI): {DefaultDoc}  (use --no-roi for manual ranges)");
        }

        private static string CaptureOutputPlain(Action action)
        {
            var original = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try { action(); }
            finally { Console.SetOut(original); }
            return sw.ToString();
        }

        private static List<Hit> ParseFindHits(string output)
        {
            var hits = new List<Hit>();
            var clean = StripAnsi(output);
            var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.StartsWith("=== page=", StringComparison.OrdinalIgnoreCase))
                    continue;
                var m = Regex.Match(line, @"page=(\d+).*obj=\[(\d+)\]");
                if (!m.Success) continue;
                var page = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var obj = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var snippet = "";
                if (i + 2 < lines.Length)
                    snippet = lines[i + 2].Trim();
                hits.Add(new Hit { Page = page, Obj = obj, Snippet = snippet });
            }
            return hits;
        }

        private static (int pageObj, List<StreamInfo> streams) ParseListStreams(string output)
        {
            var streams = new List<StreamInfo>();
            var clean = StripAnsi(output);
            var pageObj = 0;
            var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (pageObj == 0)
                {
                    var mPage = Regex.Match(line, @"page_obj=\[(\d+)\]");
                    if (mPage.Success)
                        pageObj = int.Parse(mPage.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                var m = Regex.Match(line, @"kind=contents obj=\[(\d+)\].*len=(\d+)");
                if (!m.Success) continue;
                streams.Add(new StreamInfo
                {
                    Obj = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    Len = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                });
            }
            return (pageObj, streams);
        }

        private static string StripAnsi(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"\x1b\[[0-9;]*[A-Za-z]", "");
        }

        private static string? ExtractRangeText(string output, string inputFile)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            var clean = StripAnsi(output);
            var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var full = inputFile;
            var baseName = Path.GetFileName(inputFile);
            foreach (var line in lines)
            {
                var text = TryExtractRangeTextFromLine(line, full) ?? TryExtractRangeTextFromLine(line, baseName);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return null;
        }

        private static string? TryExtractRangeTextFromLine(string line, string name)
        {
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(name)) return null;
            var marker = name + ": \"";
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var start = idx + marker.Length;
            var end = line.LastIndexOf("\" (len=", StringComparison.Ordinal);
            if (end > start)
                return line.Substring(start, end - start);
            return null;
        }

        private static string CombineText(string? front, string? back)
        {
            var a = front?.Trim() ?? "";
            var b = back?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
                return "";
            if (string.IsNullOrWhiteSpace(a))
                return b;
            if (string.IsNullOrWhiteSpace(b))
                return a;
            return a + Environment.NewLine + Environment.NewLine + b;
        }

        private static List<int> ResolveDespachoPages(PdfDocument doc, int requestedPage, string roiDoc)
        {
            if (requestedPage > 0)
                return new List<int> { requestedPage };

            var pages = GetDespachoBookmarkPages(doc);
            if (pages.Count > 0)
            {
                var allPages = GetAllBookmarkPages(doc);
                var total = doc.GetNumberOfPages();
                var selected = new HashSet<int>();
                foreach (var start in pages.OrderBy(p => p))
                {
                    var next = allPages.FirstOrDefault(p => p > start);
                    var end = next > 0 ? next - 1 : total;
                    if (start >= 1 && start <= total)
                        selected.Add(start);
                    var second = start + 1;
                    if (second <= end && second <= total)
                        selected.Add(second);
                }
                return selected.OrderBy(p => p).ToList();
            }

            var headerLabels = DespachoContentsDetector.GetDespachoHeaderLabels(roiDoc, 0);
            return FindDespachoPagesByContents(doc, headerLabels);
        }

        private static List<int> GetDespachoBookmarkPages(PdfDocument doc)
        {
            var pages = new HashSet<int>();
            var items = BookmarkExtractor.Extract(doc);
            foreach (var item in items)
                CollectBookmarkPages(item, pages);
            return pages.OrderBy(p => p).ToList();
        }

        private static List<int> GetAllBookmarkPages(PdfDocument doc)
        {
            var pages = new HashSet<int>();
            var items = BookmarkExtractor.Extract(doc);
            foreach (var item in items)
                CollectAllBookmarkPages(item, pages);
            return pages.OrderBy(p => p).ToList();
        }

        private static void CollectBookmarkPages(BookmarkItem item, HashSet<int> pages)
        {
            if (item == null)
                return;
            var title = item.Title ?? "";
            var page = item.Destination?.PageNumber ?? 0;
            if (page > 0 && title.IndexOf("despacho", StringComparison.OrdinalIgnoreCase) >= 0)
                pages.Add(page);
            if (item.Children == null || item.Children.Count == 0)
                return;
            foreach (var child in item.Children)
                CollectBookmarkPages(child, pages);
        }

        private static void CollectAllBookmarkPages(BookmarkItem item, HashSet<int> pages)
        {
            if (item == null)
                return;
            var page = item.Destination?.PageNumber ?? 0;
            if (page > 0)
                pages.Add(page);
            if (item.Children == null || item.Children.Count == 0)
                return;
            foreach (var child in item.Children)
                CollectAllBookmarkPages(child, pages);
        }

        private static List<int> FindDespachoPagesByContents(PdfDocument doc, IReadOnlyList<string> headerLabels)
        {
            var pages = new List<int>();
            if (doc == null)
                return pages;

            int total = doc.GetNumberOfPages();
            for (int p = 1; p <= total; p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var streams = EnumerateStreams(contents).ToList();
                if (streams.Count == 0)
                {
                    Console.WriteLine($"Aviso: /Contents vazio na pagina {p}.");
                    continue;
                }

                foreach (var stream in streams)
                {
                    if (HasTitlePrefix(stream, resources))
                    {
                        pages.Add(p);
                        goto NextPage;
                    }
                }

                foreach (var stream in streams)
                {
                    var text = CollapseForMatch(ExtractStreamTextRaw(stream, resources));
                    if (ContainsHeaderLabel(text, headerLabels))
                    {
                        pages.Add(p);
                        break;
                    }
                }

            NextPage:
                continue;
            }

            return pages;
        }

        private static void AddCandidate(List<PdfStream> list, PdfStream stream)
        {
            if (stream == null)
                return;
            if (!list.Contains(stream))
                list.Add(stream);
        }

        private static Regex? BuildHitRegex(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            try
            {
                return new Regex(raw, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                return null;
            }
        }

        private static (int pageObj, List<StreamInfo> streams) GetStreamsForPage(PdfDocument doc, int pageNumber, Regex? hitRegex)
        {
            if (doc == null || pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                return (0, new List<StreamInfo>());

            var page = doc.GetPage(pageNumber);
            var pageObj = page.GetPdfObject().GetIndirectReference()?.GetObjNumber() ?? 0;
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            var streams = new List<StreamInfo>();

            foreach (var stream in EnumerateStreams(contents))
            {
                int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                var info = new StreamInfo
                {
                    Obj = objId,
                    Len = stream.GetLength()
                };

                var textRaw = ExtractStreamTextRaw(stream, resources);
                if (hitRegex != null && !string.IsNullOrWhiteSpace(textRaw))
                {
                    var mRaw = hitRegex.Match(textRaw);
                    if (mRaw.Success)
                    {
                        info.HasHit = true;
                        info.Snippet = BuildSnippet(textRaw, mRaw.Index, mRaw.Length);
                    }
                    else
                    {
                        var textMatch = CollapseForMatch(textRaw);
                        var mCollapsed = hitRegex.Match(textMatch);
                        if (mCollapsed.Success)
                        {
                            info.HasHit = true;
                            info.Snippet = BuildSnippet(textRaw, 0, Math.Min(textRaw.Length, 120));
                        }
                    }
                }

                streams.Add(info);
            }

            return (pageObj, streams);
        }

        private static string BuildSnippet(string text, int index, int length)
        {
            const int context = 60;
            var start = Math.Max(0, index - context);
            var end = Math.Min(text.Length, index + length + context);
            var snippet = text.Substring(start, end - start).Trim();
            return snippet;
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
                {
                    if (item is PdfStream ss) yield return ss;
                }
            }
        }

        private static string ExtractStreamTextRaw(PdfStream stream, PdfResources resources)
        {
            try
            {
                if (PdfTextExtraction.TryExtractStreamText(stream, resources, out var text, out _)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    return NormalizeSpaces(text);
                }
                var parts = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var joined = string.Join(" ", parts);
                return NormalizeSpaces(joined);
            }
            catch
            {
                return "";
            }
        }

        private static string CollapseForMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            return TextUtils.CollapseSpacedLettersText(text);
        }

        private static bool HasTitlePrefix(PdfStream stream, PdfResources resources)
        {
            if (stream == null)
                return false;
            var prefix = ExtractPrefixText(stream, resources, 20);
            if (string.IsNullOrWhiteSpace(prefix))
                return false;
            var norm = TextUtils.NormalizeForMatch(prefix);
            return norm.Contains("despacho");
        }

        private static bool ContainsHeaderLabel(string text, IReadOnlyList<string> headerLabels)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var norm = TextUtils.NormalizeForMatch(text);
            if (headerLabels != null && headerLabels.Count > 0)
            {
                foreach (var label in headerLabels)
                {
                    if (string.IsNullOrWhiteSpace(label))
                        continue;
                    if (norm.Contains(label, StringComparison.Ordinal))
                        return true;
                }
            }
            return norm.Contains("diretoria especial", StringComparison.Ordinal);
        }

        private static string ExtractPrefixText(PdfStream stream, PdfResources resources, int maxOps)
        {
            try
            {
                var parts = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var take = Math.Max(1, Math.Min(maxOps, parts.Count));
                return string.Join(" ", parts.Take(take));
            }
            catch
            {
                return "";
            }
        }

        private static string NormalizeSpaces(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            return Regex.Replace(text, "\\s+", " ").Trim();
        }
    }
}
