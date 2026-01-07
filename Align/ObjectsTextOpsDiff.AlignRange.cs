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
        private static string NormalizeForSimilarity(string text)
        {
            text = CollapseSpaces(NormalizeBlockToken(text));
            if (string.IsNullOrEmpty(text))
                return "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsDigit(c))
                    sb.Append('#');
                else
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static double ComputeSimilarity(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0)
                return 1.0;
            if (a.Length == 0 || b.Length == 0)
                return 0.0;

            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(a, b, false);
            var dist = dmp.diff_levenshtein(diffs);
            var maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0)
                return 0.0;
            var textSim = 1.0 - (double)dist / maxLen;
            var lenSim = 1.0 - (double)Math.Abs(a.Length - b.Length) / maxLen;
            return (textSim * 0.7) + (lenSim * 0.3);
        }

        private static void AlignBlocks(string aPath, string bPath, int objId, HashSet<string> opFilter, bool useLargestContents, int contentsPage, IReadOnlyList<string> headerLabels)
        {
            using var docA = new PdfDocument(new PdfReader(aPath));
            using var docB = new PdfDocument(new PdfReader(bPath));

            int pageA = contentsPage;
            int pageB = contentsPage;
            bool fallbackA = false;
            bool fallbackB = false;
            if (useLargestContents)
            {
                pageA = DespachoContentsDetector.ResolveContentsPageForDoc(docA, contentsPage, headerLabels, out fallbackA);
                pageB = DespachoContentsDetector.ResolveContentsPageForDoc(docB, contentsPage, headerLabels, out fallbackB);
                if (pageA <= 0)
                {
                    Console.WriteLine($"Contents nao encontrado para despacho em: {aPath}");
                    return;
                }
                if (pageB <= 0)
                {
                    Console.WriteLine($"Contents nao encontrado para despacho em: {bPath}");
                    return;
                }
            }
            var foundA = useLargestContents
                ? DespachoContentsDetector.FindLargestContentsStreamByPage(docA, pageA, headerLabels, contentsPage <= 0 && !fallbackA)
                : FindStreamAndResourcesByObjId(docA, objId);
            var foundB = useLargestContents
                ? DespachoContentsDetector.FindLargestContentsStreamByPage(docB, pageB, headerLabels, contentsPage <= 0 && !fallbackB)
                : FindStreamAndResourcesByObjId(docB, objId);
            if (foundA.Stream == null || foundA.Resources == null)
            {
                Console.WriteLine(useLargestContents
                    ? $"Contents nao encontrado na pagina {pageA}: {aPath}"
                    : $"Objeto {objId} nao encontrado em: {aPath}");
                return;
            }
            if (foundB.Stream == null || foundB.Resources == null)
            {
                Console.WriteLine(useLargestContents
                    ? $"Contents nao encontrado na pagina {pageB}: {bPath}"
                    : $"Objeto {objId} nao encontrado em: {bPath}");
                return;
            }

            var blocksA = ExtractSelfBlocks(foundA.Stream, foundA.Resources, opFilter);
            var blocksB = ExtractSelfBlocks(foundB.Stream, foundB.Resources, opFilter);
            if (blocksA.Count == 0 || blocksB.Count == 0)
            {
                Console.WriteLine("Nenhum bloco encontrado para alinhar.");
                return;
            }

            var normA = blocksA.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();
            var normB = blocksB.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();

            int n = blocksA.Count;
            int m = blocksB.Count;
            const double gap = -0.35;
            const double minScore = 0.30;

            var dp = new double[n + 1, m + 1];
            var move = new byte[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                dp[i, 0] = dp[i - 1, 0] + gap;
                move[i, 0] = 1;
            }
            for (int j = 1; j <= m; j++)
            {
                dp[0, j] = dp[0, j - 1] + gap;
                move[0, j] = 2;
            }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    var sim = ComputeSimilarity(normA[i - 1], normB[j - 1]);
                    var scoreDiag = dp[i - 1, j - 1] + sim;
                    var scoreUp = dp[i - 1, j] + gap;
                    var scoreLeft = dp[i, j - 1] + gap;

                    if (scoreDiag >= scoreUp && scoreDiag >= scoreLeft)
                    {
                        dp[i, j] = scoreDiag;
                        move[i, j] = 0;
                    }
                    else if (scoreUp >= scoreLeft)
                    {
                        dp[i, j] = scoreUp;
                        move[i, j] = 1;
                    }
                    else
                    {
                        dp[i, j] = scoreLeft;
                        move[i, j] = 2;
                    }
                }
            }

            var alignments = new List<(int ai, int bi, double score)>();
            int x = n;
            int y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && move[x, y] == 0)
                {
                    var sim = ComputeSimilarity(normA[x - 1], normB[y - 1]);
                    alignments.Add((x - 1, y - 1, sim));
                    x--;
                    y--;
                }
                else if (x > 0 && (y == 0 || move[x, y] == 1))
                {
                    alignments.Add((x - 1, -1, 0));
                    x--;
                }
                else
                {
                    alignments.Add((-1, y - 1, 0));
                    y--;
                }
            }
            alignments.Reverse();

            var nameA = Path.GetFileName(aPath);
            var nameB = Path.GetFileName(bPath);
            Console.WriteLine($"OBJ - ALIGN (blocos por ordem/tamanho/texto)");
            Console.WriteLine($"{nameA} <-> {nameB}");
            Console.WriteLine();

            foreach (var (ai, bi, score) in alignments)
            {
                if (ai >= 0 && bi >= 0 && score < minScore)
                    continue;

                if (ai >= 0 && bi >= 0)
                {
                    var a = blocksA[ai];
                    var b = blocksB[bi];
                    var textA = CollapseSpaces(NormalizeBlockToken(a.Text ?? ""));
                    var textB = CollapseSpaces(NormalizeBlockToken(b.Text ?? ""));
                    var aRange = a.StartOp == a.EndOp ? $"{a.StartOp}" : $"{a.StartOp}-{a.EndOp}";
                    var bRange = b.StartOp == b.EndOp ? $"{b.StartOp}" : $"{b.StartOp}-{b.EndOp}";
                    Console.WriteLine($"score={score:F2}  {nameA} op{aRange}  \"{EscapeBlockText(textA)}\"");
                    Console.WriteLine($"           {nameB} op{bRange}  \"{EscapeBlockText(textB)}\"");
                    PrintFixedVarSegments(textA, textB);
                    Console.WriteLine();
                }
                else if (ai >= 0)
                {
                    var a = blocksA[ai];
                    var textA = CollapseSpaces(NormalizeBlockToken(a.Text ?? ""));
                    var aRange = a.StartOp == a.EndOp ? $"{a.StartOp}" : $"{a.StartOp}-{a.EndOp}";
                    Console.WriteLine($"score=--   {nameA} op{aRange}  \"{EscapeBlockText(textA)}\"");
                    Console.WriteLine($"           {nameB} (sem equivalente)");
                    Console.WriteLine();
                }
                else if (bi >= 0)
                {
                    var b = blocksB[bi];
                    var textB = CollapseSpaces(NormalizeBlockToken(b.Text ?? ""));
                    var bRange = b.StartOp == b.EndOp ? $"{b.StartOp}" : $"{b.StartOp}-{b.EndOp}";
                    Console.WriteLine($"score=--   {nameA} (sem equivalente)");
                    Console.WriteLine($"           {nameB} op{bRange}  \"{EscapeBlockText(textB)}\"");
                    Console.WriteLine();
                }
            }
        }

        internal sealed class AlignRangeValue
        {
            public int Page { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string ValueFull { get; set; } = "";
        }

        internal sealed class AlignRangeResult
        {
            public AlignRangeResult(AlignRangeValue frontA, AlignRangeValue frontB, AlignRangeValue backA, AlignRangeValue backB)
            {
                FrontA = frontA;
                FrontB = frontB;
                BackA = backA;
                BackB = backB;
            }

            public AlignRangeValue FrontA { get; }
            public AlignRangeValue FrontB { get; }
            public AlignRangeValue BackA { get; }
            public AlignRangeValue BackB { get; }
        }

        internal static AlignRangeResult? ComputeAlignRanges(string aPath, string bPath, HashSet<string> opFilter, int contentsPage, int backoff)
        {
            if (string.IsNullOrWhiteSpace(aPath) || string.IsNullOrWhiteSpace(bPath))
                return null;

            using var docA = new PdfDocument(new PdfReader(aPath));
            using var docB = new PdfDocument(new PdfReader(bPath));

            var nameA = Path.GetFileName(aPath);
            var nameB = Path.GetFileName(bPath);

            var headerLabels = DespachoContentsDetector.GetDespachoHeaderLabels("", 0);
            int pageA = DespachoContentsDetector.ResolveContentsPageForDoc(docA, contentsPage, headerLabels, out var fallbackA);
            int pageB = DespachoContentsDetector.ResolveContentsPageForDoc(docB, contentsPage, headerLabels, out var fallbackB);
            if (pageA <= 0)
            {
                Console.WriteLine($"Contents nao encontrado para despacho em: {nameA}");
                return null;
            }
            if (pageB <= 0)
            {
                Console.WriteLine($"Contents nao encontrado para despacho em: {nameB}");
                return null;
            }

            var requireMarker = contentsPage <= 0 && !fallbackA && !fallbackB;
            var front = BuildAlignRangeForPage(docA, docB, pageA, pageB, opFilter, backoff, nameA, nameB, "front_head", headerLabels, requireMarker);

            int backPageA = pageA + 1;
            int backPageB = pageB + 1;

            AlignRangeValue backA = new AlignRangeValue { Page = backPageA };
            AlignRangeValue backB = new AlignRangeValue { Page = backPageB };

            if (backPageA <= docA.GetNumberOfPages() && backPageB <= docB.GetNumberOfPages())
            {
                var back = BuildAlignRangeForPage(docA, docB, backPageA, backPageB, opFilter, backoff, nameA, nameB, "back_tail", headerLabels, requireMarker);
                backA = back.A;
                backB = back.B;
            }
            else
            {
                if (backPageA > docA.GetNumberOfPages())
                    Console.WriteLine($"Sem back_tail: pagina {backPageA} nao existe em {nameA}.");
                if (backPageB > docB.GetNumberOfPages())
                    Console.WriteLine($"Sem back_tail: pagina {backPageB} nao existe em {nameB}.");
            }

            return new AlignRangeResult(front.A, front.B, backA, backB);
        }

        private sealed class BlockAlignment
        {
            public BlockAlignment(int aIndex, int bIndex, double score)
            {
                AIndex = aIndex;
                BIndex = bIndex;
                Score = score;
            }

            public int AIndex { get; }
            public int BIndex { get; }
            public double Score { get; }
        }

        private struct VariableRange
        {
            public bool HasValue;
            public int FirstStartOp;
            public int LastEndOp;
        }

        private static (AlignRangeValue A, AlignRangeValue B) BuildAlignRangeForPage(
            PdfDocument docA,
            PdfDocument docB,
            int pageA,
            int pageB,
            HashSet<string> opFilter,
            int backoff,
            string nameA,
            string nameB,
            string label,
            IReadOnlyList<string> headerLabels,
            bool requireMarker)
        {
            var resultA = new AlignRangeValue { Page = pageA };
            var resultB = new AlignRangeValue { Page = pageB };

            if (pageA < 1 || pageA > docA.GetNumberOfPages())
            {
                Console.WriteLine($"Pagina invalida ({label}) em {nameA}: {pageA}");
                return (resultA, resultB);
            }
            if (pageB < 1 || pageB > docB.GetNumberOfPages())
            {
                Console.WriteLine($"Pagina invalida ({label}) em {nameB}: {pageB}");
                return (resultA, resultB);
            }

            var foundA = DespachoContentsDetector.FindLargestContentsStreamByPage(docA, pageA, headerLabels, requireMarker);
            var foundB = DespachoContentsDetector.FindLargestContentsStreamByPage(docB, pageB, headerLabels, requireMarker);

            if (foundA.Stream == null || foundA.Resources == null)
            {
                Console.WriteLine($"Contents nao encontrado na pagina {pageA} ({label}) em {nameA}");
                return (resultA, resultB);
            }
            if (foundB.Stream == null || foundB.Resources == null)
            {
                Console.WriteLine($"Contents nao encontrado na pagina {pageB} ({label}) em {nameB}");
                return (resultA, resultB);
            }

            var blocksA = ExtractSelfBlocks(foundA.Stream, foundA.Resources, opFilter);
            var blocksB = ExtractSelfBlocks(foundB.Stream, foundB.Resources, opFilter);
            if (blocksA.Count == 0 || blocksB.Count == 0)
            {
                Console.WriteLine($"Nenhum bloco encontrado para alinhar ({label}).");
                return (resultA, resultB);
            }

            var alignments = BuildBlockAlignments(blocksA, blocksB, out var normA, out var normB);

            var rangeA = new VariableRange();
            var rangeB = new VariableRange();
            ApplyAlignmentToRanges(alignments, blocksA, blocksB, normA, normB, ref rangeA, ref rangeB);

            if (!rangeA.HasValue)
                rangeA = FallbackRange(blocksA);
            if (!rangeB.HasValue)
                rangeB = FallbackRange(blocksB);

            var (startA, endA) = ApplyBackoff(rangeA, backoff);
            var (startB, endB) = ApplyBackoff(rangeB, backoff);

            resultA.StartOp = startA;
            resultA.EndOp = endA;
            resultA.ValueFull = ExtractValueFull(foundA.Stream, foundA.Resources, opFilter, startA, endA);

            resultB.StartOp = startB;
            resultB.EndOp = endB;
            resultB.ValueFull = ExtractValueFull(foundB.Stream, foundB.Resources, opFilter, startB, endB);

            return (resultA, resultB);
        }

        private static List<BlockAlignment> BuildBlockAlignments(
            List<SelfBlock> blocksA,
            List<SelfBlock> blocksB,
            out List<string> normA,
            out List<string> normB)
        {
            normA = blocksA.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();
            normB = blocksB.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();

            int n = blocksA.Count;
            int m = blocksB.Count;
            const double gap = -0.35;

            var dp = new double[n + 1, m + 1];
            var move = new byte[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                dp[i, 0] = dp[i - 1, 0] + gap;
                move[i, 0] = 1;
            }
            for (int j = 1; j <= m; j++)
            {
                dp[0, j] = dp[0, j - 1] + gap;
                move[0, j] = 2;
            }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    var sim = ComputeSimilarity(normA[i - 1], normB[j - 1]);
                    var scoreDiag = dp[i - 1, j - 1] + sim;
                    var scoreUp = dp[i - 1, j] + gap;
                    var scoreLeft = dp[i, j - 1] + gap;

                    if (scoreDiag >= scoreUp && scoreDiag >= scoreLeft)
                    {
                        dp[i, j] = scoreDiag;
                        move[i, j] = 0;
                    }
                    else if (scoreUp >= scoreLeft)
                    {
                        dp[i, j] = scoreUp;
                        move[i, j] = 1;
                    }
                    else
                    {
                        dp[i, j] = scoreLeft;
                        move[i, j] = 2;
                    }
                }
            }

            var alignments = new List<BlockAlignment>();
            int x = n;
            int y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && move[x, y] == 0)
                {
                    var sim = ComputeSimilarity(normA[x - 1], normB[y - 1]);
                    alignments.Add(new BlockAlignment(x - 1, y - 1, sim));
                    x--;
                    y--;
                }
                else if (x > 0 && (y == 0 || move[x, y] == 1))
                {
                    alignments.Add(new BlockAlignment(x - 1, -1, 0));
                    x--;
                }
                else
                {
                    alignments.Add(new BlockAlignment(-1, y - 1, 0));
                    y--;
                }
            }
            alignments.Reverse();
            return alignments;
        }

        private static void ApplyAlignmentToRanges(
            List<BlockAlignment> alignments,
            List<SelfBlock> blocksA,
            List<SelfBlock> blocksB,
            List<string> normA,
            List<string> normB,
            ref VariableRange rangeA,
            ref VariableRange rangeB)
        {
            foreach (var align in alignments)
            {
                if (align.AIndex >= 0 && align.BIndex >= 0)
                {
                    bool diff = !string.Equals(normA[align.AIndex], normB[align.BIndex], StringComparison.Ordinal);
                    if (diff)
                    {
                        AddBlockToRange(ref rangeA, blocksA[align.AIndex]);
                        AddBlockToRange(ref rangeB, blocksB[align.BIndex]);
                    }
                }
                else if (align.AIndex >= 0)
                {
                    AddBlockToRange(ref rangeA, blocksA[align.AIndex]);
                }
                else if (align.BIndex >= 0)
                {
                    AddBlockToRange(ref rangeB, blocksB[align.BIndex]);
                }
            }
        }

        private static void AddBlockToRange(ref VariableRange range, SelfBlock block)
        {
            if (!range.HasValue)
            {
                range.HasValue = true;
                range.FirstStartOp = block.StartOp;
                range.LastEndOp = block.EndOp;
                return;
            }
            range.LastEndOp = block.EndOp;
        }

        private static VariableRange FallbackRange(List<SelfBlock> blocks)
        {
            if (blocks.Count == 0)
                return new VariableRange();
            return new VariableRange
            {
                HasValue = true,
                FirstStartOp = blocks[0].StartOp,
                LastEndOp = blocks[^1].EndOp
            };
        }

        private static (int Start, int End) ApplyBackoff(VariableRange range, int backoff)
        {
            if (!range.HasValue)
                return (0, 0);
            int start = range.FirstStartOp - Math.Max(0, backoff);
            if (start < 1) start = 1;
            int end = range.LastEndOp;
            if (end < start) end = start;
            return (start, end);
        }

        private static string ExtractValueFull(PdfStream stream, PdfResources resources, HashSet<string> opFilter, int startOp, int endOp)
        {
            if (stream == null || resources == null || startOp <= 0 || endOp <= 0)
                return "";

            var full = ExtractFullTextWithOps(stream, resources, opFilter,
                includeLineBreaks: true,
                includeTdLineBreaks: true,
                includeTmLineBreaks: true,
                lineBreakAsSpace: true);

            var sliced = SliceFullTextByOpRange(full, startOp, endOp);
            var text = sliced.Text ?? "";
            text = NormalizeBlockToken(text);
            return CollapseSpaces(text);
        }

        private static void PrintFixedVarSegments(string textA, string textB)
        {
            if (string.IsNullOrWhiteSpace(textA) && string.IsNullOrWhiteSpace(textB))
                return;

            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(textA ?? "", textB ?? "", false);
            dmp.diff_cleanupSemantic(diffs);

            var varA = new StringBuilder();
            var varB = new StringBuilder();
            const int minFixedLen = 2;

            void FlushVar()
            {
                if (varA.Length == 0 && varB.Length == 0)
                    return;

                var a = varA.ToString().Trim();
                var b = varB.ToString().Trim();
                if (a.Length == 0 && b.Length == 0)
                {
                    varA.Clear();
                    varB.Clear();
                    return;
                }

                Console.WriteLine($"  VAR A: \"{EscapeBlockText(a)}\"");
                Console.WriteLine($"  VAR B: \"{EscapeBlockText(b)}\"");
                varA.Clear();
                varB.Clear();
            }

            foreach (var diff in diffs)
            {
                if (diff.operation == Operation.EQUAL)
                {
                    FlushVar();
                    var fixedText = diff.text.Trim();
                    if (fixedText.Length >= minFixedLen)
                        Console.WriteLine($"  FIXO: \"{EscapeBlockText(fixedText)}\"");
                    continue;
                }
                if (diff.operation == Operation.DELETE)
                {
                    varA.Append(diff.text);
                    continue;
                }
                if (diff.operation == Operation.INSERT)
                {
                    varB.Append(diff.text);
                }
            }

            FlushVar();
        }

        private static int GetBlockMaxTokenLen(VarBlockSlots block, List<string> baseTokens, List<List<string>> tokenLists, List<TokenAlignment> alignments)
        {
            int maxLen = 0;
            int pdfCount = tokenLists.Count;
            for (int i = 0; i < pdfCount; i++)
            {
                maxLen = Math.Max(maxLen, GetBlockMaxTokenLenForPdf(block, i, baseTokens, tokenLists, alignments));
            }
            return maxLen;
        }

        private static int GetBlockMaxTokenLenForPdf(VarBlockSlots block, int pdfIndex, List<string> baseTokens, List<List<string>> tokenLists, List<TokenAlignment> alignments)
        {
            int maxLen = 0;
            var maxSlot = block.EndSlot;
            for (int slot = block.StartSlot; slot <= maxSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    int gap = slot / 2;
                    if (pdfIndex == 0) continue;
                    var alignment = alignments[pdfIndex - 1];
                    var otherTokens = tokenLists[pdfIndex];
                    foreach (var tokenIdx in alignment.Insertions[gap])
                    {
                        if (tokenIdx >= 0 && tokenIdx < otherTokens.Count)
                            maxLen = Math.Max(maxLen, otherTokens[tokenIdx].Length);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= baseTokens.Count)
                        continue;
                    if (pdfIndex == 0)
                    {
                        maxLen = Math.Max(maxLen, baseTokens[tokenIdx].Length);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherTokens = tokenLists[pdfIndex];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < otherTokens.Count)
                            maxLen = Math.Max(maxLen, otherTokens[otherIdx].Length);
                    }
                }
            }
            return maxLen;
        }

        private static string BuildBlockOpLabel(VarBlockSlots block, int pdfIndex, List<List<int>> tokenOpStartLists, List<List<int>> tokenOpEndLists, List<List<string>> tokenOpNames, List<TokenAlignment> alignments)
        {
            int minOp = int.MaxValue;
            int maxOp = -1;
            var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddOpRange(int startOp, int endOp, string opName)
            {
                if (startOp <= 0 && endOp <= 0) return;
                if (startOp > 0)
                {
                    minOp = Math.Min(minOp, startOp);
                    maxOp = Math.Max(maxOp, startOp);
                }
                if (endOp > 0)
                {
                    minOp = Math.Min(minOp, endOp);
                    maxOp = Math.Max(maxOp, endOp);
                }
                if (!string.IsNullOrWhiteSpace(opName))
                    ops.Add(opName);
            }

            var maxSlot = block.EndSlot;
            for (int slot = block.StartSlot; slot <= maxSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    if (pdfIndex == 0)
                        continue;
                    int gap = slot / 2;
                    var alignment = alignments[pdfIndex - 1];
                    var otherOpStarts = tokenOpStartLists[pdfIndex];
                    var otherOpEnds = tokenOpEndLists[pdfIndex];
                    var otherNames = tokenOpNames[pdfIndex];
                    foreach (var tokenIdx in alignment.Insertions[gap])
                    {
                        if (tokenIdx >= 0 && tokenIdx < otherOpStarts.Count)
                            AddOpRange(otherOpStarts[tokenIdx], tokenIdx < otherOpEnds.Count ? otherOpEnds[tokenIdx] : otherOpStarts[tokenIdx], otherNames[tokenIdx]);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= tokenOpStartLists[0].Count)
                        continue;

                    if (pdfIndex == 0)
                    {
                        var baseStart = tokenOpStartLists[0][tokenIdx];
                        var baseEnd = tokenIdx < tokenOpEndLists[0].Count ? tokenOpEndLists[0][tokenIdx] : baseStart;
                        AddOpRange(baseStart, baseEnd, tokenOpNames[0][tokenIdx]);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < tokenOpStartLists[pdfIndex].Count)
                        {
                            var otherStart = tokenOpStartLists[pdfIndex][otherIdx];
                            var otherEnd = otherIdx < tokenOpEndLists[pdfIndex].Count ? tokenOpEndLists[pdfIndex][otherIdx] : otherStart;
                            AddOpRange(otherStart, otherEnd, tokenOpNames[pdfIndex][otherIdx]);
                        }
                    }
                }
            }

            if (maxOp < 0)
                return "op?";

            string range = minOp == maxOp ? $"op{minOp}" : $"op{minOp}-{maxOp}";
            if (ops.Count > 0)
            {
                var label = string.Join("/", ops.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
                range += $"[{label}]";
            }

            return range;
        }

        private static string EscapeBlockText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t")
                .Replace("\"", "\\\"");
        }

        private static void PrintFullTextDiffWithRange(
            List<string> inputs,
            List<FullTextOpsResult> fullResults,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency,
            string rangeStartRegex,
            string rangeEndRegex,
            int? rangeStartOp,
            int? rangeEndOp,
            bool dumpRangeText,
            string roiDoc,
            int objId,
            bool showEqual)
        {
            if (inputs.Count < 2)
                return;

            var startOps = new List<int>();
            var endOps = new List<int>();
            var rangesPerFile = new List<(string Name, int Start, int End)>();
            var hasExplicitRange = rangeStartOp.HasValue
                || rangeEndOp.HasValue
                || !string.IsNullOrWhiteSpace(rangeStartRegex)
                || !string.IsNullOrWhiteSpace(rangeEndRegex);
            TextOpsRoiFile? roi = null;
            string roiPath = "";
            if (!hasExplicitRange)
            {
                roiPath = ResolveRoiPath(roiDoc, objId);
                if (string.IsNullOrWhiteSpace(roiPath))
                    roiPath = ResolveAnyRoiPath(objId);
                roi = LoadRoi(roiPath);
            }

            for (int i = 0; i < inputs.Count; i++)
            {
                var name = Path.GetFileName(inputs[i]);
                var full = fullResults[i];

                if (!hasExplicitRange && roi != null)
                {
                    if (!TryResolveRangeFromRoi(roi, name, out var startRoi, out var endRoi, out var reasonRoi))
                    {
                        Console.WriteLine($"Range invalido para {name}: {reasonRoi}");
                        return;
                    }
                    startOps.Add(startRoi);
                    endOps.Add(endRoi);
                    rangesPerFile.Add((name, startRoi, endRoi));
                    continue;
                }

                if (!TryResolveRange(full, rangeStartRegex, rangeEndRegex, rangeStartOp, rangeEndOp, out var start, out var end, out var reason))
                {
                    Console.WriteLine($"Range invalido para {name}: {reason}");
                    return;
                }

                startOps.Add(start);
                endOps.Add(end);
                rangesPerFile.Add((name, start, end));
            }

            int globalStart = startOps.Min();
            int globalEnd = endOps.Max();
            if (globalEnd < globalStart)
            {
                Console.WriteLine("Range global invalido (fim < inicio).");
                return;
            }

            Console.WriteLine("OBJ - DIFF FULLTEXT (texto completo do objeto)");
            Console.WriteLine("Range por arquivo:");
            foreach (var r in rangesPerFile)
                Console.WriteLine($"  {r.Name}: op{r.Start}-op{r.End}");
            Console.WriteLine($"Range final (min/max): op{globalStart}-op{globalEnd}");
            if (!hasExplicitRange && roi != null && !string.IsNullOrWhiteSpace(roiPath))
                Console.WriteLine($"ROI: {roiPath}");
            Console.WriteLine();

            var slicedTexts = new List<string>();
            var slicedOps = new List<List<int>>();
            var slicedOpNames = new List<List<string>>();

            for (int i = 0; i < fullResults.Count; i++)
            {
                var range = rangesPerFile[i];
                var sliced = SliceFullTextByOpRange(fullResults[i], range.Start, range.End);
                slicedTexts.Add(sliced.Text);
                slicedOps.Add(sliced.OpIndexes);
                slicedOpNames.Add(sliced.OpNames);
            }

            if (dumpRangeText)
            {
                Console.WriteLine("Range text por arquivo:");
                for (int i = 0; i < inputs.Count; i++)
                {
                    var name = Path.GetFileName(inputs[i]);
                    var text = slicedTexts[i];
                    var display = EscapeBlockText(text);
                    Console.WriteLine($"  {name}: \"{display}\" (len={text.Length})");
                }
                Console.WriteLine();
            }

            PrintFullTextDiff(inputs, slicedTexts, slicedOps, slicedOpNames, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency, showEqual);
        }

        private static bool TryResolveRange(
            FullTextOpsResult full,
            string rangeStartRegex,
            string rangeEndRegex,
            int? rangeStartOp,
            int? rangeEndOp,
            out int startOp,
            out int endOp,
            out string reason)
        {
            startOp = 0;
            endOp = 0;
            reason = "";

            if (rangeStartOp.HasValue)
                startOp = rangeStartOp.Value;
            else if (!string.IsNullOrWhiteSpace(rangeStartRegex))
            {
                if (!TryFindOpByRegex(full, rangeStartRegex, true, out startOp))
                {
                    reason = $"range-start regex nao encontrado: {rangeStartRegex}";
                    return false;
                }
            }
            else
            {
                reason = "range-start nao definido";
                return false;
            }

            if (rangeEndOp.HasValue)
                endOp = rangeEndOp.Value;
            else if (!string.IsNullOrWhiteSpace(rangeEndRegex))
            {
                if (!TryFindOpByRegex(full, rangeEndRegex, false, out endOp))
                {
                    reason = $"range-end regex nao encontrado: {rangeEndRegex}";
                    return false;
                }
            }
            else
            {
                reason = "range-end nao definido";
                return false;
            }

            return true;
        }

        private static bool TryResolveRangeFromRoi(TextOpsRoiFile roi, string fileName, out int startOp, out int endOp, out string reason)
        {
            startOp = 0;
            endOp = 0;
            reason = "";

            if (roi == null)
            {
                reason = "ROI nao carregado";
                return false;
            }

            var range = FindRoiRange(roi.FrontHead, fileName) ?? FindRoiRange(roi.BackTail, fileName);
            if (range == null)
            {
                reason = "ROI nao encontrado para o arquivo";
                return false;
            }

            startOp = range.StartOp;
            endOp = range.EndOp;
            if (startOp <= 0 || endOp <= 0)
            {
                reason = "ROI invalido (start/end <= 0)";
                return false;
            }

            return true;
        }

        private static TextOpsRoiRange? FindRoiRange(TextOpsRoiSection? section, string fileName)
        {
            if (section == null || section.Ranges.Count == 0)
                return null;

            var name = Path.GetFileName(fileName ?? "");
            var match = section.Ranges.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.SourceFile) &&
                string.Equals(r.SourceFile, name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            match = section.Ranges.FirstOrDefault(r =>
                string.IsNullOrWhiteSpace(r.SourceFile) ||
                r.SourceFile == "*" ||
                string.Equals(r.SourceFile, "default", StringComparison.OrdinalIgnoreCase));

            return match;
        }

        private static bool TryFindOpByRegex(FullTextOpsResult full, string pattern, bool first, out int opIndex)
        {
            opIndex = 0;
            if (string.IsNullOrWhiteSpace(pattern))
                return false;

            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var matches = regex.Matches(full.Text ?? "");
                if (matches.Count == 0)
                    return false;

                var match = first ? matches[0] : matches[^1];
                if (match.Length == 0)
                    return false;

                return TryGetOpFromSpan(full.OpIndexes, match.Index, match.Length, first, out opIndex);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetOpFromSpan(List<int> opIndexes, int start, int length, bool first, out int op)
        {
            op = 0;
            if (opIndexes == null || opIndexes.Count == 0)
                return false;
            int end = Math.Min(opIndexes.Count, start + length);
            if (start < 0 || start >= end)
                return false;

            if (first)
            {
                for (int i = start; i < end; i++)
                {
                    if (opIndexes[i] > 0)
                    {
                        op = opIndexes[i];
                        return true;
                    }
                }
            }
            else
            {
                for (int i = end - 1; i >= start; i--)
                {
                    if (opIndexes[i] > 0)
                    {
                        op = opIndexes[i];
                        return true;
                    }
                }
            }

            return false;
        }

        private static FullTextOpsResult SliceFullTextByOpRange(FullTextOpsResult full, int startOp, int endOp)
        {
            if (startOp < 1) startOp = 1;
            if (endOp < startOp) endOp = startOp;

            var sb = new StringBuilder();
            var ops = new List<int>();
            var opNames = new List<string>();

            for (int i = 0; i < full.Text.Length && i < full.OpIndexes.Count && i < full.OpNames.Count; i++)
            {
                var op = full.OpIndexes[i];
                if (op >= startOp && op <= endOp)
                {
                    sb.Append(full.Text[i]);
                    ops.Add(op);
                    opNames.Add(full.OpNames[i]);
                }
            }

            return new FullTextOpsResult(sb.ToString(), ops, opNames);
        }

        private static void PrintFullTextDiff(
            List<string> inputs,
            List<string> texts,
            List<List<int>> tokenOpLists,
            List<List<string>> tokenOpNames,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency,
            bool showEqual = false)
        {
            if (inputs.Count < 2)
                return;

            var baseName = Path.GetFileName(inputs[0]);
            var baseText = texts.Count > 0 ? texts[0] : "";
            var baseOps = tokenOpLists[0];
            var baseOpNames = tokenOpNames[0];

            for (int i = 1; i < inputs.Count; i++)
            {
                var otherName = Path.GetFileName(inputs[i]);
                var otherText = texts[i];
                var otherOps = tokenOpLists[i];
                var otherOpNames = tokenOpNames[i];

                Console.WriteLine($"[DIFF] {baseName} vs {otherName}");

                var dmp = new diff_match_patch();
                var diffs = dmp.diff_main(baseText, otherText, diffLineMode);
                if (cleanupSemantic) dmp.diff_cleanupSemantic(diffs);
                if (cleanupLossless) dmp.diff_cleanupSemanticLossless(diffs);
                if (cleanupEfficiency) dmp.diff_cleanupEfficiency(diffs);

                int basePos = 0;
                int otherPos = 0;

                foreach (var diff in diffs)
                {
                    var len = diff.text.Length;
                    if (diff.operation == Operation.EQUAL)
                    {
                        if (showEqual && len > 0)
                        {
                            var label = BuildOpRangeLabel(baseOps, baseOpNames, basePos, len);
                            var display = EscapeBlockText(diff.text);
                            Console.WriteLine($"EQ  {label}\t{baseName}\t\"{display}\" (len={len})");
                        }
                        basePos += len;
                        otherPos += len;
                        continue;
                    }

                    if (diff.operation == Operation.DELETE)
                    {
                        var label = BuildOpRangeLabel(baseOps, baseOpNames, basePos, len);
                        var display = EscapeBlockText(diff.text);
                        Console.WriteLine($"DEL {label}\t{baseName}\t\"{display}\" (len={len})");
                        basePos += len;
                        continue;
                    }

                    if (diff.operation == Operation.INSERT)
                    {
                        var label = BuildOpRangeLabel(otherOps, otherOpNames, otherPos, len);
                        var display = EscapeBlockText(diff.text);
                        Console.WriteLine($"INS {label}\t{otherName}\t\"{display}\" (len={len})");
                        otherPos += len;
                        continue;
                    }
                }

                Console.WriteLine();
            }
        }

        private static string BuildOpRangeLabel(List<int> opIndexes, List<string> opNames, int start, int length)
        {
            if (length <= 0 || start < 0 || start >= opIndexes.Count)
                return "op?";

            int minOp = int.MaxValue;
            int maxOp = -1;
            var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int end = Math.Min(opIndexes.Count, start + length);
            for (int i = start; i < end; i++)
            {
                var op = opIndexes[i];
                if (op <= 0) continue;
                minOp = Math.Min(minOp, op);
                maxOp = Math.Max(maxOp, op);
                if (i < opNames.Count && !string.IsNullOrWhiteSpace(opNames[i]))
                    ops.Add(opNames[i]);
            }

            if (maxOp < 0)
                return "op?";

            string range = minOp == maxOp ? $"op{minOp}" : $"op{minOp}-{maxOp}";
            if (ops.Count > 0)
                range += $"[{string.Join("/", ops.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))}]";
            return range;
        }

        private static string FormatBlockRange(int startSlot, int endSlot)
        {
            int? startOp = null;
            int? endOp = null;
            int? startGap = null;
            int? endGap = null;

            for (int slot = startSlot; slot <= endSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    int gap = slot / 2;
                    startGap ??= gap;
                    endGap = gap;
                }
                else
                {
                    int op = (slot - 1) / 2 + 1;
                    startOp ??= op;
                    endOp = op;
                }
            }

            if (startOp.HasValue)
            {
                if (startOp.Value == endOp)
                    return $"textop {startOp}";
                return $"textops {startOp}-{endOp}";
            }

            if (startGap.HasValue)
            {
                if (startGap.Value == endGap)
                    return $"gap {startGap}";
                return $"gaps {startGap}-{endGap}";
            }

            return $"slots {startSlot}-{endSlot}";
        }

        private sealed class VarBlockSlots
        {
            public VarBlockSlots(int startSlot, int endSlot)
            {
                StartSlot = startSlot;
                EndSlot = endSlot;
            }

            public int StartSlot { get; }
            public int EndSlot { get; }
        }


    }
}
