using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using FilterPDF.TjpbDespachoExtractor.Config;
using FilterPDF.TjpbDespachoExtractor.Extraction;
using FilterPDF.TjpbDespachoExtractor.Reference;
using FilterPDF.TjpbDespachoExtractor.Utils;

namespace FilterPDF.Commands
{
    internal static class ObjectsTextOpsExtractFields
    {
        public static void Execute(string[] args)
        {
            if (!ParseOptions(
                    args,
                    out var inputFile,
                    out var mapPath,
                    out var outPath,
                    out var fieldsFilter,
                    out var validate,
                    out var asJson,
                    out var codes,
                    out var applyRules,
                    out var configPath,
                    out var useAnchors,
                    out var requireAnchors,
                    out var requireVariable,
                    out var requireFixed,
                    out var minTokenLen,
                    out var maxTokenLen,
                    out var minTextLen,
                    out var maxTextLen,
                    out var selfMinTokenLen,
                    out var selfPatternMax,
                    out var rulesPathArg,
                    out var rulesDoc))
                return;
            if (string.IsNullOrWhiteSpace(inputFile))
            {
                ShowHelp();
                return;
            }
            if (string.IsNullOrWhiteSpace(mapPath))
            {
                if (fieldsFilter.Count == 1)
                {
                    mapPath = fieldsFilter.First();
                }
                else
                {
                    ShowHelp();
                    return;
                }
            }

            mapPath = ResolveMapPath(mapPath);
            if (!File.Exists(mapPath))
            {
                Console.WriteLine("Mapa YAML nao encontrado: " + mapPath);
                return;
            }

            TextOpsFieldMap? map;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                map = deserializer.Deserialize<TextOpsFieldMap>(File.ReadAllText(mapPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao ler YAML: " + ex.Message);
                return;
            }

            if (map == null || map.Fields == null || map.Fields.Count == 0)
            {
                Console.WriteLine("Mapa vazio: " + mapPath);
                return;
            }

            if (requireVariable && requireFixed)
            {
                Console.WriteLine("Escolha apenas um: --self-variable ou --self-fixed.");
                return;
            }

            var requestedFields = new HashSet<string>(fieldsFilter, StringComparer.OrdinalIgnoreCase);
            var effectiveFields = new HashSet<string>(fieldsFilter, StringComparer.OrdinalIgnoreCase);
            var needsEspecieDerive = (requestedFields.Count == 0 && map.Fields.ContainsKey("ESPECIE_DA_PERICIA")) ||
                                     requestedFields.Contains("ESPECIE_DA_PERICIA");
            var needsEspecialidadeFormat = (requestedFields.Count == 0 && map.Fields.ContainsKey("ESPECIALIDADE")) ||
                                           requestedFields.Contains("ESPECIALIDADE");
            if (needsEspecieDerive)
            {
                var deps = new[] { "PERITO", "CPF_PERITO", "ESPECIALIDADE", "VALOR_ARBITRADO_JZ", "VALOR_ARBITRADO_DE" };
                EnsureFieldDependencies(map, deps);
                foreach (var dep in deps)
                    effectiveFields.Add(dep);
            }

            int defaultObjId = map.Obj;
            var opFilter = new HashSet<string>(StringComparer.Ordinal);
            if (map.Ops != null)
            {
                foreach (var op in map.Ops)
                {
                    if (!string.IsNullOrWhiteSpace(op))
                        opFilter.Add(op.Trim());
                }
            }

            TextOpsRules? selfRules = null;
            var rulesDocName = !string.IsNullOrWhiteSpace(rulesDoc) ? rulesDoc : map.Doc;
            var rulesPath = ResolveRulesPath(rulesPathArg, rulesDocName);
            if (!string.IsNullOrWhiteSpace(rulesPath) && File.Exists(rulesPath))
            {
                try
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    var rulesFile = deserializer.Deserialize<TextOpsRulesFile>(File.ReadAllText(rulesPath));
                    selfRules = rulesFile?.Self;
                }
                catch (Exception ex)
                {
                    if (!string.IsNullOrWhiteSpace(rulesPathArg) || !string.IsNullOrWhiteSpace(rulesDoc))
                        Console.WriteLine("Falha ao carregar rules textops: " + ex.Message);
                }
            }
            else if (!string.IsNullOrWhiteSpace(rulesPathArg) || !string.IsNullOrWhiteSpace(rulesDoc))
            {
                Console.WriteLine("Regras de textops nao encontradas: " + rulesPath);
            }

            TjpbDespachoConfig? cfg = null;
            FieldStrategyEngine? rulesEngine = null;
            if (applyRules || needsEspecieDerive || needsEspecialidadeFormat)
            {
                try
                {
                    var cfgPath = ResolveConfigPath(configPath);
                    cfg = TjpbDespachoConfig.Load(cfgPath);
                    if (applyRules)
                        rulesEngine = new FieldStrategyEngine(cfg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Falha ao carregar rules/config: " + ex.Message);
                    if (applyRules)
                        return;
                }
            }

            PeritoCatalog? peritoCatalog = null;
            HonorariosTable? honorariosTable = null;
            if (cfg != null && (needsEspecieDerive || needsEspecialidadeFormat))
            {
                honorariosTable = new HonorariosTable(cfg.Reference.Honorarios, cfg.BaseDir);
                if (needsEspecieDerive)
                    peritoCatalog = PeritoCatalog.Load(cfg.BaseDir, cfg.Reference.PeritosCatalogPaths);
            }

            var objIds = new HashSet<int>();
            if (defaultObjId > 0)
                objIds.Add(defaultObjId);
            foreach (var field in map.Fields.Values)
            {
                if (field?.Candidates == null) continue;
                foreach (var cand in field.Candidates)
                {
                    if (cand.Obj > 0)
                        objIds.Add(cand.Obj);
                }
            }

            if (objIds.Count == 0)
            {
                Console.WriteLine("Mapa sem obj valido (obj <= 0) e sem candidatos com obj.");
                return;
            }

            using var doc = new PdfDocument(new PdfReader(inputFile));
            var contexts = new Dictionary<int, ObjContext>();
            foreach (var objId in objIds.OrderBy(id => id))
            {
                var found = FindStreamAndResourcesByObjId(doc, objId);
                if (found.Stream == null || found.Resources == null)
                {
                    contexts[objId] = new ObjContext(null, null, new List<TextOpEntry>());
                    continue;
                }
                var entries = CollectTextOpEntries(found.Stream, found.Resources);
                contexts[objId] = new ObjContext(found.Stream, found.Resources, entries);
            }

            var requireSelf = requireVariable || requireFixed;
            var selfByObj = new Dictionary<int, SelfClassification>();
            if (requireSelf)
            {
                foreach (var kv in contexts)
                {
                    if (kv.Value.Stream == null || kv.Value.Resources == null)
                    {
                        selfByObj[kv.Key] = new SelfClassification(new List<SelfBlock>(), new List<SelfBlock>());
                        continue;
                    }
                    var blocks = ExtractSelfBlocks(kv.Value.Stream, kv.Value.Resources, opFilter);
                    var classified = ClassifySelfBlocks(blocks, selfMinTokenLen, selfPatternMax, selfRules);
                    selfByObj[kv.Key] = new SelfClassification(classified.Variable, classified.Fixed);
                }
            }

            var results = new List<FieldExtractResult>();
            foreach (var kv in map.Fields)
            {
                var fieldName = kv.Key;
                if (effectiveFields.Count > 0 && !effectiveFields.Contains(fieldName))
                    continue;

                var field = kv.Value;
                var fieldResult = new FieldExtractResult { Field = fieldName };
                if (field?.Candidates == null || field.Candidates.Count == 0)
                {
                    fieldResult.Status = "NO_CANDIDATES";
                    results.Add(fieldResult);
                    continue;
                }

                var fileName = Path.GetFileName(inputFile);
                var matchFile = field.Candidates
                    .Where(c => !string.IsNullOrWhiteSpace(c.SourceFile) && string.Equals(Path.GetFileName(c.SourceFile), fileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var generic = field.Candidates
                    .Where(c => string.IsNullOrWhiteSpace(c.SourceFile))
                    .ToList();
                var candidatesToUse = matchFile.Count > 0 ? matchFile : generic;

                FieldExtractResult? fallback = null;
                foreach (var cand in candidatesToUse)
                {
                    var objId = cand.Obj > 0 ? cand.Obj : defaultObjId;
                    if (objId <= 0)
                    {
                        if (validate)
                        {
                            fieldResult.Validation.Add(new CandidateValidation
                            {
                                OpRange = cand.OpRange,
                                Status = "FAIL",
                                Reason = "obj_invalido"
                            });
                        }
                        continue;
                    }
                    if (!contexts.TryGetValue(objId, out var ctx) || ctx.Entries.Count == 0)
                    {
                        if (validate)
                        {
                            fieldResult.Validation.Add(new CandidateValidation
                            {
                                OpRange = cand.OpRange,
                                Status = "FAIL",
                                Reason = "obj_nao_encontrado"
                            });
                        }
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(cand.OpRange))
                        continue;
                    if (!TryParseOpRange(cand.OpRange, out var start, out var end, out var op))
                        continue;

                    var entriesForObj = ctx.Entries;
                    var rangeOk = TryExtractRange(entriesForObj, start, end, op, out var fullValue, out var reason);
                    var ok = rangeOk;
                    var refinedStart = start;
                    var refinedEnd = end;
                    var value = fullValue;
                    if (rangeOk && !string.IsNullOrWhiteSpace(cand.MatchRegex))
                    {
                        ok = TryExtractRangeWithRegex(entriesForObj, start, end, op, cand.MatchRegex, cand.MatchGroup, out refinedStart, out refinedEnd, out value, out reason);
                    }
                    var finalStart = refinedStart;
                    var finalEnd = refinedEnd;
                    var finalOpRange = cand.OpRange;
                    if (ok && !string.IsNullOrWhiteSpace(cand.MatchRegex))
                    {
                        var refinedOpRange = FormatOpRange(refinedStart, refinedEnd, op);
                        if (!string.Equals(refinedOpRange, cand.OpRange, StringComparison.OrdinalIgnoreCase))
                            finalOpRange = refinedOpRange;
                    }

                    if (ok && useAnchors)
                    {
                        var anchorsOk = CheckAnchors(entriesForObj, cand, requireAnchors, out var anchorReason);
                        if (!anchorsOk)
                        {
                            ok = false;
                            reason = anchorReason;
                        }
                    }

                    if (ok && (minTextLen > 0 || maxTextLen > 0))
                    {
                        var len = value?.Length ?? 0;
                        if (minTextLen > 0 && len < minTextLen)
                        {
                            ok = false;
                            reason = "min_text_len";
                        }
                        else if (maxTextLen > 0 && len > maxTextLen)
                        {
                            ok = false;
                            reason = "max_text_len";
                        }
                    }

                    if (ok && (minTokenLen > 0 || maxTokenLen > 0))
                    {
                        var maxWordLen = GetMaxWordTokenLen(value ?? "");
                        if (minTokenLen > 0 && maxWordLen < minTokenLen)
                        {
                            ok = false;
                            reason = "min_token_len";
                        }
                        else if (maxTokenLen > 0 && maxWordLen > maxTokenLen)
                        {
                            ok = false;
                            reason = "max_token_len";
                        }
                    }

                    if (ok && requireSelf)
                    {
                        if (!selfByObj.TryGetValue(objId, out var self))
                        {
                            ok = false;
                            reason = "self_class_unavailable";
                        }
                        else
                        {
                        var inVar = IsRangeInBlocks(self.Variable, finalStart, finalEnd);
                        var inFixed = IsRangeInBlocks(self.Fixed, finalStart, finalEnd);
                            if (requireVariable && !inVar)
                            {
                                ok = false;
                                reason = "self_not_variable";
                            }
                            else if (requireFixed && !inFixed)
                            {
                                ok = false;
                                reason = "self_not_fixed";
                            }
                            else if (!inVar && !inFixed)
                            {
                                ok = false;
                                reason = "self_block_missing";
                            }
                        }
                    }

                    if (ok && applyRules && rulesEngine != null)
                    {
                        if (TryApplyFieldRules(rulesEngine, fieldName, value ?? "", out var cleaned))
                            value = cleaned;
                    }

                    if (validate)
                    {
                        fieldResult.Validation.Add(new CandidateValidation
                        {
                            OpRange = cand.OpRange,
                            Status = ok ? "OK" : "FAIL",
                            Reason = reason
                        });
                    }

                    if (!ok)
                    {
                        if (rangeOk && fallback == null && !string.IsNullOrWhiteSpace(fullValue))
                        {
                            var fallbackOpRange = FormatOpRange(start, end, op);
                            var status = reason.StartsWith("regex_", StringComparison.Ordinal) ? "REGEX_FAIL" : "RANGE_FAIL";
                            fallback = new FieldExtractResult
                            {
                                Field = fieldName,
                                Status = status,
                                OpRange = fallbackOpRange,
                                SourceOpRange = string.Equals(fallbackOpRange, cand.OpRange, StringComparison.OrdinalIgnoreCase) ? null : cand.OpRange,
                                ValueFull = fullValue,
                                ValueFullLen = fullValue?.Length ?? 0,
                                Value = null,
                                ValueLen = 0,
                                PrevFixed = ExtractAnchor(entriesForObj, cand.PrevOpRange),
                                NextFixed = ExtractAnchor(entriesForObj, cand.NextOpRange),
                                CodeLines = codes ? BuildCodeLines(entriesForObj, start, end) : new List<string>()
                            };
                        }
                        continue;
                    }

                    fieldResult.Status = "OK";
                    fieldResult.OpRange = finalOpRange;
                    if (!string.Equals(finalOpRange, cand.OpRange, StringComparison.OrdinalIgnoreCase))
                        fieldResult.SourceOpRange = cand.OpRange;
                    fieldResult.Value = value;
                    fieldResult.ValueLen = value?.Length ?? 0;
                    fieldResult.ValueRaw = value;
                    fieldResult.ValueRawLen = value?.Length ?? 0;
                    fieldResult.ValueFull = fullValue;
                    fieldResult.ValueFullLen = fullValue?.Length ?? 0;
                    fieldResult.PrevFixed = ExtractAnchor(entriesForObj, cand.PrevOpRange);
                    fieldResult.NextFixed = ExtractAnchor(entriesForObj, cand.NextOpRange);
                    if (codes)
                        fieldResult.CodeLines = BuildCodeLines(entriesForObj, finalStart, finalEnd);
                    break;
                }

                if (string.IsNullOrWhiteSpace(fieldResult.Status))
                {
                    if (fallback != null)
                    {
                        fieldResult.Status = fallback.Status;
                        fieldResult.OpRange = fallback.OpRange;
                        fieldResult.SourceOpRange = fallback.SourceOpRange;
                        fieldResult.ValueFull = fallback.ValueFull;
                        fieldResult.ValueFullLen = fallback.ValueFullLen;
                        fieldResult.Value = fallback.Value;
                        fieldResult.ValueLen = fallback.ValueLen;
                        fieldResult.PrevFixed = fallback.PrevFixed;
                        fieldResult.NextFixed = fallback.NextFixed;
                        if (codes && fallback.CodeLines != null && fallback.CodeLines.Count > 0)
                            fieldResult.CodeLines = fallback.CodeLines;
                    }
                    else
                    {
                        fieldResult.Status = "NOT_FOUND";
                    }
                }

                results.Add(fieldResult);
            }

            if (needsEspecialidadeFormat && honorariosTable != null)
                ApplyEspecialidadeFormat(results, honorariosTable);

            if (needsEspecieDerive && peritoCatalog != null && honorariosTable != null)
                ApplyDerivedEspecie(results, peritoCatalog, honorariosTable);

            if (requestedFields.Count > 0)
                results = results.Where(r => requestedFields.Contains(r.Field)).ToList();

            var objLabel = objIds.Count == 1
                ? objIds.First().ToString(CultureInfo.InvariantCulture)
                : string.Join(",", objIds.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)));

            if (asJson)
            {
                var payload = new
                {
                    input = Path.GetFileName(inputFile),
                    obj = objLabel,
                    fields = results
                };
                var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                WriteOutput(json, outPath);
            }
            else
            {
                var lines = new List<string>();
                lines.Add($"[extract] {Path.GetFileName(inputFile)} obj={objLabel}");
                foreach (var r in results)
                {
                    var val = r.Value ?? "";
                    var display = r.Status == "OK" ? val : $"[{r.Status}]";
                    lines.Add($"{r.Field} = {display}");
                    if (validate && r.Validation.Count > 0)
                    {
                        foreach (var v in r.Validation)
                            lines.Add($"  - {v.OpRange}: {v.Status}{(string.IsNullOrWhiteSpace(v.Reason) ? "" : " (" + v.Reason + ")")}");
                    }
                    if (codes && r.CodeLines != null && r.CodeLines.Count > 0)
                    {
                        lines.Add("  codes:");
                        foreach (var line in r.CodeLines)
                            lines.Add("    " + line);
                    }
                }
                WriteOutput(string.Join(Environment.NewLine, lines), outPath);
            }
        }

        private static void WriteOutput(string content, string? outPath)
        {
            if (string.IsNullOrWhiteSpace(outPath))
            {
                Console.WriteLine(content);
                return;
            }

            File.WriteAllText(outPath, content);
            Console.WriteLine("Saida salva em: " + outPath);
        }

        private static void ApplyDerivedEspecie(List<FieldExtractResult> results, PeritoCatalog peritoCatalog, HonorariosTable honorariosTable)
        {
            if (results == null || results.Count == 0) return;

            var perito = FindResult(results, "PERITO");
            var cpf = FindResult(results, "CPF_PERITO");
            var especialidade = FindResult(results, "ESPECIALIDADE");
            var especie = FindResult(results, "ESPECIE_DA_PERICIA");

            var nome = perito?.Value ?? "";
            var cpfVal = cpf?.Value ?? "";
            if (!IsValueOk(especialidade) || LooksWeakEspecialidade(especialidade?.Value ?? ""))
            {
                if (peritoCatalog.TryResolve(nome, cpfVal, out var info, out _))
                {
                    if (!string.IsNullOrWhiteSpace(info.Especialidade))
                    {
                        especialidade = EnsureResult(results, "ESPECIALIDADE", especialidade);
                        SetDerivedValue(especialidade, info.Especialidade, "OK_DERIVED", PickValueFullSource(especialidade, perito, cpf));
                    }
                }
            }

            var espValue = especialidade?.Value ?? "";
            if (string.IsNullOrWhiteSpace(espValue)) return;

            var valorBase = FirstOkResult(results, "VALOR_ARBITRADO_JZ") ?? FirstOkResult(results, "VALOR_ARBITRADO_DE");
            if (valorBase == null || string.IsNullOrWhiteSpace(valorBase.Value)) return;
            if (!TextUtils.TryParseMoney(valorBase.Value, out var valor)) return;

            if (!honorariosTable.TryMatch(espValue, valor, out var entry, out _))
                return;

            if (string.IsNullOrWhiteSpace(entry.Descricao)) return;
            especie = EnsureResult(results, "ESPECIE_DA_PERICIA", especie);
            SetDerivedValue(especie, entry.Descricao, "OK_DERIVED", PickValueFullSource(especie, especialidade, perito, valorBase));
        }

        private static void ApplyEspecialidadeFormat(List<FieldExtractResult> results, HonorariosTable honorariosTable)
        {
            foreach (var r in results)
            {
                if (!string.Equals(r.Field, "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(r.Value))
                    continue;

                if (string.IsNullOrWhiteSpace(r.ValueRaw))
                {
                    r.ValueRaw = r.Value;
                    r.ValueRawLen = r.ValueRaw?.Length ?? 0;
                }

                if (honorariosTable.TryResolveAreaFromText(r.Value, out var area, out _))
                {
                    r.Value = area;
                    r.ValueLen = area?.Length ?? 0;
                }
            }
        }

        private static void EnsureFieldDependencies(TextOpsFieldMap map, IEnumerable<string> fields)
        {
            foreach (var field in fields)
            {
                if (map.Fields.ContainsKey(field))
                    continue;
                var loaded = TryLoadFieldFromMap(field);
                if (loaded != null)
                    map.Fields[field] = loaded;
            }
        }

        private static TextOpsField? TryLoadFieldFromMap(string field)
        {
            var path = ResolveMapPath(field);
            if (!File.Exists(path)) return null;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var map = deserializer.Deserialize<TextOpsFieldMap>(File.ReadAllText(path));
                if (map?.Fields == null) return null;
                return map.Fields.TryGetValue(field, out var loaded) ? loaded : null;
            }
            catch
            {
                return null;
            }
        }

        private static FieldExtractResult? FindResult(List<FieldExtractResult> results, string field)
        {
            return results.FirstOrDefault(r => string.Equals(r.Field, field, StringComparison.OrdinalIgnoreCase));
        }

        private static FieldExtractResult? FirstOkResult(List<FieldExtractResult> results, string field)
        {
            var r = FindResult(results, field);
            return IsValueOk(r) ? r : null;
        }

        private static bool IsValueOk(FieldExtractResult? result)
        {
            return result != null &&
                   string.Equals(result.Status, "OK", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(result.Value);
        }

        private static FieldExtractResult EnsureResult(List<FieldExtractResult> results, string field, FieldExtractResult? existing)
        {
            if (existing != null) return existing;
            var created = new FieldExtractResult { Field = field, Status = "NOT_FOUND" };
            results.Add(created);
            return created;
        }

        private static FieldExtractResult? PickValueFullSource(params FieldExtractResult?[] candidates)
        {
            foreach (var c in candidates)
            {
                if (c != null && !string.IsNullOrWhiteSpace(c.ValueFull))
                    return c;
            }
            return null;
        }

        private static void SetDerivedValue(FieldExtractResult target, string value, string status, FieldExtractResult? source)
        {
            target.Status = status;
            target.Value = value;
            target.ValueLen = value?.Length ?? 0;
            if (string.IsNullOrWhiteSpace(target.ValueFull) && source != null)
            {
                target.ValueFull = source.ValueFull;
                if (string.IsNullOrWhiteSpace(target.OpRange))
                    target.OpRange = source.OpRange;
                if (string.IsNullOrWhiteSpace(target.SourceOpRange))
                    target.SourceOpRange = source.SourceOpRange;
                target.PrevFixed ??= source.PrevFixed;
                target.NextFixed ??= source.NextFixed;
            }
            target.ValueFullLen = target.ValueFull?.Length ?? 0;
        }

        private static bool LooksWeakEspecialidade(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var norm = TextUtils.NormalizeForMatch(value);
            if (norm.Length <= 6) return true;
            var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 1 && (norm == "engenheiro" || norm == "medico" || norm == "medica" || norm == "psicologo" || norm == "psicologa"))
                return true;
            return false;
        }

        private static bool TryExtractRange(List<TextOpEntry> entries, int start, int end, string op, out string value, out string reason)
        {
            value = "";
            reason = "";
            if (start <= 0 || end < 0)
            {
                reason = "range_invalido";
                return false;
            }
            if (end == 0)
                end = entries.Count;
            if (end < start)
            {
                reason = "range_invalido";
                return false;
            }
            if (end > entries.Count)
            {
                reason = "range_fora_do_stream";
                return false;
            }

            for (int i = start; i <= end; i++)
            {
                var entry = entries[i - 1];
                if (!string.IsNullOrWhiteSpace(op) && !IsOpWildcard(op) && !string.Equals(entry.Op, op, StringComparison.Ordinal))
                {
                    reason = "op_mismatch";
                    return false;
                }
            }

            var sb = new System.Text.StringBuilder();
            for (int i = start; i <= end; i++)
                sb.Append(entries[i - 1].Text);

            value = sb.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                reason = "texto_vazio";
                return false;
            }
            return true;
        }

        private static bool TryExtractRangeWithRegex(List<TextOpEntry> entries, int start, int end, string op, string? matchRegex, int? matchGroup,
            out int refinedStart, out int refinedEnd, out string value, out string reason)
        {
            refinedStart = start;
            refinedEnd = end;
            value = "";
            reason = "";

            if (end == 0)
                end = entries.Count;

            if (string.IsNullOrWhiteSpace(matchRegex))
            {
                return TryExtractRange(entries, start, end, op, out value, out reason);
            }

            if (!TryExtractRange(entries, start, end, op, out var fullText, out reason))
                return false;

            Regex rx;
            try
            {
                rx = new Regex(matchRegex, RegexOptions.Singleline);
            }
            catch
            {
                reason = "regex_invalido";
                return false;
            }

            var match = rx.Match(fullText);
            if (!match.Success)
            {
                reason = "regex_sem_match";
                return false;
            }

            int groupIndex = matchGroup ?? (match.Groups.Count > 1 ? 1 : 0);
            if (groupIndex < 0 || groupIndex >= match.Groups.Count)
            {
                reason = "regex_grupo_invalido";
                return false;
            }

            var group = match.Groups[groupIndex];
            if (!group.Success || group.Length == 0)
            {
                reason = "regex_grupo_vazio";
                return false;
            }

            int matchStart = group.Index;
            int matchEnd = group.Index + group.Length;

            int totalLen = 0;
            for (int i = start; i <= end; i++)
                totalLen += entries[i - 1].Text?.Length ?? 0;

            if (matchStart < 0 || matchEnd > totalLen)
            {
                reason = "regex_range_fora";
                return false;
            }

            int offset = 0;
            refinedStart = 0;
            refinedEnd = 0;
            for (int i = start; i <= end; i++)
            {
                var text = entries[i - 1].Text ?? "";
                int len = text.Length;
                int opStart = offset;
                int opEnd = offset + len;

                if (refinedStart == 0 && matchStart < opEnd)
                    refinedStart = i;

                if (matchEnd <= opEnd)
                {
                    refinedEnd = i;
                    break;
                }

                offset = opEnd;
            }

            if (refinedStart == 0 || refinedEnd == 0)
            {
                reason = "regex_range_map_fail";
                return false;
            }

            value = group.Value;
            return true;
        }

        private static string FormatOpRange(int start, int end, string op)
        {
            if (start <= 0 || end <= 0) return "";
            if (end < start) (start, end) = (end, start);
            return start == end
                ? $"op{start}[{op}]"
                : $"op{start}-{end}[{op}]";
        }

        private static bool IsOpWildcard(string op)
        {
            if (string.IsNullOrWhiteSpace(op)) return false;
            return op.Equals("*", StringComparison.Ordinal) ||
                   op.Equals("ANY", StringComparison.OrdinalIgnoreCase) ||
                   op.Equals("ALL", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseOpRange(string raw, out int start, out int end, out string op)
        {
            start = 0;
            end = 0;
            op = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var m = Regex.Match(raw.Trim(), @"^op(?<start>\d+)(?:-(?<end>\d+))?\[(?<op>[^\]]+)\]$", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups["start"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out start))
                return false;
            if (m.Groups["end"].Success)
            {
                if (!int.TryParse(m.Groups["end"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out end))
                    return false;
            }
            else end = start;
            op = m.Groups["op"].Value.Trim();
            return true;
        }

        private static List<TextOpEntry> CollectTextOpEntries(PdfStream stream, PdfResources resources)
        {
            var entries = new List<TextOpEntry>();
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return entries;

            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources));
            int index = 0;

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                if (IsTextShowingOperator(tok))
                {
                    index++;
                    var text = textQueue.Count > 0 ? textQueue.Dequeue() : "";
                    var rawToken = ExtractRawTextOperandToken(tok, operands);
                    if (string.IsNullOrWhiteSpace(text))
                        text = DecodeRawTokenText(rawToken);
                    if (!string.IsNullOrEmpty(text))
                        text = NormalizeOpText(text);
                    var rawHex = BytesToHex(ExtractRawBytes(rawToken));
                    entries.Add(new TextOpEntry(index, tok, text ?? "", rawToken, rawHex));
                }

                operands.Clear();
            }

            return entries;
        }

        private static List<string> BuildCodeLines(List<TextOpEntry> entries, int start, int end)
        {
            var lines = new List<string>();
            if (entries.Count == 0) return lines;
            if (start <= 0 || end < start || end > entries.Count) return lines;
            for (int i = start; i <= end; i++)
            {
                var e = entries[i - 1];
                var raw = string.IsNullOrWhiteSpace(e.RawToken) ? "-" : e.RawToken;
                var hex = string.IsNullOrWhiteSpace(e.RawHex) ? "-" : e.RawHex;
                lines.Add($"op{i}[{e.Op}] raw={raw} bytes={hex} text=\"{e.Text}\"");
            }
            return lines;
        }

        private static string ExtractRawTextOperandToken(string op, List<string> operands)
        {
            if (operands == null || operands.Count == 0) return "";
            return operands[^1];
        }

        private static byte[] ExtractRawBytes(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return Array.Empty<byte>();
            token = token.Trim();
            if (token.StartsWith("(", StringComparison.Ordinal))
                return DecodeLiteralBytes(token);
            if (token.StartsWith("<", StringComparison.Ordinal) && token.EndsWith(">", StringComparison.Ordinal) && !token.StartsWith("<<", StringComparison.Ordinal))
                return DecodeHexBytes(token);
            if (token.StartsWith("[", StringComparison.Ordinal))
                return DecodeArrayBytes(token);
            return Array.Empty<byte>();
        }

        private static string DecodeRawTokenText(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken)) return "";
            var bytes = ExtractRawBytes(rawToken);
            if (bytes.Length == 0) return "";
            return System.Text.Encoding.Latin1.GetString(bytes);
        }

        private static string NormalizeOpText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsControl(ch) || ch == '\u0000')
                    sb.Append(' ');
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static byte[] DecodeLiteralBytes(string token)
        {
            var bytes = new List<byte>();
            if (token.Length < 2) return bytes.ToArray();
            int i = 1;
            int depth = 1;
            while (i < token.Length && depth > 0)
            {
                char c = token[i];
                if (c == '\\')
                {
                    if (i + 1 >= token.Length) break;
                    char n = token[i + 1];
                    if (n == '\r' || n == '\n')
                    {
                        i += 2;
                        if (n == '\r' && i < token.Length && token[i] == '\n') i++;
                        continue;
                    }
                    if (n >= '0' && n <= '7')
                    {
                        int oct = 0;
                        int count = 0;
                        int j = i + 1;
                        while (j < token.Length && count < 3)
                        {
                            char oc = token[j];
                            if (oc < '0' || oc > '7') break;
                            oct = (oct * 8) + (oc - '0');
                            count++;
                            j++;
                        }
                        bytes.Add((byte)(oct & 0xFF));
                        i = j;
                        continue;
                    }
                    byte mapped = n switch
                    {
                        'n' => (byte)'\n',
                        'r' => (byte)'\r',
                        't' => (byte)'\t',
                        'b' => (byte)'\b',
                        'f' => (byte)'\f',
                        '(' => (byte)'(',
                        ')' => (byte)')',
                        '\\' => (byte)'\\',
                        _ => (byte)n
                    };
                    bytes.Add(mapped);
                    i += 2;
                    continue;
                }
                if (c == '(') { depth++; bytes.Add((byte)c); i++; continue; }
                if (c == ')') { depth--; if (depth > 0) bytes.Add((byte)c); i++; continue; }
                bytes.Add((byte)c);
                i++;
            }
            return bytes.ToArray();
        }

        private static byte[] DecodeHexBytes(string token)
        {
            var hex = token.Trim('<', '>');
            hex = new string(hex.Where(ch => !IsWhite(ch)).ToArray());
            if (hex.Length % 2 != 0) hex += "0";
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, null, out var b))
                    b = 0x20;
                bytes[i] = b;
            }
            return bytes;
        }

        private static byte[] DecodeArrayBytes(string token)
        {
            var bytes = new List<byte>();
            int i = 0;
            while (i < token.Length)
            {
                char c = token[i];
                if (c == '(')
                {
                    var lit = ReadLiteralToken(token, ref i);
                    bytes.AddRange(DecodeLiteralBytes(lit));
                    continue;
                }
                if (c == '<')
                {
                    var hex = ReadHexToken(token, ref i);
                    bytes.AddRange(DecodeHexBytes(hex));
                    continue;
                }
                i++;
            }
            return bytes.ToArray();
        }

        private static string ReadLiteralToken(string text, ref int i)
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

        private static string ReadHexToken(string text, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < text.Length && text[i] != '>') i++;
            if (i < text.Length) i++;
            return text.Substring(start, i - start);
        }

        private static string BytesToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static bool ParseOptions(
            string[] args,
            out string inputFile,
            out string mapPath,
            out string outPath,
            out HashSet<string> fieldsFilter,
            out bool validate,
            out bool asJson,
            out bool codes,
            out bool applyRules,
            out string configPath,
            out bool useAnchors,
            out bool requireAnchors,
            out bool requireVariable,
            out bool requireFixed,
            out int minTokenLen,
            out int maxTokenLen,
            out int minTextLen,
            out int maxTextLen,
            out int selfMinTokenLen,
            out int selfPatternMax,
            out string rulesPathArg,
            out string rulesDoc)
        {
            inputFile = "";
            mapPath = "";
            outPath = "";
            fieldsFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            validate = false;
            asJson = false;
            codes = false;
            applyRules = false;
            configPath = "configs/config.yaml";
            useAnchors = false;
            requireAnchors = false;
            requireVariable = false;
            requireFixed = false;
            minTokenLen = 0;
            maxTokenLen = 0;
            minTextLen = 0;
            maxTextLen = 0;
            selfMinTokenLen = 2;
            selfPatternMax = 1;
            rulesPathArg = "";
            rulesDoc = "";

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    mapPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--fields", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    foreach (var f in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        fieldsFilter.Add(f.Trim());
                    continue;
                }
                if (string.Equals(arg, "--validate", StringComparison.OrdinalIgnoreCase))
                {
                    validate = true;
                    continue;
                }
                if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
                {
                    asJson = true;
                    continue;
                }
                if (string.Equals(arg, "--codes", StringComparison.OrdinalIgnoreCase))
                {
                    codes = true;
                    continue;
                }
                if (string.Equals(arg, "--apply-rules", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--apply-fields", StringComparison.OrdinalIgnoreCase))
                {
                    applyRules = true;
                    continue;
                }
                if ((string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--rules-config", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    configPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--anchors", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--use-anchors", StringComparison.OrdinalIgnoreCase))
                {
                    useAnchors = true;
                    continue;
                }
                if (string.Equals(arg, "--require-anchors", StringComparison.OrdinalIgnoreCase))
                {
                    useAnchors = true;
                    requireAnchors = true;
                    continue;
                }
                if (string.Equals(arg, "--self-variable", StringComparison.OrdinalIgnoreCase))
                {
                    requireVariable = true;
                    continue;
                }
                if (string.Equals(arg, "--self-fixed", StringComparison.OrdinalIgnoreCase))
                {
                    requireFixed = true;
                    continue;
                }
                if (string.Equals(arg, "--min-token-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out minTokenLen);
                    continue;
                }
                if (string.Equals(arg, "--max-token-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out maxTokenLen);
                    continue;
                }
                if (string.Equals(arg, "--min-text-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out minTextLen);
                    continue;
                }
                if (string.Equals(arg, "--max-text-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out maxTextLen);
                    continue;
                }
                if (string.Equals(arg, "--self-min-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out selfMinTokenLen);
                    continue;
                }
                if (string.Equals(arg, "--self-pattern-max", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out selfPatternMax);
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
                if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = arg;
                }
            }

            return true;
        }

        private static string ResolveMapPath(string mapPath)
        {
            if (File.Exists(mapPath)) return mapPath;
            var cwd = Directory.GetCurrentDirectory();
            var candidates = new List<string>();
            candidates.Add(Path.Combine(cwd, mapPath));
            candidates.Add(Path.Combine(cwd, "ExtractFields", mapPath));
            candidates.Add(Path.Combine(cwd, "configs", "textops_fields", mapPath));
            if (!mapPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !mapPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(cwd, "ExtractFields", mapPath + ".yml"));
                candidates.Add(Path.Combine(cwd, "ExtractFields", mapPath + ".yaml"));
                candidates.Add(Path.Combine(cwd, "configs", "textops_fields", mapPath + ".yml"));
                candidates.Add(Path.Combine(cwd, "configs", "textops_fields", mapPath + ".yaml"));
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return mapPath;
        }

        private static string ResolveRulesPath(string rulesPathArg, string rulesDoc)
        {
            if (!string.IsNullOrWhiteSpace(rulesPathArg) && File.Exists(rulesPathArg))
                return rulesPathArg;

            var cwd = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(rulesPathArg))
            {
                var direct = Path.Combine(cwd, rulesPathArg);
                if (File.Exists(direct)) return direct;
            }

            if (!string.IsNullOrWhiteSpace(rulesDoc))
            {
                var byDoc = Path.Combine(cwd, "configs", "textops_rules", rulesDoc + ".yml");
                if (File.Exists(byDoc)) return byDoc;
                var byDocAlt = Path.Combine(cwd, "configs", "textops_rules", rulesDoc + ".yaml");
                if (File.Exists(byDocAlt)) return byDocAlt;
            }

            return !string.IsNullOrWhiteSpace(rulesPathArg) ? rulesPathArg : "";
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects extractfields --input file.pdf [--map <map.yml>] [--fields a,b] [--validate] [--codes] [--json] [--out <arquivo>]");
            Console.WriteLine("                                      [--apply-rules] [--config <config.yaml>]");
            Console.WriteLine("                                      [--anchors|--require-anchors] [--self-variable|--self-fixed]");
            Console.WriteLine("                                      [--min-token-len N] [--max-token-len N] [--min-text-len N] [--max-text-len N]");
            Console.WriteLine("                                      [--self-min-len N] [--self-pattern-max N] [--rules <yml> | --doc <nome>]");
        }

        private static string ResolveConfigPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Path.GetFullPath("configs/config.yaml");
            if (Path.IsPathRooted(path))
                return path;
            var cwd = Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(cwd, path));
        }

        private static bool TryApplyFieldRules(FieldStrategyEngine engine, string fieldName, string text, out string cleaned)
        {
            cleaned = "";
            if (engine == null || string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(text))
                return false;

            var ctx = new DespachoContext
            {
                FullText = text,
                StartPage1 = 1,
                EndPage1 = 1,
                Regions = new List<RegionSegment>
                {
                    new RegionSegment { Page1 = 1, Name = "textops_range", Text = text }
                }
            };

            var extracted = engine.Extract(ctx);
            if (extracted.TryGetValue(fieldName, out var info) && !string.IsNullOrWhiteSpace(info.Value))
            {
                cleaned = info.Value;
                return true;
            }

            return false;
        }

        private static bool CheckAnchors(List<TextOpEntry> entries, TextOpsFieldCandidate cand, bool requireAnchors, out string reason)
        {
            reason = "";
            var hasPrev = cand.PrevOpRange != null && cand.PrevOpRange.Count > 0;
            var hasNext = cand.NextOpRange != null && cand.NextOpRange.Count > 0;

            if (requireAnchors && !hasPrev && !hasNext)
            {
                reason = "anchors_missing";
                return false;
            }

            if (hasPrev && !TryExtractAnyRange(entries, cand.PrevOpRange, out _))
            {
                reason = "anchor_prev_missing";
                return false;
            }

            if (hasNext && !TryExtractAnyRange(entries, cand.NextOpRange, out _))
            {
                reason = "anchor_next_missing";
                return false;
            }

            return true;
        }

        private static bool TryExtractAnyRange(List<TextOpEntry> entries, List<string>? ranges, out string text)
        {
            text = "";
            if (ranges == null || ranges.Count == 0) return false;
            foreach (var raw in ranges)
            {
                if (!TryParseOpRange(raw, out var start, out var end, out var op))
                    continue;
                if (TryExtractRange(entries, start, end, op, out var value, out _))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        text = value;
                        return true;
                    }
                }
            }
            return false;
        }

        private static AnchorExtract? ExtractAnchor(List<TextOpEntry> entries, List<string>? ranges)
        {
            if (ranges == null || ranges.Count == 0) return null;
            foreach (var raw in ranges)
            {
                if (!TryParseOpRange(raw, out var start, out var end, out var op))
                    continue;
                if (TryExtractRange(entries, start, end, op, out var value, out _))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return new AnchorExtract
                        {
                            OpRange = raw,
                            Text = value,
                            Len = value.Length
                        };
                    }
                }
            }
            return null;
        }

        private static bool IsRangeInBlocks(List<SelfBlock> blocks, int start, int end)
        {
            if (blocks == null || blocks.Count == 0) return false;
            foreach (var b in blocks)
            {
                if (start >= b.StartOp && end <= b.EndOp)
                    return true;
            }
            return false;
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

        private static List<SelfBlock> ExtractSelfBlocks(PdfStream stream, PdfResources resources, HashSet<string> opFilter)
        {
            var blocks = new List<SelfBlock>();
            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0) return blocks;

            var tokens = TokenizeContent(bytes);
            var operands = new List<string>();
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(stream, resources));

            var currentTokens = new List<string>();
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
                    return;
                }

                var patternSb = new System.Text.StringBuilder();
                int maxLen = 0;
                foreach (var len in lens)
                {
                    maxLen = Math.Max(maxLen, len);
                    patternSb.Append(len == 1 ? '1' : 'W');
                }

                blockIndex++;
                blocks.Add(new SelfBlock(blockIndex, startOp, endOp, text, patternSb.ToString(), maxLen));
                currentTokens.Clear();
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

        private static bool IsLineBreakTextOperator(string op)
        {
            return op == "'" || op == "\"";
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

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < needed && textQueue.Count > 0; i++)
                    sb.Append(textQueue.Dequeue());
                var joined = sb.ToString();
                return !string.IsNullOrWhiteSpace(joined) ? joined : ExtractDecodedTextFromLine(rawLine ?? "");
            }

            return textQueue.Count > 0 ? textQueue.Dequeue() : ExtractDecodedTextFromLine(rawLine ?? "");
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
            var sb = new System.Text.StringBuilder();
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
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
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

        private sealed class ObjContext
        {
            public ObjContext(PdfStream? stream, PdfResources? resources, List<TextOpEntry> entries)
            {
                Stream = stream;
                Resources = resources;
                Entries = entries;
            }

            public PdfStream? Stream { get; }
            public PdfResources? Resources { get; }
            public List<TextOpEntry> Entries { get; }
        }

        private sealed class SelfBlock
        {
            public SelfBlock(int index, int startOp, int endOp, string text, string pattern, int maxTokenLen)
            {
                Index = index;
                StartOp = startOp;
                EndOp = endOp;
                Text = text;
                Pattern = pattern;
                MaxTokenLen = maxTokenLen;
            }

            public int Index { get; }
            public int StartOp { get; }
            public int EndOp { get; }
            public string Text { get; }
            public string Pattern { get; }
            public int MaxTokenLen { get; }
        }

        private sealed class SelfClassification
        {
            public SelfClassification(List<SelfBlock> variable, List<SelfBlock> fixedBlocks)
            {
                Variable = variable;
                Fixed = fixedBlocks;
            }

            public List<SelfBlock> Variable { get; }
            public List<SelfBlock> Fixed { get; }
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

        private sealed class TextOpEntry
        {
            public TextOpEntry(int index, string op, string text, string rawToken, string rawHex)
            {
                Index = index;
                Op = op;
                Text = text;
                RawToken = rawToken ?? "";
                RawHex = rawHex ?? "";
            }

            public int Index { get; }
            public string Op { get; }
            public string Text { get; }
            public string RawToken { get; }
            public string RawHex { get; }
        }

        private sealed class TextOpsFieldMap
        {
            public int Version { get; set; }
            public string Doc { get; set; } = "";
            public int Obj { get; set; }
            public List<string> Ops { get; set; } = new List<string>();
            public Dictionary<string, TextOpsField> Fields { get; set; } = new Dictionary<string, TextOpsField>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class TextOpsField
        {
            public List<TextOpsFieldCandidate> Candidates { get; set; } = new List<TextOpsFieldCandidate>();
        }

        private sealed class TextOpsFieldCandidate
        {
            public int Obj { get; set; }
            public string SourceFile { get; set; } = "";
            public string OpRange { get; set; } = "";
            public List<string> PrevOpRange { get; set; } = new List<string>();
            public List<string> NextOpRange { get; set; } = new List<string>();
            public string MatchRegex { get; set; } = "";
            public int? MatchGroup { get; set; }
        }

        private sealed class FieldExtractResult
        {
            public string Field { get; set; } = "";
            public string Status { get; set; } = "";
            public string? OpRange { get; set; }
            public string? SourceOpRange { get; set; }
            public string? ValueFull { get; set; }
            public int ValueFullLen { get; set; }
            public string? Value { get; set; }
            public int ValueLen { get; set; }
            public string? ValueRaw { get; set; }
            public int ValueRawLen { get; set; }
            public AnchorExtract? PrevFixed { get; set; }
            public AnchorExtract? NextFixed { get; set; }
            public List<CandidateValidation> Validation { get; set; } = new List<CandidateValidation>();
            public List<string> CodeLines { get; set; } = new List<string>();
        }

        private sealed class AnchorExtract
        {
            public string OpRange { get; set; } = "";
            public string Text { get; set; } = "";
            public int Len { get; set; }
        }

        private sealed class CandidateValidation
        {
            public string OpRange { get; set; } = "";
            public string Status { get; set; } = "";
            public string Reason { get; set; } = "";
        }

        private static (PdfStream? Stream, PdfResources? Resources) FindStreamAndResourcesByObjId(PdfDocument doc, int objId)
        {
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
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
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
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadHexString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < bytes.Length && bytes[i] != '>') i++;
            if (i < bytes.Length) i++;
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
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
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
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
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
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
            return System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

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
