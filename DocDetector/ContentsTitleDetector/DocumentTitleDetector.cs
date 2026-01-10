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
using iText.Kernel.Geom;

namespace Obj.DocDetector
{
    public sealed class DetectionOptions
    {
        public int PrefixOpCount { get; set; } = 10;
        public int SuffixOpCount { get; set; } = 40;
        public int MinTitleChars { get; set; } = 4;
        public int MaxTitleChars { get; set; } = 120;
        public bool CarryForward { get; set; } = true;
        public bool IncludeUnknownIntervals { get; set; } = false;
        public double TopBandPct { get; set; } = 0.45;
        public double BottomBandPct { get; set; } = 0.25;
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
        public int BodyObj { get; set; }
        public int BodyIndex { get; set; }
        public int BodyTextOps { get; set; }
        public int BodyStreamLen { get; set; }
        public string BodyPrefix { get; set; } = "";
        public string BodySuffix { get; set; } = "";
        public string BodyMatchedKeyword { get; set; } = "";
        public List<StreamPrefixInfo> StreamPrefixes { get; set; } = new List<StreamPrefixInfo>();
        public string TopText { get; set; } = "";
        public string BottomText { get; set; } = "";
    }

    public sealed class StreamPrefixInfo
    {
        public int Index { get; set; }
        public int ObjId { get; set; }
        public int StreamLen { get; set; }
        public int TextOps { get; set; }
        public string Prefix { get; set; } = "";
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

                var streamPrefixes = BuildStreamPrefixes(streams, resources, opts.PrefixOpCount);
                var primary = PickPrimaryStreamInfo(streams, resources, opts.PrefixOpCount, opts.SuffixOpCount);
                var bodyPrefix = primary?.Prefix ?? "";
                var bodySuffix = primary?.Suffix ?? "";
                var bodyNorm = Normalize(bodyPrefix);
                var bodyMatched = FindKeyword(bodyNorm, opts.Keywords);
                var pageHeight = page.GetPageSize().GetHeight();
                var topText = ExtractTopText(streams, resources, pageHeight, opts.TopBandPct);
                var bottomText = ExtractBottomText(streams, resources, pageHeight, opts.BottomBandPct);

                var headerStreams = PickHeaderStreams(streams);
                var best = PickBestTitle(headerStreams, resources, opts);
                if (best == null)
                {
                    // fallback: still prefix-only, but check all streams (header + body)
                    best = PickBestTitle(streams, resources, opts);
                }
                if (best == null && !string.IsNullOrWhiteSpace(topText))
                {
                    var topNorm = Normalize(topText);
                    var topMatched = FindKeyword(topNorm, opts.Keywords);
                    if (!string.IsNullOrWhiteSpace(topMatched))
                    {
                        var topTitle = ExtractTitle(topText, opts.MaxTitleChars);
                        if (!string.IsNullOrWhiteSpace(topTitle))
                        {
                            best = new TitleCandidate
                            {
                                ObjId = primary?.ObjId ?? 0,
                                StreamLen = primary?.StreamLen ?? 0,
                                OpCount = opts.PrefixOpCount,
                                Title = topTitle,
                                TitleKey = topMatched,
                                TitleNormalized = topNorm,
                                SourceText = topText,
                                MatchedKeyword = topMatched,
                                Score = 50
                            };
                        }
                    }
                }
                if (best == null)
                {
                    result.Pages.Add(new PageClassification
                    {
                        Page = p,
                        TitleKey = "",
                        BodyObj = primary?.ObjId ?? 0,
                        BodyIndex = primary?.Index ?? 0,
                        BodyTextOps = primary?.TextOps ?? 0,
                        BodyStreamLen = primary?.StreamLen ?? 0,
                        BodyPrefix = bodyPrefix,
                        BodySuffix = bodySuffix,
                        BodyMatchedKeyword = bodyMatched,
                        StreamPrefixes = streamPrefixes,
                        TopText = topText,
                        BottomText = bottomText
                    });
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
                    MatchedKeyword = best.MatchedKeyword,
                    BodyObj = primary?.ObjId ?? 0,
                    BodyIndex = primary?.Index ?? 0,
                    BodyTextOps = primary?.TextOps ?? 0,
                    BodyStreamLen = primary?.StreamLen ?? 0,
                    BodyPrefix = bodyPrefix,
                    BodySuffix = bodySuffix,
                    BodyMatchedKeyword = bodyMatched,
                    StreamPrefixes = streamPrefixes,
                    TopText = topText,
                    BottomText = bottomText
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

        private sealed class StreamInfo
        {
            public int Index { get; set; }
            public int ObjId { get; set; }
            public int StreamLen { get; set; }
            public int TextOps { get; set; }
            public string Prefix { get; set; } = "";
            public string Suffix { get; set; } = "";
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

        private static StreamInfo? PickPrimaryStreamInfo(List<PdfStream> streams, PdfResources resources, int prefixOps, int suffixOps)
        {
            StreamInfo? primary = null;
            int idx = 0;
            foreach (var stream in streams)
            {
                idx++;
                var parts = CollectTextOperatorTexts(stream, resources);
                var textOps = parts?.Count ?? 0;
                var take = Math.Max(1, Math.Min(prefixOps, textOps));
                var prefix = textOps > 0 ? string.Join(" ", parts.Take(take)) : "";
                var suffixTake = Math.Max(1, Math.Min(suffixOps, textOps));
                var suffix = textOps > 0 ? string.Join(" ", parts.Skip(Math.Max(0, textOps - suffixTake))) : "";
                var info = new StreamInfo
                {
                    Index = idx,
                    ObjId = stream.GetIndirectReference()?.GetObjNumber() ?? 0,
                    StreamLen = stream.GetLength(),
                    TextOps = textOps,
                    Prefix = CollapseSpaces(prefix),
                    Suffix = CollapseSpaces(suffix)
                };

                if (primary == null)
                {
                    primary = info;
                    continue;
                }

                if (info.TextOps > primary.TextOps ||
                    (info.TextOps == primary.TextOps && info.StreamLen > primary.StreamLen))
                {
                    primary = info;
                }
            }

            return primary;
        }

        private static List<StreamPrefixInfo> BuildStreamPrefixes(List<PdfStream> streams, PdfResources resources, int prefixOps)
        {
            var list = new List<StreamPrefixInfo>();
            int idx = 0;
            foreach (var stream in streams)
            {
                idx++;
                var parts = CollectTextOperatorTexts(stream, resources);
                var textOps = parts?.Count ?? 0;
                var take = Math.Max(1, Math.Min(prefixOps, textOps));
                var prefix = textOps > 0 ? string.Join(" ", parts.Take(take)) : "";
                list.Add(new StreamPrefixInfo
                {
                    Index = idx,
                    ObjId = stream.GetIndirectReference()?.GetObjNumber() ?? 0,
                    StreamLen = stream.GetLength(),
                    TextOps = textOps,
                    Prefix = CollapseSpaces(prefix)
                });
            }
            return list;
        }

        private readonly struct TextItem
        {
            public TextItem(string text, double x0, double x1, double y)
            {
                Text = text;
                X0 = x0;
                X1 = x1;
                Y = y;
            }

            public string Text { get; }
            public double X0 { get; }
            public double X1 { get; }
            public double Y { get; }
        }

        private static string ExtractTopText(List<PdfStream> streams, PdfResources resources, float pageHeight, double topBandPct)
        {
            var items = new List<TextItem>();
            foreach (var stream in streams)
            {
                CollectTextItems(stream, resources, items);
            }

            if (items.Count == 0 || pageHeight <= 0)
                return "";

            var topStart = pageHeight * (1.0 - Math.Max(0.05, Math.Min(0.5, topBandPct)));
            var topItems = items.Where(i => i.Y >= topStart).ToList();
            if (topItems.Count == 0)
                return "";

            // Sort by Y desc, then X asc
            var ordered = topItems.OrderByDescending(i => i.Y).ThenBy(i => i.X0).ToList();
            var lines = new List<List<TextItem>>();
            const double yTolerance = 2.0;
            foreach (var item in ordered)
            {
                if (lines.Count == 0)
                {
                    lines.Add(new List<TextItem> { item });
                    continue;
                }

                var lastLine = lines[^1];
                var lastY = lastLine[0].Y;
                if (Math.Abs(item.Y - lastY) <= yTolerance)
                {
                    lastLine.Add(item);
                }
                else
                {
                    lines.Add(new List<TextItem> { item });
                }
            }

            var lineTexts = new List<string>();
            foreach (var line in lines)
            {
                var lineText = string.Join(" ", line.OrderBy(i => i.X0).Select(i => i.Text));
                if (!string.IsNullOrWhiteSpace(lineText))
                    lineTexts.Add(lineText);
            }

            var joined = string.Join(" ", lineTexts);
            return CollapseSpaces(joined);
        }

        private static string ExtractBottomText(List<PdfStream> streams, PdfResources resources, float pageHeight, double bottomBandPct)
        {
            var items = new List<TextItem>();
            foreach (var stream in streams)
            {
                CollectTextItems(stream, resources, items);
            }

            if (items.Count == 0 || pageHeight <= 0)
                return "";

            var bottomEnd = pageHeight * Math.Max(0.05, Math.Min(0.5, bottomBandPct));
            var bottomItems = items.Where(i => i.Y <= bottomEnd).ToList();
            if (bottomItems.Count == 0)
                return "";

            var ordered = bottomItems.OrderByDescending(i => i.Y).ThenBy(i => i.X0).ToList();
            var lines = new List<List<TextItem>>();
            const double yTolerance = 2.0;
            foreach (var item in ordered)
            {
                if (lines.Count == 0)
                {
                    lines.Add(new List<TextItem> { item });
                    continue;
                }

                var lastLine = lines[^1];
                var lastY = lastLine[0].Y;
                if (Math.Abs(item.Y - lastY) <= yTolerance)
                {
                    lastLine.Add(item);
                }
                else
                {
                    lines.Add(new List<TextItem> { item });
                }
            }

            var lineTexts = new List<string>();
            foreach (var line in lines)
            {
                var lineText = string.Join(" ", line.OrderBy(i => i.X0).Select(i => i.Text));
                if (!string.IsNullOrWhiteSpace(lineText))
                    lineTexts.Add(lineText);
            }

            var joined = string.Join(" ", lineTexts);
            return CollapseSpaces(joined);
        }

        private static void CollectTextItems(PdfStream stream, PdfResources resources, List<TextItem> items)
        {
            try
            {
                var collector = new TextItemCollector(items);
                var processor = new PdfCanvasProcessor(collector);
                processor.ProcessContent(stream.GetBytes(), resources ?? new PdfResources(new PdfDictionary()));
            }
            catch
            {
                return;
            }
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
            {
                if (streams.Count <= 1)
                    return streams;
                var orderedSmall = streams.OrderBy(s => s.GetLength()).ToList();
                return orderedSmall.Take(Math.Min(2, orderedSmall.Count)).ToList();
            }

            var ordered = streams.OrderBy(s => s.GetLength()).ToList();
            return ordered.Take(3).ToList();
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
            var collapsed = CollapseSpacedLettersText(text);
            if (string.IsNullOrWhiteSpace(collapsed)) return "";
            var sb = new StringBuilder(collapsed.Length);
            bool prevSpace = false;
            foreach (var ch in collapsed)
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

        private static string CollapseSpacedLettersText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var parts = System.Text.RegularExpressions.Regex.Matches(text, "\\S+|\\s+");
            if (parts.Count == 0) return "";
            var sb = new StringBuilder();
            var buffer = new StringBuilder();
            string pendingWs = "";

            foreach (System.Text.RegularExpressions.Match m in parts)
            {
                var s = m.Value;
                if (string.IsNullOrWhiteSpace(s))
                {
                    pendingWs = s;
                    continue;
                }

                var token = s;
                var joinable = IsJoinToken(token);
                var canJoin = joinable && buffer.Length > 0 && IsTightSpace(pendingWs);

                if (canJoin)
                {
                    buffer.Append(token);
                }
                else
                {
                    if (buffer.Length > 0)
                    {
                        AppendWithSpace(sb, buffer.ToString());
                        buffer.Clear();
                    }
                    if (!string.IsNullOrEmpty(pendingWs))
                        AppendSpace(sb);

                    if (joinable)
                        buffer.Append(token);
                    else
                        AppendWithSpace(sb, token);
                }
                pendingWs = "";
            }

            if (buffer.Length > 0)
                AppendWithSpace(sb, buffer.ToString());

            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
        }

        private static bool IsJoinToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (token.Length == 1)
            {
                var c = token[0];
                if (char.IsLetterOrDigit(c)) return true;
                if (c == '$' || c == '/' || c == '-' || c == '–' || c == '.' || c == ',' ||
                    c == 'ª' || c == 'º' || c == '°')
                    return true;
            }
            return false;
        }

        private static bool IsTightSpace(string ws)
        {
            if (string.IsNullOrEmpty(ws)) return false;
            if (ws.IndexOf('\n') >= 0 || ws.IndexOf('\r') >= 0) return false;
            return ws.Length == 1 && ws[0] == ' ';
        }

        private static void AppendSpace(StringBuilder sb)
        {
            if (sb.Length == 0) return;
            if (sb[sb.Length - 1] != ' ')
                sb.Append(' ');
        }

        private static void AppendWithSpace(StringBuilder sb, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                sb.Append(' ');
            sb.Append(token);
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

        private sealed class TextItemCollector : IEventListener
        {
            private readonly List<TextItem> _items;

            public TextItemCollector(List<TextItem> items)
            {
                _items = items;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT) return;
                if (data is not TextRenderInfo tri) return;
                var text = tri.GetText();
                if (string.IsNullOrEmpty(text)) return;
                var baseLine = tri.GetBaseline();
                var start = baseLine.GetStartPoint();
                var end = baseLine.GetEndPoint();
                double x0 = start.Get(Vector.I1);
                double x1 = end.Get(Vector.I1);
                double y = start.Get(Vector.I2);
                _items.Add(new TextItem(text, x0, x1, y));
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new HashSet<EventType> { EventType.RENDER_TEXT };
            }
        }
    }
}
