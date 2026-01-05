using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DiffMatchPatch;
using iText.Kernel.Pdf;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Compara operadores de texto (Tj/TJ/Td/Tf/Tm/BT/ET) entre varios PDFs para um objeto especifico.
    /// Mostra linhas fixas (iguais) e variaveis (mudam).
    /// Uso:
    ///   tjpdf-cli inspect objects textopsvar --inputs a.pdf,b.pdf --obj 6
    ///   tjpdf-cli inspect objects textopsfixed --inputs a.pdf,b.pdf --obj 6
    ///   tjpdf-cli inspect objects textopsdiff --inputs a.pdf,b.pdf --obj 6
    /// </summary>
    internal static class ObjectsTextOpsDiff
    {
        private static readonly object ConsoleLock = new();
        internal enum DiffMode
        {
            Fixed,
            Variations,
            Both
        }

        internal enum TokenMode
        {
            Text,
            Ops
        }

        public static void Execute(string[] args, DiffMode mode)
        {
            if (!ParseOptions(
                    args,
                    out var inputs,
                    out var objId,
                    out var opFilter,
                    out var blocks,
                    out var blocksInline,
                    out var blocksOrder,
                    out var blockRange,
                    out var selfMode,
                    out var selfMinTokenLen,
                    out var selfPatternMax,
                    out var minTokenLenFilter,
                    out var anchorsMinLen,
                    out var anchorsMaxLen,
                    out var anchorsMaxWords,
                    out var selfAnchors,
                    out var rulesPathArg,
                    out var rulesDoc,
                    out var anchorsOut,
                    out var anchorsMerge,
                    out var tokenMode,
                    out var diffLineMode,
                    out var cleanupSemantic,
                    out var cleanupLossless,
                    out var cleanupEfficiency,
                    out var diffFullText,
                    out var includeLineBreaks,
                    out var includeTdLineBreaks,
                    out var rangeStartRegex,
                    out var rangeEndRegex,
                    out var rangeStartOp,
                    out var rangeEndOp,
                    out var dumpRangeText,
                    out var includeTmLineBreaks,
                    out var lineBreakAsSpace))
                return;

            var rulesPath = ResolveRulesPath(rulesPathArg, rulesDoc);
            var rules = LoadRules(rulesPath);
            if ((!string.IsNullOrWhiteSpace(rulesPathArg) || !string.IsNullOrWhiteSpace(rulesDoc))
                && (string.IsNullOrWhiteSpace(rulesPath) || rules == null))
            {
                Console.WriteLine("Regras de textops nao encontradas ou invalidas.");
            }

            if (!selfMode && inputs.Count == 1 && mode != DiffMode.Both)
                selfMode = true;

            if (selfMode)
            {
                if (inputs.Count < 1)
                {
                    Console.WriteLine("Informe ao menos um PDF com --input/--inputs ao usar --self.");
                    return;
                }
            }
            else if (inputs.Count < 2)
            {
                Console.WriteLine("Informe ao menos dois PDFs com --inputs ou --input-dir.");
                return;
            }

            if (objId <= 0)
            {
                Console.WriteLine("Informe --obj <id>.");
                return;
            }

            if (selfMode)
            {
                var results = new List<SelfResult>();
                foreach (var path in inputs)
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"Arquivo nao encontrado: {path}");
                        return;
                    }

                    using var doc = new PdfDocument(new PdfReader(path));
                    var found = FindStreamAndResourcesByObjId(doc, objId);
                    var stream = found.Stream;
                    var resources = found.Resources;
                    if (stream == null || resources == null)
                    {
                        Console.WriteLine($"Objeto {objId} nao encontrado em: {path}");
                        return;
                    }

                    var blocksSelf = ExtractSelfBlocks(stream, resources, opFilter);
                    var classified = ClassifySelfBlocks(blocksSelf, selfMinTokenLen, selfPatternMax, rules);
                    if (mode == DiffMode.Fixed)
                        results.Add(new SelfResult(path, FilterSelfBlocks(classified.Fixed, minTokenLenFilter)));
                    else
                        results.Add(new SelfResult(path, FilterSelfBlocks(classified.Variable, minTokenLenFilter)));
                }

                var selfLabel = mode == DiffMode.Fixed ? "FIXOS" : "VARIAVEIS";
                PrintSelfSummary(results, selfLabel);

                if (selfAnchors)
                {
                    var merged = new Dictionary<string, TextOpsAnchorConcept>(StringComparer.Ordinal);
                    var sources = new List<string>();

                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var path = inputs[i];
                        var blocksSelf = ExtractSelfBlocksForPath(path, objId, opFilter);
                        var classified = ClassifySelfBlocks(blocksSelf, selfMinTokenLen, selfPatternMax, rules);
                        var variableBlocks = FilterSelfBlocks(classified.Variable, minTokenLenFilter);
                        variableBlocks = FilterAnchorBlocks(variableBlocks, anchorsMinLen, anchorsMaxLen, anchorsMaxWords);
                        var anchors = BuildSelfAnchors(blocksSelf, variableBlocks, classified.Fixed);

                        if (anchorsMerge)
                        {
                            sources.Add(Path.GetFileName(path));
                            MergeAnchors(merged, anchors, path);
                        }
                        else
                        {
                            PrintSelfAnchors(path, anchors);
                            if (!string.IsNullOrWhiteSpace(anchorsOut))
                            {
                                var outPath = BuildAnchorsOutPath(anchorsOut, path, objId, i + 1, inputs.Count);
                                WriteAnchorsFile(outPath, rulesDoc, path, objId, anchors);
                            }
                        }
                    }

                    if (anchorsMerge)
                    {
                        var list = merged.Values
                            .OrderByDescending(a => a.Count)
                            .ThenBy(a => a.Prev ?? "", StringComparer.OrdinalIgnoreCase)
                            .ThenBy(a => a.Next ?? "", StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        PrintMergedAnchors(list);
                        if (!string.IsNullOrWhiteSpace(anchorsOut))
                        {
                            var outPath = BuildAnchorsMergeOutPath(anchorsOut, rulesDoc, objId);
                            WriteMergedAnchorsFile(outPath, rulesDoc, objId, sources, list);
                        }
                    }
                    return;
                }

                for (int i = 0; i < results.Count; i++)
                {
                    var result = results[i];
                    PrintSelfVariableBlocks(result, i + 1, blocksInline, blocksOrder, blockRange, selfLabel);
                }
                return;
            }

            var all = new List<List<string>>();
            var tokenLists = new List<List<string>>();
            var tokenOpLists = new List<List<int>>();
            var tokenOpNames = new List<List<string>>();
            var fullResults = new List<FullTextOpsResult>();
            foreach (var path in inputs)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"Arquivo nao encontrado: {path}");
                    return;
                }
                using var doc = new PdfDocument(new PdfReader(path));
                var found = FindStreamAndResourcesByObjId(doc, objId);
                var stream = found.Stream;
                var resources = found.Resources;
                if (stream == null || resources == null)
                {
                    Console.WriteLine($"Objeto {objId} nao encontrado em: {path}");
                    return;
                }
                if (!diffFullText)
                {
                    all.Add(ExtractTextOperatorLines(stream, resources, opFilter, tokenMode));
                    if (blocks && (mode == DiffMode.Variations || mode == DiffMode.Both))
                    {
                        var tokensWithOps = ExtractTextOperatorTokensWithOps(stream, resources, opFilter, tokenMode);
                        tokenLists.Add(tokensWithOps.Tokens);
                        tokenOpLists.Add(tokensWithOps.OpIndexes);
                        tokenOpNames.Add(tokensWithOps.OpNames);
                    }
                }
                else
                {
                    var fullText = ExtractFullTextWithOps(stream, resources, opFilter, includeLineBreaks, includeTdLineBreaks, includeTmLineBreaks, lineBreakAsSpace);
                    fullResults.Add(fullText);
                }
            }

            if (diffFullText)
            {
                if (mode == DiffMode.Variations || mode == DiffMode.Both)
                    PrintFullTextDiffWithRange(inputs, fullResults, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency, rangeStartRegex, rangeEndRegex, rangeStartOp, rangeEndOp, dumpRangeText);
                return;
            }

            var maxLen = all.Max(l => l.Count);
            var fixedLines = new List<(int idx, string line)>();
            var varLines = new List<(int idx, List<string> lines)>();

            for (int i = 0; i < maxLen; i++)
            {
                var col = new List<string>();
                foreach (var list in all)
                {
                    col.Add(i < list.Count ? list[i] : "(missing)");
                }

                bool allSame = col.All(c => c == col[0]);
                bool hasMissing = col.Any(c => c == "(missing)");

                if (allSame && !hasMissing)
                    fixedLines.Add((i + 1, col[0]));
                else
                    varLines.Add((i + 1, col));
            }

            if (mode == DiffMode.Variations || mode == DiffMode.Both)
            {
                if (blocks)
                    PrintVariationBlocks(inputs, tokenLists, tokenOpLists, tokenOpNames, blocksInline, blocksOrder, blockRange, minTokenLenFilter, tokenMode, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency);
                else
                    PrintVariations(inputs, varLines, minTokenLenFilter, tokenMode);
            }
            if (mode == DiffMode.Fixed || mode == DiffMode.Both)
                PrintFixed(fixedLines, minTokenLenFilter, tokenMode);
        }

        private static void PrintVariations(List<string> inputs, List<(int idx, List<string> lines)> varLines, int minTokenLenFilter, TokenMode tokenMode)
        {
            Console.WriteLine("OBJ - LINHAS VARIAVEIS (mudam entre os PDFs)");
            Console.WriteLine($"Total variaveis: {varLines.Count}");
            Console.WriteLine();

            for (int i = 0; i < varLines.Count; i++)
            {
                var (idx, lines) = varLines[i];
                if (minTokenLenFilter > 0)
                {
                    int maxLen = 0;
                    foreach (var line in lines)
                    {
                        if (tokenMode == TokenMode.Text)
                        {
                            var raw = StripDescription(line);
                            var text = ExtractDecodedTextFromLine(raw);
                            if (!string.IsNullOrWhiteSpace(text))
                                maxLen = Math.Max(maxLen, text.Length);
                        }
                        else
                        {
                            var raw = StripDescription(line);
                            var text = ExtractDecodedTextFromLine(raw);
                            if (!string.IsNullOrWhiteSpace(text))
                                maxLen = Math.Max(maxLen, text.Length);
                        }
                    }
                    if (maxLen < minTokenLenFilter)
                        continue;
                }
                Console.WriteLine($"idx {idx}");
                for (int j = 0; j < inputs.Count; j++)
                {
                    var line = lines[j];
                    if (tokenMode == TokenMode.Text)
                    {
                        if (line.Contains("=>"))
                        {
                            Console.WriteLine($"  {Path.GetFileName(inputs[j])}: {line}");
                            continue;
                        }

                        var raw = StripDescription(line);
                        var text = ExtractDecodedTextFromLine(raw);
                        if (!string.IsNullOrWhiteSpace(text))
                            Console.WriteLine($"  {Path.GetFileName(inputs[j])}: {line}  => \"{text}\" (len={text.Length})");
                        else
                            Console.WriteLine($"  {Path.GetFileName(inputs[j])}: {line}");
                    }
                    else
                    {
                        Console.WriteLine($"  {Path.GetFileName(inputs[j])}: {line}");
                    }
                }
                Console.WriteLine();
            }
        }

        private static void PrintFixed(List<(int idx, string line)> fixedLines, int minTokenLenFilter, TokenMode tokenMode)
        {
            Console.WriteLine("OBJ - LINHAS FIXAS (iguais em todos os PDFs)");
            Console.WriteLine($"Total fixas: {fixedLines.Count}");
            Console.WriteLine();
            Console.WriteLine("idx\tlinha");

            foreach (var (idx, line) in fixedLines)
            {
                if (tokenMode == TokenMode.Text)
                {
                    if (line.Contains("=>"))
                    {
                        Console.WriteLine($"{idx}\t{line}");
                        continue;
                    }

                    var raw = StripDescription(line);
                    var text = ExtractDecodedTextFromLine(raw);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (minTokenLenFilter > 0 && text.Length < minTokenLenFilter)
                            continue;
                        Console.WriteLine($"{idx}\t{line}  => \"{text}\" (len={text.Length})");
                    }
                    else
                        Console.WriteLine($"{idx}\t{line}");
                }
                else
                {
                    if (minTokenLenFilter > 0)
                    {
                        var raw = StripDescription(line);
                        var text = ExtractDecodedTextFromLine(raw);
                        if (!string.IsNullOrWhiteSpace(text) && text.Length < minTokenLenFilter)
                            continue;
                    }
                    Console.WriteLine($"{idx}\t{line}");
                }
            }
        }

        private static void PrintVariationBlocks(
            List<string> inputs,
            List<List<string>> tokenLists,
            List<List<int>> tokenOpLists,
            List<List<string>> tokenOpNames,
            bool inline,
            string order,
            (int? Start, int? End) range,
            int minTokenLenFilter,
            TokenMode tokenMode,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency)
        {
            if (tokenLists.Count == 0)
            {
                Console.WriteLine(tokenMode == TokenMode.Text
                    ? "OBJ - BLOCOS VARIAVEIS (texto contiguo)"
                    : "OBJ - BLOCOS VARIAVEIS (ops contiguos)");
                Console.WriteLine("Total blocos: 0");
                Console.WriteLine();
                return;
            }

            var baseTokens = tokenLists[0];
            var alignments = new List<TokenAlignment>();
            for (int i = 1; i < tokenLists.Count; i++)
                alignments.Add(BuildAlignment(baseTokens, tokenLists[i], diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency));

            var varToken = new bool[baseTokens.Count];
            var varGap = new bool[baseTokens.Count + 1];

            for (int i = 0; i < baseTokens.Count; i++)
            {
                foreach (var (alignment, tokens) in alignments.Select((a, idx) => (a, tokenLists[idx + 1])))
                {
                    var otherIdx = alignment.BaseToOther[i];
                    if (otherIdx < 0 || otherIdx >= tokens.Count)
                    {
                        varToken[i] = true;
                        break;
                    }
                    if (!string.Equals(tokens[otherIdx], baseTokens[i], StringComparison.Ordinal))
                    {
                        varToken[i] = true;
                        break;
                    }
                }
            }

            for (int gap = 0; gap < varGap.Length; gap++)
            {
                foreach (var alignment in alignments)
                {
                    if (alignment.Insertions[gap].Count > 0)
                    {
                        varGap[gap] = true;
                        break;
                    }
                }
            }

            var blocks = BuildVariableBlocks(varToken, varGap);

            Console.WriteLine(tokenMode == TokenMode.Text
                ? "OBJ - BLOCOS VARIAVEIS (texto contiguo)"
                : "OBJ - BLOCOS VARIAVEIS (ops contiguos)");
            Console.WriteLine($"Total blocos: {blocks.Count}");
            Console.WriteLine();

            if (range.Start.HasValue || range.End.HasValue)
            {
                var startIdx = range.Start ?? 1;
                var endIdx = range.End ?? startIdx;
                if (startIdx < 1) startIdx = 1;
                if (endIdx < startIdx) endIdx = startIdx;
                if (startIdx > blocks.Count)
                    return;
                if (endIdx > blocks.Count)
                    endIdx = blocks.Count;

                var startBlock = blocks[startIdx - 1];
                var endBlock = blocks[endIdx - 1];
                var merged = new VarBlockSlots(startBlock.StartSlot, endBlock.EndSlot);
                WriteInlineBlock(FormatBlockLabel(startIdx, endIdx), merged, inputs, baseTokens, tokenLists, tokenOpLists, tokenOpNames, alignments, order);
                return;
            }

            if (inline)
            {
                for (int idx = 0; idx < blocks.Count; idx++)
                {
                    var label = FormatBlockLabel(idx + 1, idx + 1);
                    WriteInlineBlock(label, blocks[idx], inputs, baseTokens, tokenLists, tokenOpLists, tokenOpNames, alignments, order);
                }
                return;
            }

            int n = 1;
            foreach (var block in blocks)
            {
                if (minTokenLenFilter > 0)
                {
                    var maxLen = GetBlockMaxTokenLen(block, baseTokens, tokenLists, alignments);
                    if (maxLen < minTokenLenFilter)
                        continue;
                }
                Console.WriteLine($"[block {n}]");

                for (int i = 0; i < inputs.Count; i++)
                {
                    var name = Path.GetFileName(inputs[i]);
                    var text = BuildBlockText(block, i, baseTokens, tokenLists, alignments);
                    if (text.Length == 0)
                        continue;

                    var opLabel = BuildBlockOpLabel(block, i, tokenOpLists, tokenOpNames, alignments);
                    var display = EscapeBlockText(text);
                    Console.WriteLine($"  {name} {opLabel}: \"{display}\" (len={text.Length})");
                }
                Console.WriteLine();
                n++;
            }
        }

        private static void WriteInlineBlock(string label, VarBlockSlots block, List<string> inputs, List<string> baseTokens, List<List<string>> tokenLists, List<List<int>> tokenOpLists, List<List<string>> tokenOpNames, List<TokenAlignment> alignments, string order)
        {
            bool blockFirst = IsBlockFirst(order);
            for (int i = 0; i < inputs.Count; i++)
            {
                var name = Path.GetFileName(inputs[i]);
                var text = BuildBlockText(block, i, baseTokens, tokenLists, alignments);
                if (text.Length == 0)
                    continue;

                var opLabel = BuildBlockOpLabel(block, i, tokenOpLists, tokenOpNames, alignments);
                var labelOut = string.IsNullOrWhiteSpace(opLabel) ? label : opLabel;
                var display = EscapeBlockText(text);
                if (blockFirst)
                    Console.WriteLine($"{labelOut}\t{name}\t\"{display}\" (len={text.Length})");
                else
                    Console.WriteLine($"\"{display}\" (len={text.Length})\t{labelOut}\t{name}");
            }
        }

        private static bool IsBlockFirst(string? order)
        {
            if (string.IsNullOrWhiteSpace(order)) return true;
            order = order.Trim().ToLowerInvariant();
            return order == "block-first" || order == "block-text" || order == "block" || order == "b";
        }

        private static string FormatBlockLabel(int startIdx, int endIdx)
        {
            if (startIdx == endIdx)
                return $"b{startIdx}";
            return $"b{startIdx}-{endIdx}";
        }

        private static string FormatSelfRangeLabel(int startIdx, int endIdx, int startOp, int endOp, string? opsLabel)
        {
            var opRange = startOp == endOp ? $"{startOp}" : $"{startOp}-{endOp}";
            var label = $"op{opRange}";
            if (!string.IsNullOrWhiteSpace(opsLabel))
                label += $"[{opsLabel}]";
            return label;
        }

        private static string FormatSelfBlockLabel(SelfBlock block)
        {
            return FormatSelfRangeLabel(block.Index, block.Index, block.StartOp, block.EndOp, block.OpsLabel);
        }

        private static string BuildBlockText(VarBlockSlots block, int pdfIndex, List<string> baseTokens, List<List<string>> tokenLists, List<TokenAlignment> alignments)
        {
            var sb = new StringBuilder();
            var maxSlot = block.EndSlot;
            for (int slot = block.StartSlot; slot <= maxSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    int gap = slot / 2;
                    if (pdfIndex == 0)
                        continue;
                    var alignment = alignments[pdfIndex - 1];
                    var otherTokens = tokenLists[pdfIndex];
                    foreach (var tokenIdx in alignment.Insertions[gap])
                    {
                        if (tokenIdx >= 0 && tokenIdx < otherTokens.Count)
                            sb.Append(otherTokens[tokenIdx]);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= baseTokens.Count)
                        continue;
                    if (pdfIndex == 0)
                    {
                        sb.Append(baseTokens[tokenIdx]);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherTokens = tokenLists[pdfIndex];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < otherTokens.Count)
                            sb.Append(otherTokens[otherIdx]);
                    }
                }
            }

            return sb.ToString();
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

        private static string BuildBlockOpLabel(VarBlockSlots block, int pdfIndex, List<List<int>> tokenOpLists, List<List<string>> tokenOpNames, List<TokenAlignment> alignments)
        {
            int minOp = int.MaxValue;
            int maxOp = -1;
            var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddOp(int op, string opName)
            {
                if (op <= 0) return;
                minOp = Math.Min(minOp, op);
                maxOp = Math.Max(maxOp, op);
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
                    var otherOps = tokenOpLists[pdfIndex];
                    var otherNames = tokenOpNames[pdfIndex];
                    foreach (var tokenIdx in alignment.Insertions[gap])
                    {
                        if (tokenIdx >= 0 && tokenIdx < otherOps.Count)
                            AddOp(otherOps[tokenIdx], otherNames[tokenIdx]);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= tokenOpLists[0].Count)
                        continue;

                    if (pdfIndex == 0)
                    {
                        AddOp(tokenOpLists[0][tokenIdx], tokenOpNames[0][tokenIdx]);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < tokenOpLists[pdfIndex].Count)
                            AddOp(tokenOpLists[pdfIndex][otherIdx], tokenOpNames[pdfIndex][otherIdx]);
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
            bool dumpRangeText)
        {
            if (inputs.Count < 2)
                return;

            var startOps = new List<int>();
            var endOps = new List<int>();
            var rangesPerFile = new List<(string Name, int Start, int End)>();

            for (int i = 0; i < inputs.Count; i++)
            {
                var name = Path.GetFileName(inputs[i]);
                var full = fullResults[i];

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
            Console.WriteLine();

            var slicedTexts = new List<string>();
            var slicedOps = new List<List<int>>();
            var slicedOpNames = new List<List<string>>();

            for (int i = 0; i < fullResults.Count; i++)
            {
                var sliced = SliceFullTextByOpRange(fullResults[i], globalStart, globalEnd);
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

            PrintFullTextDiff(inputs, slicedTexts, slicedOps, slicedOpNames, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency);
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
            bool cleanupEfficiency)
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

        private sealed class SelfBlock
        {
            public SelfBlock(int index, int startOp, int endOp, string text, string pattern, int maxTokenLen, string opsLabel)
            {
                Index = index;
                StartOp = startOp;
                EndOp = endOp;
                Text = text;
                Pattern = pattern;
                MaxTokenLen = maxTokenLen;
                OpsLabel = opsLabel ?? "";
            }

            public int Index { get; }
            public int StartOp { get; }
            public int EndOp { get; }
            public string Text { get; }
            public string Pattern { get; }
            public int MaxTokenLen { get; }
            public string OpsLabel { get; }
        }

        private sealed class SelfResult
        {
            public SelfResult(string path, List<SelfBlock> blocks)
            {
                Path = path;
                Blocks = blocks;
            }

            public string Path { get; }
            public List<SelfBlock> Blocks { get; }
        }

        private static List<SelfBlock> ExtractSelfBlocks(PdfStream stream, PdfResources resources, HashSet<string> opFilter)
        {
            var blocks = new List<SelfBlock>();
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return blocks;

            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources));

            var currentTokens = new List<string>();
            var currentOps = new List<string>();
            int textOpIndex = 0;
            int startOp = 0;
            int endOp = 0;
            double? currentY = null;
            const double lineTol = 0.1;

            int blockIndex = 0;

            void Flush()
            {
                if (currentTokens.Count == 0)
                    return;

                var text = string.Concat(currentTokens);
                if (string.IsNullOrWhiteSpace(text))
                {
                    currentTokens.Clear();
                    currentOps.Clear();
                    return;
                }

                var lens = new List<int>();
                foreach (var token in currentTokens)
                {
                    if (string.IsNullOrWhiteSpace(token)) continue;
                    lens.Add(token.Length);
                }

                if (lens.Count == 0)
                {
                    currentTokens.Clear();
                    currentOps.Clear();
                    return;
                }

                var patternSb = new StringBuilder();
                int maxLen = 0;
                foreach (var len in lens)
                {
                    maxLen = Math.Max(maxLen, len);
                    patternSb.Append(len == 1 ? '1' : 'W');
                }

                blockIndex++;
                var opsLabel = BuildOpsLabel(currentOps);
                blocks.Add(new SelfBlock(blockIndex, startOp, endOp, text, patternSb.ToString(), maxLen, opsLabel));
                currentTokens.Clear();
                currentOps.Clear();
            }

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (IsTextShowingOperator(tok) && (opFilter.Count == 0 || opFilter.Contains(tok)))
                {
                    textOpIndex++;
                    var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                    var decoded = DequeueDecodedText(tok, operands, rawLine, textQueue) ?? "";
                    if (currentTokens.Count == 0)
                        startOp = textOpIndex;
                    endOp = textOpIndex;
                    currentTokens.Add(decoded);
                    currentOps.Add(tok);

                    if (IsLineBreakTextOperator(tok))
                        Flush();
                }
                else if (ShouldFlushForPosition(tok, operands, ref currentY, lineTol))
                {
                    Flush();
                }

                operands.Clear();
            }

            Flush();
            return blocks;
        }

        private static List<SelfBlock> ExtractSelfBlocksForPath(string path, int objId, HashSet<string> opFilter)
        {
            using var doc = new PdfDocument(new PdfReader(path));
            var found = FindStreamAndResourcesByObjId(doc, objId);
            var stream = found.Stream;
            var resources = found.Resources;
            if (stream == null || resources == null)
                return new List<SelfBlock>();
            return ExtractSelfBlocks(stream, resources, opFilter);
        }

        private static List<SelfBlock> SelectSelfVariableBlocks(List<SelfBlock> blocks, int minTokenLen, int patternMaxCount)
        {
            if (blocks.Count == 0) return blocks;
            var patternCounts = blocks
                .GroupBy(b => b.Pattern)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var selected = new List<SelfBlock>();
            foreach (var block in blocks)
            {
                if (block.MaxTokenLen < minTokenLen)
                    continue;

                if (patternCounts.TryGetValue(block.Pattern, out var count))
                {
                    if (count <= patternMaxCount)
                        selected.Add(block);
                }
            }

            return selected;
        }

        private static List<SelfBlock> FilterSelfBlocks(List<SelfBlock> blocks, int minTokenLenFilter)
        {
            if (minTokenLenFilter <= 1) return blocks;
            return blocks.Where(b => GetMaxWordTokenLen(b.Text) >= minTokenLenFilter).ToList();
        }

        private static List<SelfBlock> FilterAnchorBlocks(List<SelfBlock> blocks, int minLen, int maxLen, int maxWords)
        {
            if (minLen <= 0 && maxLen <= 0 && maxWords <= 0) return blocks;
            return blocks.Where(b =>
            {
                var text = b.Text ?? "";
                var len = text.Length;
                if (minLen > 0 && len < minLen) return false;
                if (maxLen > 0 && len > maxLen) return false;
                if (maxWords > 0 && CountWords(text) > maxWords) return false;
                return true;
            }).ToList();
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string BuildOpsLabel(List<string> ops)
        {
            if (ops == null || ops.Count == 0)
                return "";

            var unique = new List<string>();
            foreach (var op in ops)
            {
                if (!unique.Contains(op))
                    unique.Add(op);
            }

            return string.Join(",", unique);
        }

        private static int GetMaxWordTokenLen(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            int max = 0;
            var parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var token = raw.Trim();
                if (token.Length > max)
                    max = token.Length;
            }

            return max;
        }

        private static List<TextOpsAnchor> BuildSelfAnchors(List<SelfBlock> allBlocks, List<SelfBlock> variableBlocks, List<SelfBlock> fixedBlocks)
        {
            var anchors = new List<TextOpsAnchor>();
            if (variableBlocks.Count == 0)
                return anchors;

            var fixedByIndex = fixedBlocks.ToDictionary(b => b.Index, b => b);
            var fixedIndices = fixedByIndex.Keys.OrderBy(x => x).ToList();

            int v = 1;
            foreach (var vb in variableBlocks)
            {
                var prev = fixedIndices.LastOrDefault(i => i < vb.Index);
                var next = fixedIndices.FirstOrDefault(i => i > vb.Index);

                var prevText = prev > 0 && fixedByIndex.TryGetValue(prev, out var p) ? p.Text : "";
                var nextText = next > 0 && fixedByIndex.TryGetValue(next, out var n) ? n.Text : "";
                anchors.Add(new TextOpsAnchor
                {
                    VarIndex = v,
                    Block = vb.Index,
                    StartOp = vb.StartOp,
                    EndOp = vb.EndOp,
                    Var = vb.Text,
                    Prev = prevText,
                    Next = nextText,
                    PrevBlock = prev > 0 ? prev : null,
                    NextBlock = next > 0 ? next : null
                });
                v++;
            }

            return anchors;
        }

        private static void PrintSelfAnchors(string path, List<TextOpsAnchor> anchors)
        {
            var fileName = Path.GetFileName(path);
            lock (ConsoleLock)
            {
                Console.WriteLine($"[ANCHORS] {fileName}");
                Console.WriteLine();
            }

            if (anchors.Count == 0)
            {
                lock (ConsoleLock)
                {
                    Console.WriteLine("(nenhum bloco variável)");
                    Console.WriteLine();
                }
                return;
            }

            foreach (var a in anchors)
            {
                lock (ConsoleLock)
                {
                    var opRange = a.StartOp == a.EndOp ? $"{a.StartOp}" : $"{a.StartOp}-{a.EndOp}";
                    Console.WriteLine($"v{a.VarIndex} op{opRange}");
                    Console.WriteLine($"  VAR: \"{EscapeBlockText(a.Var)}\" (len={a.Var.Length})");
                    Console.WriteLine($"  PREV: \"{EscapeBlockText(a.Prev)}\"");
                    Console.WriteLine($"  NEXT: \"{EscapeBlockText(a.Next)}\"");
                    Console.WriteLine();
                }
            }
        }

        private static void PrintSelfSummary(List<SelfResult> results, string label)
        {
            lock (ConsoleLock)
            {
                Console.WriteLine($"OBJ - BLOCOS {label} (self)");
                Console.WriteLine($"Total arquivos: {results.Count}");
                Console.WriteLine();
            }
            for (int i = 0; i < results.Count; i++)
            {
                var name = Path.GetFileName(results[i].Path);
                lock (ConsoleLock)
                {
                    Console.WriteLine($"{i + 1}) {name} - {results[i].Blocks.Count} blocos");
                }
            }
            lock (ConsoleLock)
            {
                Console.WriteLine();
            }
        }

        private static void PrintSelfVariableBlocks(SelfResult result, int fileIndex, bool inline, string order, (int? Start, int? End) range, string label)
        {
            var blocks = result.Blocks;
            var fileName = Path.GetFileName(result.Path);
            lock (ConsoleLock)
            {
                Console.WriteLine($"[{fileIndex}] {fileName} - {blocks.Count} blocos ({label.ToLowerInvariant()})");
                Console.WriteLine();
            }

            if (blocks.Count == 0)
                return;

            if (range.Start.HasValue || range.End.HasValue)
            {
                var startIdx = range.Start ?? 1;
                var endIdx = range.End ?? startIdx;
                if (startIdx < 1) startIdx = 1;
                if (endIdx < startIdx) endIdx = startIdx;
                if (startIdx > blocks.Count)
                    return;
                if (endIdx > blocks.Count)
                    endIdx = blocks.Count;

                var mergedText = new StringBuilder();
                int startOp = blocks[startIdx - 1].StartOp;
                int endOp = blocks[endIdx - 1].EndOp;
                for (int i = startIdx - 1; i <= endIdx - 1; i++)
                    mergedText.Append(blocks[i].Text);

                var blockLabel = FormatSelfRangeLabel(startIdx, endIdx, startOp, endOp, "");
                WriteInlineSelf(blockLabel, fileName, mergedText.ToString(), order, inline);
                return;
            }

            if (inline)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    var blockLabel = FormatSelfBlockLabel(blocks[i]);
                    WriteInlineSelf(blockLabel, fileName, blocks[i].Text, order, inline);
                }
                return;
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                lock (ConsoleLock)
                {
                    var blockLabel = FormatSelfBlockLabel(block);
                    Console.WriteLine($"[{blockLabel}]");
                    var display = EscapeBlockText(block.Text);
                    Console.WriteLine($"  {fileName}: \"{display}\" (len={block.Text.Length})");
                    Console.WriteLine();
                }
            }
        }

        private static void WriteInlineSelf(string label, string fileName, string text, string order, bool inline)
        {
            if (!inline)
            {
                lock (ConsoleLock)
                {
                    Console.WriteLine($"[{label}]");
                    Console.WriteLine($"  {fileName}: \"{EscapeBlockText(text)}\" (len={text.Length})");
                    Console.WriteLine();
                }
                return;
            }

            var display = EscapeBlockText(text);
            lock (ConsoleLock)
            {
                if (IsBlockFirst(order))
                    Console.WriteLine($"{label}\t{fileName}\t\"{display}\" (len={text.Length})");
                else
                    Console.WriteLine($"\"{display}\" (len={text.Length})\t{label}\t{fileName}");
            }
        }

        private sealed class TokenAlignment
        {
            public TokenAlignment(int[] baseToOther, List<int>[] insertions)
            {
                BaseToOther = baseToOther;
                Insertions = insertions;
            }

            public int[] BaseToOther { get; }
            public List<int>[] Insertions { get; }
        }

        private sealed class TokenEncoding
        {
            public TokenEncoding(string baseEncoded, string otherEncoded, List<string> indexToToken)
            {
                BaseEncoded = baseEncoded;
                OtherEncoded = otherEncoded;
                IndexToToken = indexToToken;
            }

            public string BaseEncoded { get; }
            public string OtherEncoded { get; }
            public List<string> IndexToToken { get; }
        }

        private static TokenAlignment BuildAlignment(
            List<string> baseTokens,
            List<string> otherTokens,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency)
        {
            var encoding = BuildTokenEncoding(baseTokens, otherTokens);
            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(encoding.BaseEncoded, encoding.OtherEncoded, diffLineMode);

            if (cleanupSemantic)
                dmp.diff_cleanupSemantic(diffs);
            if (cleanupLossless)
                dmp.diff_cleanupSemanticLossless(diffs);
            if (cleanupEfficiency)
                dmp.diff_cleanupEfficiency(diffs);

            var baseToOther = new int[baseTokens.Count];
            for (int i = 0; i < baseToOther.Length; i++)
                baseToOther[i] = -1;

            var insertions = new List<int>[baseTokens.Count + 1];
            for (int i = 0; i < insertions.Length; i++)
                insertions[i] = new List<int>();

            int baseIdx = 0;
            int otherIdx = 0;

            foreach (var diff in diffs)
            {
                if (diff.operation == Operation.EQUAL)
                {
                    foreach (var ch in diff.text)
                    {
                        if (baseIdx < baseToOther.Length)
                            baseToOther[baseIdx] = otherIdx;
                        baseIdx++;
                        otherIdx++;
                    }
                    continue;
                }

                if (diff.operation == Operation.DELETE)
                {
                    baseIdx += diff.text.Length;
                    continue;
                }

                if (diff.operation == Operation.INSERT)
                {
                    foreach (var _ in diff.text)
                    {
                        insertions[Math.Min(baseIdx, insertions.Length - 1)].Add(otherIdx);
                        otherIdx++;
                    }
                }
            }

            return new TokenAlignment(baseToOther, insertions);
        }

        private static TokenEncoding BuildTokenEncoding(List<string> baseTokens, List<string> otherTokens)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var indexToToken = new List<string> { "" };
            int next = 1;

            void AddToken(string token)
            {
                token ??= "";
                if (map.ContainsKey(token)) return;
                if (next >= char.MaxValue)
                    throw new InvalidOperationException("Quantidade de tokens excede limite do diff (char).");
                map[token] = next;
                indexToToken.Add(token);
                next++;
            }

            foreach (var token in baseTokens)
                AddToken(token);
            foreach (var token in otherTokens)
                AddToken(token);

            string Encode(List<string> tokens)
            {
                var chars = new char[tokens.Count];
                for (int i = 0; i < tokens.Count; i++)
                {
                    var key = tokens[i] ?? "";
                    if (!map.TryGetValue(key, out var idx))
                        idx = 0;
                    chars[i] = (char)idx;
                }
                return new string(chars);
            }

            return new TokenEncoding(Encode(baseTokens), Encode(otherTokens), indexToToken);
        }

        private static List<VarBlockSlots> BuildVariableBlocks(bool[] varToken, bool[] varGap)
        {
            var blocks = new List<VarBlockSlots>();
            int slotCount = varToken.Length * 2 + 1;
            int start = -1;

            for (int slot = 0; slot < slotCount; slot++)
            {
                bool isVar = slot % 2 == 0
                    ? varGap[slot / 2]
                    : varToken[(slot - 1) / 2];

                if (isVar)
                {
                    if (start < 0)
                        start = slot;
                }
                else if (start >= 0)
                {
                    blocks.Add(new VarBlockSlots(start, slot - 1));
                    start = -1;
                }
            }

            if (start >= 0)
                blocks.Add(new VarBlockSlots(start, slotCount - 1));

            return blocks;
        }

        private static bool ParseOptions(
            string[] args,
            out List<string> inputs,
            out int objId,
            out HashSet<string> opFilter,
            out bool blocks,
            out bool blocksInline,
            out string blocksOrder,
            out (int? Start, int? End) blockRange,
            out bool selfMode,
            out int selfMinTokenLen,
            out int selfPatternMax,
            out int minTokenLenFilter,
            out int anchorsMinLen,
            out int anchorsMaxLen,
            out int anchorsMaxWords,
            out bool selfAnchors,
            out string rulesPathArg,
            out string rulesDoc,
            out string anchorsOut,
            out bool anchorsMerge,
            out TokenMode tokenMode,
            out bool diffLineMode,
            out bool cleanupSemantic,
            out bool cleanupLossless,
            out bool cleanupEfficiency,
            out bool diffFullText,
            out bool includeLineBreaks,
            out bool includeTdLineBreaks,
            out string rangeStartRegex,
            out string rangeEndRegex,
            out int? rangeStartOp,
            out int? rangeEndOp,
            out bool dumpRangeText,
            out bool includeTmLineBreaks,
            out bool lineBreakAsSpace)
        {
            inputs = new List<string>();
            objId = 0;
            opFilter = new HashSet<string>(StringComparer.Ordinal);
            blocks = false;
            blocksInline = false;
            blocksOrder = "block-first";
            blockRange = (null, null);
            selfMode = false;
            selfMinTokenLen = 1;
            selfPatternMax = 1;
            minTokenLenFilter = 0;
            anchorsMinLen = 0;
            anchorsMaxLen = 0;
            anchorsMaxWords = 0;
            selfAnchors = false;
            rulesPathArg = "";
            rulesDoc = "";
            anchorsOut = "";
            anchorsMerge = false;
            tokenMode = TokenMode.Text;
            diffLineMode = false;
            cleanupSemantic = false;
            cleanupLossless = false;
            cleanupEfficiency = false;
            diffFullText = false;
            includeLineBreaks = true;
            includeTdLineBreaks = false;
            rangeStartRegex = "";
            rangeEndRegex = "";
            rangeStartOp = null;
            rangeEndOp = null;
            dumpRangeText = false;
            includeTmLineBreaks = false;
            lineBreakAsSpace = false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        inputs.Add(raw.Trim());
                    continue;
                }
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputs.Add(args[++i]);
                    continue;
                }
                if (string.Equals(arg, "--input-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var dir = args[++i];
                    if (Directory.Exists(dir))
                    {
                        inputs.AddRange(Directory.GetFiles(dir, "*.pdf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                    }
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out objId);
                    continue;
                }
                if ((string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--ops", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        opFilter.Add(raw.Trim());
                    continue;
                }
                if (string.Equals(arg, "--blocks", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--block", StringComparison.OrdinalIgnoreCase))
                {
                    blocks = true;
                    continue;
                }
                if (string.Equals(arg, "--full-text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--diff-text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--fulltext", StringComparison.OrdinalIgnoreCase))
                {
                    diffFullText = true;
                    continue;
                }
                if (string.Equals(arg, "--no-line-breaks", StringComparison.OrdinalIgnoreCase))
                {
                    includeLineBreaks = false;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks", StringComparison.OrdinalIgnoreCase))
                {
                    includeLineBreaks = true;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks-td", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--break-td", StringComparison.OrdinalIgnoreCase))
                {
                    includeTdLineBreaks = true;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks-tm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--break-tm", StringComparison.OrdinalIgnoreCase))
                {
                    includeTmLineBreaks = true;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks-space", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--break-space", StringComparison.OrdinalIgnoreCase))
                {
                    lineBreakAsSpace = true;
                    continue;
                }
                if (string.Equals(arg, "--range-start", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rangeStartRegex = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--range-end", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rangeEndRegex = args[++i];
                    continue;
                }
                if ((string.Equals(arg, "--range-start-op", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--start-op", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var op))
                        rangeStartOp = Math.Max(1, op);
                    continue;
                }
                if ((string.Equals(arg, "--range-end-op", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--end-op", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var op))
                        rangeEndOp = Math.Max(1, op);
                    continue;
                }
                if (string.Equals(arg, "--range-text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--range-dump", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--dump-range", StringComparison.OrdinalIgnoreCase))
                {
                    dumpRangeText = true;
                    continue;
                }
                if (string.Equals(arg, "--line-mode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--line-diff", StringComparison.OrdinalIgnoreCase))
                {
                    diffLineMode = true;
                    continue;
                }
                if (string.Equals(arg, "--blocks-inline", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--block-inline", StringComparison.OrdinalIgnoreCase))
                {
                    blocks = true;
                    blocksInline = true;
                    continue;
                }
                if (string.Equals(arg, "--blocks-order", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    blocksOrder = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--blocks-range", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    blocks = true;
                    blockRange = ParseBlockRange(args[++i]);
                    continue;
                }
                if (string.Equals(arg, "--self", StringComparison.OrdinalIgnoreCase))
                {
                    selfMode = true;
                    blocks = true;
                    continue;
                }
                if (string.Equals(arg, "--anchors", StringComparison.OrdinalIgnoreCase))
                {
                    selfMode = true;
                    selfAnchors = true;
                    blocks = true;
                    continue;
                }
                if (string.Equals(arg, "--anchors-min-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var len))
                        anchorsMinLen = Math.Max(0, len);
                    continue;
                }
                if (string.Equals(arg, "--anchors-max-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var len))
                        anchorsMaxLen = Math.Max(0, len);
                    continue;
                }
                if ((string.Equals(arg, "--anchors-max-words", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--anchors-max-tokens", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var len))
                        anchorsMaxWords = Math.Max(0, len);
                    continue;
                }
                if (string.Equals(arg, "--self-min-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var len))
                        selfMinTokenLen = Math.Max(1, len);
                    continue;
                }
                if (string.Equals(arg, "--self-pattern-max", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var count))
                        selfPatternMax = Math.Max(1, count);
                    continue;
                }
                if ((string.Equals(arg, "--min-token-len", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--min-text-len", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var len))
                        minTokenLenFilter = Math.Max(1, len);
                    continue;
                }
                if (string.Equals(arg, "--long-tokens", StringComparison.OrdinalIgnoreCase))
                {
                    minTokenLenFilter = Math.Max(minTokenLenFilter, 4);
                    continue;
                }
                if (string.Equals(arg, "--rules", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rulesPathArg = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--doc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rulesDoc = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--anchors-out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    anchorsOut = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--anchors-merge", StringComparison.OrdinalIgnoreCase))
                {
                    anchorsMerge = true;
                    continue;
                }
                if (string.Equals(arg, "--cleanup", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var mode = part.Trim().ToLowerInvariant();
                        if (mode == "none" || mode == "off")
                        {
                            cleanupSemantic = false;
                            cleanupLossless = false;
                            cleanupEfficiency = false;
                            continue;
                        }
                        if (mode == "semantic" || mode == "sem")
                        {
                            cleanupSemantic = true;
                            continue;
                        }
                        if (mode == "lossless" || mode == "semantic-lossless" || mode == "loss")
                        {
                            cleanupLossless = true;
                            continue;
                        }
                        if (mode == "efficiency" || mode == "efficient" || mode == "eff")
                        {
                            cleanupEfficiency = true;
                            continue;
                        }
                    }
                    continue;
                }
                if (string.Equals(arg, "--cleanup-semantic", StringComparison.OrdinalIgnoreCase))
                {
                    cleanupSemantic = true;
                    continue;
                }
                if (string.Equals(arg, "--cleanup-lossless", StringComparison.OrdinalIgnoreCase))
                {
                    cleanupLossless = true;
                    continue;
                }
                if (string.Equals(arg, "--cleanup-efficiency", StringComparison.OrdinalIgnoreCase))
                {
                    cleanupEfficiency = true;
                    continue;
                }
                if ((string.Equals(arg, "--token-mode", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--token", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--by", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var raw = args[++i].Trim().ToLowerInvariant();
                    tokenMode = raw switch
                    {
                        "ops" or "op" or "raw" or "bytes" => TokenMode.Ops,
                        _ => TokenMode.Text
                    };
                    continue;
                }
                if (string.Equals(arg, "--ops-diff", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--diff-ops", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--op-diff", StringComparison.OrdinalIgnoreCase))
                {
                    tokenMode = TokenMode.Ops;
                    continue;
                }
            }

            return true;
        }

        private static string ResolveRulesPath(string rulesPathArg, string rulesDoc)
        {
            if (!string.IsNullOrWhiteSpace(rulesPathArg))
            {
                var resolved = ResolveExistingPath(rulesPathArg);
                if (File.Exists(resolved))
                    return resolved;
                return rulesPathArg;
            }

            if (!string.IsNullOrWhiteSpace(rulesDoc))
            {
                var name = rulesDoc.Trim();
                var candidateNames = new[]
                {
                    $"{name}.yml",
                    $"{name}.yaml"
                };

                foreach (var file in candidateNames)
                {
                    var resolved = ResolveExistingPath(Path.Combine("configs", "textops_rules", file));
                    if (File.Exists(resolved))
                        return resolved;
                }
            }

            return "";
        }

        private static string ResolveExistingPath(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
                return inputPath;
            if (File.Exists(inputPath))
                return inputPath;

            var cwd = Directory.GetCurrentDirectory();
            var exeBase = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(cwd, inputPath),
                Path.Combine(cwd, "configs", inputPath),
                Path.Combine(cwd, "configs", "textops_rules", inputPath),
                Path.Combine(cwd, "..", inputPath),
                Path.Combine(cwd, "..", "configs", "textops_rules", inputPath),
                Path.Combine(cwd, "..", "..", "configs", "textops_rules", inputPath),
                Path.GetFullPath(Path.Combine(exeBase, "../../../../configs/textops_rules", inputPath))
            };

            return candidates.FirstOrDefault(File.Exists) ?? inputPath;
        }

        private static TextOpsRules? LoadRules(string rulesPath)
        {
            if (string.IsNullOrWhiteSpace(rulesPath) || !File.Exists(rulesPath))
                return null;

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                using var reader = new StreamReader(rulesPath);
                var doc = deserializer.Deserialize<TextOpsRulesFile>(reader);
                return doc?.Self;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildAnchorsOutPath(string anchorsOut, string inputPath, int objId, int index, int total)
        {
            if (string.IsNullOrWhiteSpace(anchorsOut))
                return "";

            var outPath = anchorsOut;
            var isDir = Directory.Exists(outPath)
                || outPath.EndsWith(Path.DirectorySeparatorChar)
                || outPath.EndsWith(Path.AltDirectorySeparatorChar);

            if (isDir)
            {
                var dir = outPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(dir);
                var name = Path.GetFileNameWithoutExtension(inputPath);
                return Path.Combine(dir, $"{name}_obj{objId}.yml");
            }

            if (total > 1)
            {
                var dir = Path.GetDirectoryName(outPath) ?? "";
                var baseName = Path.GetFileNameWithoutExtension(outPath);
                var ext = Path.GetExtension(outPath);
                return Path.Combine(dir, $"{baseName}_{index}{ext}");
            }

            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(outDir))
                Directory.CreateDirectory(outDir);
            return outPath;
        }

        private static void WriteAnchorsFile(string outPath, string rulesDoc, string inputPath, int objId, List<TextOpsAnchor> anchors)
        {
            if (string.IsNullOrWhiteSpace(outPath))
                return;

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var docName = string.IsNullOrWhiteSpace(rulesDoc) ? null : rulesDoc.Trim();
            var payload = new TextOpsAnchorsFile
            {
                Version = 1,
                Doc = docName,
                SourceFile = Path.GetFileName(inputPath),
                Obj = objId,
                Anchors = anchors
            };

            var yaml = serializer.Serialize(payload);
            File.WriteAllText(outPath, yaml);
            Console.WriteLine("Anchors salvos em: " + outPath);
        }

        private static void MergeAnchors(Dictionary<string, TextOpsAnchorConcept> merged, List<TextOpsAnchor> anchors, string sourcePath)
        {
            var sourceFile = Path.GetFileName(sourcePath);
            foreach (var anchor in anchors)
            {
                var key = $"{anchor.Prev}\u001F{anchor.Next}";
                if (!merged.TryGetValue(key, out var concept))
                {
                    concept = new TextOpsAnchorConcept
                    {
                        Prev = anchor.Prev,
                        Next = anchor.Next
                    };
                    merged[key] = concept;
                }

                concept.Count++;
                if (concept.Examples.Count < 3)
                {
                    concept.Examples.Add(new TextOpsAnchorExample
                    {
                        Var = anchor.Var,
                        SourceFile = sourceFile,
                        Block = anchor.Block,
                        StartOp = anchor.StartOp,
                        EndOp = anchor.EndOp
                    });
                }
            }
        }

        private static void PrintMergedAnchors(List<TextOpsAnchorConcept> anchors)
        {
            Console.WriteLine("OBJ - ANCHORS (conceitual)");
            Console.WriteLine($"Total anchors: {anchors.Count}");
            Console.WriteLine();

            int i = 1;
            foreach (var anchor in anchors)
            {
                Console.WriteLine($"a{i} (count={anchor.Count})");
                Console.WriteLine($"  PREV: \"{EscapeBlockText(anchor.Prev ?? "")}\"");
                Console.WriteLine($"  NEXT: \"{EscapeBlockText(anchor.Next ?? "")}\"");
                if (anchor.Examples.Count > 0)
                {
                    foreach (var ex in anchor.Examples)
                    {
                        Console.WriteLine($"  EX: \"{EscapeBlockText(ex.Var)}\" ({ex.SourceFile}, ops {ex.StartOp}-{ex.EndOp})");
                    }
                }
                Console.WriteLine();
                i++;
            }
        }

        private static string BuildAnchorsMergeOutPath(string anchorsOut, string rulesDoc, int objId)
        {
            if (string.IsNullOrWhiteSpace(anchorsOut))
                return "";

            var isDir = Directory.Exists(anchorsOut)
                || anchorsOut.EndsWith(Path.DirectorySeparatorChar)
                || anchorsOut.EndsWith(Path.AltDirectorySeparatorChar);

            if (!isDir)
            {
                var outDir = Path.GetDirectoryName(anchorsOut);
                if (!string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);
                return anchorsOut;
            }

            var dir = anchorsOut.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(dir);
            var doc = string.IsNullOrWhiteSpace(rulesDoc) ? "anchors" : rulesDoc.Trim();
            return Path.Combine(dir, $"{doc}_obj{objId}_concept.yml");
        }

        private static void WriteMergedAnchorsFile(string outPath, string rulesDoc, int objId, List<string> sources, List<TextOpsAnchorConcept> anchors)
        {
            if (string.IsNullOrWhiteSpace(outPath))
                return;

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var payload = new TextOpsAnchorConceptFile
            {
                Version = 1,
                Doc = string.IsNullOrWhiteSpace(rulesDoc) ? null : rulesDoc.Trim(),
                Obj = objId,
                Sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                Anchors = anchors
            };

            var yaml = serializer.Serialize(payload);
            File.WriteAllText(outPath, yaml);
            Console.WriteLine("Anchors conceituais salvos em: " + outPath);
        }

        private static (List<SelfBlock> Variable, List<SelfBlock> Fixed) ClassifySelfBlocks(
            List<SelfBlock> blocks,
            int selfMinTokenLen,
            int selfPatternMax,
            TextOpsRules? rules)
        {
            if (blocks.Count == 0)
                return (new List<SelfBlock>(), new List<SelfBlock>());

            var minLen = rules?.MinTokenLen ?? selfMinTokenLen;
            var patternMax = rules?.PatternMax ?? selfPatternMax;

            var defaultVariable = SelectSelfVariableBlocks(blocks, minLen, patternMax);
            var isVariable = new Dictionary<SelfBlock, bool>();

            foreach (var b in blocks)
                isVariable[b] = defaultVariable.Contains(b);

            if (rules != null)
            {
                foreach (var b in blocks)
                {
                    if (MatchesAnyRule(rules.Fixed, b.Text))
                        isVariable[b] = false;
                }

                foreach (var b in blocks)
                {
                    if (MatchesAnyRule(rules.Variable, b.Text))
                        isVariable[b] = true;
                }
            }

            var vars = new List<SelfBlock>();
            var fixeds = new List<SelfBlock>();
            foreach (var kv in isVariable)
            {
                if (kv.Value) vars.Add(kv.Key);
                else fixeds.Add(kv.Key);
            }

            vars = vars.OrderBy(b => b.Index).ToList();
            fixeds = fixeds.OrderBy(b => b.Index).ToList();
            return (vars, fixeds);
        }

        private static bool MatchesAnyRule(List<TextOpsRule>? rules, string text)
        {
            if (rules == null || rules.Count == 0)
                return false;

            foreach (var rule in rules)
            {
                if (RuleMatches(rule, text))
                    return true;
            }

            return false;
        }

        private static bool RuleMatches(TextOpsRule rule, string text)
        {
            if (rule == null)
                return false;

            if (rule.MinLen.HasValue && text.Length < rule.MinLen.Value)
                return false;
            if (rule.MaxLen.HasValue && text.Length > rule.MaxLen.Value)
                return false;

            if (!string.IsNullOrWhiteSpace(rule.StartsWith)
                && !text.StartsWith(rule.StartsWith, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(rule.EndsWith)
                && !text.EndsWith(rule.EndsWith, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(rule.Contains)
                && text.IndexOf(rule.Contains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (!string.IsNullOrWhiteSpace(rule.Regex))
            {
                try
                {
                    if (!Regex.IsMatch(text, rule.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsLineBreakTextOperator(string op)
        {
            return op == "'" || op == "\"";
        }

        private static bool IsExplicitLineBreakOperator(string op)
        {
            return op == "T*" || op == "'" || op == "\"";
        }

        private sealed class TextOpsRulesFile
        {
            public int Version { get; set; } = 1;
            public string? Doc { get; set; }
            public TextOpsRules? Self { get; set; }
        }

        private sealed class TextOpsRules
        {
            public int? MinTokenLen { get; set; }
            public int? PatternMax { get; set; }
            public List<TextOpsRule> Fixed { get; set; } = new List<TextOpsRule>();
            public List<TextOpsRule> Variable { get; set; } = new List<TextOpsRule>();
        }

        private sealed class TextOpsRule
        {
            public string? Name { get; set; }
            public string? Regex { get; set; }
            public string? Contains { get; set; }
            public string? StartsWith { get; set; }
            public string? EndsWith { get; set; }
            public int? MinLen { get; set; }
            public int? MaxLen { get; set; }
        }

        private sealed class TextOpsAnchorsFile
        {
            public int Version { get; set; } = 1;
            public string? Doc { get; set; }
            public string? SourceFile { get; set; }
            public int Obj { get; set; }
            public List<TextOpsAnchor> Anchors { get; set; } = new List<TextOpsAnchor>();
        }

        private sealed class TextOpsAnchor
        {
            public int VarIndex { get; set; }
            public int Block { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Var { get; set; } = "";
            public int? PrevBlock { get; set; }
            public string Prev { get; set; } = "";
            public int? NextBlock { get; set; }
            public string Next { get; set; } = "";
        }

        private sealed class TextOpsAnchorConceptFile
        {
            public int Version { get; set; } = 1;
            public string? Doc { get; set; }
            public int Obj { get; set; }
            public List<string> Sources { get; set; } = new List<string>();
            public List<TextOpsAnchorConcept> Anchors { get; set; } = new List<TextOpsAnchorConcept>();
        }

        private sealed class TextOpsAnchorConcept
        {
            public string? Prev { get; set; }
            public string? Next { get; set; }
            public int Count { get; set; }
            public List<TextOpsAnchorExample> Examples { get; set; } = new List<TextOpsAnchorExample>();
        }

        private sealed class TextOpsAnchorExample
        {
            public string Var { get; set; } = "";
            public string SourceFile { get; set; } = "";
            public int Block { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
        }

        private static bool ShouldFlushForPosition(string op, List<string> operands, ref double? currentY, double lineTol)
        {
            if (op == "BT")
            {
                currentY = null;
                return true;
            }
            if (op == "ET")
                return true;
            if (op == "T*")
            {
                currentY = null;
                return true;
            }

            if (op == "Tm" && operands.Count >= 6)
            {
                if (TryParseNumber(operands[^1], out var y))
                {
                    var changed = currentY.HasValue && Math.Abs(y - currentY.Value) > lineTol;
                    currentY = y;
                    return changed;
                }
                return false;
            }

            if ((op == "Td" || op == "TD") && operands.Count >= 2)
            {
                if (TryParseNumber(operands[^1], out var ty))
                {
                    var newY = currentY.HasValue ? currentY.Value + ty : ty;
                    var changed = currentY.HasValue && Math.Abs(newY - currentY.Value) > lineTol;
                    currentY = newY;
                    return changed;
                }
                return false;
            }

            return false;
        }

        private static bool TryParseNumber(string token, out double value)
        {
            return double.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static (int? Start, int? End) ParseBlockRange(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);
            raw = raw.Trim();
            if (raw.Contains('-', StringComparison.Ordinal))
            {
                var parts = raw.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && int.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var start)
                    && int.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var end))
                    return (start, end);
            }
            if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var single))
                return (single, single);
            return (null, null);
        }

        private static (PdfStream? Stream, PdfResources? Resources) FindStreamAndResourcesByObjId(PdfDocument doc, int objId)
        {
            // Prefer content streams with page resources (ToUnicode aware)
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources();
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                foreach (var s in EnumerateStreams(contents))
                {
                    int id = s.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id == objId)
                        return (s, resources);
                }

                var xobjects = resources?.GetResource(PdfName.XObject) as PdfDictionary;
                if (xobjects != null)
                {
                    foreach (var name in xobjects.KeySet())
                    {
                        var xs = xobjects.GetAsStream(name);
                        if (xs == null) continue;
                        int id = xs.GetIndirectReference()?.GetObjNumber() ?? 0;
                        if (id == objId)
                        {
                            var xresDict = xs.GetAsDictionary(PdfName.Resources);
                            var xres = xresDict != null ? new PdfResources(xresDict) : resources;
                            return (xs, xres);
                        }
                    }
                }
            }

            // Fallback: direct lookup without resources (less accurate)
            int max = doc.GetNumberOfPdfObjects();
            for (int i = 0; i < max; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj is not PdfStream stream)
                    continue;
                int id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (id == objId)
                    return (stream, new PdfResources(new PdfDictionary()));
            }

            return (null, null);
        }

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
            public TokenOpsResult(List<string> tokens, List<int> opIndexes, List<string> opNames)
            {
                Tokens = tokens;
                OpIndexes = opIndexes;
                OpNames = opNames;
            }

            public List<string> Tokens { get; }
            public List<int> OpIndexes { get; }
            public List<string> OpNames { get; }
        }

        private static TokenOpsResult ExtractTextOperatorTokensWithOps(PdfStream stream, PdfResources resources, HashSet<string> opFilter, TokenMode tokenMode)
        {
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0)
                return new TokenOpsResult(new List<string>(), new List<int>(), new List<string>());

            var tokens = TokenizeContent(bytes);
            var result = new List<string>();
            var opIndexes = new List<int>();
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

                if (IsTextShowingOperator(tok) && (opFilter.Count == 0 || opFilter.Contains(tok)))
                {
                    opIndex++;
                    string text;
                    if (tokenMode == TokenMode.Text)
                    {
                        var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                        text = DequeueDecodedText(tok, operands, rawLine, textQueue) ?? "";
                    }
                    else
                    {
                        text = ExtractRawTextToken(tok, operands);
                    }

                    result.Add(text);
                    opIndexes.Add(opIndex);
                    opNames.Add(tok);
                }

                operands.Clear();
            }

            return new TokenOpsResult(result, opIndexes, opNames);
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

                if (IsTextShowingOperator(tok) && (opFilter.Count == 0 || opFilter.Contains(tok)))
                {
                    opIndex++;
                    var decoded = DequeueDecodedText(tok, operands, null, textQueue) ?? "";
                    AppendText(decoded, opIndex, tok);
                    lastTextOpIndex = opIndex;
                    if (includeLineBreaks && IsLineBreakTextOperator(tok))
                        AppendBreak(opIndex, tok);
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
