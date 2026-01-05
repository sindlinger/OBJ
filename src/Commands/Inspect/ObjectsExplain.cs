using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using iText.Kernel.Pdf;
using Spectre.Console;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Explica tipos de objetos PDF e mostra contagens/ids de exemplo.
    /// Uso: tjpdf-cli inspect objects table --input file.pdf [--limit N]
    /// </summary>
    internal static class ObjectsExplain
    {
        public static void Execute(string[] args)
        {
            if (!ParseCommonOptions(args, out var inputFile, out var opts))
                return;
            if (string.IsNullOrWhiteSpace(inputFile))
            {
                Console.WriteLine("Informe --input <file.pdf>");
                return;
            }

            int limit = opts.TryGetValue("--limit", out var l) && int.TryParse(l, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
                ? n
                : int.MaxValue;

            var byType = new Dictionary<string, TypeSummary>(StringComparer.OrdinalIgnoreCase);
            var contentStreamResources = BuildContentStreamResources(inputFile);
            var contentTextIds = new HashSet<int>();
            var contentNoTextIds = new HashSet<int>();

            using var doc = new PdfDocument(new PdfReader(inputFile));
            int max = doc.GetNumberOfPdfObjects();
            int count = 0;
            for (int i = 0; i < max && count < limit; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj == null || obj.IsNull()) continue;

                string type = "";
                string subtype = "";
                bool isStream = obj is PdfStream;
                bool isDict = obj is PdfDictionary;

                int objId = obj.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (objId <= 0)
                    objId = i;
                if (obj is PdfDictionary dict)
                {
                    var tp = dict.GetAsName(PdfName.Type);
                    var st = dict.GetAsName(PdfName.Subtype);
                    if (tp != null) type = tp.ToString();
                    if (st != null) subtype = st.ToString();
                }

                if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(subtype))
                {
                    if (obj is PdfStream stream)
                    {
                        if (objId > 0 && contentStreamResources.TryGetValue(objId, out var resources))
                        {
                            var hasText = HasTextOperators(stream);
                            if (hasText)
                            {
                                subtype = "texto";
                                contentTextIds.Add(objId);
                            }
                            else
                            {
                                subtype = "sem_texto";
                                contentNoTextIds.Add(objId);
                            }
                        }
                        else
                        {
                            subtype = "stream";
                        }
                    }
                    else if (obj is PdfDictionary)
                    {
                        subtype = "dict";
                    }
                }

                var key = BuildTypeKey(type, subtype);
                if (!byType.TryGetValue(key, out var summary))
                {
                    summary = new TypeSummary { Key = key, Type = type, Subtype = subtype };
                    byType[key] = summary;
                }
                summary.Count++;
                if (isStream) summary.Streams++;
                if (isDict) summary.Dictories++;
                if (summary.SampleIds.Count < 8)
                {
                    summary.SampleIds.Add(i);
                    summary.SampleObjIds.Add(objId);
                }
                count++;
            }

            ObjectsLegend.WriteLegend();

            var ordered = byType
                .OrderBy(k => GetRank(k.Key))
                .ThenByDescending(k => k.Value.Count)
                .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = ordered.Select(e =>
            {
                var s = e.Value;
                var typeLabel = string.IsNullOrWhiteSpace(s.Type) ? "(sem tipo)" : s.Type;
                var subtypeLabel = string.IsNullOrWhiteSpace(s.Subtype) ? "-" : s.Subtype;
                return new
                {
                    Type = typeLabel,
                    Subtype = subtypeLabel,
                    Count = s.Count,
                    Ids = BuildIds(s),
                    SampleId = s.SampleIds.FirstOrDefault(),
                    Explain = ExplainType(s.Key, s)
                };
            }).ToList();

            var table = new Table
            {
                Border = TableBorder.Rounded,
                Expand = true,
                Width = AnsiConsole.Profile.Width
            };
            table.AddColumn(new TableColumn("Type\n(/Type)").LeftAligned());
            table.AddColumn(new TableColumn("Subtype\n(/Subtype)").LeftAligned());
            table.AddColumn(new TableColumn("Qtd").RightAligned());
            table.AddColumn(new TableColumn("[IDs]").LeftAligned());
            table.AddColumn(new TableColumn("Descri\ncao").LeftAligned());

            foreach (var row in rows)
            {
                var detail = DescribeDetail(doc, row.Type, row.Subtype, row.SampleId);
                var desc = string.IsNullOrWhiteSpace(detail) ? row.Explain : $"{row.Explain} Detalhe: {detail}";
                table.AddRow(
                    new Text(row.Type) { Overflow = Overflow.Fold },
                    new Text(row.Subtype) { Overflow = Overflow.Fold },
                    new Text($"({row.Count.ToString(CultureInfo.InvariantCulture)})"),
                    new Text(row.Ids) { Overflow = Overflow.Fold },
                    new Text(desc) { Overflow = Overflow.Fold }
                );
            }

            AnsiConsole.Write(table);

            if (contentTextIds.Count > 0 || contentNoTextIds.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Resumo de texto (streams /Contents):[/]");
                AnsiConsole.MarkupLine($"- [green]COM TEXTO[/]: {Markup.Escape(FormatIdList(contentTextIds))}");
                AnsiConsole.MarkupLine($"- [yellow]SEM TEXTO[/]: {Markup.Escape(FormatIdList(contentNoTextIds))}");
            }
        }

        private static string BuildTypeKey(string type, string subtype)
        {
            if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(subtype))
                return $"{type}/{subtype}";
            if (!string.IsNullOrWhiteSpace(type)) return type;
            if (!string.IsNullOrWhiteSpace(subtype)) return $"(sem tipo)/{subtype}";
            return "(sem tipo)";
        }

        private static string ExplainType(string key, TypeSummary summary)
        {
            var norm = (key ?? "").Replace(" ", "").ToUpperInvariant();

            if (norm == "/CATALOG")
                return "Raiz do documento; aponta para /Pages e metadados globais.";
            if (norm == "/PAGES")
                return "Árvore de páginas; contém /Kids e /Count.";
            if (norm == "/PAGE")
                return "Página individual; aponta /Contents e /Resources.";
            if (norm.Contains("XOBJECT") && norm.Contains("IMAGE"))
                return "Imagem embutida (scanner, carimbo, rubrica digitalizada).";
            if (norm.Contains("XOBJECT") && norm.Contains("FORM"))
                return "Form XObject: conteúdo reutilizável desenhado na página.";
            if (norm.Contains("FONT") && norm.Contains("TYPE0"))
                return "Fonte composta (Unicode).";
            if (norm.Contains("FONT") && norm.Contains("CIDFONTTYPE2"))
                return "Sub-fonte TrueType (CIDFontType2).";
            if (norm.Contains("FONT") && norm.Contains("TYPE1"))
                return "Fonte Type1.";
            if (norm.Contains("FONTDESCRIPTOR"))
                return "Descriptor de fonte (métricas e flags).";
            if (norm.Contains("ANNOT") && norm.Contains("LINK"))
                return "Anotação de link clicável.";
            if (norm.Contains("ACTION"))
                return "Ação associada a links/outlines (ex.: URI/GoTo).";
            if (norm.Contains("EXTGSTATE"))
                return "Estado gráfico (opacidade, blend, etc.).";
            if (norm == "(SEMTIPO)/TEXTO")
                return "Stream de conteúdo com texto (conteúdo da página).";
            if (norm == "(SEMTIPO)/SEM_TEXTO")
                return "Stream de conteúdo sem texto (apenas gráficos/linhas).";
            if (norm == "(SEMTIPO)/STREAM")
                return "Stream sem /Type fora de /Contents; dado bruto ou auxiliar.";
            if (norm == "(SEMTIPO)/DICT")
                return "Dicionário sem /Type; costuma ser metadado ou objeto auxiliar.";
            if (norm == "(SEMTIPO)")
                return summary.Streams > 0
                    ? "Stream sem /Type; normalmente /Contents (texto/desenho)."
                    : "Objeto sem /Type; metadado ou auxiliar.";

            return "";
        }

        private static int GetRank(string key)
        {
            if (FixedOrder.TryGetValue(key ?? "", out var rank))
                return rank;
            return 1000;
        }

        private static string BuildIds(TypeSummary summary)
        {
            if (summary.SampleObjIds.Count == 0) return "[]";
            return string.Join(" ", summary.SampleObjIds.Select(id => $"[{id}]"));
        }

        private static string FormatIdList(HashSet<int> ids)
        {
            if (ids.Count == 0) return "[]";
            return string.Join(" ", ids.OrderBy(i => i).Select(i => $"[{i}]"));
        }

        private static readonly Dictionary<string, int> FixedOrder = new(StringComparer.OrdinalIgnoreCase)
        {
            { "/Catalog", 10 },
            { "/Pages", 20 },
            { "/Page", 30 },
            { "/XObject//Image", 40 },
            { "/Annot//Link", 50 },
            { "/Action", 60 },
            { "/Font//Type0", 70 },
            { "/Font//CIDFontType2", 71 },
            { "/FontDescriptor", 72 },
            { "/Font//Type1", 73 },
            { "/ExtGState", 80 },
            { "(sem tipo)/texto", 90 },
            { "(sem tipo)/sem_texto", 91 },
            { "(sem tipo)/stream", 92 },
            { "(sem tipo)/dict", 93 },
            { "(sem tipo)", 94 },
        };

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

        private sealed class TypeSummary
        {
            public string Key = "";
            public string Type = "";
            public string Subtype = "";
            public int Count;
            public int Streams;
            public int Dictories;
            public List<int> SampleIds = new();
            public List<int> SampleObjIds = new();
        }

        private static string DescribeDetail(PdfDocument doc, string type, string subtype, int sampleId)
        {
            if (doc == null || sampleId <= 0) return "";
            var obj = doc.GetPdfObject(sampleId);
            if (obj is not PdfDictionary dict) return "";

            var t = (type ?? "").Trim();
            var st = (subtype ?? "").Trim();

            if (string.Equals(t, "/XObject", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(st, "/Image", StringComparison.OrdinalIgnoreCase))
            {
                var w = dict.GetAsNumber(PdfName.Width)?.DoubleValue() ?? 0;
                var h = dict.GetAsNumber(PdfName.Height)?.DoubleValue() ?? 0;
                var cs = dict.GetAsName(PdfName.ColorSpace)?.ToString() ?? "";
                var bpc = dict.GetAsNumber(PdfName.BitsPerComponent)?.IntValue() ?? 0;
                var filter = dict.Get(PdfName.Filter)?.ToString() ?? "";
                return $"tam={FormatNum(w)}x{FormatNum(h)} cs={cs} bpc={bpc} filter={filter}";
            }

            if (string.Equals(t, "/Annot", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(st, "/Link", StringComparison.OrdinalIgnoreCase))
            {
                var rect = dict.GetAsArray(PdfName.Rect);
                var rectStr = rect != null ? $"rect={FormatArray(rect)}" : "";
                var action = dict.GetAsDictionary(PdfName.A);
                var s = action?.GetAsName(PdfName.S)?.ToString() ?? "";
                var uri = action?.GetAsString(PdfName.URI)?.ToString() ?? "";
                var detail = $"{rectStr}";
                if (!string.IsNullOrWhiteSpace(s)) detail += $" action={s}";
                if (!string.IsNullOrWhiteSpace(uri)) detail += $" uri={uri}";
                return detail.Trim();
            }

            if (string.Equals(t, "/Action", StringComparison.OrdinalIgnoreCase))
            {
                var s = dict.GetAsName(PdfName.S)?.ToString() ?? "";
                var uri = dict.GetAsString(PdfName.URI)?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(uri)) return $"S={s} URI={uri}";
                return !string.IsNullOrWhiteSpace(s) ? $"S={s}" : "";
            }

            if (string.Equals(t, "/Font", StringComparison.OrdinalIgnoreCase))
            {
                var baseFont = dict.GetAsName(PdfName.BaseFont)?.ToString() ?? "";
                var enc = dict.GetAsName(PdfName.Encoding)?.ToString() ?? "";
                return $"base={baseFont} enc={enc}".Trim();
            }

            if (string.Equals(t, "/ExtGState", StringComparison.OrdinalIgnoreCase))
            {
                var ca = dict.GetAsNumber(PdfName.ca)?.DoubleValue() ?? 0;
                var CA = dict.GetAsNumber(PdfName.CA)?.DoubleValue() ?? 0;
                return $"CA={FormatNum(CA)} ca={FormatNum(ca)}";
            }

            if (string.Equals(t, "/Page", StringComparison.OrdinalIgnoreCase))
            {
                var media = dict.GetAsArray(PdfName.MediaBox);
                var rotate = dict.GetAsNumber(PdfName.Rotate)?.IntValue() ?? 0;
                return media != null ? $"media={FormatArray(media)} rot={rotate}" : $"rot={rotate}";
            }

            if (string.Equals(t, "/Pages", StringComparison.OrdinalIgnoreCase))
            {
                var count = dict.GetAsNumber(PdfName.Count)?.IntValue() ?? 0;
                return count > 0 ? $"count={count}" : "";
            }

            if (string.Equals(t, "/Catalog", StringComparison.OrdinalIgnoreCase))
            {
                var hasOutlines = dict.GetAsDictionary(PdfName.Outlines) != null;
                return hasOutlines ? "outlines=sim" : "outlines=nao";
            }

            return "";
        }

        private static string FormatArray(iText.Kernel.Pdf.PdfArray arr)
        {
            var items = arr.Select(v => v?.ToString() ?? "").ToList();
            return $"[{string.Join(",", items)}]";
        }

        private static string FormatNum(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static Dictionary<int, PdfResources> BuildContentStreamResources(string inputFile)
        {
            var map = new Dictionary<int, PdfResources>();
            using var doc = new PdfDocument(new PdfReader(inputFile));
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                foreach (var stream in EnumerateStreams(contents))
                {
                    var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id > 0 && !map.ContainsKey(id))
                        map[id] = resources;
                }
            }
            return map;
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

        private static bool HasTextOperators(PdfStream stream)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return false;
            var tokens = TokenizeContent(bytes);
            foreach (var tok in tokens)
            {
                if (TextOperators.Contains(tok))
                    return true;
            }
            return false;
        }

        private static byte[] ExtractStreamBytes(PdfStream stream)
        {
            try
            {
                return stream.GetBytes();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static readonly HashSet<string> TextOperators = new(StringComparer.Ordinal)
        {
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\""
        };

        private static List<string> TokenizeContent(byte[] bytes)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c))
                {
                    i++;
                    continue;
                }
                if (c == '%')
                {
                    i = SkipToEol(bytes, i);
                    continue;
                }
                if (c == '(')
                {
                    tokens.Add(ReadLiteralString(bytes, ref i));
                    continue;
                }
                if (c == '<')
                {
                    if (i + 1 < bytes.Length && bytes[i + 1] == '<')
                    {
                        tokens.Add(ReadBalanced(bytes, ref i, "<<", ">>"));
                        continue;
                    }
                    tokens.Add(ReadHexString(bytes, ref i));
                    continue;
                }
                if (c == '[')
                {
                    tokens.Add(ReadArray(bytes, ref i));
                    continue;
                }
                if (c == '/')
                {
                    tokens.Add(ReadName(bytes, ref i));
                    continue;
                }

                tokens.Add(ReadToken(bytes, ref i));
            }

            return tokens;
        }

        private static string ReadToken(byte[] bytes, ref int i)
        {
            int start = i;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadName(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // skip '/'
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadLiteralString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '('
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i++];
                if (b == '\\')
                {
                    if (i < bytes.Length) i++;
                    continue;
                }
                if (b == '(') depth++;
                else if (b == ')') depth--;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadHexString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < bytes.Length && bytes[i] != '>')
                i++;
            if (i < bytes.Length) i++; // '>'
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadBalanced(byte[] bytes, ref int i, string startToken, string endToken)
        {
            int start = i;
            i += startToken.Length;
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                if (Match(bytes, i, startToken))
                {
                    depth++;
                    i += startToken.Length;
                    continue;
                }
                if (Match(bytes, i, endToken))
                {
                    depth--;
                    i += endToken.Length;
                    continue;
                }
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadArray(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '['
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i];
                if (b == '[') depth++;
                else if (b == ']') depth--;
                i++;
            }
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static bool Match(byte[] bytes, int idx, string token)
        {
            if (idx + token.Length > bytes.Length) return false;
            for (int j = 0; j < token.Length; j++)
            {
                if (bytes[idx + j] != token[j]) return false;
            }
            return true;
        }

        private static int SkipToEol(byte[] bytes, int i)
        {
            while (i < bytes.Length)
            {
                byte b = bytes[i++];
                if (b == '\n' || b == '\r') break;
            }
            return i;
        }

        private static bool IsWhite(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        private static bool IsDelimiter(char c)
        {
            return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
        }
    }
}
