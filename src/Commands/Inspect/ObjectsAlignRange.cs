using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Obj.Align;

namespace FilterPDF.Commands
{
    internal static class ObjectsAlignRange
    {
        private const int DefaultBackoff = 2;
        private static readonly string DefaultOutDir = Path.Combine("outputs", "align_ranges");

        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var inputs, out var opFilter, out var contentsPage, out var outDir))
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

            if (opFilter.Count == 0)
            {
                opFilter.Add("Tj");
                opFilter.Add("TJ");
            }

            var result = ObjectsTextOpsDiff.ComputeAlignRanges(aPath, bPath, opFilter, contentsPage, DefaultBackoff);
            if (result == null)
                return;

            if (string.IsNullOrWhiteSpace(outDir))
                outDir = DefaultOutDir;

            Directory.CreateDirectory(outDir);

            var nameA = Path.GetFileName(aPath);
            var nameB = Path.GetFileName(bPath);

            var output = FormatOutput(result, nameA, nameB);
            Console.WriteLine(output);

            var outFile = Path.Combine(outDir,
                $"{Path.GetFileNameWithoutExtension(aPath)}__{Path.GetFileNameWithoutExtension(bPath)}.txt");
            File.WriteAllText(outFile, output);
            Console.WriteLine($"Arquivo salvo: {outFile}");
        }

        private static bool ParseOptions(string[] args, out List<string> inputs, out HashSet<string> opFilter, out int contentsPage, out string outDir)
        {
            inputs = new List<string>();
            opFilter = new HashSet<string>(StringComparer.Ordinal);
            contentsPage = 0;
            outDir = "";

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

                if ((string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--ops", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        opFilter.Add(raw.Trim());
                    continue;
                }

                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page))
                        contentsPage = page;
                    continue;
                }

                if ((string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--out-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                    continue;
                }

                if (string.Equals(arg, "--contents", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                    inputs.Add(arg);
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli inspect objects alignrange --contents --op Tj,TJ <pdfA> <pdfB>");
            Console.WriteLine("  --inputs a.pdf,b.pdf   (opcional)");
            Console.WriteLine("  --page N               (opcional, força pagina do despacho)");
            Console.WriteLine("  --out <dir>            (opcional, default outputs/align_ranges)");
        }

        private static string FormatOutput(ObjectsTextOpsDiff.AlignRangeResult result, string nameA, string nameB)
        {
            var sb = new StringBuilder();
            AppendSection(sb, "front_head", result.FrontA, result.FrontB, nameA, nameB);
            AppendSection(sb, "back_tail", result.BackA, result.BackB, nameA, nameB);
            return sb.ToString();
        }

        private static void AppendSection(StringBuilder sb, string label, ObjectsTextOpsDiff.AlignRangeValue a, ObjectsTextOpsDiff.AlignRangeValue b, string nameA, string nameB)
        {
            sb.AppendLine($"{label}:");
            AppendValue(sb, "a", nameA, a);
            AppendValue(sb, "b", nameB, b);
        }

        private static void AppendValue(StringBuilder sb, string suffix, string name, ObjectsTextOpsDiff.AlignRangeValue value)
        {
            sb.AppendLine($"  pdf_{suffix}: {name}");
            sb.AppendLine($"  op_range_{suffix}: {FormatOpRange(value.StartOp, value.EndOp)}");
            sb.AppendLine($"  value_full_{suffix}: \"{EscapeValue(value.ValueFull)}\"");
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
    }
}
