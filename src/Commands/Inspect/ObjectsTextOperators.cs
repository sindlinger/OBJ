using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Lista operadores de texto dentro de objetos stream.
    /// Uso: tjpdf-cli inspect objects textoperators --input file.pdf [--id N] [--limit N]
    /// </summary>
    internal static class ObjectsTextOperators
    {
        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var inputFile, out var idFilter, out var limit, out var opFilter, out var minTextLen))
                return;
            if (string.IsNullOrWhiteSpace(inputFile))
            {
                ShowHelp();
                return;
            }

            using var doc = new PdfDocument(new PdfReader(inputFile));
            int max = doc.GetNumberOfPdfObjects();
            int shown = 0;
            bool any = false;

            for (int i = 0; i < max; i++)
            {
                if (limit > 0 && shown >= limit)
                    break;

                var obj = doc.GetPdfObject(i);
                if (obj is not PdfStream stream)
                    continue;

                int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (objId <= 0)
                    objId = i;

                if (idFilter.Count > 0 && !idFilter.Contains(objId))
                    continue;

                if (!HasTextOperators(stream))
                    continue;

                var type = stream.GetAsName(PdfName.Type)?.ToString() ?? "";
                var subtype = stream.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                var derived = "";
                if (string.IsNullOrWhiteSpace(type) && string.IsNullOrWhiteSpace(subtype))
                    derived = "texto";
                var typeLabel = FormatTypeLabel(type, subtype, derived);

                any = true;
                Console.WriteLine($"[ID] [{objId}] Type={typeLabel}");
                var resources = PdfTextExtraction.TryFindResourcesForObjId(doc, objId, out var found)
                    ? found
                    : new PdfResources(new PdfDictionary());
                var ops = ExtractTextOperators(stream, resources, opFilter, minTextLen);
                if (string.IsNullOrWhiteSpace(ops))
                    Console.WriteLine("(sem operadores)");
                else
                    Console.WriteLine(ops);
                Console.WriteLine();
                shown++;
            }

            if (!any)
                Console.WriteLine("Nenhum objeto com operadores de texto.");
        }

        private static bool ParseOptions(string[] args, out string inputFile, out HashSet<int> ids, out int limit, out HashSet<string> opFilter, out int minTextLen)
        {
            inputFile = "";
            ids = new HashSet<int>();
            limit = 0;
            opFilter = new HashSet<string>(StringComparer.Ordinal);
            minTextLen = 0;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    foreach (var id in ParseIdList(args[++i]))
                        ids.Add(id);
                    continue;
                }
                if (string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        limit = n;
                    continue;
                }
                if ((string.Equals(arg, "--min-text-len", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--min-token-len", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        minTextLen = Math.Max(0, n);
                    continue;
                }
                if ((string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--ops", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        opFilter.Add(raw.Trim());
                    continue;
                }
                if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = arg;
                }
            }
            return true;
        }

        private static IEnumerable<int> ParseIdList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) yield break;
            foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(raw.Trim(), out var id))
                    yield return id;
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects textoperators --input file.pdf [--id N] [--limit N] [--op Tj,TJ]");
            Console.WriteLine("                                     [--min-text-len N]  (filtra so textos longos)");
        }

        private static string FormatTypeLabel(string type, string subtype, string derived)
        {
            var typeLabel = string.IsNullOrWhiteSpace(type) ? "(sem tipo)" : type;
            if (!string.IsNullOrWhiteSpace(subtype))
                return $"{typeLabel} {subtype}";
            if (!string.IsNullOrWhiteSpace(derived))
                return $"{typeLabel}/{derived}";
            return typeLabel;
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

        private static string ExtractTextOperators(PdfStream stream, PdfResources resources, HashSet<string> opFilter, int minTextLen)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return "";

            var tokens = TokenizeContent(bytes);
            var sb = new System.Text.StringBuilder();
            var operands = new List<string>();
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources));

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                string decodedForOp = "";
                if (IsTextShowingOperator(tok))
                    decodedForOp = DequeueDecodedText(tok, operands, textQueue);

                if (TextOperators.Contains(tok) && (opFilter.Count == 0 || opFilter.Contains(tok)))
                {
                    if (minTextLen > 0 && !IsTextShowingOperator(tok))
                    {
                        operands.Clear();
                        continue;
                    }
                    var line = PdfOperatorLegend.AppendDescription(rawLine, tok);
                    if (tok == "Tj" || tok == "TJ" || tok == "'" || tok == "\"")
                    {
                        if (!string.IsNullOrEmpty(decodedForOp))
                            line = $"{line}  => \"{decodedForOp}\" (len={decodedForOp.Length})";
                        if (minTextLen > 0 && decodedForOp.Length < minTextLen)
                        {
                            operands.Clear();
                            continue;
                        }
                    }
                    sb.AppendLine(line);
                }

                operands.Clear();
            }

            return sb.ToString().TrimEnd();
        }

        private static byte[] ExtractStreamBytes(PdfStream stream)
        {
            try { return stream.GetBytes(); } catch { return Array.Empty<byte>(); }
        }

        private static string DequeueDecodedText(string op, List<string> operands, Queue<string> textQueue)
        {
            if (textQueue.Count == 0) return "";
            if (op == "TJ")
            {
                var operandsText = operands.Count > 0 ? string.Join(" ", operands) : "";
                var arrayToken = ExtractArrayToken(operandsText);
                var needed = CountTextChunksInArray(arrayToken);
                if (needed <= 1)
                    return textQueue.Count > 0 ? textQueue.Dequeue() : "";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < needed && textQueue.Count > 0; i++)
                    sb.Append(textQueue.Dequeue());
                return sb.ToString();
            }
            return textQueue.Count > 0 ? textQueue.Dequeue() : "";
        }

        private static bool IsTextShowingOperator(string op)
        {
            return op == "Tj" || op == "TJ" || op == "'" || op == "\"";
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

        private static string ExtractArrayToken(string operands)
        {
            if (string.IsNullOrWhiteSpace(operands)) return "";
            int start = operands.IndexOf('[');
            int end = operands.LastIndexOf(']');
            if (start >= 0 && end > start)
                return operands.Substring(start, end - start + 1);
            return "";
        }

        private static int CountTextChunksInArray(string? arrayToken)
        {
            if (string.IsNullOrWhiteSpace(arrayToken)) return 0;
            int count = 0;
            int i = 0;
            while (i < arrayToken.Length)
            {
                char c = arrayToken[i];
                if (c == '(')
                {
                    ReadLiteralString(arrayToken, ref i);
                    count++;
                    continue;
                }
                if (c == '<')
                {
                    ReadHexString(arrayToken, ref i);
                    count++;
                    continue;
                }
                i++;
            }
            return count;
        }

        private static string ReadLiteralString(string text, ref int i)
        {
            int start = i;
            i++; // '('
            int depth = 1;
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')') depth--;
                i++;
            }
            return text.Substring(start, i - start);
        }

        private static string ReadHexString(string text, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < text.Length && text[i] != '>') i++;
            if (i < text.Length) i++;
            return text.Substring(start, i - start);
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

        private static bool IsOperatorToken(string token)
        {
            return Operators.Contains(token);
        }

        private static readonly HashSet<string> Operators = new HashSet<string>
        {
            "q","Q","cm","w","J","j","M","d","ri","i","gs",
            "m","l","c","v","y","h","re","S","s","f","F","f*","B","B*","b","b*","n",
            "W","W*",
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\"",
            "Do","BI","ID","EI"
        };

        private static readonly HashSet<string> TextOperators = new HashSet<string>
        {
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\""
        };

    }
}
