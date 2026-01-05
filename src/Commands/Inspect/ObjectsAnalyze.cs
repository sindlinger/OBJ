using System;
using System.Collections.Generic;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Lista objetos com suas chaves (Type/Subtype/Length) para inspeção rápida.
    /// Uso: tjpdf-cli inspect objects analyze --input file.pdf [--limit N]
    /// </summary>
    internal static class ObjectsAnalyze
    {
        public static void Execute(string[] args)
        {
            if (!ParseCommonOptions(args, out var inputFile, out var opts)) return;
            if (string.IsNullOrWhiteSpace(inputFile)) { Console.WriteLine("Informe --input <file.pdf>"); return; }
            int limit = opts.TryGetValue("--limit", out var l) && int.TryParse(l, out var n) ? n : int.MaxValue;

            ObjectsLegend.WriteLegend();

            using var doc = new PdfDocument(new PdfReader(inputFile));
            int max = doc.GetNumberOfPdfObjects();
            int count = 0;
            for (int i = 0; i < max && count < limit; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj == null || obj.IsNull()) continue;
                int objId = obj.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (objId <= 0)
                    objId = i;
                string type = obj.GetType().Name;
                string tp = "";
                string st = "";
                long len = 0;
                if (obj is PdfDictionary dict)
                {
                    tp = dict.GetAsName(PdfName.Type)?.ToString() ?? "";
                    st = dict.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                    if (dict is PdfStream stream) len = stream.GetLength();
                }
                else if (obj is PdfStream stream)
                {
                    st = stream.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                    len = stream.GetLength();
                }
                Console.WriteLine($"[{objId}] {type} Type={tp} Subtype={st} Len={len}");
                count++;
            }
        }

        private static bool ParseCommonOptions(string[] args, out string inputFile, out Dictionary<string, string> options)
        {
            inputFile = "";
            options = new Dictionary<string, string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (args[i].StartsWith("-"))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        options[args[i]] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        options[args[i]] = "true";
                    }
                }
                else if (string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = args[i];
                }
            }
            return true;
        }
    }
}
