using System;
using System.Collections.Generic;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Util;

namespace FilterPDF.Commands
{
    internal static class PdfTextExtraction
    {
        internal readonly struct TextItem
        {
            public TextItem(string text, double x0, double x1, double y, double charWidth)
            {
                Text = text;
                X0 = x0;
                X1 = x1;
                Y = y;
                CharWidth = charWidth;
            }

            public string Text { get; }
            public double X0 { get; }
            public double X1 { get; }
            public double Y { get; }
            public double CharWidth { get; }
        }

        internal static bool TryExtractStreamText(PdfStream stream, PdfResources resources, out string text, out string? error)
        {
            text = "";
            error = null;
            try
            {
                var strategy = new SimpleTextExtractionStrategy();
                var processor = new PdfCanvasProcessor(strategy);
                processor.ProcessContent(stream.GetBytes(), resources);
                text = strategy.GetResultantText();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static List<TextItem> CollectTextItems(PdfStream stream, PdfResources resources)
        {
            var items = new List<TextItem>();
            try
            {
                var collector = new TextItemCollector(items);
                var processor = new PdfCanvasProcessor(collector);
                processor.ProcessContent(stream.GetBytes(), resources);
            }
            catch
            {
                return items;
            }
            return items;
        }

        internal static List<string> CollectTextPieces(PdfStream stream, PdfResources resources)
        {
            var items = CollectTextItems(stream, resources);
            var list = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Text))
                    list.Add(item.Text);
            }
            return list;
        }

        internal static List<string> CollectTextOperatorTexts(PdfStream stream, PdfResources resources)
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

        internal static bool TryFindResourcesForObjId(PdfDocument doc, int objId, out PdfResources resources)
        {
            resources = new PdfResources(new PdfDictionary());
            if (objId <= 0) return false;

            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var pageResources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                foreach (var stream in EnumerateStreams(contents))
                {
                    var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id == objId)
                    {
                        resources = pageResources;
                        return true;
                    }
                }

                var xobjects = pageResources.GetResource(PdfName.XObject) as PdfDictionary;
                if (xobjects == null) continue;
                foreach (var name in xobjects.KeySet())
                {
                    var xs = xobjects.GetAsStream(name);
                    if (xs == null) continue;
                    var id = xs.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id == objId)
                    {
                        var xresDict = xs.GetAsDictionary(PdfName.Resources);
                        resources = xresDict != null ? new PdfResources(xresDict) : pageResources;
                        return true;
                    }
                }
            }

            return false;
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
                double width = Math.Abs(x1 - x0);
                double charWidth = width / Math.Max(1, text.Length);
                _items.Add(new TextItem(text, x0, x1, y, charWidth));
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new HashSet<EventType> { EventType.RENDER_TEXT };
            }
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
    }
}
