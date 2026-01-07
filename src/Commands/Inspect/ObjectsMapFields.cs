using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FilterPDF.TjpbDespachoExtractor.Utils;

namespace FilterPDF.Commands
{
    internal static class ObjectsMapFields
    {
        private static readonly string DefaultOutDir = Path.Combine("outputs", "fields");

        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var alignrangePath, out var mapPath, out var outDir, out var useFront, out var useBack, out var side))
            {
                ShowHelp();
                return;
            }

            if (string.IsNullOrWhiteSpace(alignrangePath))
            {
                ShowHelp();
                return;
            }

            alignrangePath = ResolveExistingPath(alignrangePath);
            if (!File.Exists(alignrangePath))
            {
                Console.WriteLine("Alignrange nao encontrado: " + alignrangePath);
                return;
            }

            if (string.IsNullOrWhiteSpace(mapPath))
            {
                ShowHelp();
                return;
            }

            mapPath = ResolveMapPath(mapPath);
            if (!File.Exists(mapPath))
            {
                Console.WriteLine("Mapa YAML nao encontrado: " + mapPath);
                return;
            }

            AlignRangeFile? alignFile;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                alignFile = deserializer.Deserialize<AlignRangeFile>(File.ReadAllText(alignrangePath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao ler alignrange: " + ex.Message);
                return;
            }

            if (alignFile == null)
            {
                Console.WriteLine("Alignrange vazio: " + alignrangePath);
                return;
            }

            AlignRangeFieldMap? map;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                map = deserializer.Deserialize<AlignRangeFieldMap>(File.ReadAllText(mapPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao ler map YAML: " + ex.Message);
                return;
            }

            if (map == null || map.Fields == null || map.Fields.Count == 0)
            {
                Console.WriteLine("Mapa vazio: " + mapPath);
                return;
            }

            if (!useFront && !useBack)
            {
                useFront = true;
                useBack = true;
            }

            var output = BuildOutput(alignFile, map, useFront, useBack, side);

            var json = JsonConvert.SerializeObject(output, Formatting.Indented);
            Console.WriteLine(json);

            if (string.IsNullOrWhiteSpace(outDir))
                outDir = DefaultOutDir;
            Directory.CreateDirectory(outDir);

            var baseName = Path.GetFileNameWithoutExtension(alignrangePath);
            var modeSuffix = useFront && useBack ? "both" : useFront ? "front" : "back";
            var sideSuffix = side == MapSide.Both ? "ab" : side == MapSide.A ? "a" : "b";
            var outFile = Path.Combine(outDir, $"{baseName}__mapfields_{modeSuffix}_{sideSuffix}.json");
            File.WriteAllText(outFile, json);
            Console.WriteLine("Arquivo salvo: " + outFile);
        }

        private static object BuildOutput(AlignRangeFile alignFile, AlignRangeFieldMap map, bool useFront, bool useBack, MapSide side)
        {
            var front = alignFile.FrontHead;
            var back = alignFile.BackTail;

            var frontA = BuildSegment("front_head", front?.OpRangeA, front?.ValueFullA);
            var frontB = BuildSegment("front_head", front?.OpRangeB, front?.ValueFullB);
            var backA = BuildSegment("back_tail", back?.OpRangeA, back?.ValueFullA);
            var backB = BuildSegment("back_tail", back?.OpRangeB, back?.ValueFullB);

            var segmentsA = new Dictionary<string, AlignSegment>
            {
                ["front_head"] = frontA,
                ["back_tail"] = backA
            };
            var segmentsB = new Dictionary<string, AlignSegment>
            {
                ["front_head"] = frontB,
                ["back_tail"] = backB
            };

            var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (useFront) filter.Add("front_head");
            if (useBack) filter.Add("back_tail");

            if (side == MapSide.A)
            {
                return ExtractFieldsForSide(map, segmentsA, filter, "pdf_a");
            }
            if (side == MapSide.B)
            {
                return ExtractFieldsForSide(map, segmentsB, filter, "pdf_b");
            }

            return new Dictionary<string, object>
            {
                ["pdf_a"] = ExtractFieldsForSide(map, segmentsA, filter, "pdf_a"),
                ["pdf_b"] = ExtractFieldsForSide(map, segmentsB, filter, "pdf_b")
            };
        }

        private static Dictionary<string, FieldOutput> ExtractFieldsForSide(
            AlignRangeFieldMap map,
            Dictionary<string, AlignSegment> segments,
            HashSet<string> allowedBands,
            string label)
        {
            var output = new Dictionary<string, FieldOutput>(StringComparer.OrdinalIgnoreCase);

            var nlpCache = new Dictionary<string, List<NlpEntity>>(StringComparer.OrdinalIgnoreCase);
            foreach (var band in segments.Keys)
            {
                var seg = segments[band];
                if (!allowedBands.Contains(band))
                    continue;
                if (seg == null || string.IsNullOrWhiteSpace(seg.ValueFull))
                    continue;
                nlpCache[band] = NlpLite.Annotate(seg.WorkText);
            }

            foreach (var kv in map.Fields)
            {
                var fieldName = kv.Key;
                var field = kv.Value;
                var result = new FieldOutput();

                var sources = field?.Sources?.Count > 0
                    ? field.Sources
                    : new List<AlignRangeSource> { new AlignRangeSource { Band = "front_head" } };

                string? chosenValue = null;
                string? chosenValueFull = null;
                string? chosenBand = null;

                foreach (var source in sources)
                {
                    var band = NormalizeBand(source.Band);
                    if (!allowedBands.Contains(band))
                        continue;

                    if (!segments.TryGetValue(band, out var seg))
                        continue;

                    if (seg == null || string.IsNullOrWhiteSpace(seg.ValueFull))
                    {
                        Console.WriteLine($"[{label}] Sem ValueFull em {band} para {fieldName}.");
                        if (chosenValueFull == null)
                        {
                            chosenValueFull = seg?.ValueFull ?? "";
                            chosenBand = band;
                        }
                        continue;
                    }

                    if (chosenValueFull == null)
                    {
                        chosenValueFull = seg.ValueFull;
                        chosenBand = band;
                    }

                    if (TryExtractFromNlp(source, nlpCache.GetValueOrDefault(band), out var nlpValue))
                    {
                        chosenValue = nlpValue;
                        chosenValueFull = seg.ValueFull;
                        chosenBand = band;
                        break;
                    }

                    if (TryExtractFromRegex(source, seg.WorkText, out var rxValue))
                    {
                        chosenValue = rxValue;
                        chosenValueFull = seg.ValueFull;
                        chosenBand = band;
                        break;
                    }
                }

                result.ValueFull = chosenValueFull ?? "";
                result.Value = NormalizeFieldValue(fieldName, chosenValue ?? "");
                result.Source = chosenBand ?? "";
                output[fieldName] = result;
            }

            return output;
        }

        private static AlignSegment BuildSegment(string band, string? opRange, string? valueFull)
        {
            var raw = valueFull ?? "";
            var normalized = TextUtils.NormalizeWhitespace(raw.Replace('\r', ' ').Replace('\n', ' '));
            return new AlignSegment
            {
                Band = band,
                OpRange = opRange ?? "",
                ValueFull = raw,
                WorkText = normalized
            };
        }

        private static bool TryExtractFromNlp(AlignRangeSource source, List<NlpEntity>? entities, out string value)
        {
            value = "";
            if (source?.NlpLabels == null || source.NlpLabels.Count == 0 || entities == null || entities.Count == 0)
                return false;

            foreach (var label in source.NlpLabels)
            {
                var hit = entities.FirstOrDefault(e => e.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                    continue;
                value = hit.Text?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return true;
            }

            return false;
        }

        private static bool TryExtractFromRegex(AlignRangeSource source, string text, out string value)
        {
            value = "";
            if (source?.Regex == null || source.Regex.Count == 0)
                return false;

            foreach (var rule in source.Regex)
            {
                if (string.IsNullOrWhiteSpace(rule.Pattern))
                    continue;

                Regex rx;
                try
                {
                    rx = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                }
                catch
                {
                    continue;
                }

                var m = rx.Match(text ?? "");
                if (!m.Success)
                    continue;

                var groupIndex = rule.Group ?? (m.Groups.Count > 1 ? 1 : 0);
                if (groupIndex < 0 || groupIndex >= m.Groups.Count)
                    continue;

                var g = m.Groups[groupIndex];
                if (!g.Success)
                    continue;

                value = g.Value?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return true;
            }

            return false;
        }

        private static string NormalizeFieldValue(string fieldName, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var trimmed = value.Trim().Trim(',', ';', '.', ':');

            if (fieldName.Contains("CPF", StringComparison.OrdinalIgnoreCase))
            {
                return FormatCpf(trimmed);
            }

            if (fieldName.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase))
            {
                return TextUtils.NormalizeWhitespace(trimmed);
            }

            return TextUtils.NormalizeWhitespace(trimmed);
        }

        private static string FormatCpf(string raw)
        {
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length != 11) return raw;
            return $"{digits.Substring(0, 3)}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits.Substring(9, 2)}";
        }

        private static string NormalizeBand(string band)
        {
            if (string.IsNullOrWhiteSpace(band)) return "front_head";
            var norm = band.Trim().ToLowerInvariant();
            if (norm == "front" || norm == "head") return "front_head";
            if (norm == "back" || norm == "tail") return "back_tail";
            return norm;
        }

        private static bool ParseOptions(
            string[] args,
            out string alignrangePath,
            out string mapPath,
            out string outDir,
            out bool useFront,
            out bool useBack,
            out MapSide side)
        {
            alignrangePath = "";
            mapPath = "";
            outDir = "";
            useFront = false;
            useBack = false;
            side = MapSide.Both;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                    return false;

                if (string.Equals(arg, "--alignrange", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    alignrangePath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    mapPath = args[++i];
                    continue;
                }
                if ((string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--out-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--front", StringComparison.OrdinalIgnoreCase))
                {
                    useFront = true;
                    continue;
                }
                if (string.Equals(arg, "--back", StringComparison.OrdinalIgnoreCase))
                {
                    useBack = true;
                    continue;
                }
                if (string.Equals(arg, "--both", StringComparison.OrdinalIgnoreCase))
                {
                    useFront = true;
                    useBack = true;
                    continue;
                }
                if (string.Equals(arg, "--side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    side = ParseSide(args[++i]);
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(alignrangePath))
                        alignrangePath = arg;
                    else if (string.IsNullOrWhiteSpace(mapPath))
                        mapPath = arg;
                }
            }

            return true;
        }

        private static MapSide ParseSide(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return MapSide.Both;
            var t = raw.Trim().ToLowerInvariant();
            return t switch
            {
                "a" => MapSide.A,
                "b" => MapSide.B,
                "both" => MapSide.Both,
                "ab" => MapSide.Both,
                _ => MapSide.Both
            };
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf.exe objects mapfields --alignrange <arquivo.txt> --map <map.yml> [--front|--back|--both] [--side a|b|both]");
            Console.WriteLine("  --out <dir>    (opcional, default outputs/fields)");
        }

        private static string ResolveMapPath(string mapPath)
        {
            if (File.Exists(mapPath)) return mapPath;
            var cwd = Directory.GetCurrentDirectory();
            var candidates = new List<string>
            {
                Path.Combine(cwd, mapPath),
                Path.Combine(cwd, "configs", "alignrange_fields", mapPath),
                Path.Combine(cwd, "configs", "textops_fields", mapPath),
                Path.Combine(cwd, "ExtractFields", mapPath)
            };

            if (!mapPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !mapPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(cwd, "configs", "alignrange_fields", mapPath + ".yml"));
                candidates.Add(Path.Combine(cwd, "configs", "alignrange_fields", mapPath + ".yaml"));
                candidates.Add(Path.Combine(cwd, "configs", "textops_fields", mapPath + ".yml"));
                candidates.Add(Path.Combine(cwd, "configs", "textops_fields", mapPath + ".yaml"));
                candidates.Add(Path.Combine(cwd, "ExtractFields", mapPath + ".yml"));
                candidates.Add(Path.Combine(cwd, "ExtractFields", mapPath + ".yaml"));
            }

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }

            return mapPath;
        }

        private static string ResolveExistingPath(string path)
        {
            if (File.Exists(path)) return path;
            var cwd = Directory.GetCurrentDirectory();
            var full = Path.Combine(cwd, path);
            return File.Exists(full) ? full : path;
        }

        private enum MapSide
        {
            A,
            B,
            Both
        }

        private sealed class AlignRangeFile
        {
            public AlignRangeSection? FrontHead { get; set; }
            public AlignRangeSection? BackTail { get; set; }
        }

        private sealed class AlignRangeSection
        {
            public string PdfA { get; set; } = "";
            public string OpRangeA { get; set; } = "";
            public string ValueFullA { get; set; } = "";
            public string PdfB { get; set; } = "";
            public string OpRangeB { get; set; } = "";
            public string ValueFullB { get; set; } = "";
        }

        private sealed class AlignSegment
        {
            public string Band { get; set; } = "";
            public string OpRange { get; set; } = "";
            public string ValueFull { get; set; } = "";
            public string WorkText { get; set; } = "";
        }

        private sealed class AlignRangeFieldMap
        {
            public int Version { get; set; } = 1;
            public string Doc { get; set; } = "";
            public Dictionary<string, AlignRangeField> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class AlignRangeField
        {
            public List<AlignRangeSource> Sources { get; set; } = new();
        }

        private sealed class AlignRangeSource
        {
            public string Band { get; set; } = "front_head";
            public List<string> NlpLabels { get; set; } = new();
            public List<RegexRule> Regex { get; set; } = new();
        }

        private sealed class RegexRule
        {
            public string Pattern { get; set; } = "";
            public int? Group { get; set; }
        }

        private sealed class FieldOutput
        {
            public string ValueFull { get; set; } = "";
            public string Value { get; set; } = "";
            public string Source { get; set; } = "";
        }

        private sealed class NlpEntity
        {
            public NlpEntity(string label, int start, int end, string text)
            {
                Label = label;
                Start = start;
                End = end;
                Text = text;
            }

            public string Label { get; }
            public int Start { get; }
            public int End { get; }
            public string Text { get; }
        }

        private static class NlpLite
        {
            private static readonly Regex CpfRegex = new(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex CnpjRegex = new(@"\b\d{2}\.?\d{3}\.?\d{3}/\d{4}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex CnjLooseRegex = new(@"\b\d{7}-?\d{2}[.\-]?\d{4}[.\-]?\d[.\-]?\d{2}(?:[.\-]?\d{4})?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex MoneyRegex = new(@"\b(?:R\$\s*)?\d{1,3}(?:\.\d{3})*,\d{2}\b|\b(?:R\$\s*)?\d+,\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex DateRegex = new(@"\b(?:\d{1,2}/\d{1,2}/\d{4}|\d{1,2}\s+de\s+(?:janeiro|fevereiro|março|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+\d{4})\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            private static readonly Regex EmailRegex = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

            public static List<NlpEntity> Annotate(string text)
            {
                var entities = new List<NlpEntity>();
                if (string.IsNullOrWhiteSpace(text))
                    return entities;

                AddMatches(entities, "CPF", CpfRegex, text);
                AddMatches(entities, "CNPJ", CnpjRegex, text);
                AddCnjMatches(entities, text);
                AddMatches(entities, "VALOR", MoneyRegex, text);
                AddMatches(entities, "DATA", DateRegex, text);
                AddMatches(entities, "EMAIL", EmailRegex, text);

                entities = entities
                    .OrderBy(e => e.Start)
                    .ThenByDescending(e => e.End - e.Start)
                    .ToList();

                var filtered = new List<NlpEntity>();
                var lastEnd = -1;
                foreach (var entity in entities)
                {
                    if (entity.Start < lastEnd)
                        continue;
                    filtered.Add(entity);
                    lastEnd = entity.End;
                }

                return filtered;
            }

            private static void AddMatches(List<NlpEntity> entities, string label, Regex regex, string text)
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (!match.Success)
                        continue;
                    entities.Add(new NlpEntity(label, match.Index, match.Index + match.Length, match.Value));
                }
            }

            private static void AddCnjMatches(List<NlpEntity> entities, string text)
            {
                foreach (Match match in CnjLooseRegex.Matches(text))
                {
                    if (!match.Success)
                        continue;
                    var digits = new string(match.Value.Where(char.IsDigit).ToArray());
                    var label = digits.Length >= 20 ? "CNJ" : "CNJ_PARTIAL";
                    entities.Add(new NlpEntity(label, match.Index, match.Index + match.Length, match.Value));
                }
            }
        }
    }
}
