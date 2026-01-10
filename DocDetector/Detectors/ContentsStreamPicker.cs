using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Obj.Utils;
using Obj.TjpbDespachoExtractor.Utils;
using iText.Kernel.Pdf;
using PdfTextExtraction = Obj.Commands.PdfTextExtraction;

namespace Obj.DocDetector
{
    public sealed class StreamPickRequest
    {
        public string PdfPath { get; set; } = "";
        public int Page { get; set; }
        public bool RequireMarker { get; set; } = true;
    }

    public static class ContentsStreamPicker
    {
        private const int PrefixOps = 200;

        public static DetectionHit Pick(StreamPickRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.PdfPath) || !File.Exists(request.PdfPath))
                return DetectionHit.Empty(request?.PdfPath ?? "", "pdf_not_found");
            if (request.Page <= 0)
                return DetectionHit.Empty(request.PdfPath, "page_invalid");

            var defaults = ObjDefaultsLoader.Load();
            var rulesDoc = defaults?.Doc ?? "tjpb_despacho";
            var headerLabels = DespachoContentsDetector.GetDespachoHeaderLabels(rulesDoc, 0)
                .Select(TextUtils.NormalizeForMatch)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var doc = new PdfDocument(new PdfReader(request.PdfPath));
            if (request.Page > doc.GetNumberOfPages())
                return DetectionHit.Empty(request.PdfPath, "page_out_of_range");

            var page = doc.GetPage(request.Page);
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            var streams = EnumerateStreams(contents).ToList();
            if (streams.Count == 0)
                return DetectionHit.Empty(request.PdfPath, "no_streams");

            var candidates = new List<(PdfStream Stream, int Len, int TextOps, bool HasMarker, string Matched)>();
            var streamInfos = new List<(PdfStream Stream, int Len, int TextOps)>();
            foreach (var stream in streams)
            {
                var textOps = PdfTextExtraction.CollectTextOperatorTexts(stream, resources).Count;
                streamInfos.Add((stream, stream.GetLength(), textOps));
            }

            var minLen = streamInfos.Where(s => s.TextOps > 0).Select(s => s.Len).DefaultIfEmpty(0).Min();
            var minOps = streamInfos.Where(s => s.TextOps > 0).Select(s => s.TextOps).DefaultIfEmpty(0).Min();

            foreach (var stream in streams)
            {
                var info = streamInfos.First(s => ReferenceEquals(s.Stream, stream));
                var prefix = ExtractPrefixText(stream, resources, PrefixOps);
                var norm = TextUtils.NormalizeForMatch(prefix ?? "");
                bool isTitleCandidate = info.TextOps > 0 && (info.Len <= minLen * 1.25 || info.TextOps <= minOps + 1);
                bool hasTitle = isTitleCandidate && norm.Contains("despacho", StringComparison.Ordinal);
                var matched = "";
                bool hasHeader = HasHeaderMatch(norm, headerLabels, out matched);
                bool hasMarker = hasTitle || hasHeader;
                if (hasTitle && string.IsNullOrWhiteSpace(matched))
                    matched = "despacho";
                candidates.Add((stream, info.Len, info.TextOps, hasMarker, matched));
            }

            if (request.RequireMarker && candidates.All(c => !c.HasMarker))
                return DetectionHit.Empty(request.PdfPath, "marker_not_found");

            var picked = candidates
                .OrderByDescending(c => c.TextOps)
                .ThenByDescending(c => c.Len)
                .First();

            var objId = picked.Stream.GetIndirectReference()?.GetObjNumber() ?? 0;
            return new DetectionHit
            {
                PdfPath = request.PdfPath,
                Page = request.Page,
                Obj = objId,
                TitleKey = picked.Matched,
                Title = "",
                PathRef = $"page={request.Page}/obj={objId}",
                MatchedKeyword = picked.Matched,
                Reason = request.RequireMarker ? "stream_marker" : "stream_largest"
            };
        }

        private static bool HasHeaderMatch(string norm, List<string> labels, out string matched)
        {
            matched = "";
            if (string.IsNullOrWhiteSpace(norm))
                return false;

            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                if (norm.Contains(label, StringComparison.Ordinal))
                {
                    matched = label;
                    return true;
                }
            }
            if (norm.Contains("diretoria especial", StringComparison.Ordinal))
            {
                matched = "diretoria especial";
                return true;
            }
            return false;
        }

        private static string ExtractPrefixText(PdfStream stream, PdfResources resources, int maxOps)
        {
            try
            {
                var parts = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var take = Math.Max(1, Math.Min(maxOps, parts.Count));
                var joined = string.Join(" ", parts.Take(take));
                var collapsed = TextUtils.CollapseSpacedLettersText(joined);
                return CollapseSpaces(collapsed);
            }
            catch
            {
                return "";
            }
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
            return sb.ToString().Trim();
        }
    }
}
