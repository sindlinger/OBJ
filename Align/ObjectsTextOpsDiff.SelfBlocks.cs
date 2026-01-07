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

                if (IsTextShowingOperator(tok))
                {
                    var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                    var decoded = DequeueDecodedText(tok, operands, rawLine, textQueue) ?? "";
                    if (IsTextOpAllowed(tok, opFilter))
                    {
                        textOpIndex++;
                        if (currentTokens.Count == 0)
                            startOp = textOpIndex;
                        endOp = textOpIndex;
                        currentTokens.Add(decoded);
                        currentOps.Add(tok);
                    }

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
            out bool blocksSpecified,
            out bool selfMode,
            out int selfMinTokenLen,
            out int selfPatternMax,
            out int minTokenLenFilter,
            out int minBlockLenFilter,
            out bool minBlockLenSpecified,
            out int anchorsMinLen,
            out int anchorsMaxLen,
            out int anchorsMaxWords,
            out bool selfAnchors,
            out string rulesPathArg,
            out string rulesDoc,
            out string anchorsOut,
            out bool anchorsMerge,
            out bool plainOutput,
            out bool blockTokens,
            out bool blockTokensSpecified,
            out TokenMode tokenMode,
            out bool diffLineMode,
            out bool cleanupSemantic,
            out bool cleanupLossless,
            out bool cleanupEfficiency,
            out bool cleanupSpecified,
            out bool diffLineModeSpecified,
            out bool diffFullText,
            out bool includeLineBreaks,
            out bool includeTdLineBreaks,
            out string rangeStartRegex,
            out string rangeEndRegex,
            out int? rangeStartOp,
            out int? rangeEndOp,
            out bool dumpRangeText,
            out bool includeTmLineBreaks,
            out bool lineBreakAsSpace,
            out bool lineBreaksSpecified,
            out bool useLargestContents,
            out int contentsPage)
        {
            inputs = new List<string>();
            objId = 0;
            opFilter = new HashSet<string>(StringComparer.Ordinal);
            blocks = false;
            blocksInline = false;
            blocksOrder = "block-first";
            blockRange = (null, null);
            blocksSpecified = false;
            selfMode = false;
            selfMinTokenLen = 1;
            selfPatternMax = 1;
            minTokenLenFilter = 0;
            minBlockLenFilter = 0;
            minBlockLenSpecified = false;
            anchorsMinLen = 0;
            anchorsMaxLen = 0;
            anchorsMaxWords = 0;
            selfAnchors = false;
            rulesPathArg = "";
            rulesDoc = "";
            anchorsOut = "";
            anchorsMerge = false;
            plainOutput = false;
            blockTokens = false;
            blockTokensSpecified = false;
            tokenMode = TokenMode.Text;
            diffLineMode = false;
            diffLineModeSpecified = false;
            cleanupSemantic = false;
            cleanupLossless = false;
            cleanupEfficiency = false;
            cleanupSpecified = false;
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
            lineBreaksSpecified = false;
            useLargestContents = false;
            contentsPage = 0;

            var defaults = LoadObjDefaults();
            if (defaults != null)
            {
                if (!string.IsNullOrWhiteSpace(defaults.Doc))
                    rulesDoc = defaults.Doc.Trim();
                if (defaults.Ops != null && defaults.Ops.Count > 0)
                {
                    foreach (var op in defaults.Ops)
                    {
                        if (!string.IsNullOrWhiteSpace(op))
                            opFilter.Add(op.Trim());
                    }
                }
                if (defaults.Contents.HasValue && defaults.Contents.Value)
                    useLargestContents = true;
                if (defaults.Page.HasValue && defaults.Page.Value > 0)
                    contentsPage = defaults.Page.Value;
            }

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
                    blocksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--plain", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--no-internal", StringComparison.OrdinalIgnoreCase))
                {
                    plainOutput = true;
                    continue;
                }
                if (string.Equals(arg, "--block-tokens", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--tokens-block", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--tokens-line", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--line-tokens", StringComparison.OrdinalIgnoreCase))
                {
                    blockTokens = true;
                    blockTokensSpecified = true;
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
                    lineBreaksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks", StringComparison.OrdinalIgnoreCase))
                {
                    includeLineBreaks = true;
                    lineBreaksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks-td", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--break-td", StringComparison.OrdinalIgnoreCase))
                {
                    includeTdLineBreaks = true;
                    lineBreaksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks-tm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--break-tm", StringComparison.OrdinalIgnoreCase))
                {
                    includeTmLineBreaks = true;
                    lineBreaksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--line-breaks-space", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--break-space", StringComparison.OrdinalIgnoreCase))
                {
                    lineBreakAsSpace = true;
                    lineBreaksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--contents", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--page-contents", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--contents-largest", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--largest-contents", StringComparison.OrdinalIgnoreCase))
                {
                    useLargestContents = true;
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                        contentsPage = Math.Max(1, p);
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
                    diffLineModeSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--blocks-inline", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--block-inline", StringComparison.OrdinalIgnoreCase))
                {
                    blocks = true;
                    blocksInline = true;
                    blocksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--blocks-order", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    blocksOrder = args[++i];
                    blocksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--blocks-range", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    blocks = true;
                    blockRange = ParseBlockRange(args[++i]);
                    blocksSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--min-block-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out minBlockLenFilter);
                    minBlockLenSpecified = true;
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
                    cleanupSpecified = true;
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
                    cleanupSpecified = true;
                    cleanupSemantic = true;
                    continue;
                }
                if (string.Equals(arg, "--cleanup-lossless", StringComparison.OrdinalIgnoreCase))
                {
                    cleanupSpecified = true;
                    cleanupLossless = true;
                    continue;
                }
                if (string.Equals(arg, "--cleanup-efficiency", StringComparison.OrdinalIgnoreCase))
                {
                    cleanupSpecified = true;
                    cleanupEfficiency = true;
                    continue;
                }
                if ((string.Equals(arg, "--token-mode", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--token", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--by", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var raw = args[++i].Trim().ToLowerInvariant();
                    if (raw == "blocks" || raw == "block" || raw == "lines" || raw == "line")
                    {
                        blockTokens = true;
                        blockTokensSpecified = true;
                        tokenMode = TokenMode.Text;
                    }
                    else
                    {
                        blockTokens = false;
                        blockTokensSpecified = true;
                        tokenMode = raw switch
                        {
                            "ops" or "op" or "raw" or "bytes" => TokenMode.Ops,
                            _ => TokenMode.Text
                        };
                    }
                    continue;
                }
                if (string.Equals(arg, "--ops-diff", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--diff-ops", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--op-diff", StringComparison.OrdinalIgnoreCase))
                {
                    tokenMode = TokenMode.Ops;
                    blockTokens = false;
                    blockTokensSpecified = true;
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    var raw = arg.Trim();
                    if (raw.Contains(',', StringComparison.Ordinal))
                    {
                        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            inputs.Add(part.Trim());
                        continue;
                    }
                    if (Directory.Exists(raw))
                    {
                        inputs.AddRange(Directory.GetFiles(raw, "*.pdf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                        continue;
                    }
                    inputs.Add(raw);
                }
            }

            if (objId <= 0)
                useLargestContents = true;

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

        private sealed class ObjDefaultsFile
        {
            public int Version { get; set; } = 1;
            public string? Doc { get; set; }
            public List<string> Ops { get; set; } = new List<string>();
            public bool? Contents { get; set; }
            public int? Page { get; set; }
        }

        private static ObjDefaultsFile? LoadObjDefaults()
        {
            var path = ResolveObjDefaultsPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                using var reader = new StreamReader(path);
                return deserializer.Deserialize<ObjDefaultsFile>(reader);
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveObjDefaultsPath()
        {
            var cwd = Directory.GetCurrentDirectory();
            var exeBase = AppContext.BaseDirectory;
            foreach (var ext in new[] { ".yml", ".yaml" })
            {
                var file = "obj_defaults" + ext;
                var candidates = new[]
                {
                    Path.Combine(cwd, "configs", file),
                    Path.Combine(cwd, file),
                    Path.Combine(cwd, "..", "configs", file),
                    Path.Combine(cwd, "..", "..", "configs", file),
                    Path.GetFullPath(Path.Combine(exeBase, "../../../../configs", file))
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            return "";
        }

        private static string ResolveRoiDoc(string rulesDoc, int objId)
        {
            if (!string.IsNullOrWhiteSpace(rulesDoc))
                return rulesDoc.Trim();

            const string defaultDoc = "tjpb_despacho";
            var defaultPath = ResolveRoiPath(defaultDoc, objId);
            if (!string.IsNullOrWhiteSpace(defaultPath))
                return defaultDoc;
            return "";
        }

        private static string ResolveRoiPath(string roiDoc, int objId)
        {
            if (string.IsNullOrWhiteSpace(roiDoc) || objId <= 0)
                return "";

            var name = roiDoc.Trim();
            var baseName = $"{name}_obj{objId}_roi";
            var candidates = new List<string>();
            var cwd = Directory.GetCurrentDirectory();
            var exeBase = AppContext.BaseDirectory;

            foreach (var ext in new[] { ".yml", ".yaml" })
            {
                var file = baseName + ext;
                candidates.Add(Path.Combine(cwd, "configs", "textops_anchors", file));
                candidates.Add(Path.Combine(cwd, file));
                candidates.Add(Path.Combine(cwd, "..", "configs", "textops_anchors", file));
                candidates.Add(Path.Combine(cwd, "..", "..", "configs", "textops_anchors", file));
                candidates.Add(Path.GetFullPath(Path.Combine(exeBase, "../../../../configs/textops_anchors", file)));
            }

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return "";
        }

        private static string ResolveAnyRoiPath(int objId)
        {
            if (objId <= 0)
                return "";

            var cwd = Directory.GetCurrentDirectory();
            var exeBase = AppContext.BaseDirectory;
            var dirs = new List<string>
            {
                Path.Combine(cwd, "configs", "textops_anchors"),
                Path.Combine(cwd, "..", "configs", "textops_anchors"),
                Path.Combine(cwd, "..", "..", "configs", "textops_anchors"),
                Path.GetFullPath(Path.Combine(exeBase, "../../../../configs/textops_anchors"))
            };

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var ext in new[] { "yml", "yaml" })
                {
                    var pattern = $"*_obj{objId}_roi.{ext}";
                    var files = Directory.GetFiles(dir, pattern);
                    if (files.Length > 0)
                        return files[0];
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

        private static TextOpsRoiFile? LoadRoi(string roiPath)
        {
            if (string.IsNullOrWhiteSpace(roiPath) || !File.Exists(roiPath))
                return null;

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                using var reader = new StreamReader(roiPath);
                return deserializer.Deserialize<TextOpsRoiFile>(reader);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao carregar ROI: " + ex.Message);
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

        private static bool IsTextOpAllowed(string op, HashSet<string> opFilter)
        {
            if (opFilter == null || opFilter.Count == 0)
                return true;
            return opFilter.Contains(op);
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

        private sealed class TextOpsRoiFile
        {
            public int Version { get; set; } = 1;
            public string? Doc { get; set; }
            public int Obj { get; set; }
            public TextOpsRoiSection? FrontHead { get; set; }
            public TextOpsRoiSection? BackTail { get; set; }
        }

        private sealed class TextOpsRoiSection
        {
            public string? Label { get; set; }
            public List<TextOpsRoiRange> Ranges { get; set; } = new List<TextOpsRoiRange>();
        }

        private sealed class TextOpsRoiRange
        {
            public string? SourceFile { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string? Op { get; set; }
            public string? StartLabel { get; set; }
            public string? EndLabel { get; set; }
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


    }
}
