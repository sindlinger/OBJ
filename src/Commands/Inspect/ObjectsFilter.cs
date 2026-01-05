using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Filtra objetos por Type/Subtype (ou derivados) e lista os resultados.
    /// Uso:
    ///   tjpdf-cli inspect objects filter /XObject /Image --input file.pdf
    ///   tjpdf-cli inspect objects filter /Font /Type0 --input file.pdf
    ///   tjpdf-cli inspect objects filter texto --input file.pdf
    /// </summary>
    internal static class ObjectsFilter
    {
        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var inputFile, out var typeToken, out var subtypeToken))
                return;

            if (string.IsNullOrWhiteSpace(inputFile))
            {
                ShowHelp();
                return;
            }

            var derivedTarget = NormalizeDerived(typeToken);
            var typeTarget = string.IsNullOrWhiteSpace(derivedTarget)
                ? NormalizeTypeName(typeToken)
                : "";
            var subtypeTarget = string.IsNullOrWhiteSpace(derivedTarget)
                ? NormalizeTypeName(subtypeToken)
                : "";

            using var doc = new PdfDocument(new PdfReader(inputFile));
            var contentStreamResources = BuildContentStreamResources(doc);

            var rows = new List<Row>();
            int max = doc.GetNumberOfPdfObjects();
            for (int i = 0; i < max; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj == null || obj.IsNull()) continue;

                string type = "";
                string subtype = "";
                if (obj is PdfDictionary dict)
                {
                    type = dict.GetAsName(PdfName.Type)?.ToString() ?? "";
                    subtype = dict.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                }

                string derived = "";
                if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(subtype))
                {
                    if (obj is PdfStream stream)
                    {
                        var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                        if (id > 0 && contentStreamResources.TryGetValue(id, out var resources))
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
                        derived = "dict";
                    }
                }

                if (!Matches(typeTarget, subtypeTarget, derivedTarget, type, subtype, derived))
                    continue;

                var detail = DescribeDetail(doc, obj as PdfDictionary, type, subtype);
                long len = obj is PdfStream s ? s.GetLength() : 0;
                int objId = obj.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (objId <= 0) objId = i;
                rows.Add(new Row
                {
                    Id = objId,
                    Type = string.IsNullOrWhiteSpace(type) ? "(sem tipo)" : type,
                    Subtype = string.IsNullOrWhiteSpace(subtype) ? (string.IsNullOrWhiteSpace(derived) ? "-" : derived) : subtype,
                    Length = len,
                    Detail = detail
                });
            }

            if (rows.Count == 0)
            {
                Console.WriteLine("Nenhum objeto encontrado para esse tipo/subtipo.");
                return;
            }

            var typeWidth = Math.Max(12, rows.Max(r => r.Type.Length));
            var subtypeWidth = Math.Max(8, rows.Max(r => r.Subtype.Length));
            var lenWidth = Math.Max(3, rows.Max(r => r.Length.ToString(CultureInfo.InvariantCulture).Length));
            var idWidth = Math.Max(4, rows.Max(r => r.Id.ToString(CultureInfo.InvariantCulture).Length) + 2);

            Console.WriteLine($"[ID]{"".PadLeft(idWidth - 4)}  {"Type".PadRight(typeWidth)}  {"Subtype".PadRight(subtypeWidth)}  {"Len".PadLeft(lenWidth)}  Detalhe");
            foreach (var r in rows.OrderBy(r => r.Id))
            {
                var typePad = r.Type.PadRight(typeWidth);
                var subtypePad = r.Subtype.PadRight(subtypeWidth);
                var lenPad = r.Length.ToString(CultureInfo.InvariantCulture).PadLeft(lenWidth);
                Console.WriteLine($"[{r.Id.ToString(CultureInfo.InvariantCulture).PadLeft(idWidth - 2)}]  {typePad}  {subtypePad}  {lenPad}  {r.Detail}");
            }
        }

        private static bool ParseOptions(string[] args, out string inputFile, out string typeToken, out string subtypeToken)
        {
            inputFile = "";
            typeToken = "";
            subtypeToken = "";

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    typeToken = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--subtype", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    subtypeToken = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(typeToken))
                {
                    typeToken = arg;
                    continue;
                }
                if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(subtypeToken))
                {
                    subtypeToken = arg;
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(typeToken))
            {
                ShowHelp();
                return false;
            }
            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects filter /XObject /Image --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects filter /Font /Type0 --input file.pdf");
            Console.WriteLine("tjpdf-cli inspect objects filter texto|sem_texto|stream|dict --input file.pdf");
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
                    if (item is PdfStream ss) yield return ss;
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
            try { return stream.GetBytes(); } catch { return Array.Empty<byte>(); }
        }

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
            if (i < bytes.Length) i++;
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
                if (bytes[idx + j] != token[j]) return false;
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

        private static string DescribeDetail(PdfDocument doc, PdfDictionary? dict, string type, string subtype)
        {
            if (doc == null || dict == null) return "";
            if (string.Equals(type, "/XObject", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(subtype, "/Image", StringComparison.OrdinalIgnoreCase))
            {
                var w = dict.GetAsNumber(PdfName.Width)?.DoubleValue() ?? 0;
                var h = dict.GetAsNumber(PdfName.Height)?.DoubleValue() ?? 0;
                var cs = dict.GetAsName(PdfName.ColorSpace)?.ToString() ?? "";
                var bpc = dict.GetAsNumber(PdfName.BitsPerComponent)?.IntValue() ?? 0;
                var filter = dict.Get(PdfName.Filter)?.ToString() ?? "";
                return $"tam={FormatNum(w)}x{FormatNum(h)} cs={cs} bpc={bpc} filter={filter}";
            }

            if (string.Equals(type, "/Annot", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(subtype, "/Link", StringComparison.OrdinalIgnoreCase))
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

            if (string.Equals(type, "/Action", StringComparison.OrdinalIgnoreCase))
            {
                var s = dict.GetAsName(PdfName.S)?.ToString() ?? "";
                var uri = dict.GetAsString(PdfName.URI)?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(uri)) return $"S={s} URI={uri}";
                return !string.IsNullOrWhiteSpace(s) ? $"S={s}" : "";
            }

            if (string.Equals(type, "/Font", StringComparison.OrdinalIgnoreCase))
            {
                var baseFont = dict.GetAsName(PdfName.BaseFont)?.ToString() ?? "";
                var enc = dict.GetAsName(PdfName.Encoding)?.ToString() ?? "";
                return $"base={baseFont} enc={enc}".Trim();
            }

            if (string.Equals(type, "/ExtGState", StringComparison.OrdinalIgnoreCase))
            {
                var ca = dict.GetAsNumber(PdfName.ca)?.DoubleValue() ?? 0;
                var CA = dict.GetAsNumber(PdfName.CA)?.DoubleValue() ?? 0;
                return $"CA={FormatNum(CA)} ca={FormatNum(ca)}";
            }

            if (string.Equals(type, "/Page", StringComparison.OrdinalIgnoreCase))
            {
                var media = dict.GetAsArray(PdfName.MediaBox);
                var rotate = dict.GetAsNumber(PdfName.Rotate)?.IntValue() ?? 0;
                return media != null ? $"media={FormatArray(media)} rot={rotate}" : $"rot={rotate}";
            }

            if (string.Equals(type, "/Pages", StringComparison.OrdinalIgnoreCase))
            {
                var count = dict.GetAsNumber(PdfName.Count)?.IntValue() ?? 0;
                return count > 0 ? $"count={count}" : "";
            }

            if (string.Equals(type, "/Catalog", StringComparison.OrdinalIgnoreCase))
            {
                var hasOutlines = dict.GetAsDictionary(PdfName.Outlines) != null;
                return hasOutlines ? "outlines=sim" : "outlines=nao";
            }

            return "";
        }

        private static string FormatArray(PdfArray arr)
        {
            var items = arr.Select(v => v?.ToString() ?? "").ToList();
            return $"[{string.Join(",", items)}]";
        }

        private static string FormatNum(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static readonly HashSet<string> TextOperators = new HashSet<string>
        {
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\""
        };

        private sealed class Row
        {
            public int Id;
            public string Type = "";
            public string Subtype = "";
            public long Length;
            public string Detail = "";
        }
    }
}
