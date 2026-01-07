using System;
using System.Collections.Generic;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;

namespace Obj.DocDetector
{
    public sealed class BookmarkEntry
    {
        public string Title { get; set; } = "";
        public int Page { get; set; }
        public int Level { get; set; }
    }

    public static class BookmarkTitleDetector
    {
        public static List<BookmarkEntry> Fetch(string pdfPath)
        {
            var list = new List<BookmarkEntry>();
            if (string.IsNullOrWhiteSpace(pdfPath))
                return list;

            using var doc = new PdfDocument(new PdfReader(pdfPath));
            try
            {
                var root = doc.GetOutlines(false);
                if (root == null || root.GetAllChildren().Count == 0)
                    return list;

                foreach (var child in root.GetAllChildren())
                    AddOutline(child, 0, doc, list);
            }
            catch
            {
                return list;
            }

            return list;
        }

        private static void AddOutline(PdfOutline outline, int level, PdfDocument doc, List<BookmarkEntry> list)
        {
            var dest = outline.GetDestination();
            int page = ResolveDestinationPage(dest, doc);
            list.Add(new BookmarkEntry
            {
                Title = outline.GetTitle() ?? "",
                Page = page,
                Level = level
            });

            foreach (var child in outline.GetAllChildren())
                AddOutline(child, level + 1, doc, list);
        }

        private static int ResolveDestinationPage(PdfDestination destination, PdfDocument doc)
        {
            if (destination == null || doc == null) return 0;
            try
            {
                var nameTree = doc.GetCatalog().GetNameTree(PdfName.Dests);
                var names = nameTree?.GetNames();
                var destPage = destination.GetDestinationPage(names);
                if (destPage is PdfDictionary dict)
                    return doc.GetPageNumber(dict);
            }
            catch
            {
            }
            return 0;
        }
    }
}
