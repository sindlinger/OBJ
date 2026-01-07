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
    /// <summary>
    /// Compara operadores de texto (Tj/TJ/Td/Tf/Tm/BT/ET) entre varios PDFs para um objeto especifico.
    /// Mostra linhas fixas (iguais) e variaveis (mudam).
    /// Uso:
    ///   tjpdf-cli inspect objects textopsvar --inputs a.pdf,b.pdf --obj 6
    ///   tjpdf-cli inspect objects textopsfixed --inputs a.pdf,b.pdf --obj 6
    ///   tjpdf-cli inspect objects textopsdiff --inputs a.pdf,b.pdf --obj 6
    /// </summary>
    internal static partial class ObjectsTextOpsDiff
    {
        private static readonly object ConsoleLock = new();
        internal enum DiffMode
        {
            Fixed,
            Variations,
            Both,
            Align
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
                    out var blocksSpecified,
                    out var selfMode,
                    out var selfMinTokenLen,
                    out var selfPatternMax,
                    out var minTokenLenFilter,
                    out var minBlockLenFilter,
                    out var minBlockLenSpecified,
                    out var anchorsMinLen,
                    out var anchorsMaxLen,
                    out var anchorsMaxWords,
                    out var selfAnchors,
                    out var rulesPathArg,
                    out var rulesDoc,
                    out var anchorsOut,
                    out var anchorsMerge,
                    out var plainOutput,
                    out var blockTokens,
                    out var blockTokensSpecified,
                    out var tokenMode,
                    out var diffLineMode,
                    out var cleanupSemantic,
                    out var cleanupLossless,
                    out var cleanupEfficiency,
                    out var cleanupSpecified,
                    out var diffLineModeSpecified,
                    out var diffFullText,
                    out var includeLineBreaks,
                    out var includeTdLineBreaks,
                    out var rangeStartRegex,
                    out var rangeEndRegex,
                    out var rangeStartOp,
                    out var rangeEndOp,
                    out var dumpRangeText,
                    out var includeTmLineBreaks,
                    out var lineBreakAsSpace,
                    out var lineBreaksSpecified,
                    out var useLargestContents,
                    out var contentsPage))
                return;

            var rulesPath = ResolveRulesPath(rulesPathArg, rulesDoc);
            var rules = LoadRules(rulesPath);
            if ((!string.IsNullOrWhiteSpace(rulesPathArg) || !string.IsNullOrWhiteSpace(rulesDoc))
                && (string.IsNullOrWhiteSpace(rulesPath) || rules == null))
            {
                Console.WriteLine("Regras de textops nao encontradas ou invalidas.");
            }

            var headerLabels = DespachoContentsDetector.GetDespachoHeaderLabels(rulesDoc, objId);

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

            if (objId <= 0 && !useLargestContents)
            {
                Console.WriteLine("Informe --obj <id> ou use --contents (maior stream da pagina).");
                return;
            }

            if (!selfMode && mode == DiffMode.Variations && !blocksSpecified)
            {
                blocks = true;
                blocksInline = true;
                blocksOrder = "block-first";
                if (!plainOutput)
                    plainOutput = true;
            }
            if (!selfMode && mode == DiffMode.Variations && !blockTokensSpecified)
                blockTokens = true;

            if (!selfMode && mode == DiffMode.Both)
            {
                diffFullText = true;
                dumpRangeText = true;
                if (!cleanupSpecified)
                {
                    cleanupSemantic = true;
                    cleanupLossless = true;
                    cleanupEfficiency = true;
                }
                if (!diffLineModeSpecified)
                    diffLineMode = true;
                if (!lineBreaksSpecified)
                {
                    includeLineBreaks = true;
                    includeTdLineBreaks = true;
                    includeTmLineBreaks = true;
                    lineBreakAsSpace = true;
                }
            }

            if (selfMode)
            {
                var results = new List<SelfResult>();
                foreach (var path in inputs)
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"Arquivo nao encontrado: {path}");
                        continue;
                    }

                    using var doc = new PdfDocument(new PdfReader(path));
                    int pageForFile = contentsPage;
                    bool fallbackUsed = false;
                    if (useLargestContents)
                    {
                        pageForFile = DespachoContentsDetector.ResolveContentsPageForDoc(doc, contentsPage, headerLabels, out fallbackUsed);
                        if (pageForFile <= 0)
                        {
                            Console.WriteLine($"Contents nao encontrado para despacho em: {path}");
                            continue;
                        }
                    }
                    var found = useLargestContents
                        ? DespachoContentsDetector.FindLargestContentsStreamByPage(doc, pageForFile, headerLabels, contentsPage <= 0 && !fallbackUsed)
                        : FindStreamAndResourcesByObjId(doc, objId);
                    var stream = found.Stream;
                    var resources = found.Resources;
                    if (stream == null || resources == null)
                    {
                        Console.WriteLine(useLargestContents
                            ? $"Contents nao encontrado na pagina {pageForFile}: {path}"
                            : $"Objeto {objId} nao encontrado em: {path}");
                        continue;
                    }

                    var blocksSelf = ExtractSelfBlocks(stream, resources, opFilter);
                    var classified = ClassifySelfBlocks(blocksSelf, selfMinTokenLen, selfPatternMax, rules);
                    if (mode == DiffMode.Fixed)
                        results.Add(new SelfResult(path, FilterSelfBlocks(classified.Fixed, minTokenLenFilter)));
                    else
                        results.Add(new SelfResult(path, FilterSelfBlocks(classified.Variable, minTokenLenFilter)));
                }

                var selfLabel = mode == DiffMode.Fixed ? "FIXOS" : "VARIAVEIS";
                if (results.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF valido para processar (--self).");
                    return;
                }
                PrintSelfSummary(results, selfLabel);

                if (selfAnchors)
                {
                    var merged = new Dictionary<string, TextOpsAnchorConcept>(StringComparer.Ordinal);
                    var sources = new List<string>();

                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var path = inputs[i];
                        int pageForFile = contentsPage;
                        bool fallbackUsed = false;
                        if (useLargestContents)
                            pageForFile = DespachoContentsDetector.ResolveContentsPageForDoc(path, contentsPage, headerLabels, out fallbackUsed);
                        if (useLargestContents && pageForFile <= 0)
                        {
                            Console.WriteLine($"Contents nao encontrado para despacho em: {path}");
                            continue;
                        }
                        var blocksSelf = useLargestContents
                            ? ExtractSelfBlocksForPathByPage(path, pageForFile, opFilter, headerLabels, contentsPage <= 0 && !fallbackUsed)
                            : ExtractSelfBlocksForPath(path, objId, opFilter);
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

            if (mode == DiffMode.Align)
            {
                useLargestContents = true;
                var valid = new List<string>();
                foreach (var path in inputs)
                {
                    if (File.Exists(path))
                        valid.Add(path);
                    else
                        Console.WriteLine($"Arquivo nao encontrado: {path}");
                }

                if (valid.Count < 2)
                {
                    Console.WriteLine("Informe ao menos dois PDFs validos para alinhar.");
                    return;
                }

                var aPath = valid[0];
                var bPath = valid[1];
                AlignBlocks(aPath, bPath, objId, opFilter, useLargestContents, contentsPage, headerLabels);
                return;
            }

            var all = new List<List<string>>();
            var tokenLists = new List<List<string>>();
            var tokenOpStartLists = new List<List<int>>();
            var tokenOpEndLists = new List<List<int>>();
            var tokenOpNames = new List<List<string>>();
            var fullResults = new List<FullTextOpsResult>();
            var usedInputs = new List<string>();
            foreach (var path in inputs)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"Arquivo nao encontrado: {path}");
                    continue;
                }
                using var doc = new PdfDocument(new PdfReader(path));
                int pageForFile = contentsPage;
                bool fallbackUsed = false;
                if (useLargestContents)
                {
                    pageForFile = DespachoContentsDetector.ResolveContentsPageForDoc(doc, contentsPage, headerLabels, out fallbackUsed);
                    if (pageForFile <= 0)
                    {
                        Console.WriteLine($"Contents nao encontrado para despacho em: {path}");
                        continue;
                    }
                }
                var found = useLargestContents
                    ? DespachoContentsDetector.FindLargestContentsStreamByPage(doc, pageForFile, headerLabels, contentsPage <= 0 && !fallbackUsed)
                    : FindStreamAndResourcesByObjId(doc, objId);
                var stream = found.Stream;
                var resources = found.Resources;
                if (stream == null || resources == null)
                {
                    Console.WriteLine(useLargestContents
                        ? $"Contents nao encontrado na pagina {pageForFile}: {path}"
                        : $"Objeto {objId} nao encontrado em: {path}");
                    continue;
                }
                usedInputs.Add(path);
                if (!diffFullText)
                {
                    all.Add(ExtractTextOperatorLines(stream, resources, opFilter, tokenMode));
                    if (blocks && (mode == DiffMode.Variations || mode == DiffMode.Both))
                    {
                        var tokensWithOps = blockTokens
                            ? ExtractTextOperatorBlockTokensWithOps(stream, resources, opFilter)
                            : ExtractTextOperatorTokensWithOps(stream, resources, opFilter, tokenMode);
                        tokenLists.Add(tokensWithOps.Tokens);
                        tokenOpStartLists.Add(tokensWithOps.OpStarts);
                        tokenOpEndLists.Add(tokensWithOps.OpEnds);
                        tokenOpNames.Add(tokensWithOps.OpNames);
                    }
                }
                else
                {
                    var fullText = ExtractFullTextWithOps(stream, resources, opFilter, includeLineBreaks, includeTdLineBreaks, includeTmLineBreaks, lineBreakAsSpace);
                    fullResults.Add(fullText);
                }
            }
            inputs = usedInputs;
            if (inputs.Count < 2 && !selfMode && mode != DiffMode.Both)
            {
                Console.WriteLine("Informe ao menos dois PDFs validos para comparar.");
                return;
            }
            if (inputs.Count == 0)
            {
                Console.WriteLine("Nenhum PDF valido para processar.");
                return;
            }

            if (diffFullText)
            {
                var roiDoc = ResolveRoiDoc(rulesDoc, objId);
                if (mode == DiffMode.Variations || mode == DiffMode.Both)
                    PrintFullTextDiffWithRange(inputs, fullResults, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency, rangeStartRegex, rangeEndRegex, rangeStartOp, rangeEndOp, dumpRangeText, roiDoc, objId, mode == DiffMode.Both);
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
                    PrintVariationBlocks(inputs, tokenLists, tokenOpStartLists, tokenOpEndLists, tokenOpNames, blocksInline, blocksOrder, blockRange, minTokenLenFilter, minBlockLenFilter, plainOutput, blockTokens, tokenMode, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency);
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
            List<List<int>> tokenOpStartLists,
            List<List<int>> tokenOpEndLists,
            List<List<string>> tokenOpNames,
            bool inline,
            string order,
            (int? Start, int? End) range,
            int minTokenLenFilter,
            int minBlockLenFilter,
            bool plainOutput,
            bool blockTokens,
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
                WriteInlineBlock(FormatBlockLabel(startIdx, endIdx), merged, inputs, baseTokens, tokenLists, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments, order, plainOutput, blockTokens);
                return;
            }

            if (inline)
            {
                for (int idx = 0; idx < blocks.Count; idx++)
                {
                    if (minTokenLenFilter > 0)
                    {
                        var maxLen = GetBlockMaxTokenLen(blocks[idx], baseTokens, tokenLists, alignments);
                        if (maxLen < minTokenLenFilter)
                            continue;
                    }
                    if (minBlockLenFilter > 0)
                    {
                        var maxBlockLen = 0;
                        for (int i = 0; i < inputs.Count; i++)
                        {
                            var textLen = BuildBlockText(blocks[idx], i, baseTokens, tokenLists, alignments, blockTokens).Length;
                            if (textLen > maxBlockLen)
                                maxBlockLen = textLen;
                        }
                        if (maxBlockLen < minBlockLenFilter)
                            continue;
                    }
                    var label = FormatBlockLabel(idx + 1, idx + 1);
                    WriteInlineBlock(label, blocks[idx], inputs, baseTokens, tokenLists, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments, order, plainOutput, blockTokens);
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
                if (minBlockLenFilter > 0)
                {
                    var maxBlockLen = 0;
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var textLen = BuildBlockText(block, i, baseTokens, tokenLists, alignments, blockTokens).Length;
                        if (textLen > maxBlockLen)
                            maxBlockLen = textLen;
                    }
                    if (maxBlockLen < minBlockLenFilter)
                        continue;
                }
                for (int i = 0; i < inputs.Count; i++)
                {
                    var name = Path.GetFileName(inputs[i]);
                    var text = BuildBlockText(block, i, baseTokens, tokenLists, alignments, blockTokens);
                    if (text.Length == 0)
                        continue;

                    var opLabel = BuildBlockOpLabel(block, i, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments);
                    var display = EscapeBlockText(text);
                    if (plainOutput)
                    {
                        WritePlainLine(name, opLabel, display, text.Length);
                    }
                    else
                    {
                        if (i == 0)
                            Console.WriteLine($"[block {n}]");
                        Console.WriteLine($"  {name} {opLabel}: \"{display}\" (len={text.Length})");
                    }
                }
                if (!plainOutput)
                    Console.WriteLine();
                n++;
            }
        }

        private static void WriteInlineBlock(string label, VarBlockSlots block, List<string> inputs, List<string> baseTokens, List<List<string>> tokenLists, List<List<int>> tokenOpStartLists, List<List<int>> tokenOpEndLists, List<List<string>> tokenOpNames, List<TokenAlignment> alignments, string order, bool plainOutput, bool blockTokens)
        {
            bool blockFirst = IsBlockFirst(order);
            for (int i = 0; i < inputs.Count; i++)
            {
                var name = Path.GetFileName(inputs[i]);
                var text = BuildBlockText(block, i, baseTokens, tokenLists, alignments, blockTokens);
                if (text.Length == 0)
                    continue;

                var opLabel = BuildBlockOpLabel(block, i, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments);
                var labelOut = string.IsNullOrWhiteSpace(opLabel) ? label : opLabel;
                var display = EscapeBlockText(text);
                if (plainOutput)
                {
                    WritePlainLine(name, opLabel, display, text.Length);
                    continue;
                }
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

        private static void WritePlainLine(string name, string opLabel, string display, int length)
        {
            if (string.IsNullOrWhiteSpace(opLabel))
                Console.WriteLine($"{name}\t\"{display}\" (len={length})");
            else
                Console.WriteLine($"{name}\t{opLabel}\t\"{display}\" (len={length})");
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

        private static string BuildBlockText(VarBlockSlots block, int pdfIndex, List<string> baseTokens, List<List<string>> tokenLists, List<TokenAlignment> alignments, bool blockTokens)
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
                            AppendBlockToken(sb, otherTokens[tokenIdx], blockTokens);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= baseTokens.Count)
                        continue;
                    if (pdfIndex == 0)
                    {
                        AppendBlockToken(sb, baseTokens[tokenIdx], blockTokens);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherTokens = tokenLists[pdfIndex];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < otherTokens.Count)
                            AppendBlockToken(sb, otherTokens[otherIdx], blockTokens);
                    }
                }
            }

            return sb.ToString();
        }

        private static void AppendBlockToken(StringBuilder sb, string token, bool blockTokens)
        {
            if (string.IsNullOrEmpty(token))
                return;

            if (blockTokens)
            {
                token = NormalizeBlockToken(token);
                if (token.Length == 0)
                    return;
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(token[0]))
                    sb.Append(' ');
            }

            sb.Append(token);
        }

        private static string NormalizeBlockToken(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
                        sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string CollapseSpaces(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var sb = new StringBuilder(text.Length);
            bool inSpace = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!inSpace && sb.Length > 0)
                        sb.Append(' ');
                    inSpace = true;
                    continue;
                }
                sb.Append(c);
                inSpace = false;
            }
            return sb.ToString().Trim();
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
}
}
