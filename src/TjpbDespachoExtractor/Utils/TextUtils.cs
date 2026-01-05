using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FilterPDF.Models;
using FilterPDF.TjpbDespachoExtractor.Models;

namespace FilterPDF.TjpbDespachoExtractor.Utils
{
    public static class TextUtils
    {
        public static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var cleaned = Regex.Replace(text, "\\s+", " ");
            return cleaned.Trim();
        }

        public static string FixMissingSpaces(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var t = text;
            t = Regex.Replace(t, @"(?<=[,:;])(?=\S)", " ");
            t = Regex.Replace(t, @"(?<=\))(?=\S)", ") ");
            t = Regex.Replace(t, @"(?<=\S)(?=\()", " ");
            t = Regex.Replace(t, @"(?<=[A-Za-zÀ-ÿ])(?=[0-9])", " ");
            t = Regex.Replace(t, @"(?<=[0-9])(?=[A-Za-zÀ-ÿ])", " ");
            t = Regex.Replace(t, "\\s+", " ");
            return t.Trim();
        }

        public static string BuildLineText(List<WordInfo> words, double wordGapX)
        {
            if (words == null || words.Count == 0) return "";
            var ordered = DeduplicateWords(words).OrderBy(w => w.NormX0).ToList();
            var gapThreshold = wordGapX;
            if (ordered.Count >= 4)
            {
                var gaps = new List<double>();
                for (int i = 1; i < ordered.Count; i++)
                {
                    var gap = ordered[i].NormX0 - ordered[i - 1].NormX1;
                    if (gap > 0) gaps.Add(gap);
                }
                if (gaps.Count >= 4)
                {
                    gaps.Sort();
                    var idx = (int)Math.Floor(0.9 * (gaps.Count - 1));
                    var p90 = gaps[idx];
                    var dynamic = p90 * 0.3;
                    gapThreshold = Math.Max(0.001, Math.Min(wordGapX, dynamic));
                }
            }
            var sb = new StringBuilder();
            WordInfo? prev = null;
            foreach (var w in ordered)
            {
                if (prev != null)
                {
                    var gap = w.NormX0 - prev.NormX1;
                    if (gap > gapThreshold)
                        sb.Append(' ');
                }
                sb.Append(NormalizeToken(w.Text));
                prev = w;
            }
            return NormalizeWhitespace(sb.ToString());
        }

        public static string BuildTextFromWords(List<WordInfo> words, double lineMergeY, double wordGapX)
        {
            if (words == null || words.Count == 0) return "";
            var ordered = DeduplicateWords(words)
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();

            var lines = new List<List<WordInfo>>();
            var current = new List<WordInfo>();
            double prevCy = double.MaxValue;
            foreach (var w in ordered)
            {
                var cy = (w.NormY0 + w.NormY1) / 2.0;
                if (current.Count == 0 || Math.Abs(cy - prevCy) <= lineMergeY)
                {
                    current.Add(w);
                }
                else
                {
                    lines.Add(current);
                    current = new List<WordInfo> { w };
                }
                prevCy = cy;
            }
            if (current.Count > 0) lines.Add(current);

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(BuildLineText(line, wordGapX));
            }
            return NormalizeWhitespace(sb.ToString());
        }

        public static string NormalizeForHash(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.ToLowerInvariant();
            t = Regex.Replace(t, "\\p{C}+", " ");
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        public static string NormalizeForMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var collapsed = CollapseSpacedLettersText(text);
            var t = RemoveDiacritics(collapsed).ToLowerInvariant();
            t = Regex.Replace(t, "[^a-z0-9\\s/\\-\\.]+", " ");
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        public static string NormalizeForDiff(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = RemoveDiacritics(text);
            return t.ToLowerInvariant();
        }

        public static string NormalizeToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            return CollapseDoubledChars(token);
        }

        public static string CollapseDoubledChars(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            var t = token;
            if (t.Length < 4 || (t.Length % 2) != 0)
                return t;
            int totalPairs = t.Length / 2;
            int equalPairs = 0;
            for (int i = 0; i < t.Length - 1; i += 2)
            {
                if (t[i] == t[i + 1])
                    equalPairs++;
            }
            if (equalPairs < Math.Max(3, (int)Math.Ceiling(totalPairs * 0.7)))
                return t;
            var sb = new StringBuilder(totalPairs);
            for (int i = 0; i < t.Length; i += 2)
                sb.Append(t[i]);
            return sb.ToString();
        }

        public static string CollapseSpacedLettersText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var parts = Regex.Matches(text, "\\S+|\\s+");
            if (parts.Count == 0) return "";
            var sb = new StringBuilder();
            var buffer = new StringBuilder();
            string pendingWs = "";

            foreach (Match m in parts)
            {
                var s = m.Value;
                if (string.IsNullOrWhiteSpace(s))
                {
                    pendingWs = s;
                    continue;
                }

                var token = s;
                var joinable = IsJoinToken(token);
                var canJoin = joinable && buffer.Length > 0 && IsTightSpace(pendingWs);

                if (canJoin)
                {
                    buffer.Append(token);
                }
                else
                {
                    if (buffer.Length > 0)
                    {
                        AppendWithSpace(sb, buffer.ToString());
                        buffer.Clear();
                    }
                    if (!string.IsNullOrEmpty(pendingWs))
                        AppendSpace(sb);

                    if (joinable)
                        buffer.Append(token);
                    else
                        AppendWithSpace(sb, token);
                }
                pendingWs = "";
            }

            if (buffer.Length > 0)
                AppendWithSpace(sb, buffer.ToString());

            return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
        }

        public static List<WordInfo> DeduplicateWords(List<WordInfo> words, int decimals = 3)
        {
            if (words == null || words.Count == 0) return new List<WordInfo>();
            var map = new Dictionary<string, WordInfo>();
            foreach (var w in words)
            {
                if (w == null || string.IsNullOrWhiteSpace(w.Text)) continue;
                var key = $"{w.Text}|{Math.Round(w.NormX0, decimals)}|{Math.Round(w.NormY0, decimals)}|{Math.Round(w.NormX1, decimals)}|{Math.Round(w.NormY1, decimals)}";
                if (!map.ContainsKey(key))
                    map[key] = w;
            }
            return map.Values.ToList();
        }

        private static bool IsJoinToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (token.Length == 1)
            {
                var c = token[0];
                if (char.IsLetterOrDigit(c)) return true;
                if (c == '$' || c == '/' || c == '-' || c == '–' || c == '.' || c == ',' ||
                    c == 'ª' || c == 'º' || c == '°')
                    return true;
            }
            return false;
        }

        private static bool IsTightSpace(string ws)
        {
            if (string.IsNullOrEmpty(ws)) return false;
            if (ws.IndexOf('\n') >= 0 || ws.IndexOf('\r') >= 0) return false;
            return ws.Length == 1 && ws[0] == ' ';
        }

        private static void AppendSpace(StringBuilder sb)
        {
            if (sb.Length == 0) return;
            if (sb[sb.Length - 1] != ' ')
                sb.Append(' ');
        }

        private static void AppendWithSpace(StringBuilder sb, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                sb.Append(' ');
            sb.Append(token);
        }

        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static string Sha256Hex(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static BBoxN? UnionBBox(IEnumerable<WordInfo> words)
        {
            if (words == null) return null;
            double x0 = 1, y0 = 1, x1 = 0, y1 = 0;
            bool any = false;
            foreach (var w in words)
            {
                any = true;
                x0 = Math.Min(x0, w.NormX0);
                y0 = Math.Min(y0, w.NormY0);
                x1 = Math.Max(x1, w.NormX1);
                y1 = Math.Max(y1, w.NormY1);
            }
            if (!any) return null;
            return new BBoxN { X0 = Clamp01(x0), Y0 = Clamp01(y0), X1 = Clamp01(x1), Y1 = Clamp01(y1) };
        }

        public static BBoxN? UnionBBox(IEnumerable<BBoxN> boxes)
        {
            if (boxes == null) return null;
            double x0 = 1, y0 = 1, x1 = 0, y1 = 0;
            bool any = false;
            foreach (var b in boxes)
            {
                any = true;
                x0 = Math.Min(x0, b.X0);
                y0 = Math.Min(y0, b.Y0);
                x1 = Math.Max(x1, b.X1);
                y1 = Math.Max(y1, b.Y1);
            }
            if (!any) return null;
            return new BBoxN { X0 = Clamp01(x0), Y0 = Clamp01(y0), X1 = Clamp01(x1), Y1 = Clamp01(y1) };
        }

        public static string SafeSnippet(string text, int start, int length, int maxLen = 160)
        {
            if (string.IsNullOrEmpty(text)) return "";
            start = Math.Max(0, Math.Min(start, text.Length));
            length = Math.Max(0, Math.Min(length, text.Length - start));
            var snippet = text.Substring(start, length);
            snippet = NormalizeWhitespace(snippet);
            if (snippet.Length > maxLen)
                snippet = snippet.Substring(0, maxLen);
            return snippet;
        }

        public static string NormalizeCpf(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            return digits;
        }

        public static bool TryParseMoney(string raw, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var cleaned = raw.Trim();
            cleaned = Regex.Replace(cleaned, @"^\s*R\s*\$?\s*", "", RegexOptions.IgnoreCase);
            cleaned = cleaned.Replace(".", "");
            cleaned = cleaned.Replace(",", ".");
            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        public static string NormalizeMoney(string raw)
        {
            if (!TryParseMoney(raw, out var val)) return "";
            return $"R$ {val:N2}".Replace(",", "X").Replace(".", ",").Replace("X", ".");
        }

        public static bool TryParseDate(string raw, out string iso)
        {
            iso = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var r = raw.Trim();
            string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy", "dd-MM-yyyy", "d-M-yyyy", "dd-MM-yy", "d-M-yy" };
            if (DateTime.TryParseExact(r, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                iso = dt.ToString("yyyy-MM-dd");
                return true;
            }
            var m = Regex.Match(r, @"\b(\d{1,2})\s+de\s+([A-Za-z]+)\s+de\s+(\d{4})\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var day = int.Parse(m.Groups[1].Value);
                var month = MonthFromPortuguese(m.Groups[2].Value);
                var year = int.Parse(m.Groups[3].Value);
                if (month >= 1 && month <= 12)
                {
                    var dt2 = new DateTime(year, month, day);
                    iso = dt2.ToString("yyyy-MM-dd");
                    return true;
                }
            }
            return false;
        }

        public static int MonthFromPortuguese(string month)
        {
            var m = RemoveDiacritics(month).ToLowerInvariant();
            return m switch
            {
                "janeiro" => 1,
                "fevereiro" => 2,
                "marco" => 3,
                "abril" => 4,
                "maio" => 5,
                "junho" => 6,
                "julho" => 7,
                "agosto" => 8,
                "setembro" => 9,
                "outubro" => 10,
                "novembro" => 11,
                "dezembro" => 12,
                _ => 0
            };
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}
