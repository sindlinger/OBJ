using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FilterPDF.Commands;
using FilterPDF.Models;
using FilterPDF.Utils;
using FilterPDF.TjpbDespachoExtractor.Utils;
using iText.Kernel.Pdf;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.DocDetector
{
    // Encapsula a deteccao de despacho por bookmark + /Contents (header).
    public static class DespachoContentsDetector
    {
        public static IReadOnlyList<string> GetDespachoHeaderLabels(string rulesDoc, int objId)
        {
            return LoadHeaderLabelsNormalized(rulesDoc, objId);
        }

        public static string ResolveRoiPathForObj(string rulesDoc, int objId)
        {
            if (objId <= 0)
                return "";
            var roiDoc = ResolveRoiDoc(rulesDoc, objId);
            if (!string.IsNullOrWhiteSpace(roiDoc))
            {
                var roiPath = ResolveRoiPath(roiDoc, objId);
                if (!string.IsNullOrWhiteSpace(roiPath))
                    return roiPath;
            }
            return ResolveAnyRoiPath(objId);
        }

        public static int ResolveContentsPageForDoc(string path, int requestedPage, IReadOnlyList<string> headerLabels, out bool fallbackUsed)
        {
            fallbackUsed = false;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0;
            using var doc = new PdfDocument(new PdfReader(path));
            return ResolveContentsPageForDoc(doc, requestedPage, headerLabels, out fallbackUsed);
        }

        public static int ResolveContentsPageForDoc(PdfDocument doc, int requestedPage, IReadOnlyList<string> headerLabels, out bool fallbackUsed)
        {
            fallbackUsed = false;
            if (requestedPage > 0)
                return requestedPage;

            var pages = GetDespachoBookmarkPages(doc);
            if (pages.Count > 0)
                return pages[0];

            var byContents = FindDespachoPageByContents(doc, headerLabels);
            if (byContents > 0)
                return byContents;

            var byLargest = FindPageWithLargestContentsStream(doc);
            if (byLargest > 0)
            {
                fallbackUsed = true;
                return byLargest;
            }

            return 0;
        }

        public static (PdfStream? Stream, PdfResources? Resources) FindLargestContentsStreamByPage(PdfDocument doc, int pageNumber, IReadOnlyList<string> headerLabels, bool requireMarker)
        {
            if (doc == null || pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                return (null, null);

            var picked = FindDespachoContentsStreamByPage(doc, pageNumber, headerLabels, requireMarker);
            if (picked.Stream != null && picked.Resources != null)
                return picked;
            if (requireMarker)
                return (null, null);

            var page = doc.GetPage(pageNumber);
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            PdfStream? best = null;
            int bestLen = -1;

            foreach (var s in EnumerateStreams(contents))
            {
                int len = s.GetLength();
                if (len > bestLen)
                {
                    best = s;
                    bestLen = len;
                }
            }

            return best != null ? (best, resources) : (null, null);
        }

        public static int FindDespachoPageByContents(PdfDocument doc, IReadOnlyList<string> headerLabels)
        {
            if (doc == null)
                return 0;

            int total = doc.GetNumberOfPages();
            for (int p = 1; p <= total; p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var streams = EnumerateStreams(contents).ToList();
                if (streams.Count == 0)
                    continue;

                foreach (var stream in streams)
                {
                    if (HasTitlePrefix(stream, resources))
                        return p;
                }

                foreach (var stream in streams)
                {
                    var text = ExtractStreamText(stream, resources);
                    if (ContainsHeaderLabel(text, headerLabels))
                        return p;
                }
            }

            return 0;
        }

        private sealed class StreamCandidate
        {
            public PdfStream Stream { get; set; } = null!;
            public int Len { get; set; }
            public bool HasTitle { get; set; }
            public bool HasHeader { get; set; }
        }

        private static (PdfStream? Stream, PdfResources? Resources) FindDespachoContentsStreamByPage(PdfDocument doc, int pageNumber, IReadOnlyList<string> headerLabels, bool requireMarker)
        {
            if (doc == null || pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                return (null, null);

            var page = doc.GetPage(pageNumber);
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            var streams = EnumerateStreams(contents).ToList();
            if (streams.Count == 0)
                return (null, null);

            var candidates = new List<StreamCandidate>();
            foreach (var s in streams)
            {
                var prefix = ExtractPrefixText(s, resources, 20);
                var norm = TextUtils.NormalizeForMatch(prefix ?? "");
                var hasTitle = !string.IsNullOrWhiteSpace(norm) && norm.Contains("despacho");
                var text = ExtractStreamText(s, resources);
                var hasHeader = ContainsHeaderLabel(text, headerLabels);
                candidates.Add(new StreamCandidate
                {
                    Stream = s,
                    Len = s.GetLength(),
                    HasTitle = hasTitle,
                    HasHeader = hasHeader
                });
            }

            var hasMarker = candidates.Any(c => c.HasTitle || c.HasHeader);
            if (requireMarker && !hasMarker)
                return (null, null);

            StreamCandidate? title = candidates
                .Where(c => c.HasTitle)
                .OrderBy(c => c.Len)
                .FirstOrDefault();

            var bodyCandidates = title != null
                ? candidates.Where(c => !ReferenceEquals(c.Stream, title.Stream)).ToList()
                : candidates;

            StreamCandidate? body = bodyCandidates
                .Where(c => c.HasHeader)
                .OrderByDescending(c => c.Len)
                .FirstOrDefault();

            if (body == null)
                body = bodyCandidates.OrderByDescending(c => c.Len).FirstOrDefault();

            return body != null ? (body.Stream, resources) : (null, null);
        }

        private static List<int> GetDespachoBookmarkPages(PdfDocument doc)
        {
            var pages = new HashSet<int>();
            var items = BookmarkExtractor.Extract(doc);
            foreach (var item in items)
                CollectBookmarkPages(item, pages);
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

        private static int FindPageWithLargestContentsStream(PdfDocument doc)
        {
            if (doc == null)
                return 0;

            int bestPage = 0;
            int bestLen = -1;
            int total = doc.GetNumberOfPages();
            for (int p = 1; p <= total; p++)
            {
                var page = doc.GetPage(p);
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                foreach (var stream in EnumerateStreams(contents))
                {
                    int len = stream.GetLength();
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestPage = p;
                    }
                }
            }

            return bestPage;
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

        private static string ExtractStreamText(PdfStream stream, PdfResources resources)
        {
            try
            {
                var parts = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var joined = string.Join(" ", parts);
                var collapsed = TextUtils.CollapseSpacedLettersText(joined);
                return CollapseSpaces(NormalizeBlockToken(collapsed));
            }
            catch
            {
                return "";
            }
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

        private static string CollapseSpaces(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
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

        private static string NormalizeBlockToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
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

        private static List<string> LoadHeaderLabelsNormalized(string rulesDoc, int objId)
        {
            var labels = new List<string>();
            var roiPath = ResolveRoiPathForHeader(rulesDoc, objId);
            if (string.IsNullOrWhiteSpace(roiPath) || !File.Exists(roiPath))
                return labels;

            var roi = LoadRoi(roiPath);
            if (roi?.FrontHead?.Ranges == null)
                return labels;

            foreach (var range in roi.FrontHead.Ranges)
            {
                if (string.IsNullOrWhiteSpace(range.StartLabel))
                    continue;
                var norm = TextUtils.NormalizeForMatch(range.StartLabel);
                if (!string.IsNullOrWhiteSpace(norm))
                    labels.Add(norm);
            }

            return labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ResolveRoiPathForHeader(string rulesDoc, int objId)
        {
            if (objId > 0)
            {
                var roiDoc = ResolveRoiDoc(rulesDoc, objId);
                var roiPath = ResolveRoiPath(roiDoc, objId);
                if (!string.IsNullOrWhiteSpace(roiPath))
                    return roiPath;
                roiPath = ResolveAnyRoiPath(objId);
                if (!string.IsNullOrWhiteSpace(roiPath))
                    return roiPath;
            }

            var fallbackDoc = string.IsNullOrWhiteSpace(rulesDoc) ? "tjpb_despacho" : rulesDoc.Trim();
            if (!string.IsNullOrWhiteSpace(fallbackDoc))
            {
                var dirCandidates = new[]
                {
                    Path.Combine(Directory.GetCurrentDirectory(), "configs", "textops_anchors"),
                    Path.Combine(Directory.GetCurrentDirectory(), "..", "configs", "textops_anchors"),
                    Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "configs", "textops_anchors"),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../configs/textops_anchors"))
                };

                foreach (var dir in dirCandidates)
                {
                    if (!Directory.Exists(dir))
                        continue;
                    foreach (var ext in new[] { "yml", "yaml" })
                    {
                        var pattern = $"{fallbackDoc}_obj*_roi.{ext}";
                        var files = Directory.GetFiles(dir, pattern)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (files.Count > 0)
                            return files[0];
                    }
                }
            }

            return "";
        }

        private static string ResolveRoiDoc(string rulesDoc, int objId)
        {
            if (!string.IsNullOrWhiteSpace(rulesDoc))
                return rulesDoc.Trim();

            const string defaultDoc = "tjpb_despacho";
            var defaultPath = ResolveRoiPath(defaultDoc, objId);
            if (!string.IsNullOrWhiteSpace(defaultPath))
                return defaultDoc;
            return "";
        }

        private static string ResolveRoiPath(string roiDoc, int objId)
        {
            if (string.IsNullOrWhiteSpace(roiDoc) || objId <= 0)
                return "";

            var name = roiDoc.Trim();
            var baseName = $"{name}_obj{objId}_roi";
            var candidates = new List<string>();
            var cwd = Directory.GetCurrentDirectory();
            var exeBase = AppContext.BaseDirectory;

            foreach (var ext in new[] { ".yml", ".yaml" })
            {
                var file = baseName + ext;
                candidates.Add(Path.Combine(cwd, "configs", "textops_anchors", file));
                candidates.Add(Path.Combine(cwd, file));
                candidates.Add(Path.Combine(cwd, "..", "configs", "textops_anchors", file));
                candidates.Add(Path.Combine(cwd, "..", "..", "configs", "textops_anchors", file));
                candidates.Add(Path.GetFullPath(Path.Combine(exeBase, "../../../../configs/textops_anchors", file)));
            }

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return "";
        }

        private static string ResolveAnyRoiPath(int objId)
        {
            if (objId <= 0)
                return "";

            var cwd = Directory.GetCurrentDirectory();
            var exeBase = AppContext.BaseDirectory;
            var dirs = new List<string>
            {
                Path.Combine(cwd, "configs", "textops_anchors"),
                Path.Combine(cwd, "..", "configs", "textops_anchors"),
                Path.Combine(cwd, "..", "..", "configs", "textops_anchors"),
                Path.GetFullPath(Path.Combine(exeBase, "../../../../configs/textops_anchors"))
            };

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var ext in new[] { "yml", "yaml" })
                {
                    var pattern = $"*_obj{objId}_roi.{ext}";
                    var files = Directory.GetFiles(dir, pattern);
                    if (files.Length > 0)
                        return files[0];
                }
            }

            return "";
        }

        private static TextOpsRoiFile? LoadRoi(string roiPath)
        {
            if (string.IsNullOrWhiteSpace(roiPath) || !File.Exists(roiPath))
                return null;

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                using var reader = new StreamReader(roiPath);
                return deserializer.Deserialize<TextOpsRoiFile>(reader);
            }
            catch
            {
                return null;
            }
        }

        private sealed class TextOpsRoiFile
        {
            public int Version { get; set; }
            public string Doc { get; set; } = "";
            public int Obj { get; set; }
            public TextOpsRoiSection? FrontHead { get; set; }
            public TextOpsRoiSection? BackTail { get; set; }
        }

        private sealed class TextOpsRoiSection
        {
            public List<TextOpsRoiRange> Ranges { get; set; } = new List<TextOpsRoiRange>();
        }

        private sealed class TextOpsRoiRange
        {
            public string SourceFile { get; set; } = "";
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string StartLabel { get; set; } = "";
            public string EndLabel { get; set; } = "";
        }
    }
}
