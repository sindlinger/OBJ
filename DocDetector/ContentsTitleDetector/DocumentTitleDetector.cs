using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Util;

namespace Obj.DocDetector
{
    public sealed class DetectionOptions
    {
        public int PrefixOpCount { get; set; } = 10;
        public int MinTitleChars { get; set; } = 4;
        public int MaxTitleChars { get; set; } = 120;
        public bool CarryForward { get; set; } = true;
        public bool IncludeUnknownIntervals { get; set; } = false;
        public List<string> Keywords { get; set; } = new List<string>
        {
            "despacho",
            "diretoria especial"
        };
    }

    public sealed class PageClassification
    {
        public int Page { get; set; }
        public int ContentsObj { get; set; }
        public string Title { get; set; } = "";
        public string TitleKey { get; set; } = "";
        public string TitleNormalized { get; set; } = "";
        public string OpRange { get; set; } = "";
        public string PathRef { get; set; } = ""; // page/obj/op
        public string SourceText { get; set; } = ""; // prefix text used
        public string MatchedKeyword { get; set; } = "";
    }

    public sealed class DocumentSpan
    {
        public string Title { get; set; } = "";
        public string TitleKey { get; set; } = "";
        public int StartPage { get; set; }
        public int EndPage { get; set; }
        public string PathRef { get; set; } = ""; // first evidence
        public List<PageClassification> Pages { get; set; } = new List<PageClassification>();
    }

    public sealed class DetectionResult
    {
        public string PdfPath { get; set; } = "";
        public List<PageClassification> Pages { get; set; } = new List<PageClassification>();
        public List<DocumentSpan> Documents { get; set; } = new List<DocumentSpan>();
    }

    public static class DocumentTitleDetector
    {
        public static DetectionResult Detect(string pdfPath, DetectionOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                throw new FileNotFoundException("PDF not found", pdfPath);

            var opts = options ?? new DetectionOptions();
            var result = new DetectionResult { PdfPath = pdfPath };

            using var doc = new PdfDocument(new PdfReader(pdfPath));
            int total = doc.GetNumberOfPages();

            for (int p = 1; p <= total; p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var streams = EnumerateStreams(contents).ToList();
                if (streams.Count == 0)
                {
                    result.Pages.Add(new PageClassification { Page = p, TitleKey = "" });
                    continue;
                }

                var headerStreams = PickHeaderStreams(streams);
                var best = PickBestTitle(headerStreams, resources, opts);
                if (best == null)
                {
                    result.Pages.Add(new PageClassification { Page = p, TitleKey = "" });
                    continue;
                }

                var opRange = best.OpCount > 0
                    ? $"op0-op{Math.Max(0, best.OpCount - 1)}"
                    : "op0";

                var pathRef = $"page={p}/obj={best.ObjId}/op={opRange}";
                result.Pages.Add(new PageClassification
                {
                    Page = p,
                    ContentsObj = best.ObjId,
                    Title = best.Title,
                    TitleKey = best.TitleKey,
                    TitleNormalized = best.TitleNormalized,
                    OpRange = opRange,
                    PathRef = pathRef,
                    SourceText = best.SourceText,
                    MatchedKeyword = best.MatchedKeyword
                });
            }

            ApplyCarryForward(result.Pages, opts);
            result.Documents = BuildIntervals(result.Pages, opts);
            return result;
        }

        public static string DetectToJson(string pdfPath, DetectionOptions? options = null)
        {
            var result = Detect(pdfPath, options);
            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }

        private sealed class TitleCandidate
        {
            public int ObjId { get; set; }
            public int StreamLen { get; set; }
            public int OpCount { get; set; }
            public string Title { get; set; } = "";
            public string TitleKey { get; set; } = "";
            public string TitleNormalized { get; set; } = "";
            public string SourceText { get; set; } = "";
            public string MatchedKeyword { get; set; } = "";
            public double Score { get; set; }
        }

        private static TitleCandidate? PickBestTitle(List<PdfStream> streams, PdfResources resources, DetectionOptions opts)
        {
            TitleCandidate? best = null;

            foreach (var stream in streams)
            {
                int opCount;
                var prefix = ExtractPrefixText(stream, resources, opts.PrefixOpCount, out opCount);
                if (string.IsNullOrWhiteSpace(prefix))
                    continue;

                var normalized = Normalize(prefix);
                if (normalized.Length < opts.MinTitleChars)
                    continue;

                var title = ExtractTitle(prefix, opts.MaxTitleChars);
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var matched = FindKeyword(normalized, opts.Keywords);
                if (string.IsNullOrWhiteSpace(matched))
                    continue;

                var titleKey = matched;

                double score = 0;
                score += 100;

                var upperRatio = UppercaseRatio(title);
                if (upperRatio >= 0.6)
                    score += 10;

                if (title.Length <= opts.MaxTitleChars)
                    score += 5;

                score -= stream.GetLength() / 1000.0;

                var candidate = new TitleCandidate
                {
                    ObjId = stream.GetIndirectReference()?.GetObjNumber() ?? 0,
                    StreamLen = stream.GetLength(),
                    OpCount = opCount,
                    Title = title,
                    TitleKey = titleKey,
                    TitleNormalized = normalized,
                    SourceText = prefix,
                    MatchedKeyword = matched,
                    Score = score
                };

                if (best == null || candidate.Score > best.Score)
                    best = candidate;
            }

            return best;
        }

        private static List<DocumentSpan> BuildIntervals(List<PageClassification> pages, DetectionOptions opts)
        {
            var spans = new List<DocumentSpan>();
            if (pages.Count == 0) return spans;

            DocumentSpan? current = null;
            foreach (var page in pages.OrderBy(p => p.Page))
            {
                var key = page.TitleKey ?? "";
                if (string.IsNullOrWhiteSpace(key))
                    key = "UNKNOWN";

                if (current == null || !string.Equals(current.TitleKey, key, StringComparison.Ordinal))
                {
                    if (current != null && (opts.IncludeUnknownIntervals || current.TitleKey != "UNKNOWN"))
                        spans.Add(current);

                    current = new DocumentSpan
                    {
                        Title = page.Title,
                        TitleKey = key,
                        StartPage = page.Page,
                        EndPage = page.Page,
                        PathRef = page.PathRef
                    };
                }
                else
                {
                    current.EndPage = page.Page;
                }

                current.Pages.Add(page);
            }

            if (current != null && (opts.IncludeUnknownIntervals || current.TitleKey != "UNKNOWN"))
                spans.Add(current);

            return spans;
        }

        private static void ApplyCarryForward(List<PageClassification> pages, DetectionOptions opts)
        {
            if (!opts.CarryForward) return;

            string lastKey = "";
            string lastTitle = "";
            foreach (var page in pages.OrderBy(p => p.Page))
            {
                if (string.IsNullOrWhiteSpace(page.TitleKey))
                {
                    if (!string.IsNullOrWhiteSpace(lastKey))
                    {
                        page.TitleKey = lastKey;
                        page.Title = lastTitle;
                    }
                }
                else
                {
                    lastKey = page.TitleKey;
                    lastTitle = page.Title;
                }
            }
        }

        private static string ExtractTitle(string prefix, int maxLen)
        {
            var cleaned = CollapseSpaces(prefix).Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return "";
            var lines = cleaned.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var first = lines.Length > 0 ? lines[0].Trim() : cleaned;
            if (first.Length > maxLen)
                first = first.Substring(0, maxLen);
            return first;
        }

        private static string BuildTitleKey(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return "";
            var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            var joined = string.Join(" ", parts.Take(6));
            return joined.Length > 60 ? joined.Substring(0, 60) : joined;
        }

        private static string FindKeyword(string normalized, List<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return "";
            foreach (var raw in keywords)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var key = Normalize(raw);
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (normalized.Contains(key, StringComparison.Ordinal))
                    return key;
            }
            return "";
        }

        private static string ExtractPrefixText(PdfStream stream, PdfResources resources, int maxOps, out int opCountUsed)
        {
            opCountUsed = 0;
            try
            {
                var parts = CollectTextOperatorTexts(stream, resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var take = Math.Max(1, Math.Min(maxOps, parts.Count));
                opCountUsed = take;
                return string.Join(" ", parts.Take(take));
            }
            catch
            {
                return "";
            }
        }

        private static List<string> CollectTextOperatorTexts(PdfStream stream, PdfResources resources)
        {
            var texts = new List<string>();
            try
            {
                var collector = new TextRenderInfoCollector(texts);
                var processor = new PdfCanvasProcessor(collector);
                processor.RegisterXObjectDoHandler(PdfName.Form, new NoOpXObjectDoHandler());
                processor.ProcessContent(stream.GetBytes(), resources ?? new PdfResources(new PdfDictionary()));
            }
            catch
            {
                return texts;
            }
            return texts;
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

        private static List<PdfStream> PickHeaderStreams(List<PdfStream> streams)
        {
            if (streams.Count <= 3)
                return streams;

            var ordered = streams.OrderBy(s => s.GetLength()).ToList();
            var smallest = ordered.First();
            var largest = ordered.Last();
            var middle = ordered[ordered.Count / 2];

            var selected = new List<PdfStream>();
            AddDistinct(selected, smallest);
            AddDistinct(selected, middle);
            AddDistinct(selected, largest);
            return selected;
        }

        private static void AddDistinct(List<PdfStream> list, PdfStream stream)
        {
            foreach (var existing in list)
            {
                if (ReferenceEquals(existing, stream))
                    return;
            }
            list.Add(stream);
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool prevSpace = false;
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    prevSpace = false;
                }
                else if (char.IsWhiteSpace(ch))
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                }
            }
            return sb.ToString().Trim();
        }

        private static string CollapseSpaces(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool prevSpace = false;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                    continue;
                }
                sb.Append(ch);
                prevSpace = false;
            }
            return sb.ToString();
        }

        private static double UppercaseRatio(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            int letters = 0;
            int upper = 0;
            foreach (var ch in text)
            {
                if (!char.IsLetter(ch)) continue;
                letters++;
                if (char.IsUpper(ch)) upper++;
            }
            if (letters == 0) return 0;
            return upper / (double)letters;
        }

        private sealed class NoOpXObjectDoHandler : IXObjectDoHandler
        {
            public void HandleXObject(PdfCanvasProcessor processor, Stack<CanvasTag> canvasTagHierarchy, PdfStream xObjectStream, PdfName resourceName)
            {
            }
        }

        private sealed class TextRenderInfoCollector : IEventListener
        {
            private readonly List<string> _texts;

            public TextRenderInfoCollector(List<string> texts)
            {
                _texts = texts;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT) return;
                if (data is not TextRenderInfo tri) return;
                string decoded = "";
                try
                {
                    var pdfString = tri.GetPdfString();
                    var font = tri.GetFont();
                    if (pdfString != null && font != null)
                        decoded = font.Decode(pdfString) ?? "";
                }
                catch
                {
                    decoded = "";
                }

                _texts.Add(decoded ?? "");
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new HashSet<EventType> { EventType.RENDER_TEXT };
            }
        }
    }
}
