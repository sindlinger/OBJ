using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Obj.Align;
using Obj.DocDetector;
using Obj.Extraction;
using Obj.FrontBack;
using Obj.Nlp;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Reference;
using Obj.TjpbDespachoExtractor.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Commands
{
    internal static class ObjectsPipeline
    {
        private const int DefaultBackoff = 2;
        private static readonly string DefaultOutDir = Path.Combine("outputs", "objects_pipeline");

        internal sealed class PipelineResult
        {
            public string PdfA { get; set; } = "";
            public string PdfB { get; set; } = "";
            public DetectionSummary DetectionA { get; set; } = new DetectionSummary();
            public DetectionSummary DetectionB { get; set; } = new DetectionSummary();
            public HeaderFooterSummary? HeaderFooter { get; set; }
            public AlignRangeSummary? AlignRange { get; set; }
            public MapFieldsSummary? MapFields { get; set; }
            public NlpSummary? Nlp { get; set; }
            public FieldsSummary? Fields { get; set; }
            public HonorariosSummary? Honorarios { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class DetectionSummary
        {
            public string TitleKey { get; set; } = "";
            public string Title { get; set; } = "";
            public int StartPage { get; set; }
            public int EndPage { get; set; }
            public string PathRef { get; set; } = "";
        }

        internal sealed class HeaderFooterSummary
        {
            public HeaderFooterPageInfo? FrontA { get; set; }
            public HeaderFooterPageInfo? FrontB { get; set; }
            public HeaderFooterPageInfo? BackA { get; set; }
            public HeaderFooterPageInfo? BackB { get; set; }
        }

        internal sealed class HeaderFooterPageInfo
        {
            public int Page { get; set; }
            public int PrimaryIndex { get; set; }
            public int PrimaryObj { get; set; }
            public int PrimaryTextOps { get; set; }
            public int PrimaryStreamLen { get; set; }
            public string HeaderText { get; set; } = "";
            public string FooterText { get; set; } = "";
            public int FooterIndex { get; set; }
            public int FooterObj { get; set; }
            public string HeaderKey { get; set; } = "";
        }

        internal sealed class AlignRangeSummary
        {
            public RangeValue FrontA { get; set; } = new RangeValue();
            public RangeValue FrontB { get; set; } = new RangeValue();
            public RangeValue BackA { get; set; } = new RangeValue();
            public RangeValue BackB { get; set; } = new RangeValue();
        }

        internal sealed class MapFieldsSummary
        {
            public string MapPath { get; set; } = "";
            public string AlignRangePath { get; set; } = "";
            public string JsonPath { get; set; } = "";
            public string RejectsPath { get; set; } = "";
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class NlpSummary
        {
            public NlpSegment? FrontA { get; set; }
            public NlpSegment? FrontB { get; set; }
            public NlpSegment? BackA { get; set; }
            public NlpSegment? BackB { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class FieldsSummary
        {
            public FieldsSegment? FrontA { get; set; }
            public FieldsSegment? FrontB { get; set; }
            public FieldsSegment? BackA { get; set; }
            public FieldsSegment? BackB { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class HonorariosSummary
        {
            public HonorariosSide? PdfA { get; set; }
            public HonorariosSide? PdfB { get; set; }
            public string ConfigPath { get; set; } = "";
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class HonorariosSide
        {
            public string Status { get; set; } = "";
            public string Source { get; set; } = "";
            public string Especialidade { get; set; } = "";
            public string EspecialidadeSource { get; set; } = "";
            public string ValorField { get; set; } = "";
            public string ValorRaw { get; set; } = "";
            public decimal ValorParsed { get; set; }
            public string ValorNormalized { get; set; } = "";
            public string Area { get; set; } = "";
            public string EspecieDaPericia { get; set; } = "";
            public string ValorTabeladoAnexoI { get; set; } = "";
            public string Fator { get; set; } = "";
            public string EntryId { get; set; } = "";
            public double Confidence { get; set; }
            public string Error { get; set; } = "";
        }

        internal sealed class NlpSegment
        {
            public string Label { get; set; } = "";
            public string Status { get; set; } = "";
            public string TextPath { get; set; } = "";
            public string NlpJsonPath { get; set; } = "";
            public string TypedPath { get; set; } = "";
            public string CboOutPath { get; set; } = "";
            public string Error { get; set; } = "";
        }

        internal sealed class FieldsSegment
        {
            public string Label { get; set; } = "";
            public string Status { get; set; } = "";
            public string JsonPath { get; set; } = "";
            public int Count { get; set; }
            public string Error { get; set; } = "";
        }

        internal sealed class RangeValue
        {
            public int Page { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string ValueFull { get; set; } = "";
        }

        private sealed class AlignRangeYaml
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

        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var inputs, out var pageA, out var pageB, out var asJson, out var outPath))
                return;

            if (inputs.Count < 2)
            {
                ShowHelp();
                return;
            }

            var aPath = inputs[0];
            var bPath = inputs[1];

            if (!File.Exists(aPath))
            {
                Console.WriteLine($"PDF nao encontrado: {aPath}");
                return;
            }
            if (!File.Exists(bPath))
            {
                Console.WriteLine($"PDF nao encontrado: {bPath}");
                return;
            }

            var result = RunPipeline(aPath, bPath, pageA, pageB);
            PrintSummary(result);

            if (asJson || !string.IsNullOrWhiteSpace(outPath))
            {
                if (string.IsNullOrWhiteSpace(outPath))
                {
                    Directory.CreateDirectory(DefaultOutDir);
                    outPath = Path.Combine(DefaultOutDir,
                        $"{Path.GetFileNameWithoutExtension(aPath)}__{Path.GetFileNameWithoutExtension(bPath)}.json");
                }

                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(outPath, json);
                Console.WriteLine($"Arquivo salvo: {outPath}");
            }
        }

        private static PipelineResult RunPipeline(string aPath, string bPath, int? pageA, int? pageB)
        {
            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var debugDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "debug");

            var result = new PipelineResult
            {
                PdfA = aPath,
                PdfB = bPath
            };

            WriteDebugJson(debugDir, "00_request.json", new
            {
                PdfA = aPath,
                PdfB = bPath,
                PageA = pageA,
                PageB = pageB
            });
            WriteModuleJson(debugDir, "path", "input", "request.json", new
            {
                PdfA = aPath,
                PdfB = bPath,
                PageA = pageA,
                PageB = pageB
            });

            FillDetection(result.DetectionA, aPath, result.Errors, required: !pageA.HasValue);
            FillDetection(result.DetectionB, bPath, result.Errors, required: !pageB.HasValue);

            WriteDebugJson(debugDir, "01_detection_a.json", result.DetectionA);
            WriteDebugJson(debugDir, "01_detection_b.json", result.DetectionB);
            WriteModuleJson(debugDir, "detector", "input", "a.json", new { Pdf = aPath, Required = !pageA.HasValue });
            WriteModuleJson(debugDir, "detector", "input", "b.json", new { Pdf = bPath, Required = !pageB.HasValue });
            WriteModuleJson(debugDir, "detector", "output", "a.json", result.DetectionA);
            WriteModuleJson(debugDir, "detector", "output", "b.json", result.DetectionB);

            int startA = pageA ?? result.DetectionA.StartPage;
            int startB = pageB ?? result.DetectionB.StartPage;

            if (startA <= 0)
                result.Errors.Add("Pagina A nao encontrada pelo detector.");
            if (startB <= 0)
                result.Errors.Add("Pagina B nao encontrada pelo detector.");
            if (result.Errors.Count > 0)
            {
                WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                return result;
            }

            result.HeaderFooter = BuildHeaderFooter(aPath, bPath, startA, startB);
            WriteDebugJson(debugDir, "01_header_footer.json", result.HeaderFooter);
            WriteModuleJson(debugDir, "header_footer", "input", "a.json", new { Pdf = aPath, Page = startA });
            WriteModuleJson(debugDir, "header_footer", "input", "b.json", new { Pdf = bPath, Page = startB });
            WriteModuleJson(debugDir, "header_footer", "output", "summary.json", result.HeaderFooter);

            var docTypeA = NormalizeDocTypeHint(result.DetectionA.TitleKey);
            var docTypeB = NormalizeDocTypeHint(result.DetectionB.TitleKey);
            var isDespacho = docTypeA == "despacho" && docTypeB == "despacho";

            var opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var defaults = ObjectsTextOpsDiff.LoadObjDefaults();
            if (defaults?.Ops != null)
            {
                foreach (var op in defaults.Ops)
                {
                    if (!string.IsNullOrWhiteSpace(op))
                        opFilter.Add(op.Trim());
                }
            }
            if (opFilter.Count == 0)
            {
                opFilter.Add("Tj");
                opFilter.Add("TJ");
            }

            if (isDespacho)
            {
                var frontBackRequest = new FrontBackRequest
                {
                    PdfA = aPath,
                    PdfB = bPath,
                    PageA = startA,
                    PageB = startB,
                    OpFilter = opFilter,
                    Backoff = DefaultBackoff,
                    FrontRequireMarker = true
                };
                WriteModuleJson(debugDir, "frontback", "input", "request.json", frontBackRequest);

                var frontBack = FrontBackResolver.Resolve(frontBackRequest);

                WriteDebugJson(debugDir, "02_frontback.json", frontBack);
                WriteModuleJson(debugDir, "frontback", "output", "result.json", frontBack);

                if (frontBack.Errors.Count > 0)
                {
                    result.Errors.AddRange(frontBack.Errors);
                    WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                    return result;
                }

                if (frontBack.AlignRange == null)
                {
                    result.Errors.Add("AlignRange nao retornou resultado.");
                    WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                    return result;
                }

                result.AlignRange = new AlignRangeSummary
                {
                    FrontA = ToRangeValue(frontBack.AlignRange.FrontA),
                    FrontB = ToRangeValue(frontBack.AlignRange.FrontB),
                    BackA = ToRangeValue(frontBack.AlignRange.BackA),
                    BackB = ToRangeValue(frontBack.AlignRange.BackB)
                };

                WriteDebugJson(debugDir, "03_alignrange.json", result.AlignRange);
                WriteModuleJson(debugDir, "alignrange", "input", "selection.json", new
                {
                    FrontA = frontBack.FrontA,
                    FrontB = frontBack.FrontB,
                    BackA = frontBack.BackBodyA,
                    BackB = frontBack.BackBodyB,
                    OpFilter = opFilter.ToArray()
                });
                WriteModuleJson(debugDir, "alignrange", "output", "summary.json", result.AlignRange);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(docTypeA) || string.IsNullOrWhiteSpace(docTypeB))
                    result.Errors.Add("doc_type_not_found");
                else if (!string.Equals(docTypeA, docTypeB, StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"doc_type_mismatch: {docTypeA} vs {docTypeB}");
                else if (docTypeA == "despacho")
                    result.Errors.Add("doc_type_invalid");

                if (result.Errors.Count > 0)
                {
                    WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                    return result;
                }

                var frontA = BandTextExtractor.ExtractBandText(aPath, startA, 0.65, 1.0);
                var frontB = BandTextExtractor.ExtractBandText(bPath, startB, 0.65, 1.0);
                var backA = BandTextExtractor.ExtractBandText(aPath, startA, 0.0, 0.35);
                var backB = BandTextExtractor.ExtractBandText(bPath, startB, 0.0, 0.35);

                result.AlignRange = new AlignRangeSummary
                {
                    FrontA = new RangeValue { Page = startA, StartOp = 0, EndOp = 0, ValueFull = frontA },
                    FrontB = new RangeValue { Page = startB, StartOp = 0, EndOp = 0, ValueFull = frontB },
                    BackA = new RangeValue { Page = startA, StartOp = 0, EndOp = 0, ValueFull = backA },
                    BackB = new RangeValue { Page = startB, StartOp = 0, EndOp = 0, ValueFull = backB }
                };

                WriteDebugJson(debugDir, "03_alignrange.json", result.AlignRange);
                WriteModuleJson(debugDir, "frontback", "input", "request.json", new
                {
                    Status = "skipped",
                    Reason = "non_despacho"
                });
                WriteModuleJson(debugDir, "frontback", "output", "result.json", new
                {
                    Status = "skipped",
                    Reason = "non_despacho"
                });
                WriteModuleJson(debugDir, "alignrange", "input", "selection.json", new
                {
                    Status = "synthetic",
                    Mode = "band_text"
                });
                WriteModuleJson(debugDir, "alignrange", "output", "summary.json", result.AlignRange);
            }

            result.MapFields = RunMapFields(aPath, bPath, result.AlignRange, result.DetectionA, result.DetectionB);
            if (result.MapFields != null)
            {
                WriteDebugJson(debugDir, "03_mapfields.json", result.MapFields);
                WriteModuleJson(debugDir, "mapfields", "output", "summary.json", result.MapFields);
            }

            result.Nlp = RunNlp(aPath, bPath, result.AlignRange);
            WriteDebugText(debugDir, "04_nlp_input_front_head_a.txt", result.AlignRange.FrontA.ValueFull);
            WriteDebugText(debugDir, "04_nlp_input_front_head_b.txt", result.AlignRange.FrontB.ValueFull);
            WriteDebugText(debugDir, "04_nlp_input_back_tail_a.txt", result.AlignRange.BackA.ValueFull);
            WriteDebugText(debugDir, "04_nlp_input_back_tail_b.txt", result.AlignRange.BackB.ValueFull);
            WriteModuleText(debugDir, "nlp", "input", "front_head_a.txt", result.AlignRange.FrontA.ValueFull);
            WriteModuleText(debugDir, "nlp", "input", "front_head_b.txt", result.AlignRange.FrontB.ValueFull);
            WriteModuleText(debugDir, "nlp", "input", "back_tail_a.txt", result.AlignRange.BackA.ValueFull);
            WriteModuleText(debugDir, "nlp", "input", "back_tail_b.txt", result.AlignRange.BackB.ValueFull);
            if (result.Nlp != null)
            {
                WriteDebugJson(debugDir, "04_nlp_output_front_head_a.json", result.Nlp.FrontA);
                WriteDebugJson(debugDir, "04_nlp_output_front_head_b.json", result.Nlp.FrontB);
                WriteDebugJson(debugDir, "04_nlp_output_back_tail_a.json", result.Nlp.BackA);
                WriteDebugJson(debugDir, "04_nlp_output_back_tail_b.json", result.Nlp.BackB);
                WriteModuleJson(debugDir, "nlp", "output", "front_head_a.json", result.Nlp.FrontA);
                WriteModuleJson(debugDir, "nlp", "output", "front_head_b.json", result.Nlp.FrontB);
                WriteModuleJson(debugDir, "nlp", "output", "back_tail_a.json", result.Nlp.BackA);
                WriteModuleJson(debugDir, "nlp", "output", "back_tail_b.json", result.Nlp.BackB);
            }

            result.Fields = RunFields(aPath, bPath, result.AlignRange, result.Nlp, result.DetectionA, result.DetectionB);
            if (result.Fields != null)
            {
                WriteDebugJson(debugDir, "05_fields_output_front_head_a.json", result.Fields.FrontA);
                WriteDebugJson(debugDir, "05_fields_output_front_head_b.json", result.Fields.FrontB);
                WriteDebugJson(debugDir, "05_fields_output_back_tail_a.json", result.Fields.BackA);
                WriteDebugJson(debugDir, "05_fields_output_back_tail_b.json", result.Fields.BackB);
                WriteModuleJson(debugDir, "fields", "input", "front_head_a.json", new { Text = result.AlignRange.FrontA.ValueFull, Nlp = result.Nlp?.FrontA });
                WriteModuleJson(debugDir, "fields", "input", "front_head_b.json", new { Text = result.AlignRange.FrontB.ValueFull, Nlp = result.Nlp?.FrontB });
                WriteModuleJson(debugDir, "fields", "input", "back_tail_a.json", new { Text = result.AlignRange.BackA.ValueFull, Nlp = result.Nlp?.BackA });
                WriteModuleJson(debugDir, "fields", "input", "back_tail_b.json", new { Text = result.AlignRange.BackB.ValueFull, Nlp = result.Nlp?.BackB });
                WriteModuleJson(debugDir, "fields", "output", "front_head_a.json", result.Fields.FrontA);
                WriteModuleJson(debugDir, "fields", "output", "front_head_b.json", result.Fields.FrontB);
                WriteModuleJson(debugDir, "fields", "output", "back_tail_a.json", result.Fields.BackA);
                WriteModuleJson(debugDir, "fields", "output", "back_tail_b.json", result.Fields.BackB);
            }

            WriteModuleJson(debugDir, "honorarios", "input", "request.json", new
            {
                MapFieldsJson = result.MapFields?.JsonPath ?? "",
                FieldsFrontA = result.Fields?.FrontA?.JsonPath ?? "",
                FieldsFrontB = result.Fields?.FrontB?.JsonPath ?? ""
            });
            result.Honorarios = RunHonorariosEnrichment(result.MapFields, result.Fields);
            if (result.Honorarios != null)
            {
                WriteDebugJson(debugDir, "06_honorarios.json", result.Honorarios);
                WriteModuleJson(debugDir, "honorarios", "output", "result.json", result.Honorarios);
            }

            return result;
        }

        private static void FillDetection(DetectionSummary target, string pdfPath, List<string> errors, bool required)
        {
            var hit = DetectDoc(pdfPath, out var endPage);
            if (!hit.Found)
            {
                if (required)
                    errors.Add($"Detector nao encontrou despacho/diretoria especial em {Path.GetFileName(pdfPath)}.");
                return;
            }

            target.TitleKey = hit.TitleKey;
            target.Title = hit.Title;
            target.StartPage = hit.Page;
            target.EndPage = endPage > 0 ? endPage : hit.Page;
            target.PathRef = hit.PathRef;
        }

        private static RangeValue ToRangeValue(AlignRangeValue value)
        {
            return new RangeValue
            {
                Page = value.Page,
                StartOp = value.StartOp,
                EndOp = value.EndOp,
                ValueFull = value.ValueFull ?? ""
            };
        }

        private static void PrintSummary(PipelineResult result)
        {
            Console.WriteLine("OBJ PIPELINE");
            Console.WriteLine($"A: {Path.GetFileName(result.PdfA)}");
            Console.WriteLine($"B: {Path.GetFileName(result.PdfB)}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine("Erros:");
                foreach (var err in result.Errors)
                    Console.WriteLine($"- {err}");
                return;
            }

            Console.WriteLine("Detector A:");
            Console.WriteLine($"  title_key={result.DetectionA.TitleKey} pages={result.DetectionA.StartPage}-{result.DetectionA.EndPage}");
            Console.WriteLine($"  path={result.DetectionA.PathRef}");
            Console.WriteLine("Detector B:");
            Console.WriteLine($"  title_key={result.DetectionB.TitleKey} pages={result.DetectionB.StartPage}-{result.DetectionB.EndPage}");
            Console.WriteLine($"  path={result.DetectionB.PathRef}");

            if (result.AlignRange == null)
                return;

            Console.WriteLine("AlignRange (front/back):");
            PrintRange("front_head A", result.AlignRange.FrontA);
            PrintRange("front_head B", result.AlignRange.FrontB);
            PrintRange("back_tail A", result.AlignRange.BackA);
            PrintRange("back_tail B", result.AlignRange.BackB);

            if (result.Nlp != null)
            {
                Console.WriteLine("NLP (typed):");
                PrintNlp("front_head A", result.Nlp.FrontA);
                PrintNlp("front_head B", result.Nlp.FrontB);
                PrintNlp("back_tail A", result.Nlp.BackA);
                PrintNlp("back_tail B", result.Nlp.BackB);
                if (result.Nlp.Errors.Count > 0)
                {
                    Console.WriteLine("NLP errors:");
                    foreach (var err in result.Nlp.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.Fields != null)
            {
                Console.WriteLine("FIELDS (typed):");
                PrintFields("front_head A", result.Fields.FrontA);
                PrintFields("front_head B", result.Fields.FrontB);
                PrintFields("back_tail A", result.Fields.BackA);
                PrintFields("back_tail B", result.Fields.BackB);
                if (result.Fields.Errors.Count > 0)
                {
                    Console.WriteLine("FIELDS errors:");
                    foreach (var err in result.Fields.Errors)
                    Console.WriteLine($"- {err}");
                }
            }

            if (result.MapFields != null)
            {
                Console.WriteLine("MAPFIELDS (yaml):");
                var status = string.IsNullOrWhiteSpace(result.MapFields.JsonPath) ? "skip" : "ok";
                Console.WriteLine($"  status: {status}");
                if (!string.IsNullOrWhiteSpace(result.MapFields.MapPath))
                    Console.WriteLine($"  map: {result.MapFields.MapPath}");
                if (!string.IsNullOrWhiteSpace(result.MapFields.JsonPath))
                    Console.WriteLine($"  json: {result.MapFields.JsonPath}");
                if (!string.IsNullOrWhiteSpace(result.MapFields.RejectsPath))
                    Console.WriteLine($"  rejects: {result.MapFields.RejectsPath}");
                if (result.MapFields.Errors.Count > 0)
                {
                    Console.WriteLine("MAPFIELDS errors:");
                    foreach (var err in result.MapFields.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.Honorarios != null)
            {
                Console.WriteLine("HONORARIOS (computed):");
                PrintHonorarios("pdf_a", result.Honorarios.PdfA);
                PrintHonorarios("pdf_b", result.Honorarios.PdfB);
                if (result.Honorarios.Errors.Count > 0)
                {
                    Console.WriteLine("HONORARIOS errors:");
                    foreach (var err in result.Honorarios.Errors)
                        Console.WriteLine($"- {err}");
                }
            }
        }

        private static void PrintRange(string label, RangeValue value)
        {
            var range = FormatOpRange(value.StartOp, value.EndOp);
            Console.WriteLine($"  {label}: page={value.Page} range={range}");
            Console.WriteLine($"    value_full: \"{EscapeValue(value.ValueFull)}\"");
        }

        private static void PrintNlp(string label, NlpSegment? seg)
        {
            if (seg == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(seg.Status) ? "unknown" : seg.Status;
            Console.WriteLine($"  {label}: {status}");
            if (!string.IsNullOrWhiteSpace(seg.TypedPath))
                Console.WriteLine($"    typed: {seg.TypedPath}");
            if (!string.IsNullOrWhiteSpace(seg.NlpJsonPath))
                Console.WriteLine($"    nlp: {seg.NlpJsonPath}");
            if (!string.IsNullOrWhiteSpace(seg.Error))
                Console.WriteLine($"    error: {seg.Error}");
        }

        private static void PrintFields(string label, FieldsSegment? seg)
        {
            if (seg == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(seg.Status) ? "unknown" : seg.Status;
            Console.WriteLine($"  {label}: {status} (count={seg.Count})");
            if (!string.IsNullOrWhiteSpace(seg.JsonPath))
                Console.WriteLine($"    json: {seg.JsonPath}");
            if (!string.IsNullOrWhiteSpace(seg.Error))
                Console.WriteLine($"    error: {seg.Error}");
        }

        private static void PrintHonorarios(string label, HonorariosSide? side)
        {
            if (side == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(side.Status) ? "unknown" : side.Status;
            Console.WriteLine($"  {label}: {status} ({side.Source})");
            if (!string.IsNullOrWhiteSpace(side.Especialidade))
                Console.WriteLine($"    especialidade: {side.Especialidade} ({side.EspecialidadeSource})");
            if (!string.IsNullOrWhiteSpace(side.ValorNormalized))
                Console.WriteLine($"    valor_base: {side.ValorNormalized} ({side.ValorField})");
            if (!string.IsNullOrWhiteSpace(side.EspecieDaPericia))
                Console.WriteLine($"    especie: {side.EspecieDaPericia}");
            if (!string.IsNullOrWhiteSpace(side.ValorTabeladoAnexoI))
                Console.WriteLine($"    valor_tabelado: {side.ValorTabeladoAnexoI}");
            if (!string.IsNullOrWhiteSpace(side.Fator))
                Console.WriteLine($"    fator: {side.Fator}");
            if (!string.IsNullOrWhiteSpace(side.Error))
                Console.WriteLine($"    error: {side.Error}");
        }

        private static string FormatOpRange(int start, int end)
        {
            if (start <= 0 || end <= 0) return "op0";
            if (end < start) (start, end) = (end, start);
            return start == end ? $"op{start}" : $"op{start}-{end}";
        }

        private static string EscapeValue(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Replace("\"", "\\\"");
        }

        private static string NormalizeDocTypeHint(string? titleKey)
        {
            if (string.IsNullOrWhiteSpace(titleKey)) return "";
            var norm = titleKey.Trim().ToLowerInvariant();
            if (norm.Contains("despacho", StringComparison.Ordinal) || norm.Contains("diretoria", StringComparison.Ordinal))
                return "despacho";
            if (norm.Contains("certidao", StringComparison.Ordinal))
                return "certidao_conselho";
            if (norm.Contains("requerimento", StringComparison.Ordinal))
                return "requerimento_honorarios";
            return norm.Replace(' ', '_');
        }

        private static bool ParseOptions(
            string[] args,
            out List<string> inputs,
            out int? pageA,
            out int? pageB,
            out bool asJson,
            out string outPath)
        {
            inputs = new List<string>();
            pageA = null;
            pageB = null;
            asJson = false;
            outPath = "";

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                {
                    ShowHelp();
                    return false;
                }
                if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        inputs.Add(raw.Trim());
                    continue;
                }
                if (string.Equals(arg, "--page-a", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                        pageA = Math.Max(1, p);
                    continue;
                }
                if (string.Equals(arg, "--page-b", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                        pageB = Math.Max(1, p);
                    continue;
                }
                if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
                {
                    asJson = true;
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal))
                    inputs.Add(arg);
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects pipeline <pdfA> <pdfB>");
            Console.WriteLine("  --page-a N    (opcional, forca pagina A)");
            Console.WriteLine("  --page-b N    (opcional, forca pagina B)");
            Console.WriteLine("  --json        (salva JSON em outputs/objects_pipeline/)");
            Console.WriteLine("  --out <file>  (salva JSON no caminho informado)");
        }

        private static DetectionHit DetectDoc(string pdfPath, out int endPage)
        {
            endPage = 0;

            var hit = DetectByBookmarkRange(pdfPath, out endPage);
            if (hit.Found)
                return hit;

            hit = ContentsPrefixDetector.Detect(pdfPath);
            if (hit.Found)
            {
                endPage = Math.Max(hit.Page, hit.Page + 1);
                return hit;
            }

            hit = HeaderLabelDetector.Detect(pdfPath);
            if (hit.Found)
            {
                endPage = Math.Max(hit.Page, hit.Page + 1);
                return hit;
            }

            hit = NonDespachoDetector.Detect(pdfPath);
            if (hit.Found)
            {
                endPage = hit.Page;
                return hit;
            }

            hit = LargestContentsDetector.Detect(pdfPath);
            if (hit.Found)
            {
                endPage = Math.Max(hit.Page, hit.Page + 1);
                return hit;
            }

            return hit;
        }

        private static MapFieldsSummary RunMapFields(string aPath, string bPath, AlignRangeSummary align, DetectionSummary detA, DetectionSummary detB)
        {
            var summary = new MapFieldsSummary();
            if (align == null)
                return summary;

            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "mapfields");
            Directory.CreateDirectory(outDir);

            var docHintA = NormalizeDocTypeHint(detA.TitleKey);
            var docHintB = NormalizeDocTypeHint(detB.TitleKey);
            var docHint = string.Equals(docHintA, docHintB, StringComparison.OrdinalIgnoreCase) ? docHintA : docHintA;

            var mapPath = ResolveMapPathForDoc(docHint);
            if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
            {
                summary.Errors.Add("map_not_found");
                summary.MapPath = mapPath;
                return summary;
            }

            var alignPath = Path.Combine(outDir, $"{baseA}__{baseB}.yml");
            WriteAlignRangeYaml(align, aPath, bPath, alignPath);

            summary.MapPath = mapPath;
            summary.AlignRangePath = alignPath;

            try
            {
                ObjectsMapFields.Execute(new[]
                {
                    "--alignrange", alignPath,
                    "--map", mapPath,
                    "--out", outDir,
                    "--both",
                    "--side", "both"
                });

                var baseName = Path.GetFileNameWithoutExtension(alignPath);
                var outFile = Path.Combine(outDir, $"{baseName}__mapfields_both_ab.json");
                if (File.Exists(outFile))
                {
                    summary.JsonPath = outFile;
                    summary.RejectsPath = WriteMapFieldsRejects(outFile, outDir, baseName);
                }
                else
                {
                    summary.Errors.Add("mapfields_output_not_found");
                }
            }
            catch (Exception ex)
            {
                summary.Errors.Add("mapfields_error: " + ex.Message);
            }

            return summary;
        }

        private static string ResolveMapPathForDoc(string docHint)
        {
            var key = string.IsNullOrWhiteSpace(docHint) ? "" : docHint.Trim().ToLowerInvariant();
            if (key == "despacho")
                return ResolveMapPath("tjpb_despacho");
            if (key == "certidao_conselho")
                return ResolveMapPath("tjpb_certidao");
            if (key == "requerimento_honorarios")
                return ResolveMapPath("tjpb_requerimento");
            return ResolveMapPath(key);
        }

        private static string ResolveMapPath(string mapPath)
        {
            if (string.IsNullOrWhiteSpace(mapPath))
                return "";
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
            return "";
        }

        private static void WriteAlignRangeYaml(AlignRangeSummary align, string aPath, string bPath, string outPath)
        {
            var data = new AlignRangeYaml
            {
                FrontHead = new AlignRangeSection
                {
                    PdfA = Path.GetFileName(aPath),
                    OpRangeA = FormatOpRange(align.FrontA.StartOp, align.FrontA.EndOp),
                    ValueFullA = align.FrontA.ValueFull ?? "",
                    PdfB = Path.GetFileName(bPath),
                    OpRangeB = FormatOpRange(align.FrontB.StartOp, align.FrontB.EndOp),
                    ValueFullB = align.FrontB.ValueFull ?? ""
                },
                BackTail = new AlignRangeSection
                {
                    PdfA = Path.GetFileName(aPath),
                    OpRangeA = FormatOpRange(align.BackA.StartOp, align.BackA.EndOp),
                    ValueFullA = align.BackA.ValueFull ?? "",
                    PdfB = Path.GetFileName(bPath),
                    OpRangeB = FormatOpRange(align.BackB.StartOp, align.BackB.EndOp),
                    ValueFullB = align.BackB.ValueFull ?? ""
                }
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            File.WriteAllText(outPath, serializer.Serialize(data));
        }

        private static string WriteMapFieldsRejects(string mapFieldsPath, string outDir, string baseName)
        {
            try
            {
                var json = File.ReadAllText(mapFieldsPath);
                using var doc = JsonDocument.Parse(json);
                var rejects = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var side in new[] { "pdf_a", "pdf_b" })
                {
                    if (!doc.RootElement.TryGetProperty(side, out var sideObj) || sideObj.ValueKind != JsonValueKind.Object)
                        continue;

                    var missing = new List<string>();
                    foreach (var prop in sideObj.EnumerateObject())
                    {
                        if (!prop.Value.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.String)
                        {
                            missing.Add(prop.Name);
                            continue;
                        }
                        var value = v.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(value))
                            missing.Add(prop.Name);
                    }
                    rejects[side] = missing;
                }

                var outPath = Path.Combine(outDir, $"{baseName}__mapfields_rejects.json");
                File.WriteAllText(outPath, JsonSerializer.Serialize(rejects, new JsonSerializerOptions { WriteIndented = true }));
                return outPath;
            }
            catch
            {
                return "";
            }
        }

        private static DetectionHit DetectByBookmarkRange(string pdfPath, out int endPage)
        {
            endPage = 0;
            var hit = BookmarkDetector.Detect(pdfPath);
            if (!hit.Found)
                return hit;

            var range = ResolveBookmarkRange(pdfPath, hit.Page);
            if (range.EndPage > 0)
                endPage = range.EndPage;
            else
                endPage = hit.Page;

            var markerPage = FindFirstMarkerPage(pdfPath, hit.Page, endPage);
            if (markerPage > 0)
            {
                hit.Page = markerPage;
                hit.PathRef = $"bookmark/page={markerPage}";
                return hit;
            }

            return DetectionHit.Empty(pdfPath, "bookmark_marker_not_found");
        }

        private static (int StartPage, int EndPage) ResolveBookmarkRange(string pdfPath, int startPage)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || startPage <= 0)
                return (0, 0);

            using var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(pdfPath));
            var outlines = OutlineUtils.GetOutlinePages(doc);
            if (outlines.Count == 0)
                return (startPage, startPage);

            var ordered = outlines.OrderBy(o => o.Page).ToList();
            var next = ordered.FirstOrDefault(o => o.Page > startPage);
            var endPage = next.Page > 0 ? Math.Max(startPage, next.Page - 1) : startPage;
            return (startPage, endPage);
        }

        private static int FindFirstMarkerPage(string pdfPath, int startPage, int endPage)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return 0;
            if (startPage <= 0) return 0;
            if (endPage < startPage) endPage = startPage;

            for (int p = startPage; p <= endPage; p++)
            {
                var pick = ContentsStreamPicker.Pick(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = p,
                    RequireMarker = true
                });
                if (pick.Found)
                    return p;
            }

            return 0;
        }

        private static HeaderFooterSummary BuildHeaderFooter(string aPath, string bPath, int pageA, int pageB)
        {
            var summary = new HeaderFooterSummary();
            var backA = pageA + 1;
            var backB = pageB + 1;

            summary.FrontA = ToHeaderFooterPage(HeaderFooterProbe.Probe(aPath, pageA));
            summary.FrontB = ToHeaderFooterPage(HeaderFooterProbe.Probe(bPath, pageB));
            summary.BackA = ToHeaderFooterPage(HeaderFooterProbe.Probe(aPath, backA));
            summary.BackB = ToHeaderFooterPage(HeaderFooterProbe.Probe(bPath, backB));

            return summary;
        }

        private static HeaderFooterPageInfo? ToHeaderFooterPage(HeaderFooterPage? page)
        {
            if (page == null) return null;
            return new HeaderFooterPageInfo
            {
                Page = page.Page,
                PrimaryIndex = page.PrimaryIndex,
                PrimaryObj = page.PrimaryObj,
                PrimaryTextOps = page.PrimaryTextOps,
                PrimaryStreamLen = page.PrimaryStreamLen,
                HeaderText = page.HeaderText ?? "",
                FooterText = page.FooterText ?? "",
                FooterIndex = page.FooterIndex,
                FooterObj = page.FooterObj,
                HeaderKey = page.HeaderKey ?? ""
            };
        }

        private static DetectionHit PickStream(string pdfPath, int page, bool requireMarker)
        {
            return ContentsStreamPicker.Pick(new StreamPickRequest
            {
                PdfPath = pdfPath,
                Page = page,
                RequireMarker = requireMarker
            });
        }

        private static NlpSummary RunNlp(string aPath, string bPath, AlignRangeSummary align)
        {
            var summary = new NlpSummary();
            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "nlp");

            summary.FrontA = RunNlpSegment(baseA, "front_head_a", align.FrontA.ValueFull, outDir, summary.Errors);
            summary.FrontB = RunNlpSegment(baseB, "front_head_b", align.FrontB.ValueFull, outDir, summary.Errors);
            summary.BackA = RunNlpSegment(baseA, "back_tail_a", align.BackA.ValueFull, outDir, summary.Errors);
            summary.BackB = RunNlpSegment(baseB, "back_tail_b", align.BackB.ValueFull, outDir, summary.Errors);

            return summary;
        }

        private static FieldsSummary RunFields(string aPath, string bPath, AlignRangeSummary align, NlpSummary? nlp, DetectionSummary detA, DetectionSummary detB)
        {
            var summary = new FieldsSummary();
            if (nlp == null)
                return summary;

            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "fields");
            var docHintA = NormalizeDocTypeHint(detA.TitleKey);
            var docHintB = NormalizeDocTypeHint(detB.TitleKey);

            summary.FrontA = RunFieldsSegment(baseA, "front_head_a", align.FrontA.ValueFull, nlp.FrontA, outDir, docHintA, summary.Errors);
            summary.FrontB = RunFieldsSegment(baseB, "front_head_b", align.FrontB.ValueFull, nlp.FrontB, outDir, docHintB, summary.Errors);
            summary.BackA = RunFieldsSegment(baseA, "back_tail_a", align.BackA.ValueFull, nlp.BackA, outDir, docHintA, summary.Errors);
            summary.BackB = RunFieldsSegment(baseB, "back_tail_b", align.BackB.ValueFull, nlp.BackB, outDir, docHintB, summary.Errors);

            return summary;
        }

        private static HonorariosSummary RunHonorariosEnrichment(MapFieldsSummary? mapFields, FieldsSummary? fields)
        {
            var summary = new HonorariosSummary();
            var cfgPath = ResolveConfigPath();
            summary.ConfigPath = cfgPath;
            if (string.IsNullOrWhiteSpace(cfgPath) || !File.Exists(cfgPath))
            {
                summary.Errors.Add("config_not_found");
                return summary;
            }

            TjpbDespachoConfig cfg;
            try
            {
                cfg = TjpbDespachoConfig.Load(cfgPath);
            }
            catch (Exception ex)
            {
                summary.Errors.Add("config_error: " + ex.Message);
                return summary;
            }

            var honorarios = new HonorariosTable(cfg.Reference.Honorarios, cfg.BaseDir);
            var peritos = PeritoCatalog.Load(cfg.BaseDir, cfg.Reference.PeritosCatalogPaths);

            summary.PdfA = ComputeHonorariosSide(
                "pdf_a",
                mapFields?.JsonPath,
                fields?.FrontA?.JsonPath,
                honorarios,
                peritos,
                cfg.Reference.Honorarios);

            summary.PdfB = ComputeHonorariosSide(
                "pdf_b",
                mapFields?.JsonPath,
                fields?.FrontB?.JsonPath,
                honorarios,
                peritos,
                cfg.Reference.Honorarios);

            if (summary.PdfA?.Status == "error")
                summary.Errors.Add("pdf_a_error");
            if (summary.PdfB?.Status == "error")
                summary.Errors.Add("pdf_b_error");

            return summary;
        }

        private static HonorariosSide ComputeHonorariosSide(
            string side,
            string? mapFieldsPath,
            string? nlpFieldsPath,
            HonorariosTable honorarios,
            PeritoCatalog peritos,
            HonorariosConfig cfg)
        {
            var result = new HonorariosSide();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var source = "";

            if (!string.IsNullOrWhiteSpace(mapFieldsPath) && File.Exists(mapFieldsPath))
            {
                values = ReadMapFields(mapFieldsPath, side);
                if (values.Count > 0)
                    source = "mapfields";
            }

            if (values.Count == 0 && !string.IsNullOrWhiteSpace(nlpFieldsPath) && File.Exists(nlpFieldsPath))
            {
                values = ReadNlpFields(nlpFieldsPath);
                if (values.Count > 0)
                    source = "nlp_fields";
            }

            result.Source = source;
            if (values.Count == 0)
            {
                result.Status = "no_fields";
                return result;
            }

            var especialidade = PickValue(values, "ESPECIALIDADE");
            var espSource = "fields";
            if (string.IsNullOrWhiteSpace(especialidade))
            {
                var perito = PickValue(values, "PERITO");
                var cpf = PickValue(values, "CPF_PERITO");
                if (peritos != null)
                {
                    if (!string.IsNullOrWhiteSpace(perito) && peritos.TryResolve(perito, "", out var infoByName, out _))
                    {
                        if (!string.IsNullOrWhiteSpace(infoByName.Especialidade))
                        {
                            especialidade = infoByName.Especialidade;
                            espSource = "perito_name_catalog";
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(cpf) && peritos.TryResolve("", cpf, out var infoByCpf, out _))
                    {
                        if (!string.IsNullOrWhiteSpace(infoByCpf.Especialidade))
                        {
                            especialidade = infoByCpf.Especialidade;
                            espSource = "perito_cpf_catalog";
                        }
                    }
                }
            }
            result.Especialidade = especialidade;
            result.EspecialidadeSource = espSource;
            if (string.IsNullOrWhiteSpace(especialidade))
            {
                result.Status = "missing_especialidade";
                return result;
            }

            var valorPick = PickValor(values, cfg);
            result.ValorField = valorPick.Field;
            result.ValorRaw = valorPick.Raw;

            if (!TextUtils.TryParseMoney(valorPick.Raw, out var valorParsed))
            {
                result.Status = "missing_valor";
                return result;
            }

            result.ValorParsed = valorParsed;
            result.ValorNormalized = FormatMoney(valorParsed);

            if (!honorarios.TryMatch(especialidade, valorParsed, out var entry, out var confidence))
            {
                result.Status = "no_match";
                return result;
            }

            result.Status = "ok";
            result.Area = entry.Area ?? "";
            result.EntryId = entry.Id ?? "";
            result.EspecieDaPericia = entry.Descricao ?? "";
            result.ValorTabeladoAnexoI = FormatMoney(entry.Valor);
            result.Fator = entry.Id ?? "";
            result.Confidence = confidence;
            return result;
        }

        private static string PickValue(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out var v) ? v ?? "" : "";
        }

        private static (string Field, string Raw) PickValor(Dictionary<string, string> values, HonorariosConfig cfg)
        {
            var candidates = new List<string>();
            if (cfg.PreferValorDe)
            {
                candidates.Add("VALOR_ARBITRADO_DE");
                if (cfg.AllowValorJz)
                    candidates.Add("VALOR_ARBITRADO_JZ");
            }
            else
            {
                if (cfg.AllowValorJz)
                    candidates.Add("VALOR_ARBITRADO_JZ");
                candidates.Add("VALOR_ARBITRADO_DE");
            }

            candidates.Add("VALOR_ARBITRADO_CM");

            if (cfg.AllowValorJz && !candidates.Contains("VALOR_ARBITRADO_JZ"))
                candidates.Add("VALOR_ARBITRADO_JZ");
            if (!candidates.Contains("VALOR_ARBITRADO_DE"))
                candidates.Add("VALOR_ARBITRADO_DE");

            foreach (var key in candidates)
            {
                if (values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
                    return (key, raw);
            }

            return ("", "");
        }

        private static Dictionary<string, string> ReadMapFields(string mapFieldsPath, string side)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mapFieldsPath));
                if (!doc.RootElement.TryGetProperty(side, out var sideObj) || sideObj.ValueKind != JsonValueKind.Object)
                    return result;

                foreach (var prop in sideObj.EnumerateObject())
                {
                    if (!prop.Value.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.String)
                        continue;
                    var value = v.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(value))
                        result[prop.Name] = value;
                }
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private static Dictionary<string, string> ReadNlpFields(string nlpFieldsPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(nlpFieldsPath));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("Field", out var f) || f.ValueKind != JsonValueKind.String)
                        continue;
                    if (!item.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.String)
                        continue;
                    var key = f.GetString() ?? "";
                    var value = v.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                        continue;
                    if (!result.ContainsKey(key))
                        result[key] = value;
                }
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private static string ResolveConfigPath()
        {
            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(cwd, "configs", "config.yaml"),
                Path.Combine(cwd, "configs", "config.yml"),
                Path.Combine(cwd, "OBJ", "configs", "config.yaml"),
                Path.Combine(cwd, "..", "configs", "config.yaml")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }

        private static string FormatMoney(decimal value)
        {
            var formatted = value.ToString("C", new CultureInfo("pt-BR"));
            return formatted.Replace('\u00A0', ' ');
        }

        private static FieldsSegment? RunFieldsSegment(string baseName, string label, string rawText, NlpSegment? nlpSeg, string outDir, string docTypeHint, List<string> errors)
        {
            if (nlpSeg == null)
                return null;
            if (string.IsNullOrWhiteSpace(rawText))
                return null;
            if (string.IsNullOrWhiteSpace(nlpSeg.NlpJsonPath) || !File.Exists(nlpSeg.NlpJsonPath))
            {
                errors.Add($"{label}: nlp_json_not_found");
                return new FieldsSegment { Label = label, Status = "error", Error = "nlp_json_not_found" };
            }

            var res = NlpFieldMapper.Run(new NlpFieldMapRequest
            {
                Label = label,
                BaseName = baseName,
                RawText = rawText,
                NlpJsonPath = nlpSeg.NlpJsonPath,
                OutputDir = outDir,
                DocTypeHint = docTypeHint
            });

            var seg = new FieldsSegment
            {
                Label = label,
                Status = res.Success ? "ok" : "error",
                JsonPath = res.JsonPath,
                Count = res.Fields?.Count ?? 0,
                Error = res.Error
            };

            if (!res.Success && !string.IsNullOrWhiteSpace(res.Error))
                errors.Add($"{label}: {res.Error}");

            return seg;
        }

        private static NlpSegment? RunNlpSegment(string baseName, string label, string text, string outDir, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var result = new NlpSegment { Label = label };
            var nlp = NlpRunner.Run(new NlpRequest
            {
                Text = text,
                Label = label,
                BaseName = baseName,
                OutputDir = outDir
            });

            result.TextPath = nlp.TextPath;
            result.NlpJsonPath = nlp.NlpJsonPath;
            result.TypedPath = nlp.TypedPath;
            result.CboOutPath = nlp.CboOutPath;
            result.Error = nlp.Error;
            result.Status = nlp.Success ? "ok" : "error";

            if (!nlp.Success && !string.IsNullOrWhiteSpace(nlp.Error))
                errors.Add($"{label}: {nlp.Error}");

            return result;
        }

        private static void WriteDebugJson(string dir, string name, object? payload)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static void WriteDebugText(string dir, string name, string? text)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text ?? "");
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static void WriteModuleJson(string debugDir, string module, string io, string name, object? payload)
        {
            try
            {
                var dir = Path.Combine(debugDir, "modules", module, io);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static void WriteModuleText(string debugDir, string module, string io, string name, string? text)
        {
            try
            {
                var dir = Path.Combine(debugDir, "modules", module, io);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text ?? "");
            }
            catch
            {
                // debug output should not break pipeline
            }
        }
    }
}
