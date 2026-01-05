using System;
using System.Collections.Generic;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Lista objetos do PDF com filtros e niveis de detalhe.
    /// Uso:
    ///   tjpdf-cli inspect objects list --input file.pdf [--limit N]
    ///   tjpdf-cli inspect objects list --input file.pdf --text-only [--length]
    ///   tjpdf-cli inspect objects list --input file.pdf --type /XObject --subtype /Image
    ///   tjpdf-cli inspect objects list --input file.pdf --detail analyze|deep|table
    /// </summary>
    internal static class ObjectsListCommand
    {
        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var opt))
                return;
            if (string.IsNullOrWhiteSpace(opt.InputFile))
            {
                Console.WriteLine("Informe --input <file.pdf>");
                return;
            }

            ObjectsLegend.WriteLegend();

            using var doc = new PdfDocument(new PdfReader(opt.InputFile));

            if (string.Equals(opt.Detail, "table", StringComparison.OrdinalIgnoreCase))
            {
                ObjectsExplain.Execute(BuildForwardArgs(opt));
                return;
            }

            var contentStreamResources = BuildContentStreamResources(doc);
            int limit = opt.Limit ?? int.MaxValue;
            int max = doc.GetNumberOfPdfObjects();
            int count = 0;
            for (int i = 0; i < max && count < limit; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj == null || obj.IsNull()) continue;
                int objId = obj.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (objId <= 0)
                    objId = i;
                string typeName = obj.GetType().Name;
                string type = "";
                string subtype = "";
                string derived = "";
                if (obj is PdfDictionary dict)
                {
                    var tp = dict.GetAsName(PdfName.Type);
                    if (tp != null) type = tp.ToString();
                    var st = dict.GetAsName(PdfName.Subtype);
                    if (st != null) subtype = st.ToString();
                }

                long len = 0;
                if (obj is PdfStream stream)
                {
                    len = stream.GetLength();
                    if (objId > 0 && contentStreamResources.ContainsKey(objId))
                    {
                        derived = HasTextOperators(stream) ? "texto" : "sem_texto";
                    }
                    else
                    {
                        derived = "stream";
                    }
                }
                else if (obj is PdfDictionary)
                {
                    if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(subtype))
                        derived = "dict";
                }

                if (!Matches(opt.TypeTarget, opt.SubtypeTarget, opt.DerivedTarget, type, subtype, derived))
                    continue;
                if (opt.TextOnly && !string.Equals(derived, "texto", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(opt.Detail, "deep", StringComparison.OrdinalIgnoreCase))
                {
                    var deepSubtype = !string.IsNullOrWhiteSpace(subtype) ? subtype : type;
                    if (string.IsNullOrWhiteSpace(deepSubtype)) deepSubtype = derived;
                    Console.WriteLine($"{i}: {typeName} {deepSubtype} len={len}");
                    count++;
                    continue;
                }

                if (string.Equals(opt.Detail, "analyze", StringComparison.OrdinalIgnoreCase))
                {
                    var tpLabel = string.IsNullOrWhiteSpace(type) ? "" : type;
                    var stLabel = string.IsNullOrWhiteSpace(subtype) ? (string.IsNullOrWhiteSpace(derived) ? "" : derived) : subtype;
                    Console.WriteLine($"[{objId}] {typeName} Type={tpLabel} Subtype={stLabel} Len={len}");
                    count++;
                    continue;
                }

                var labelType = string.IsNullOrWhiteSpace(type) ? typeName : type;
                var labelSubtype = !string.IsNullOrWhiteSpace(subtype) ? subtype : derived;
                if (opt.ShowLength)
                    Console.WriteLine($"[{objId}] {labelType} {labelSubtype} Len={len}".Trim());
                else
                    Console.WriteLine($"[{objId}] {labelType} {labelSubtype}".Trim());
                count++;
            }
        }

        private static string[] BuildForwardArgs(ListOptions opt)
        {
            var args = new List<string> { "--input", opt.InputFile };
            if (opt.Limit.HasValue)
            {
                args.Add("--limit");
                args.Add(opt.Limit.Value.ToString());
            }
            return args.ToArray();
        }

        private static bool ParseOptions(string[] args, out ListOptions opt)
        {
            opt = new ListOptions();

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    opt.InputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var limit))
                        opt.Limit = limit;
                    continue;
                }
                if (string.Equals(arg, "--detail", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    opt.Detail = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--text-only", StringComparison.OrdinalIgnoreCase))
                {
                    opt.TextOnly = true;
                    continue;
                }
                if (string.Equals(arg, "--length", StringComparison.OrdinalIgnoreCase))
                {
                    opt.ShowLength = true;
                    continue;
                }
                if (string.Equals(arg, "--type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    opt.TypeTarget = NormalizeTypeName(args[++i]);
                    continue;
                }
                if (string.Equals(arg, "--subtype", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    opt.SubtypeTarget = NormalizeTypeName(args[++i]);
                    continue;
                }
                if (string.Equals(arg, "--derived", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    opt.DerivedTarget = NormalizeDerived(args[++i]);
                    continue;
                }
                if (arg.StartsWith("-"))
                {
                    continue;
                }

                var derived = NormalizeDerived(arg);
                if (!string.IsNullOrWhiteSpace(derived) && string.IsNullOrWhiteSpace(opt.DerivedTarget))
                {
                    opt.DerivedTarget = derived;
                    continue;
                }

                if (arg.StartsWith("/") && string.IsNullOrWhiteSpace(opt.TypeTarget))
                {
                    opt.TypeTarget = NormalizeTypeName(arg);
                    continue;
                }
                if (arg.StartsWith("/") && string.IsNullOrWhiteSpace(opt.SubtypeTarget))
                {
                    opt.SubtypeTarget = NormalizeTypeName(arg);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(opt.InputFile))
                {
                    opt.InputFile = arg;
                }
            }

            if (string.IsNullOrWhiteSpace(opt.Detail))
                opt.Detail = "list";
            return true;
        }

        private sealed class ListOptions
        {
            public string InputFile { get; set; } = "";
            public int? Limit { get; set; }
            public string Detail { get; set; } = "list";
            public bool TextOnly { get; set; }
            public bool ShowLength { get; set; }
            public string TypeTarget { get; set; } = "";
            public string SubtypeTarget { get; set; } = "";
            public string DerivedTarget { get; set; } = "";
        }

        private static string NormalizeTypeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value.Trim();
            if (!v.StartsWith("/") && !v.StartsWith("(")) v = "/" + v;
            return v;
        }

        private static string NormalizeDerived(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return v switch
            {
                "texto" => "texto",
                "sem_texto" => "sem_texto",
                "stream" => "stream",
                "dict" => "dict",
                _ => ""
            };
        }

        private static bool Matches(string typeTarget, string subtypeTarget, string derivedTarget, string type, string subtype, string derived)
        {
            if (!string.IsNullOrWhiteSpace(derivedTarget))
                return string.Equals(derivedTarget, derived, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(typeTarget) && !string.Equals(typeTarget, type, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(subtypeTarget) && !string.Equals(subtypeTarget, subtype, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private static Dictionary<int, PdfResources> BuildContentStreamResources(PdfDocument doc)
        {
            var map = new Dictionary<int, PdfResources>();
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                if (page == null) continue;
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().GetAsArray(PdfName.Contents);
                if (contents != null)
                {
                    foreach (var item in contents)
                    {
                        if (item is PdfStream stream)
                        {
                            var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                            if (id > 0)
                                map[id] = resources;
                        }
                    }
                }
                else
                {
                    var stream = page.GetContentStream(0);
                    if (stream != null)
                    {
                        var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                        if (id > 0)
                            map[id] = resources;
                    }
                }
            }
            return map;
        }

        private static bool HasTextOperators(PdfStream stream)
        {
            try
            {
                var bytes = stream.GetBytes();
                if (bytes == null || bytes.Length == 0) return false;
                var s = System.Text.Encoding.ASCII.GetString(bytes);
                return s.Contains("Tj") || s.Contains("TJ") || s.Contains("Tf") || s.Contains("Tm");
            }
            catch
            {
                return false;
            }
        }
    }
}
