using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DiffMatchPatch;
using FilterPDF.Models;
using FilterPDF.Utils;
using FilterPDF.TjpbDespachoExtractor.Utils;
using iText.Kernel.Pdf;
using Obj.DocDetector;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        private static List<string> ExtractTextOperatorLines(PdfStream stream, PdfResources resources, HashSet<string> opFilter, TokenMode tokenMode)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return new List<string>();

            var tokens = TokenizeContent(bytes);
            var result = new List<string>();
            var operands = new List<string>();
            var textQueue = tokenMode == TokenMode.Text
                ? new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources))
                : new Queue<string>();

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                string decodedForOp = "";
                if (tokenMode == TokenMode.Text && IsTextShowingOperator(tok))
                    decodedForOp = DequeueDecodedText(tok, operands, rawLine, textQueue);

                if (TextOperators.Contains(tok) && (opFilter.Count == 0 || opFilter.Contains(tok)))
                {
                    var line = PdfOperatorLegend.AppendDescription(rawLine, tok);
                    if (tokenMode == TokenMode.Text && (tok == "Tj" || tok == "TJ" || tok == "'" || tok == "\""))
                    {
                        if (!string.IsNullOrEmpty(decodedForOp))
                            line = $"{line}  => \"{decodedForOp}\" (len={decodedForOp.Length})";
                    }
                    result.Add(line);
                }

                operands.Clear();
            }

            return result;
        }

        private static List<string> ExtractTextOperatorTokens(PdfStream stream, PdfResources resources, HashSet<string> opFilter, TokenMode tokenMode)
        {
            return ExtractTextOperatorTokensWithOps(stream, resources, opFilter, tokenMode).Tokens;
        }

        private sealed class TokenOpsResult
        {
            public TokenOpsResult(List<string> tokens, List<int> opStarts, List<int> opEnds, List<string> opNames)
            {
                Tokens = tokens;
                OpStarts = opStarts;
                OpEnds = opEnds;
                OpNames = opNames;
            }

            public List<string> Tokens { get; }
            public List<int> OpStarts { get; }
            public List<int> OpEnds { get; }
            public List<string> OpNames { get; }
        }

        private static TokenOpsResult ExtractTextOperatorTokensWithOps(PdfStream stream, PdfResources resources, HashSet<string> opFilter, TokenMode tokenMode)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0)
                return new TokenOpsResult(new List<string>(), new List<int>(), new List<int>(), new List<string>());

            var tokens = TokenizeContent(bytes);
            var result = new List<string>();
            var opStarts = new List<int>();
            var opEnds = new List<int>();
            var opNames = new List<string>();
            var operands = new List<string>();
            var textQueue = tokenMode == TokenMode.Text
                ? new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources))
                : new Queue<string>();

            int opIndex = 0;
            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (IsTextShowingOperator(tok))
                {
                    var allowed = IsTextOpAllowed(tok, opFilter);
                    string text = "";
                    if (tokenMode == TokenMode.Text)
                    {
                        var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                        text = DequeueDecodedText(tok, operands, rawLine, textQueue) ?? "";
                    }
                    else if (allowed)
                    {
                        text = ExtractRawTextToken(tok, operands);
                    }

                    if (allowed)
                    {
                        opIndex++;
                        result.Add(text);
                        opStarts.Add(opIndex);
                        opEnds.Add(opIndex);
                        opNames.Add(tok);
                    }
                }

                operands.Clear();
            }

            return new TokenOpsResult(result, opStarts, opEnds, opNames);
        }

        private static TokenOpsResult ExtractTextOperatorBlockTokensWithOps(PdfStream stream, PdfResources resources, HashSet<string> opFilter)
        {
            var blocks = ExtractSelfBlocks(stream, resources, opFilter);
            if (blocks.Count == 0)
                return new TokenOpsResult(new List<string>(), new List<int>(), new List<int>(), new List<string>());

            var tokens = new List<string>();
            var opStarts = new List<int>();
            var opEnds = new List<int>();
            var opNames = new List<string>();

            foreach (var block in blocks)
            {
                tokens.Add(block.Text ?? "");
                opStarts.Add(block.StartOp);
                opEnds.Add(block.EndOp);
                opNames.Add("");
            }

            return new TokenOpsResult(tokens, opStarts, opEnds, opNames);
        }

        private sealed class FullTextOpsResult
        {
            public FullTextOpsResult(string text, List<int> opIndexes, List<string> opNames)
            {
                Text = text;
                OpIndexes = opIndexes;
                OpNames = opNames;
            }

            public string Text { get; }
            public List<int> OpIndexes { get; }
            public List<string> OpNames { get; }
        }

        private static FullTextOpsResult ExtractFullTextWithOps(
            PdfStream stream,
            PdfResources resources,
            HashSet<string> opFilter,
            bool includeLineBreaks,
            bool includeTdLineBreaks,
            bool includeTmLineBreaks,
            bool lineBreakAsSpace)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0)
                return new FullTextOpsResult("", new List<int>(), new List<string>());

            var tokens = TokenizeContent(bytes);
            var sb = new StringBuilder();
            var opIndexes = new List<int>();
            var opNames = new List<string>();
            var operands = new List<string>();
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources));

            int opIndex = 0;
            bool hasTm = false;
            double lastTmY = 0;
            int lastTextOpIndex = 0;

            void AppendText(string text, int opIdx, string opName)
            {
                if (string.IsNullOrEmpty(text))
                    return;
                foreach (var ch in text)
                {
                    sb.Append(ch);
                    opIndexes.Add(opIdx);
                    opNames.Add(opName);
                }
            }

            var breakText = includeLineBreaks ? (lineBreakAsSpace ? " " : "\n") : "";

            void AppendBreak(int opIdx, string opName)
            {
                if (string.IsNullOrEmpty(breakText))
                    return;
                if (breakText == " " && sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
                    return;
                foreach (var ch in breakText)
                {
                    sb.Append(ch);
                    opIndexes.Add(opIdx);
                    opNames.Add(opName);
                }
            }

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (IsTextShowingOperator(tok))
                {
                    var decoded = DequeueDecodedText(tok, operands, null, textQueue) ?? "";
                    if (IsTextOpAllowed(tok, opFilter))
                    {
                        opIndex++;
                        AppendText(decoded, opIndex, tok);
                        lastTextOpIndex = opIndex;
                        if (includeLineBreaks && IsLineBreakTextOperator(tok))
                            AppendBreak(opIndex, tok);
                    }
                }
                else if (includeLineBreaks)
                {
                    if (IsExplicitLineBreakOperator(tok))
                        AppendBreak(lastTextOpIndex, "");
                    else if (includeTdLineBreaks && (tok == "Td" || tok == "TD"))
                    {
                        if (TryParseTdY(operands, out var ty) && Math.Abs(ty) > 0.01)
                            AppendBreak(lastTextOpIndex, "");
                    }
                    else if (includeTmLineBreaks && tok == "Tm")
                    {
                        if (TryParseTmY(operands, out var y))
                        {
                            if (hasTm && Math.Abs(y - lastTmY) > 0.01)
                                AppendBreak(lastTextOpIndex, "");
                            hasTm = true;
                            lastTmY = y;
                        }
                    }
                }

                operands.Clear();
            }

            return new FullTextOpsResult(sb.ToString(), opIndexes, opNames);
        }

        private static bool TryParseTmY(List<string> operands, out double y)
        {
            y = 0;
            if (operands == null || operands.Count < 6)
                return false;
            if (double.TryParse(operands[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var f))
            {
                y = f;
                return true;
            }
            return false;
        }

        private static bool TryParseTdY(List<string> operands, out double y)
        {
            y = 0;
            if (operands == null || operands.Count < 2)
                return false;
            if (double.TryParse(operands[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var ty))
            {
                y = ty;
                return true;
            }
            return false;
        }

        private static string ExtractDecodedTextFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            int lastSpace = line.LastIndexOf(' ');
            if (lastSpace < 0) return "";
            var op = line.Substring(lastSpace + 1);
            var operands = line.Substring(0, lastSpace).Trim();

            if (op == "Tj" || op == "'" || op == "\"")
            {
                var token = ExtractLastTextToken(operands);
                return ExtractTextOperand(token);
            }
            if (op == "TJ")
            {
                var token = ExtractArrayToken(operands);
                return ExtractTextFromArray(token);
            }
            return "";
        }

        private static string DequeueDecodedText(string op, List<string> operands, string? rawLine, Queue<string> textQueue)
        {
            if (textQueue.Count == 0)
                return rawLine != null ? ExtractDecodedTextFromLine(rawLine) : "";

            if (op == "TJ")
            {
                var operandsText = operands.Count > 0 ? string.Join(" ", operands) : "";
                var arrayToken = ExtractArrayToken(operandsText);
                var needed = CountTextChunksInArray(arrayToken);
                if (needed <= 1)
                    return textQueue.Count > 0 ? textQueue.Dequeue() : ExtractDecodedTextFromLine(rawLine ?? "");

                var sb = new StringBuilder();
                for (int i = 0; i < needed && textQueue.Count > 0; i++)
                    sb.Append(textQueue.Dequeue());
                var joined = sb.ToString();
                return !string.IsNullOrWhiteSpace(joined) ? joined : (rawLine != null ? ExtractDecodedTextFromLine(rawLine) : "");
            }

            return textQueue.Count > 0 ? textQueue.Dequeue() : (rawLine != null ? ExtractDecodedTextFromLine(rawLine) : "");
        }

        private static string ExtractRawTextToken(string op, List<string> operands)
        {
            var operandsText = operands.Count > 0 ? string.Join(" ", operands) : "";
            if (op == "TJ")
                return ExtractArrayToken(operandsText);
            return ExtractLastTextToken(operandsText);
        }

        private static string StripDescription(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            int idx = line.IndexOf("->", StringComparison.Ordinal);
            if (idx < 0) return line;
            return line.Substring(0, idx).TrimEnd();
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

        private static string ExtractLastTextToken(string operands)
        {
            if (string.IsNullOrWhiteSpace(operands)) return "";
            var s = operands.TrimEnd();
            if (s.EndsWith(")", StringComparison.Ordinal))
            {
                int depth = 0;
                for (int i = s.Length - 1; i >= 0; i--)
                {
                    char c = s[i];
                    if (c == ')') depth++;
                    else if (c == '(')
                    {
                        depth--;
                        if (depth == 0)
                            return s.Substring(i);
                    }
                }
            }
            if (s.EndsWith(">", StringComparison.Ordinal))
            {
                int start = s.LastIndexOf('<');
                if (start >= 0)
                    return s.Substring(start);
            }
            return "";
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

        private static bool IsTextShowingOperator(string op)
        {
            return op == "Tj" || op == "TJ" || op == "'" || op == "\"";
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
                if (IsWhite(c)) { i++; continue; }
                if (c == '%') { i = SkipToEol(bytes, i); continue; }
                if (c == '(') { tokens.Add(ReadLiteralString(bytes, ref i)); continue; }
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
                if (c == '[') { tokens.Add(ReadArray(bytes, ref i)); continue; }
                if (c == '/') { tokens.Add(ReadName(bytes, ref i)); continue; }
                if (IsDelimiter(c)) { tokens.Add(c.ToString()); i++; continue; }
                tokens.Add(ReadToken(bytes, ref i));
            }
            return tokens;
        }

        private static bool IsOperatorToken(string token) => Operators.Contains(token);

        private static bool IsWhite(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f';

        private static bool IsDelimiter(char c)
            => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';

        private static int SkipToEol(byte[] bytes, int i)
        {
            while (i < bytes.Length && bytes[i] != '\n' && bytes[i] != '\r') i++;
            return i;
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
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadLiteralString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '('
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                char c = (char)bytes[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')') depth--;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadHexString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < bytes.Length && bytes[i] != '>') i++;
            if (i < bytes.Length) i++;
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadBalanced(byte[] bytes, ref int i, string open, string close)
        {
            int start = i;
            int depth = 0;
            while (i < bytes.Length)
            {
                if (i + 1 < bytes.Length && bytes[i] == open[0] && bytes[i + 1] == open[1]) depth++;
                if (i + 1 < bytes.Length && bytes[i] == close[0] && bytes[i + 1] == close[1]) depth--;
                i++;
                if (depth == 0) { i++; break; }
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadArray(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '['
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                char c = (char)bytes[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '[') depth++;
                if (c == ']') depth--;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadName(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '/'
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ExtractTextOperand(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            if (token.StartsWith("(", StringComparison.Ordinal))
                return UnwrapLiteral(token);
            if (token.StartsWith("<", StringComparison.Ordinal) && token.EndsWith(">", StringComparison.Ordinal))
                return DecodeHexString(token);
            return "";
        }

        private static string ExtractTextFromArray(string? arrayToken)
        {
            if (string.IsNullOrWhiteSpace(arrayToken)) return "";
            var sb = new StringBuilder();
            int i = 0;
            while (i < arrayToken.Length)
            {
                char c = arrayToken[i];
                if (c == '(')
                {
                    var lit = ReadLiteralString(arrayToken, ref i);
                    sb.Append(UnwrapLiteral(lit));
                    continue;
                }
                if (c == '<')
                {
                    var hex = ReadHexString(arrayToken, ref i);
                    sb.Append(DecodeHexString(hex));
                    continue;
                }
                i++;
            }
            return sb.ToString();
        }

        private static string UnwrapLiteral(string token)
        {
            if (token.Length >= 2 && token[0] == '(' && token[^1] == ')')
                return token.Substring(1, token.Length - 2);
            return token;
        }

        private static string DecodeHexString(string token)
        {
            if (token.Length < 2) return "";
            var hex = token.Trim('<', '>');
            if (hex.Length % 2 != 0) hex += "0";
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, null, out var b))
                    b = 0x20;
                bytes[i] = b;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
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

        private static readonly HashSet<string> TextOperators = new HashSet<string>
        {
            "BT", "ET", "Tf", "Tm", "Td", "TD", "Tj", "TJ", "'", "\"", "T*", "Ts", "Tc", "Tw", "Tz", "Tr"
        };

        private static readonly HashSet<string> Operators = new HashSet<string>
        {
            "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX",
            "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP",
            "EI", "EMC", "ET", "EX", "f", "F", "f*", "G", "g",
            "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m",
            "M", "MP", "n", "q", "Q", "re", "rg", "RG", "ri", "s",
            "S", "SC", "sc", "SCN", "scn", "sh", "T*", "Tc", "Td",
            "TD", "Tf", "Tj", "TJ", "TL", "Tm", "Tr", "Ts", "Tw",
            "Tz", "v", "w", "W", "W*", "y", "'", "\""
        };

    }
}

    }
}
