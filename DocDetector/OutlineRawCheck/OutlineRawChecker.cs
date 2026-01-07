using System;
using System.Collections.Generic;
using iText.Kernel.Pdf;

namespace Obj.DocDetector
{
    public sealed class OutlineRawResult
    {
        public bool HasOutlines { get; set; }
        public int TopLevelCount { get; set; }
        public int TotalCount { get; set; }
    }

    public static class OutlineRawChecker
    {
        public static OutlineRawResult Check(string pdfPath)
        {
            var result = new OutlineRawResult();
            using var doc = new PdfDocument(new PdfReader(pdfPath));
            var catalog = doc.GetCatalog();
            if (catalog == null) return result;

            var outlinesObj = catalog.GetPdfObject().Get(PdfName.Outlines);
            if (outlinesObj is not PdfDictionary outlinesDict)
                return result;

            result.HasOutlines = true;

            var first = outlinesDict.GetAsDictionary(PdfName.First);
            result.TopLevelCount = CountSiblings(first);
            result.TotalCount = CountAll(first, new HashSet<int>());
            return result;
        }

        private static int CountSiblings(PdfDictionary? node)
        {
            int count = 0;
            var cur = node;
            var seen = new HashSet<int>();
            while (cur != null)
            {
                var id = cur.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (id != 0 && !seen.Add(id))
                    break;
                count++;
                cur = cur.GetAsDictionary(PdfName.Next);
            }
            return count;
        }

        private static int CountAll(PdfDictionary? node, HashSet<int> visited)
        {
            if (node == null) return 0;
            var id = node.GetIndirectReference()?.GetObjNumber() ?? 0;
            if (id != 0 && !visited.Add(id))
                return 0;

            int count = 1;
            var firstChild = node.GetAsDictionary(PdfName.First);
            count += CountAll(firstChild, visited);
            var next = node.GetAsDictionary(PdfName.Next);
            count += CountAll(next, visited);
            return count;
        }
    }
}
